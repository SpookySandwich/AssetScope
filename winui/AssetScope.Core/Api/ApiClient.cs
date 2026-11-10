using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AssetScope.Core.Api;

/// <summary>
/// Typed client for the sidecar /v1 API. One long-lived HttpClient; every call
/// unwraps the response envelope and surfaces failures as ApiException.
/// The WinUI layer never touches SQLite or raw JSON (SPEC §5.2).
/// </summary>
public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public ApiClient(int port, string sessionToken)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
    }

    private async Task<T> UnwrapAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        ApiEnvelope<T>? envelope;
        try
        {
            envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(JsonOpts, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new ApiException("PROTOCOL_ERROR", "The local data engine returned an unreadable response.", true, null);
        }
        if (envelope?.Error is { } err)
            throw new ApiException(err.Code, err.Message, err.Retryable, envelope.Meta?.RequestId);
        if (envelope is null || envelope.Data is null)
            throw new ApiException("PROTOCOL_ERROR", "Empty response from the local data engine.", true, envelope?.Meta?.RequestId);
        return envelope.Data;
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        return await UnwrapAsync<T>(response, ct).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOpts);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return await UnwrapAsync<T>(response, ct).ConfigureAwait(false);
    }

    // ── System ──
    public Task<HealthDto> GetHealthAsync(CancellationToken ct = default) =>
        GetAsync<HealthDto>("v1/health", ct);

    public Task<BootstrapDto> GetBootstrapAsync(CancellationToken ct = default) =>
        GetAsync<BootstrapDto>("v1/bootstrap", ct);

    public Task<DiagnosticsSummaryDto> GetDiagnosticsAsync(CancellationToken ct = default) =>
        GetAsync<DiagnosticsSummaryDto>("v1/diagnostics/summary", ct);

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        try { await SendAsync<JsonElement>(HttpMethod.Post, "v1/shutdown", null, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ApiException) { /* already gone */ }
    }

    // ── Dashboard ──
    public Task<DashboardDto> GetDashboardAsync(string range, CancellationToken ct = default) =>
        GetAsync<DashboardDto>($"v1/dashboard?range={Uri.EscapeDataString(range)}", ct);

    public Task<HistoryResponseDto> GetNetWorthHistoryAsync(string range, CancellationToken ct = default) =>
        GetAsync<HistoryResponseDto>($"v1/net-worth/history?range={Uri.EscapeDataString(range)}", ct);

    public Task<AllocationDto> GetAllocationAsync(CancellationToken ct = default) =>
        GetAsync<AllocationDto>("v1/allocation", ct);

    // ── Accounts ──
    public Task<AccountsResponseDto> GetAccountsAsync(
        string? category = null, string? search = null, bool includeExcluded = true, CancellationToken ct = default)
    {
        var query = $"v1/accounts?includeExcluded={(includeExcluded ? "true" : "false")}";
        if (!string.IsNullOrEmpty(category)) query += $"&category={Uri.EscapeDataString(category)}";
        if (!string.IsNullOrEmpty(search)) query += $"&search={Uri.EscapeDataString(search)}";
        return GetAsync<AccountsResponseDto>(query, ct);
    }

    public Task<AccountDetailDto> GetAccountAsync(string accountId, CancellationToken ct = default) =>
        GetAsync<AccountDetailDto>($"v1/accounts/{Uri.EscapeDataString(accountId)}", ct);

    public Task<JsonElement> PatchAccountAsync(string accountId, object patch, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Patch, $"v1/accounts/{Uri.EscapeDataString(accountId)}", patch, ct);

    public Task<CreateManualAccountResponse> CreateManualAccountAsync(object input, CancellationToken ct = default) =>
        SendAsync<CreateManualAccountResponse>(HttpMethod.Post, "v1/accounts/manual", input, ct);

    public Task<JsonElement> UpdateManualValueAsync(string accountId, string amount, string? effectiveDate, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Patch, $"v1/accounts/{Uri.EscapeDataString(accountId)}/value",
            new { amount, effectiveDate }, ct);

    public Task<DeleteAccountResponse> DeleteManualAccountAsync(string accountId, CancellationToken ct = default) =>
        SendAsync<DeleteAccountResponse>(HttpMethod.Delete, $"v1/accounts/{Uri.EscapeDataString(accountId)}", null, ct);

    public Task<JsonElement> SetArchivedAsync(string accountId, bool archived, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Post,
            $"v1/accounts/{Uri.EscapeDataString(accountId)}/{(archived ? "archive" : "unarchive")}", null, ct);

    // ── Sync ──
    public Task<SyncJobDto> StartSyncAsync(string trigger, bool force, CancellationToken ct = default) =>
        SendAsync<SyncJobDto>(HttpMethod.Post, "v1/sync", new { trigger, force }, ct);

    public Task<SyncStatusDto> GetSyncStatusAsync(CancellationToken ct = default) =>
        GetAsync<SyncStatusDto>("v1/sync/status", ct);

    // ── Plaid ──
    public Task<PlaidConfigStatusDto> GetPlaidConfigStatusAsync(CancellationToken ct = default) =>
        GetAsync<PlaidConfigStatusDto>("v1/plaid/config-status", ct);

    public Task<PlaidConfigStatusDto> PutPlaidConfigAsync(
        string environment, string clientId, string secret, CancellationToken ct = default) =>
        SendAsync<PlaidConfigStatusDto>(HttpMethod.Put, "v1/plaid/config",
            new { environment, clientId, secret }, ct);

    public Task<PlaidItemsResponseDto> GetPlaidItemsAsync(CancellationToken ct = default) =>
        GetAsync<PlaidItemsResponseDto>("v1/plaid/items", ct);

    // ── SnapTrade / investments ──
    public Task<SnapTradeConfigStatusDto> GetSnapTradeConfigStatusAsync(CancellationToken ct = default) =>
        GetAsync<SnapTradeConfigStatusDto>("v1/snaptrade/config-status", ct);

    public Task<SnapTradeConfigStatusDto> PutSnapTradeConfigAsync(
        string clientId, string consumerKey, CancellationToken ct = default) =>
        SendAsync<SnapTradeConfigStatusDto>(HttpMethod.Put, "v1/snaptrade/config",
            new { clientId, consumerKey }, ct);

    public Task<SnapTradePortalDto> CreateSnapTradePortalAsync(
        string? reconnectConnectionId = null, CancellationToken ct = default) =>
        SendAsync<SnapTradePortalDto>(HttpMethod.Post, "v1/snaptrade/portal",
            new { reconnectConnectionId, darkMode = false }, ct);

    public Task<SnapTradeConnectionsDto> GetSnapTradeConnectionsAsync(CancellationToken ct = default) =>
        GetAsync<SnapTradeConnectionsDto>("v1/snaptrade/connections", ct);

    public Task<SnapTradePortalDto> ReconnectSnapTradeAsync(string connectionId, CancellationToken ct = default) =>
        SendAsync<SnapTradePortalDto>(HttpMethod.Post,
            $"v1/snaptrade/connections/{Uri.EscapeDataString(connectionId)}/reconnect",
            new { darkMode = false }, ct);

    public Task<InvestmentSyncRunDto> StartInvestmentSyncAsync(string trigger, CancellationToken ct = default) =>
        SendAsync<InvestmentSyncRunDto>(HttpMethod.Post, "v1/snaptrade/sync", new { trigger }, ct);

    public Task<InvestmentSyncStatusDto> GetInvestmentSyncStatusAsync(CancellationToken ct = default) =>
        GetAsync<InvestmentSyncStatusDto>("v1/snaptrade/sync/status", ct);

    public Task<InvestmentOverviewDto> GetInvestmentOverviewAsync(string range, CancellationToken ct = default) =>
        GetAsync<InvestmentOverviewDto>($"v1/investments/overview?range={Uri.EscapeDataString(range)}", ct);

    public Task<InvestmentPositionsResponseDto> GetInvestmentPositionsAsync(
        string accountId, CancellationToken ct = default) =>
        GetAsync<InvestmentPositionsResponseDto>(
            $"v1/investments/positions?accountId={Uri.EscapeDataString(accountId)}", ct);

    public Task<InvestmentActivitiesResponseDto> GetInvestmentActivitiesAsync(
        string? accountId = null, int limit = 100, CancellationToken ct = default) =>
        GetAsync<InvestmentActivitiesResponseDto>(InvestmentListPath("activities", accountId, limit), ct);

    public Task<InvestmentOrdersResponseDto> GetInvestmentOrdersAsync(
        string? accountId = null, int limit = 100, CancellationToken ct = default) =>
        GetAsync<InvestmentOrdersResponseDto>(InvestmentListPath("orders", accountId, limit), ct);

    public Task<InvestmentRealizedResponseDto> GetInvestmentRealizedAsync(
        string? accountId = null, int limit = 200, CancellationToken ct = default) =>
        GetAsync<InvestmentRealizedResponseDto>(InvestmentListPath("realized", accountId, limit), ct);

    private static string InvestmentListPath(string kind, string? accountId, int limit)
    {
        var path = $"v1/investments/{kind}?limit={Math.Clamp(limit, 1, 1000)}";
        return string.IsNullOrWhiteSpace(accountId)
            ? path
            : $"{path}&accountId={Uri.EscapeDataString(accountId)}";
    }

    public Task<LinkSessionDto> CreateLinkSessionAsync(CancellationToken ct = default) =>
        SendAsync<LinkSessionDto>(HttpMethod.Post, "v1/plaid/link-sessions", new { }, ct);

    public Task<LinkSessionDto> GetLinkSessionAsync(string sessionId, CancellationToken ct = default) =>
        GetAsync<LinkSessionDto>($"v1/plaid/link-sessions/{Uri.EscapeDataString(sessionId)}", ct);

    public Task<LinkSessionDto> CancelLinkSessionAsync(string sessionId, CancellationToken ct = default) =>
        SendAsync<LinkSessionDto>(HttpMethod.Post, $"v1/plaid/link-sessions/{Uri.EscapeDataString(sessionId)}/cancel", null, ct);

    public Task<LinkSessionDto> ReconnectItemAsync(string itemId, CancellationToken ct = default) =>
        SendAsync<LinkSessionDto>(HttpMethod.Post, $"v1/plaid/items/{Uri.EscapeDataString(itemId)}/reconnect", null, ct);

    public Task<JsonElement> DisconnectItemAsync(string itemId, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, $"v1/plaid/items/{Uri.EscapeDataString(itemId)}/disconnect", null, ct);

    public Task<DeleteItemDataResponseDto> DeleteItemDataAsync(string itemId, bool confirm, CancellationToken ct = default) =>
        SendAsync<DeleteItemDataResponseDto>(HttpMethod.Delete,
            $"v1/plaid/items/{Uri.EscapeDataString(itemId)}/data?confirm={(confirm ? "true" : "false")}", null, ct);

    // ── Trends / backups / exports / logs ──
    public Task<TrendsDto> GetTrendsAsync(string range, CancellationToken ct = default) =>
        GetAsync<TrendsDto>($"v1/trends?range={Uri.EscapeDataString(range)}", ct);

    public Task<InsightsResponseDto> GetInsightsAsync(string range, int limit = 3, CancellationToken ct = default) =>
        GetAsync<InsightsResponseDto>(
            $"v1/insights?range={Uri.EscapeDataString(range)}&limit={Math.Clamp(limit, 1, 10)}", ct);

    public Task<BackupsResponseDto> GetBackupsAsync(CancellationToken ct = default) =>
        GetAsync<BackupsResponseDto>("v1/backups", ct);

    public Task<BackupDto> CreateBackupAsync(CancellationToken ct = default) =>
        SendAsync<BackupDto>(HttpMethod.Post, "v1/backups", null, ct);

    public Task<RestoreResponseDto> RestoreBackupAsync(string backupId, CancellationToken ct = default) =>
        SendAsync<RestoreResponseDto>(HttpMethod.Post, $"v1/backups/{Uri.EscapeDataString(backupId)}/restore", null, ct);

    public Task<ExportResultDto> ExportAccountsCsvAsync(CancellationToken ct = default) =>
        SendAsync<ExportResultDto>(HttpMethod.Post, "v1/exports/accounts.csv", null, ct);

    public Task<ExportResultDto> ExportNetWorthCsvAsync(string range, CancellationToken ct = default) =>
        SendAsync<ExportResultDto>(HttpMethod.Post, $"v1/exports/net-worth.csv?range={Uri.EscapeDataString(range)}", null, ct);

    public Task<LogsResponseDto> GetLogsAsync(int limit = 50, CancellationToken ct = default) =>
        GetAsync<LogsResponseDto>($"v1/diagnostics/logs?limit={limit}", ct);

    public Task<JsonElement> ClearLogsAsync(CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, "v1/diagnostics/logs/clear", null, ct);

    public Task<JsonElement> DeleteAllDataAsync(CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Delete, "v1/data?confirm=true", null, ct);

    public Task<ImportEnvResponseDto> ImportEnvCredentialsAsync(CancellationToken ct = default) =>
        SendAsync<ImportEnvResponseDto>(HttpMethod.Post, "v1/plaid/config/import-env", null, ct);

    // ── Settings ──
    public Task<PreferencesDto> GetSettingsAsync(CancellationToken ct = default) =>
        GetAsync<PreferencesDto>("v1/settings", ct);

    public Task<JsonElement> PatchSettingsAsync(object patch, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Patch, "v1/settings", patch, ct);

    public Task<LivingOverviewDto> GetLivingAsync(int? diningMealsPerWeek = null, CancellationToken ct = default)
    {
        var path = diningMealsPerWeek is null
            ? "v1/living"
            : $"v1/living?diningMealsPerWeek={diningMealsPerWeek.Value}";
        return GetAsync<LivingOverviewDto>(path, ct);
    }

    public Task<TransactionsResponseDto> GetTransactionsAsync(
        string? from = null, string? to = null, int limit = 100,
        string? accountId = null, string? category = null, CancellationToken ct = default)
    {
        var query = $"v1/transactions?limit={Math.Clamp(limit, 1, 1000)}";
        if (!string.IsNullOrEmpty(from)) query += $"&from={Uri.EscapeDataString(from)}";
        if (!string.IsNullOrEmpty(to)) query += $"&to={Uri.EscapeDataString(to)}";
        if (!string.IsNullOrEmpty(accountId)) query += $"&accountId={Uri.EscapeDataString(accountId)}";
        if (!string.IsNullOrEmpty(category)) query += $"&category={Uri.EscapeDataString(category)}";
        return GetAsync<TransactionsResponseDto>(query, ct);
    }

    public Task<TransactionsSummaryDto> GetTransactionsSummaryAsync(
        string from, string to, string? accountId = null, CancellationToken ct = default)
    {
        var query = $"v1/transactions/summary?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}";
        if (!string.IsNullOrEmpty(accountId)) query += $"&accountId={Uri.EscapeDataString(accountId)}";
        return GetAsync<TransactionsSummaryDto>(query, ct);
    }

    public Task<JsonElement> PatchTransactionAsync(string transactionId, object patch, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Patch, $"v1/transactions/{Uri.EscapeDataString(transactionId)}", patch, ct);

    public Task<JsonElement> PutLivingPreferencesAsync(object preferences, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Put, "v1/living/preferences", preferences, ct);

    public Task<JsonElement> CreateLivingObligationAsync(object input, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, "v1/living/obligations", input, ct);

    public Task<JsonElement> PatchLivingObligationAsync(string obligationId, object patch, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Patch,
            $"v1/living/obligations/{Uri.EscapeDataString(obligationId)}", patch, ct);

    public Task<JsonElement> DeleteLivingObligationAsync(string obligationId, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Delete,
            $"v1/living/obligations/{Uri.EscapeDataString(obligationId)}", null, ct);

    public Task<JsonElement> PutLivingRouteAlternativeAsync(object input, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Put, "v1/living/routes/alternative", input, ct);

    // ── Card rewards ──
    public Task<RewardsOverviewDto> GetRewardsAsync(string range = "3m", CancellationToken ct = default) =>
        GetAsync<RewardsOverviewDto>($"v1/rewards?range={Uri.EscapeDataString(range)}", ct);

    public Task<JsonElement> PutRewardCardAsync(string accountId, object input, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Put, $"v1/rewards/cards/{Uri.EscapeDataString(accountId)}", input, ct);

    public Task<JsonElement> PutRewardPreferencesAsync(object input, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Put, "v1/rewards/preferences", input, ct);

    public Task<LivingMerchantsDto> GetLivingMerchantsAsync(CancellationToken ct = default) =>
        GetAsync<LivingMerchantsDto>("v1/living/merchants", ct);

    public Task<JsonElement> PutLivingMerchantDefaultAsync(string merchantKey, object input, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Put,
            $"v1/living/merchant-defaults/{Uri.EscapeDataString(merchantKey)}", input, ct);

    public void Dispose()
    {
        _http.Dispose();
    }
}
