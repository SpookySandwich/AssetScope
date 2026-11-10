import fs from 'node:fs';
import { createClient, type Client } from '@libsql/client';
import { DATA_DIR, DATABASE_PATH } from './paths.js';
import { encryptToken } from './crypto.js';

fs.mkdirSync(DATA_DIR, { recursive: true });
export const db: Client = createClient({ url: `file:${DATABASE_PATH}` });

const BASE_SCHEMA = [
  `CREATE TABLE IF NOT EXISTS accounts (
    id TEXT PRIMARY KEY, name TEXT NOT NULL, type TEXT NOT NULL, subtype TEXT,
    institution_name TEXT, mask TEXT, nickname TEXT, owner TEXT, apr REAL,
    excluded INTEGER NOT NULL DEFAULT 0, item_id TEXT,
    -- Which provider owns this account's balance_history series. Two providers
    -- writing one series made net worth alternate between their views of the
    -- same money; see services/schema.ts.
    balance_source TEXT)`,
  `CREATE TABLE IF NOT EXISTS transactions (
    id TEXT PRIMARY KEY, account_id TEXT NOT NULL, date TEXT NOT NULL,
    name TEXT NOT NULL, merchant_name TEXT, amount REAL NOT NULL, category TEXT,
    raw_category TEXT, pending INTEGER NOT NULL DEFAULT 0, manual_category TEXT,
    display_name TEXT, ignored INTEGER NOT NULL DEFAULT 0,
    party_size INTEGER, trip_origin TEXT, trip_destination TEXT, trip_kind TEXT,
    living_kind TEXT)`,
  `CREATE INDEX IF NOT EXISTS idx_transactions_date ON transactions(date)`,
  `CREATE INDEX IF NOT EXISTS idx_transactions_account ON transactions(account_id)`,
  `CREATE TABLE IF NOT EXISTS sync_state (account_id TEXT PRIMARY KEY, cursor TEXT)`,
  `CREATE TABLE IF NOT EXISTS plaid_items (
    item_id TEXT PRIMARY KEY, access_token TEXT NOT NULL, institution_name TEXT,
    last_synced_at INTEGER, days_requested INTEGER)`,
  `CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)`,
  `CREATE TABLE IF NOT EXISTS balance_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT, account_id TEXT NOT NULL,
    balance REAL NOT NULL, date TEXT NOT NULL)`,
  `CREATE UNIQUE INDEX IF NOT EXISTS idx_balance_history_acct_date
    ON balance_history(account_id, date)`,
  // Legacy support tables remain because connected databases may contain
  // transaction metadata from older builds. They are intentionally not part
  // of the current product surface, but preserving them makes migration safe.
  `CREATE TABLE IF NOT EXISTS transaction_tags (
    transaction_id TEXT NOT NULL, tag_id INTEGER NOT NULL,
    PRIMARY KEY(transaction_id, tag_id))`,
  `CREATE TABLE IF NOT EXISTS tag_rule_suppressions (
    transaction_id TEXT NOT NULL, tag_id INTEGER NOT NULL,
    PRIMARY KEY(transaction_id, tag_id))`,
  `CREATE TABLE IF NOT EXISTS hidden_categories (category TEXT PRIMARY KEY)`,
  `CREATE TABLE IF NOT EXISTS categories (name TEXT PRIMARY KEY, flexibility TEXT)`,
  `CREATE TABLE IF NOT EXISTS tags (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE)`,
  `CREATE TABLE IF NOT EXISTS category_rules (
    id INTEGER PRIMARY KEY AUTOINCREMENT, priority INTEGER NOT NULL DEFAULT 0,
    match_type TEXT NOT NULL, pattern TEXT NOT NULL, category TEXT NOT NULL,
    min_amount REAL, max_amount REAL, account_id TEXT)`,
  `CREATE TABLE IF NOT EXISTS name_rules (
    id INTEGER PRIMARY KEY AUTOINCREMENT, match_type TEXT NOT NULL,
    pattern TEXT NOT NULL, replacement TEXT NOT NULL, min_amount REAL,
    max_amount REAL, account_id TEXT)`,
  `CREATE TABLE IF NOT EXISTS tag_rules (
    id INTEGER PRIMARY KEY AUTOINCREMENT, priority INTEGER NOT NULL DEFAULT 0,
    match_type TEXT NOT NULL, pattern TEXT NOT NULL DEFAULT '', tag_id INTEGER NOT NULL,
    account_id TEXT, min_amount REAL, max_amount REAL)`,
  `CREATE TABLE IF NOT EXISTS living_obligations (
    id TEXT PRIMARY KEY, name TEXT NOT NULL, amount REAL NOT NULL,
    interval TEXT NOT NULL, kind TEXT NOT NULL, created_at TEXT NOT NULL)`,
  `CREATE TABLE IF NOT EXISTS living_merchant_defaults (
    merchant_key TEXT PRIMARY KEY, party_size INTEGER, trip_origin TEXT,
    trip_destination TEXT, trip_kind TEXT, living_kind TEXT, updated_at TEXT NOT NULL)`,
  `CREATE TABLE IF NOT EXISTS living_route_alternatives (
    origin TEXT NOT NULL, destination TEXT NOT NULL, label TEXT NOT NULL,
    monthly_amount REAL NOT NULL, PRIMARY KEY(origin, destination))`,
];

export async function initDb(): Promise<void> {
  await db.batch(BASE_SCHEMA, 'write');
  const items = await db.execute('SELECT item_id, access_token FROM plaid_items');
  const plaintext = (items.rows as unknown as { item_id: string; access_token: string }[])
    .filter(item => !item.access_token.includes(':'));
  if (plaintext.length) {
    await db.batch(plaintext.map(item => ({
      sql: 'UPDATE plaid_items SET access_token = ? WHERE item_id = ?',
      args: [encryptToken(item.access_token), item.item_id],
    })), 'write');
  }
}

