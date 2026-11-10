using System.Diagnostics;
using AssetScope.Core;
using AssetScope.Core.Security;
using AssetScope.Core.Sidecar;

namespace AssetScope.App.Services;

internal static class DailySyncScheduler
{
    public const string TaskName = "assetscope-daily-sync";

    public static async Task RegisterAsync(TimeSpan time)
    {
        _ = SidecarLauncher.FindInstalledSidecar()
            ?? throw new InvalidOperationException(Display.S("DailySyncNeedsInstall"));
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException(Display.S("DailySyncNeedsInstall"));
        var command = $"\"{executable}\" --sync-once";
        var psi = HiddenProcess("schtasks.exe");
        foreach (var arg in new[]
        {
            "/Create", "/F", "/SC", "DAILY", "/TN", TaskName,
            "/ST", $"{time.Hours:00}:{time.Minutes:00}", "/TR", command,
        }) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException(Display.S("DailySyncRegisterFailed"));
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(Display.S("DailySyncRegisterFailed"));
    }

    public static async Task UnregisterAsync()
    {
        var psi = HiddenProcess("schtasks.exe");
        foreach (var arg in new[] { "/Delete", "/F", "/TN", TaskName }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi);
        if (process is not null) await process.WaitForExitAsync();
    }

    /// <summary>
    /// Headless scheduled entry point. The WinUI executable reads provider
    /// secrets from Credential Manager, then passes them only through the child
    /// process environment. It never appears in Task Scheduler or a command
    /// line. The Node sync host owns the database transaction/lock protocol.
    /// </summary>
    public static async Task<int> RunOnceAsync()
    {
        var settings = AppSettings.Load();
        if (settings.Mode == "demo") return 0;
        var plaidSecret = string.IsNullOrWhiteSpace(settings.PlaidClientId)
            ? null : WindowsCredentials.Read(WindowsCredentials.PlaidSecretTarget);
        var snapTradeConsumerKey = string.IsNullOrWhiteSpace(settings.SnapTradeClientId)
            ? null : WindowsCredentials.Read(WindowsCredentials.SnapTradeConsumerKeyTarget);
        if (plaidSecret is null && snapTradeConsumerKey is null) return 2;
        var installed = SidecarLauncher.FindInstalledSidecar();
        if (installed is null) return 2;

        var dataDir = settings.RealDataDir
            ?? Environment.GetEnvironmentVariable("ASSETSCOPE_DATA_DIR")
            ?? Environment.GetEnvironmentVariable("FUNGIBLE_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".assetscope");
        var psi = HiddenProcess(installed.Value.NodeExe);
        psi.WorkingDirectory = Path.GetDirectoryName(installed.Value.Bundle)!;
        foreach (var arg in new[]
        {
            installed.Value.Bundle, "--sync-once", "--mode", "real", "--data-dir", dataDir,
        }) psi.ArgumentList.Add(arg);
        if (plaidSecret is not null)
        {
            psi.Environment["PLAID_CLIENT_ID"] = settings.PlaidClientId!;
            psi.Environment["PLAID_SECRET"] = plaidSecret;
            psi.Environment["PLAID_ENV"] = settings.PlaidEnvironment;
        }
        if (snapTradeConsumerKey is not null)
        {
            psi.Environment["SNAPTRADE_CLIENT_ID"] = settings.SnapTradeClientId!;
            psi.Environment["SNAPTRADE_CONSUMER_KEY"] = snapTradeConsumerKey;
        }

        using var process = Process.Start(psi);
        if (process is null) return 2;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static ProcessStartInfo HiddenProcess(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
    };
}
