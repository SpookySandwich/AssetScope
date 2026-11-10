import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../../native-api/src/core/database.js', async () => {
  const { makeTestDb } = await import('./helpers/makeTestDb.js');
  const db = await makeTestDb();
  return { db, initDb: async () => {} };
});

import { db } from '../../native-api/src/core/database.js';
import { ensureNativeSchema } from '../../native-api/src/services/schema.js';

/**
 * Plaid and SnapTrade both report the same Robinhood accounts. When both wrote
 * balance_history for one account the stored series alternated between two
 * providers' views of the same money, sawtoothing net worth. Exactly one
 * provider owns an account's series.
 */
const PLAID_WRITE = `INSERT INTO balance_history (account_id, balance, date)
  SELECT ?, ?, ? WHERE EXISTS (
    SELECT 1 FROM accounts WHERE id = ? AND COALESCE(balance_source, 'plaid') = 'plaid')
  ON CONFLICT(account_id, date) DO UPDATE SET balance = excluded.balance`;

const SNAPTRADE_WRITE = `INSERT INTO balance_history (account_id, balance, date)
  SELECT ?, ?, ? WHERE EXISTS (
    SELECT 1 FROM accounts WHERE id = ? AND balance_source = 'snaptrade')
  ON CONFLICT(account_id, date) DO UPDATE SET balance = excluded.balance`;

const write = (sql: string, id: string, balance: number) =>
  db.execute({ sql, args: [id, balance, '2026-08-16', id] });

const balanceOf = async (id: string) => {
  const r = await db.execute({
    sql: 'SELECT balance FROM balance_history WHERE account_id = ? AND date = ?',
    args: [id, '2026-08-16'],
  });
  return r.rows.length ? Number((r.rows[0] as unknown as { balance: number }).balance) : null;
};

beforeAll(async () => { await ensureNativeSchema(); });

beforeEach(async () => {
  await db.execute('DELETE FROM balance_history');
  await db.execute('DELETE FROM investment_balances');
  await db.execute('DELETE FROM accounts');
});

async function account(id: string, type: string, source: string) {
  await db.execute({
    sql: `INSERT INTO accounts (id, name, type, institution_name, source)
          VALUES (?, ?, ?, 'Robinhood', ?)`,
    args: [id, id, type, source],
  });
}

/** The backfill runs once per schema version; rewind so it sees these rows. */
async function runBackfill() {
  await db.execute("DELETE FROM settings WHERE key = 'native_api_schema_version'");
  await ensureNativeSchema();
}

describe('one provider owns each balance series', () => {
  it('gives a deposit account to Plaid and refuses SnapTrade writes', async () => {
    await account('savings', 'depository', 'plaid');
    await runBackfill();

    await write(PLAID_WRITE, 'savings', 950.25);
    // SnapTrade's total for this account bundles money that is not savings cash.
    await write(SNAPTRADE_WRITE, 'savings', 55250.25);

    expect(await balanceOf('savings')).toBe(950.25);
  });

  it('gives a synced brokerage account to SnapTrade and refuses Plaid writes', async () => {
    await account('brokerage', 'investment', 'plaid');
    await db.execute({
      sql: `INSERT INTO investment_balances (account_id, currency_code, cash, as_of_at)
            VALUES (?, 'USD', '17724.75', '2026-08-16T00:00:00Z')`,
      args: ['brokerage'],
    });
    await runBackfill();

    // SnapTrade knows positions plus the cash sweep; Plaid's figure omits it.
    await write(SNAPTRADE_WRITE, 'brokerage', 45427.99);
    await write(PLAID_WRITE, 'brokerage', 38107.92);

    expect(await balanceOf('brokerage')).toBe(45427.99);
  });

  it('never lets a second provider overwrite a date already written', async () => {
    await account('savings', 'depository', 'plaid');
    await runBackfill();

    for (const value of [950.25, 55250.25, 950.25, 55250.25]) {
      await write(PLAID_WRITE, 'savings', 950.25);
      await write(SNAPTRADE_WRITE, 'savings', value);
    }
    // The alternation that produced the sawtooth cannot happen.
    expect(await balanceOf('savings')).toBe(950.25);
  });

  it('assigns an authority to every account', async () => {
    await account('savings', 'depository', 'plaid');
    await account('manual-1', 'other', 'manual');
    await account('st-1', 'investment', 'snaptrade');
    await runBackfill();

    const r = await db.execute('SELECT id, balance_source FROM accounts ORDER BY id');
    const rows = r.rows as unknown as { id: string; balance_source: string }[];
    expect(rows.every((row) => row.balance_source !== null)).toBe(true);
    expect(rows.find((row) => row.id === 'manual-1')!.balance_source).toBe('manual');
    expect(rows.find((row) => row.id === 'st-1')!.balance_source).toBe('snaptrade');
    expect(rows.find((row) => row.id === 'savings')!.balance_source).toBe('plaid');
  });
});
