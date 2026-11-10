using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using AssetScope.Core.Diagnostics;
using AssetScope.Core.Localization;

namespace AssetScope.App.Services;

/// <summary>Presentation lookups shared across pages: category glyphs, brushes, labels, times.</summary>
public static class Display
{
    private static readonly ResourceLoader Loader = new();

    public static string S(string key)
    {
        // x:Uid entries live in property form ("Key/Text"); code-only strings in
        // plain form. Try plain, then the common property forms, so each string
        // needs exactly one .resw entry.
        return ResourceLookup.Resolve(key, Loader.GetString,
            missing => AppLog.Warn("Localization", $"Missing resource key: {missing}."));
    }

    // Segoe Fluent Icons glyphs per account category.
    public static string CategoryGlyph(string category) => category switch
    {
        "bank" => "",        // Bank
        "cash" => "",        // Bank (same bucket)
        "investment" => "",  // PieSingle
        "retirement" => "",  // Clock (long horizon)
        "credit" => "",      // Payment card
        "loan" => "",        // Bank
        "property" => "",    // Home
        "vehicle" => "",     // Car
        _ => "",             // Folder
    };

    public static string CategoryBrushKey(string category) => category switch
    {
        "bank" or "cash" => "CategoryBrushBankCash",
        "investment" => "CategoryBrushInvestment",
        "retirement" => "CategoryBrushRetirement",
        "property" => "CategoryBrushProperty",
        "vehicle" => "CategoryBrushVehicle",
        "credit" or "loan" => "CategoryBrushLiability",
        _ => "CategoryBrushOther",
    };

    public static Brush CategoryBrush(string category) =>
        (Brush)Application.Current.Resources[CategoryBrushKey(category)];

    public static Brush BucketBrush(string bucket) =>
        (Brush)Application.Current.Resources[bucket switch
        {
            "bankCash" => "CategoryBrushBankCash",
            "investment" => "CategoryBrushInvestment",
            "retirement" => "CategoryBrushRetirement",
            "property" => "CategoryBrushProperty",
            "vehicle" => "CategoryBrushVehicle",
            "Stocks" => "CategoryBrushBankCash",
            "ETFs" => "CategoryBrushInvestment",
            "Cash" => "CategoryBrushProperty",
            "Crypto" => "CategoryBrushRetirement",
            "MutualFunds" => "CategoryBrushVehicle",
            "Unclassified" or "Other" => "CategoryBrushOther",
            "liability" => "CategoryBrushLiability",
            _ => "CategoryBrushOther",
        }];

    /// <summary>
    /// Stable portfolio palette used by every security-allocation surface.
    /// Colors describe the instrument type rather than its current sort order,
    /// so ETF never silently changes from blue to green when balances move.
    /// </summary>
    public static Brush PortfolioBucketBrush(string bucket) =>
        (Brush)Application.Current.Resources[NormalizePortfolioBucket(bucket) switch
        {
            "ETFs" => "ReferenceBlueBrush",
            "Stocks" => "ReferenceGreenBrush",
            "Cash" => "ReferencePurpleBrush",
            "MutualFunds" => "CategoryBrushProperty",
            "Crypto" => "CategoryBrushVehicle",
            _ => "CategoryBrushOther",
        }];

    public static string NormalizePortfolioBucket(string bucket) => bucket.Trim().ToLowerInvariant() switch
    {
        "stock" or "stocks" or "adr" => "Stocks",
        "etf" or "etfs" or "cef" => "ETFs",
        "cash" => "Cash",
        "crypto" or "cryptocurrency" => "Crypto",
        "mutualfund" or "mutual fund" or "mutual funds" => "MutualFunds",
        "unclassified" => "Unclassified",
        _ => "Other",
    };

    public static string CategoryLabel(string category) => S($"Category_{category}");

    public static string SpendingCategoryLabel(string category) => category.Trim().ToUpperInvariant() switch
    {
        "BANK_FEES" => S("SpendingCategoryBankFees"),
        "ENTERTAINMENT" => S("SpendingCategoryEntertainment"),
        "FOOD_AND_DRINK" => S("SpendingCategoryFoodDrink"),
        "GENERAL_MERCHANDISE" => S("SpendingCategoryMerchandise"),
        "GENERAL_SERVICES" => S("SpendingCategoryServices"),
        "HOME_IMPROVEMENT" => S("SpendingCategoryHome"),
        "MEDICAL" => S("SpendingCategoryMedical"),
        "PERSONAL_CARE" => S("SpendingCategoryPersonalCare"),
        "RENT_AND_UTILITIES" => S("SpendingCategoryRentUtilities"),
        "TRANSPORTATION" => S("SpendingCategoryTransportation"),
        "TRAVEL" => S("SpendingCategoryTravel"),
        "INCOME" => S("SpendingCategoryIncome"),
        "TRANSFER_IN" => S("SpendingCategoryTransferIn"),
        "TRANSFER_OUT" => S("SpendingCategoryTransferOut"),
        "TRANSFER" => S("SpendingCategoryTransfer"),
        "LOAN_PAYMENTS" => S("SpendingCategoryLoanPayments"),
        "GOVERNMENT_AND_NON_PROFIT" => S("SpendingCategoryGovernment"),
        "" or "OTHER" => S("SpendingCategoryOther"),
        _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
            category.Replace('_', ' ').ToLowerInvariant()),
    };

    /// <summary>True for categories that move money between the user's own accounts or pay down debt.</summary>
    public static bool IsTransferCategory(string category) =>
        category.Trim().ToUpperInvariant() is "TRANSFER_IN" or "TRANSFER_OUT" or "TRANSFER" or "LOAN_PAYMENTS";

    private static readonly System.Text.RegularExpressions.Regex AchReference = new(
        @"\s*\b(PPD|CCD|WEB|ARC|TEL|POS|IAT)\s*ID:?\s*\S+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Merchant name when the provider supplied one, otherwise the statement
    /// text without the per-payment ACH reference ("PPD ID: 1000000001") that
    /// makes every stipend deposit look like a different payee.
    /// </summary>
    public static string TransactionTitle(string? merchantName, string name)
    {
        if (!string.IsNullOrWhiteSpace(merchantName)) return merchantName.Trim();
        var cleaned = AchReference.Replace(name, string.Empty).Trim();
        return cleaned.Length == 0 ? name.Trim() : cleaned;
    }

    /// <summary>"2026-09-02" as "Sep 2" in the current culture.</summary>
    public static string ShortDate(string ymd) =>
        DateTime.TryParseExact(ymd, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date)
            ? date.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture)
            : ymd;

    /// <summary>What a charge counts as on the Living page and in its Settings card.</summary>
    public static string LivingKindLabel(string? kind) => kind switch
    {
        "rent" => S("LivingKindRent"),
        "utilities" => S("LivingKindUtilities"),
        "phone" => S("LivingKindPhone"),
        "subscription" => S("LivingKindSubscription"),
        "tuition" => S("LivingKindTuition"),
        "dining" => S("LivingKindDining"),
        "grocery" => S("LivingKindGrocery"),
        "rideshare" => S("LivingKindRideshare"),
        "income" => S("LivingIncomeLabel"),
        "transfer" => S("LivingKindTransfer"),
        _ => S("LivingKindOther"),
    };

    public static string BucketLabel(string bucket) =>
        S($"Bucket_{(bucket.Equals("other", StringComparison.OrdinalIgnoreCase) ? "Other" : bucket)}");

    public static string FreshnessLabel(string freshness) => S($"Freshness_{freshness}");

    /// <summary>"2 min ago" / "3 hours ago" from an RFC3339 timestamp.</summary>
    public static string RelativeTime(string? iso)
    {
        if (iso is null) return S("Time_never");
        if (!DateTimeOffset.TryParse(iso, out var t)) return iso;
        var age = DateTimeOffset.UtcNow - t.ToUniversalTime();
        if (age < TimeSpan.FromMinutes(1)) return S("Time_justNow");
        if (age < TimeSpan.FromHours(1)) return string.Format(S("Time_minutesAgo"), (int)age.TotalMinutes);
        if (age < TimeSpan.FromHours(24)) return string.Format(S("Time_hoursAgo"), (int)age.TotalHours);
        return string.Format(S("Time_daysAgo"), (int)age.TotalDays);
    }

    /// <summary>Comparison label per SPEC §9.7: "vs. yesterday" / "vs. previous record".</summary>
    public static string ComparisonLabel(string? comparison) => comparison switch
    {
        "yesterday" => S("Comparison_yesterday"),
        "previousRecord" => S("Comparison_previousRecord"),
        _ => string.Empty,
    };
}
