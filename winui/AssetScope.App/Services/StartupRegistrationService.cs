using Microsoft.Win32;
using AssetScope.Core;

namespace AssetScope.App.Services;

/// <summary>Per-user startup registration for this unpackaged application.</summary>
public static class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "assetscope";

    public static string ExecutablePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("The application executable path is unavailable.");

    public static bool CanRegisterCurrentExecutable
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return AppLaunch.IsInstalledExecutable(ExecutablePath, local);
        }
    }

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var actual = key?.GetValue(ValueName) as string;
            return string.Equals(actual, AppLaunch.BuildStartupCommand(ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        if (!CanRegisterCurrentExecutable)
            throw new InvalidOperationException(Display.S("StartupNeedsInstall"));
        key.SetValue(ValueName, AppLaunch.BuildStartupCommand(ExecutablePath), RegistryValueKind.String);
    }
}
