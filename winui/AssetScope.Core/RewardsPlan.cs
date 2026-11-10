using AssetScope.Core.Api;

namespace AssetScope.Core;

public sealed record RewardsPlanItem(string Category, string Rate, string Unit);

/// <summary>
/// One card to carry. <see cref="Categories"/> is every category given to
/// it; <see cref="Items"/> is what to show, with the everyday card's
/// base-rate categories folded into "other".
/// </summary>
public sealed record RewardsPlanCard(
    string AccountId,
    IReadOnlyList<string> Categories,
    IReadOnlyList<RewardsPlanItem> Items,
    bool IsEveryday);

/// <summary>
/// Turns the per-category ranking into the short answer: which card to reach
/// for, and for what. The sidecar decides every rate; this only groups.
/// </summary>
public static class RewardsPlan
{
    public const string EverydayCategory = "other";

    public static IReadOnlyList<RewardsPlanCard> Build(IReadOnlyList<RewardCategoryDto> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);

        var reach = categories
            .SelectMany(c => c.Best)
            .GroupBy(b => b.AccountId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var owned = new Dictionary<string, List<(RewardCategoryDto Category, RewardCardRateDto Rate)>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var category in categories)
        {
            if (category.Best.Count == 0) continue;
            // A tie earns the same on either card. Give it to the one that
            // already covers the most, so there are fewer cards to remember.
            var pick = category.Best.OrderByDescending(b => reach[b.AccountId]).First();
            if (!owned.TryGetValue(pick.AccountId, out var list))
            {
                owned[pick.AccountId] = list = [];
                order.Add(pick.AccountId);
            }
            list.Add((category, pick));
        }

        return order
            .Select(accountId =>
            {
                var picks = owned[accountId];
                var everyday = picks.FirstOrDefault(p => p.Category.Category == EverydayCategory);
                var items = everyday.Rate is null
                    ? picks.Select(p => Item(p.Category, p.Rate)).ToList()
                    : picks
                        .Where(p => p.Category.Category != EverydayCategory
                            && (p.Rate.Rate != everyday.Rate.Rate || p.Rate.Unit != everyday.Rate.Unit))
                        .Select(p => Item(p.Category, p.Rate))
                        .Append(Item(everyday.Category, everyday.Rate))
                        .ToList();
                return (
                    Card: new RewardsPlanCard(
                        accountId,
                        picks.Select(p => p.Category.Category).ToList(),
                        items,
                        everyday.Rate is not null),
                    Spend: picks.Sum(p => Money.Parse(p.Category.Spend)));
            })
            // Specialty cards first, biggest spending first; the everyday card closes the list.
            .OrderBy(x => x.Card.IsEveryday)
            .ThenByDescending(x => x.Spend)
            .Select(x => x.Card)
            .ToList();
    }

    private static RewardsPlanItem Item(RewardCategoryDto category, RewardCardRateDto rate) =>
        new(category.Category, rate.Rate, rate.Unit);
}
