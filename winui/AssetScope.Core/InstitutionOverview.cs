namespace AssetScope.Core;

public sealed record InstitutionOverviewInput(
    string AccountId,
    string InstitutionName,
    string DisplayName,
    string Category,
    decimal? Balance,
    decimal? Change);

public sealed record InstitutionOverviewGroup(
    string Name,
    IReadOnlyList<InstitutionOverviewInput> Accounts,
    IReadOnlyList<InstitutionOverviewRow> Rows,
    decimal Total,
    decimal? Change,
    decimal? ChangeRatio);

public sealed record InstitutionOverviewRow(string Bucket, decimal Total);

/// <summary>
/// Builds the signed institution summaries shown on the home page. Account
/// balances from the native API are already net-worth contributions: assets
/// are positive and liabilities are negative. This layer must never invert a
/// liability a second time.
/// </summary>
public static class InstitutionOverview
{
    public static IReadOnlyList<InstitutionOverviewGroup> Build(
        IEnumerable<InstitutionOverviewInput> accounts,
        int maximumGroups = 3)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (maximumGroups <= 0) throw new ArgumentOutOfRangeException(nameof(maximumGroups));

        return accounts
            .GroupBy(account => account.InstitutionName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var groupedAccounts = group.ToList();
                var total = groupedAccounts.Sum(account => account.Balance ?? 0m);
                var changedAccounts = groupedAccounts.Where(account => account.Change is not null).ToList();
                decimal? change = changedAccounts.Count == 0
                    ? null
                    : changedAccounts.Sum(account => account.Change!.Value);
                var previous = change is null ? (decimal?)null : total - change.Value;
                decimal? ratio = previous is null || previous == 0
                    ? null
                    : change / Math.Abs(previous.Value);
                var rows = groupedAccounts
                    .GroupBy(AccountBucket)
                    .Select(bucket => new InstitutionOverviewRow(
                        bucket.Key,
                        bucket.Sum(account => account.Balance ?? 0m)))
                    .OrderBy(row => BucketOrder(row.Bucket))
                    .ToList();
                return new InstitutionOverviewGroup(
                    group.First().InstitutionName,
                    groupedAccounts,
                    rows,
                    total,
                    change,
                    ratio);
            })
            .OrderByDescending(group => Math.Abs(group.Total))
            .Take(maximumGroups)
            .ToList();
    }

    private static string AccountBucket(InstitutionOverviewInput account)
    {
        if (account.Category is "investment" or "retirement") return "investment";
        if (account.Category is "bank" or "cash") return "cash";
        if (account.Category is "credit" or "loan") return "liability";
        return "other";
    }

    private static int BucketOrder(string bucket) => bucket switch
    {
        "investment" => 0,
        "cash" => 1,
        "liability" => 2,
        _ => 3,
    };
}
