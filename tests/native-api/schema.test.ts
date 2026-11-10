import { describe, it, expect, beforeAll, vi } from 'vitest';

vi.mock('../../native-api/src/core/database.js', async () => {
  const { makeTestDb } = await import('./helpers/makeTestDb.js');
  const db = await makeTestDb();
  return { db, initDb: async () => {} };
});
import { db } from '../../native-api/src/core/database.js';
import { ensureNativeSchema, NATIVE_SCHEMA_VERSION } from '../../native-api/src/services/schema.js';
import { recoverInterruptedSyncRuns } from '../../native-api/src/services/syncjobs.js';

// Migration tests (SPEC §27.2): legacy rows in, correct inference out, values
// conserved, version recorded, idempotent re-run.

beforeAll(async () => {
  // Reproduce a database created by the first native schema version, where an
  // upstream Plaid sync could leave provenance inconsistent.
  await db.execute('ALTER TABLE accounts ADD COLUMN source TEXT');
  // Legacy upstream-shaped rows: no source/class/currency columns filled.
  await db.batch([
    { sql: `INSERT INTO plaid_items (item_id, access_token, institution_name) VALUES ('item-a', 'encrypted-test-token', 'Robinhood')`, args: [] },
    { sql: `INSERT INTO accounts (id, name, type, subtype, item_id) VALUES ('p1', 'Checking', 'depository', 'checking', 'item-a')`, args: [] },
    { sql: `INSERT INTO accounts (id, name, type, subtype, item_id, source) VALUES ('p2', 'Brokerage', 'investment', 'brokerage', 'item-a', 'manual')`, args: [] },
    { sql: `INSERT INTO accounts (id, name, type, subtype) VALUES ('m1', 'Old Mortgage', 'other', 'manual')`, args: [] },
    { sql: `INSERT INTO accounts (id, name, type, subtype) VALUES ('c1', 'Imported CSV', 'depository', 'checking')`, args: [] },
    { sql: `INSERT INTO accounts (id, name, type, subtype, item_id) VALUES ('l1', 'Car Loan', 'loan', 'auto', 'item-a')`, args: [] },
    // Legacy negative-value manual liability (SPEC §8.6).
    { sql: `INSERT INTO balance_history (account_id, balance, date) VALUES ('m1', -250000, '2026-01-01')`, args: [] },
    { sql: `INSERT INTO balance_history (account_id, balance, date) VALUES ('m1', -249000, '2026-02-01')`, args: [] },
    { sql: `INSERT INTO balance_history (account_id, balance, date) VALUES ('p1', 5000, '2026-02-01')`, args: [] },
  ], 'write');
});

describe('native schema migration (SPEC §8.6/§27.2)', () => {
  it('migrates in place and records the schema version', async () => {
    const result = await ensureNativeSchema();
    expect(result.migrated).toBe(true);
    const version = await db.execute("SELECT value FROM settings WHERE key = 'native_api_schema_version'");
    expect(Number((version.rows[0] as never as { value: string }).value)).toBe(NATIVE_SCHEMA_VERSION);
  });

  it('infers source and account_class from legacy shapes', async () => {
    const rows = await db.execute('SELECT id, source, account_class, currency_code FROM accounts ORDER BY id');
    const byId = Object.fromEntries((rows.rows as never as { id: string; source: string; account_class: string; currency_code: string }[])
      .map((r) => [r.id, r]));
    expect(byId.p1.source).toBe('plaid');
    expect(byId.p2.source).toBe('plaid');
    expect(byId.m1.source).toBe('manual');
    expect(byId.c1.source).toBe('imported');
    expect(byId.p1.account_class).toBe('asset');
    expect(byId.l1.account_class).toBe('liability'); // loan → liability
    expect(byId.p1.currency_code).toBe('USD');
    const institution = await db.execute("SELECT institution_name FROM accounts WHERE id = 'p2'");
    expect((institution.rows[0] as never as { institution_name: string }).institution_name).toBe('Robinhood');
  });

  it('normalizes legacy negative manual balances to positive amount-owed liabilities', async () => {
    const account = await db.execute("SELECT account_class, manual_category FROM accounts WHERE id = 'm1'");
    expect((account.rows[0] as never as { account_class: string }).account_class).toBe('liability');
    expect((account.rows[0] as never as { manual_category: string }).manual_category).toBe('loan');
    const history = await db.execute("SELECT balance FROM balance_history WHERE account_id = 'm1' ORDER BY date");
    const balances = (history.rows as never as { balance: number }[]).map((r) => Number(r.balance));
    expect(balances).toEqual([250000, 249000]); // sign flipped, values conserved
  });

  it('closes an orphaned running sync after a process interruption', async () => {
    await db.execute({
      sql: `INSERT INTO sync_runs (id, trigger, force, status, started_at)
            VALUES ('sync-interrupted', 'scheduled', 1, 'running', '2026-08-12T01:00:00Z')`,
      args: [],
    });

    expect(await recoverInterruptedSyncRuns()).toBe(1);
    const row = await db.execute(
      "SELECT status, completed_at, summary_json FROM sync_runs WHERE id = 'sync-interrupted'",
    );
    const recovered = row.rows[0] as unknown as {
      status: string; completed_at: string | null; summary_json: string | null;
    };
    expect(recovered.status).toBe('failed');
    expect(recovered.completed_at).not.toBeNull();
    expect(JSON.parse(recovered.summary_json ?? '{}').errorCode).toBe('INTERRUPTED');
  });

  it('is idempotent: a second run changes nothing', async () => {
    const result = await ensureNativeSchema();
    expect(result.migrated).toBe(false);
    const history = await db.execute("SELECT balance FROM balance_history WHERE account_id = 'm1' ORDER BY date");
    expect((history.rows as never as { balance: number }[]).map((r) => Number(r.balance))).toEqual([250000, 249000]);
  });

  it('creates provider-neutral investment persistence tables', async () => {
    const tables = await db.execute(`SELECT name FROM sqlite_master
      WHERE type = 'table' AND name IN (
        'snaptrade_connections', 'investment_positions', 'investment_balances',
        'investment_activities', 'investment_orders', 'investment_sync_runs',
        'account_provider_links') ORDER BY name`);
    expect((tables.rows as unknown as { name: string }[]).map((row) => row.name)).toEqual([
      'account_provider_links',
      'investment_activities',
      'investment_balances',
      'investment_orders',
      'investment_positions',
      'investment_sync_runs',
      'snaptrade_connections',
    ]);
    const syncColumns = await db.execute(`PRAGMA table_info(investment_sync_runs)`);
    expect((syncColumns.rows as unknown as { name: string }[]).map((row) => row.name))
      .toContain('order_count');
  });

  it('upgrades an existing v4 database instead of skipping the order table', async () => {
    await db.batch([
      { sql: 'DROP TABLE investment_orders', args: [] },
      {
        sql: `UPDATE settings SET value = '4' WHERE key = 'native_api_schema_version'`,
        args: [],
      },
    ], 'write');

    const result = await ensureNativeSchema();
    expect(result).toEqual({ migrated: true, schemaVersion: NATIVE_SCHEMA_VERSION });
    const table = await db.execute(
      "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'investment_orders'",
    );
    expect(table.rows).toHaveLength(1);
  });
});
