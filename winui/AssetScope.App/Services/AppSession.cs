using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using AssetScope.Core;
using AssetScope.Core.Api;
using AssetScope.Core.Security;
using AssetScope.Core.Sidecar;
using AssetScope.Core.Diagnostics;

namespace AssetScope.App.Services;

public enum SessionState
{
    NotStarted,
    StartingSidecar,
    LoadingBootstrap,
    Ready,
    FatalError,
}

/// <summary>
/// App-wide singleton: owns the sidecar process, the API client, privacy mode,
/// and the startup state machine (SPEC §11.1). Pages read it via AppSession.Current.
/// </summary>
public sealed partial class AppSession : ObservableObject
{
    public static AppSession Current { get; } = new();

    public AppSettings Settings { get; } = AppSettings.Load();
    public ApiClient? Api { get; private set; }
    public BootstrapDto? Bootstrap { get; set; }

    private SidecarLauncher? _launcher;

    [ObservableProperty] public partial SessionState State { get; set; } = SessionState.NotStarted;
    [ObservableProperty] public partial string? StartupError { get; set; }
    [ObservableProperty] public partial bool PrivacyMode { get; set; }
    [ObservableProperty] public partial bool IsSyncing { get; set; }
    [ObservableProperty] public partial string? LastSyncedAt { get; set; }
    [ObservableProperty] public partial int AttentionItemCount { get; set; }
    [ObservableProperty] public partial bool IsOffline { get; set; }
    /// <summary>Resource key for the last sync failure, or null when healthy.</summary>
    [ObservableProperty] public partial string? LastSyncError { get; set; }

    public bool DegradedReadOnly => Bootstrap?.App.DegradedReadOnly ?? false;

    public bool IsDemoMode => Settings.Mode == "demo";
    public bool ShowDecimals => Bootstrap?.Preferences.ShowDecimals ?? true;

    public event EventHandler? SessionReady;
    public event EventHandler? DataRefreshed;

    private AppSession() { }

    /// <summary>Formats an amount string honoring Privacy Mode.</summary>
    public string Fmt(string? amount, bool decimals = true) =>
        amount is null ? "—" : Money.Format(amount, decimals, PrivacyMode);

    public string FmtSigned(string? amount, bool decimals = true) =>
        amount is null ? "—" : Money.FormatSigned(Money.Parse(amount), decimals, PrivacyMode);

    public async Task StartAsync()
    {
        if (!await TryStartAsync())
            AppLog.Error("AppSession", StartupError ?? "Startup failed.");
    }

    /// <summary>
    /// Starts a new sidecar and only then drops the previous one, so a failed
    /// demo/real switch does not leave the app talking to a dead port.
    /// </summary>
    public async Task<bool> TryStartAsync()
    {
        AppLog.Info("AppSession", $"Starting local data engine; mode={Settings.Mode}.");
        State = SessionState.StartingSidecar;
        StartupError = null;
        var previousLauncher = _launcher;
        var previousApi = Api;
        SidecarLauncher? incoming = null;
        try
        {
            // Installed layout ships its own sidecar; the repo is the dev fallback.
            var repoRoot = SidecarLauncher.FindInstalledSidecar() is null
                ? SidecarLauncher.FindRepoRoot(Settings.RepoRoot)
                : null;

            if (IsDemoMode)
                Directory.CreateDirectory(AppSettings.DemoDataDirectory);
            var dataDir = IsDemoMode ? AppSettings.DemoDataDirectory : Settings.RealDataDir;

            // Real mode: hand stored Plaid credentials to the sidecar via its
            // process environment (Client ID from settings, Secret from Windows
            // Credential Manager). Demo Mode never receives credentials.
            Dictionary<string, string>? env = IsDemoMode ? null : [];
            if (!IsDemoMode && !string.IsNullOrEmpty(Settings.PlaidClientId))
            {
                var secret = WindowsCredentials.Read(WindowsCredentials.PlaidSecretTarget);
                if (secret is not null)
                {
                    env!["PLAID_CLIENT_ID"] = Settings.PlaidClientId;
                    env["PLAID_SECRET"] = secret;
                    env["PLAID_ENV"] = Settings.PlaidEnvironment;
                }
            }
            if (!IsDemoMode && !string.IsNullOrEmpty(Settings.SnapTradeClientId))
            {
                var consumerKey = WindowsCredentials.Read(WindowsCredentials.SnapTradeConsumerKeyTarget);
                if (consumerKey is not null)
                {
                    env!["SNAPTRADE_CLIENT_ID"] = Settings.SnapTradeClientId;
                    env["SNAPTRADE_CONSUMER_KEY"] = consumerKey;
                }
            }

            // Demo seed writes hundreds of history rows before the handshake,
            // so the first demo boot needs more than the default 10s.
            var handshakeTimeout = IsDemoMode ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(10);
            incoming = new SidecarLauncher();
            SidecarHandshake handshake;
            try
            {
                handshake = await incoming.StartAsync(
                    repoRoot, Settings.Mode, dataDir, env, handshakeTimeout);
            }
            catch (SidecarException)
            {
                incoming.Dispose();
                incoming = new SidecarLauncher();
                handshake = await incoming.StartAsync(
                    repoRoot, Settings.Mode, dataDir, env, handshakeTimeout);
            }

            var incomingApi = new ApiClient(handshake.Port, handshake.Token);
            State = SessionState.LoadingBootstrap;
            await incomingApi.GetHealthAsync();
            Bootstrap = await incomingApi.GetBootstrapAsync();
            LastSyncedAt = NewestTimestamp(Bootstrap.Sync.LastSuccessAt, Bootstrap.Sync.InvestmentLastSuccessAt);
            AttentionItemCount = Bootstrap.Sync.AttentionItemCount;
            IsOffline = false;
            LastSyncError = null;

            _launcher = incoming;
            incoming = null;
            Api = incomingApi;
            previousApi?.Dispose();
            previousLauncher?.Dispose();

            State = SessionState.Ready;
            AppLog.Info("AppSession", $"Ready; accountCount={Bootstrap.Onboarding.AccountCount}, plaidConfigured={Bootstrap.Onboarding.PlaidConfigured}.");
            SessionReady?.Invoke(this, EventArgs.Empty);

            if (!IsDemoMode && !DegradedReadOnly
                && (Bootstrap.Onboarding.PlaidConfigured || Bootstrap.Onboarding.SnapTradeConfigured)
                && Bootstrap.Onboarding.AccountCount > 0 && Bootstrap.Preferences.DailySyncEnabled)
            {
                _ = EnsureDailySyncScheduledAsync();
            }

            // Launch sync (SPEC §12.2): non-blocking, only when the latest
            // success is older than 15 minutes, honoring the user setting.
            if (!IsDemoMode && !DegradedReadOnly
                && (Bootstrap.Onboarding.PlaidConfigured || Bootstrap.Onboarding.SnapTradeConfigured)
                && Bootstrap.Preferences.SyncOnLaunch)
            {
                var stale = LastSyncedAt is null
                    || !DateTimeOffset.TryParse(LastSyncedAt, out var t)
                    || DateTimeOffset.UtcNow - t.ToUniversalTime() > TimeSpan.FromMinutes(15);
                if (stale) _ = TriggerSyncAsync("launch", force: false);
            }
            return true;
        }
        catch (Exception ex) when (ex is SidecarException or ApiException or HttpRequestException or TaskCanceledException)
        {
            incoming?.Dispose();
            _launcher = previousLauncher;
            Api = previousApi;
            AppLog.Error("AppSession", "Startup failed.", ex);
            StartupError = ex.Message;
            State = previousLauncher is { HasExited: false }
                ? SessionState.Ready
                : SessionState.FatalError;
            if (State != SessionState.Ready)
                LastSyncError = "SyncEngineUnreachable";
            return false;
        }
    }

    /// <summary>Switch between demo and real data. Mode is persisted only after the new engine is up.</summary>
    public async Task SwitchModeAsync(string mode)
    {
        if (mode == Settings.Mode && State == SessionState.Ready && _launcher is { HasExited: false })
            return;
        var previous = Settings.Mode;
        Settings.Mode = mode;
        if (await TryStartAsync())
        {
            Settings.Save();
            OnPropertyChanged(nameof(IsDemoMode));
            return;
        }

        Settings.Mode = previous;
        if (State != SessionState.Ready)
            await TryStartAsync();
        OnPropertyChanged(nameof(IsDemoMode));
        throw new InvalidOperationException(StartupError ?? "The local data engine did not start.");
    }

    /// <summary>Restarts the sidecar if the process exited while the window was hidden.</summary>
    public async Task EnsureEngineAsync()
    {
        if (State == SessionState.Ready && _launcher is { HasExited: false }) return;
        if (await TryStartAsync())
            NotifyDataRefreshed();
    }

    public async Task<bool> TriggerSyncAsync(string trigger = "manual", bool force = true)
    {
        if (Api is null || IsSyncing || IsDemoMode || DegradedReadOnly) return false;
        IsSyncing = true;
        LastSyncError = null;
        AppLog.Breadcrumb("Sync", $"Sync requested; trigger={trigger}, force={force}.");
        try
        {
            SyncJobDto? job = null;
            if (Bootstrap?.Onboarding.PlaidConfigured == true)
            {
                await Api.StartSyncAsync(trigger, force);
                // Poll until the Plaid job settles (SPEC §12.3).
                for (var i = 0; i < 120; i++)
                {
                    await Task.Delay(1000);
                    var status = await Api.GetSyncStatusAsync();
                    job = status.Job;
                    if (job is null || job.State != "running") break;
                }
            }
            // SnapTrade's daily cached reads do not call the paid manual-refresh
            // endpoint. Its response is committed atomically by the sidecar.
            if (Bootstrap?.Onboarding.SnapTradeConfigured == true)
            {
                var investment = await Api.StartInvestmentSyncAsync(
                    trigger == "reconnect" ? "portal-return" : trigger);
                for (var i = 0; i < 600 && investment.Status == "running"; i++)
                {
                    await Task.Delay(1000);
                    investment = (await Api.GetInvestmentSyncStatusAsync()).Run ?? investment;
                }
                if (investment.Status == "failed")
                {
                    AppLog.Error("Sync", $"SnapTrade sync failed; code={investment.ErrorCode ?? "unknown"}.");
                    LastSyncError = investment.ErrorCode == "NETWORK_UNAVAILABLE"
                        ? "SnapTradeUnreachable"
                        : "InvestmentSyncFailed";
                }
            }
            // Offline detection (SPEC §22.4): every attempted Plaid item failing
            // with a network error means the machine is offline, not the banks.
            // A SnapTrade-only outage is not "offline" — Plaid data still synced.
            var attempted = job?.Items.Where(i => i.Status is "succeeded" or "failed").ToList();
            IsOffline = attempted is { Count: > 0 } && attempted.All(i => i.ErrorCode == "NETWORK_UNAVAILABLE");
            if (IsOffline) LastSyncError ??= "OfflineStatus";

            Bootstrap = await Api.GetBootstrapAsync();
            LastSyncedAt = NewestTimestamp(Bootstrap.Sync.LastSuccessAt, Bootstrap.Sync.InvestmentLastSuccessAt);
            AttentionItemCount = Bootstrap.Sync.AttentionItemCount;
            DataRefreshed?.Invoke(this, EventArgs.Empty);
            AppLog.Info("Sync", $"Sync completed; attentionItems={AttentionItemCount}, offline={IsOffline}.");
            return !IsOffline;
        }
        catch (ApiException ex)
        {
            AppLog.Error("Sync", "The local data engine rejected the sync request.", ex);
            LastSyncError = "SyncRequestRejected";
            // NOT_CONFIGURED etc. — surfaced by the settings page; a failed manual
            // sync must never take down cached data (SPEC §22.4).
            return false;
        }
        catch (HttpRequestException ex)
        {
            AppLog.Error("Sync", "Network request failed.", ex);
            LastSyncError = "SyncEngineUnreachable";
            return false;
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>Reload local data without touching Plaid (F5, SPEC §14.3).</summary>
    public void NotifyDataRefreshed() => DataRefreshed?.Invoke(this, EventArgs.Empty);

    private static string? NewestTimestamp(string? left, string? right)
    {
        if (!DateTimeOffset.TryParse(left, out var a)) return right;
        if (!DateTimeOffset.TryParse(right, out var b)) return left;
        return a >= b ? left : right;
    }

    public async Task EnsureDailySyncScheduledAsync()
    {
        if (Api is null || IsDemoMode || DegradedReadOnly) return;
        try
        {
            var prefs = await Api.GetSettingsAsync();
            if (!prefs.DailySyncEnabled) return;
            var time = TimeSpan.TryParse(prefs.DailySyncTime + ":00", out var parsed)
                ? parsed
                : new TimeSpan(8, 0, 0);
            await DailySyncScheduler.RegisterAsync(time);
            AppLog.Info("Sync", $"Automatic daily sync registered for {time:hh\\:mm}.");
        }
        catch (Exception ex) when (ex is ApiException or InvalidOperationException)
        {
            AppLog.Error("Sync", "Automatic daily sync registration failed.", ex);
            try { await Api.PatchSettingsAsync(new { dailySyncEnabled = false }); } catch { }
        }
    }

    /// <summary>
    /// Persist Plaid credentials (Client ID + env in settings, Secret in
    /// Credential Manager) and push them to the running sidecar, which
    /// validates them against Plaid before accepting.
    /// </summary>
    public async Task<PlaidConfigStatusDto> SavePlaidConfigAsync(string environment, string clientId, string secret)
    {
        if (Api is null) throw new InvalidOperationException("Session not ready.");
        // Validate against Plaid first; only persist credentials that work.
        AppLog.Breadcrumb("Plaid", $"Validating credentials; environment={environment}.");
        var status = await Api.PutPlaidConfigAsync(environment, clientId, secret);
        Settings.PlaidClientId = clientId;
        Settings.PlaidEnvironment = environment;
        Settings.Save();
        WindowsCredentials.Save(WindowsCredentials.PlaidSecretTarget, secret);
        AppLog.Info("Plaid", $"Credentials validated and stored; environment={environment}, clientIdSuffix={status.ClientIdSuffix ?? "none"}.");
        return status;
    }

    public void ClearPlaidSecret()
    {
        WindowsCredentials.Delete(WindowsCredentials.PlaidSecretTarget);
        Settings.PlaidClientId = null;
        Settings.Save();
    }

    public async Task<SnapTradeConfigStatusDto> SaveSnapTradeConfigAsync(string clientId, string consumerKey)
    {
        if (Api is null) throw new InvalidOperationException("Session not ready.");
        AppLog.Breadcrumb("SnapTrade", "Validating Personal API Key credentials.");
        var status = await Api.PutSnapTradeConfigAsync(clientId, consumerKey);
        Settings.SnapTradeClientId = clientId;
        Settings.Save();
        WindowsCredentials.Save(WindowsCredentials.SnapTradeConsumerKeyTarget, consumerKey);
        AppLog.Info("SnapTrade",
            $"Credentials validated and stored; clientIdSuffix={status.ClientIdSuffix ?? "none"}.");
        Bootstrap = await Api.GetBootstrapAsync();
        return status;
    }

    public void ClearSnapTradeCredentials()
    {
        WindowsCredentials.Delete(WindowsCredentials.SnapTradeConsumerKeyTarget);
        Settings.SnapTradeClientId = null;
        Settings.Save();
    }

    public async Task ShutdownAsync()
    {
        AppLog.Breadcrumb("AppSession", "Shutdown requested.");
        if (Api is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Api.ShutdownAsync(cts.Token);
            Api.Dispose();
            Api = null;
        }
        ShutdownFast();
    }

    /// <summary>
    /// Tear down the sidecar immediately. Tray Exit and window close must not
    /// block the UI thread waiting for the Node process to flush.
    /// </summary>
    public void ShutdownFast()
    {
        try { Api?.Dispose(); } catch { }
        Api = null;
        _launcher?.Dispose();
        _launcher = null;
    }
}
