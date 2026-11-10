namespace AssetScope.Core;

public enum AppLaunchMode
{
    Normal,
    Background,
    SyncOnce,
}

/// <summary>
/// Pure command-line policy shared by the WinUI lifecycle and tests. Keeping
/// this outside App.xaml.cs makes secondary-instance routing deterministic.
/// </summary>
public static class AppLaunch
{
    public static AppLaunchMode Parse(IEnumerable<string> arguments)
    {
        var args = arguments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (args.Contains("--sync-once")) return AppLaunchMode.SyncOnce;
        if (args.Contains("--background")) return AppLaunchMode.Background;
        return AppLaunchMode.Normal;
    }

    public static string BuildStartupCommand(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
        return $"\"{Path.GetFullPath(executablePath)}\" --background";
    }

    /// <summary>
    /// cmd.exe arguments that start <paramref name="executablePath"/> after
    /// this process has had a moment to release the single-instance key.
    /// </summary>
    public static string BuildRelaunchArguments(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
        var exe = Path.GetFullPath(executablePath);
        return $"/d /c ping 127.0.0.1 -n 2 >nul & start \"\" \"{exe}\"";
    }

    public static string InstalledExecutablePath(string localAppData) =>
        Path.Combine(Path.GetFullPath(localAppData), "Programs", "AssetScope", "assetscope.exe");

    public static bool IsInstalledExecutable(string executablePath, string localAppData) =>
        Path.GetFullPath(executablePath).Equals(
            InstalledExecutablePath(localAppData), StringComparison.OrdinalIgnoreCase);
}
