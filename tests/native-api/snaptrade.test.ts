import { beforeAll, describe, expect, it, vi } from 'vitest';

vi.mock('../../native-api/src/core/database.js', async () => {
  const { makeTestDb } = await import('./helpers/makeTestDb.js');
  const db = await makeTestDb();
  return { db, initDb: async () => {} };
});

import { db } from '../../native-api/src/core/database.js';
import { ApiError } from '../../native-api/src/errors.js';
import { ensureNativeSchema } from '../../native-api/src/services/schema.js';
import {
  matchCanonicalProviderAccount,
  providerBalanceForStorage,
  recoverInterruptedInvestmentSyncRuns,
  syncSnapTrade,
} from '../../native-api/src/services/snaptrade-sync.js';
import type { SnapTradeGateway } from '../../native-api/src/services/snaptrade-client.js';
import {
  buildInvestmentAllocation,
  investmentBucketForInstrument,
  investmentOrders,
  investmentOverview,
} from '../../native-api/src/services/investments.js';

const utcToday = new Date().toISOString().slice(0, 10);

const gateway: SnapTradeGateway = {
  check: async () => {},
  partnerInfo: async () => ({ can_access_holdings: true, can_access_account_history: true }),
  createPortal: async () => ({ redirectURI: 'https://example.invalid/portal', sessionId: 'portal-1' }),
  listConnections: async () => [{
    id: 'connection-1',
    name: 'Primary Robinhood',
    type: 'read',
    disabled: false,
    brokerage: { id: 'broker-1', slug: 'ROBINHOOD', display_name: 'Robinhood' },
  }],
  listAccounts: async () => [{
    id: 'account-1',
    brokerage_authorization: 'connection-1',
    name: 'Individual Brokerage',
    number: '••••4242',
    institution_name: 'Robinhood',
    account_category: 'INVESTMENT',
    raw_type: 'Individual',
    status: 'open',
    created_date: '2024-01-01T00:00:00Z',
    balance: { total: { amount: 1250.50, currency: 'USD' } },
    sync_status: { holdings: { last_successful_sync: '2026-08-12T02:00:00Z' } },
  }],
  listPositions: async () => ({
    asOf: '2026-08-12T02:00:00Z',
    results: [{
      instrument: { id: 'instrument-aapl', kind: 'stock', symbol: 'AAPL', description: 'Apple Inc.' },
      units: '5', price: '200', cost_basis: '150', currency: 'USD', cash_equivalent: false,
    }],
  }),
  listBalances: async () => [{ currency: { code: 'USD' }, cash: 250.50, buying_power: 250.50 }],
  listActivities: async () => [{
    id: 'activity-1', type: 'BUY', trade_date: '2026-08-01T00:00:00Z',
    settlement_date: '2026-08-03T00:00:00Z',
    symbol: { symbol: { symbol: 'AAPL' } }, units: 5, price: 150,
    amount: -750, currency: { code: 'USD' }, description: 'Bought Apple',
  }],
  listOrders: async () => [{
    brokerage_order_id: 'broker-order-1', status: 'EXECUTED', action: 'BUY',
    universal_symbol: { symbol: 'AAPL' }, total_quantity: '5', open_quantity: '0',
    filled_quantity: '5', execution_price: '150', order_type: 'Limit',
    time_in_force: 'Day', time_placed: '2026-08-01T14:00:00Z',
    time_executed: '2026-08-01T14:01:00Z',
  }],
  listBalanceHistory: async () => ({
    currency: 'USD',
    history: [
      { date: '2026-06-01', total_value: '1000.25' },
      { date: '2026-07-01', total_value: '1125.75' },
      { date: utcToday, total_value: '1.00' },
    ],
  }),
};

beforeAll(async () => {
  await ensureNativeSchema();
  await db.batch([
    { sql: `INSERT INTO plaid_items (item_id, access_token, institution_name)
            VALUES ('plaid-robinhood', 'encrypted-test-token', 'Robinhood')`, args: [] },
    { sql: `INSERT INTO accounts
              (id, name, type, subtype, institution_name, mask, item_id, source,
               account_class, currency_code, excluded, created_at, updated_at)
            VALUES ('existing-robinhood', 'Robinhood Brokerage', 'investment', 'brokerage',
                    'Robinhood', '4242', 'plaid-robinhood', 'plaid', 'asset', 'USD', 0,
                    '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z')`, args: [] },
  ], 'write');
});

describe('SnapTrade atomic synchronization', () => {
  it('merges uniquely matching Plaid deposit and credit accounts without trusting provider masks', () => {
    const accounts = [
      { id: 'plaid-checking', source: 'plaid', institution_name: 'Robinhood', mask: '1001', type: 'depository', subtype: 'checking', latest_balance: 0 },
      { id: 'plaid-savings', source: 'plaid', institution_name: 'Robinhood', mask: '1002', type: 'depository', subtype: 'savings', latest_balance: 950.25 },
      { id: 'plaid-credit', source: 'plaid', institution_name: 'Robinhood', mask: '1003', type: 'credit', subtype: 'credit card', latest_balance: 132 },
    ];
    expect(matchCanonicalProviderAccount({
      institutionName: 'Robinhood', mask: 'a1b2', type: 'depository', subtype: 'SAVINGS',
      accountClass: 'asset', total: 950.25,
    }, accounts)?.id).toBe('plaid-savings');
    expect(matchCanonicalProviderAccount({
      institutionName: 'Robinhood', mask: 'c3d4', type: 'depository', subtype: 'CHECKING',
      accountClass: 'asset', total: 0,
    }, accounts)?.id).toBe('plaid-checking');
    expect(matchCanonicalProviderAccount({
      institutionName: 'Robinhood', mask: 'e5f6', type: 'credit', subtype: 'CREDITCARD',
      accountClass: 'liability', total: -132,
    }, accounts)?.id).toBe('plaid-credit');
    expect(providerBalanceForStorage('liability', -132)).toBe(132);
  });

  it('refuses an ambiguous semantic provider-account match', () => {
    const duplicateCards = [
      { id: 'card-a', source: 'plaid', institution_name: 'Bank', mask: '1111', type: 'credit', subtype: 'credit card', latest_balance: 100 },
      { id: 'card-b', source: 'plaid', institution_name: 'Bank', mask: '2222', type: 'credit', subtype: 'credit card', latest_balance: 100 },
    ];
    expect(matchCanonicalProviderAccount({
      institutionName: 'Bank', mask: '9999', type: 'credit', subtype: 'CREDITCARD',
      accountClass: 'liability', total: -100,
    }, duplicateCards)).toBeUndefined();
  });

  it('keeps provider instrument kinds in stable visual buckets', () => {
    expect(investmentBucketForInstrument('stock')).toBe('Stocks');
    expect(investmentBucketForInstrument('ADR')).toBe('Stocks');
    expect(investmentBucketForInstrument('etf')).toBe('ETFs');
    expect(investmentBucketForInstrument('cef')).toBe('ETFs');
    expect(investmentBucketForInstrument('mutualfund')).toBe('Mutual Funds');
    expect(investmentBucketForInstrument('crypto')).toBe('Crypto');
    expect(investmentBucketForInstrument('option')).toBe('Other');
  });

  it('commits the complete account snapshot and exposes an investment overview', async () => {
    const run = await syncSnapTrade('initial', gateway);
    expect(run.status).toBe('succeeded');
    expect(run.accountCount).toBe(1);
    expect(run.positionCount).toBe(1);
    expect(run.activityCount).toBe(1);
    expect(run.orderCount).toBe(1);

    const duplicate = await db.execute("SELECT COUNT(*) AS c FROM accounts WHERE id = 'snaptrade-account-1'");
    expect(Number((duplicate.rows[0] as unknown as { c: number }).c)).toBe(0);
    const link = await db.execute("SELECT account_id, provider_connection_id FROM account_provider_links WHERE provider = 'snaptrade' AND provider_account_id = 'account-1'");
    expect(link.rows[0]).toMatchObject({ account_id: 'existing-robinhood', provider_connection_id: 'connection-1' });
    const position = await db.execute('SELECT account_id, source, symbol, market_value, gain_loss FROM investment_positions');
    expect(position.rows[0]).toMatchObject({ account_id: 'existing-robinhood', source: 'snaptrade', symbol: 'AAPL', market_value: '1000', gain_loss: '250' });
    const activity = await db.execute('SELECT activity_type, symbol FROM investment_activities');
    expect(activity.rows[0]).toMatchObject({ activity_type: 'BUY', symbol: 'AAPL' });
    const order = await db.execute(
      'SELECT status, action, symbol, total_quantity, filled_quantity FROM investment_orders',
    );
    expect(order.rows[0]).toMatchObject({
      status: 'EXECUTED', action: 'BUY', symbol: 'AAPL', total_quantity: '5', filled_quantity: '5',
    });
    const cachedOrders = await investmentOrders(100, 'existing-robinhood');
    expect(cachedOrders[0]).toMatchObject({
      accountId: 'existing-robinhood', status: 'EXECUTED', action: 'BUY', symbol: 'AAPL',
      totalQuantity: '5', filledQuantity: '5',
    });
    // The optional estimated series is never imported: it interleaves two
    // valuations of the same account on alternating dates and is rewritten on
    // every sync, which fabricated a permanent net-worth sawtooth.
    const history = await db.execute({
      sql: `SELECT date, balance FROM balance_history
            WHERE account_id = 'existing-robinhood' AND date IN (?, ?) ORDER BY date`,
      args: ['2026-06-01', '2026-07-01'],
    });
    expect(history.rows).toEqual([]);
    const today = await db.execute({
      sql: "SELECT balance FROM balance_history WHERE account_id = 'existing-robinhood' AND date = ?",
      args: [utcToday],
    });
    expect(today.rows[0]).toMatchObject({ balance: 1250.5 });

    const overview = await investmentOverview('1y');
    expect(overview.totalValue.amount).toBe('1250.50');
    expect(overview.positions[0].symbol).toBe('AAPL');
    expect(overview.allocation.some((slice) => slice.bucket === 'Stocks')).toBe(true);
    expect(overview.allocation.some((slice) => slice.bucket === 'Cash')).toBe(true);
    expect(overview.allocation.reduce((sum, slice) => sum + Number(slice.amount), 0)).toBeCloseTo(1250.50, 2);
  });

  it('does not count broker cash twice when it is also a cash-equivalent position', () => {
    const slices = buildInvestmentAllocation([
      { bucket: 'Stocks', value: 1000, cashEquivalent: false },
      { bucket: 'Other', value: 250.50, cashEquivalent: true },
    ], 250.50, 1250.50);
    expect(slices).toEqual([
      { bucket: 'Stocks', value: 1000 },
      { bucket: 'Cash', value: 250.5 },
    ]);
    expect(slices.reduce((sum, slice) => sum + slice.value, 0)).toBe(1250.5);
  });

  it('reconciles missing or stale allocation components to the portfolio total', () => {
    expect(buildInvestmentAllocation([
      { bucket: 'ETFs', value: 900, cashEquivalent: false },
    ], 0, 1000)).toEqual([
      { bucket: 'ETFs', value: 900 },
      { bucket: 'Unclassified', value: 100 },
    ]);

    expect(buildInvestmentAllocation([], 0, 1000)).toEqual([
      { bucket: 'Unclassified', value: 1000 },
    ]);

    const stale = buildInvestmentAllocation([
      { bucket: 'Stocks', value: 1200, cashEquivalent: false },
      { bucket: 'ETFs', value: 300, cashEquivalent: false },
    ], 0, 1000);
    expect(stale[0]).toEqual({ bucket: 'Stocks', value: 800 });
    expect(stale[1]).toEqual({ bucket: 'ETFs', value: 200 });
  });

  it('still records the daily snapshot when optional history is unsupported', async () => {
    const unsupported: SnapTradeGateway = {
      ...gateway,
      listBalanceHistory: async () => null,
    };
    const run = await syncSnapTrade('scheduled', unsupported);
    expect(run.status).toBe('succeeded');
    const notImported = await db.execute(
      "SELECT balance FROM balance_history WHERE account_id = 'existing-robinhood' AND date = '2026-07-01'",
    );
    expect(notImported.rows).toHaveLength(0);
    const latest = await db.execute(
      "SELECT balance FROM balance_history WHERE account_id = 'existing-robinhood' ORDER BY date DESC LIMIT 1",
    );
    expect(latest.rows).toHaveLength(1);
  });

  it('never lets estimated history points reach the stored series', async () => {
    const malformed: SnapTradeGateway = {
      ...gateway,
      listBalanceHistory: async () => ({
        currency: 'USD',
        history: [
          { date: 'not-a-date', total_value: '999999' },
          { date: '2026-02-30', total_value: '999999' },
          { date: '2026-07-15', total_value: 'not-a-number' },
          { date: '2026-07-20', total_value: '1200.50' },
        ],
      }),
    };
    const run = await syncSnapTrade('retry', malformed);
    expect(run.status).toBe('succeeded');
    // Well-formed estimated points are ignored along with malformed ones.
    const valid = await db.execute(
      "SELECT balance FROM balance_history WHERE account_id = 'existing-robinhood' AND date = '2026-07-20'",
    );
    expect(valid.rows).toHaveLength(0);
    const corrupt = await db.execute(
      "SELECT COUNT(*) AS c FROM balance_history WHERE account_id = 'existing-robinhood' AND balance = 999999",
    );
    expect(Number((corrupt.rows[0] as unknown as { c: number }).c)).toBe(0);
  });

  it('preserves the previous complete snapshot when the next fetch fails', async () => {
    const failing: SnapTradeGateway = {
      ...gateway,
      listAccounts: async () => { throw new ApiError('NETWORK_UNAVAILABLE', 'offline', { retryable: true }); },
    };
    await expect(syncSnapTrade('retry', failing)).rejects.toMatchObject({ code: 'NETWORK_UNAVAILABLE' });

    const positions = await db.execute('SELECT symbol, market_value FROM investment_positions');
    expect(positions.rows).toHaveLength(1);
    expect(positions.rows[0]).toMatchObject({ symbol: 'AAPL', market_value: '1000' });
    const orders = await db.execute('SELECT brokerage_order_id, status FROM investment_orders');
    expect(orders.rows[0]).toMatchObject({ brokerage_order_id: 'broker-order-1', status: 'EXECUTED' });
    const account = await db.execute("SELECT archived_at FROM accounts WHERE id = 'existing-robinhood'");
    expect((account.rows[0] as unknown as { archived_at: string | null }).archived_at).toBeNull();
    const latest = await db.execute('SELECT status, error_code FROM investment_sync_runs ORDER BY started_at DESC LIMIT 1');
    expect(latest.rows[0]).toMatchObject({ status: 'failed', error_code: 'NETWORK_UNAVAILABLE' });
  });

  it('marks orphaned running syncs interrupted without touching cached data', async () => {
    await db.execute(`INSERT INTO investment_sync_runs (id, trigger, status, started_at)
                      VALUES ('orphaned-run', 'scheduled', 'running', '2026-08-12T01:00:00Z')`);
    expect(await recoverInterruptedInvestmentSyncRuns()).toBe(1);
    const row = await db.execute("SELECT status, error_code FROM investment_sync_runs WHERE id = 'orphaned-run'");
    expect(row.rows[0]).toMatchObject({ status: 'failed', error_code: 'INTERRUPTED' });
    const positions = await db.execute('SELECT COUNT(*) AS c FROM investment_positions');
    expect(Number((positions.rows[0] as unknown as { c: number }).c)).toBe(1);
  });
});
