import fs from 'node:fs';
import path from 'node:path';
import { createClient } from '@libsql/client';
import { db } from '../core/database.js';
import { DATA_DIR, DATABASE_PATH } from '../core/paths.js';
import { ApiError, notFound, validation } from '../errors.js';
import {
  netWorthHistory, accountSummaries, loadAccounts, loadSnapshots,
  accountBalanceTimeline, isIncludedUsd, type Granularity,
} from './networth.js';
import { classify, allocationBucket, netWorthContributionCents } from './classify.js';
import { centsToAmount } from './money.js';
import { log } from '../log.js';

// Backups, restore, and CSV exports (SPEC §10.3, §21.4, UC-011).

const BACKUP_DIR = path.join(DATA_DIR, 'backups');
const EXPORT_DIR = path.join(DATA_DIR, 'exports');
const DB_PATH = DATABASE_PATH;

// Backup ids are bare filenames matching these shapes only — no path traversal.
const BACKUP_NAME_RE = /^(?:assetscope|fungible)\.[0-9T\-]+(\.manual|\.pre-restore)?\.bak$/;

export type BackupDto = { id: string; createdAt: string; sizeBytes: number };

export async function listBackups(): Promise<BackupDto[]> {
  if (!fs.existsSync(BACKUP_DIR)) return [];
  return fs.readdirSync(BACKUP_DIR)
    .filter((f) => BACKUP_NAME_RE.test(f))
    .map((f) => {
      const stat = fs.statSync(path.join(BACKUP_DIR, f));
      return { id: f, createdAt: stat.mtime.toISOString().replace(/\.\d{3}Z$/, 'Z'), sizeBytes: stat.size };
    })
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt));
}

/** "Back up now": always creates a fresh timestamped backup (unlike the daily auto-backup). */
export async function createBackup(): Promise<BackupDto> {
  fs.mkdirSync(BACKUP_DIR, { recursive: true });
  const stamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
  const name = `assetscope.${stamp}.manual.bak`;
  await db.execute({ sql: 'VACUUM INTO ?', args: [path.join(BACKUP_DIR, name)] });
  log('info', `backup created: ${name}`);
  const stat = fs.statSync(path.join(BACKUP_DIR, name));
  return { id: name, createdAt: stat.mtime.toISOString().replace(/\.\d{3}Z$/, 'Z'), sizeBytes: stat.size };
}

/**
 * Restore (UC-011): verify the backup's integrity, protectively back up the
 * current database, close the connection, copy the backup into place, and tell
 * the client to restart the sidecar. This process exits after responding.
 */
export async function restoreBackup(backupId: string): Promise<{ protectiveBackupId: string }> {
  if (!BACKUP_NAME_RE.test(backupId)) throw validation('Unknown backup id format.');
  const source = path.join(BACKUP_DIR, backupId);
  if (!fs.existsSync(source)) throw notFound('Backup');

  // Only verified SQLite databases may be restored (SPEC §23.5).
  const probe = createClient({ url: `file:${source}` });
  try {
    const check = await probe.execute('PRAGMA integrity_check');
    const verdict = String((check.rows[0] as unknown as Record<string, unknown>)[Object.keys(check.rows[0] as object)[0]]);
    if (verdict !== 'ok') throw new ApiError('DATA_CORRUPT', `Backup failed integrity check: ${verdict.slice(0, 100)}`);
  } catch (err) {
    if (err instanceof ApiError) throw err;
    throw new ApiError('DATA_CORRUPT', 'The selected backup is not a readable database.');
  } finally {
    probe.close();
  }

  const stamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
  const protectiveName = `assetscope.${stamp}.pre-restore.bak`;
  fs.mkdirSync(BACKUP_DIR, { recursive: true });
  await db.execute({ sql: 'VACUUM INTO ?', args: [path.join(BACKUP_DIR, protectiveName)] });

  db.close();
  fs.copyFileSync(source, DB_PATH);
  log('info', `restored backup ${backupId}; protective copy ${protectiveName}`);
  return { protectiveBackupId: protectiveName };
}

// ── CSV exports (SPEC §20.5, §21.4) ────────────────────────────────────────────

const csvEscape = (v: string | null): string =>
  v === null ? '' : /[",\n]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v;

export async function exportAccountsCsv(): Promise<{ path: string; rows: number }> {
  fs.mkdirSync(EXPORT_DIR, { recursive: true });
  const accounts = await accountSummaries({ includeExcluded: true, includeArchived: true });
  const lines = ['id,name,institution,category,class,balance,currency,as_of_date,excluded,archived'];
  for (const a of accounts) {
    lines.push([
      csvEscape(a.id), csvEscape(a.displayName), csvEscape(a.institutionName),
      csvEscape(a.category), csvEscape(a.accountClass),
      csvEscape(a.balance?.amount ?? null), csvEscape(a.currencyCode),
      csvEscape(a.balance?.asOfDate ?? null),
      a.excluded ? 'true' : 'false', a.archived ? 'true' : 'false',
    ].join(','));
  }
  const file = path.join(EXPORT_DIR, `accounts-${new Date().toISOString().slice(0, 10)}.csv`);
  fs.writeFileSync(file, lines.join('\n') + '\n', 'utf8');
  log('info', `exported ${accounts.length} accounts to csv`);
  return { path: file, rows: accounts.length };
}

export async function exportNetWorthCsv(
  from: string, to: string, granularity: Granularity,
): Promise<{ path: string; rows: number }> {
  fs.mkdirSync(EXPORT_DIR, { recursive: true });
  const points = await netWorthHistory(from, to, granularity);
  // Carry-forward semantics documented in the header (SPEC §20.5).
  const lines = [
    '# Daily-snapshot as-of series: each date uses every account\'s latest balance on or before that date. USD only.',
    'date,assets,liabilities,net_worth',
    ...points.map((p) => `${p.date},${p.assets.amount},${p.liabilities.amount},${p.netWorth.amount}`),
  ];
  const file = path.join(EXPORT_DIR, `net-worth-${from}-to-${to}.csv`);
  fs.writeFileSync(file, lines.join('\n') + '\n', 'utf8');
  log('info', `exported ${points.length} net-worth points to csv`);
  return { path: file, rows: points.length };
}

// ── Sync run detail (SPEC §10.3) ───────────────────────────────────────────────

export async function getSyncRun(runId: string): Promise<unknown> {
  const runRes = await db.execute({
    sql: `SELECT id, trigger, status, started_at, completed_at, item_count, success_count, failure_count, summary_json
          FROM sync_runs WHERE id = ?`,
    args: [runId],
  });
  if (runRes.rows.length === 0) throw notFound('Sync run');
  const itemsRes = await db.execute({
    sql: `SELECT item_id, institution_name, status, added_count, modified_count, removed_count, error_code
          FROM sync_item_runs WHERE run_id = ? ORDER BY institution_name`,
    args: [runId],
  });
  const run = runRes.rows[0] as unknown as Record<string, unknown>;
  return {
    id: run.id, trigger: run.trigger, status: run.status,
    startedAt: run.started_at, completedAt: run.completed_at,
    itemCount: Number(run.item_count), successCount: Number(run.success_count),
    failureCount: Number(run.failure_count),
    summary: run.summary_json ? JSON.parse(String(run.summary_json)) : null,
    items: (itemsRes.rows as unknown as Record<string, unknown>[]).map((r) => ({
      itemId: r.item_id, institutionName: r.institution_name, status: r.status,
      addedCount: r.added_count === null ? null : Number(r.added_count),
      modifiedCount: r.modified_count === null ? null : Number(r.modified_count),
      removedCount: r.removed_count === null ? null : Number(r.removed_count),
      errorCode: r.error_code,
    })),
  };
}

// ── Delete all local data (SPEC §21.5) ─────────────────────────────────────────

/**
 * Full local wipe: database, encryption key, exports, logs, runtime state, and
 * backups. The caller enforces the typed product-name confirmation; this
 * process exits afterwards so the app restarts into onboarding.
 */
export async function deleteAllData(): Promise<{ deleted: true }> {
  log('warn', 'audit: delete-all-local-data requested');
  db.close();
  for (const target of ['assetscope.db', 'fungible.db', 'key', 'encryption.key', 'exports', 'backups', 'runtime', '.env']) {
    try { fs.rmSync(path.join(DATA_DIR, target), { recursive: true, force: true }); }
    catch { /* best effort per entry */ }
  }
  return { deleted: true };
}

// ── Plaid .env import (SPEC §13.1) ─────────────────────────────────────────────

/**
 * Import upstream `<dataDir>/.env` credentials. The secret crosses the authed
 * loopback channel exactly once so the client can migrate it into Windows
 * Credential Manager (SPEC §13.1: after import the Secret should move there).
 */
export function readEnvCredentials(): { clientId: string; secret: string; environment: string } | null {
  const envPath = path.join(DATA_DIR, '.env');
  if (!fs.existsSync(envPath)) return null;
  const parsed: Record<string, string> = {};
  for (const line of fs.readFileSync(envPath, 'utf8').split('\n')) {
    const match = line.match(/^\s*([A-Z_]+)\s*=\s*"?([^"\r\n]*)"?\s*$/);
    if (match) parsed[match[1]] = match[2];
  }
  if (!parsed.PLAID_CLIENT_ID || !parsed.PLAID_SECRET) return null;
  return {
    clientId: parsed.PLAID_CLIENT_ID,
    secret: parsed.PLAID_SECRET,
    environment: parsed.PLAID_ENV ?? 'sandbox',
  };
}

// ── Per-category trends (SPEC §20.2/§20.3) ─────────────────────────────────────

export type CategorySeriesDto = {
  bucket: string;
  points: { date: string; amount: string }[];
};

export type InstitutionSeriesDto = {
  institution: string;
  points: { date: string; amount: string }[];
};

export async function categoryTrends(
  from: string, to: string, granularity: Granularity,
): Promise<{
  assetBuckets: CategorySeriesDto[];
  liabilities: { date: string; amount: string }[];
  institutionSeries: InstitutionSeriesDto[];
}> {
  const accounts = (await loadAccounts()).filter(isIncludedUsd);
  const snapshots = await loadSnapshots(accounts.map((a) => a.id));
  const timeline = await accountBalanceTimeline(from, to, granularity, accounts, snapshots);
  const dates = timeline.dates;
  if (dates.length === 0) return { assetBuckets: [], liabilities: [], institutionSeries: [] };
  const bucketTotals = new Map<string, number[]>();
  const institutionTotals = new Map<string, number[]>();
  const liabilityTotals: number[] = new Array(dates.length).fill(0);

  for (const account of accounts) {
    const { category, accountClass } = classify(account);
    const points = timeline.byAccount.get(account.id) ?? [];
    // Institution series represents signed net worth at each institution,
    // not movement since the range start. Assets remain positive and debts
    // negative, so transferring cash between institutions changes both
    // balances without falsely claiming that the source institution is debt.
    const institution = account.institution_name?.trim() || 'manual';
    let institutionValues = institutionTotals.get(institution);
    if (!institutionValues) {
      institutionValues = new Array(dates.length).fill(0);
      institutionTotals.set(institution, institutionValues);
    }
    for (let i = 0; i < dates.length; i++) {
      const point = points.find(candidate => candidate.date === dates[i]);
      if (!point) continue;
      institutionValues[i] += netWorthContributionCents(account, point.cents);
      if (accountClass === 'asset') {
        const bucket = allocationBucket(category);
        let totals = bucketTotals.get(bucket);
        if (!totals) { totals = new Array(dates.length).fill(0); bucketTotals.set(bucket, totals); }
        totals[i] += point.cents;
      } else {
        liabilityTotals[i] += account.source === 'manual' ? Math.abs(point.cents) : point.cents;
      }
    }
  }

  const order = ['bankCash', 'investment', 'retirement', 'property', 'vehicle', 'other'];
  const institutionSeries = [...institutionTotals.entries()]
    .map(([institution, totals]) => ({
      institution,
      points: dates.map((date, i) => ({ date, amount: centsToAmount(totals[i]) })),
    }))
    .sort((a, b) => Math.abs(Number(b.points.at(-1)?.amount ?? 0))
      - Math.abs(Number(a.points.at(-1)?.amount ?? 0)));
  return {
    assetBuckets: order
      .filter((b) => bucketTotals.has(b))
      .map((b) => ({
        bucket: b,
        points: dates.map((d, i) => ({ date: d, amount: centsToAmount(bucketTotals.get(b)![i]) })),
      })),
    liabilities: dates.map((d, i) => ({ date: d, amount: centsToAmount(liabilityTotals[i]) })),
    institutionSeries,
  };
}
