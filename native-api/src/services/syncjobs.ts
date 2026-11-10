import { randomUUID } from 'node:crypto';
import { db } from '../core/database.js';
import { syncTransactions, type SyncItemResult } from '../core/plaid-sync.js';
import { isPlaidConfigured, plaidErrorMessage } from '../core/plaid.js';
import { decryptToken } from '../core/crypto.js';
import { refreshRotatingCalendars } from './rewards.js';
import { ApiError } from '../errors.js';
import { PROTOCOL_VERSION } from '../envelope.js';

// Persistent sync runs (SPEC §8.3/§8.4, §11.2, §12). Iterates Plaid items
// directly via the product-owned atomic Plaid synchronizer:
// disconnected items must be skipped (SPEC §13.5.1) and core has no notion of
// item status. Debounce and per-item error isolation mirror syncAll exactly.
// One job per data directory at a time; a second request attaches to the
// running job; every run and per-item outcome is recorded in sync_runs /
// sync_item_runs for diagnostics.

export type SyncTrigger = 'initial' | 'launch' | 'manual' | 'scheduled' | 'reconnect' | 'retry';

export type SyncJobDto = {
  id: string;
  trigger: SyncTrigger;
  state: 'running' | 'succeeded' | 'partial' | 'failed';
  startedAt: string;
  completedAt: string | null;
  progress: { totalItems: number; completedItems: number; successfulItems: number; failedItems: number };
  items: { itemId: string; institutionName: string | null; status: string; errorCode: string | null }[];
};

const DEBOUNCE_MS = 15 * 60_000;

const nowIso = (): string => new Date().toISOString().replace(/\.\d{3}Z$/, 'Z');

let currentJob: SyncJobDto | null = null;
let currentJobPromise: Promise<void> | null = null;

export function getCurrentJob(): SyncJobDto | null {
  return currentJob;
}

/**
 * A process cannot commit its final diagnostic row after it has been killed.
 * On the next startup, close any orphaned `running` rows explicitly. Financial
 * data needs no repair here: each Item's accounts, balances, transactions and
 * cursor are committed by syncTransactions() in one SQLite transaction.
 */
export async function recoverInterruptedSyncRuns(): Promise<number> {
  const result = await db.execute({
    sql: `UPDATE sync_runs
          SET status = 'failed', completed_at = ?, summary_json = ?
          WHERE status = 'running'`,
    args: [
      nowIso(),
      JSON.stringify({ errorCode: 'INTERRUPTED', message: 'The previous sync process stopped before completion.' }),
    ],
  });
  return result.rowsAffected;
}

// Plaid error strings from plaidErrorMessage() are human text; map the known
// re-auth case to its public code, keep everything else generic + redacted.
function classifyError(message: string): string {
  if (/ITEM_LOGIN_REQUIRED|login required|reauthenticate/i.test(message)) return 'ITEM_LOGIN_REQUIRED';
  if (/rate limit|RATE_LIMIT/i.test(message)) return 'RATE_LIMITED';
  if (/ENOTFOUND|ECONNREFUSED|ETIMEDOUT|network/i.test(message)) return 'NETWORK_UNAVAILABLE';
  return 'SYNC_ERROR';
}

type ItemRow = {
  item_id: string;
  access_token: string;
  last_synced_at: number | null;
  institution_name: string | null;
  status: string | null;
};

async function runItems(force: boolean): Promise<(SyncItemResult & { disconnected?: boolean })[]> {
  const itemsRes = await db.execute(
    'SELECT item_id, access_token, last_synced_at, institution_name, status FROM plaid_items',
  );
  const items = itemsRes.rows as unknown as ItemRow[];

  const results: (SyncItemResult & { disconnected?: boolean })[] = [];
  for (const item of items) {
    if (item.status === 'disconnected') {
      results.push({ itemId: item.item_id, added: 0, modified: 0, removed: 0, dupes: 0, skipped: true, disconnected: true });
      continue;
    }
    if (!force && item.last_synced_at && Date.now() - Number(item.last_synced_at) < DEBOUNCE_MS) {
      results.push({ itemId: item.item_id, added: 0, modified: 0, removed: 0, dupes: 0, skipped: true });
      continue;
    }
    try {
      const result = await syncTransactions(decryptToken(item.access_token), item.item_id);
      results.push({ itemId: item.item_id, ...result, skipped: false });
    } catch (err) {
      results.push({
        itemId: item.item_id, added: 0, modified: 0, removed: 0, dupes: 0,
        skipped: false, error: plaidErrorMessage(err),
      });
    }
  }
  return results;
}

export async function startSync(trigger: SyncTrigger, force: boolean, appVersion: string): Promise<SyncJobDto> {
  if (currentJob && currentJob.state === 'running') return currentJob; // attach (SPEC §11.2)
  if (!isPlaidConfigured()) {
    throw new ApiError('NOT_CONFIGURED', 'Plaid credentials are not configured.');
  }

  const itemsRes = await db.execute('SELECT item_id, institution_name FROM plaid_items');
  const itemMeta = new Map(
    (itemsRes.rows as unknown as { item_id: string; institution_name: string | null }[])
      .map((r) => [r.item_id, r.institution_name]),
  );

  const job: SyncJobDto = {
    id: `sync_${randomUUID()}`,
    trigger,
    state: 'running',
    startedAt: nowIso(),
    completedAt: null,
    progress: { totalItems: itemMeta.size, completedItems: 0, successfulItems: 0, failedItems: 0 },
    items: [],
  };
  currentJob = job;

  await db.execute({
    sql: `INSERT INTO sync_runs (id, trigger, force, status, started_at, item_count, app_version, protocol_version)
          VALUES (?, ?, ?, 'running', ?, ?, ?, ?)`,
    args: [job.id, trigger, force ? 1 : 0, job.startedAt, itemMeta.size, appVersion, PROTOCOL_VERSION],
  });

  currentJobPromise = (async () => {
    // Card issuers' rotating 5% categories are checked alongside; that check
    // never decides whether the sync itself succeeded.
    const calendars = refreshRotatingCalendars().catch(() => {});
    let results: (SyncItemResult & { disconnected?: boolean })[] = [];
    try {
      results = await runItems(force);
    } catch (err) {
      // Per-item failures are isolated inside runItems; reaching here means an
      // infrastructural failure (e.g. database unavailable).
      job.state = 'failed';
      job.completedAt = nowIso();
      await db.execute({
        sql: `UPDATE sync_runs SET status = 'failed', completed_at = ?, summary_json = ? WHERE id = ?`,
        args: [job.completedAt, JSON.stringify({ error: String(err).slice(0, 300) }), job.id],
      }).catch(() => {});
      return;
    }

    const succeeded = results.filter((r) => !r.error);
    const failed = results.filter((r) => r.error);
    const finalProgress = {
      totalItems: results.length,
      completedItems: results.length,
      successfulItems: succeeded.length,
      failedItems: failed.length,
    };
    const finalItems = results.map((r) => ({
      itemId: r.itemId,
      institutionName: itemMeta.get(r.itemId) ?? null,
      status: r.error ? 'failed' : r.disconnected ? 'disconnected' : r.skipped ? 'skipped' : 'succeeded',
      errorCode: r.error ? classifyError(r.error) : null,
    }));
    const finalState = failed.length === 0 ? 'succeeded' : succeeded.length > 0 ? 'partial' : 'failed';
    const completedAt = nowIso();

    const statements = [
      {
        sql: `UPDATE sync_runs SET status = ?, completed_at = ?, success_count = ?, failure_count = ?, summary_json = ? WHERE id = ?`,
        args: [
          finalState,
          completedAt, succeeded.length, failed.length,
          JSON.stringify({
            added: results.reduce((s, r) => s + r.added, 0),
            modified: results.reduce((s, r) => s + r.modified, 0),
            removed: results.reduce((s, r) => s + r.removed, 0),
          }),
          job.id,
        ] as (string | number | null)[],
      },
      ...results.map((r) => ({
        sql: `INSERT INTO sync_item_runs (id, run_id, item_id, institution_name, status, started_at, completed_at,
                added_count, modified_count, removed_count, error_code, error_message_redacted)
              VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
        args: [
          `sir_${randomUUID()}`, job.id, r.itemId, itemMeta.get(r.itemId) ?? null,
          r.error ? 'failed' : r.skipped ? 'skipped' : 'succeeded',
          job.startedAt, completedAt,
          r.added, r.modified, r.removed,
          r.error ? classifyError(r.error) : null,
          // Redaction: keep the public error class only, never raw Plaid payloads.
          r.error ? String(r.error).slice(0, 200) : null,
        ] as (string | number | null)[],
      })),
      // Track per-item health for the attention state (SPEC §8.2). Skipped
      // (including disconnected) items keep their current status untouched.
      ...results.filter((r) => !r.skipped).map((r) => ({
        sql: r.error
          ? `UPDATE plaid_items SET status = 'attention', last_error_code = ?, last_error_at = ? WHERE item_id = ?`
          : `UPDATE plaid_items SET status = 'active', last_error_code = NULL, last_error_at = NULL, last_success_at = ? WHERE item_id = ?`,
        args: r.error
          ? [classifyError(r.error), completedAt, r.itemId]
          : [completedAt, r.itemId] as (string | number | null)[],
      })),
    ];
    // Finish the calendar check first, so pages reloading on completion see it.
    await calendars;
    try {
      // Publish the in-memory terminal state only after the persistent status,
      // item diagnostics and Item health have committed together.
      await db.batch(statements, 'write');
      job.progress = finalProgress;
      job.items = finalItems;
      job.state = finalState;
      job.completedAt = completedAt;
    } catch {
      // Do not expose a success that was never durably recorded. The next
      // process startup will also recover the still-running persistent row.
      job.progress = finalProgress;
      job.items = finalItems;
      job.state = 'failed';
      job.completedAt = completedAt;
    }
  })();

  return job;
}

export async function waitForCurrentJob(): Promise<void> {
  await currentJobPromise;
}

export type SyncRunRow = {
  id: string; trigger: string; status: string;
  startedAt: string | null; completedAt: string | null;
  itemCount: number; successCount: number; failureCount: number;
};

export async function listRuns(limit = 50): Promise<SyncRunRow[]> {
  const res = await db.execute({
    sql: `SELECT id, trigger, status, started_at, completed_at, item_count, success_count, failure_count
          FROM sync_runs ORDER BY started_at DESC LIMIT ?`,
    args: [Math.min(Math.max(limit, 1), 200)],
  });
  return (res.rows as unknown as {
    id: string; trigger: string; status: string; started_at: string | null; completed_at: string | null;
    item_count: number; success_count: number; failure_count: number;
  }[]).map((r) => ({
    id: r.id, trigger: r.trigger, status: r.status,
    startedAt: r.started_at, completedAt: r.completed_at,
    itemCount: Number(r.item_count), successCount: Number(r.success_count), failureCount: Number(r.failure_count),
  }));
}
