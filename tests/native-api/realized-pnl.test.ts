import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../../native-api/src/core/database.js', async () => {
  const { makeTestDb } = await import('./helpers/makeTestDb.js');
  const db = await makeTestDb();
  return { db, initDb: async () => {} };
});

import { db } from '../../native-api/src/core/database.js';
import { ensureNativeSchema } from '../../native-api/src/services/schema.js';
import { investmentRealizedPnl } from '../../native-api/src/services/investments.js';

let activityId = 0;

async function seedAccount(id: string, name: string) {
  await db.execute({
    sql: `INSERT OR REPLACE INTO accounts (id, name, type, institution_name)
          VALUES (?, ?, 'investment', 'Test Brokerage')`,
    args: [id, name],
  });
}

/** Units are signed as SnapTrade reports them: buys positive, sells negative. */
async function trade(
  type: 'BUY' | 'SELL',
  symbol: string | null,
  units: number,
  price: number,
  tradeDate: string,
  options: { accountId?: string; fee?: number; amount?: number } = {},
) {
  const gross = options.amount ?? units * price;
  await db.execute({
    sql: `INSERT INTO investment_activities
            (id, account_id, activity_type, trade_date, symbol, units, price, amount, fee, updated_at)
          VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    args: [
      `activity-${++activityId}`,
      options.accountId ?? 'acct-1',
      type,
      tradeDate,
      symbol,
      String(type === 'BUY' ? units : -units),
      String(price),
      String(type === 'BUY' ? -gross : gross),
      String(options.fee ?? 0),
      tradeDate,
    ],
  });
}

beforeAll(async () => {
  await ensureNativeSchema();
});

beforeEach(async () => {
  await db.execute('DELETE FROM investment_activities');
  await seedAccount('acct-1', 'Individual Brokerage');
  await seedAccount('acct-2', 'Roth IRA');
});

describe('realized profit and loss (FIFO)', () => {
  it('matches the oldest lot first, not the cheapest or the newest', async () => {
    await trade('BUY', 'AAA', 10, 100, '2025-01-01T00:00:00Z');
    await trade('BUY', 'AAA', 10, 200, '2025-02-01T00:00:00Z');
    await trade('SELL', 'AAA', 10, 150, '2025-03-01T00:00:00Z');

    const result = await investmentRealizedPnl();
    // FIFO consumes the $100 lot: 1500 - 1000 = +500. LIFO would give -500.
    expect(result.summary.total).toBe('500.00');
    expect(result.method).toBe('fifo');

    const sell = result.trades.find((t) => t.action === 'sell')!;
    expect(sell.costBasis).toBe('1000.00');
    expect(sell.realized).toBe('500.00');
    expect(sell.basisComplete).toBe(true);
  });

  it('consumes a lot partially without losing or inventing cents', async () => {
    await trade('BUY', 'BBB', 3, 10.01, '2025-01-01T00:00:00Z'); // 30.03
    await trade('SELL', 'BBB', 1, 20, '2025-02-01T00:00:00Z');
    await trade('SELL', 'BBB', 2, 20, '2025-03-01T00:00:00Z');

    const result = await investmentRealizedPnl();
    const sells = result.trades.filter((t) => t.action === 'sell');
    const basis = sells.reduce((sum, t) => sum + Number(t.costBasis), 0);
    // The two partial draws must add back to exactly what the lot cost.
    expect(basis.toFixed(2)).toBe('30.03');
    expect(result.summary.total).toBe(String((60 - 30.03).toFixed(2)));
  });

  it('reports a sell with no matching buy instead of inventing a cost basis', async () => {
    await trade('SELL', 'CCC', 5, 40, '2025-05-01T00:00:00Z');

    const result = await investmentRealizedPnl();
    const sell = result.trades.find((t) => t.action === 'sell')!;
    expect(sell.basisComplete).toBe(false);
    expect(sell.costBasis).toBe('0.00');
    // Nothing is covered, so nothing is claimed as profit.
    expect(sell.realized).toBe('0.00');
    expect(result.summary.tradesMissingBasis).toBe(1);
    expect(result.summary.total).toBe('0.00');
  });

  it('scores only the covered fraction of a partially covered sell', async () => {
    await trade('BUY', 'DDD', 5, 10, '2025-01-01T00:00:00Z');
    await trade('SELL', 'DDD', 10, 20, '2025-02-01T00:00:00Z');

    const result = await investmentRealizedPnl();
    const sell = result.trades.find((t) => t.action === 'sell')!;
    expect(sell.basisComplete).toBe(false);
    // Half the units are covered: proceeds 100 against a 50 basis.
    expect(sell.costBasis).toBe('50.00');
    expect(sell.realized).toBe('50.00');
  });

  it('keeps inventories separate per account so lots never cross', async () => {
    await trade('BUY', 'EEE', 10, 100, '2025-01-01T00:00:00Z', { accountId: 'acct-1' });
    await trade('SELL', 'EEE', 10, 150, '2025-02-01T00:00:00Z', { accountId: 'acct-2' });

    const result = await investmentRealizedPnl();
    const sell = result.trades.find((t) => t.action === 'sell')!;
    // The buy lives in the other account, so this sale has no basis here.
    expect(sell.basisComplete).toBe(false);
    expect(result.summary.tradesMissingBasis).toBe(1);
  });

  it('treats a buy fee as cost and a sell fee as a reduction of proceeds', async () => {
    await trade('BUY', 'FFF', 10, 100, '2025-01-01T00:00:00Z', { fee: 5 });
    await trade('SELL', 'FFF', 10, 100, '2025-02-01T00:00:00Z', { fee: 5 });

    const result = await investmentRealizedPnl();
    // Flat price, so the round trip loses exactly both fees.
    expect(result.summary.total).toBe('-10.00');
  });

  it('ignores card and cash activity that carries no symbol', async () => {
    await db.execute({
      sql: `INSERT INTO investment_activities
              (id, account_id, activity_type, trade_date, symbol, units, price, amount, fee, updated_at)
            VALUES ('posdebit-1', 'acct-1', 'POSDEBIT', '2025-04-01T00:00:00Z', NULL, '0', '0', '20', '0', '2025-04-01T00:00:00Z')`,
      args: [],
    });
    await trade('BUY', 'GGG', 1, 10, '2025-01-01T00:00:00Z');
    await trade('SELL', 'GGG', 1, 15, '2025-02-01T00:00:00Z');

    const result = await investmentRealizedPnl();
    expect(result.summary.total).toBe('5.00');
    expect(result.summary.buyCount).toBe(1);
    expect(result.summary.sellCount).toBe(1);
    expect(result.trades).toHaveLength(2);
  });

  it('returns trades newest first and honours the limit', async () => {
    await trade('BUY', 'HHH', 1, 10, '2025-01-01T00:00:00Z');
    await trade('BUY', 'HHH', 1, 10, '2025-02-01T00:00:00Z');
    await trade('SELL', 'HHH', 1, 15, '2025-03-01T00:00:00Z');

    const all = await investmentRealizedPnl();
    expect(all.trades[0].tradeDate).toBe('2025-03-01T00:00:00Z');

    const limited = await investmentRealizedPnl(1);
    expect(limited.trades).toHaveLength(1);
    // The limit trims the display list; the summary still covers every trade.
    expect(limited.summary.total).toBe('5.00');
    expect(limited.summary.buyCount).toBe(2);
  });

  it('accumulates year-to-date and month-to-date from the same ledger', async () => {
    const now = new Date();
    const thisMonth = `${now.getUTCFullYear()}-${String(now.getUTCMonth() + 1).padStart(2, '0')}-01T00:00:00Z`;
    await trade('BUY', 'III', 1, 10, '2020-01-01T00:00:00Z');
    await trade('SELL', 'III', 1, 30, '2020-02-01T00:00:00Z'); // old, total only
    await trade('BUY', 'III', 1, 10, '2020-01-02T00:00:00Z');
    await trade('SELL', 'III', 1, 20, thisMonth);

    const result = await investmentRealizedPnl();
    expect(result.summary.total).toBe('30.00');
    expect(result.summary.yearToDate).toBe('10.00');
    expect(result.summary.monthToDate).toBe('10.00');
  });
});
