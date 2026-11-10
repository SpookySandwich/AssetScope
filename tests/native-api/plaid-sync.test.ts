import { beforeAll, describe, expect, it } from 'vitest';
import type { AccountBase, Transaction } from 'plaid';
import { makeTestDb } from './helpers/makeTestDb.js';
import { buildPlaidCommitStatements } from '../../native-api/src/core/plaid-sync.js';

const account = {
  account_id: 'acct-new',
  name: 'New checking',
  official_name: null,
  type: 'depository',
  subtype: 'checking',
  mask: '1234',
  persistent_account_id: null,
  balances: { available: 450, current: 500, iso_currency_code: 'USD', unofficial_currency_code: null, limit: null },
  verification_status: null,
} as unknown as AccountBase;

function transaction(accountId: string | null): Transaction {
  return {
    transaction_id: 'tx-new',
    account_id: accountId,
    date: '2026-08-12',
    name: 'Atomicity regression',
    merchant_name: null,
    amount: 10,
    pending: false,
    personal_finance_category: null,
  } as unknown as Transaction;
}

describe('Plaid item commit atomicity', () => {
  const today = '2026-08-13';

  it('commits account, balance, transaction, cursor and sync time as one state transition', async () => {
    const db = await makeTestDb();
    // Production always migrates before syncing; the commit statements rely on
    // balance_source to decide whether Plaid owns this account's series.
    await db.execute('ALTER TABLE accounts ADD COLUMN balance_source TEXT');
    await db.execute("INSERT INTO plaid_items (item_id, access_token) VALUES ('item-a', 'encrypted')");
    await db.batch(buildPlaidCommitStatements({
      itemId: 'item-a', cursor: 'cursor-2', today, syncedAt: 1234,
      accounts: [account], added: [transaction('acct-new')], modified: [], removed: [],
    }), 'write');

    expect((await db.execute("SELECT id FROM accounts WHERE id = 'acct-new'")).rows).toHaveLength(1);
    expect((await db.execute("SELECT balance FROM balance_history WHERE account_id = 'acct-new'")).rows[0])
      .toMatchObject({ balance: 500 });
    expect((await db.execute("SELECT id FROM transactions WHERE id = 'tx-new'")).rows).toHaveLength(1);
    expect((await db.execute("SELECT cursor FROM sync_state WHERE account_id = 'item-a'")).rows[0])
      .toMatchObject({ cursor: 'cursor-2' });
    expect((await db.execute("SELECT last_synced_at FROM plaid_items WHERE item_id = 'item-a'")).rows[0])
      .toMatchObject({ last_synced_at: 1234 });
  });

  it('prefers Plaid official account names over generic short names', () => {
    const namedAccount = {
      ...account,
      name: 'CREDIT CARD',
      official_name: 'Chase Freedom Unlimited',
    } as unknown as AccountBase;

    const statements = buildPlaidCommitStatements({
      itemId: 'item-a', cursor: 'cursor-2', today, syncedAt: 1234,
      accounts: [namedAccount], added: [], modified: [], removed: [],
    });

    expect(statements[0].args[1]).toBe('Chase Freedom Unlimited');
  });

  it('rolls back account and balance changes when a later statement fails', async () => {
    const db = await makeTestDb();
    // Production always migrates before syncing; the commit statements rely on
    // balance_source to decide whether Plaid owns this account's series.
    await db.execute('ALTER TABLE accounts ADD COLUMN balance_source TEXT');
    await db.execute("INSERT INTO plaid_items (item_id, access_token) VALUES ('item-a', 'encrypted')");

    await expect(db.batch(buildPlaidCommitStatements({
      itemId: 'item-a', cursor: 'cursor-2', today, syncedAt: 1234,
      accounts: [account], added: [transaction(null)], modified: [], removed: [],
    }), 'write')).rejects.toThrow();

    expect((await db.execute("SELECT 1 FROM accounts WHERE id = 'acct-new'")).rows).toHaveLength(0);
    expect((await db.execute("SELECT 1 FROM balance_history WHERE account_id = 'acct-new'")).rows).toHaveLength(0);
    expect((await db.execute("SELECT 1 FROM transactions WHERE id = 'tx-new'")).rows).toHaveLength(0);
    expect((await db.execute("SELECT 1 FROM sync_state WHERE account_id = 'item-a'")).rows).toHaveLength(0);
    expect((await db.execute("SELECT last_synced_at FROM plaid_items WHERE item_id = 'item-a'")).rows[0])
      .toMatchObject({ last_synced_at: null });
  });
});
