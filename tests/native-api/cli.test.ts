import { describe, it, expect, beforeAll, afterAll, beforeEach, afterEach, vi } from 'vitest';
import fs from 'node:fs';
import path from 'node:path';

// Same pattern as maintenance.test.ts: mock both core/paths.js (so lock files,
// exports, and backups land in a throwaway temp dir instead of the real
// %USERPROFILE%\.assetscope) and core/database.js (an in-memory mock db).
const tmpDataDir = vi.hoisted(() => {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const nodeFs = require('node:fs') as typeof import('node:fs');
  const nodeOs = require('node:os') as typeof import('node:os');
  const nodePath = require('node:path') as typeof import('node:path');
  return nodeFs.mkdtempSync(nodePath.join(nodeOs.tmpdir(), 'assetscope-cli-test-'));
});

vi.mock('../../native-api/src/core/paths.js', () => ({
  DATA_DIR: tmpDataDir,
  DATABASE_PATH: `${tmpDataDir}/assetscope.db`,
}));
vi.mock('../../native-api/src/core/database.js', async () => {
  const { makeTestDb } = await import('./helpers/makeTestDb.js');
  const db = await makeTestDb();
  return { db, initDb: async () => {} };
});

import { db } from '../../native-api/src/core/database.js';
import { ensureNativeSchema, NATIVE_SCHEMA_VERSION } from '../../native-api/src/services/schema.js';
import { runCli } from '../../native-api/src/cli.js';

function daysAgo(n: number): string {
  const d = new Date();
  d.setUTCDate(d.getUTCDate() - n);
  return d.toISOString().slice(0, 10);
}

beforeAll(async () => {
  await ensureNativeSchema();
  await db.batch([
    {
      sql: `INSERT INTO accounts (id, name, nickname, owner, type, subtype, institution_name, mask,
              source, account_class, currency_code, excluded, created_at, updated_at)
            VALUES ('acct-chk', 'Everyday Checking', NULL, 'Alex', 'depository', 'checking',
              'First National Bank', '4242', 'plaid', 'asset', 'USD', 0, datetime('now'), datetime('now'))`,
      args: [],
    },
    {
      sql: `INSERT INTO accounts (id, name, nickname, owner, type, subtype, institution_name, mask,
              source, account_class, currency_code, excluded, created_at, updated_at)
            VALUES ('acct-credit', 'Rewards Visa', NULL, 'Alex', 'credit', 'credit card',
              'First National Bank', '1234', 'plaid', 'liability', 'USD', 0, datetime('now'), datetime('now'))`,
      args: [],
    },
    {
      sql: `INSERT INTO accounts (id, name, nickname, owner, type, subtype, institution_name, mask,
              source, account_class, manual_category, currency_code, excluded, created_at, updated_at)
            VALUES ('acct-car', 'Subaru Outback', NULL, NULL, 'other', 'manual', NULL, NULL,
              'manual', 'asset', 'vehicle', 'USD', 0, datetime('now'), datetime('now'))`,
      args: [],
    },
  ], 'write');
  for (let day = 5; day >= 0; day--) {
    await db.batch([
      { sql: `INSERT OR REPLACE INTO balance_history (account_id, balance, date) VALUES ('acct-chk', ?, ?)`, args: [1000 + day, daysAgo(day)] },
      { sql: `INSERT OR REPLACE INTO balance_history (account_id, balance, date) VALUES ('acct-credit', ?, ?)`, args: [200, daysAgo(day)] },
    ], 'write');
  }
  await db.execute({ sql: `INSERT OR REPLACE INTO balance_history (account_id, balance, date) VALUES ('acct-car', 9000, ?)`, args: [daysAgo(3)] });
  await db.batch([
    {
      sql: `INSERT INTO transactions (id, account_id, date, name, merchant_name, amount, category, pending, ignored)
            VALUES ('txn-groceries', 'acct-chk', ?, 'WHOLE FOODS MARKET', 'Whole Foods', 85.50, 'FOOD_AND_DRINK', 0, 0)`,
      args: [daysAgo(2)],
    },
    {
      sql: `INSERT INTO transactions (id, account_id, date, name, merchant_name, amount, category, pending, ignored)
            VALUES ('txn-coffee', 'acct-chk', ?, 'BLUE BOTTLE COFFEE', 'Blue Bottle', 6.25, 'FOOD_AND_DRINK', 0, 0)`,
      args: [daysAgo(1)],
    },
    {
      sql: `INSERT INTO transactions (id, account_id, date, name, merchant_name, amount, category, pending, ignored)
            VALUES ('txn-pending', 'acct-chk', ?, 'PENDING CHARGE', NULL, 40.00, 'SHOPPING', 1, 0)`,
      args: [daysAgo(1)],
    },
    {
      sql: `INSERT INTO transactions (id, account_id, date, name, merchant_name, amount, category, pending, ignored)
            VALUES ('txn-paycheck', 'acct-chk', ?, 'ACME CORP PAYROLL', NULL, -2000.00, 'INCOME', 0, 0)`,
      args: [daysAgo(3)],
    },
  ], 'write');
});

afterAll(() => {
  fs.rmSync(tmpDataDir, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
});

// eslint-disable-next-line @typescript-eslint/no-explicit-any
let stdoutSpy: any;
// eslint-disable-next-line @typescript-eslint/no-explicit-any
let stderrSpy: any;

beforeEach(() => {
  stdoutSpy = vi.spyOn(process.stdout, 'write').mockImplementation(() => true);
  stderrSpy = vi.spyOn(process.stderr, 'write').mockImplementation(() => true);
});

afterEach(() => {
  stdoutSpy.mockRestore();
  stderrSpy.mockRestore();
  delete process.env.PLAID_CLIENT_ID;
  delete process.env.PLAID_SECRET;
});

async function run(argv: string[]): Promise<{ code: number; stdout: string; stderr: string }> {
  stdoutSpy.mockClear();
  stderrSpy.mockClear();
  const code = await runCli(['--data-dir', tmpDataDir, ...argv]);
  return {
    code,
    stdout: stdoutSpy.mock.calls.map((c: unknown[]) => String(c[0])).join(''),
    stderr: stderrSpy.mock.calls.map((c: unknown[]) => String(c[0])).join(''),
  };
}

async function runJson<T = unknown>(argv: string[]): Promise<{ code: number; data: T }> {
  const { code, stdout } = await run(argv);
  return { code, data: JSON.parse(stdout) as T };
}

describe('read commands', () => {
  it('summary: totals reflect carry-forward balances', async () => {
    const { code, data } = await runJson<{ summary: { assets: { amount: string }; liabilities: { amount: string }; netWorth: { amount: string } } }>(['summary']);
    expect(code).toBe(0);
    expect(data.summary.assets.amount).toBe('10000.00');
    expect(data.summary.liabilities.amount).toBe('200.00');
    expect(data.summary.netWorth.amount).toBe('9800.00');
  });

  it('accounts: lists all three, filters by category and search', async () => {
    const all = await runJson<{ accounts: { id: string; category: string }[] }>(['accounts']);
    expect(all.data.accounts).toHaveLength(3);

    const vehicles = await runJson<{ accounts: { id: string }[] }>(['accounts', '--category', 'vehicle']);
    expect(vehicles.data.accounts.map((a) => a.id)).toEqual(['acct-car']);

    const searched = await runJson<{ accounts: { id: string }[] }>(['accounts', '--search', 'visa']);
    expect(searched.data.accounts.map((a) => a.id)).toEqual(['acct-credit']);
  });

  it('accounts, transactions, snapshot: a credit card carries the card chosen in the app, and --redact hides it', async () => {
    type Listed = { accounts: { id: string; displayName: string; cardProduct: { id: string; name: string; source: string } | null }[] };
    const before = await runJson<Listed>(['accounts']);
    expect(before.data.accounts.map((a) => a.cardProduct)).toEqual([null, null, null]);

    const { putRewardCard } = await import('../../native-api/src/services/rewards.js');
    await db.execute({
      sql: `INSERT INTO transactions (id, account_id, date, name, merchant_name, amount, category, pending, ignored)
            VALUES ('txn-card', 'acct-credit', ?, 'CORNER CAFE', 'Corner Cafe', 12.00, 'FOOD_AND_DRINK', 0, 0)`,
      args: [daysAgo(1)],
    });
    await putRewardCard('acct-credit', { productId: 'chase-freedom-flex' });
    try {
      const listed = await runJson<Listed>(['accounts', '--category', 'credit']);
      expect(listed.data.accounts).toEqual([expect.objectContaining({
        id: 'acct-credit',
        displayName: 'Chase Freedom Flex',
        cardProduct: { id: 'chase-freedom-flex', name: 'Chase Freedom Flex', source: 'chosen' },
      })]);

      const txns = await runJson<{ transactions: { id: string; accountName: string }[] }>(['transactions']);
      expect(txns.data.transactions.find((t) => t.id === 'txn-card')!.accountName).toBe('Chase Freedom Flex');
      expect(txns.data.transactions.find((t) => t.id === 'txn-coffee')!.accountName).toBe('Everyday Checking');

      for (const argv of [['snapshot', '--redact'], ['transactions', '--redact']]) {
        const { stdout } = await run(argv);
        expect(stdout).not.toContain('Chase Freedom Flex');
        expect(stdout).not.toContain('chase-freedom-flex');
        expect(stdout).not.toContain('Rewards Visa');
      }
    } finally {
      await putRewardCard('acct-credit', { productId: null });
      await db.execute("DELETE FROM transactions WHERE id = 'txn-card'");
    }
  });

  it('account <id>: returns the account and its recent snapshots', async () => {
    const { code, data } = await runJson<{ account: { id: string }; recentSnapshots: { amount: string }[] }>(['account', 'acct-chk']);
    expect(code).toBe(0);
    expect(data.account.id).toBe('acct-chk');
    expect(data.recentSnapshots.length).toBeGreaterThan(0);
  });

  it('account: missing id fails validation, exit 2', async () => {
    const { code, stderr } = await run(['account']);
    expect(code).toBe(2);
    expect(JSON.parse(stderr).error.code).toBe('VALIDATION_ERROR');
  });

  it('account: unknown id is not found, exit 4', async () => {
    const { code, stderr } = await run(['account', 'does-not-exist']);
    expect(code).toBe(4);
    expect(JSON.parse(stderr).error.code).toBe('NOT_FOUND');
  });

  it('net-worth: returns a point series ending today', async () => {
    const { code, data } = await runJson<{ points: { date: string; netWorth: { amount: string } }[] }>(['net-worth', '--range', '1m']);
    expect(code).toBe(0);
    expect(data.points.length).toBeGreaterThan(0);
    expect(data.points.at(-1)?.netWorth.amount).toBe('9800.00');
  });

  it('allocation: buckets bank cash and vehicle assets', async () => {
    const { data } = await runJson<{ totalAssets: { amount: string }; slices: { bucket: string; amount: { amount: string } }[] }>(['allocation']);
    expect(data.totalAssets.amount).toBe('10000.00');
    const buckets = Object.fromEntries(data.slices.map((s) => [s.bucket, s.amount.amount]));
    expect(buckets.bankCash).toBe('1000.00');
    expect(buckets.vehicle).toBe('9000.00');
  });

  it('trends: covers the bank/cash bucket', async () => {
    const { data } = await runJson<{ assetBuckets: { bucket: string }[] }>(['trends', '--range', '1m']);
    expect(data.assetBuckets.some((b) => b.bucket === 'bankCash')).toBe(true);
  });

  it('investments/positions/activities/orders/realized: run cleanly with no investment accounts', async () => {
    expect((await run(['investments'])).code).toBe(0);
    expect((await runJson<{ positions: unknown[] }>(['positions'])).data.positions).toEqual([]);
    expect((await runJson<{ activities: unknown[] }>(['activities'])).data.activities).toEqual([]);
    expect((await runJson<{ orders: unknown[] }>(['orders'])).data.orders).toEqual([]);
    const realized = await runJson<{ summary: { total: string } }>(['realized']);
    expect(realized.data.summary.total).toBe('0.00');
  });

  it('insights: returns an (empty) period-scoped result', async () => {
    const { code, data } = await runJson<{ items: unknown[] }>(['insights']);
    expect(code).toBe(0);
    expect(data.items).toEqual([]);
  });

  it('transactions: lists newest first and excludes pending', async () => {
    const { code, data } = await runJson<{ transactions: { id: string; name: string }[] }>(['transactions']);
    expect(code).toBe(0);
    expect(data.transactions.map((t) => t.id)).toEqual(['txn-coffee', 'txn-groceries', 'txn-paycheck']);
  });

  it('transactions: filters by category, account, and limit', async () => {
    const byCategory = await runJson<{ transactions: { id: string }[] }>(['transactions', '--category', 'INCOME']);
    expect(byCategory.data.transactions.map((t) => t.id)).toEqual(['txn-paycheck']);

    const limited = await runJson<{ transactions: unknown[] }>(['transactions', '--limit', '1']);
    expect(limited.data.transactions).toHaveLength(1);

    const wrongAccount = await runJson<{ transactions: unknown[] }>(['transactions', '--account', 'acct-credit']);
    expect(wrongAccount.data.transactions).toEqual([]);
  });

  it('transactions: --redact hides merchant and transaction names, keeps amounts', async () => {
    const { data } = await runJson<{ transactions: { id: string; name: string; merchantName: string | null; amount: string }[] }>(
      ['transactions', '--redact'],
    );
    const raw = JSON.stringify(data);
    expect(raw).not.toContain('Whole Foods');
    expect(raw).not.toContain('WHOLE FOODS MARKET');
    expect(raw).not.toContain('Blue Bottle');
    expect(raw).not.toContain('ACME CORP PAYROLL');

    const groceries = data.transactions.find((t) => t.id === 'txn-groceries')!;
    expect(groceries.amount).toBe('85.50');
    expect(groceries.merchantName).toMatch(/^merchant-\d+$/);
  });

  it('living: returns leftover, split, and a 0–14 dining slider table', async () => {
    const { code, data } = await runJson<{
      leftover: { projected: string };
      split: { flexiblePosted: string };
      diningOutcomes: { mealsPerWeek: number }[];
    }>(['living']);
    expect(code).toBe(0);
    expect(data.leftover.projected).toMatch(/^-?\d+\.\d{2}$/);
    expect(data.diningOutcomes).toHaveLength(15);
    expect(data.split.flexiblePosted).toMatch(/^\d+\.\d{2}$/);
  });

  it('rewards: ranks every spending category and rejects unknown ranges', async () => {
    const { code, data } = await runJson<{ window: { range: string }; categories: { category: string }[] }>(['rewards']);
    expect(code).toBe(0);
    expect(data.window.range).toBe('3m');
    expect(data.categories).toHaveLength(12);
    expect((await run(['rewards', '--range', '5y'])).code).toBe(2);
  });

  it('spending --by-category: totals spend per category, excluding income', async () => {
    const { code, data } = await runJson<{ byCategory: { categories: { category: string; amount: string }[] } }>(
      ['spending', '--by-category', '--range', '1y'],
    );
    expect(code).toBe(0);
    const byCategory = Object.fromEntries(data.byCategory.categories.map((c) => [c.category, c.amount]));
    expect(byCategory.FOOD_AND_DRINK).toBe('91.75'); // 85.50 groceries + 6.25 coffee
    expect(byCategory.INCOME).toBeUndefined();
  });

  it('spending --by-month: buckets cash flow by calendar month', async () => {
    const { data } = await runJson<{ byMonth: { totals: { moneyIn: string; moneyOut: string } } }>(
      ['spending', '--by-month', '--range', '1y'],
    );
    expect(data.byMonth.totals.moneyIn).toBe('2000.00'); // the paycheck
    expect(data.byMonth.totals.moneyOut).toBe('91.75'); // groceries + coffee (pending excluded)
  });

  it('spending: with neither flag, returns both byCategory and byMonth', async () => {
    const { data } = await runJson<{ byCategory: unknown; byMonth: unknown }>(['spending', '--range', '1y']);
    expect(data.byCategory).toBeDefined();
    expect(data.byMonth).toBeDefined();
  });

  it('sync-status: reports no in-process job and no runs yet', async () => {
    const { data } = await runJson<{ job: null; runs: unknown[]; investmentSync: null }>(['sync-status']);
    expect(data.job).toBeNull();
    expect(data.runs).toEqual([]);
    expect(data.investmentSync).toBeNull();
  });

  it('--table renders a table instead of JSON', async () => {
    const { code, stdout } = await run(['accounts', '--table']);
    expect(code).toBe(0);
    expect(stdout.trim().startsWith('{')).toBe(false);
    expect(stdout).toContain('displayName');
  });
});

describe('export and backup (the CLI\'s only writes besides sync)', () => {
  it('export accounts: writes a CSV with one row per account', async () => {
    const { code, data } = await runJson<{ path: string; rows: number }>(['export', 'accounts']);
    expect(code).toBe(0);
    expect(data.rows).toBe(3);
    expect(fs.existsSync(data.path)).toBe(true);
    expect(fs.readFileSync(data.path, 'utf8')).toContain('acct-car,Subaru Outback');
  });

  it('export net-worth: writes a carry-forward CSV', async () => {
    const { data } = await runJson<{ path: string; rows: number }>(['export', 'net-worth', '--range', '1m']);
    expect(fs.existsSync(data.path)).toBe(true);
    expect(fs.readFileSync(data.path, 'utf8')).toContain('date,assets,liabilities,net_worth');
  });

  it('export: unknown target fails validation', async () => {
    const { code, stderr } = await run(['export', 'bogus']);
    expect(code).toBe(2);
    expect(JSON.parse(stderr).error.code).toBe('VALIDATION_ERROR');
  });

  it('backup then backup list: the created backup is listed', async () => {
    const created = await runJson<{ id: string }>(['backup']);
    expect(created.data.id).toMatch(/^assetscope\..*\.manual\.bak$/);
    const listed = await runJson<{ backups: { id: string }[] }>(['backup', 'list']);
    expect(listed.data.backups.some((b) => b.id === created.data.id)).toBe(true);
  });
});

describe('doctor', () => {
  it('reports data dir, db path, schema version, and counts', async () => {
    const { code, data } = await runJson<{
      dataDir: string; databasePath: string; nativeSchemaVersion: number;
      sidecarLockHeldByAnotherProcess: boolean; plaidConfigured: boolean; snapTradeConfigured: boolean;
      accountCount: number; snapshotCount: number;
    }>(['doctor']);
    expect(code).toBe(0);
    expect(data.dataDir).toBe(tmpDataDir);
    expect(data.databasePath).toBe(`${tmpDataDir}/assetscope.db`);
    expect(data.nativeSchemaVersion).toBe(NATIVE_SCHEMA_VERSION);
    expect(data.sidecarLockHeldByAnotherProcess).toBe(false);
    expect(data.plaidConfigured).toBe(false);
    expect(data.snapTradeConfigured).toBe(false);
    expect(data.accountCount).toBe(3);
    expect(data.snapshotCount).toBe(13); // 6 chk + 6 credit + 1 car
  });
});

describe('sync (delegates to sync-host.ts, no second write path)', () => {
  it('with no provider configured: queues via the pending-sync marker and exits 0', async () => {
    const markerPath = path.join(tmpDataDir, 'runtime', 'pending-sync.json');
    fs.rmSync(markerPath, { force: true });

    const { code, data } = await runJson<{ status: string }>(['sync']);
    expect(code).toBe(0);
    expect(data.status).toBe('queued');
    expect(fs.existsSync(markerPath)).toBe(true);
  });
});

describe('snapshot', () => {
  it('unredacted: has the documented shape and drops sparklines', async () => {
    const { code, data } = await runJson<{
      schemaVersion: number;
      provenance: { redaction: string };
      accounts: Record<string, unknown>[];
      spending: {
        last3Months: { categories: { category: string; amount: string }[] };
        last12Months: { categories: { category: string; amount: string }[] };
        monthlyCashFlow: { totals: { moneyIn: string; moneyOut: string } };
      };
      dataQuality: { excludedAccountCount: number };
    }>(['snapshot']);
    expect(code).toBe(0);
    expect(data.schemaVersion).toBe(1);
    expect(data.provenance.redaction).toBe('none');
    expect(data.accounts).toHaveLength(3);
    expect(data.accounts.every((a) => !('sparkline' in a))).toBe(true);

    const last3 = Object.fromEntries(data.spending.last3Months.categories.map((c) => [c.category, c.amount]));
    expect(last3.FOOD_AND_DRINK).toBe('91.75'); // 85.50 groceries + 6.25 coffee, pending excluded
    expect(last3.INCOME).toBeUndefined(); // income excluded from spend-by-category
    expect(data.spending.monthlyCashFlow.totals.moneyIn).toBe('2000.00'); // the paycheck
    expect(data.spending.monthlyCashFlow.totals.moneyOut).toBe('91.75');
  });

  it('--redact: hides institution/account/owner identity but keeps numbers and relationships', async () => {
    const { data } = await runJson<{
      provenance: { redaction: string };
      accounts: { id: string; institutionName: string | null; displayName: string; mask: string | null }[];
      summary: { assets: { amount: string } };
    }>(['snapshot', '--redact']);
    expect(data.provenance.redaction).toBe('redacted');

    const raw = JSON.stringify(data);
    expect(raw).not.toContain('First National Bank');
    expect(raw).not.toContain('Everyday Checking');
    expect(raw).not.toContain('Rewards Visa');
    expect(raw).not.toContain('Subaru Outback');
    expect(raw).not.toContain('Alex');
    expect(raw).not.toContain('4242');
    expect(raw).not.toContain('1234');

    // Numbers are untouched by redaction.
    expect(data.summary.assets.amount).toBe('10000.00');

    // Same real institution -> same pseudonym across accounts (relationship preserved).
    const chk = data.accounts.find((a) => a.id === 'acct-chk')!;
    const credit = data.accounts.find((a) => a.id === 'acct-credit')!;
    expect(chk.institutionName).not.toBeNull();
    expect(chk.institutionName).toBe(credit.institutionName);
  });

  it('--out FILE: writes the snapshot to disk and reports a confirmation on stdout', async () => {
    const outFile = path.join(tmpDataDir, 'portfolio.json');
    const { code, data } = await runJson<{ written: string; schemaVersion: number }>(['snapshot', '--out', outFile]);
    expect(code).toBe(0);
    expect(data.written).toBe(outFile);
    expect(fs.existsSync(outFile)).toBe(true);
    const written = JSON.parse(fs.readFileSync(outFile, 'utf8'));
    expect(written.schemaVersion).toBe(1);
  });
});

describe('argument handling', () => {
  it('unknown command fails validation, exit 2', async () => {
    const { code, stderr } = await run(['not-a-command']);
    expect(code).toBe(2);
    expect(JSON.parse(stderr).error.code).toBe('VALIDATION_ERROR');
  });

  it('no command prints the help catalog, exit 0', async () => {
    const { code, data } = await runJson<{ name: string; commands: { name: string }[] }>([]);
    expect(code).toBe(0);
    expect(data.name).toBe('assetscope');
    expect(data.commands.some((c) => c.name === 'summary')).toBe(true);
  });

  it('help and --help print the catalog without opening a command', async () => {
    const viaHelp = await runJson<{ commands: { name: string }[] }>(['help']);
    const viaFlag = await runJson<{ commands: { name: string }[] }>(['--help']);
    expect(viaHelp.code).toBe(0);
    expect(viaFlag.code).toBe(0);
    expect(viaHelp.data.commands.map((c) => c.name)).toEqual(viaFlag.data.commands.map((c) => c.name));
  });

  it('help <command> and <command> --help print that command spec', async () => {
    const viaTopic = await runJson<{ command: { name: string; writes: boolean } }>(['help', 'accounts']);
    const viaFlag = await runJson<{ command: { name: string } }>(['accounts', '--help']);
    expect(viaTopic.code).toBe(0);
    expect(viaTopic.data.command.name).toBe('accounts');
    expect(viaTopic.data.command.writes).toBe(false);
    expect(viaFlag.data.command.name).toBe('accounts');
  });

  it('help for an unknown command fails validation', async () => {
    const { code, stderr } = await run(['help', 'not-a-command']);
    expect(code).toBe(2);
    expect(JSON.parse(stderr).error.code).toBe('VALIDATION_ERROR');
  });

  it('--redact is refused by a command that would still print real names', async () => {
    for (const command of ['accounts', 'rewards', 'summary']) {
      const { code, stdout, stderr } = await run([command, '--redact']);
      expect(code).toBe(2);
      expect(stdout).toBe('');
      expect(JSON.parse(stderr).error.message).toContain('--redact is not supported');
    }
  });
});

describe('assistant skill', () => {
  it('names every command in the help catalog, so an assistant can find each one', async () => {
    const skill = fs.readFileSync(path.resolve('.claude/skills/assetscope/SKILL.md'), 'utf8');
    const { data } = await runJson<{ commands: { name: string }[] }>(['help']);
    const missing = data.commands.map((c) => c.name).filter((name) => !new RegExp('`' + name + '[ `]').test(skill));
    expect(missing).toEqual([]);
  });
});

// Demo-mode tests run last: seedDemoData() adds ~7 more accounts to this
// file's shared in-memory database and is not reversible within the run.
describe('demo mode', () => {
  it('seeds demo data on top of any existing accounts', async () => {
    const doctor = await runJson<{ accountCount: number }>(['doctor', '--demo']);
    expect(doctor.data.accountCount).toBe(10); // 3 seeded above + 7 demo accounts

    const summary = await runJson<{ summary: { netWorth: { amount: string } } }>(['summary', '--demo']);
    expect(parseFloat(summary.data.summary.netWorth.amount)).toBeGreaterThan(50_000);
  });
});
