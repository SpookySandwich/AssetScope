import { randomUUID } from 'node:crypto';
import { Products, CountryCode } from 'plaid';
import { db } from '../core/database.js';
import { getPlaidClient, exchangePublicToken, isPlaidConfigured, plaidErrorMessage } from '../core/plaid.js';
import { encryptToken, decryptToken } from '../core/crypto.js';
import { getSetting, setSetting } from '../core/settings.js';
import { ApiError, notFound } from '../errors.js';
import { SIDECAR_VERSION } from '../envelope.js';
import { startSync } from './syncjobs.js';
import { log } from '../log.js';

// Plaid Hosted Link flow (SPEC §13.3/§13.4). The browser opens Plaid's own
// hosted page directly: Plaid's consent screen ("assetscope wants to
// access…"), the institution picker with logos, and the bank's own
// authorization — no local wrapper page, no extra clicks. The sidecar polls
// /link/token/get until the session finishes, then exchanges the public token
// server-side. No local HTTP callback server exists at all.

export type LinkSessionState = 'waitingForUser' | 'completed' | 'cancelled' | 'expired' | 'failed';

export type ConnectedLinkItemDto = {
  itemId: string;
  institutionName: string | null;
  accountCount: number;
  existing: boolean;
};

export type LinkSessionDto = {
  id: string;
  mode: 'connect' | 'reconnect';
  state: LinkSessionState;
  url: string;
  createdAt: string;
  expiresAt: string;
  institutionName: string | null;
  itemId: string | null;
  connectedItems: ConnectedLinkItemDto[];
  failedItemCount: number;
  warning: string | null;
  error: string | null;
};

type Session = LinkSessionDto & {
  linkToken: string;
  requestedHistoryDays: number | null;
  timer: NodeJS.Timeout | null;
  polling: boolean;
};

const SESSION_TTL_MS = 15 * 60_000;
// Test override keeps the vitest suite fast; production polls every 3 s.
const POLL_INTERVAL_MS = Number(process.env.WO_LINK_POLL_MS ?? 3_000);

const sessions = new Map<string, Session>();
let activeSessionId: string | null = null;

type ItemAddResult = {
  public_token?: string | null;
  accounts?: unknown[] | null;
  institution?: { name?: string | null } | null;
};

const nowIso = (): string => new Date().toISOString().replace(/\.\d{3}Z$/, 'Z');

const plaidUserSettingKey = (): string => {
  const environment = process.env.PLAID_ENV ?? 'sandbox';
  const clientId = process.env.PLAID_CLIENT_ID ?? '';
  return `plaid_user_id:${environment}:${clientId}`;
};

/**
 * Multi-Item Link requires a Plaid User. Persisting it per Plaid environment
 * and client ID makes subsequent sessions belong to the same Plaid user while
 * avoiding an invalid cross-environment ID when credentials are changed.
 */
async function getOrCreatePlaidUserId(): Promise<string> {
  const key = plaidUserSettingKey();
  const existing = await getSetting(key);
  if (existing) return existing;

  const response = await getPlaidClient().userCreate(
    { client_user_id: 'assetscope-user' },
    true,
  );
  const userId = response.data.user_id;
  if (!userId) throw new Error('Plaid did not return a user ID.');
  await setSetting(key, userId);
  return userId;
}

export async function createLinkSession(opts: {
  reconnectItemId?: string;
  daysRequested?: number;
} = {}): Promise<LinkSessionDto> {
  if (!isPlaidConfigured()) {
    throw new ApiError('NOT_CONFIGURED', 'Configure Plaid credentials before connecting a bank.');
  }

  // One active session at a time; a new request supersedes the old one.
  if (activeSessionId !== null) cancelLinkSession(activeSessionId);

  const mode: 'connect' | 'reconnect' = opts.reconnectItemId ? 'reconnect' : 'connect';
  const requestedHistoryDays = mode === 'connect' ? (opts.daysRequested ?? 730) : null;

  let accessToken: string | undefined;
  if (mode === 'reconnect') {
    const res = await db.execute({
      sql: 'SELECT access_token FROM plaid_items WHERE item_id = ?',
      args: [opts.reconnectItemId!],
    });
    if (res.rows.length === 0) throw notFound('Plaid item');
    accessToken = decryptToken((res.rows[0] as unknown as { access_token: string }).access_token);
  }

  let linkToken: string;
  let hostedUrl: string;
  try {
    const plaidUserId = mode === 'connect' ? await getOrCreatePlaidUserId() : undefined;
    const response = await getPlaidClient().linkTokenCreate({
      ...(mode === 'connect'
        ? { user_id: plaidUserId, enable_multi_item_link: true }
        : { user: { client_user_id: 'assetscope-user' } }),
      client_name: 'assetscope',
      language: 'en',
      country_codes: [CountryCode.Us],
      // Update mode reuses the existing item; products only apply to new links.
      ...(mode === 'connect' ? { products: [Products.Transactions] } : { access_token: accessToken }),
      ...(requestedHistoryDays ? { transactions: { days_requested: requestedHistoryDays } } : {}),
      hosted_link: { url_lifetime_seconds: SESSION_TTL_MS / 1000 },
    });
    linkToken = response.data.link_token;
    hostedUrl = response.data.hosted_link_url ?? '';
    if (!hostedUrl) throw new Error('Plaid did not return a hosted link URL.');
  } catch (err) {
    throw new ApiError('AUTH_REQUIRED', `Could not create a link session: ${plaidErrorMessage(err)}`);
  }

  const session: Session = {
    id: `link_${randomUUID()}`,
    mode,
    state: 'waitingForUser',
    url: hostedUrl,
    createdAt: nowIso(),
    expiresAt: new Date(Date.now() + SESSION_TTL_MS).toISOString().replace(/\.\d{3}Z$/, 'Z'),
    institutionName: null,
    itemId: opts.reconnectItemId ?? null,
    connectedItems: [],
    failedItemCount: 0,
    warning: null,
    error: null,
    linkToken,
    requestedHistoryDays,
    timer: null,
    polling: false,
  };

  session.timer = setInterval(() => void pollSession(session), POLL_INTERVAL_MS);
  session.timer.unref();

  sessions.set(session.id, session);
  activeSessionId = session.id;
  pruneFinishedSessions();
  log('info', `link session ${session.id} created (${mode})`);
  return toDto(session);
}

async function pollSession(session: Session): Promise<void> {
  if (session.polling || session.state !== 'waitingForUser') return;
  session.polling = true;
  try {
    if (Date.now() > Date.parse(session.expiresAt)) {
      finish(session, 'expired');
      return;
    }

    const response = await getPlaidClient().linkTokenGet({ link_token: session.linkToken });
    const linkSessions = response.data.link_sessions ?? [];

    // Multi-Item results are not complete until the entire Link session has
    // finished. Processing an early ITEM_ADD_RESULT would prematurely close
    // our poller after the first institution.
    if (session.mode === 'connect') {
      const finishedSessions = linkSessions.filter((ls) => ls.finished_at);
      const addedItems = finishedSessions.flatMap(
        (ls) => (ls.results?.item_add_results ?? []) as ItemAddResult[],
      );
      if (addedItems.length > 0) {
        await completeMultiConnect(session, addedItems);
        return;
      }
    }

    // Update mode has no item_add_results; a finished session without an exit means success.
    if (session.mode === 'reconnect' && linkSessions.some((ls) => ls.finished_at && !ls.exit && !ls.on_exit)) {
      await db.execute({
        sql: `UPDATE plaid_items SET status = 'active', last_error_code = NULL, last_error_at = NULL WHERE item_id = ?`,
        args: [session.itemId],
      });
      finish(session, 'completed');
      startSync('reconnect', true, SIDECAR_VERSION).catch(() => {});
      return;
    }

    // User closed Plaid's page without connecting.
    if (linkSessions.some((ls) => ls.finished_at && (ls.exit || ls.on_exit))) {
      finish(session, 'cancelled');
    }
  } catch (err) {
    // Transient poll errors are tolerated; the next tick retries. Only an
    // invalid/expired token is terminal.
    const message = plaidErrorMessage(err);
    if (/INVALID_LINK_TOKEN|expired/i.test(message)) {
      session.error = message;
      finish(session, 'expired');
    }
  } finally {
    session.polling = false;
  }
}

async function completeMultiConnect(session: Session, addedItems: ItemAddResult[]): Promise<void> {
  const failures: string[] = [];

  for (const added of addedItems) {
    const institutionName = added.institution?.name ?? null;
    if (!added.public_token) {
      failures.push(institutionName ?? 'unknown institution');
      continue;
    }

    try {
      const { accessToken, itemId } = await exchangePublicToken(added.public_token);
      const existing = await db.execute({
        sql: 'SELECT 1 FROM plaid_items WHERE item_id = ?',
        args: [itemId],
      });
      await db.execute({
        sql: `INSERT INTO plaid_items (item_id, access_token, institution_name, days_requested, status, last_error_code, last_error_at)
              VALUES (?, ?, ?, ?, 'active', NULL, NULL)
              ON CONFLICT(item_id) DO UPDATE SET
                access_token = excluded.access_token,
                institution_name = excluded.institution_name,
                days_requested = COALESCE(plaid_items.days_requested, excluded.days_requested),
                status = 'active', last_error_code = NULL, last_error_at = NULL`,
        args: [itemId, encryptToken(accessToken), institutionName, session.requestedHistoryDays],
      });
      session.connectedItems.push({
        itemId,
        institutionName,
        accountCount: added.accounts?.length ?? 0,
        existing: existing.rows.length > 0,
      });
    } catch {
      failures.push(institutionName ?? 'unknown institution');
    }
  }

  session.failedItemCount = failures.length;
  const first = session.connectedItems[0];
  session.itemId = first?.itemId ?? null;
  session.institutionName = first?.institutionName ?? null;

  if (session.connectedItems.length > 0) {
    if (failures.length > 0) {
      session.warning = `${failures.length} institution(s) could not be connected. Try those institutions again.`;
    }
    finish(session, 'completed');
    log('info', `link session ${session.id} completed (${session.connectedItems.length} item(s), ${failures.length} failed)`);
    startSync('initial', true, SIDECAR_VERSION).catch(() => {});
  } else {
    session.error = failures.length > 0
      ? 'None of the selected institutions could be connected. Please try again.'
      : 'Plaid returned no usable institutions.';
    finish(session, 'failed');
    log('error', `link session ${session.id} failed during exchange`);
  }
}

export function getLinkSession(id: string): LinkSessionDto {
  const session = sessions.get(id);
  if (!session) throw notFound('Link session');
  return toDto(session);
}

export function cancelLinkSession(id: string): LinkSessionDto {
  const session = sessions.get(id);
  if (!session) throw notFound('Link session');
  if (session.state === 'waitingForUser') finish(session, 'cancelled');
  return toDto(session);
}

function finish(session: Session, state: LinkSessionState): void {
  session.state = state;
  if (session.timer !== null) {
    clearInterval(session.timer);
    session.timer = null;
  }
  if (activeSessionId === session.id) activeSessionId = null;
}

function pruneFinishedSessions(): void {
  const finished = [...sessions.values()].filter((s) => s.state !== 'waitingForUser');
  for (const s of finished.slice(0, Math.max(0, finished.length - 10))) sessions.delete(s.id);
}

const toDto = (s: Session): LinkSessionDto => ({
  id: s.id,
  mode: s.mode,
  state: s.state,
  url: s.url,
  createdAt: s.createdAt,
  expiresAt: s.expiresAt,
  institutionName: s.institutionName,
  itemId: s.itemId,
  connectedItems: s.connectedItems.map((item) => ({ ...item })),
  failedItemCount: s.failedItemCount,
  warning: s.warning,
  error: s.error,
});
