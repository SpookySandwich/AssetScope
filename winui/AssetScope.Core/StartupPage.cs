namespace AssetScope.Core;

/// <summary>
/// Resolves the first navigation destination without coupling persisted
/// settings to WinUI controls. Unknown/removed pages always fall back home.
/// </summary>
public static class StartupPage
{
    private static readonly HashSet<string> KnownPages =
    [
        "home",
        "accounts",
        "transactions",
        "investments",
        "living",
        "rewards",
        "trends",
        "settings",
    ];

    public static string Resolve(bool startOnHome, string? lastPage, bool resumeLastPage = false) =>
        resumeLastPage && lastPage is not null && KnownPages.Contains(lastPage)
            ? lastPage
            : startOnHome || string.IsNullOrWhiteSpace(lastPage) || !KnownPages.Contains(lastPage)
                ? "home"
                : lastPage;

    public static bool IsKnown(string? page) =>
        page is not null && KnownPages.Contains(page);
}
