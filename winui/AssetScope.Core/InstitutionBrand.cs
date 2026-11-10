namespace AssetScope.Core;

/// <summary>
/// Stable institution identities used by the presentation layer. Matching is
/// deliberately centralized so every account surface chooses the same brand
/// asset and unknown institutions retain a category-icon fallback.
/// </summary>
public enum InstitutionBrand
{
    None,
    BankOfAmerica,
    Chase,
    Robinhood,
    XMoney,
}

public static class InstitutionBrands
{
    public static InstitutionBrand Match(string? institutionName)
    {
        if (string.IsNullOrWhiteSpace(institutionName)) return InstitutionBrand.None;

        var normalized = new string(institutionName
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

        if (normalized.Contains("bankofamerica", StringComparison.Ordinal)
            || normalized is "boa" or "bofa")
            return InstitutionBrand.BankOfAmerica;
        if (normalized.Contains("chase", StringComparison.Ordinal)
            || normalized.Contains("jpmorgan", StringComparison.Ordinal))
            return InstitutionBrand.Chase;
        if (normalized.Contains("robinhood", StringComparison.Ordinal))
            return InstitutionBrand.Robinhood;
        // X Money arrives through Plaid; the institution name is expected to be
        // "X Money" (or a bare "X"), never a longer phrase that merely contains x.
        if (normalized is "x" or "xmoney" or "xmoneyinc" or "xmoneyllc"
            || normalized.StartsWith("xmoney", StringComparison.Ordinal)
            || normalized.Contains("xpayments", StringComparison.Ordinal))
            return InstitutionBrand.XMoney;

        return InstitutionBrand.None;
    }
}
