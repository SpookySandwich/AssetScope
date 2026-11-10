using AssetScope.App.Services;
using AssetScope.Core.Api;

namespace AssetScope.App.Pages;

internal static class PlaidLinkPresentation
{
    public static string InstitutionSummary(LinkSessionDto session)
    {
        var names = session.ConnectedItems
            .Select(item => item.InstitutionName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (names.Count == 0 && !string.IsNullOrWhiteSpace(session.InstitutionName))
        {
            names.Add(session.InstitutionName);
        }

        if (names.Count == 0) return Display.S("LinkUnknownInstitution");
        if (names.Count == 1) return names[0]!;
        return string.Format(Display.S("LinkInstitutionSummaryMultiple"), names.Count, string.Join(", ", names));
    }

    public static string? PartialFailure(LinkSessionDto session) =>
        session.FailedItemCount > 0
            ? string.Format(Display.S("LinkPartialFailure"), session.FailedItemCount)
            : null;
}
