import { db } from '../core/database.js';

// Product-owned schema extensions. Idempotent ALTERs retain compatibility
// with databases created by earlier builds; no upstream runtime is required.

export const NATIVE_SCHEMA_VERSION = 12;
const VERSION_KEY = 'native_api_schema_version';

async function getSchemaVersion(): Promise<number> {
  try {
    const res = await db.execute({ sql: 'SELECT value FROM settings WHERE key = ?', args: [VERSION_KEY] });
    return res.rows.length ? Number((res.rows[0] as unknown as { value: string }).value) : 0;
  } catch {
    return 0;
  }
}

// Read-only peek used by the CLI to decide whether an existing database lags
// behind this build and needs a migration pass before the command runs.
export async function nativeSchemaVersion(): Promise<number> {
  return getSchemaVersion();
}

export async function ensureNativeSchema(): Promise<{ migrated: boolean; schemaVersion: number }> {
  const current = await getSchemaVersion();
  if (current >= NATIVE_SCHEMA_VERSION) return { migrated: false, schemaVersion: current };

  const alters = [
    "ALTER TABLE accounts ADD COLUMN source TEXT",
    "ALTER TABLE accounts ADD COLUMN account_class TEXT",
    "ALTER TABLE accounts ADD COLUMN manual_category TEXT",
    "ALTER TABLE accounts ADD COLUMN currency_code TEXT",
    "ALTER TABLE accounts ADD COLUMN archived_at TEXT",
    "ALTER TABLE accounts ADD COLUMN created_at TEXT",
    "ALTER TABLE accounts ADD COLUMN updated_at TEXT",
    "ALTER TABLE accounts ADD COLUMN notes TEXT",
    "ALTER TABLE accounts ADD COLUMN balance_source TEXT",
    "ALTER TABLE accounts ADD COLUMN provider_account_id TEXT",
    "ALTER TABLE accounts ADD COLUMN provider_connection_id TEXT",
    "ALTER TABLE plaid_items ADD COLUMN status TEXT",
    "ALTER TABLE plaid_items ADD COLUMN disconnected_at TEXT",
    "ALTER TABLE plaid_items ADD COLUMN last_error_code TEXT",
    "ALTER TABLE plaid_items ADD COLUMN last_error_at TEXT",
    "ALTER TABLE plaid_items ADD COLUMN last_success_at TEXT",
    "ALTER TABLE transactions ADD COLUMN party_size INTEGER",
    "ALTER TABLE transactions ADD COLUMN trip_origin TEXT",
    "ALTER TABLE transactions ADD COLUMN trip_destination TEXT",
    "ALTER TABLE transactions ADD COLUMN trip_kind TEXT",
    "ALTER TABLE transactions ADD COLUMN living_kind TEXT",
  ];
  for (const sql of alters) {
    try {
      await db.execute(sql);
    } catch (e) {
      const msg = String(e);
      if (!msg.includes('duplicate column name') && !msg.includes('already exists')) throw e;
    }
  }

  await db.batch([
    `CREATE TABLE IF NOT EXISTS sync_runs (
      id TEXT PRIMARY KEY,
      trigger TEXT NOT NULL,
      force INTEGER NOT NULL,
      status TEXT NOT NULL,
      started_at TEXT,
      completed_at TEXT,
      item_count INTEGER NOT NULL DEFAULT 0,
      success_count INTEGER NOT NULL DEFAULT 0,
      failure_count INTEGER NOT NULL DEFAULT 0,
      summary_json TEXT,
      app_version TEXT,
      protocol_version TEXT
    )`,
    `CREATE TABLE IF NOT EXISTS sync_item_runs (
      id TEXT PRIMARY KEY,
      run_id TEXT NOT NULL,
      item_id TEXT NOT NULL,
      institution_name TEXT,
      status TEXT NOT NULL,
      started_at TEXT,
      completed_at TEXT,
      added_count INTEGER,
      modified_count INTEGER,
      removed_count INTEGER,
      error_code TEXT,
      error_message_redacted TEXT,
      FOREIGN KEY(run_id) REFERENCES sync_runs(id)
    )`,
    `CREATE INDEX IF NOT EXISTS idx_sync_item_runs_run ON sync_item_runs(run_id)`,
    `CREATE INDEX IF NOT EXISTS idx_accounts_item ON accounts(item_id)`,
    `CREATE INDEX IF NOT EXISTS idx_accounts_excluded ON accounts(excluded, archived_at)`,
    `CREATE UNIQUE INDEX IF NOT EXISTS idx_accounts_provider_account
       ON accounts(source, provider_account_id)
       WHERE provider_account_id IS NOT NULL`,
    `CREATE TABLE IF NOT EXISTS snaptrade_connections (
      connection_id TEXT PRIMARY KEY,
      brokerage_id TEXT,
      brokerage_slug TEXT,
      brokerage_name TEXT NOT NULL,
      brokerage_logo_url TEXT,
      display_name TEXT,
      connection_type TEXT NOT NULL DEFAULT 'read',
      disabled INTEGER NOT NULL DEFAULT 0,
      disabled_at TEXT,
      created_at TEXT,
      remote_updated_at TEXT,
      last_success_at TEXT,
      last_error_code TEXT,
      last_error_at TEXT
    )`,
    `CREATE TABLE IF NOT EXISTS investment_positions (
      id TEXT PRIMARY KEY,
      account_id TEXT NOT NULL,
      source TEXT NOT NULL DEFAULT 'snaptrade',
      provider_position_id TEXT,
      symbol TEXT NOT NULL,
      name TEXT NOT NULL,
      instrument_type TEXT NOT NULL,
      units TEXT,
      price TEXT,
      average_price TEXT,
      market_value TEXT,
      gain_loss TEXT,
      currency_code TEXT NOT NULL DEFAULT 'USD',
      cash_equivalent INTEGER NOT NULL DEFAULT 0,
      as_of_at TEXT NOT NULL,
      FOREIGN KEY(account_id) REFERENCES accounts(id)
    )`,
    `CREATE INDEX IF NOT EXISTS idx_investment_positions_account
       ON investment_positions(account_id)`,
    `CREATE INDEX IF NOT EXISTS idx_investment_positions_symbol
       ON investment_positions(symbol)`,
    `CREATE TABLE IF NOT EXISTS investment_balances (
      account_id TEXT NOT NULL,
      currency_code TEXT NOT NULL,
      source TEXT NOT NULL DEFAULT 'snaptrade',
      cash TEXT,
      buying_power TEXT,
      as_of_at TEXT NOT NULL,
      PRIMARY KEY(account_id, currency_code),
      FOREIGN KEY(account_id) REFERENCES accounts(id)
    )`,
    `CREATE TABLE IF NOT EXISTS investment_activities (
      id TEXT PRIMARY KEY,
      account_id TEXT NOT NULL,
      source TEXT NOT NULL DEFAULT 'snaptrade',
      activity_type TEXT NOT NULL,
      trade_date TEXT,
      settlement_date TEXT,
      symbol TEXT,
      description TEXT,
      units TEXT,
      price TEXT,
      amount TEXT,
      fee TEXT,
      currency_code TEXT NOT NULL DEFAULT 'USD',
      external_reference_id TEXT,
      updated_at TEXT NOT NULL,
      FOREIGN KEY(account_id) REFERENCES accounts(id)
    )`,
    `CREATE INDEX IF NOT EXISTS idx_investment_activities_account_date
       ON investment_activities(account_id, trade_date DESC)`,
    `CREATE TABLE IF NOT EXISTS investment_orders (
      id TEXT PRIMARY KEY,
      account_id TEXT NOT NULL,
      source TEXT NOT NULL DEFAULT 'snaptrade',
      brokerage_order_id TEXT,
      status TEXT NOT NULL,
      action TEXT,
      symbol TEXT,
      order_type TEXT,
      time_in_force TEXT,
      total_quantity TEXT,
      open_quantity TEXT,
      filled_quantity TEXT,
      execution_price TEXT,
      limit_price TEXT,
      stop_price TEXT,
      time_placed TEXT,
      time_updated TEXT,
      time_executed TEXT,
      expiry_date TEXT,
      FOREIGN KEY(account_id) REFERENCES accounts(id)
    )`,
    `CREATE INDEX IF NOT EXISTS idx_investment_orders_account_time
       ON investment_orders(account_id, time_placed DESC)`,
    `CREATE TABLE IF NOT EXISTS investment_sync_runs (
      id TEXT PRIMARY KEY,
      trigger TEXT NOT NULL,
      status TEXT NOT NULL,
      started_at TEXT NOT NULL,
      completed_at TEXT,
      connection_count INTEGER NOT NULL DEFAULT 0,
      account_count INTEGER NOT NULL DEFAULT 0,
      position_count INTEGER NOT NULL DEFAULT 0,
      activity_count INTEGER NOT NULL DEFAULT 0,
      order_count INTEGER NOT NULL DEFAULT 0,
      error_code TEXT,
      error_message_redacted TEXT
    )`,
    `CREATE INDEX IF NOT EXISTS idx_investment_sync_runs_started
       ON investment_sync_runs(started_at DESC)`,
    `CREATE TABLE IF NOT EXISTS account_provider_links (
      provider TEXT NOT NULL,
      provider_account_id TEXT NOT NULL,
      account_id TEXT NOT NULL,
      provider_connection_id TEXT,
      institution_account_id TEXT,
      last_seen_at TEXT NOT NULL,
      PRIMARY KEY(provider, provider_account_id),
      FOREIGN KEY(account_id) REFERENCES accounts(id)
    )`,
    `CREATE INDEX IF NOT EXISTS idx_account_provider_links_account
       ON account_provider_links(account_id)`,
    `CREATE TABLE IF NOT EXISTS living_obligations (
      id TEXT PRIMARY KEY,
      name TEXT NOT NULL,
      amount REAL NOT NULL,
      interval TEXT NOT NULL,
      kind TEXT NOT NULL,
      created_at TEXT NOT NULL
    )`,
    `CREATE TABLE IF NOT EXISTS living_merchant_defaults (
      merchant_key TEXT PRIMARY KEY,
      party_size INTEGER,
      trip_origin TEXT,
      trip_destination TEXT,
      trip_kind TEXT,
      living_kind TEXT,
      updated_at TEXT NOT NULL
    )`,
    `CREATE TABLE IF NOT EXISTS living_route_alternatives (
      origin TEXT NOT NULL,
      destination TEXT NOT NULL,
      label TEXT NOT NULL,
      monthly_amount REAL NOT NULL,
      PRIMARY KEY(origin, destination)
    )`,
  ], 'write');

  for (const sql of [
    "ALTER TABLE living_merchant_defaults ADD COLUMN living_kind TEXT",
    "ALTER TABLE living_merchant_defaults ADD COLUMN monthly_amount REAL",
  ]) {
    try {
      await db.execute(sql);
    } catch (e) {
      const msg = String(e);
      if (!msg.includes('duplicate column name') && !msg.includes('already exists')) throw e;
    }
  }

  // v3 created the investment tables before provider provenance was explicit.
  // New databases already include these columns in CREATE TABLE above.
  for (const sql of [
    "ALTER TABLE investment_positions ADD COLUMN source TEXT",
    "ALTER TABLE investment_balances ADD COLUMN source TEXT",
    "ALTER TABLE investment_activities ADD COLUMN source TEXT",
    "ALTER TABLE investment_sync_runs ADD COLUMN order_count INTEGER NOT NULL DEFAULT 0",
  ]) {
    try {
      await db.execute(sql);
    } catch (e) {
      const msg = String(e);
      if (!msg.includes('duplicate column name') && !msg.includes('already exists')) throw e;
    }
  }

  // Backfill SPEC §8.6: infer source, account_class, currency for legacy rows.
  // A non-null item_id is authoritative even if an older build left source
  // null or incorrectly labelled it. Institution metadata belongs to the
  // Plaid item, so copy it onto legacy account rows for upstream compatibility.
  await db.batch([
    `UPDATE accounts
        SET source = 'plaid',
            institution_name = COALESCE(
              NULLIF(TRIM(institution_name), ''),
              (SELECT NULLIF(TRIM(p.institution_name), '')
                 FROM plaid_items p WHERE p.item_id = accounts.item_id)
            )
      WHERE item_id IS NOT NULL`,
    `UPDATE accounts SET source = CASE
        WHEN subtype = 'manual' THEN 'manual'
        WHEN item_id IS NOT NULL THEN 'plaid'
        ELSE 'imported'
      END
      WHERE source IS NULL`,
    `UPDATE accounts SET account_class = CASE
        WHEN type IN ('credit', 'loan') THEN 'liability'
        ELSE 'asset'
      END
      WHERE account_class IS NULL`,
    `UPDATE accounts SET currency_code = 'USD' WHERE currency_code IS NULL`,
    `UPDATE accounts SET created_at = COALESCE(created_at, datetime('now')),
                         updated_at = COALESCE(updated_at, datetime('now'))`,
    `UPDATE investment_positions SET source = CASE
        WHEN id LIKE 'demo-%' THEN 'demo' ELSE 'snaptrade' END
      WHERE source IS NULL`,
    `UPDATE investment_balances SET source = CASE
        WHEN account_id LIKE 'demo-%' THEN 'demo' ELSE 'snaptrade' END
      WHERE source IS NULL`,
    `UPDATE investment_activities SET source = CASE
        WHEN id LIKE 'demo-%' THEN 'demo' ELSE 'snaptrade' END
      WHERE source IS NULL`,
  ], 'write');

  // Exactly one provider owns an account's balance series (SPEC §9.1).
  //
  // Plaid and SnapTrade both report Robinhood accounts, and SnapTrade folds its
  // accounts onto the matching Plaid rows. With both writing balance_history for
  // the same account, whichever synced last owned that date and the stored
  // series alternated between two providers' views of the same money, sawtoothing
  // net worth by tens of thousands.
  //
  // The authority is decided by which provider actually knows the number:
  // SnapTrade for brokerage accounts, where it reports positions plus the cash
  // sweep that Plaid omits, and Plaid for deposit and credit accounts, where
  // SnapTrade's account total can bundle balances that are not that account's.
  await db.execute(
    `UPDATE accounts SET balance_source = CASE
        WHEN source = 'manual' THEN 'manual'
        WHEN source = 'demo' THEN 'demo'
        WHEN source = 'snaptrade' THEN 'snaptrade'
        WHEN type = 'investment'
          AND EXISTS (SELECT 1 FROM investment_balances ib WHERE ib.account_id = accounts.id)
          THEN 'snaptrade'
        ELSE 'plaid' END
      WHERE balance_source IS NULL`,
  );

  // Legacy negative-value manual accounts are outstanding liabilities entered
  // with a minus sign. Normalize: flip history to positive "amount owed" and
  // mark the account as a manual liability (SPEC §8.6). Idempotent because
  // after the flip the latest balance is no longer negative.
  const negatives = await db.execute(
    `SELECT a.id FROM accounts a
     JOIN balance_history bh ON bh.account_id = a.id
     WHERE a.source = 'manual'
       AND bh.date = (SELECT MAX(date) FROM balance_history WHERE account_id = a.id)
       AND bh.balance < 0`,
  );
  for (const row of negatives.rows as unknown as { id: string }[]) {
    await db.batch([
      { sql: 'UPDATE balance_history SET balance = ABS(balance) WHERE account_id = ?', args: [row.id] },
      { sql: "UPDATE accounts SET account_class = 'liability', manual_category = COALESCE(manual_category, 'loan') WHERE id = ?", args: [row.id] },
    ], 'write');
  }

  await db.execute({
    sql: `INSERT INTO settings (key, value) VALUES (?, ?)
          ON CONFLICT(key) DO UPDATE SET value = excluded.value`,
    args: [VERSION_KEY, String(NATIVE_SCHEMA_VERSION)],
  });

  return { migrated: true, schemaVersion: NATIVE_SCHEMA_VERSION };
}
