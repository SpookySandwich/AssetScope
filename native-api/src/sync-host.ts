// One-shot headless scheduled sync (SPEC §12.4): runs exactly one sync and
// exits. If the desktop sidecar owns the data directory, defer by writing
// runtime/pending-sync.json — the running app (or the next launch) performs
// the sync instead. Results persist in sync_runs like every other trigger.

export async function runScheduledSync(): Promise<number> {
  const { isLockHeldByOther, writePendingSyncMarker } = await import('./services/lock.js');
  if (isLockHeldByOther()) {
    writePendingSyncMarker();
    console.error('[sync-host] desktop app owns the data directory; deferred via pending-sync marker');
    return 0;
  }

  const { initDb } = await import('./core/database.js');
  const { ensureNativeSchema } = await import('./services/schema.js');
  const { isPlaidConfigured } = await import('./core/plaid.js');
  const { isSnapTradeConfigured } = await import('./services/snaptrade-config.js');
  const {
    syncSnapTrade, recoverInterruptedInvestmentSyncRuns,
  } = await import('./services/snaptrade-sync.js');
  const {
    startSync, waitForCurrentJob, getCurrentJob, recoverInterruptedSyncRuns,
  } = await import('./services/syncjobs.js');
  const { SIDECAR_VERSION } = await import('./envelope.js');

  await initDb();
  await ensureNativeSchema();
  await recoverInterruptedSyncRuns();
  await recoverInterruptedInvestmentSyncRuns();
  const plaidConfigured = isPlaidConfigured();
  const snapTradeConfigured = isSnapTradeConfigured();
  if (!plaidConfigured && !snapTradeConfigured) {
    // The CLI runs standalone and can never read Windows Credential Manager
    // (SPEC: provider secrets are C#-only), so this is its normal path. Defer
    // via the same marker the desktop app already polls for, rather than
    // failing silently with nothing queued.
    writePendingSyncMarker();
    console.error('[sync-host] no data provider is configured; deferred via pending-sync marker');
    return 2;
  }
  let failed = false;
  if (plaidConfigured) {
    await startSync('scheduled', true, SIDECAR_VERSION);
    await waitForCurrentJob();
    const job = getCurrentJob();
    console.error(`[sync-host] Plaid ${job?.state}: ${job?.progress.successfulItems} ok, ${job?.progress.failedItems} failed`);
    failed ||= job?.state === 'failed';
  }
  if (snapTradeConfigured) {
    try {
      const run = await syncSnapTrade('scheduled');
      console.error(`[sync-host] SnapTrade ${run.status}: ${run.accountCount} accounts, ${run.positionCount} positions`);
    } catch {
      console.error('[sync-host] SnapTrade failed; cached investment data was preserved');
      failed = true;
    }
  }
  return failed ? 1 : 0;
}
