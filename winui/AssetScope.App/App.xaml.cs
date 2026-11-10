using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using AssetScope.App.Services;
using AssetScope.Core;
using AssetScope.Core.Diagnostics;

namespace AssetScope.App;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }
    private TrayIconService? _tray;

    public App()
    {
        var settings = AppSession.Current.Settings;
        var language = settings.Language;
        if (language is "en-US" or "zh-CN")
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;

        // Select the configured theme before XamlControlsResources is
        // initialized. Otherwise code-created controls that read from
        // Application.Current.Resources inherit the OS theme even when the
        // window is explicitly light, producing dark navigation/segment/card
        // brushes inside a light window until restart.
        RequestedTheme = settings.Theme switch
        {
            "light" => ApplicationTheme.Light,
            "dark" => ApplicationTheme.Dark,
            _ => RequestedTheme,
        };

        UnhandledException += (_, e) =>
        {
            AppLog.Fatal("Application.UnhandledException", e.Exception);
            // Do not mark handled: preserving fail-fast behavior avoids leaving
            // financial UI state half-mutated. The synchronous log is flushed.
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) AppLog.Fatal("AppDomain.UnhandledException", ex);
            else AppLog.Error("AppDomain.UnhandledException", e.ExceptionObject?.ToString() ?? "Unknown exception");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
            AppLog.Error("TaskScheduler.UnobservedTaskException", "A background task fault was not observed.", e.Exception);

        InitializeComponent();

        // Density (§21.1) must be applied before any control renders.
        //
        // UseCompactResources cannot be flipped on the XamlControlsResources
        // instance App.xaml already merged: its setter clears and rebuilds that
        // dictionary's own merged sources, and App.xaml resolves StaticResource
        // lookups during InitializeComponent, so the dictionary is already in
        // use. Mutating it throws E_UNEXPECTED out of the constructor — before
        // AppLog exists — and the process dies at 0xC000027B with no log. Build
        // a fresh dictionary with the flag set at construction and swap it in.
    }

    /// <summary>
    /// Applies the compact layout density (§21.1).
    /// </summary>
    /// <remarks>
    /// This cannot run in the constructor. <see cref="Application.Resources"/>
    /// throws <c>E_UNEXPECTED</c> (0x8000FFFF) when read before the framework
    /// finishes starting, and an exception escaping the constructor kills the
    /// process at 0xC000027B before <see cref="AppLog"/> exists — a silent,
    /// unlaunchable app for anyone who had turned compact density on. Density
    /// is still applied before the first window is constructed, so no control
    /// renders at the wrong size.
    /// <para>
    /// XamlControlsResources.UseCompactResources is not an option: it throws
    /// "only supported in Experimental releases of WinUI" on the shipping
    /// Windows App SDK. Compact sizing is our own dictionary instead, merged
    /// last so it overrides Density.Comfortable.xaml.
    /// </para>
    /// <para>
    /// A cosmetic preference must never block startup, so a failure here is
    /// logged and the app continues at the default density.
    /// </para>
    /// </remarks>
    private static void ApplyDensity()
    {
        if (AppSession.Current.Settings.Density != "compact") return;
        try
        {
            Current.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("ms-appx:///Themes/Density.Compact.xaml"),
            });
            AppLog.Breadcrumb("Density", "Compact density applied.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Density", "Compact density could not be applied; continuing at default density.", ex);
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var launchMode = AppLaunch.Parse(Environment.GetCommandLineArgs().Skip(1));

        // Every mode participates in single-instance routing. In particular,
        // the scheduled --sync-once launch is handed to the tray process when
        // it exists, preventing two sidecars from writing the same database.
        var mainInstance = AppInstance.FindOrRegisterForKey("assetscope-main");
        if (!mainInstance.IsCurrent)
        {
            mainInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs())
                .AsTask().Wait(TimeSpan.FromSeconds(3));
            System.Diagnostics.Process.GetCurrentProcess().Kill();
            return;
        }
        // Only the main process owns app.log. A second-instance activation must
        // not erase the active process's reproduction log.
        AppLog.Initialize();
        AppLog.Info("App", $"Main instance launched; mode={launchMode}.");
        mainInstance.Activated += (_, activation) =>
        {
            var redirectedMode = ActivationMode(activation);
            AppLog.Breadcrumb("App", $"Existing instance activated; mode={redirectedMode}.");
            Window?.DispatcherQueue.TryEnqueue(() => RouteActivation(redirectedMode));
        };

        // When no tray process exists, the scheduled task performs its short,
        // headless sync and exits without constructing WinUI pages.
        if (launchMode == AppLaunchMode.SyncOnce)
        {
            Environment.ExitCode = await DailySyncScheduler.RunOnceAsync();
            Exit();
            return;
        }

        // Default Privacy Mode behavior (§21.1); still session-only afterwards (§14.4).
        AppSession.Current.PrivacyMode = AppSession.Current.Settings.StartInPrivacyMode;

        // Density must be settled before the first control is constructed.
        ApplyDensity();

        try
        {
            Window = new MainWindow(startHidden: launchMode == AppLaunchMode.Background);
            _tray = new TrayIconService(
                Window.DispatcherQueue,
                () => Window.ShowMainWindow(),
                async () => { await AppSession.Current.TriggerSyncAsync("manual", force: true); },
                ExitFromTray);

            if (launchMode == AppLaunchMode.Background)
            {
                await Window.InitializeInBackgroundAsync();
                AppLog.Info("App", "Background startup initialized; main window remains hidden.");
            }
            else
            {
                Window.ShowMainWindow();
                AppLog.Info("App", "Main window activated.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Fatal("App.OnLaunched", ex);
            throw;
        }
    }

    private void RouteActivation(AppLaunchMode mode)
    {
        if (mode == AppLaunchMode.SyncOnce)
        {
            _ = AppSession.Current.TriggerSyncAsync("scheduled", force: false);
            return;
        }
        if (mode == AppLaunchMode.Normal) Window?.ShowMainWindow();
    }

    private static AppLaunchMode ActivationMode(AppActivationArguments activation)
    {
        if (activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
            && !string.IsNullOrWhiteSpace(launch.Arguments))
            return AppLaunch.Parse(launch.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return AppLaunchMode.Normal;
    }

    private void ExitFromTray()
    {
        AppLog.Info("Tray", "Explicit exit requested.");
        _tray?.Dispose();
        _tray = null;
        Window?.RequestExit();
    }

    /// <summary>
    /// Language and Application.RequestedTheme only apply at process start.
    /// Schedule a new instance, then tear this one down without blocking.
    /// </summary>
    internal void RestartForSettingsChange()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = AppLaunch.BuildRelaunchArguments(exe),
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
        }
        ExitFromTray();
    }
}
