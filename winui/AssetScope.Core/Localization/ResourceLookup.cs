using System.Runtime.InteropServices;

namespace AssetScope.Core.Localization;

/// <summary>
/// Resolves code-facing localization keys against WinUI .resw conventions.
/// ResourceLoader throws 0x80073B17 for a missing candidate instead of
/// returning null, so each candidate must be isolated from the next one.
/// </summary>
public static class ResourceLookup
{
    public const int NamedResourceNotFound = unchecked((int)0x80073B17);

    public static string Resolve(
        string key,
        Func<string, string> getString,
        Action<string>? onMissing = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(getString);

        foreach (var candidate in Candidates(key))
        {
            try
            {
                var value = getString(candidate);
                if (!string.IsNullOrEmpty(value)) return value;
            }
            catch (COMException ex) when (ex.HResult == NamedResourceNotFound)
            {
                // Expected miss. Continue to .Text/.Content property forms.
            }
        }

        onMissing?.Invoke(key);
        return key;
    }

    public static IReadOnlyList<string> Candidates(string key) =>
        [key, $"{key}/Text", $"{key}/Content"];
}
