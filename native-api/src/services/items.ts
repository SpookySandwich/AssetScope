import { db } from '../core/database.js';
import { backupDb } from '../core/backup.js';
import { notFound } from '../errors.js';
import { log } from '../log.js';

// Plaid item management (SPEC §13.5): list, disconnect-keep-data, and the
// destructive two-phase delete. Individual Plaid accounts are never deleted —
// only excluded or archived.

export type PlaidItemDto = {
  itemId: string;
  institutionName: string | null;
  status: 'active' | 'attention' | 'disconnected';
  accountCount: number;
  lastSuccessAt: string | null;
  lastErrorCode: string | null;
  disconnectedAt: string | null;
};

export async function listItems(): Promise<PlaidItemDto[]> {
  const res = await db.execute(`
    SELECT p.item_id, p.institution_name, p.status, p.last_success_at, p.last_error_code, p.disconnected_at,
           (SELECT COUNT(*) FROM accounts a WHERE a.item_id = p.item_id) AS account_count
    FROM plaid_items p
    ORDER BY p.institution_name, p.item_id
  `);
  return (res.rows as unknown as {
    item_id: string; institution_name: string | null; status: string | null;
    last_success_at: string | null; last_error_code: string | null; disconnected_at: string | null;
    account_count: number;
  }[]).map((r) => ({
    itemId: r.item_id,
    institutionName: r.institution_name,
    status: (r.status === 'attention' || r.status === 'disconnected' ? r.status : 'active'),
    accountCount: Number(r.account_count),
    lastSuccessAt: r.last_success_at,
    lastErrorCode: r.last_error_code,
    disconnectedAt: r.disconnected_at,
  }));
}

async function requireItem(itemId: string): Promise<void> {
  const res = await db.execute({ sql: 'SELECT item_id FROM plaid_items WHERE item_id = ?', args: [itemId] });
  if (res.rows.length === 0) throw notFound('Plaid item');
}

/** Default disconnect: stop syncing, keep all local data (SPEC §13.5.1). */
export async function disconnectItem(itemId: string): Promise<void> {
  await requireItem(itemId);
  await db.execute({
    sql: `UPDATE plaid_items SET status = 'disconnected', disconnected_at = ? WHERE item_id = ?`,
    args: [new Date().toISOString().replace(/\.\d{3}Z$/, 'Z'), itemId],
  });
}

export type DeleteDataPreview = {
  itemId: string;
  institutionName: string | null;
  accountCount: number;
  transactionCount: number;
  snapshotCount: number;
};

export async function previewDeleteItemData(itemId: string): Promise<DeleteDataPreview> {
  await requireItem(itemId);
  const res = await db.execute({
    sql: `SELECT
            (SELECT institution_name FROM plaid_items WHERE item_id = ?) AS institution_name,
            (SELECT COUNT(*) FROM accounts WHERE item_id = ?) AS accounts,
            (SELECT COUNT(*) FROM transactions WHERE account_id IN (SELECT id FROM accounts WHERE item_id = ?)) AS transactions,
            (SELECT COUNT(*) FROM balance_history WHERE account_id IN (SELECT id FROM accounts WHERE item_id = ?)) AS snapshots`,
    args: [itemId, itemId, itemId, itemId],
  });
  const row = res.rows[0] as unknown as {
    institution_name: string | null; accounts: number; transactions: number; snapshots: number;
  };
  return {
    itemId,
    institutionName: row.institution_name,
    accountCount: Number(row.accounts),
    transactionCount: Number(row.transactions),
    snapshotCount: Number(row.snapshots),
  };
}

/**
 * Destructive delete (SPEC §13.5.2): protective backup first, then remove the
 * item, its accounts, and all associated local data. Returns what was removed.
 */
export async function deleteItemData(itemId: string): Promise<DeleteDataPreview> {
  const preview = await previewDeleteItemData(itemId);
  await backupDb();
  await db.batch([
    { sql: `DELETE FROM transaction_tags WHERE transaction_id IN
              (SELECT id FROM transactions WHERE account_id IN (SELECT id FROM accounts WHERE item_id = ?))`, args: [itemId] },
    { sql: `DELETE FROM tag_rule_suppressions WHERE transaction_id IN
              (SELECT id FROM transactions WHERE account_id IN (SELECT id FROM accounts WHERE item_id = ?))`, args: [itemId] },
    { sql: `DELETE FROM transactions WHERE account_id IN (SELECT id FROM accounts WHERE item_id = ?)`, args: [itemId] },
    { sql: `DELETE FROM balance_history WHERE account_id IN (SELECT id FROM accounts WHERE item_id = ?)`, args: [itemId] },
    { sql: `DELETE FROM sync_state WHERE account_id = ?`, args: [itemId] },
    { sql: `DELETE FROM accounts WHERE item_id = ?`, args: [itemId] },
    { sql: `DELETE FROM plaid_items WHERE item_id = ?`, args: [itemId] },
  ], 'write');
  // Audit event without secrets (SPEC §13.5.2): counts + item id only.
  log('warn', `audit: deleted local data for item ${itemId} `
    + `(${preview.accountCount} accounts, ${preview.transactionCount} transactions, ${preview.snapshotCount} snapshots)`);
  return preview;
}
