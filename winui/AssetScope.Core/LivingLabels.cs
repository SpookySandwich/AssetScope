using AssetScope.Core.Api;

namespace AssetScope.Core;

/// <summary>
/// The Settings card where the user says what a merchant is (rent, income, …)
/// and what a month of it costs. The sidecar owns the merchant list and every
/// classification; this decides which rows show and what an edit sends.
/// </summary>
public static class LivingLabels
{
    /// <summary>Picker order: bills, then income, then day-to-day spending.</summary>
    public static readonly IReadOnlyList<string> Kinds =
    [
        "rent", "utilities", "phone", "subscription", "tuition", "income",
        "grocery", "dining", "rideshare", "transfer", "other",
    ];

    public const int SuggestionLimit = 8;

    /// <summary>
    /// Rows always shown: merchants the user labelled, plus any picked from
    /// search or edited during this visit (the pinned keys).
    /// </summary>
    public static IReadOnlyList<LivingMerchantDto> Labelled(
        IEnumerable<LivingMerchantDto> merchants, IReadOnlySet<string> pinnedKeys)
    {
        ArgumentNullException.ThrowIfNull(merchants);
        ArgumentNullException.ThrowIfNull(pinnedKeys);
        return merchants.Where(m => m.Group == "labelled" || pinnedKeys.Contains(m.Key)).ToList();
    }

    /// <summary>
    /// Bills and income the sidecar recognised by itself. They already work,
    /// so the card shows them on demand.
    /// </summary>
    public static IReadOnlyList<LivingMerchantDto> Detected(
        IEnumerable<LivingMerchantDto> merchants, IReadOnlySet<string> pinnedKeys)
    {
        ArgumentNullException.ThrowIfNull(merchants);
        ArgumentNullException.ThrowIfNull(pinnedKeys);
        return merchants.Where(m => m.Group == "recurring" && !pinnedKeys.Contains(m.Key)).ToList();
    }

    /// <summary>
    /// Search results among every merchant not already in the labelled rows.
    /// The sidecar sorts by size, so the first matches are the largest charges.
    /// </summary>
    public static IReadOnlyList<LivingMerchantDto> Suggestions(
        IEnumerable<LivingMerchantDto> merchants, IReadOnlySet<string> pinnedKeys, string? query)
    {
        ArgumentNullException.ThrowIfNull(merchants);
        ArgumentNullException.ThrowIfNull(pinnedKeys);
        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0) return [];
        return merchants
            .Where(m => m.Group != "labelled" && !pinnedKeys.Contains(m.Key))
            .Where(m => m.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || m.Key.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .Take(SuggestionLimit)
            .ToList();
    }

    /// <summary>Request body for a kind change; null hands the merchant back to automatic.</summary>
    public static Dictionary<string, object?> KindPatch(string? kind) =>
        new() { ["kind"] = string.IsNullOrEmpty(kind) ? null : kind };

    /// <summary>
    /// Request body for a monthly-amount edit, or null when the box still
    /// shows the saved value. Empty text clears the amount; the sidecar
    /// validates anything else.
    /// </summary>
    public static Dictionary<string, object?>? AmountPatch(string? text, string? savedAmount)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed == (savedAmount ?? string.Empty)) return null;
        return new() { ["monthlyAmount"] = trimmed.Length == 0 ? null : trimmed };
    }

    /// <summary>The merchant as it reads after a saved edit, so the next edit compares against it.</summary>
    public static LivingMerchantDto Apply(LivingMerchantDto merchant, IReadOnlyDictionary<string, object?> patch)
    {
        ArgumentNullException.ThrowIfNull(merchant);
        ArgumentNullException.ThrowIfNull(patch);
        var updated = merchant;
        if (patch.TryGetValue("kind", out var kind))
        {
            var label = kind as string;
            updated = updated with { LabelledKind = label, Kind = label ?? merchant.AutomaticKind };
        }
        if (patch.TryGetValue("monthlyAmount", out var amount))
            updated = updated with { MonthlyAmount = amount as string };
        return updated;
    }
}
