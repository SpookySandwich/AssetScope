import type { AccountBase, Transaction } from 'plaid';
import { db } from './database.js';
import { getPlaidClient } from './plaid.js';
import { log } from '../log.js';

export type SyncItemResult = {
  itemId: string;
  added: number;
  modified: number;
  removed: number;
  dupes: number;
  skipped: boolean;
  error?: string;
};

type SqlStatement = { sql: string; args: (string | number | null)[] };

/**
 * Build the one authoritative local state transition for a Plaid item.
 *
 * Keeping this pure makes the atomicity contract explicit and testable: the
 * caller may fetch any number of remote pages, but SQLite sees exactly one
 * transaction containing account metadata, balance snapshots, transaction
 * mutations, cursor advancement, and the successful-sync timestamp.
 */
export function buildPlaidCommitStatements(input: {
  itemId: string;
  cursor?: string;
  today: string;
  syncedAt: number;
  accounts: AccountBase[];
  added: Transaction[];
  modified: Transaction[];
  removed: string[];
}): SqlStatement[] {
  const statements: SqlStatement[] = [];

  for (const account of input.accounts) {
    // Plaid's short name is often only "CREDIT CARD". Prefer the institution's
    // official product name when it exists, while retaining the short name as
    // the documented fallback. A user nickname still overrides this at read time.
    const accountName = account.official_name?.trim() || account.name;
    statements.push({
      sql: `INSERT INTO accounts (id, name, type, subtype, mask, item_id)
            VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name, type=excluded.type,
              subtype=excluded.subtype, mask=excluded.mask, item_id=excluded.item_id`,
      args: [account.account_id, accountName, account.type, account.subtype ?? null,
        account.mask ?? null, input.itemId],
    });
    if (account.balances.current !== null) statements.push({
      // Only the owning provider may write this account's series; see the
      // balance_source backfill in services/schema.ts.
      sql: `INSERT INTO balance_history (account_id, balance, date)
            SELECT ?, ?, ? WHERE EXISTS (
              SELECT 1 FROM accounts
               WHERE id = ? AND COALESCE(balance_source, 'plaid') = 'plaid')
            ON CONFLICT(account_id, date) DO UPDATE SET balance=excluded.balance`,
      args: [account.account_id, account.balances.current, input.today, account.account_id],
    });
  }

  for (const transaction of [...input.added, ...input.modified]) {
    const primary = transaction.personal_finance_category?.primary ?? null;
    const detailed = transaction.personal_finance_category?.detailed ?? primary;
    statements.push({
      sql: `INSERT INTO transactions
              (id, account_id, date, name, merchant_name, amount, category,
               raw_category, pending, display_name)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(id) DO UPDATE SET date=excluded.date, name=excluded.name,
              merchant_name=excluded.merchant_name, amount=excluded.amount,
              category=COALESCE(transactions.manual_category, excluded.category),
              raw_category=excluded.raw_category, pending=excluded.pending,
              display_name=excluded.display_name`,
      args: [transaction.transaction_id, transaction.account_id, transaction.date,
        transaction.name, transaction.merchant_name ?? null, transaction.amount,
        primary, detailed, transaction.pending ? 1 : 0, null],
    });
  }

  if (input.removed.length) {
    const placeholders = input.removed.map(() => '?').join(',');
    statements.push(
      { sql: `DELETE FROM transaction_tags WHERE transaction_id IN (${placeholders})`, args: input.removed },
      { sql: `DELETE FROM tag_rule_suppressions WHERE transaction_id IN (${placeholders})`, args: input.removed },
      { sql: `DELETE FROM transactions WHERE id IN (${placeholders})`, args: input.removed },
    );
  }

  statements.push(
    { sql: `INSERT INTO sync_state (account_id, cursor) VALUES (?, ?)
            ON CONFLICT(account_id) DO UPDATE SET cursor=excluded.cursor`,
      args: [input.itemId, input.cursor ?? null] },
    { sql: 'UPDATE plaid_items SET last_synced_at = ? WHERE item_id = ?',
      args: [input.syncedAt, input.itemId] },
  );
  return statements;
}

/** Fetch a complete Plaid delta, then commit accounts, balances, transaction
 * mutations and cursor in one local transaction. A crash or remote failure
 * before the batch leaves the previous complete snapshot untouched. */
export async function syncTransactions(accessToken: string, itemId: string) {
  const cursorResult = await db.execute({
    sql: 'SELECT cursor FROM sync_state WHERE account_id = ?', args: [itemId],
  });
  let cursor = cursorResult.rows.length ? String(cursorResult.rows[0].cursor) : undefined;
  let added: Transaction[] = [];
  let modified: Transaction[] = [];
  let removed: string[] = [];
  let hasMore = true;
  while (hasMore) {
    const page = (await getPlaidClient().transactionsSync({ access_token: accessToken, cursor })).data;
    added = added.concat(page.added);
    modified = modified.concat(page.modified);
    removed = removed.concat(page.removed.map(item => item.transaction_id));
    cursor = page.next_cursor;
    hasMore = page.has_more;
  }
  const accounts = (await getPlaidClient().accountsGet({ access_token: accessToken })).data.accounts;
  log('info', `Plaid item ${itemId}: ${accounts.length} accounts`);
  await db.batch(buildPlaidCommitStatements({
    itemId,
    cursor,
    today: new Date().toISOString().slice(0, 10),
    syncedAt: Date.now(),
    accounts,
    added,
    modified,
    removed,
  }), 'write');
  return { added: added.length, modified: modified.length, removed: removed.length, dupes: 0 };
}
