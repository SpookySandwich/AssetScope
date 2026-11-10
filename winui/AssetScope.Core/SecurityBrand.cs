namespace AssetScope.Core;

/// <summary>
/// Stable security brand identities used by investment surfaces. SnapTrade's
/// V2 position model intentionally exposes instrument metadata but no logo URL,
/// so recognized artwork is bundled locally and unknown symbols use a
/// deterministic monogram fallback.
/// </summary>
public enum SecurityBrand
{
    None,
    Vanguard,
    Invesco,
    Fidelity,
    Apple,
    Microsoft,
    Nvidia,
    Cash,
}

public static class SecurityBrands
{
    private static readonly HashSet<string> VanguardSymbols =
    [
        "BND", "BNDX", "VEA", "VO", "VOO", "VT", "VTI", "VTV", "VUG", "VWO", "VXUS",
    ];

    private static readonly HashSet<string> InvescoSymbols = ["QQQ", "QQQM"];

    public static SecurityBrand Match(string? symbol, string? name = null)
    {
        var normalizedSymbol = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        var suffix = normalizedSymbol.IndexOf('.');
        if (suffix > 0) normalizedSymbol = normalizedSymbol[..suffix];
        var normalizedName = new string((name ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

        if (normalizedSymbol is "USD" or "CASH" || normalizedName is "cash" or "cashusd")
            return SecurityBrand.Cash;
        if (normalizedSymbol == "AAPL" || normalizedName.Contains("appleinc", StringComparison.Ordinal))
            return SecurityBrand.Apple;
        if (normalizedSymbol == "MSFT" || normalizedName.Contains("microsoft", StringComparison.Ordinal))
            return SecurityBrand.Microsoft;
        if (normalizedSymbol == "NVDA" || normalizedName.Contains("nvidia", StringComparison.Ordinal))
            return SecurityBrand.Nvidia;
        if (VanguardSymbols.Contains(normalizedSymbol) || normalizedName.Contains("vanguard", StringComparison.Ordinal))
            return SecurityBrand.Vanguard;
        if (InvescoSymbols.Contains(normalizedSymbol) || normalizedName.Contains("invesco", StringComparison.Ordinal))
            return SecurityBrand.Invesco;
        if (normalizedName.Contains("fidelity", StringComparison.Ordinal)
            || normalizedSymbol is "FXAIX" or "FZROX" or "FSKAX" or "FNILX")
            return SecurityBrand.Fidelity;

        return SecurityBrand.None;
    }
}
