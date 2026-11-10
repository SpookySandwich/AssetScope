namespace AssetScope.Core;

public sealed record CompositionAmount(string Bucket, decimal Amount);

public sealed record CompositionSlice(string Bucket, decimal Amount, decimal Share);

/// <summary>
/// Builds the home-page net-worth composition. Asset allocation remains an
/// asset-only API contract; liabilities are added here as a positive ring
/// magnitude while the donut center continues to show signed net worth.
/// </summary>
public static class NetWorthComposition
{
    public static IReadOnlyList<CompositionSlice> Build(
        IEnumerable<CompositionAmount> assetSlices,
        decimal liabilities)
    {
        var amounts = assetSlices
            .Where(slice => slice.Amount > 0)
            .ToList();

        var liabilityMagnitude = Math.Abs(liabilities);
        if (liabilityMagnitude > 0)
            amounts.Add(new CompositionAmount("liability", liabilityMagnitude));

        var total = amounts.Sum(slice => slice.Amount);
        if (total <= 0) return [];

        return amounts
            .Select(slice => new CompositionSlice(
                slice.Bucket,
                slice.Amount,
                slice.Amount / total))
            .ToList();
    }
}
