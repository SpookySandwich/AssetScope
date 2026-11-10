// Headless CLI entry point (MERC-3): thin wrappers over the existing
// services/* functions, modeled on sync-host.ts. DATA_DIR is captured at
// module import by core/paths.ts, so every service import below is dynamic
// and happens only after ASSETSCOPE_DATA_DIR is set by bootstrap() — a static
// import at the top of this file would silently read the wrong database.
import path from 'node:path';
import os from 'node:os';
import { pathToFileURL } from 'node:url';
import { ApiError, validation, notFound, type ErrorCode } from './errors.js';
import type { AccountRecord } from './services/networth.js';

type FlagValue = string | boolean;
type Flags = Map<string, FlagValue>;

const BOOLEAN_FLAGS = new Set([
  'demo', 'json', 'table', 'redact', 'include-archived', 'no-excluded', 'by-category', 'by-month',
  'help',
]);

function parseArgv(argv: string[]): { command: string | undefined; positionals: string[]; flags: Flags } {
  const flags: Flags = new Map();
  const positionals: string[] = [];
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '-h') { flags.set('help', true); continue; }
    if (arg.startsWith('--')) {
      const name = arg.slice(2);
      if (BOOLEAN_FLAGS.has(name)) { flags.set(name, true); continue; }
      const next = argv[i + 1];
      if (next !== undefined && !next.startsWith('--')) { flags.set(name, next); i++; }
      else flags.set(name, true);
    } else {
      positionals.push(arg);
    }
  }
  const [command, ...rest] = positionals;
  return { command, positionals: rest, flags };
}

const flagString = (flags: Flags, name: string): string | undefined => {
  const v = flags.get(name);
  return typeof v === 'string' ? v : undefined;
};
const flagBool = (flags: Flags, name: string): boolean => flags.get(name) === true;

// ── Data-dir resolution + bootstrap (mirrors main.ts) ──────────────────────

function resolveDataDir(flags: Flags): { dataDir: string; mode: 'real' | 'demo' } {
  const demo = flagBool(flags, 'demo');
  const explicit = flagString(flags, 'data-dir') ?? null;
  const dataDir = explicit
    ?? process.env.ASSETSCOPE_DATA_DIR
    ?? process.env.FUNGIBLE_DATA_DIR
    ?? (demo
      ? path.join(process.env.LOCALAPPDATA ?? os.homedir(), 'AssetScope', 'Demo')
      : path.join(os.homedir(), '.assetscope'));
  return { dataDir, mode: demo ? 'demo' : 'real' };
}

async function bootstrap(flags: Flags): Promise<{ dataDir: string; mode: 'real' | 'demo' }> {
  const { dataDir, mode } = resolveDataDir(flags);
  process.env.ASSETSCOPE_DATA_DIR = dataDir;
  if (mode !== 'demo') {
    const { config } = await import('dotenv');
    config({ path: path.join(dataDir, '.env'), quiet: true });
  }
  return { dataDir, mode };
}

async function openDb(mode: 'real' | 'demo'): Promise<void> {
  const fs = await import('node:fs');
  const { initDb } = await import('./core/database.js');
  const { DATABASE_PATH } = await import('./core/paths.js');
  // Schema writes contend with a running desktop sidecar. An existing
  // database is usually already migrated by the app; only run the migration
  // when the stored version lags behind this build, so a read-only CLI
  // invocation stays write-free and can share the file.
  const exists = fs.existsSync(DATABASE_PATH);
  const needsSchema = mode === 'demo' || !exists;
  if (needsSchema) {
    const { ensureNativeSchema } = await import('./services/schema.js');
    await initDb();
    await ensureNativeSchema();
  } else {
    const { nativeSchemaVersion, ensureNativeSchema, NATIVE_SCHEMA_VERSION } =
      await import('./services/schema.js');
    if (await nativeSchemaVersion() < NATIVE_SCHEMA_VERSION) {
      await initDb();
      await ensureNativeSchema();
    }
  }
  if (mode === 'demo') {
    const { seedDemoData } = await import('./services/demo.js');
    await seedDemoData();
  }
}

// ── Read commands ────────────────────────────────────────────────────────

async function cmdSummary() {
  const { currentSummary, allocation } = await import('./services/networth.js');
  const [summary, alloc] = await Promise.all([currentSummary(), allocation()]);
  return { summary, allocation: alloc };
}

async function cmdAccounts(flags: Flags) {
  const { accountSummaries } = await import('./services/networth.js');
  const includeExcluded = !flagBool(flags, 'no-excluded');
  const includeArchived = flagBool(flags, 'include-archived');
  let list = await accountSummaries({ includeExcluded, includeArchived });
  const category = flagString(flags, 'category');
  if (category) list = list.filter((a) => a.category === category);
  const search = flagString(flags, 'search')?.toLowerCase();
  if (search) {
    list = list.filter((a) =>
      [a.displayName, a.institutionName, a.nickname, a.owner, a.mask]
        .some((f) => f !== null && f.toLowerCase().includes(search)));
  }
  return { accounts: list };
}

async function cmdAccount(id: string | undefined) {
  if (!id) throw validation('account id is required: assetscope account <id>');
  const { accountSummaries } = await import('./services/networth.js');
  const { recentSnapshots } = await import('./services/accounts.js');
  const list = await accountSummaries({ includeExcluded: true, includeArchived: true });
  const account = list.find((a) => a.id === id);
  if (!account) throw notFound('Account');
  return { account, recentSnapshots: await recentSnapshots(id, 10) };
}

const VALID_RANGES = new Set(['1m', '3m', '1y', 'all']);
const VALID_GRANULARITIES = new Set(['day', 'week', 'month']);
const VALID_INVESTMENT_RANGES = new Set(['1d', '1w', '1m', '3m', '1y', 'all']);

async function cmdNetWorth(flags: Flags) {
  const { rangeToWindow, netWorthHistory } = await import('./services/networth.js');
  const today = new Date().toISOString().slice(0, 10);
  const rangeParam = flagString(flags, 'range');
  let from: string, to: string, granularity: 'day' | 'week' | 'month';
  if (rangeParam) {
    if (!VALID_RANGES.has(rangeParam)) throw validation('range must be 1m|3m|1y|all.');
    const win = rangeToWindow(rangeParam as '1m' | '3m' | '1y' | 'all', today, null);
    from = win.from; to = win.to; granularity = win.granularity;
  } else {
    from = flagString(flags, 'from') ?? `${today.slice(0, 4)}-01-01`;
    to = flagString(flags, 'to') ?? today;
    granularity = (flagString(flags, 'granularity') ?? 'day') as 'day' | 'week' | 'month';
    if (!/^\d{4}-\d{2}-\d{2}$/.test(from) || !/^\d{4}-\d{2}-\d{2}$/.test(to)) {
      throw validation('from/to must be YYYY-MM-DD.');
    }
    if (!VALID_GRANULARITIES.has(granularity)) throw validation('granularity must be day|week|month.');
  }
  return { points: await netWorthHistory(from, to, granularity) };
}

async function cmdAllocation() {
  const { allocation } = await import('./services/networth.js');
  return allocation();
}

async function cmdTrends(flags: Flags) {
  const { rangeToWindow } = await import('./services/networth.js');
  const { categoryTrends } = await import('./services/maintenance.js');
  const range = flagString(flags, 'range') ?? '1y';
  if (!VALID_RANGES.has(range)) throw validation('range must be 1m|3m|1y|all.');
  const today = new Date().toISOString().slice(0, 10);
  const win = rangeToWindow(range as '1m' | '3m' | '1y' | 'all', today, null);
  return categoryTrends(win.from, win.to, win.granularity);
}

async function cmdInvestments(flags: Flags) {
  const { investmentOverview } = await import('./services/investments.js');
  const range = flagString(flags, 'range') ?? '1y';
  if (!VALID_INVESTMENT_RANGES.has(range)) throw validation('range must be 1d|1w|1m|3m|1y|all.');
  return investmentOverview(range as '1d' | '1w' | '1m' | '3m' | '1y' | 'all');
}

async function cmdPositions(flags: Flags) {
  const { investmentPositions } = await import('./services/investments.js');
  return { positions: await investmentPositions(flagString(flags, 'account')) };
}

async function cmdActivities(flags: Flags) {
  const { investmentActivities } = await import('./services/investments.js');
  const limit = Number(flagString(flags, 'limit') ?? '100') || 100;
  return { activities: await investmentActivities(limit, flagString(flags, 'account')) };
}

async function cmdOrders(flags: Flags) {
  const { investmentOrders } = await import('./services/investments.js');
  const limit = Number(flagString(flags, 'limit') ?? '100') || 100;
  return { orders: await investmentOrders(limit, flagString(flags, 'account')) };
}

async function cmdRealized(flags: Flags) {
  const { investmentRealizedPnl } = await import('./services/investments.js');
  const limit = Number(flagString(flags, 'limit') ?? '200') || 200;
  return investmentRealizedPnl(limit, flagString(flags, 'account'));
}

async function cmdInsights(flags: Flags) {
  const { generateInsights } = await import('./services/insights.js');
  const range = flagString(flags, 'range') ?? '1y';
  if (!VALID_RANGES.has(range)) throw validation('range must be 1m|3m|1y|all.');
  const limit = Number(flagString(flags, 'limit') ?? '3') || 3;
  return generateInsights(range as '1m' | '3m' | '1y' | 'all', limit);
}

async function cmdTransactions(flags: Flags) {
  const { listTransactions } = await import('./services/transactions.js');
  const from = flagString(flags, 'from');
  const to = flagString(flags, 'to');
  if (from !== undefined && !/^\d{4}-\d{2}-\d{2}$/.test(from)) throw validation('from must be YYYY-MM-DD.');
  if (to !== undefined && !/^\d{4}-\d{2}-\d{2}$/.test(to)) throw validation('to must be YYYY-MM-DD.');
  const limitRaw = flagString(flags, 'limit');
  const limit = limitRaw === undefined ? undefined : Number(limitRaw);
  if (limitRaw !== undefined && (!Number.isFinite(limit) || (limit as number) <= 0)) {
    throw validation('limit must be a positive number.');
  }

  const result = await listTransactions({
    from, to, accountId: flagString(flags, 'account'), category: flagString(flags, 'category'), limit,
  });
  if (!flagBool(flags, 'redact')) return result;

  const { loadAccounts } = await import('./services/networth.js');
  const accounts = await loadAccounts({ includeExcluded: true, includeArchived: true });
  const map = buildTransactionRedactionMap(result.transactions, await buildRedactionMap(accounts));
  return { transactions: redactValue(result.transactions, map) };
}

async function cmdLiving(flags: Flags) {
  const { livingOverview } = await import('./services/living.js');
  const mealsRaw = flagString(flags, 'dining-meals-per-week');
  const diningMealsPerWeek = mealsRaw === undefined ? undefined : Number(mealsRaw);
  if (mealsRaw !== undefined && (!Number.isInteger(diningMealsPerWeek) || (diningMealsPerWeek as number) < 0 || (diningMealsPerWeek as number) > 14)) {
    throw validation('dining-meals-per-week must be an integer from 0 to 14.');
  }
  const result = await livingOverview({ diningMealsPerWeek });
  if (flagBool(flags, 'redact')) {
    const { loadAccounts } = await import('./services/networth.js');
    const accounts = await loadAccounts({ includeExcluded: true, includeArchived: true });
    const map = buildTransactionRedactionMap(
      result.transactions,
      await buildRedactionMap(accounts),
    );
    let placeCount = 0;
    const withPlaces = new Map(map);
    for (const t of result.transactions) {
      for (const raw of [t.tripOrigin, t.tripDestination]) {
        if (raw && raw.trim() && !withPlaces.has(raw)) {
          placeCount++;
          withPlaces.set(raw, `place-${placeCount}`);
        }
      }
    }
    for (const route of result.routes) {
      for (const raw of [route.origin, route.destination, route.alternativeLabel]) {
        if (raw && raw.trim() && !withPlaces.has(raw)) {
          placeCount++;
          withPlaces.set(raw, `place-${placeCount}`);
        }
      }
    }
    return redactValue(result, withPlaces);
  }
  return result;
}

async function cmdRewards(flags: Flags) {
  const { rewardsOverview } = await import('./services/rewards.js');
  const range = flagString(flags, 'range') ?? '3m';
  if (range !== '1m' && range !== '3m' && range !== '1y') throw validation('range must be 1m|3m|1y.');
  return rewardsOverview({ range });
}

async function cmdSpending(flags: Flags) {
  const { rangeToWindow } = await import('./services/networth.js');
  const { spendingByCategory, monthlyCashFlow } = await import('./services/transactions.js');
  const range = flagString(flags, 'range') ?? '1y';
  if (!VALID_RANGES.has(range)) throw validation('range must be 1m|3m|1y|all.');
  const today = new Date().toISOString().slice(0, 10);
  const win = rangeToWindow(range as '1m' | '3m' | '1y' | 'all', today, null);

  const byCategory = flagBool(flags, 'by-category');
  const byMonth = flagBool(flags, 'by-month');
  const includeBoth = !byCategory && !byMonth;
  const result: { byCategory?: Awaited<ReturnType<typeof spendingByCategory>>; byMonth?: Awaited<ReturnType<typeof monthlyCashFlow>> } = {};
  if (includeBoth || byCategory) result.byCategory = await spendingByCategory(win.from, win.to);
  if (includeBoth || byMonth) result.byMonth = await monthlyCashFlow(win.from, win.to);
  return result;
}

async function cmdSyncStatus() {
  const { getCurrentJob, listRuns } = await import('./services/syncjobs.js');
  const { latestInvestmentSyncRun } = await import('./services/snaptrade-sync.js');
  const [runs, investmentSync] = await Promise.all([listRuns(20), latestInvestmentSyncRun()]);
  return { job: getCurrentJob(), runs, investmentSync };
}

// ── Export / backup (the only other writers, both delegate entirely) ───────

async function cmdExport(target: string | undefined, flags: Flags) {
  if (target === 'accounts') {
    const { exportAccountsCsv } = await import('./services/maintenance.js');
    return exportAccountsCsv();
  }
  if (target === 'net-worth') {
    const { rangeToWindow } = await import('./services/networth.js');
    const { exportNetWorthCsv } = await import('./services/maintenance.js');
    const range = flagString(flags, 'range') ?? 'all';
    if (!VALID_RANGES.has(range)) throw validation('range must be 1m|3m|1y|all.');
    const today = new Date().toISOString().slice(0, 10);
    const win = rangeToWindow(range as '1m' | '3m' | '1y' | 'all', today, null);
    return exportNetWorthCsv(win.from, win.to, win.granularity);
  }
  throw validation('export target must be accounts|net-worth.');
}

async function cmdBackup(sub: string | undefined) {
  const { createBackup, listBackups } = await import('./services/maintenance.js');
  if (sub === undefined) return createBackup();
  if (sub === 'list') return { backups: await listBackups() };
  throw validation('backup subcommand must be omitted or "list".');
}

// ── Sync: delegates entirely to sync-host.ts, no second write path ─────────

async function cmdSync() {
  const { isLockHeldByOther } = await import('./services/lock.js');
  const { isPlaidConfigured } = await import('./core/plaid.js');
  const { isSnapTradeConfigured } = await import('./services/snaptrade-config.js');
  const { runScheduledSync } = await import('./sync-host.js');

  // Read-only checks before delegating, purely to describe the outcome —
  // runScheduledSync() itself is the single place that decides and acts.
  const wasLockedByOther = isLockHeldByOther();
  const providerConfigured = isPlaidConfigured() || isSnapTradeConfigured();
  const exitCode = await runScheduledSync();

  if (wasLockedByOther) {
    return { status: 'queued' as const, message: 'AssetScope is currently running; sync deferred to its next check.' };
  }
  if (!providerConfigured) {
    return {
      status: 'queued' as const,
      message: 'No Plaid or SnapTrade credentials are configured; sync queued for the next app launch.',
    };
  }
  if (exitCode === 0) return { status: 'succeeded' as const, message: 'Sync completed.' };
  if (exitCode === 1) return { status: 'partial' as const, message: 'Sync completed with failures; see sync-status for details.' };
  return { status: 'failed' as const, message: `Sync did not run (exit ${exitCode}).` };
}

// ── Doctor ───────────────────────────────────────────────────────────────

async function cmdDoctor(mode: 'real' | 'demo') {
  const paths = await import('./core/paths.js');
  const { db } = await import('./core/database.js');
  const { NATIVE_SCHEMA_VERSION } = await import('./services/schema.js');
  const { isLockHeldByOther } = await import('./services/lock.js');
  const { isPlaidConfigured } = await import('./core/plaid.js');
  const { isSnapTradeConfigured } = await import('./services/snaptrade-config.js');

  const [accountsRes, snapshotsRes] = await Promise.all([
    db.execute('SELECT COUNT(*) AS c FROM accounts'),
    db.execute('SELECT COUNT(*) AS c FROM balance_history'),
  ]);

  return {
    mode,
    dataDir: paths.DATA_DIR,
    databasePath: paths.DATABASE_PATH,
    nativeSchemaVersion: NATIVE_SCHEMA_VERSION,
    sidecarLockHeldByAnotherProcess: isLockHeldByOther(),
    plaidConfigured: isPlaidConfigured(),
    snapTradeConfigured: isSnapTradeConfigured(),
    accountCount: Number((accountsRes.rows[0] as unknown as { c: number }).c),
    snapshotCount: Number((snapshotsRes.rows[0] as unknown as { c: number }).c),
  };
}

// ── Snapshot + redaction ────────────────────────────────────────────────────

// Stable pseudonyms per real-world identity string ("First National Bank" ->
// "bank-1"), shared across every place that identity appears in the snapshot,
// so relationships between accounts (same institution, same owner) survive
// redaction even though the identity itself does not.
function categoryPrefix(category: string): string {
  switch (category) {
    case 'bank': case 'cash': return 'bank';
    case 'investment': return 'brokerage';
    case 'retirement': return 'retirement';
    case 'credit': return 'credit';
    case 'loan': return 'loan';
    case 'property': return 'property';
    case 'vehicle': return 'vehicle';
    default: return 'account';
  }
}

async function buildRedactionMap(accounts: AccountRecord[]): Promise<Map<string, string>> {
  const { classify } = await import('./services/classify.js');
  const { accountIdentities } = await import('./services/rewards.js');
  const identities = await accountIdentities(accounts);
  const map = new Map<string, string>();
  let productCount = 0;
  const institutionCounters = new Map<string, number>();
  const accountCounters = new Map<string, number>();
  let ownerCount = 0;

  for (const a of [...accounts].sort((x, y) => x.id.localeCompare(y.id))) {
    const { category } = classify(a);
    const institution = a.institution_name?.trim() || null;
    // Institution-less (manual) accounts each get their own synthetic bucket
    // key so they don't collide with one another, without ever being looked
    // up against real snapshot data (the key itself never appears verbatim).
    const bucketKey = institution ?? `manual:${a.id}`;
    let instPseudo = map.get(bucketKey);
    if (!instPseudo) {
      // A real institution (bank, brokerage) commonly holds accounts across
      // several categories at once (checking + credit + a loan, say) — a
      // category-flavored prefix chosen from just the first account seen at
      // that institution would misrepresent the others. Manual accounts have
      // no institution and are always exactly one category, so their prefix
      // stays category-flavored and accurate.
      const prefix = institution ? 'institution' : categoryPrefix(category);
      const n = (institutionCounters.get(prefix) ?? 0) + 1;
      institutionCounters.set(prefix, n);
      instPseudo = `${prefix}-${n}`;
      map.set(bucketKey, instPseudo);
    }
    const accountN = (accountCounters.get(instPseudo) ?? 0) + 1;
    accountCounters.set(instPseudo, accountN);
    const accountPseudo = institution ? `${instPseudo} account ${accountN}` : instPseudo;

    // A card can be shown under its product name, which names the issuer too.
    const identity = identities.get(a.id);
    for (const raw of [a.name, a.nickname, identity?.displayName, identity?.cardProduct?.name]) {
      if (raw && raw.trim() && !map.has(raw)) map.set(raw, accountPseudo);
    }
    const productId = identity?.cardProduct?.id;
    if (productId && !map.has(productId)) {
      productCount++;
      map.set(productId, `card-product-${productCount}`);
    }
    if (a.mask && !map.has(a.mask)) map.set(a.mask, String(1000 + map.size));
    if (a.owner && a.owner.trim() && !map.has(a.owner)) {
      ownerCount++;
      map.set(a.owner, `owner-${ownerCount}`);
    }
  }
  return map;
}

// A transaction's `name`/`merchantName` leaks more than an account label —
// redact must cover them too, not just institution/account identity. Stable
// per-string pseudonyms so the same merchant maps to the same `merchant-N`
// across every transaction, mirroring how buildRedactionMap keeps accounts
// at the same institution consistent.
function buildTransactionRedactionMap(
  transactions: { name: string; merchantName: string | null }[],
  base: Map<string, string>,
): Map<string, string> {
  const map = new Map(base);
  let merchantCount = 0;
  for (const t of transactions) {
    for (const raw of [t.merchantName, t.name]) {
      if (raw && raw.trim() && !map.has(raw)) {
        merchantCount++;
        map.set(raw, `merchant-${merchantCount}`);
      }
    }
  }
  return map;
}

function redactValue(value: unknown, map: Map<string, string>): unknown {
  if (typeof value === 'string') return map.get(value) ?? value;
  if (Array.isArray(value)) return value.map((v) => redactValue(v, map));
  if (value && typeof value === 'object') {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value)) out[k] = redactValue(v, map);
    return out;
  }
  return value;
}

async function buildSnapshot(redact: boolean) {
  const {
    currentSummary, allocation, accountSummaries, loadAccounts, netWorthHistory, rangeToWindow,
  } = await import('./services/networth.js');
  const { categoryTrends } = await import('./services/maintenance.js');
  const { investmentOverview, investmentPositions, investmentRealizedPnl } = await import('./services/investments.js');
  const { generateInsights } = await import('./services/insights.js');
  const { spendingByCategory, monthlyCashFlow } = await import('./services/transactions.js');
  const { livingOverview } = await import('./services/living.js');
  const { NATIVE_SCHEMA_VERSION } = await import('./services/schema.js');
  const { latestInvestmentSyncRun } = await import('./services/snaptrade-sync.js');
  const { SIDECAR_VERSION, PROTOCOL_VERSION } = await import('./envelope.js');
  const { db } = await import('./core/database.js');

  const today = new Date().toISOString().slice(0, 10);
  const win = rangeToWindow('all', today, null);
  const win3 = rangeToWindow('3m', today, null);
  const win12 = rangeToWindow('1y', today, null);

  const [
    summary, alloc, accounts, netWorth, trends, overview, positions, realized, insights,
    lastSyncRes, investmentSync, spendingLast3Months, spendingLast12Months, cashFlowLast12Months, living,
  ] = await Promise.all([
      currentSummary(),
      allocation(),
      accountSummaries({ includeExcluded: true, includeArchived: false }),
      netWorthHistory(win.from, win.to, win.granularity),
      categoryTrends(win.from, win.to, win.granularity),
      investmentOverview('all'),
      investmentPositions(),
      investmentRealizedPnl(),
      generateInsights('all', 10),
      db.execute('SELECT MAX(last_success_at) AS t FROM plaid_items'),
      latestInvestmentSyncRun(),
      spendingByCategory(win3.from, win3.to),
      spendingByCategory(win12.from, win12.to),
      monthlyCashFlow(win12.from, win12.to),
      livingOverview(),
    ]);

  const included = accounts.filter((a) => !a.excluded);
  const dataQuality = {
    excludedAccountCount: accounts.filter((a) => a.excluded).length,
    nonUsdAccountCount: accounts.filter((a) => a.currencyCode !== 'USD').length,
    missingBalanceCount: included.filter((a) => a.balance === null).length,
    staleAccountCount: included.filter((a) => a.freshness === 'stale' || a.freshness === 'very_stale').length,
    attentionItemCount: included.filter((a) => a.freshness === 'attention').length,
  };

  const snapshot = {
    schemaVersion: 1,
    generatedAt: new Date().toISOString(),
    provenance: {
      sidecarVersion: SIDECAR_VERSION,
      protocolVersion: PROTOCOL_VERSION,
      nativeSchemaVersion: NATIVE_SCHEMA_VERSION,
      lastPlaidSyncAt: (lastSyncRes.rows[0] as unknown as { t: string | null }).t,
      lastInvestmentSyncAt: investmentSync?.status === 'succeeded' ? investmentSync.completedAt : null,
      redaction: (redact ? 'redacted' : 'none') as 'redacted' | 'none',
    },
    summary,
    allocation: alloc,
    accounts: accounts.map(({ sparkline: _sparkline, ...rest }) => rest),
    netWorth,
    trends,
    investments: { overview, positions, realized: { summary: realized.summary } },
    insights,
    dataQuality,
    spending: {
      last3Months: spendingLast3Months,
      last12Months: spendingLast12Months,
      monthlyCashFlow: cashFlowLast12Months,
    },
    living,
  };

  if (!redact) return snapshot;
  const rawAccounts = await loadAccounts({ includeExcluded: true, includeArchived: false });
  const map = buildTransactionRedactionMap(
    living.transactions,
    await buildRedactionMap(rawAccounts),
  );
  return redactValue(snapshot, map) as typeof snapshot;
}

async function cmdSnapshot(flags: Flags) {
  const snapshot = await buildSnapshot(flagBool(flags, 'redact'));
  const outPath = flagString(flags, 'out');
  if (outPath) {
    const fs = await import('node:fs');
    fs.writeFileSync(outPath, JSON.stringify(snapshot, null, 2) + '\n', 'utf8');
    return {
      written: path.resolve(outPath),
      schemaVersion: snapshot.schemaVersion,
      generatedAt: snapshot.generatedAt,
      redaction: snapshot.provenance.redaction,
    };
  }
  return snapshot;
}

// ── Output ──────────────────────────────────────────────────────────────

function tableFromRows(rows: Record<string, unknown>[]): string {
  const columns = [...new Set(rows.flatMap((r) => Object.keys(r)))]
    .filter((col) => rows.every((r) => r[col] === null || typeof r[col] !== 'object'));
  const cell = (v: unknown): string => (v === null || v === undefined ? '' : String(v));
  const widths = columns.map((col) => Math.max(col.length, ...rows.map((r) => cell(r[col]).length)));
  const line = (cells: string[]) => cells.map((c, i) => c.padEnd(widths[i])).join('  ');
  return [line(columns), line(widths.map((w) => '-'.repeat(w))), ...rows.map((r) => line(columns.map((c) => cell(r[c]))))].join('\n');
}

function renderTable(result: unknown): string | null {
  if (Array.isArray(result) && result.length > 0 && result.every((r) => r && typeof r === 'object')) {
    return tableFromRows(result as Record<string, unknown>[]);
  }
  if (result && typeof result === 'object') {
    for (const value of Object.values(result as Record<string, unknown>)) {
      if (Array.isArray(value) && value.length > 0 && value.every((r) => r && typeof r === 'object' && !Array.isArray(r))) {
        return tableFromRows(value as Record<string, unknown>[]);
      }
    }
  }
  return null;
}

function printResult(result: unknown, table: boolean): void {
  if (table) {
    const rendered = renderTable(result);
    if (rendered !== null) { process.stdout.write(rendered + '\n'); return; }
  }
  process.stdout.write(JSON.stringify(result, null, 2) + '\n');
}

// ── Dispatcher ──────────────────────────────────────────────────────────

const EXIT_CODES: Record<ErrorCode, number> = {
  VALIDATION_ERROR: 2,
  NOT_CONFIGURED: 3,
  NOT_FOUND: 4,
  AUTH_REQUIRED: 1,
  ITEM_LOGIN_REQUIRED: 1,
  NETWORK_UNAVAILABLE: 1,
  RATE_LIMITED: 1,
  SYNC_IN_PROGRESS: 1,
  DATA_CORRUPT: 1,
  CONFLICT: 1,
  UNAUTHORIZED: 1,
  INTERNAL_ERROR: 1,
};

const COMMANDS = new Set([
  'summary', 'accounts', 'account', 'net-worth', 'allocation', 'trends', 'investments',
  'positions', 'activities', 'orders', 'realized', 'insights', 'transactions', 'spending', 'living',
  'rewards',
  'sync-status', 'export', 'backup', 'sync', 'doctor', 'snapshot',
]);

// Every other command would print real names despite the flag, so it is refused there.
const REDACTABLE_COMMANDS = new Set(['transactions', 'living', 'snapshot']);

type HelpFlag = { name: string; value?: string; summary: string };
type HelpCommand = {
  name: string;
  summary: string;
  args?: string[];
  flags?: HelpFlag[];
  writes: boolean;
};

const GLOBAL_FLAGS: HelpFlag[] = [
  { name: 'data-dir', value: 'PATH', summary: 'Database directory. Defaults to %USERPROFILE%\\.assetscope (or ASSETSCOPE_DATA_DIR).' },
  { name: 'demo', summary: 'Use the synthetic demo dataset instead of real data.' },
  { name: 'table', summary: 'Render the first list in the result as a text table. JSON remains the default.' },
  { name: 'json', summary: 'JSON on stdout (already the default). Kept so callers can be explicit.' },
  { name: 'help', summary: 'Print this catalog, or the spec for one command. Alias: -h.' },
];

const HELP_COMMANDS: HelpCommand[] = [
  { name: 'summary', summary: 'Current assets, liabilities, net worth, and change versus the previous snapshot.', writes: false },
  {
    name: 'accounts',
    summary: 'List accounts with balances, category, freshness, and institution. Credit cards carry cardProduct, the card they are (chosen in the app or detected).',
    flags: [
      { name: 'category', value: 'NAME', summary: 'bank|investment|retirement|credit|loan|cash|property|vehicle|other' },
      { name: 'search', value: 'TEXT', summary: 'Case-insensitive match on name, institution, nickname, owner, or mask.' },
      { name: 'include-archived', summary: 'Include archived accounts.' },
      { name: 'no-excluded', summary: 'Hide accounts marked excluded from net worth.' },
    ],
    writes: false,
  },
  { name: 'account', summary: 'One account plus its 10 most recent balance snapshots.', args: ['<id>'], writes: false },
  {
    name: 'net-worth',
    summary: 'As-of carry-forward net-worth series.',
    flags: [
      { name: 'range', value: '1m|3m|1y|all', summary: 'Named window. Overrides --from/--to/--granularity.' },
      { name: 'from', value: 'YYYY-MM-DD', summary: 'Inclusive start. Default: January 1 of this year.' },
      { name: 'to', value: 'YYYY-MM-DD', summary: 'Inclusive end. Default: today.' },
      { name: 'granularity', value: 'day|week|month', summary: 'Used only with --from/--to. Default: day.' },
    ],
    writes: false,
  },
  { name: 'allocation', summary: 'Asset-only allocation slices (bankCash, investment, retirement, property, vehicle, other).', writes: false },
  {
    name: 'trends',
    summary: 'Category balance trends over a named range.',
    flags: [{ name: 'range', value: '1m|3m|1y|all', summary: 'Default: 1y.' }],
    writes: false,
  },
  {
    name: 'investments',
    summary: 'Brokerage overview: accounts, allocation, and period performance.',
    flags: [{ name: 'range', value: '1d|1w|1m|3m|1y|all', summary: 'Default: 1y.' }],
    writes: false,
  },
  {
    name: 'positions',
    summary: 'Open holdings with market value and cost-basis gain/loss.',
    flags: [{ name: 'account', value: 'ID', summary: 'Restrict to one investment account.' }],
    writes: false,
  },
  {
    name: 'activities',
    summary: 'Cached brokerage activity (buys, sells, cash, card).',
    flags: [
      { name: 'account', value: 'ID', summary: 'Restrict to one investment account.' },
      { name: 'limit', value: 'N', summary: 'Default 100, max 1000.' },
    ],
    writes: false,
  },
  {
    name: 'orders',
    summary: 'Cached brokerage orders. Historical only — the app cannot place orders.',
    flags: [
      { name: 'account', value: 'ID', summary: 'Restrict to one investment account.' },
      { name: 'limit', value: 'N', summary: 'Default 100, max 1000.' },
    ],
    writes: false,
  },
  {
    name: 'realized',
    summary: 'FIFO realized P/L from cached BUY/SELL activity. Rows with basisComplete=false are incomplete.',
    flags: [
      { name: 'account', value: 'ID', summary: 'Restrict to one investment account.' },
      { name: 'limit', value: 'N', summary: 'Default 200, max 1000.' },
    ],
    writes: false,
  },
  {
    name: 'insights',
    summary: 'Rule-based observations (currently spending-increase). Empty when nothing qualifies.',
    flags: [
      { name: 'range', value: '1m|3m|1y|all', summary: 'Default: 1y.' },
      { name: 'limit', value: 'N', summary: 'Default 3, max 10.' },
    ],
    writes: false,
  },
  {
    name: 'transactions',
    summary: 'Posted, non-ignored bank/card transactions. Newest first. Pending rows are omitted.',
    flags: [
      { name: 'from', value: 'YYYY-MM-DD', summary: 'Inclusive start date.' },
      { name: 'to', value: 'YYYY-MM-DD', summary: 'Inclusive end date.' },
      { name: 'account', value: 'ID', summary: 'Restrict to one account.' },
      { name: 'category', value: 'NAME', summary: 'Exact category, e.g. FOOD_AND_DRINK or INCOME.' },
      { name: 'limit', value: 'N', summary: 'Default 100, max 1000.' },
      { name: 'redact', summary: 'Replace merchant and transaction names with stable merchant-N pseudonyms.' },
    ],
    writes: false,
  },
  {
    name: 'spending',
    summary: 'Spending by category and/or monthly cash flow. Category spend excludes transfers, income, and loan payments; cash flow includes all movement.',
    flags: [
      { name: 'range', value: '1m|3m|1y|all', summary: 'Default: 1y.' },
      { name: 'by-category', summary: 'Only category totals.' },
      { name: 'by-month', summary: 'Only monthly cash flow.' },
    ],
    writes: false,
  },
  {
    name: 'living',
    summary: 'This month leftover, fixed vs flexible split, meal costs, repeated rides, and tradeoffs.',
    flags: [
      { name: 'dining-meals-per-week', value: 'N', summary: 'What-if slider 0–14. Does not write the saved preference.' },
      { name: 'redact', summary: 'Replace merchant, transaction, and place names with stable pseudonyms.' },
    ],
    writes: false,
  },
  {
    name: 'rewards',
    summary: 'Each credit card as set up in the app (product, rates, this quarter\'s categories and caps), the best card per spending category, and what card purchases earned versus the best card for each.',
    flags: [{ name: 'range', value: '1m|3m|1y', summary: 'Review window. Default: 3m.' }],
    writes: false,
  },
  { name: 'sync-status', summary: 'In-process Plaid job, recent sync runs, and latest SnapTrade run.', writes: false },
  {
    name: 'export',
    summary: 'Write a CSV under the data directory and return { path, rows }.',
    args: ['accounts|net-worth'],
    flags: [{ name: 'range', value: '1m|3m|1y|all', summary: 'net-worth only. Default: all.' }],
    writes: true,
  },
  {
    name: 'backup',
    summary: 'Copy the database into dataDir/backups, or list existing backups.',
    args: ['[list]'],
    writes: true,
  },
  {
    name: 'sync',
    summary: 'Request a provider refresh. The CLI cannot read Windows Credential Manager, so this usually writes a pending-sync marker for the desktop app.',
    writes: true,
  },
  { name: 'doctor', summary: 'Data-dir, schema version, lock, provider-configured flags, and row counts. No balances.', writes: false },
  {
    name: 'snapshot',
    summary: 'One versioned JSON document: summary, accounts, net worth, trends, investments, insights, spending, data quality.',
    flags: [
      { name: 'redact', summary: 'Stable per-institution/account/merchant pseudonyms. Numbers and relationships stay.' },
      { name: 'out', value: 'FILE', summary: 'Write the snapshot to FILE; stdout is a confirmation.' },
    ],
    writes: false,
  },
  { name: 'help', summary: 'This catalog. `help <command>` prints one command.', args: ['[command]'], writes: false },
];

const HELP = {
  name: 'assetscope',
  usage: 'assetscope <command> [flags]',
  globalFlags: GLOBAL_FLAGS,
  commands: HELP_COMMANDS,
  exitCodes: {
    0: 'success (sync queued or succeeded also uses 0)',
    1: 'runtime failure, or sync partial/failed',
    2: 'validation error (unknown command, bad flag, missing id)',
    3: 'not configured',
    4: 'not found',
  },
};

function helpPayload(topic?: string) {
  if (!topic) return HELP;
  const command = HELP_COMMANDS.find((c) => c.name === topic);
  if (!command) throw validation(`Unknown command: ${topic}`);
  return { command, globalFlags: GLOBAL_FLAGS, exitCodes: HELP.exitCodes };
}

export async function runCli(argv: string[]): Promise<number> {
  const { command, positionals, flags } = parseArgv(argv);
  const table = flagBool(flags, 'table');

  if (command === 'help' || !command || flagBool(flags, 'help')) {
    try {
      const topic = command === 'help' ? positionals[0] : command;
      printResult(helpPayload(topic), table);
      return 0;
    } catch (err) {
      const apiErr = err instanceof ApiError
        ? err
        : new ApiError('INTERNAL_ERROR', err instanceof Error ? err.message : String(err));
      process.stderr.write(JSON.stringify({
        error: { code: apiErr.code, message: apiErr.message },
      }) + '\n');
      return EXIT_CODES[apiErr.code];
    }
  }

  if (!COMMANDS.has(command)) {
    process.stderr.write(JSON.stringify({
      error: {
        code: 'VALIDATION_ERROR',
        message: `Unknown command: ${command}`,
      },
    }) + '\n');
    return 2;
  }

  if (flagBool(flags, 'redact') && !REDACTABLE_COMMANDS.has(command)) {
    process.stderr.write(JSON.stringify({
      error: {
        code: 'VALIDATION_ERROR',
        message: `--redact is not supported by ${command}; it works with ${[...REDACTABLE_COMMANDS].join(', ')}.`,
      },
    }) + '\n');
    return 2;
  }

  try {
    const { mode } = await bootstrap(flags);
    if (command !== 'sync') await openDb(mode);

    let result: unknown;
    switch (command) {
      case 'summary': result = await cmdSummary(); break;
      case 'accounts': result = await cmdAccounts(flags); break;
      case 'account': result = await cmdAccount(positionals[0]); break;
      case 'net-worth': result = await cmdNetWorth(flags); break;
      case 'allocation': result = await cmdAllocation(); break;
      case 'trends': result = await cmdTrends(flags); break;
      case 'investments': result = await cmdInvestments(flags); break;
      case 'positions': result = await cmdPositions(flags); break;
      case 'activities': result = await cmdActivities(flags); break;
      case 'orders': result = await cmdOrders(flags); break;
      case 'realized': result = await cmdRealized(flags); break;
      case 'insights': result = await cmdInsights(flags); break;
      case 'transactions': result = await cmdTransactions(flags); break;
      case 'spending': result = await cmdSpending(flags); break;
      case 'living': result = await cmdLiving(flags); break;
      case 'rewards': result = await cmdRewards(flags); break;
      case 'sync-status': result = await cmdSyncStatus(); break;
      case 'export': result = await cmdExport(positionals[0], flags); break;
      case 'backup': result = await cmdBackup(positionals[0]); break;
      case 'sync': result = await cmdSync(); break;
      case 'doctor': result = await cmdDoctor(mode); break;
      case 'snapshot': result = await cmdSnapshot(flags); break;
    }

    printResult(result, table);
    if (command === 'sync') {
      const status = (result as { status: string }).status;
      return status === 'partial' || status === 'failed' ? 1 : 0;
    }
    return 0;
  } catch (err) {
    const apiErr = err instanceof ApiError
      ? err
      : new ApiError('INTERNAL_ERROR', err instanceof Error ? err.message : String(err));
    process.stderr.write(JSON.stringify({
      error: { code: apiErr.code, message: apiErr.message },
    }) + '\n');
    return EXIT_CODES[apiErr.code];
  }
}

const isMainModule = process.argv[1] !== undefined
  && pathToFileURL(process.argv[1]).href === import.meta.url;
if (isMainModule) {
  runCli(process.argv.slice(2)).then((code) => { process.exitCode = code; });
}
