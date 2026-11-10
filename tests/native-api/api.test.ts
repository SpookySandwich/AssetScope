import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import { vi } from 'vitest';

vi.mock('../../native-api/src/core/database.js', async () => {
  const { makeTestDb } = await import('./helpers/makeTestDb.js');
  const db = await makeTestDb();
  return { db, initDb: async () => {} };
});
vi.mock('../../native-api/src/core/backup.js', () => ({ backupDb: async () => {} }));

import { buildApp } from '../../native-api/src/app.js';
import { db } from '../../native-api/src/core/database.js';
import type { Server } from 'node:http';

const TOKEN = 'test-session-token-test-session-token-1234';
let server: Server;
let base: string;

type Envelope<T> = {
  data: T | null;
  meta: { requestId: string; generatedAt: string; protocolVersion: string };
  error: { code: string; message: string; retryable: boolean } | null;
};

async function call<T>(
  method: string,
  path: string,
  opts: { body?: unknown; token?: string | null } = {},
): Promise<{ status: number; envelope: Envelope<T> }> {
  const headers: Record<string, string> = {};
  const token = opts.token === undefined ? TOKEN : opts.token;
  if (token) headers['Authorization'] = `Bearer ${token}`;
  if (opts.body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(`${base}${path}`, {
    method,
    headers,
    body: opts.body === undefined ? undefined : JSON.stringify(opts.body),
  });
  return { status: res.status, envelope: (await res.json()) as Envelope<T> };
}

beforeAll(async () => {
  const app = await buildApp({
    mode: 'demo',
    dataDir: 'C:\\test\\demo',
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

describe('auth (SPEC §23.1)', () => {
  it('rejects requests without a bearer token', async () => {
    const { status, envelope } = await call('GET', '/v1/health', { token: null });
    expect(status).toBe(401);
    expect(envelope.error?.code).toBe('UNAUTHORIZED');
    expect(envelope.data).toBeNull();
  });

  it('rejects a wrong token', async () => {
    const { status } = await call('GET', '/v1/health', { token: 'wrong' });
    expect(status).toBe(401);
  });
});

describe('envelope (SPEC §10.2)', () => {
  it('wraps success with data/meta/error', async () => {
    const { status, envelope } = await call<{ status: string; mode: string }>('GET', '/v1/health');
    expect(status).toBe(200);
    expect(envelope.error).toBeNull();
    expect(envelope.data?.status).toBe('ok');
    expect(envelope.data?.mode).toBe('demo');
    expect(envelope.meta.protocolVersion).toBe('1.0');
    expect(envelope.meta.requestId).toBeTruthy();
  });

  it('returns typed 404 for unknown endpoints', async () => {
    const { status, envelope } = await call('GET', '/v1/nope');
    expect(status).toBe(404);
    expect(envelope.error?.code).toBe('NOT_FOUND');
  });
});

describe('bootstrap + demo seed', () => {
  it('reports demo mode with seeded accounts', async () => {
    const { envelope } = await call<{
      app: { mode: string; schemaVersion: number };
      onboarding: { accountCount: number; required: boolean };
    }>('GET', '/v1/bootstrap');
    expect(envelope.data?.app.mode).toBe('demo');
    expect(envelope.data?.onboarding.accountCount).toBeGreaterThanOrEqual(7);
    expect(envelope.data?.onboarding.required).toBe(false);
  });
});

describe('dashboard', () => {
  it('serves summary, history, allocation and top accounts from the demo db', async () => {
    const { envelope } = await call<{
      summary: { netWorth: { amount: string }; assets: { amount: string }; liabilities: { amount: string } };
      history: { date: string }[];
      topAccounts: { id: string }[];
      allocation: { slices: { bucket: string }[] };
    }>('GET', '/v1/dashboard?range=1y');
    const d = envelope.data!;
    expect(parseFloat(d.summary.assets.amount)).toBeGreaterThan(0);
    expect(parseFloat(d.summary.liabilities.amount)).toBeGreaterThan(0);
    expect(d.history.length).toBeGreaterThan(10);
    expect(d.topAccounts.length).toBeGreaterThan(3);
    expect(d.allocation.slices.length).toBeGreaterThanOrEqual(3);
  });

  it('rejects invalid ranges', async () => {
    const { status, envelope } = await call('GET', '/v1/dashboard?range=5y');
    expect(status).toBe(400);
    expect(envelope.error?.code).toBe('VALIDATION_ERROR');
  });
});

describe('investments', () => {
  it('serves demo investment accounts, positions, performance and allocation', async () => {
    const { status, envelope } = await call<{
      totalValue: { amount: string };
      accounts: { id: string }[];
      positions: { symbol: string }[];
      performance: { date: string; value: string }[];
      allocation: { bucket: string }[];
    }>('GET', '/v1/investments/overview?range=1y');
    expect(status).toBe(200);
    expect(Number(envelope.data?.totalValue.amount)).toBeGreaterThan(0);
    expect(envelope.data?.accounts.length).toBeGreaterThanOrEqual(2);
    expect(envelope.data?.positions.some((position) => position.symbol === 'AAPL')).toBe(true);
    expect(envelope.data?.performance.length).toBeGreaterThan(10);
    expect(envelope.data?.allocation.length).toBeGreaterThan(1);
  });

  it('rejects invalid investment ranges', async () => {
    const { status, envelope } = await call('GET', '/v1/investments/overview?range=5y');
    expect(status).toBe(400);
    expect(envelope.error?.code).toBe('VALIDATION_ERROR');
  });

  it('serves this-month leftover and dining outcomes from demo living data', async () => {
    const { status, envelope } = await call<{
      leftover: { projected: string };
      split: { fixedProjected: string; flexibleProjected: string };
      diningOutcomes: { mealsPerWeek: number }[];
      transactions: { id: string }[];
    }>('GET', '/v1/living');
    expect(status).toBe(200);
    expect(envelope.data?.leftover.projected).toMatch(/^-?\d+\.\d{2}$/);
    expect(envelope.data?.split.fixedProjected).toMatch(/^\d+\.\d{2}$/);
    expect(envelope.data?.diningOutcomes).toHaveLength(15);
    expect(envelope.data?.transactions.length).toBeGreaterThan(0);
  });

  it('serves card rewards and saves which product a card is', async () => {
    type Rewards = { cards: { accountId: string; productId: string | null }[]; categories: { category: string }[] };
    const before = await call<Rewards>('GET', '/v1/rewards?range=1m');
    expect(before.status).toBe(200);
    expect(before.envelope.data?.cards).toEqual([expect.objectContaining({ accountId: 'demo-credit', productId: null })]);
    expect(before.envelope.data?.categories).toHaveLength(12);

    const put = await call('PUT', '/v1/rewards/cards/demo-credit', { body: { productId: 'citi-double-cash' } });
    expect(put.status).toBe(200);
    const after = await call<Rewards>('GET', '/v1/rewards');
    expect(after.envelope.data?.cards[0].productId).toBe('citi-double-cash');
    await call('PUT', '/v1/rewards/cards/demo-credit', { body: { productId: null } });

    expect((await call('GET', '/v1/rewards?range=5y')).status).toBe(400);
    expect((await call('PUT', '/v1/rewards/cards/demo-credit', { body: { productId: 'nope' } })).status).toBe(400);
    expect((await call('PUT', '/v1/rewards/preferences', { body: { pointValues: { cash: '2.00' } } })).status).toBe(400);
  });

  it('lists merchants for labelling and saves a label', async () => {
    type Merchants = { merchants: { key: string; kind: string; labelledKind: string | null; group: string }[] };
    const before = await call<Merchants>('GET', '/v1/living/merchants');
    expect(before.status).toBe(200);
    const rent = before.envelope.data?.merchants.find((m) => m.key === 'campus heights');
    expect(rent).toMatchObject({ kind: 'rent', labelledKind: null, group: 'recurring' });

    const put = await call('PUT', '/v1/living/merchant-defaults/campus%20heights', { body: { kind: 'other' } });
    expect(put.status).toBe(200);
    const after = await call<Merchants>('GET', '/v1/living/merchants');
    expect(after.envelope.data?.merchants.find((m) => m.key === 'campus heights'))
      .toMatchObject({ kind: 'other', labelledKind: 'other', group: 'labelled' });
    await call('PUT', '/v1/living/merchant-defaults/campus%20heights', { body: { kind: null } });

    expect((await call('PUT', '/v1/living/merchant-defaults/campus%20heights', { body: {} })).status).toBe(400);
  });

  it('rejects an out-of-range dining slider', async () => {
    const { status, envelope } = await call('GET', '/v1/living?diningMealsPerWeek=99');
    expect(status).toBe(400);
    expect(envelope.error?.code).toBe('VALIDATION_ERROR');
  });

  it('lists posted transactions and accepts a party-size annotation', async () => {
    const listed = await call<{ transactions: { id: string; partySize: number | null }[] }>(
      'GET', '/v1/transactions?limit=20',
    );
    expect(listed.status).toBe(200);
    const dining = listed.envelope.data?.transactions.find((t) => t.id.startsWith('demo-liv-'));
    expect(dining).toBeTruthy();

    const patched = await call<{ updated: boolean }>(
      'PATCH', `/v1/transactions/${dining!.id}`,
      { body: { partySize: 3 } },
    );
    expect(patched.status).toBe(200);
    expect(patched.envelope.data?.updated).toBe(true);
  });

  it('summarizes a transaction window and validates its dates', async () => {
    const { status, envelope } = await call<{
      transactionCount: number;
      cashFlow: { totals: { moneyIn: string; moneyOut: string; net: string } };
      spending: { total: string; categories: { category: string; amount: string }[] };
    }>('GET', '/v1/transactions/summary?from=2020-01-01&to=2099-12-31');
    expect(status).toBe(200);
    expect(envelope.data?.transactionCount).toBeGreaterThan(0);
    expect(envelope.data?.cashFlow.totals.moneyOut).toMatch(/^\d+\.\d{2}$/);
    expect(envelope.data?.spending.total).toMatch(/^\d+\.\d{2}$/);

    const missing = await call('GET', '/v1/transactions/summary');
    expect(missing.status).toBe(400);
    expect(missing.envelope.error?.code).toBe('VALIDATION_ERROR');

    const inverted = await call('GET', '/v1/transactions/summary?from=2026-02-01&to=2026-01-01');
    expect(inverted.status).toBe(400);
  });

  it('serves structured, explainable insight facts', async () => {
    const { status, envelope } = await call<{
      period: { range: string };
      items: { id: string; kind: string; confidence: string; priority: number }[];
    }>('GET', '/v1/insights?range=1y&limit=3');
    expect(status).toBe(200);
    expect(envelope.data?.period.range).toBe('1y');
    expect(envelope.data?.items.length).toBeLessThanOrEqual(3);
    for (const insight of envelope.data?.items ?? []) {
      expect(insight.id).toBeTruthy();
      expect(insight.kind).toBeTruthy();
      expect(['externalInflow', 'investmentFunding', 'dataCoverage']).not.toContain(insight.kind);
      expect(['high', 'medium', 'low']).toContain(insight.confidence);
    }
  });

  it('serves only cached read-only orders and honors the account filter', async () => {
    await db.execute({
      sql: `INSERT INTO investment_orders (
              id, account_id, source, brokerage_order_id, status, action,
              symbol, total_quantity, filled_quantity, time_placed)
            VALUES (?, ?, 'snaptrade', ?, 'EXECUTED', 'BUY', 'AAPL', '2', '2', ?)`,
      args: ['api-cached-order', 'demo-brokerage', 'broker-order-api', '2026-08-01T14:00:00Z'],
    });

    const { status, envelope } = await call<{
      orders: { id: string; accountId: string; status: string; symbol: string }[];
    }>('GET', '/v1/investments/orders?accountId=demo-brokerage&limit=10');

    expect(status).toBe(200);
    expect(envelope.data?.orders).toEqual([
      expect.objectContaining({
        id: 'api-cached-order', accountId: 'demo-brokerage', status: 'EXECUTED', symbol: 'AAPL',
      }),
    ]);
  });

  it('serves cached brokerage activities without contacting the provider', async () => {
    const { status, envelope } = await call<{
      activities: { id: string; accountId: string; type: string; symbol: string | null }[];
    }>('GET', '/v1/investments/activities?accountId=demo-brokerage&limit=10');

    expect(status).toBe(200);
    expect(envelope.data?.activities).toEqual(expect.arrayContaining([
      expect.objectContaining({
        id: 'demo-act-1', accountId: 'demo-brokerage', type: 'DIVIDEND', symbol: 'AAPL',
      }),
      expect.objectContaining({
        id: 'demo-act-2', accountId: 'demo-brokerage', type: 'BUY', symbol: 'VTI',
      }),
    ]));
  });
});

describe('manual account lifecycle (SPEC §10.6/§10.7)', () => {
  let accountId: string;

  it('creates a manual asset', async () => {
    const { envelope } = await call<{ accountId: string }>('POST', '/v1/accounts/manual', {
      body: {
        name: 'Home',
        accountClass: 'asset',
        manualCategory: 'property',
        initialValue: { amount: '520000.00', currencyCode: 'USD', effectiveDate: '2026-01-15' },
        includeInNetWorth: true,
      },
    });
    expect(envelope.error).toBeNull();
    accountId = envelope.data!.accountId;
    expect(accountId).toMatch(/^manual-/);
  });

  it('rejects negative manual amounts', async () => {
    const { status } = await call('POST', '/v1/accounts/manual', {
      body: {
        name: 'Bad', accountClass: 'liability', manualCategory: 'loan',
        initialValue: { amount: '-5.00', effectiveDate: '2026-01-15' },
      },
    });
    expect(status).toBe(400);
  });

  it('rejects future effective dates', async () => {
    const { status } = await call('POST', '/v1/accounts/manual', {
      body: {
        name: 'Bad', accountClass: 'asset', manualCategory: 'cash',
        initialValue: { amount: '5.00', effectiveDate: '2099-01-01' },
      },
    });
    expect(status).toBe(400);
  });

  it('updates the value on a chosen effective date', async () => {
    const { envelope } = await call('PATCH', `/v1/accounts/${accountId}/value`, {
      body: { amount: '535000.00', effectiveDate: '2026-02-01' },
    });
    expect(envelope.error).toBeNull();
    const { envelope: detail } = await call<{ recentSnapshots: { date: string; amount: string }[] }>(
      'GET', `/v1/accounts/${accountId}`,
    );
    expect(detail.data?.recentSnapshots[0]).toEqual({ date: '2026-02-01', amount: '535000.00' });
  });

  it('patches nickname/excluded and rejects unknown fields', async () => {
    const ok = await call('PATCH', `/v1/accounts/${accountId}`, { body: { nickname: 'Our House', excluded: true } });
    expect(ok.envelope.error).toBeNull();
    const bad = await call('PATCH', `/v1/accounts/${accountId}`, { body: { balance: '9999.00' } });
    expect(bad.status).toBe(400);
  });

  it('deletes manual accounts and reports removed history count', async () => {
    const { envelope } = await call<{ deleted: boolean; historyCount: number }>(
      'DELETE', `/v1/accounts/${accountId}`,
    );
    expect(envelope.data?.deleted).toBe(true);
    expect(envelope.data?.historyCount).toBe(2);
  });

  it('refuses to delete Plaid accounts', async () => {
    const { status, envelope } = await call('DELETE', '/v1/accounts/demo-checking');
    expect(status).toBe(409);
    expect(envelope.error?.code).toBe('CONFLICT');
  });
});

describe('sync in demo mode', () => {
  it('refuses to sync demo data', async () => {
    const { status, envelope } = await call('POST', '/v1/sync', { body: { trigger: 'manual' } });
    expect(status).toBe(409);
    expect(envelope.error?.code).toBe('NOT_CONFIGURED');
  });
});
