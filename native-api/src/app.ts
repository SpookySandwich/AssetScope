import { createServer, type Server } from 'node:http';
import { db, initDb } from './core/database.js';
import { isPlaidConfigured } from './core/plaid.js';
import { getSetting, setSetting } from './core/settings.js';
import { Router, readJsonBody, type RouteContext } from './router.js';
import { sendData, sendError, newRequestId, PROTOCOL_VERSION } from './envelope.js';
import { ApiError, validation, notFound } from './errors.js';
import { assertAuthorized } from './security/auth.js';
import { ensureNativeSchema, NATIVE_SCHEMA_VERSION } from './services/schema.js';
import {
  currentSummary, netWorthHistory, allocation, accountSummaries,
  rangeToWindow, type Range, type Granularity,
} from './services/networth.js';
import {
  createManualAccount, updateManualValue, patchAccount, setArchived,
  deleteManualAccount, recentSnapshots, type ManualAccountInput, type AccountPatch,
} from './services/accounts.js';
import {
  startSync, getCurrentJob, listRuns, recoverInterruptedSyncRuns, type SyncTrigger,
} from './services/syncjobs.js';
import { seedDemoData } from './services/demo.js';
import { configStatus, applyAndTestConfig, type PlaidConfigInput } from './services/plaid-config.js';
import { createLinkSession, getLinkSession, cancelLinkSession } from './services/link-sessions.js';
import { listItems, disconnectItem, previewDeleteItemData, deleteItemData } from './services/items.js';
import {
  listBackups, createBackup, restoreBackup, exportAccountsCsv, exportNetWorthCsv,
  getSyncRun, categoryTrends, deleteAllData, readEnvCredentials,
} from './services/maintenance.js';
import { acquireLock, releaseLock, takePendingSyncMarker } from './services/lock.js';
import { log, recentLogs, clearLogs } from './log.js';
import {
  snapTradeConfigStatus, applyAndTestSnapTradeConfig, requireSnapTradeGateway,
  isSnapTradeConfigured, type SnapTradeConfigInput,
} from './services/snaptrade-config.js';
import {
  syncSnapTrade, startSnapTradeSync, latestInvestmentSyncRun, recoverInterruptedInvestmentSyncRuns,
  type InvestmentSyncTrigger,
} from './services/snaptrade-sync.js';
import { listSnapTradeConnections } from './services/snaptrade-connections.js';
import {
  investmentOverview, investmentAccounts, investmentPositions,
  investmentActivities, investmentOrders, investmentRealizedPnl, type InvestmentRange,
} from './services/investments.js';
import { generateInsights } from './services/insights.js';
import { listTransactions, transactionsSummary } from './services/transactions.js';
import {
  livingOverview, patchTransaction, putLivingPreferences,
  createObligation, patchObligation, deleteObligation, putRouteAlternative,
  putMerchantDefault, livingMerchants,
} from './services/living.js';
import {
  rewardsOverview, putRewardCard, putRewardPreferences, type RewardRangeKey,
} from './services/rewards.js';

export { SIDECAR_VERSION } from './envelope.js';
import { SIDECAR_VERSION } from './envelope.js';

export type AppOptions = {
  mode: 'real' | 'demo';
  dataDir: string;
  sessionToken: string;
  onShutdownRequested: () => void;
};

const VALID_RANGES = new Set<Range>(['1m', '3m', '1y', 'all']);
const VALID_GRANULARITIES = new Set<Granularity>(['day', 'week', 'month']);
const VALID_INVESTMENT_RANGES = new Set<InvestmentRange>(['1d', '1w', '1m', '3m', '1y', 'all']);

// UI preference keys stored in the upstream settings table, namespaced to
// avoid colliding with TUI/GUI keys.
const SETTING_KEYS: Record<string, string> = {
  theme: 'winui_theme',
  syncOnLaunch: 'winui_sync_on_launch',
  dailySyncEnabled: 'winui_daily_sync_enabled',
  dailySyncTime: 'winui_daily_sync_time',
  showDecimals: 'winui_show_decimals',
};

export async function buildApp(opts: AppOptions): Promise<{ server: Server; router: Router }> {
  await initDb();

  // Read-only detection (SPEC §22.5): probe a write before claiming health.
  let degradedReadOnly = false;
  let migration = { migrated: false, schemaVersion: 0 };
  try {
    await db.execute("INSERT OR REPLACE INTO settings (key, value) VALUES ('winui_write_probe', '1')");
    await db.execute("DELETE FROM settings WHERE key = 'winui_write_probe'");
    migration = await ensureNativeSchema();
    await recoverInterruptedSyncRuns();
    await recoverInterruptedInvestmentSyncRuns();
    if (opts.mode === 'demo') await seedDemoData();
  } catch {
    degradedReadOnly = true;
    log('warn', 'database is not writable — running in read-only degraded mode');
  }

  // Data-directory ownership + deferred scheduled-sync marker (SPEC §12.4/§12.5).
  if (!degradedReadOnly) {
    acquireLock();
    process.on('exit', releaseLock);
    const processMarker = () => {
      if (opts.mode === 'real' && takePendingSyncMarker()) {
        log('info', 'processing deferred scheduled sync marker');
        if (isPlaidConfigured()) startSync('scheduled', true, SIDECAR_VERSION).catch(() => {});
        if (isSnapTradeConfigured()) syncSnapTrade('scheduled').catch(() => {});
      }
    };
    processMarker();
    setInterval(processMarker, 60_000).unref();
  }

  const router = new Router();

  // ── System ──────────────────────────────────────────────────────────────────
  router.get('/v1/health', async () => ({
    status: 'ok',
    sidecarVersion: SIDECAR_VERSION,
    protocolVersion: PROTOCOL_VERSION,
    mode: opts.mode,
  }));

  router.get('/v1/bootstrap', async () => {
    const accountsRes = await db.execute('SELECT COUNT(*) AS c FROM accounts WHERE archived_at IS NULL');
    const accountCount = Number((accountsRes.rows[0] as unknown as { c: number }).c);
    const attentionRes = await db.execute("SELECT COUNT(*) AS c FROM plaid_items WHERE status = 'attention'");
    const lastSuccessRes = await db.execute('SELECT MAX(last_success_at) AS t FROM plaid_items');
    const investmentAccountRes = await db.execute(
      "SELECT COUNT(*) AS c FROM accounts WHERE type = 'investment' AND archived_at IS NULL",
    );
    const investmentSync = await latestInvestmentSyncRun();
    const job = getCurrentJob();
    return {
      app: {
        sidecarVersion: SIDECAR_VERSION,
        protocolVersion: PROTOCOL_VERSION,
        schemaVersion: NATIVE_SCHEMA_VERSION,
        dataDir: opts.dataDir,
        mode: opts.mode,
        migratedOnStartup: migration.migrated,
        degradedReadOnly,
      },
      onboarding: {
        required: accountCount === 0 && opts.mode === 'real',
        hasExistingData: accountCount > 0,
        plaidConfigured: isPlaidConfigured(),
        snapTradeConfigured: isSnapTradeConfigured(),
        accountCount,
        investmentAccountCount: Number((investmentAccountRes.rows[0] as unknown as { c: number }).c),
      },
      sync: {
        state: job?.state === 'running' ? 'running' : 'idle',
        lastSuccessAt: (lastSuccessRes.rows[0] as unknown as { t: string | null }).t,
        attentionItemCount: Number((attentionRes.rows[0] as unknown as { c: number }).c),
        investmentLastSuccessAt: investmentSync?.status === 'succeeded' ? investmentSync.completedAt : null,
      },
      preferences: {
        theme: (await getSetting(SETTING_KEYS.theme)) ?? 'system',
        syncOnLaunch: ((await getSetting(SETTING_KEYS.syncOnLaunch)) ?? 'true') === 'true',
        dailySyncEnabled: ((await getSetting(SETTING_KEYS.dailySyncEnabled)) ?? 'true') === 'true',
        dailySyncTime: (await getSetting(SETTING_KEYS.dailySyncTime)) ?? '08:00',
        showDecimals: ((await getSetting(SETTING_KEYS.showDecimals)) ?? 'true') === 'true',
      },
    };
  });

  router.post('/v1/shutdown', async () => {
    // Ack first; the process exits after the response flushes.
    setTimeout(() => opts.onShutdownRequested(), 50);
    return { shuttingDown: true };
  });

  router.get('/v1/diagnostics/summary', async () => {
    const runs = await listRuns(5);
    return {
      sidecarVersion: SIDECAR_VERSION,
      protocolVersion: PROTOCOL_VERSION,
      schemaVersion: NATIVE_SCHEMA_VERSION,
      nodeVersion: process.version,
      pid: process.pid,
      dataDir: opts.dataDir,
      mode: opts.mode,
      recentRuns: runs,
    };
  });

  // ── Dashboard ───────────────────────────────────────────────────────────────
  router.get('/v1/dashboard', async ({ query }) => {
    const range = (query.get('range') ?? '1y') as Range;
    if (!VALID_RANGES.has(range)) throw validation('range must be 1m|3m|1y|all.');
    const today = new Date().toISOString().slice(0, 10);
    const win = rangeToWindow(range, today, null);
    const [summary, history, alloc, accounts] = await Promise.all([
      currentSummary(),
      netWorthHistory(win.from, win.to, win.granularity),
      allocation(),
      accountSummaries({ includeExcluded: true, includeArchived: false }),
    ]);
    const included = accounts.filter((a) => !a.excluded);
    const topAccounts = included
      .filter((a) => a.balance !== null)
      .sort((a, b) => Math.abs(parseFloat(b.balance!.amount)) - Math.abs(parseFloat(a.balance!.amount)))
      .slice(0, 6);
    const staleCount = included.filter((a) => a.freshness === 'stale' || a.freshness === 'very_stale').length;
    return {
      summary,
      history,
      topAccounts,
      allocation: alloc,
      dataQuality: {
        excludedAccountCount: accounts.filter((a) => a.excluded).length,
        nonUsdAccountCount: accounts.filter((a) => a.currencyCode !== 'USD').length,
        missingBalanceCount: included.filter((a) => a.balance === null).length,
        staleAccountCount: staleCount,
        attentionItemCount: included.filter((a) => a.freshness === 'attention').length,
      },
    };
  });

  router.get('/v1/net-worth/history', async ({ query }) => {
    const today = new Date().toISOString().slice(0, 10);
    const rangeParam = query.get('range');
    let from: string, to: string, granularity: Granularity;
    if (rangeParam) {
      if (!VALID_RANGES.has(rangeParam as Range)) throw validation('range must be 1m|3m|1y|all.');
      const win = rangeToWindow(rangeParam as Range, today, null);
      from = win.from; to = win.to; granularity = win.granularity;
    } else {
      from = query.get('from') ?? today.slice(0, 4) + '-01-01';
      to = query.get('to') ?? today;
      granularity = (query.get('granularity') ?? 'day') as Granularity;
      if (!/^\d{4}-\d{2}-\d{2}$/.test(from) || !/^\d{4}-\d{2}-\d{2}$/.test(to)) {
        throw validation('from/to must be YYYY-MM-DD.');
      }
      if (!VALID_GRANULARITIES.has(granularity)) throw validation('granularity must be day|week|month.');
    }
    return { points: await netWorthHistory(from, to, granularity) };
  });

  router.get('/v1/allocation', async () => allocation());

  // ── Investments ────────────────────────────────────────────────────────────
  router.get('/v1/investments/overview', async ({ query }) => {
    const range = (query.get('range') ?? '1y') as InvestmentRange;
    if (!VALID_INVESTMENT_RANGES.has(range)) throw validation('range must be 1d|1w|1m|3m|1y|all.');
    return investmentOverview(range);
  });

  router.get('/v1/investments/accounts', async () => ({ accounts: await investmentAccounts() }));

  router.get('/v1/investments/positions', async ({ query }) => ({
    positions: await investmentPositions(query.get('accountId') ?? undefined),
  }));

  router.get('/v1/investments/activities', async ({ query }) => ({
    activities: await investmentActivities(
      Number(query.get('limit') ?? '100') || 100,
      query.get('accountId') ?? undefined,
    ),
  }));

  router.get('/v1/investments/orders', async ({ query }) => ({
    orders: await investmentOrders(
      Number(query.get('limit') ?? '100') || 100,
      query.get('accountId') ?? undefined,
    ),
  }));

  router.get('/v1/investments/realized', async ({ query }) => investmentRealizedPnl(
    Number(query.get('limit') ?? '200') || 200,
    query.get('accountId') ?? undefined,
  ));

  // ── Accounts ────────────────────────────────────────────────────────────────
  router.get('/v1/accounts', async ({ query }) => {
    const includeExcluded = query.get('includeExcluded') !== 'false';
    const includeArchived = query.get('includeArchived') === 'true';
    let list = await accountSummaries({ includeExcluded, includeArchived });
    const category = query.get('category');
    if (category) list = list.filter((a) => a.category === category);
    const search = query.get('search')?.toLowerCase();
    if (search) {
      list = list.filter((a) =>
        [a.displayName, a.institutionName, a.nickname, a.owner, a.mask]
          .some((f) => f !== null && f.toLowerCase().includes(search)),
      );
    }
    return { accounts: list };
  });

  router.get('/v1/accounts/:accountId', async ({ params }) => {
    const list = await accountSummaries({ includeExcluded: true, includeArchived: true });
    const account = list.find((a) => a.id === params.accountId);
    if (!account) throw notFound('Account');
    const snapshots = await recentSnapshots(params.accountId, 10);
    return { account, recentSnapshots: snapshots };
  });

  router.patch('/v1/accounts/:accountId', async ({ params, body }) => {
    await patchAccount(params.accountId, (body ?? {}) as AccountPatch);
    return { updated: true };
  });

  router.post('/v1/accounts/manual', async ({ body }) => {
    const id = await createManualAccount((body ?? {}) as ManualAccountInput);
    return { accountId: id };
  });

  router.patch('/v1/accounts/:accountId/value', async ({ params, body }) => {
    const input = (body ?? {}) as { amount: string; effectiveDate?: string; note?: string };
    await updateManualValue(params.accountId, input);
    return { updated: true };
  });

  router.delete('/v1/accounts/:accountId', async ({ params }) => {
    const preview = await deleteManualAccount(params.accountId);
    return { deleted: true, historyCount: preview.historyCount };
  });

  router.post('/v1/accounts/:accountId/archive', async ({ params }) => {
    await setArchived(params.accountId, true);
    return { archived: true };
  });

  router.post('/v1/accounts/:accountId/unarchive', async ({ params }) => {
    await setArchived(params.accountId, false);
    return { archived: false };
  });

  // ── Sync ────────────────────────────────────────────────────────────────────
  router.post('/v1/sync', async ({ body }) => {
    if (opts.mode === 'demo') {
      throw new ApiError('NOT_CONFIGURED', 'Demo Mode uses local sample data; there is nothing to sync.');
    }
    const input = (body ?? {}) as { trigger?: SyncTrigger; force?: boolean };
    const trigger = input.trigger ?? 'manual';
    if (!['initial', 'launch', 'manual', 'scheduled', 'reconnect', 'retry'].includes(trigger)) {
      throw validation('Unknown sync trigger.');
    }
    return startSync(trigger, input.force ?? true, SIDECAR_VERSION);
  });

  router.get('/v1/sync/status', async () => ({ job: getCurrentJob() }));

  router.get('/v1/sync/runs', async ({ query }) => ({
    runs: await listRuns(Number(query.get('limit') ?? '50') || 50),
  }));

  router.get('/v1/sync/runs/:runId', async ({ params }) => getSyncRun(params.runId));

  // ── Trends (SPEC §20.2/§20.3): per-category as-of series ────────────────────
  router.get('/v1/trends', async ({ query }) => {
    const range = (query.get('range') ?? '1y') as Range;
    if (!VALID_RANGES.has(range)) throw validation('range must be 1m|3m|1y|all.');
    const today = new Date().toISOString().slice(0, 10);
    const win = rangeToWindow(range, today, null);
    return categoryTrends(win.from, win.to, win.granularity);
  });

  router.get('/v1/insights', async ({ query }) => {
    const range = (query.get('range') ?? '1y') as Range;
    if (!VALID_RANGES.has(range)) throw validation('range must be 1m|3m|1y|all.');
    return generateInsights(range, Number(query.get('limit') ?? '3') || 3);
  });

  router.get('/v1/transactions', async ({ query }) => {
    const from = query.get('from') ?? undefined;
    const to = query.get('to') ?? undefined;
    if (from && !/^\d{4}-\d{2}-\d{2}$/.test(from)) throw validation('from must be YYYY-MM-DD.');
    if (to && !/^\d{4}-\d{2}-\d{2}$/.test(to)) throw validation('to must be YYYY-MM-DD.');
    const limitRaw = query.get('limit');
    const limit = limitRaw === null ? undefined : Number(limitRaw);
    if (limitRaw !== null && (!Number.isFinite(limit) || (limit as number) <= 0)) {
      throw validation('limit must be a positive number.');
    }
    return listTransactions({
      from, to,
      accountId: query.get('accountId') ?? undefined,
      category: query.get('category') ?? undefined,
      limit,
    });
  });

  router.get('/v1/transactions/summary', async ({ query }) => {
    const from = query.get('from');
    const to = query.get('to');
    if (!from || !/^\d{4}-\d{2}-\d{2}$/.test(from)) throw validation('from must be YYYY-MM-DD.');
    if (!to || !/^\d{4}-\d{2}-\d{2}$/.test(to)) throw validation('to must be YYYY-MM-DD.');
    if (from > to) throw validation('from must not be after to.');
    return transactionsSummary({ from, to, accountId: query.get('accountId') ?? undefined });
  });

  router.patch('/v1/transactions/:transactionId', async ({ params, body }) =>
    patchTransaction(params.transactionId, (body ?? {}) as {
      partySize?: number | null;
      tripOrigin?: string | null;
      tripDestination?: string | null;
      tripKind?: string | null;
      livingKind?: string | null;
    }));

  router.get('/v1/living', async ({ query }) => {
    const mealsRaw = query.get('diningMealsPerWeek');
    if (mealsRaw === null) return livingOverview();
    const diningMealsPerWeek = Number(mealsRaw);
    if (!Number.isInteger(diningMealsPerWeek) || diningMealsPerWeek < 0 || diningMealsPerWeek > 14) {
      throw validation('diningMealsPerWeek must be an integer from 0 to 14.');
    }
    return livingOverview({ diningMealsPerWeek });
  });

  router.get('/v1/living/merchants', async () => livingMerchants());

  router.put('/v1/living/merchant-defaults/:merchantKey', async ({ params, body }) =>
    putMerchantDefault({
      ...(body ?? {}) as { monthlyAmount?: string | null; kind?: string | null },
      merchantKey: decodeURIComponent(params.merchantKey),
    }));

  router.put('/v1/living/preferences', async ({ body }) =>
    putLivingPreferences((body ?? {}) as {
      diningMealsPerWeek?: number | null;
      goalKind?: 'none' | 'contribution' | 'target';
      goalLabel?: string | null;
      goalMonthlyContribution?: string | null;
      goalTargetAmount?: string | null;
    }));

  router.post('/v1/living/obligations', async ({ body }) =>
    createObligation((body ?? {}) as { name?: string; amount?: string; interval?: string; kind?: string }));

  router.patch('/v1/living/obligations/:obligationId', async ({ params, body }) =>
    patchObligation(params.obligationId, (body ?? {}) as {
      name?: string; amount?: string; interval?: string; kind?: string;
    }));

  router.delete('/v1/living/obligations/:obligationId', async ({ params }) =>
    deleteObligation(params.obligationId));

  router.put('/v1/living/routes/alternative', async ({ body }) =>
    putRouteAlternative((body ?? {}) as {
      origin?: string; destination?: string; label?: string | null; monthlyAmount?: string | null;
    }));

  // ── Card rewards ────────────────────────────────────────────────────────────
  router.get('/v1/rewards', async ({ query }) => {
    const range = (query.get('range') ?? '3m') as RewardRangeKey;
    if (!['1m', '3m', '1y'].includes(range)) throw validation('range must be 1m|3m|1y.');
    return rewardsOverview({ range });
  });

  router.put('/v1/rewards/cards/:accountId', async ({ params, body }) =>
    putRewardCard(params.accountId, (body ?? {}) as {
      productId?: string | null; choiceCategory?: string | null; rotatingCategories?: string[] | null;
      activation?: { quarter?: string; activated?: boolean } | null;
    }));

  router.put('/v1/rewards/preferences', async ({ body }) =>
    putRewardPreferences((body ?? {}) as { pointValues?: Record<string, string | null> }));

  // ── Backups & exports (SPEC §21.4, UC-011) ──────────────────────────────────
  router.get('/v1/backups', async () => ({ backups: await listBackups() }));

  router.post('/v1/backups', async () => createBackup());

  router.post('/v1/backups/:backupId/restore', async ({ params }) => {
    const result = await restoreBackup(params.backupId);
    // The database connection is closed; this process must exit so the client
    // can restart a fresh sidecar against the restored file (UC-011).
    setTimeout(() => opts.onShutdownRequested(), 100);
    return { restored: true, restartRequired: true, ...result };
  });

  router.post('/v1/exports/accounts.csv', async () => exportAccountsCsv());

  router.post('/v1/exports/net-worth.csv', async ({ query }) => {
    const range = (query.get('range') ?? 'all') as Range;
    if (!VALID_RANGES.has(range)) throw validation('range must be 1m|3m|1y|all.');
    const today = new Date().toISOString().slice(0, 10);
    const win = rangeToWindow(range, today, null);
    return exportNetWorthCsv(win.from, win.to, win.granularity);
  });

  router.get('/v1/diagnostics/logs', async ({ query }) => ({
    entries: recentLogs(Number(query.get('limit') ?? '50') || 50),
  }));

  router.post('/v1/diagnostics/logs/clear', async () => {
    clearLogs();
    return { cleared: true };
  });

  // High-risk full wipe (SPEC §21.5). The client enforces the typed
  // product-name confirmation; confirm=true is the API-level second gate.
  router.delete('/v1/data', async ({ query }) => {
    if (query.get('confirm') !== 'true') {
      throw validation('Pass confirm=true to delete all local data.');
    }
    const result = await deleteAllData();
    setTimeout(() => opts.onShutdownRequested(), 100);
    return { ...result, restartRequired: true };
  });

  // ── Plaid ───────────────────────────────────────────────────────────────────
  // Demo Mode never accepts or uses real credentials (SPEC §15.6).
  const requireRealMode = () => {
    if (opts.mode === 'demo') {
      throw new ApiError('NOT_CONFIGURED', 'Bank connections are unavailable in Demo Mode. Switch to real data first.');
    }
  };

  router.get('/v1/plaid/config-status', async () => ({ ...configStatus(), mode: opts.mode }));

  router.put('/v1/plaid/config', async ({ body }) => {
    requireRealMode();
    return { ...(await applyAndTestConfig((body ?? {}) as PlaidConfigInput)), mode: opts.mode };
  });

  // Import upstream .env credentials, validate live, hand them to the client
  // exactly once so it can migrate the secret into Credential Manager (§13.1).
  router.post('/v1/plaid/config/import-env', async () => {
    requireRealMode();
    const credentials = readEnvCredentials();
    if (credentials === null) {
      throw new ApiError('NOT_FOUND', 'No usable .env file with Plaid credentials was found in the data directory.');
    }
    await applyAndTestConfig(credentials);
    return { ...credentials, status: configStatus() };
  });

  router.get('/v1/plaid/items', async () => ({ items: await listItems() }));

  router.post('/v1/plaid/link-sessions', async ({ body }) => {
    requireRealMode();
    const input = (body ?? {}) as { daysRequested?: number };
    return createLinkSession({ daysRequested: input.daysRequested });
  });

  router.get('/v1/plaid/link-sessions/:sessionId', async ({ params }) =>
    getLinkSession(params.sessionId));

  router.post('/v1/plaid/link-sessions/:sessionId/cancel', async ({ params }) =>
    cancelLinkSession(params.sessionId));

  router.post('/v1/plaid/items/:itemId/reconnect', async ({ params }) => {
    requireRealMode();
    return createLinkSession({ reconnectItemId: params.itemId });
  });

  router.post('/v1/plaid/items/:itemId/disconnect', async ({ params }) => {
    requireRealMode();
    await disconnectItem(params.itemId);
    return { disconnected: true };
  });

  // ── SnapTrade ──────────────────────────────────────────────────────────────
  // Personal API Key mode is intentional for this single-user desktop app:
  // no app-created userId/userSecret pair exists or is stored locally.
  router.get('/v1/snaptrade/config-status', async () => ({ ...snapTradeConfigStatus(), mode: opts.mode }));

  router.put('/v1/snaptrade/config', async ({ body }) => {
    requireRealMode();
    return { ...(await applyAndTestSnapTradeConfig((body ?? {}) as SnapTradeConfigInput)), mode: opts.mode };
  });

  router.post('/v1/snaptrade/portal', async ({ body }) => {
    requireRealMode();
    const input = (body ?? {}) as { reconnectConnectionId?: string; darkMode?: boolean };
    const gateway = requireSnapTradeGateway();
    const portal = await gateway.createPortal({
      reconnect: input.reconnectConnectionId,
      darkMode: input.darkMode ?? false,
    });
    return {
      url: portal.redirectURI,
      sessionId: portal.sessionId,
      expiresAt: new Date(Date.now() + 5 * 60_000).toISOString(),
      mode: input.reconnectConnectionId ? 'reconnect' : 'connect',
    };
  });

  router.get('/v1/snaptrade/connections', async () => ({ connections: await listSnapTradeConnections() }));

  router.post('/v1/snaptrade/connections/:connectionId/reconnect', async ({ params, body }) => {
    requireRealMode();
    const gateway = requireSnapTradeGateway();
    const portal = await gateway.createPortal({
      reconnect: params.connectionId,
      darkMode: Boolean((body as { darkMode?: boolean } | null)?.darkMode),
    });
    return {
      url: portal.redirectURI,
      sessionId: portal.sessionId,
      expiresAt: new Date(Date.now() + 5 * 60_000).toISOString(),
      mode: 'reconnect',
    };
  });

  router.post('/v1/snaptrade/sync', async ({ body }) => {
    requireRealMode();
    const trigger = String((body as { trigger?: string } | null)?.trigger ?? 'manual') as InvestmentSyncTrigger;
    if (!['initial', 'launch', 'manual', 'scheduled', 'portal-return', 'retry'].includes(trigger)) {
      throw validation('Unknown investment sync trigger.');
    }
    return startSnapTradeSync(trigger);
  });

  router.get('/v1/snaptrade/sync/status', async () => ({ run: await latestInvestmentSyncRun() }));

  // Two-phase destructive delete (SPEC §13.5.2): without confirm the call only
  // reports what would be removed; with confirm=true it backs up, then deletes.
  router.delete('/v1/plaid/items/:itemId/data', async ({ params, query }) => {
    requireRealMode();
    if (query.get('confirm') !== 'true') {
      return { deleted: false, preview: await previewDeleteItemData(params.itemId) };
    }
    return { deleted: true, preview: await deleteItemData(params.itemId) };
  });

  // ── Settings ────────────────────────────────────────────────────────────────
  router.get('/v1/settings', async () => ({
    theme: (await getSetting(SETTING_KEYS.theme)) ?? 'system',
    syncOnLaunch: ((await getSetting(SETTING_KEYS.syncOnLaunch)) ?? 'true') === 'true',
    dailySyncEnabled: ((await getSetting(SETTING_KEYS.dailySyncEnabled)) ?? 'true') === 'true',
    dailySyncTime: (await getSetting(SETTING_KEYS.dailySyncTime)) ?? '08:00',
    showDecimals: ((await getSetting(SETTING_KEYS.showDecimals)) ?? 'true') === 'true',
  }));

  router.patch('/v1/settings', async ({ body }) => {
    const input = (body ?? {}) as Record<string, unknown>;
    for (const [key, value] of Object.entries(input)) {
      const dbKey = SETTING_KEYS[key];
      if (!dbKey) throw validation(`Unknown setting: ${key}`, { field: key });
      if (key === 'theme' && !['system', 'light', 'dark'].includes(String(value))) {
        throw validation('theme must be system|light|dark.', { field: 'theme' });
      }
      if (key === 'dailySyncTime' && !/^\d{2}:\d{2}$/.test(String(value))) {
        throw validation('dailySyncTime must be HH:MM.', { field: 'dailySyncTime' });
      }
      await setSetting(dbKey, String(value));
    }
    return { updated: true };
  });

  // ── HTTP plumbing ───────────────────────────────────────────────────────────
  const server = createServer((req, res) => {
    const requestId = newRequestId();
    void (async () => {
      try {
        assertAuthorized(req, opts.sessionToken);
        const url = new URL(req.url ?? '/', 'http://127.0.0.1');
        const match = router.match(req.method ?? 'GET', url.pathname);
        if (!match) throw notFound('Endpoint');
        const body = await readJsonBody(req);
        const ctx: RouteContext = { req, res, requestId, params: match.params, query: url.searchParams, body };
        const data = await match.handler(ctx);
        sendData(res, requestId, data);
      } catch (err) {
        if (err instanceof ApiError) {
          sendError(res, requestId, err);
        } else {
          // Never leak stack traces or raw driver errors across the API (SPEC §23.3).
          log('error', `${requestId} unhandled: ${err instanceof Error ? err.constructor.name : typeof err}`);
          sendError(res, requestId, new ApiError('INTERNAL_ERROR', 'An unexpected error occurred.', { retryable: true }));
        }
      }
    })();
  });

  return { server, router };
}
