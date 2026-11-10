using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetScope.Core;

/// <summary>
/// Local app-side settings for the unpackaged WinUI shell (mode choice, repo
/// override, window placement). Stored as JSON under %LOCALAPPDATA%; the
/// financial data itself always lives in the AssetScope data directory.
/// Never holds secrets.
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "real"; // "real" | "demo"
    // True after the first-run guide is finished or skipped so an empty
    // real-mode profile is not locked behind the setup page again.
    [JsonPropertyName("onboardingDismissed")] public bool OnboardingDismissed { get; set; }
    [JsonPropertyName("repoRoot")] public string? RepoRoot { get; set; }
    [JsonPropertyName("realDataDir")] public string? RealDataDir { get; set; }
    // Plaid: Client ID + environment are ordinary settings; the Secret lives in
    // Windows Credential Manager only (SPEC §13.1).
    [JsonPropertyName("plaidClientId")] public string? PlaidClientId { get; set; }
    [JsonPropertyName("plaidEnvironment")] public string PlaidEnvironment { get; set; } = "production";
    // SnapTrade Personal API Key mode: the Client ID is ordinary metadata;
    // Consumer Key is stored only in Windows Credential Manager.
    [JsonPropertyName("snapTradeClientId")] public string? SnapTradeClientId { get; set; }
    // Optional local display name used by the reference-style Home greeting.
    // This is presentation metadata only; it is never sent to Plaid/SnapTrade.
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    // "system" follows Windows. Explicit en-US / zh-CN values are useful for
    // deterministic visual comparison and remain limited to shipped catalogs.
    [JsonPropertyName("language")] public string Language { get; set; } = "system";
    // The reference baseline is light. Users can still explicitly choose dark
    // or system; the default and the rendered selection must agree.
    [JsonPropertyName("theme")] public string Theme { get; set; } = "light";
    [JsonPropertyName("startOnHome")] public bool StartOnHome { get; set; } = true;
    [JsonPropertyName("lastPage")] public string LastPage { get; set; } = "home";
    // Set just before a settings-driven relaunch so StartOnHome does not
    // steal the page the user was looking at.
    [JsonPropertyName("resumeLastPageOnce")] public bool ResumeLastPageOnce { get; set; }
    // §21.1: default Privacy Mode behavior and layout density ("comfortable" | "compact").
    [JsonPropertyName("startInPrivacyMode")] public bool StartInPrivacyMode { get; set; }
    [JsonPropertyName("density")] public string Density { get; set; } = "comfortable";
    // §21.1: user animation preference; the system reduced-motion setting always wins.
    [JsonPropertyName("animationsEnabled")] public bool AnimationsEnabled { get; set; } = true;
    [JsonPropertyName("windowWidth")] public int WindowWidth { get; set; } = 1400;
    [JsonPropertyName("windowHeight")] public int WindowHeight { get; set; } = 900;
    [JsonPropertyName("windowMaximized")] public bool WindowMaximized { get; set; }

    public static string Directory
    {
        get
        {
            // Explicit isolation hook for UI automation and portable test
            // launches. Normal installed builds never set this variable.
            var overrideDirectory = Environment.GetEnvironmentVariable("ASSETSCOPE_APPDATA_DIR");
            return string.IsNullOrWhiteSpace(overrideDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetScope")
                : Path.GetFullPath(overrideDirectory);
        }
    }

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static string DemoDataDirectory => Path.Combine(Directory, "Demo");

    /// <summary>True when no settings file existed at load time — a first run.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool FirstRun { get; private set; }

    public static AppSettings Load()
    {
        try
        {
            MigrateLegacySettingsIfNeeded();
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings { FirstRun = true };
        }
        catch { /* corrupted settings fall back to defaults */ }
        return new AppSettings { FirstRun = true };
    }

    private static void MigrateLegacySettingsIfNeeded()
    {
        if (File.Exists(FilePath)) return;
        var legacyDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fungible-winui");
        var legacyFile = Path.Combine(legacyDirectory, "settings.json");
        if (!File.Exists(legacyFile)) return;

        System.IO.Directory.CreateDirectory(Directory);
        File.Copy(legacyFile, FilePath, overwrite: false);
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
