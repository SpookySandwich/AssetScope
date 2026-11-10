import { describe, it, expect, beforeAll, afterAll, vi } from 'vitest';
import fs from 'node:fs';
import path from 'node:path';

const tmpDataDir = vi.hoisted(() => {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const nodeFs = require('node:fs') as typeof import('node:fs');
  const nodeOs = require('node:os') as typeof import('node:os');
  const nodePath = require('node:path') as typeof import('node:path');
  return nodeFs.mkdtempSync(nodePath.join(nodeOs.tmpdir(), 'assetscope-test-'));
});

vi.mock('../../native-api/src/core/paths.js', () => ({ DATA_DIR: tmpDataDir, DATABASE_PATH: `${tmpDataDir}/assetscope.db` }));
vi.mock('../../native-api/src/core/database.js', async () => {
  const { makeTestDb } = await import('./helpers/makeTestDb.js');
  const db = await makeTestDb();
  return { db, initDb: async () => {} };
});
vi.mock('../../native-api/src/core/backup.js', () => ({ backupDb: async () => {} }));

import { db } from '../../native-api/src/core/database.js';
import { ensureNativeSchema } from '../../native-api/src/services/schema.js';
import {
  listBackups, createBackup, exportAccountsCsv, exportNetWorthCsv, categoryTrends,
} from '../../native-api/src/services/maintenance.js';

function daysAgo(n: number): string {
  const d = new Date();
  d.setUTCDate(d.getUTCDate() - n);
  return d.toISOString().slice(0, 10);
}

beforeAll(async () => {
  await ensureNativeSchema();
  await db.batch([
    { sql: `INSERT INTO accounts (id, name, type, subtype, source, account_class, currency_code, excluded)
            VALUES ('chk', 'Checking', 'depository', 'checking', 'plaid', 'asset', 'USD', 0)`, args: [] },
    { sql: `INSERT INTO accounts (id, name, type, subtype, source, account_class, manual_category, currency_code, excluded)
            VALUES ('car', 'Car', 'other', 'manual', 'manual', 'asset', 'vehicle', 'USD', 0)`, args: [] },
    { sql: `INSERT INTO accounts (id, name, type, subtype, source, account_class, currency_code, excluded)
            VALUES ('loan', 'Auto Loan', 'loan', 'auto', 'plaid', 'liability', 'USD', 0)`, args: [] },
  ], 'write');
  for (let day = 10; day >= 0; day--) {
    await db.batch([
      { sql: `INSERT OR REPLACE INTO balance_history (account_id, balance, date) VALUES ('chk', ?, ?)`, args: [1000 + day, daysAgo(day)] },
      { sql: `INSERT OR REPLACE INTO balance_history (account_id, balance, date) VALUES ('loan', ?, ?)`, args: [500, daysAgo(day)] },
    ], 'write');
  }
  await db.execute({ sql: `INSERT OR REPLACE INTO balance_history (account_id, balance, date) VALUES ('car', 9000, ?)`, args: [daysAgo(8)] });
});

afterAll(() => {
  fs.rmSync(tmpDataDir, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
});

describe('csv exports (SPEC §20.5/§21.4)', () => {
  it('writes accounts.csv into the exports directory', async () => {
    const result = await exportAccountsCsv();
    expect(result.rows).toBe(3);
    const content = fs.readFileSync(result.path, 'utf8');
    expect(content).toContain('id,name,institution,category,class,balance');
    expect(content).toContain('loan,Auto Loan');
    expect(result.path.startsWith(path.join(tmpDataDir, 'exports'))).toBe(true);
  });

  it('writes net-worth.csv with carry-forward documentation and correct math', async () => {
    const result = await exportNetWorthCsv(daysAgo(5), daysAgo(0), 'day');
    const content = fs.readFileSync(result.path, 'utf8');
    expect(content).toContain('as-of series');
    expect(content).toContain('date,assets,liabilities,net_worth');
    // Last row: chk 1000 + car 9000 (carried) = 10000 assets, 500 liabilities.
    const lastLine = content.trim().split('\n').at(-1)!;
    expect(lastLine).toBe(`${daysAgo(0)},10000.00,500.00,9500.00`);
  });
});

describe('backups (SPEC §21.4)', () => {
  it('creates and lists manual backups', async () => {
    const created = await createBackup();
    expect(created.id).toMatch(/^assetscope\..*\.manual\.bak$/);
    expect(created.sizeBytes).toBeGreaterThan(0);
    const backups = await listBackups();
    expect(backups.some((b) => b.id === created.id)).toBe(true);
  });
});

describe('category trends (SPEC §20.3)', () => {
  it('produces per-bucket as-of series with liabilities separate', async () => {
    const trends = await categoryTrends(daysAgo(5), daysAgo(0), 'day');
    const buckets = trends.assetBuckets.map((b) => b.bucket);
    expect(buckets).toEqual(['bankCash', 'vehicle']);
    const vehicle = trends.assetBuckets.find((b) => b.bucket === 'vehicle')!;
    // Carried forward from the day-8 snapshot on every sampled day.
    expect(vehicle.points.every((p) => p.amount === '9000.00')).toBe(true);
    expect(trends.liabilities.at(-1)?.amount).toBe('500.00');
    // Liabilities are never mixed into the asset stack (SPEC §20.3).
    expect(buckets).not.toContain('loan');
    expect(trends.institutionSeries).toHaveLength(1);
    expect(trends.institutionSeries[0].institution).toBe('manual');
    // Institution series is signed net worth, not movement since the range
    // start: checking + vehicle - loan.
    expect(trends.institutionSeries[0].points[0].amount).toBe('9505.00');
    expect(trends.institutionSeries[0].points.at(-1)?.amount).toBe('9500.00');
  });

  it('retains the first observed date so short weekly histories remain chartable', async () => {
    const trends = await categoryTrends(daysAgo(8), daysAgo(0), 'week');
    const manual = trends.institutionSeries.find((series) => series.institution === 'manual');
    expect(manual).toBeDefined();
    expect(manual!.points.length).toBeGreaterThanOrEqual(2);
    expect(manual!.points[0].amount).toBe('9508.00');
  });

  it('retains a connected institution whose contribution is flat in the selected range', async () => {
    await db.batch([
      { sql: `INSERT INTO accounts
          (id, name, type, subtype, institution_name, source, account_class, currency_code, excluded)
          VALUES ('flat-chase', 'Flat checking', 'depository', 'checking', 'Chase',
                  'plaid', 'asset', 'USD', 0)`, args: [] },
      { sql: `INSERT INTO balance_history (account_id, balance, date)
              VALUES ('flat-chase', 2500, ?)`, args: [daysAgo(5)] },
      { sql: `INSERT INTO balance_history (account_id, balance, date)
              VALUES ('flat-chase', 2500, ?)`, args: [daysAgo(0)] },
    ], 'write');

    const trends = await categoryTrends(daysAgo(5), daysAgo(0), 'day');
    const chase = trends.institutionSeries.find((series) => series.institution === 'Chase');
    expect(chase).toBeDefined();
    expect(chase!.points).toHaveLength(6);
    expect(chase!.points.every((point) => point.amount === '2500.00')).toBe(true);
  });

  it('shows a later-linked account only from its first observation onward', async () => {
    await db.batch([
      { sql: `INSERT INTO accounts
          (id, name, type, subtype, institution_name, source, account_class, currency_code, excluded)
          VALUES ('later-robinhood', 'Later brokerage', 'investment', 'brokerage', 'Robinhood',
                  'snaptrade', 'asset', 'USD', 0)`, args: [] },
      { sql: `INSERT INTO balance_history (account_id, balance, date) VALUES ('later-robinhood', 40000, ?)`,
        args: [daysAgo(2)] },
      { sql: `INSERT INTO balance_history (account_id, balance, date) VALUES ('later-robinhood', 40500, ?)`,
        args: [daysAgo(0)] },
    ], 'write');

    const trends = await categoryTrends(daysAgo(5), daysAgo(0), 'day');
    const robinhood = trends.institutionSeries.find((series) => series.institution === 'Robinhood');
    expect(robinhood).toBeDefined();
    expect(robinhood!.points.slice(0, 3).every((point) => point.amount === '0.00')).toBe(true);
    expect(robinhood!.points[3].amount).toBe('40000.00');
    expect(robinhood!.points.at(-1)?.amount).toBe('40500.00');
  });

  it('uses posted transactions to estimate each institution before its first balance snapshot', async () => {
    await db.batch([
      { sql: `INSERT INTO accounts
          (id, name, type, subtype, institution_name, source, account_class, currency_code, excluded)
          VALUES ('estimated-boa', 'Estimated checking', 'depository', 'checking',
                  'Bank of America Estimate', 'plaid', 'asset', 'USD', 0)`, args: [] },
      { sql: `INSERT INTO balance_history (account_id, balance, date)
              VALUES ('estimated-boa', 1000, ?)`, args: [daysAgo(10)] },
      { sql: `INSERT INTO transactions
          (id, account_id, date, name, amount, pending)
          VALUES ('estimated-boa-tx', 'estimated-boa', ?, 'Posted outflow', 100, 0)`,
        args: [daysAgo(15)] },
    ], 'write');

    const trends = await categoryTrends(daysAgo(20), daysAgo(0), 'day');
    const boa = trends.institutionSeries.find((series) => series.institution === 'Bank of America Estimate');
    expect(boa).toBeDefined();
    expect(boa!.points[0].amount).toBe('1100.00');
    expect(boa!.points.at(-1)?.amount).toBe('1000.00');
    expect(boa!.points.length).toBeGreaterThan(10);
  });

  it('keeps an institution positive after cash is transferred away', async () => {
    await db.batch([
      { sql: `INSERT INTO accounts
          (id, name, type, subtype, institution_name, source, account_class, currency_code, excluded)
          VALUES ('boa-positive', 'BOA checking', 'depository', 'checking',
                  'Bank of America', 'plaid', 'asset', 'USD', 0)`, args: [] },
      { sql: `INSERT INTO balance_history (account_id, balance, date) VALUES ('boa-positive', 5000, ?)`,
        args: [daysAgo(5)] },
      { sql: `INSERT INTO balance_history (account_id, balance, date) VALUES ('boa-positive', 634, ?)`,
        args: [daysAgo(0)] },
    ], 'write');

    const trends = await categoryTrends(daysAgo(5), daysAgo(0), 'day');
    const boa = trends.institutionSeries.find((series) => series.institution === 'Bank of America');
    expect(boa).toBeDefined();
    expect(boa!.points[0].amount).toBe('5000.00');
    expect(boa!.points.at(-1)?.amount).toBe('634.00');
  });
});
