import { describe, it, expect, beforeAll, afterAll, beforeEach, vi } from 'vitest';

vi.mock('../../native-api/src/core/database.js', async () => {
  const { makeTestDb } = await import('./helpers/makeTestDb.js');
  const db = await makeTestDb();
  return { db, initDb: async () => {} };
});
vi.mock('../../native-api/src/core/backup.js', () => ({ backupDb: vi.fn(async () => {}) }));
vi.mock('../../native-api/src/core/crypto.js', () => ({
  encryptToken: (t: string) => `enc:${t}`,
  decryptToken: (t: string) => t.replace(/^enc:/, ''),
}));
vi.mock('../../native-api/src/core/plaid-sync.js', () => ({
  syncTransactions: vi.fn(async () => ({ added: 1, modified: 0, removed: 0, dupes: 0 })),
}));
// Mutable Plaid-side state the hosted-link poll observes. Hoisted so the poll
// interval override is set before link-sessions.ts is imported.
const plaidState = vi.hoisted(() => {
  process.env.WO_LINK_POLL_MS = '40';
  return {
    linkSessions: [] as unknown[],
    userCreateRequests: [] as unknown[],
    linkTokenCreateRequests: [] as unknown[],
    exchangePublicTokens: [] as string[],
    failingPublicTokens: new Set<string>(),
  };
});

vi.mock('../../native-api/src/core/plaid.js', () => ({
  isPlaidConfigured: () => !!(process.env.PLAID_CLIENT_ID && process.env.PLAID_SECRET),
  createLinkToken: vi.fn(async () => 'link-sandbox-token-123'),
  exchangePublicToken: vi.fn(async (publicToken: string) => {
    plaidState.exchangePublicTokens.push(publicToken);
    if (plaidState.failingPublicTokens.has(publicToken)) throw new Error('exchange failed');
    const suffix = publicToken.replace(/^public-/, '');
    return {
      accessToken: publicToken === 'public-sandbox-xyz' ? 'access-sandbox-abc' : `access-${suffix}`,
      itemId: publicToken === 'public-sandbox-xyz' ? 'item-test-1' : `item-${suffix}`,
    };
  }),
  getPlaidClient: vi.fn(() => ({
    userCreate: vi.fn(async (request: unknown) => {
      plaidState.userCreateRequests.push(request);
      return { data: { user_id: 'usr-test-fungible', request_id: 'request-1' } };
    }),
    linkTokenCreate: vi.fn(async (request: unknown) => {
      plaidState.linkTokenCreateRequests.push(request);
      return {
      data: {
        link_token: 'hosted-link-token-123',
        hosted_link_url: 'https://secure.plaid.com/hl/abc123hosted',
      },
      };
    }),
    linkTokenGet: vi.fn(async () => ({ data: { link_sessions: plaidState.linkSessions } })),
  })),
  plaidErrorMessage: (err: unknown) => (err instanceof Error ? err.message : String(err)),
}));

import { db } from '../../native-api/src/core/database.js';
import { createLinkToken } from '../../native-api/src/core/plaid.js';
import { syncTransactions } from '../../native-api/src/core/plaid-sync.js';
import { buildApp } from '../../native-api/src/app.js';
import { waitForCurrentJob } from '../../native-api/src/services/syncjobs.js';
import type { Server } from 'node:http';

const TOKEN = 'plaid-test-session-token-0000000000000000';
let server: Server;
let base: string;

type Envelope<T> = {
  data: T | null;
  error: { code: string; message: string } | null;
};

async function call<T>(
  method: string,
  path: string,
  opts: { body?: unknown } = {},
): Promise<{ status: number; envelope: Envelope<T> }> {
  const headers: Record<string, string> = { Authorization: `Bearer ${TOKEN}` };
  if (opts.body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(`${base}${path}`, {
    method,
    headers,
    body: opts.body === undefined ? undefined : JSON.stringify(opts.body),
  });
  return { status: res.status, envelope: (await res.json()) as Envelope<T> };
}

beforeAll(async () => {
  delete process.env.PLAID_CLIENT_ID;
  delete process.env.PLAID_SECRET;
  delete process.env.PLAID_ENV;
  const app = await buildApp({
    mode: 'real',
    dataDir: 'C:\\test\\real',
    sessionToken: TOKEN,
    onShutdownRequested: () => {},
  });
  server = app.server;
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const addr = server.address();
  base = `http://127.0.0.1:${typeof addr === 'object' && addr ? addr.port : 0}`;
});

afterAll(async () => {
  await new Promise<void>((resolve) => server.close(() => resolve()));
});

beforeEach(() => {
  vi.mocked(createLinkToken as never as () => Promise<string>).mockClear?.();
});

describe('plaid config (SPEC §13.1/§15.4)', () => {
  it('starts unconfigured', async () => {
    const { envelope } = await call<{ configured: boolean; secretConfigured: boolean }>('GET', '/v1/plaid/config-status');
    expect(envelope.data?.configured).toBe(false);
    expect(envelope.data?.secretConfigured).toBe(false);
  });

  it('rejects an invalid environment', async () => {
    const { status } = await call('PUT', '/v1/plaid/config', {
      body: { environment: 'prod', clientId: 'abcdef123456', secret: 'sandbox-secret-1' },
    });
    expect(status).toBe(400);
  });

  it('accepts valid credentials, validates via link token, never echoes the secret', async () => {
    const { status, envelope } = await call<{
      configured: boolean; environment: string; clientIdSuffix: string; secretConfigured: boolean;
    }>('PUT', '/v1/plaid/config', {
      body: { environment: 'sandbox', clientId: 'abcdef123456', secret: 'sandbox-secret-1' },
    });
    expect(status).toBe(200);
    expect(envelope.data?.configured).toBe(true);
    expect(envelope.data?.clientIdSuffix).toBe('3456');
    expect(JSON.stringify(envelope)).not.toContain('sandbox-secret-1');
  });

  it('rolls back when Plaid rejects the credentials', async () => {
    const mock = (createLinkToken as never as { mockRejectedValueOnce: (e: Error) => void });
    mock.mockRejectedValueOnce(new Error('INVALID_API_KEYS: invalid client_id or secret'));
    const { status, envelope } = await call('PUT', '/v1/plaid/config', {
      body: { environment: 'sandbox', clientId: 'zzzzzz999999', secret: 'wrong-secret-99' },
    });
    expect(status).toBe(401);
    expect(envelope.error?.code).toBe('AUTH_REQUIRED');
    // Previous good credentials survive the failed attempt.
    const { envelope: after } = await call<{ clientIdSuffix: string }>('GET', '/v1/plaid/config-status');
    expect(after.data?.clientIdSuffix).toBe('3456');
  });
});

describe('hosted link session lifecycle (SPEC §13.3/§11.3)', () => {
  let sessionId: string;

  it('creates a session pointing directly at Plaid\'s hosted page', async () => {
    plaidState.linkSessions = [];
    plaidState.userCreateRequests = [];
    plaidState.linkTokenCreateRequests = [];
    const { envelope } = await call<{ id: string; url: string; state: string }>(
      'POST', '/v1/plaid/link-sessions', { body: {} });
    expect(envelope.error).toBeNull();
    sessionId = envelope.data!.id;
    expect(envelope.data?.state).toBe('waitingForUser');
    // The browser opens Plaid's own UI — no local wrapper page exists.
    expect(envelope.data?.url).toBe('https://secure.plaid.com/hl/abc123hosted');
    expect(plaidState.userCreateRequests).toEqual([{ client_user_id: 'assetscope-user' }]);
    expect(plaidState.linkTokenCreateRequests.at(-1)).toMatchObject({
      user_id: 'usr-test-fungible',
      enable_multi_item_link: true,
      products: ['transactions'],
      transactions: { days_requested: 730 },
    });
    expect(plaidState.linkTokenCreateRequests.at(-1)).not.toHaveProperty('user');
  });

  it('stays waiting while Plaid has an item result but the overall session is unfinished', async () => {
    plaidState.linkSessions = [{
      link_session_id: 'ls-1',
      results: {
        item_add_results: [{
          public_token: 'public-sandbox-xyz',
          accounts: [],
          institution: { name: 'First Platypus Bank', institution_id: 'ins_109508' },
        }],
      },
    }];
    await new Promise((r) => setTimeout(r, 120));
    const { envelope } = await call<{ state: string }>('GET', `/v1/plaid/link-sessions/${sessionId}`);
    expect(envelope.data?.state).toBe('waitingForUser');
    expect(plaidState.exchangePublicTokens).not.toContain('public-sandbox-xyz');
  });

  it('waits for completion, then exchanges and stores every selected institution', async () => {
    plaidState.linkSessions = [{
      link_session_id: 'ls-1',
      finished_at: new Date().toISOString(),
      results: {
        item_add_results: [{
          public_token: 'public-sandbox-xyz',
          accounts: [],
          institution: { name: 'First Platypus Bank', institution_id: 'ins_109508' },
        }, {
          public_token: 'public-chase',
          accounts: [{ id: 'chase-checking' }],
          institution: { name: 'Chase', institution_id: 'ins_chase' },
        }, {
          public_token: 'public-boa',
          accounts: [{ id: 'boa-checking' }, { id: 'boa-card' }],
          institution: { name: 'Bank of America', institution_id: 'ins_boa' },
        }],
      },
    }];
    // Give the 40 ms poll a few ticks to observe completion.
    let state = '';
    for (let i = 0; i < 50 && state !== 'completed'; i++) {
      await new Promise((r) => setTimeout(r, 40));
      const { envelope } = await call<{ state: string }>('GET', `/v1/plaid/link-sessions/${sessionId}`);
      state = envelope.data?.state ?? '';
    }
    expect(state).toBe('completed');

    const { envelope } = await call<{
      institutionName: string;
      itemId: string;
      connectedItems: { itemId: string; institutionName: string; accountCount: number }[];
    }>(
      'GET', `/v1/plaid/link-sessions/${sessionId}`);
    expect(envelope.data?.institutionName).toBe('First Platypus Bank');
    expect(envelope.data?.itemId).toBe('item-test-1');
    expect(envelope.data?.connectedItems).toEqual([
      { itemId: 'item-test-1', institutionName: 'First Platypus Bank', accountCount: 0, existing: false },
      { itemId: 'item-chase', institutionName: 'Chase', accountCount: 1, existing: false },
      { itemId: 'item-boa', institutionName: 'Bank of America', accountCount: 2, existing: false },
    ]);

    const rows = await db.execute('SELECT item_id, access_token, status FROM plaid_items ORDER BY item_id');
    expect(rows.rows).toHaveLength(3);
    const first = rows.rows.find((row) => (row as unknown as { item_id: string }).item_id === 'item-test-1');
    expect((first as never as { access_token: string }).access_token).toBe('enc:access-sandbox-abc');
    expect((first as never as { status: string }).status).toBe('active');
    await waitForCurrentJob(); // the auto-triggered initial sync settles
  });

  it('keeps successful institutions when another token exchange fails', async () => {
    plaidState.linkSessions = [];
    plaidState.failingPublicTokens.add('public-fail');
    const created = await call<{ id: string }>('POST', '/v1/plaid/link-sessions', { body: {} });
    const id = created.envelope.data!.id;
    plaidState.linkSessions = [{
      link_session_id: 'ls-partial',
      finished_at: new Date().toISOString(),
      results: { item_add_results: [
        { public_token: 'public-partial-good', accounts: [], institution: { name: 'Good Bank' } },
        { public_token: 'public-fail', accounts: [], institution: { name: 'Unavailable Bank' } },
      ] },
    }];

    let result: Envelope<{ state: string; failedItemCount: number; connectedItems: unknown[] }> | undefined;
    for (let i = 0; i < 50 && result?.data?.state !== 'completed'; i++) {
      await new Promise((r) => setTimeout(r, 40));
      result = (await call<{ state: string; failedItemCount: number; connectedItems: unknown[] }>(
        'GET', `/v1/plaid/link-sessions/${id}`)).envelope;
    }
    expect(result?.data?.state).toBe('completed');
    expect(result?.data?.failedItemCount).toBe(1);
    expect(result?.data?.connectedItems).toHaveLength(1);
    const stored = await db.execute("SELECT 1 FROM plaid_items WHERE item_id = 'item-partial-good'");
    expect(stored.rows).toHaveLength(1);
    plaidState.failingPublicTokens.delete('public-fail');
    await waitForCurrentJob();
  });

  it('reuses the persisted Plaid user and upserts an already-known item without duplicating it', async () => {
    plaidState.linkSessions = [];
    const before = await db.execute("SELECT COUNT(*) AS count FROM plaid_items WHERE item_id = 'item-chase'");
    const created = await call<{ id: string }>('POST', '/v1/plaid/link-sessions', { body: {} });
    const id = created.envelope.data!.id;
    expect(plaidState.userCreateRequests).toHaveLength(1);

    plaidState.linkSessions = [{
      link_session_id: 'ls-duplicate',
      finished_at: new Date().toISOString(),
      results: { item_add_results: [
        { public_token: 'public-chase', accounts: [{ id: 'checking' }], institution: { name: 'Chase' } },
      ] },
    }];
    let result: Envelope<{ state: string; connectedItems: { existing: boolean }[] }> | undefined;
    for (let i = 0; i < 50 && result?.data?.state !== 'completed'; i++) {
      await new Promise((r) => setTimeout(r, 40));
      result = (await call<{ state: string; connectedItems: { existing: boolean }[] }>(
        'GET', `/v1/plaid/link-sessions/${id}`)).envelope;
    }

    expect(result?.data?.connectedItems[0]?.existing).toBe(true);
    const after = await db.execute("SELECT COUNT(*) AS count FROM plaid_items WHERE item_id = 'item-chase'");
    expect((after.rows[0] as unknown as { count: number }).count)
      .toBe((before.rows[0] as unknown as { count: number }).count);
    await waitForCurrentJob();
  });

  it('marks a session cancelled when the user exits Plaid without connecting', async () => {
    plaidState.linkSessions = [];
    const created = await call<{ id: string }>('POST', '/v1/plaid/link-sessions', { body: {} });
    const id = created.envelope.data!.id;
    plaidState.linkSessions = [{
      link_session_id: 'ls-2',
      finished_at: new Date().toISOString(),
      exit: { metadata: {} },
    }];
    let state = '';
    for (let i = 0; i < 50 && state !== 'cancelled'; i++) {
      await new Promise((r) => setTimeout(r, 40));
      const { envelope } = await call<{ state: string }>('GET', `/v1/plaid/link-sessions/${id}`);
      state = envelope.data?.state ?? '';
    }
    expect(state).toBe('cancelled');
  });
});

describe('item management (SPEC §13.5)', () => {
  it('lists the connected item', async () => {
    const { envelope } = await call<{ items: { itemId: string; status: string }[] }>('GET', '/v1/plaid/items');
    const item = envelope.data?.items.find((i) => i.itemId === 'item-test-1');
    expect(item?.status).toBe('active');
  });

  it('disconnect keeps data and the sync loop skips the item', async () => {
    await call('POST', '/v1/plaid/items/item-test-1/disconnect');
    const { envelope } = await call<{ items: { itemId: string; status: string }[] }>('GET', '/v1/plaid/items');
    expect(envelope.data?.items.find((item) => item.itemId === 'item-test-1')?.status).toBe('disconnected');

    // The link-completion auto-sync above ran while the item was still active;
    // only calls after this point count.
    vi.mocked(syncTransactions).mockClear();
    const sync = await call<{ id: string }>('POST', '/v1/sync', { body: { trigger: 'manual', force: true } });
    expect(sync.envelope.error).toBeNull();
    await waitForCurrentJob();
    const status = await call<{ job: { items: { itemId: string; status: string }[] } }>('GET', '/v1/sync/status');
    const entry = status.envelope.data?.job.items.find((i) => i.itemId === 'item-test-1');
    expect(entry?.status).toBe('disconnected');
    expect(vi.mocked(syncTransactions as never as () => void)).not.toHaveBeenCalledWith(
      'access-sandbox-abc', 'item-test-1');
  });

  it('delete is two-phase: preview first, destructive only with confirm=true', async () => {
    const preview = await call<{ deleted: boolean; preview: { accountCount: number } }>(
      'DELETE', '/v1/plaid/items/item-test-1/data');
    expect(preview.envelope.data?.deleted).toBe(false);

    const stillThere = await call<{ items: { itemId: string }[] }>('GET', '/v1/plaid/items');
    expect(stillThere.envelope.data?.items.some((item) => item.itemId === 'item-test-1')).toBe(true);

    const deleted = await call<{ deleted: boolean }>('DELETE', '/v1/plaid/items/item-test-1/data?confirm=true');
    expect(deleted.envelope.data?.deleted).toBe(true);

    const gone = await call<{ items: { itemId: string }[] }>('GET', '/v1/plaid/items');
    expect(gone.envelope.data?.items.some((item) => item.itemId === 'item-test-1')).toBe(false);
  });
});
