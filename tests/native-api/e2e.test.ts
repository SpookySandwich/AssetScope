import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import { spawn, type ChildProcess } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// End-to-end path requiring no Plaid account (SPEC §27.6): a REAL sidecar
// process (no mocks) over a temporary data directory in Demo Mode —
// launch → browse Home → search accounts → add a manual asset → update its
// value → see the changed trend → export CSV → back up → restore → data
// survives a restart. This is also the "sidecar + temporary-database
// integration path that verifies the real data contract".

const REPO_ROOT = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const dataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'assetscope-e2e-'));

let child: ChildProcess | null = null;
let base = '';
let token = '';

function startSidecar(): Promise<void> {
  return new Promise((resolve, reject) => {
    child = spawn(process.execPath, ['--import', 'tsx/esm', 'native-api/src/main.ts',
      '--port', '0', '--mode', 'demo', '--data-dir', dataDir], {
      cwd: REPO_ROOT,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    const timer = setTimeout(() => reject(new Error('handshake timeout')), 30_000);
    let buffer = '';
    child.stdout!.on('data', (chunk: Buffer) => {
      buffer += chunk.toString();
      const line = buffer.split('\n')[0];
      if (line && line.includes('"event":"ready"')) {
        clearTimeout(timer);
        const handshake = JSON.parse(line) as { port: number; token: string };
        base = `http://127.0.0.1:${handshake.port}`;
        token = handshake.token;
        resolve();
      }
    });
    child.stderr!.on('data', () => {});
    child.on('exit', (code) => {
      if (base === '') { clearTimeout(timer); reject(new Error(`sidecar exited early (${code})`)); }
    });
  });
}

async function call<T>(method: string, apiPath: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { Authorization: `Bearer ${token}` };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const response = await fetch(`${base}${apiPath}`, {
    method, headers, body: body === undefined ? undefined : JSON.stringify(body),
  });
  const envelope = (await response.json()) as { data: T; error: { message: string } | null };
  if (envelope.error) throw new Error(envelope.error.message);
  return envelope.data;
}

// Local calendar date, matching the sidecar's future-date check; a UTC date
// runs a day ahead in the evening west of Greenwich.
const daysAgo = (n: number): string => {
  const d = new Date();
  d.setDate(d.getDate() - n);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

beforeAll(async () => {
  await startSidecar();
}, 60_000);

afterAll(async () => {
  child?.kill();
  // Windows may hold the db file briefly after the kill; cleanup must not
  // fail the suite.
  await new Promise((r) => setTimeout(r, 500));
  try { fs.rmSync(dataDir, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); }
  catch { /* temp dir; the OS cleans it eventually */ }
});

describe('E2E demo path against a real sidecar process (SPEC §27.6)', () => {
  let manualId = '';
  let baselineAssets = 0;

  it('1-2. launches and serves a seeded Home dashboard', async () => {
    const bootstrap = await call<{ app: { mode: string }; onboarding: { accountCount: number } }>('GET', '/v1/bootstrap');
    expect(bootstrap.app.mode).toBe('demo');
    expect(bootstrap.onboarding.accountCount).toBeGreaterThanOrEqual(7);

    const dashboard = await call<{ summary: { assets: { amount: string } }; history: unknown[]; allocation: { slices: unknown[] } }>(
      'GET', '/v1/dashboard?range=1y');
    baselineAssets = parseFloat(dashboard.summary.assets.amount);
    expect(baselineAssets).toBeGreaterThan(0);
    expect(dashboard.history.length).toBeGreaterThan(10);
    expect(dashboard.allocation.slices.length).toBeGreaterThanOrEqual(3);
  });

  it('3. searches accounts', async () => {
    const result = await call<{ accounts: { displayName: string }[] }>('GET', '/v1/accounts?search=checking');
    expect(result.accounts.some((a) => a.displayName.includes('Checking'))).toBe(true);
  });

  it('4. adds a manual asset', async () => {
    const created = await call<{ accountId: string }>('POST', '/v1/accounts/manual', {
      name: 'E2E Cabin', accountClass: 'asset', manualCategory: 'property',
      initialValue: { amount: '250000.00', currencyCode: 'USD', effectiveDate: daysAgo(30) },
      includeInNetWorth: true,
    });
    manualId = created.accountId;
    expect(manualId).toMatch(/^manual-/);
  });

  it('5. updates its value on a later date', async () => {
    await call('PATCH', `/v1/accounts/${manualId}/value`, { amount: '260000.00', effectiveDate: daysAgo(0) });
    const detail = await call<{ recentSnapshots: { amount: string }[] }>('GET', `/v1/accounts/${manualId}`);
    expect(detail.recentSnapshots[0].amount).toBe('260000.00');
  });

  it('6. the trend and allocation reflect the change', async () => {
    const dashboard = await call<{ summary: { assets: { amount: string } }; allocation: { slices: { bucket: string }[] } }>(
      'GET', '/v1/dashboard?range=1m');
    expect(parseFloat(dashboard.summary.assets.amount)).toBeCloseTo(baselineAssets + 260000, 0);
    expect(dashboard.allocation.slices.some((s) => s.bucket === 'property')).toBe(true);

    const trends = await call<{ assetBuckets: { bucket: string; points: { amount: string }[] }[] }>(
      'GET', '/v1/trends?range=1m');
    const property = trends.assetBuckets.find((b) => b.bucket === 'property');
    expect(property?.points.at(-1)?.amount).toBe('260000.00');
  });

  it('7. exports the net-worth CSV', async () => {
    const result = await call<{ path: string; rows: number }>('POST', '/v1/exports/net-worth.csv?range=1m');
    expect(result.rows).toBeGreaterThan(10);
    expect(fs.existsSync(result.path)).toBe(true);
    expect(fs.readFileSync(result.path, 'utf8')).toContain('date,assets,liabilities,net_worth');
  });

  it('8-9. backs up, restores, and the data survives the sidecar restart', async () => {
    const backup = await call<{ id: string }>('POST', '/v1/backups');

    // Mutate after the backup so the restore visibly rolls back.
    await call('PATCH', `/v1/accounts/${manualId}/value`, { amount: '999999.00', effectiveDate: daysAgo(0) });

    const restore = await call<{ restored: boolean; restartRequired: boolean }>(
      'POST', `/v1/backups/${backup.id}/restore`);
    expect(restore.restored).toBe(true);
    expect(restore.restartRequired).toBe(true);

    // The sidecar exits by contract (UC-011); restart it on the restored file.
    await new Promise<void>((resolve) => child!.once('exit', () => resolve()));
    await startSidecar();

    const detail = await call<{ recentSnapshots: { amount: string }[] }>('GET', `/v1/accounts/${manualId}`);
    expect(detail.recentSnapshots[0].amount).toBe('260000.00'); // pre-backup value restored
  }, 60_000);
});
