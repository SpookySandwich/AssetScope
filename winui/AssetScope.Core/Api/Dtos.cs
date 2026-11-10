using System.Text.Json.Serialization;

namespace AssetScope.Core.Api;

// Typed mirrors of the sidecar /v1 DTOs. Monetary amounts are fixed-decimal
// strings on the wire (SPEC §9.1); Money.Parse turns them into decimal.

public sealed record MoneyDto(
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("currencyCode")] string CurrencyCode);

public sealed record ChangeDto(
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("ratio")] string? Ratio,
    [property: JsonPropertyName("comparison")] string Comparison,   // "yesterday" | "previousRecord"
    [property: JsonPropertyName("comparedTo")] string ComparedTo);

public sealed record BalanceDto(
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("currencyCode")] string CurrencyCode,
    [property: JsonPropertyName("asOfDate")] string AsOfDate);

public sealed record SummaryDto(
    [property: JsonPropertyName("assets")] MoneyDto Assets,
    [property: JsonPropertyName("liabilities")] MoneyDto Liabilities,
    [property: JsonPropertyName("netWorth")] MoneyDto NetWorth,
    [property: JsonPropertyName("change")] ChangeDto? Change,
    [property: JsonPropertyName("asOfDate")] string? AsOfDate);

public sealed record HistoryPointDto(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("assets")] MoneyDto Assets,
    [property: JsonPropertyName("liabilities")] MoneyDto Liabilities,
    [property: JsonPropertyName("netWorth")] MoneyDto NetWorth,
    [property: JsonPropertyName("source")] string Source);

public sealed record SparkPointDto(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("amount")] string Amount);

public sealed record AccountSummaryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("institutionName")] string? InstitutionName,
    [property: JsonPropertyName("nickname")] string? Nickname,
    [property: JsonPropertyName("owner")] string? Owner,
    [property: JsonPropertyName("mask")] string? Mask,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("providerSources")] List<string> ProviderSources,
    [property: JsonPropertyName("accountClass")] string AccountClass,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("currencyCode")] string CurrencyCode,
    [property: JsonPropertyName("balance")] BalanceDto? Balance,
    [property: JsonPropertyName("change")] ChangeDto? Change,
    [property: JsonPropertyName("sparkline")] List<SparkPointDto> Sparkline,
    [property: JsonPropertyName("excluded")] bool Excluded,
    [property: JsonPropertyName("archived")] bool Archived,
    [property: JsonPropertyName("freshness")] string Freshness,
    [property: JsonPropertyName("lastSyncedAt")] string? LastSyncedAt,
    [property: JsonPropertyName("attention")] string? Attention);

public sealed record AllocationSliceDto(
    [property: JsonPropertyName("bucket")] string Bucket,
    [property: JsonPropertyName("amount")] MoneyDto Amount,
    [property: JsonPropertyName("share")] string? Share,
    [property: JsonPropertyName("accountIds")] List<string> AccountIds);

public sealed record AllocationDto(
    [property: JsonPropertyName("totalAssets")] MoneyDto TotalAssets,
    [property: JsonPropertyName("slices")] List<AllocationSliceDto> Slices);

public sealed record DataQualityDto(
    [property: JsonPropertyName("excludedAccountCount")] int ExcludedAccountCount,
    [property: JsonPropertyName("nonUsdAccountCount")] int NonUsdAccountCount,
    [property: JsonPropertyName("missingBalanceCount")] int MissingBalanceCount,
    [property: JsonPropertyName("staleAccountCount")] int StaleAccountCount,
    [property: JsonPropertyName("attentionItemCount")] int AttentionItemCount);

public sealed record DashboardDto(
    [property: JsonPropertyName("summary")] SummaryDto Summary,
    [property: JsonPropertyName("history")] List<HistoryPointDto> History,
    [property: JsonPropertyName("topAccounts")] List<AccountSummaryDto> TopAccounts,
    [property: JsonPropertyName("allocation")] AllocationDto Allocation,
    [property: JsonPropertyName("dataQuality")] DataQualityDto DataQuality);

public sealed record HealthDto(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("sidecarVersion")] string SidecarVersion,
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("mode")] string Mode);

public sealed record BootstrapAppDto(
    [property: JsonPropertyName("sidecarVersion")] string SidecarVersion,
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("dataDir")] string DataDir,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("migratedOnStartup")] bool MigratedOnStartup,
    [property: JsonPropertyName("degradedReadOnly")] bool DegradedReadOnly);

public sealed record ImportEnvResponseDto(
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("secret")] string Secret,
    [property: JsonPropertyName("environment")] string Environment);

public sealed record BootstrapOnboardingDto(
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("hasExistingData")] bool HasExistingData,
    [property: JsonPropertyName("plaidConfigured")] bool PlaidConfigured,
    [property: JsonPropertyName("snapTradeConfigured")] bool SnapTradeConfigured,
    [property: JsonPropertyName("accountCount")] int AccountCount,
    [property: JsonPropertyName("investmentAccountCount")] int InvestmentAccountCount);

public sealed record BootstrapSyncDto(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("lastSuccessAt")] string? LastSuccessAt,
    [property: JsonPropertyName("attentionItemCount")] int AttentionItemCount,
    [property: JsonPropertyName("investmentLastSuccessAt")] string? InvestmentLastSuccessAt);

public sealed record SnapTradeConfigStatusDto(
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("clientIdSuffix")] string? ClientIdSuffix,
    [property: JsonPropertyName("consumerKeyConfigured")] bool ConsumerKeyConfigured,
    [property: JsonPropertyName("authMode")] string AuthMode,
    [property: JsonPropertyName("canAccessHoldings")] bool CanAccessHoldings,
    [property: JsonPropertyName("canAccessAccountHistory")] bool CanAccessAccountHistory,
    [property: JsonPropertyName("partnerName")] string? PartnerName,
    [property: JsonPropertyName("mode")] string Mode);

public sealed record SnapTradePortalDto(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("expiresAt")] string ExpiresAt,
    [property: JsonPropertyName("mode")] string Mode);

public sealed record SnapTradeConnectionDto(
    [property: JsonPropertyName("connectionId")] string ConnectionId,
    [property: JsonPropertyName("brokerageSlug")] string? BrokerageSlug,
    [property: JsonPropertyName("brokerageName")] string BrokerageName,
    [property: JsonPropertyName("brokerageLogoUrl")] string? BrokerageLogoUrl,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("connectionType")] string ConnectionType,
    [property: JsonPropertyName("disabled")] bool Disabled,
    [property: JsonPropertyName("disabledAt")] string? DisabledAt,
    [property: JsonPropertyName("lastSuccessAt")] string? LastSuccessAt,
    [property: JsonPropertyName("lastErrorCode")] string? LastErrorCode,
    [property: JsonPropertyName("accountCount")] int AccountCount);

public sealed record SnapTradeConnectionsDto(
    [property: JsonPropertyName("connections")] List<SnapTradeConnectionDto> Connections);

public sealed record InvestmentSyncRunDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("trigger")] string Trigger,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("startedAt")] string StartedAt,
    [property: JsonPropertyName("completedAt")] string? CompletedAt,
    [property: JsonPropertyName("connectionCount")] int ConnectionCount,
    [property: JsonPropertyName("accountCount")] int AccountCount,
    [property: JsonPropertyName("positionCount")] int PositionCount,
    [property: JsonPropertyName("activityCount")] int ActivityCount,
    [property: JsonPropertyName("orderCount")] int OrderCount,
    [property: JsonPropertyName("errorCode")] string? ErrorCode);

public sealed record InvestmentSyncStatusDto(
    [property: JsonPropertyName("run")] InvestmentSyncRunDto? Run);

public sealed record InvestmentAccountDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("institutionName")] string? InstitutionName,
    [property: JsonPropertyName("mask")] string? Mask,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("balance")] BalanceDto Balance,
    [property: JsonPropertyName("change")] InvestmentChangeDto? Change,
    [property: JsonPropertyName("positionCount")] int PositionCount,
    [property: JsonPropertyName("lastSyncedAt")] string? LastSyncedAt);

public sealed record InvestmentChangeDto(
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("ratio")] string? Ratio);

public sealed record InvestmentPositionDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("accountName")] string AccountName,
    [property: JsonPropertyName("institutionName")] string? InstitutionName,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("instrumentType")] string InstrumentType,
    [property: JsonPropertyName("units")] string? Units,
    [property: JsonPropertyName("averagePrice")] string? AveragePrice,
    [property: JsonPropertyName("currentPrice")] string? CurrentPrice,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("gainLoss")] string? GainLoss,
    [property: JsonPropertyName("gainLossRatio")] string? GainLossRatio,
    [property: JsonPropertyName("currencyCode")] string CurrencyCode,
    [property: JsonPropertyName("cashEquivalent")] bool CashEquivalent,
    [property: JsonPropertyName("asOfAt")] string AsOfAt);

public sealed record InvestmentPositionsResponseDto(
    [property: JsonPropertyName("positions")] List<InvestmentPositionDto> Positions);

public sealed record InvestmentActivityDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("accountName")] string AccountName,
    [property: JsonPropertyName("institutionName")] string? InstitutionName,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("tradeDate")] string? TradeDate,
    [property: JsonPropertyName("settlementDate")] string? SettlementDate,
    [property: JsonPropertyName("symbol")] string? Symbol,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("units")] string? Units,
    [property: JsonPropertyName("price")] string? Price,
    [property: JsonPropertyName("amount")] string? Amount,
    [property: JsonPropertyName("fee")] string? Fee,
    [property: JsonPropertyName("currencyCode")] string CurrencyCode);

public sealed record InvestmentActivitiesResponseDto(
    [property: JsonPropertyName("activities")] List<InvestmentActivityDto> Activities);

public sealed record InvestmentOrderDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("accountName")] string AccountName,
    [property: JsonPropertyName("institutionName")] string? InstitutionName,
    [property: JsonPropertyName("brokerageOrderId")] string? BrokerageOrderId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("symbol")] string? Symbol,
    [property: JsonPropertyName("orderType")] string? OrderType,
    [property: JsonPropertyName("timeInForce")] string? TimeInForce,
    [property: JsonPropertyName("totalQuantity")] string? TotalQuantity,
    [property: JsonPropertyName("openQuantity")] string? OpenQuantity,
    [property: JsonPropertyName("filledQuantity")] string? FilledQuantity,
    [property: JsonPropertyName("executionPrice")] string? ExecutionPrice,
    [property: JsonPropertyName("limitPrice")] string? LimitPrice,
    [property: JsonPropertyName("stopPrice")] string? StopPrice,
    [property: JsonPropertyName("timePlaced")] string? TimePlaced,
    [property: JsonPropertyName("timeUpdated")] string? TimeUpdated,
    [property: JsonPropertyName("timeExecuted")] string? TimeExecuted,
    [property: JsonPropertyName("expiryDate")] string? ExpiryDate);

public sealed record InvestmentOrdersResponseDto(
    [property: JsonPropertyName("orders")] List<InvestmentOrderDto> Orders);

/// <summary>
/// One buy or sell from the realized profit-and-loss ledger. <c>Realized</c> and
/// <c>RealizedRatio</c> are populated on sells only; a buy opens a lot and has
/// nothing to realize yet.
/// </summary>
public sealed record InvestmentRealizedTradeDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("accountName")] string AccountName,
    [property: JsonPropertyName("institutionName")] string? InstitutionName,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("tradeDate")] string? TradeDate,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("units")] string Units,
    [property: JsonPropertyName("price")] string? Price,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("fee")] string Fee,
    [property: JsonPropertyName("costBasis")] string? CostBasis,
    [property: JsonPropertyName("realized")] string? Realized,
    [property: JsonPropertyName("realizedRatio")] string? RealizedRatio,
    /// <summary>False when the sold shares predate the cached activity history.</summary>
    [property: JsonPropertyName("basisComplete")] bool BasisComplete,
    [property: JsonPropertyName("currencyCode")] string CurrencyCode);

public sealed record InvestmentRealizedSummaryDto(
    [property: JsonPropertyName("total")] string Total,
    [property: JsonPropertyName("yearToDate")] string YearToDate,
    [property: JsonPropertyName("monthToDate")] string MonthToDate,
    [property: JsonPropertyName("buyCount")] int BuyCount,
    [property: JsonPropertyName("sellCount")] int SellCount,
    [property: JsonPropertyName("tradesMissingBasis")] int TradesMissingBasis,
    [property: JsonPropertyName("currencyCode")] string CurrencyCode);

public sealed record InvestmentRealizedResponseDto(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("summary")] InvestmentRealizedSummaryDto Summary,
    [property: JsonPropertyName("trades")] List<InvestmentRealizedTradeDto> Trades);

public sealed record InvestmentPerformancePointDto(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("gain")] string Gain,
    [property: JsonPropertyName("gainRatio")] string? GainRatio);

public sealed record InvestmentAllocationDto(
    [property: JsonPropertyName("bucket")] string Bucket,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("share")] string? Share);

public sealed record InvestmentOverviewDto(
    [property: JsonPropertyName("totalValue")] MoneyDto TotalValue,
    [property: JsonPropertyName("totalGainLoss")] InvestmentChangeDto TotalGainLoss,
    [property: JsonPropertyName("accounts")] List<InvestmentAccountDto> Accounts,
    [property: JsonPropertyName("positions")] List<InvestmentPositionDto> Positions,
    [property: JsonPropertyName("performance")] List<InvestmentPerformancePointDto> Performance,
    [property: JsonPropertyName("allocation")] List<InvestmentAllocationDto> Allocation);

public sealed record InsightPeriodDto(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("range")] string Range);

public sealed record InsightDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("tone")] string Tone,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("primaryAmount")] string? PrimaryAmount,
    [property: JsonPropertyName("secondaryAmount")] string? SecondaryAmount,
    [property: JsonPropertyName("tertiaryAmount")] string? TertiaryAmount,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("transactionCount")] int TransactionCount);

public sealed record InsightsResponseDto(
    [property: JsonPropertyName("period")] InsightPeriodDto Period,
    [property: JsonPropertyName("items")] List<InsightDto> Items);

public sealed record PreferencesDto(
    [property: JsonPropertyName("theme")] string Theme,
    [property: JsonPropertyName("syncOnLaunch")] bool SyncOnLaunch,
    [property: JsonPropertyName("dailySyncEnabled")] bool DailySyncEnabled,
    [property: JsonPropertyName("dailySyncTime")] string DailySyncTime,
    [property: JsonPropertyName("showDecimals")] bool ShowDecimals);

public sealed record BootstrapDto(
    [property: JsonPropertyName("app")] BootstrapAppDto App,
    [property: JsonPropertyName("onboarding")] BootstrapOnboardingDto Onboarding,
    [property: JsonPropertyName("sync")] BootstrapSyncDto Sync,
    [property: JsonPropertyName("preferences")] PreferencesDto Preferences);

public sealed record AccountsResponseDto(
    [property: JsonPropertyName("accounts")] List<AccountSummaryDto> Accounts);

public sealed record SnapshotDto(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("amount")] string Amount);

public sealed record AccountDetailDto(
    [property: JsonPropertyName("account")] AccountSummaryDto Account,
    [property: JsonPropertyName("recentSnapshots")] List<SnapshotDto> RecentSnapshots);

public sealed record HistoryResponseDto(
    [property: JsonPropertyName("points")] List<HistoryPointDto> Points);

public sealed record SyncProgressDto(
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("completedItems")] int CompletedItems,
    [property: JsonPropertyName("successfulItems")] int SuccessfulItems,
    [property: JsonPropertyName("failedItems")] int FailedItems);

public sealed record SyncItemDto(
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("institutionName")] string? InstitutionName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("errorCode")] string? ErrorCode);

public sealed record SyncJobDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("trigger")] string Trigger,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("startedAt")] string StartedAt,
    [property: JsonPropertyName("completedAt")] string? CompletedAt,
    [property: JsonPropertyName("progress")] SyncProgressDto Progress,
    [property: JsonPropertyName("items")] List<SyncItemDto> Items);

public sealed record SyncStatusDto(
    [property: JsonPropertyName("job")] SyncJobDto? Job);

public sealed record CreateManualAccountResponse(
    [property: JsonPropertyName("accountId")] string AccountId);

public sealed record DeleteAccountResponse(
    [property: JsonPropertyName("deleted")] bool Deleted,
    [property: JsonPropertyName("historyCount")] int HistoryCount);

public sealed record DiagnosticsSummaryDto(
    [property: JsonPropertyName("sidecarVersion")] string SidecarVersion,
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("nodeVersion")] string NodeVersion,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("dataDir")] string DataDir,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("recentRuns")] List<SyncRunDto> RecentRuns);

public sealed record PlaidConfigStatusDto(
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("clientIdSuffix")] string? ClientIdSuffix,
    [property: JsonPropertyName("secretConfigured")] bool SecretConfigured,
    [property: JsonPropertyName("envFileDetected")] bool EnvFileDetected,
    [property: JsonPropertyName("mode")] string Mode);

public sealed record ConnectedLinkItemDto(
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("institutionName")] string? InstitutionName,
    [property: JsonPropertyName("accountCount")] int AccountCount,
    [property: JsonPropertyName("existing")] bool Existing);

public sealed record LinkSessionDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("expiresAt")] string ExpiresAt,
    [property: JsonPropertyName("institutionName")] string? InstitutionName,
    [property: JsonPropertyName("itemId")] string? ItemId,
    [property: JsonPropertyName("connectedItems")] List<ConnectedLinkItemDto> ConnectedItems,
    [property: JsonPropertyName("failedItemCount")] int FailedItemCount,
    [property: JsonPropertyName("warning")] string? Warning,
    [property: JsonPropertyName("error")] string? Error);

public sealed record PlaidItemDto(
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("institutionName")] string? InstitutionName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("accountCount")] int AccountCount,
    [property: JsonPropertyName("lastSuccessAt")] string? LastSuccessAt,
    [property: JsonPropertyName("lastErrorCode")] string? LastErrorCode,
    [property: JsonPropertyName("disconnectedAt")] string? DisconnectedAt);

public sealed record PlaidItemsResponseDto(
    [property: JsonPropertyName("items")] List<PlaidItemDto> Items);

public sealed record DeleteItemDataPreviewDto(
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("institutionName")] string? InstitutionName,
    [property: JsonPropertyName("accountCount")] int AccountCount,
    [property: JsonPropertyName("transactionCount")] int TransactionCount,
    [property: JsonPropertyName("snapshotCount")] int SnapshotCount);

public sealed record DeleteItemDataResponseDto(
    [property: JsonPropertyName("deleted")] bool Deleted,
    [property: JsonPropertyName("preview")] DeleteItemDataPreviewDto Preview);

public sealed record CategorySeriesDto(
    [property: JsonPropertyName("bucket")] string Bucket,
    [property: JsonPropertyName("points")] List<SparkPointDto> Points);

public sealed record InstitutionSeriesDto(
    [property: JsonPropertyName("institution")] string Institution,
    [property: JsonPropertyName("points")] List<SparkPointDto> Points);

public sealed record TrendsDto(
    [property: JsonPropertyName("assetBuckets")] List<CategorySeriesDto> AssetBuckets,
    [property: JsonPropertyName("liabilities")] List<SparkPointDto> Liabilities,
    [property: JsonPropertyName("institutionSeries")] List<InstitutionSeriesDto> InstitutionSeries);

public sealed record BackupDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes);

public sealed record BackupsResponseDto(
    [property: JsonPropertyName("backups")] List<BackupDto> Backups);

public sealed record RestoreResponseDto(
    [property: JsonPropertyName("restored")] bool Restored,
    [property: JsonPropertyName("restartRequired")] bool RestartRequired,
    [property: JsonPropertyName("protectiveBackupId")] string ProtectiveBackupId);

public sealed record ExportResultDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("rows")] int Rows);

public sealed record LogEntryDto(
    [property: JsonPropertyName("at")] string At,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("message")] string Message);

public sealed record LogsResponseDto(
    [property: JsonPropertyName("entries")] List<LogEntryDto> Entries);

public sealed record SyncRunDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("trigger")] string Trigger,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("startedAt")] string? StartedAt,
    [property: JsonPropertyName("completedAt")] string? CompletedAt,
    [property: JsonPropertyName("itemCount")] int ItemCount,
    [property: JsonPropertyName("successCount")] int SuccessCount,
    [property: JsonPropertyName("failureCount")] int FailureCount);

public sealed record LivingPeriodDto(
    [property: JsonPropertyName("month")] string Month,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("today")] string Today,
    [property: JsonPropertyName("daysElapsed")] int DaysElapsed,
    [property: JsonPropertyName("daysInMonth")] int DaysInMonth,
    [property: JsonPropertyName("daysRemaining")] int DaysRemaining,
    [property: JsonPropertyName("reviewFrom")] string ReviewFrom,
    [property: JsonPropertyName("reviewTo")] string ReviewTo,
    [property: JsonPropertyName("usingPriorMonth")] bool UsingPriorMonth);

public sealed record LivingIncomeDto(
    [property: JsonPropertyName("posted")] string Posted,
    [property: JsonPropertyName("expectedRemaining")] string ExpectedRemaining,
    [property: JsonPropertyName("projected")] string Projected);

public sealed record LivingSplitDto(
    [property: JsonPropertyName("fixedPosted")] string FixedPosted,
    [property: JsonPropertyName("fixedRemaining")] string FixedRemaining,
    [property: JsonPropertyName("fixedProjected")] string FixedProjected,
    [property: JsonPropertyName("flexiblePosted")] string FlexiblePosted,
    [property: JsonPropertyName("flexibleProjected")] string FlexibleProjected,
    [property: JsonPropertyName("fixedShareOfIncome")] string? FixedShareOfIncome,
    [property: JsonPropertyName("flexibleShareOfIncome")] string? FlexibleShareOfIncome);

public sealed record LivingLeftoverDto(
    [property: JsonPropertyName("posted")] string Posted,
    [property: JsonPropertyName("projected")] string Projected);

public sealed record LivingMealsDto(
    [property: JsonPropertyName("diningTransactions")] int DiningTransactions,
    [property: JsonPropertyName("diningMeals")] int DiningMeals,
    [property: JsonPropertyName("diningSpend")] string DiningSpend,
    [property: JsonPropertyName("diningCostPerMeal")] string? DiningCostPerMeal,
    [property: JsonPropertyName("grocerySpend")] string GrocerySpend,
    [property: JsonPropertyName("cookedMeals")] int CookedMeals,
    [property: JsonPropertyName("cookingCostPerMeal")] string? CookingCostPerMeal,
    [property: JsonPropertyName("currentDiningMealsPerWeek")] string CurrentDiningMealsPerWeek);

public sealed record LivingDiningOutcomeDto(
    [property: JsonPropertyName("mealsPerWeek")] int MealsPerWeek,
    [property: JsonPropertyName("monthlyDining")] string MonthlyDining,
    [property: JsonPropertyName("monthlyCooking")] string? MonthlyCooking,
    [property: JsonPropertyName("monthlySavingsVsCurrent")] string MonthlySavingsVsCurrent,
    [property: JsonPropertyName("leftover")] string Leftover);

public sealed record LivingTradeoffDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("monthlyImpact")] string MonthlyImpact,
    [property: JsonPropertyName("yearlyImpact")] string YearlyImpact,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("extraCookedMealsPerWeek")] int? ExtraCookedMealsPerWeek,
    [property: JsonPropertyName("diningCostPerMeal")] string? DiningCostPerMeal,
    [property: JsonPropertyName("cookingCostPerMeal")] string? CookingCostPerMeal,
    [property: JsonPropertyName("origin")] string? Origin,
    [property: JsonPropertyName("destination")] string? Destination,
    [property: JsonPropertyName("rideCount")] int? RideCount,
    [property: JsonPropertyName("rideMonthlyCost")] string? RideMonthlyCost,
    [property: JsonPropertyName("alternativeLabel")] string? AlternativeLabel,
    [property: JsonPropertyName("alternativeMonthlyCost")] string? AlternativeMonthlyCost,
    [property: JsonPropertyName("baseline")] string? Baseline,
    [property: JsonPropertyName("current")] string? Current,
    [property: JsonPropertyName("fixedShareOfIncome")] string? FixedShareOfIncome,
    [property: JsonPropertyName("goalLabel")] string? GoalLabel,
    [property: JsonPropertyName("goalMonths")] int? GoalMonths,
    [property: JsonPropertyName("goalYearly")] string? GoalYearly);

public sealed record LivingRouteDto(
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("destination")] string Destination,
    [property: JsonPropertyName("rideCount")] int RideCount,
    [property: JsonPropertyName("monthlyCost")] string MonthlyCost,
    [property: JsonPropertyName("tripKind")] string? TripKind,
    [property: JsonPropertyName("alternativeLabel")] string? AlternativeLabel,
    [property: JsonPropertyName("alternativeMonthlyCost")] string? AlternativeMonthlyCost);

public sealed record LivingCategoryTrendDto(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("flexibility")] string Flexibility,
    [property: JsonPropertyName("currentMonth")] string CurrentMonth,
    [property: JsonPropertyName("baseline")] string? Baseline,
    [property: JsonPropertyName("drifted")] bool Drifted,
    [property: JsonPropertyName("points")] List<LivingMonthPointDto> Points);

public sealed record LivingMonthPointDto(
    [property: JsonPropertyName("month")] string Month,
    [property: JsonPropertyName("amount")] string Amount);

public sealed record LivingObligationDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("interval")] string Interval,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("monthlyAmount")] string MonthlyAmount);

public sealed record LivingPreferencesDto(
    [property: JsonPropertyName("diningMealsPerWeek")] int? DiningMealsPerWeek,
    [property: JsonPropertyName("goalKind")] string GoalKind,
    [property: JsonPropertyName("goalLabel")] string? GoalLabel,
    [property: JsonPropertyName("goalMonthlyContribution")] string? GoalMonthlyContribution,
    [property: JsonPropertyName("goalTargetAmount")] string? GoalTargetAmount);

public sealed record LivingDetectedDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("monthlyAmount")] string? MonthlyAmount,
    [property: JsonPropertyName("lastDate")] string LastDate,
    [property: JsonPropertyName("postedThisMonth")] bool PostedThisMonth,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("overridden")] bool Overridden);

/// <summary>
/// One merchant as the Settings label card sees it. <see cref="Group"/> is
/// "labelled", "recurring" or "other"; only "other" needs searching for.
/// </summary>
public sealed record LivingMerchantDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("automaticKind")] string AutomaticKind,
    [property: JsonPropertyName("labelledKind")] string? LabelledKind,
    [property: JsonPropertyName("monthlyAmount")] string? MonthlyAmount,
    [property: JsonPropertyName("lastDate")] string? LastDate,
    [property: JsonPropertyName("lastAmount")] string? LastAmount,
    [property: JsonPropertyName("group")] string Group);

public sealed record LivingMerchantsDto(
    [property: JsonPropertyName("merchants")] List<LivingMerchantDto> Merchants);

public sealed record LivingTransactionDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("accountName")] string AccountName,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("merchantName")] string? MerchantName,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("livingKind")] string LivingKind,
    [property: JsonPropertyName("partySize")] int? PartySize,
    [property: JsonPropertyName("effectivePartySize")] int EffectivePartySize,
    [property: JsonPropertyName("tripOrigin")] string? TripOrigin,
    [property: JsonPropertyName("tripDestination")] string? TripDestination,
    [property: JsonPropertyName("tripKind")] string? TripKind,
    [property: JsonPropertyName("needsAnnotation")] bool NeedsAnnotation);

public sealed record LivingTypicalCategoryDto(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("flexibility")] string Flexibility,
    [property: JsonPropertyName("monthlyAverage")] string MonthlyAverage,
    [property: JsonPropertyName("share")] string? Share,
    [property: JsonPropertyName("topMerchants")] List<string>? TopMerchants = null);

public sealed record LivingGoalDto(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("months")] int? Months,
    [property: JsonPropertyName("yearly")] string Yearly);

public sealed record LivingTypicalDto(
    [property: JsonPropertyName("months")] List<string> Months,
    [property: JsonPropertyName("income")] string Income,
    [property: JsonPropertyName("spend")] string Spend,
    [property: JsonPropertyName("fixed")] string Fixed,
    [property: JsonPropertyName("flexible")] string Flexible,
    [property: JsonPropertyName("leftover")] string Leftover,
    [property: JsonPropertyName("categories")] List<LivingTypicalCategoryDto> Categories,
    [property: JsonPropertyName("diningMealsPerWeek")] string? DiningMealsPerWeek,
    [property: JsonPropertyName("diningCostPerMeal")] string? DiningCostPerMeal,
    [property: JsonPropertyName("goal")] LivingGoalDto? Goal);

public sealed record LivingThisMonthDto(
    [property: JsonPropertyName("spent")] string Spent,
    [property: JsonPropertyName("expectedFixedRemaining")] string ExpectedFixedRemaining,
    [property: JsonPropertyName("flexibleExpectedByNow")] string FlexibleExpectedByNow,
    [property: JsonPropertyName("flexiblePace")] string? FlexiblePace,
    [property: JsonPropertyName("projectedLeftoverIfTypical")] string ProjectedLeftoverIfTypical);

public sealed record LivingRunwayDto(
    [property: JsonPropertyName("liquidCash")] string LiquidCash,
    [property: JsonPropertyName("months")] string? Months);

public sealed record LivingOverviewDto(
    [property: JsonPropertyName("period")] LivingPeriodDto Period,
    [property: JsonPropertyName("income")] LivingIncomeDto Income,
    [property: JsonPropertyName("split")] LivingSplitDto Split,
    [property: JsonPropertyName("leftover")] LivingLeftoverDto Leftover,
    [property: JsonPropertyName("hasHistory")] bool HasHistory,
    [property: JsonPropertyName("detected")] List<LivingDetectedDto> Detected,
    [property: JsonPropertyName("meals")] LivingMealsDto Meals,
    [property: JsonPropertyName("diningOutcomes")] List<LivingDiningOutcomeDto> DiningOutcomes,
    [property: JsonPropertyName("selectedDiningMealsPerWeek")] int SelectedDiningMealsPerWeek,
    [property: JsonPropertyName("tradeoffs")] List<LivingTradeoffDto> Tradeoffs,
    [property: JsonPropertyName("routes")] List<LivingRouteDto> Routes,
    [property: JsonPropertyName("categoryTrends")] List<LivingCategoryTrendDto> CategoryTrends,
    [property: JsonPropertyName("obligations")] List<LivingObligationDto> Obligations,
    [property: JsonPropertyName("preferences")] LivingPreferencesDto Preferences,
    [property: JsonPropertyName("transactions")] List<LivingTransactionDto> Transactions,
    [property: JsonPropertyName("typical")] LivingTypicalDto? Typical = null,
    [property: JsonPropertyName("thisMonth")] LivingThisMonthDto? ThisMonth = null,
    [property: JsonPropertyName("runway")] LivingRunwayDto? Runway = null);

public sealed record BankTransactionDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("accountName")] string AccountName,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("merchantName")] string? MerchantName,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("partySize")] int? PartySize,
    [property: JsonPropertyName("tripOrigin")] string? TripOrigin,
    [property: JsonPropertyName("tripDestination")] string? TripDestination,
    [property: JsonPropertyName("tripKind")] string? TripKind);

public sealed record TransactionsResponseDto(
    [property: JsonPropertyName("transactions")] List<BankTransactionDto> Transactions);

public sealed record CashFlowMonthDto(
    [property: JsonPropertyName("month")] string Month,
    [property: JsonPropertyName("moneyIn")] string MoneyIn,
    [property: JsonPropertyName("moneyOut")] string MoneyOut,
    [property: JsonPropertyName("net")] string Net);

public sealed record CashFlowTotalsDto(
    [property: JsonPropertyName("moneyIn")] string MoneyIn,
    [property: JsonPropertyName("moneyOut")] string MoneyOut,
    [property: JsonPropertyName("net")] string Net);

public sealed record CashFlowDto(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("months")] List<CashFlowMonthDto> Months,
    [property: JsonPropertyName("totals")] CashFlowTotalsDto Totals);

public sealed record CategorySpendingDto(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("transactionCount")] int TransactionCount);

public sealed record SpendingBreakdownDto(
    [property: JsonPropertyName("total")] string Total,
    [property: JsonPropertyName("categories")] List<CategorySpendingDto> Categories);

public sealed record TransactionsSummaryDto(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("transactionCount")] int TransactionCount,
    [property: JsonPropertyName("cashFlow")] CashFlowDto CashFlow,
    [property: JsonPropertyName("spending")] SpendingBreakdownDto Spending);

// ── Card rewards ──
// Rates are strings: "3" with unit "points" reads 3x, with "percent" reads 3%.
// effectiveRate is the value per dollar as a ratio ("0.0300" = 3%).

public sealed record RewardWindowDto(
    [property: JsonPropertyName("range")] string Range,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To);

public sealed record RewardPointValueDto(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("centsPerPoint")] string CentsPerPoint,
    [property: JsonPropertyName("defaultCentsPerPoint")] string DefaultCentsPerPoint,
    [property: JsonPropertyName("minimum")] string Minimum,
    [property: JsonPropertyName("maximum")] string Maximum);

public sealed record RewardProductDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("hasChoice")] bool HasChoice,
    [property: JsonPropertyName("hasRotating")] bool HasRotating);

public sealed record RewardBonusDto(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("rate")] string Rate);

/// <summary>kind: quarterlyCap (amount, scope) | onlineGroceryOnly | membershipRequired (membership) | portal (label, rate) | pooledPoints | bofaBoost | travelDiningRedemption.</summary>
public sealed record RewardNoteDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("amount")] string? Amount = null,
    [property: JsonPropertyName("scope")] string? Scope = null,
    [property: JsonPropertyName("membership")] string? Membership = null,
    [property: JsonPropertyName("label")] string? Label = null,
    [property: JsonPropertyName("rate")] string? Rate = null);

public sealed record RewardCardDto(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("institutionName")] string? InstitutionName,
    [property: JsonPropertyName("mask")] string? Mask,
    [property: JsonPropertyName("productId")] string? ProductId,
    [property: JsonPropertyName("productName")] string? ProductName,
    [property: JsonPropertyName("productSource")] string? ProductSource,
    [property: JsonPropertyName("choiceCategory")] string? ChoiceCategory,
    [property: JsonPropertyName("rotatingCategories")] List<string> RotatingCategories,
    /// <summary>"chosen" (the user's list) | "calendar" (the issuer's announced list) | null.</summary>
    [property: JsonPropertyName("rotatingSource")] string? RotatingSource,
    /// <summary>"activated" (the only one that earns the bonus) | "notActivated" | "unconfirmed" (the app can't tell) | null.</summary>
    [property: JsonPropertyName("rotatingStatus")] string? RotatingStatus,
    /// <summary>What each of this quarter's categories pays while activated and under the cap.</summary>
    [property: JsonPropertyName("rotatingRates")] List<RewardBonusDto> RotatingRates,
    [property: JsonPropertyName("rotatingActivateBy")] string? RotatingActivateBy,
    /// <summary>The user said they activated this quarter's categories with the issuer.</summary>
    [property: JsonPropertyName("rotatingActivated")] bool RotatingActivated,
    /// <summary>This quarter's picks with no spending category here, such as one store or a charity.</summary>
    [property: JsonPropertyName("rotatingUntracked")] List<string> RotatingUntracked,
    [property: JsonPropertyName("rotatingAnnouncement")] RewardAnnouncementDto? RotatingAnnouncement,
    [property: JsonPropertyName("rotatingNext")] RewardNextQuarterDto? RotatingNext,
    [property: JsonPropertyName("activation")] RewardActivationDto? Activation,
    /// <summary>This quarter's use of the shared cap, while a capped bonus is earning.</summary>
    [property: JsonPropertyName("capUsage")] RewardCapUsageDto? CapUsage,
    [property: JsonPropertyName("unit")] string? Unit,
    [property: JsonPropertyName("baseRate")] string? BaseRate,
    [property: JsonPropertyName("bonuses")] List<RewardBonusDto> Bonuses,
    [property: JsonPropertyName("notes")] List<RewardNoteDto> Notes,
    [property: JsonPropertyName("spend")] string Spend,
    [property: JsonPropertyName("earned")] string? Earned,
    [property: JsonPropertyName("transactionCount")] int TransactionCount,
    /// <summary>What a detected product was recognized from: "name" | "annualFee" | null.</summary>
    [property: JsonPropertyName("productDetectedBy")] string? ProductDetectedBy = null,
    /// <summary>For a card not identified yet, the products that fit what the bank reports, likeliest first.</summary>
    [property: JsonPropertyName("productCandidates")] List<string>? ProductCandidates = null);

/// <summary>The issuer's page for this quarter once posted. state: "read" | "unreadable".</summary>
public sealed record RewardAnnouncementDto(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("state")] string State);

/// <summary>Next quarter's list once named; open once its own page is up and activation has begun.</summary>
public sealed record RewardNextQuarterDto(
    [property: JsonPropertyName("quarter")] string Quarter,
    [property: JsonPropertyName("categories")] List<string> Categories,
    [property: JsonPropertyName("untracked")] List<string> Untracked,
    [property: JsonPropertyName("activateBy")] string ActivateBy,
    [property: JsonPropertyName("open")] bool Open,
    [property: JsonPropertyName("activated")] bool Activated);

/// <summary>Where to activate rotating categories, while a quarter is open for it and not yet marked activated.</summary>
public sealed record RewardActivationDto(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("quarter")] string Quarter,
    [property: JsonPropertyName("activateBy")] string ActivateBy);

public sealed record RewardCapUsageDto(
    [property: JsonPropertyName("cap")] string Cap,
    [property: JsonPropertyName("used")] string Used,
    [property: JsonPropertyName("remaining")] string Remaining);

public sealed record RewardCardRateDto(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("rate")] string Rate,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("effectiveRate")] string EffectiveRate);

public sealed record RewardPortalDto(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("rate")] string Rate,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("effectiveRate")] string EffectiveRate);

public sealed record RewardCategoryDto(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("spend")] string Spend,
    [property: JsonPropertyName("transactionCount")] int TransactionCount,
    [property: JsonPropertyName("best")] List<RewardCardRateDto> Best,
    [property: JsonPropertyName("runnerUp")] RewardCardRateDto? RunnerUp,
    [property: JsonPropertyName("portal")] RewardPortalDto? Portal,
    [property: JsonPropertyName("capWarning")] RewardCapWarningDto? CapWarning);

/// <summary>The best card's bonus stops at a quarterly cap this category's spending pace would pass.</summary>
public sealed record RewardCapWarningDto(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("cap")] string Cap,
    [property: JsonPropertyName("quarterlySpend")] string QuarterlySpend);

public sealed record RewardReviewCategoryDto(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("spend")] string Spend,
    [property: JsonPropertyName("earned")] string Earned,
    [property: JsonPropertyName("bestPossible")] string BestPossible,
    [property: JsonPropertyName("missed")] string Missed,
    [property: JsonPropertyName("mostUsedAccountId")] string? MostUsedAccountId,
    [property: JsonPropertyName("bestAccountId")] string? BestAccountId);

public sealed record RewardMissDto(
    [property: JsonPropertyName("transactionId")] string TransactionId,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("merchantName")] string? MerchantName,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("bestAccountId")] string BestAccountId,
    [property: JsonPropertyName("earned")] string Earned,
    [property: JsonPropertyName("bestPossible")] string BestPossible,
    [property: JsonPropertyName("missed")] string Missed);

public sealed record RewardReviewDto(
    [property: JsonPropertyName("spend")] string Spend,
    [property: JsonPropertyName("earned")] string Earned,
    [property: JsonPropertyName("bestPossible")] string BestPossible,
    [property: JsonPropertyName("missed")] string Missed,
    [property: JsonPropertyName("transactionCount")] int TransactionCount,
    [property: JsonPropertyName("unidentifiedSpend")] string UnidentifiedSpend,
    [property: JsonPropertyName("byCategory")] List<RewardReviewCategoryDto> ByCategory,
    [property: JsonPropertyName("topMisses")] List<RewardMissDto> TopMisses);

public sealed record RewardsOverviewDto(
    [property: JsonPropertyName("asOf")] string AsOf,
    [property: JsonPropertyName("quarter")] string Quarter,
    [property: JsonPropertyName("window")] RewardWindowDto Window,
    [property: JsonPropertyName("pointValues")] List<RewardPointValueDto> PointValues,
    [property: JsonPropertyName("products")] List<RewardProductDto> Products,
    [property: JsonPropertyName("choiceOptions")] List<string> ChoiceOptions,
    [property: JsonPropertyName("cards")] List<RewardCardDto> Cards,
    [property: JsonPropertyName("categories")] List<RewardCategoryDto> Categories,
    [property: JsonPropertyName("review")] RewardReviewDto Review);
