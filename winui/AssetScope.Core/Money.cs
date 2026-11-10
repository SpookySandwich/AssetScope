using System.Globalization;

namespace AssetScope.Core;

/// <summary>
/// Money display rules (SPEC §9.1, §14.4): amounts arrive as fixed decimal
/// strings, are handled as decimal (never double), and render as US dollars.
/// Privacy Mode replaces every amount with a mask.
/// </summary>
public static class Money
{
    public const string PrivacyMask = "••••••";

    public static decimal Parse(string amount) =>
        decimal.Parse(amount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    public static bool TryParse(string? amount, out decimal value) =>
        decimal.TryParse(amount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    /// <summary>"$28,410.45" (or "$28,410" without decimals). Sign preserved.</summary>
    public static string Format(decimal value, bool decimals = true, bool privacy = false)
    {
        if (privacy) return PrivacyMask;
        var format = decimals ? "C2" : "C0";
        return value.ToString(format, CultureInfo.GetCultureInfo("en-US"));
    }

    public static string Format(string amount, bool decimals = true, bool privacy = false) =>
        privacy ? PrivacyMask : Format(Parse(amount), decimals);

    /// <summary>"+$4,895.00" / "-$222.48" for change values.</summary>
    public static string FormatSigned(decimal value, bool decimals = true, bool privacy = false)
    {
        if (privacy) return PrivacyMask;
        var abs = Format(Math.Abs(value), decimals);
        return value >= 0 ? $"+{abs}" : $"-{abs}";
    }

    /// <summary>Compact axis label: "$260K", "$1.2M".</summary>
    public static string FormatCompact(decimal value, bool privacy = false)
    {
        if (privacy) return PrivacyMask;
        var abs = Math.Abs(value);
        string text = abs switch
        {
            >= 1_000_000_000m => $"${abs / 1_000_000_000m:0.#}B",
            >= 1_000_000m => $"${abs / 1_000_000m:0.#}M",
            >= 1_000m => $"${abs / 1_000m:0.#}K",
            _ => $"${abs:0}",
        };
        return value < 0 ? $"-{text}" : text;
    }

    /// <summary>"2.02%" from a ratio string like "0.0202"; empty when null.</summary>
    public static string FormatRatio(string? ratio, bool privacy = false)
    {
        if (ratio is null) return string.Empty;
        if (privacy) return string.Empty;
        if (!decimal.TryParse(ratio, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var r))
            return string.Empty;
        return $"{Math.Abs(r) * 100m:0.00}%";
    }
}
