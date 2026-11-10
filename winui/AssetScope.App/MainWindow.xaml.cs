using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using AssetScope.App.Pages;
using AssetScope.App.Services;
using AssetScope.Core.Diagnostics;

namespace AssetScope.App;

public sealed partial class MainWindow : Window
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);

    // DWM window attributes used to stop the 1px system caption/border from
    // painting white across the top of a maximized custom title bar.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const uint DwmwaColorNone = 0xFFFFFFFE;

    private readonly AppSession _session = AppSession.Current;
    private Task? _sessionInitialization;
    private bool _exitRequested;
    private bool _onboardingDeferred;
    private bool _privacySelectionPending;
    private NavigationViewItem? _selectedDestination;

    public MainWindow(bool startHidden = false)
    {
        _onboardingDeferred = startHidden;
        AppLog.Breadcrumb("MainWindow", "Constructing window.");
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        ApplyTheme(_session.Settings.Theme);
        // AppWindow.Resize takes physical pixels; settings store logical units.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)(Math.Max(900, _session.Settings.WindowWidth) * scale),
            (int)(Math.Max(650, _session.Settings.WindowHeight) * scale)));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            // Minimum window size 900×650 logical (SPEC §14.1).
            p.PreferredMinimumWidth = (int)(900 * scale);
            p.PreferredMinimumHeight = (int)(650 * scale);
            if (_session.Settings.WindowMaximized) p.Maximize();
        }

        AppWindow.Changed += OnAppWindowChanged;
        ApplyMaximizedCaptionInset();
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;
        RegisterAccelerators();
        _session.PropertyChanged += Session_PropertyChanged;
        UpdatePrivacyFooter();
        RootGrid.ActualThemeChanged += (_, _) => UpdateThemeChrome();

        if (startHidden) AppWindow.Hide();
        RootGrid.Loaded += (_, _) =>
        {
            OffsetPaneBelowCaption();
            ApplyMaximizedCaptionInset();
            if (!_onboardingDeferred) _ = InitializeAndOnboardAsync();
        };
    }

    private bool _onboardStarted;

    private async Task InitializeAndOnboardAsync()
    {
        if (_onboardStarted) return;
        _onboardStarted = true;
        AppLog.Breadcrumb("Startup", "Initialization and onboarding started.");

        var firstRun = _session.Settings.FirstRun;

        if (_session.Settings.Mode == "real" && string.IsNullOrWhiteSpace(_session.Settings.RealDataDir))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var currentDataDir = Path.Combine(profile, ".assetscope");
            var legacyDataDir = Path.Combine(profile, ".fungible");
            MigrateLegacyDataDirectory(currentDataDir, legacyDataDir);
        }
        if (firstRun) _session.Settings.Save(); // persist migrated/default real-mode settings

        // Existing-data detection (UC-002/SPEC §7.2): before first touching a
        // pre-existing AssetScope database, confirm and explain the backup.
        if (firstRun && _session.Settings.Mode == "real")
        {
            var dataDir = _session.Settings.RealDataDir
                ?? Environment.GetEnvironmentVariable("ASSETSCOPE_DATA_DIR")
                ?? Environment.GetEnvironmentVariable("FUNGIBLE_DATA_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".assetscope");
            var currentDbPath = Path.Combine(dataDir, "assetscope.db");
            var legacyDbPath = Path.Combine(dataDir, "fungible.db");
            var dbPath = File.Exists(currentDbPath) ? currentDbPath : legacyDbPath;
            if (File.Exists(dbPath))
            {
                var confirm = new ContentDialog
                {
                    Title = Services.Display.S("ExistingDataTitle"),
                    Content = string.Format(Services.Display.S("ExistingDataBody"),
                        dbPath, Path.Combine(dataDir, "backups")),
                    PrimaryButtonText = Services.Display.S("ExistingDataUse"),
                    CloseButtonText = Services.Display.S("Cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = RootGrid.XamlRoot,
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                {
                    StartupRing.IsActive = false;
                    StartupText.Text = Services.Display.S("SetupCancelled");
                    RetryButton.Visibility = Visibility.Visible;
                    _onboardStarted = false;
                    return;
                }
            }
        }

        await EnsureSessionInitializedAsync();

        // First-run guide. Plaid and SnapTrade are optional; skip or a
        // manual balance also dismisses it so the empty app is reachable.
        if (_session.State == SessionState.Ready && NeedsSetup())
        {
            AppLog.Breadcrumb("Startup", "Showing first-run setup guide.");
            ShowSetupGate(allowCancel: false);
        }
    }

    private static void MigrateLegacyDataDirectory(string target, string legacy)
    {
        var legacyDatabase = Path.Combine(legacy, "fungible.db");
        var currentDatabase = Path.Combine(target, "assetscope.db");
        if (IsUsableFile(currentDatabase) || !IsUsableFile(legacyDatabase)) return;

        var staging = $"{target}.migrating-{Environment.ProcessId}";
        try
        {
            CopyDirectory(legacy, staging);
            File.Copy(Path.Combine(staging, "fungible.db"), Path.Combine(staging, "assetscope.db"));
            if (!Directory.Exists(target))
            {
                Directory.Move(staging, target);
                return;
            }

            CopyMissing(staging, target);
            if (File.Exists(currentDatabase))
                File.Move(currentDatabase, currentDatabase + ".pre-migration-empty", overwrite: true);
            File.Move(Path.Combine(staging, "assetscope.db"), currentDatabase);
            Directory.Delete(staging, recursive: true);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private static bool IsUsableFile(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    private static void CopyMissing(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination)) File.Copy(file, destination);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyMissing(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    /// <summary>
    /// Starts the sidecar and launch-sync pipeline without showing the window.
    /// Setup UI is intentionally deferred until the user opens the app.
    /// </summary>
    public Task InitializeInBackgroundAsync() => EnsureSessionInitializedAsync();

    private Task EnsureSessionInitializedAsync() =>
        _sessionInitialization ??= InitializeSessionAsync();

    public void ShowMainWindow()
    {
        if (_onboardingDeferred)
        {
            _onboardingDeferred = false;
            _ = InitializeAndOnboardAsync();
        }
        else if (_session.State is SessionState.Ready or SessionState.FatalError)
        {
            _ = _session.EnsureEngineAsync();
        }
        AppWindow.Show();
        Activate();
    }

    public void RequestExit()
    {
        _exitRequested = true;
        try { AppWindow.Hide(); } catch { }
        Close();
    }

    private bool NeedsSetup() =>
        !_session.IsDemoMode
        && !_session.Settings.OnboardingDismissed
        && (_session.Bootstrap?.Onboarding.AccountCount ?? 0) == 0;

    /// <summary>
    /// Shows the full-page setup guide. First launch can be skipped; later
    /// openings from Settings or empty states can be cancelled.
    /// </summary>
    public void ShowSetupGate(bool allowCancel, bool startAutomatic = false)
    {
        AppLog.Breadcrumb("Setup", $"Showing setup gate; allowCancel={allowCancel}, startAutomatic={startAutomatic}.");
        var view = new SetupView
        {
            AllowCancel = allowCancel,
            StartAutomatic = startAutomatic,
        };
        view.Finished += async (_, _) =>
        {
            AppLog.Breadcrumb("Setup", "Setup view reported finished.");
            // Only leave the gate once the session actually has an account.
            if (_session.Api is not null)
            {
                try { _session.Bootstrap = await _session.Api.GetBootstrapAsync(); }
                catch (AssetScope.Core.Api.ApiException) { }
            }
            if (!allowCancel && NeedsSetup()) return;
            SetupHost.Children.Clear();
            SetupHost.Visibility = Visibility.Collapsed;
            Nav.IsEnabled = true;
            _session.NotifyDataRefreshed();
            AppLog.Breadcrumb("Setup", "Setup gate closed.");
        };

        SetupHost.Children.Clear();
        SetupHost.Children.Add(view);
        SetupHost.Visibility = Visibility.Visible;
        // Navigation is unreachable while the gate is up.
        Nav.IsEnabled = allowCancel;
    }

    private async Task InitializeSessionAsync()
    {
        StartupOverlay.Visibility = Visibility.Visible;
        StartupRing.IsActive = true;
        StartupErrorText.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        StartupText.Text = Display.S("StartupStarting/Text");

        await _session.StartAsync();

        if (_session.State == SessionState.Ready)
        {
            AppLog.Info("Startup", "Sidecar and bootstrap are ready.");
            StartupOverlay.Visibility = Visibility.Collapsed;
            StartupRing.IsActive = false;
            Nav.IsEnabled = true;
            var resumeLastPage = _session.Settings.ResumeLastPageOnce;
            if (resumeLastPage)
            {
                _session.Settings.ResumeLastPageOnce = false;
                _session.Settings.Save();
            }
            var startPage = AssetScope.Core.StartupPage.Resolve(
                _session.Settings.StartOnHome,
                _session.Settings.LastPage,
                resumeLastPage);
            Nav.SelectedItem = startPage == "settings"
                ? SettingsNavItem
                : Nav.MenuItems[MenuIndex(startPage)];
            // NavigationView can create its initially-selected page before the
            // sidecar handshake completes. That page's first LoadAsync then
            // correctly exits because Api is null, but selecting the same item
            // after Ready does not raise SelectionChanged again. Notify every
            // cached page at the readiness boundary so startup never leaves a
            // permanently empty dashboard.
            _session.NotifyDataRefreshed();
        }
        else
        {
            AppLog.Error("Startup", _session.StartupError ?? "Session initialization failed without an error message.");
            StartupRing.IsActive = false;
            StartupText.Text = Display.S("StartupFailed");
            StartupErrorText.Text = _session.StartupError ?? string.Empty;
            StartupErrorText.Visibility = Visibility.Visible;
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        _onboardStarted = false;
        _ = InitializeAndOnboardAsync();
    }

    /// <summary>
    /// Animation preference (§21.1): the user toggle governs page transitions,
    /// and the system reduced-motion setting overrides it unconditionally.
    /// </summary>
    private Microsoft.UI.Xaml.Media.Animation.NavigationTransitionInfo TransitionInfo()
    {
        var systemAllows = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        return systemAllows && _session.Settings.AnimationsEnabled
            ? new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo()
            : new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo();
    }

    private void Nav_PaneOpened(NavigationView sender, object args)
    {
        OffsetPaneBelowCaption();
        UpdatePaneChrome(sender, paneOpen: true);
    }

    private void Nav_PaneClosed(NavigationView sender, object args)
    {
        OffsetPaneBelowCaption();
        UpdatePaneChrome(sender, paneOpen: false);
    }

    /// <summary>
    /// The brand mark is a transparent overlay on the pane. Custom SetTitleBar
    /// does not populate TemplateSettings.TopPadding, so the hamburger would
    /// sit under the 48px caption unless those template spacers are set here.
    /// </summary>
    private void OffsetPaneBelowCaption()
    {
        const double caption = 48;
        if (FindDescendant<FrameworkElement>(Nav, "TogglePaneTopPadding") is { } togglePad)
            togglePad.Height = caption;
        if (FindDescendant<FrameworkElement>(Nav, "ContentPaneTopPadding") is { } panePad)
            panePad.Height = caption;
        // Clear the 48px overlay caption only. Page top padding is 0 so
        // this spacer is not stacked with a second empty band.
        if (FindDescendant<FrameworkElement>(Nav, "ContentTopPadding") is { } contentPad)
            contentPad.Height = caption;
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name) return match;
            if (FindDescendant<T>(child, name) is { } nested) return nested;
        }
        return null;
    }

    private void UpdatePaneChrome(NavigationView sender, bool paneOpen)
    {
        // NavigationView is the sole owner of pane state. The title-bar brand
        // only reflects the completed open/closed event; it never writes back
        // to IsPaneOpen or tries to infer state from a responsive display mode.
        TitleBarBrandHost.Width = paneOpen ? sender.OpenPaneLength : sender.CompactPaneLength;
        TitleBarBrandContent.Padding = paneOpen
            ? new Thickness(32, 0, 0, 0)
            : new Thickness(0);
        TitleBarBrandContent.HorizontalAlignment = paneOpen
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Center;
        AppTitleText.Visibility = paneOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var selectedItem = args.SelectedItem as NavigationViewItem;
        var tag = selectedItem?.Tag as string;
        if (tag == "privacy")
        {
            if (_privacySelectionPending) return;
            _privacySelectionPending = true;
            _session.PrivacyMode = !_session.PrivacyMode;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_selectedDestination is not null)
                    sender.SelectedItem = _selectedDestination;
                _privacySelectionPending = false;
            });
            return;
        }

        AppLog.Breadcrumb("Navigation", $"Page selected: {tag ?? "home"}.");
        if (AssetScope.Core.StartupPage.IsKnown(tag)
            && !string.Equals(_session.Settings.LastPage, tag, StringComparison.Ordinal))
        {
            _session.Settings.LastPage = tag!;
            _session.Settings.Save();
        }
        if (AssetScope.Core.StartupPage.IsKnown(tag))
            _selectedDestination = selectedItem;
        var pageType = tag switch
        {
            "accounts" => typeof(AccountsPage),
            "transactions" => typeof(TransactionsPage),
            "investments" => typeof(InvestmentsPage),
            "living" => typeof(LivingPage),
            "rewards" => typeof(RewardsPage),
            "trends" => typeof(TrendsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(HomePage),
        };
        if (ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType, null, TransitionInfo());
    }

    // Menu order is also the Ctrl+N shortcut order; keep RegisterAccelerators in step.
    private static int MenuIndex(string page) => page switch
    {
        "accounts" => 1,
        "transactions" => 2,
        "investments" => 3,
        "living" => 4,
        "rewards" => 5,
        "trends" => 6,
        _ => 0,
    };

    public void NavigateToAccounts(string? accountId = null)
    {
        Nav.SelectedItem = Nav.MenuItems[MenuIndex("accounts")];
        if (accountId is not null && ContentFrame.Content is AccountsPage page)
            page.SelectAccount(accountId);
        else if (accountId is not null)
            AccountsPage.PendingSelection = accountId;
    }

    public void NavigateToInvestments() => Nav.SelectedItem = Nav.MenuItems[MenuIndex("investments")];

    /// <summary>Opens Transactions, optionally pre-filtered to one category and range tag.</summary>
    public void NavigateToTransactions(string? category = null, string? range = null)
    {
        TransactionsPage.PendingCategory = category;
        TransactionsPage.PendingRange = range;
        Nav.SelectedItem = Nav.MenuItems[MenuIndex("transactions")];
        if ((category is not null || range is not null) && ContentFrame.Content is TransactionsPage page)
            page.ApplyPendingFilter(reload: true);
    }

    public void NavigateToSettings() => Nav.SelectedItem = SettingsNavItem;

    public void ApplyTheme(string theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            "dark" => ElementTheme.Dark,
            "system" => ElementTheme.Default,
            _ => ElementTheme.Light,
        };
        UpdateThemeChrome();
    }

    private void UpdateThemeChrome()
    {
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var titleBar = AppWindow.TitleBar;
        // Default caption-button fill is the system title-bar color (white in
        // light Windows). Left as-is, it shows as a 1px strip across the top of
        // a maximized window. Transparent lets Mica and the canvas show through.
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = dark
            ? Microsoft.UI.Colors.White
            : Microsoft.UI.Colors.Black;
        titleBar.ButtonInactiveForegroundColor = dark
            ? Microsoft.UI.ColorHelper.FromArgb(255, 160, 160, 160)
            : Microsoft.UI.Colors.Gray;
        titleBar.ButtonHoverForegroundColor = titleBar.ButtonForegroundColor;
        titleBar.ButtonPressedForegroundColor = titleBar.ButtonForegroundColor;
        titleBar.ButtonHoverBackgroundColor = dark
            ? Microsoft.UI.ColorHelper.FromArgb(24, 255, 255, 255)
            : Microsoft.UI.ColorHelper.FromArgb(24, 0, 0, 0);

        // DWM still paints a 1px caption/border using the *system* theme, not
        // RootGrid.RequestedTheme. Match the app so a dark window on a light OS
        // does not flash a white line when maximized.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        uint useDark = dark ? 1u : 0u;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(uint));
        uint none = DwmwaColorNone;
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref none, sizeof(uint));
        uint border = dark ? 0x001F1F1Fu : 0x00F3F3F3u;
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(uint));
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) =>
        ApplyMaximizedCaptionInset();

    /// <summary>
    /// WinUI leaves a 1px system strip at the top of the client area when the
    /// window is maximized with a custom title bar (microsoft-ui-xaml#9176).
    /// Pull the navigation surface up by that pixel so the canvas covers it.
    /// </summary>
    private void ApplyMaximizedCaptionInset()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var margin = presenter.State == OverlappedPresenterState.Maximized
            ? new Thickness(0, -1, 0, 0)
            : new Thickness(0);
        if (Nav.Margin != margin) Nav.Margin = margin;
    }

    private void Session_PropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSession.PrivacyMode)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdatePrivacyFooter();
            if (ContentFrame.Content is SettingsPage settingsPage)
                settingsPage.SyncPrivacySelection(_session.PrivacyMode);
        });
    }

    private void UpdatePrivacyFooter()
    {
        PrivacyNavIcon.Glyph = _session.PrivacyMode ? "\uED1A" : "\uE890";
        var label = Display.S(_session.PrivacyMode
            ? "NavPrivacyDisable"
            : "NavPrivacyEnable");
        PrivacyNavItem.Content = label;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PrivacyNavItem, label);
    }

    private void RegisterAccelerators()
    {
        AddAccelerator(Windows.System.VirtualKey.R, Windows.System.VirtualKeyModifiers.Control,
            (s, e) => { _ = _session.TriggerSyncAsync(); e.Handled = true; });
        AddAccelerator(Windows.System.VirtualKey.F5, Windows.System.VirtualKeyModifiers.None,
            (_, e) => { _session.NotifyDataRefreshed(); e.Handled = true; });
        AddAccelerator(Windows.System.VirtualKey.Number1, Windows.System.VirtualKeyModifiers.Control,
            (_, e) => { Nav.SelectedItem = Nav.MenuItems[0]; e.Handled = true; });
        AddAccelerator(Windows.System.VirtualKey.Number2, Windows.System.VirtualKeyModifiers.Control,
            (_, e) => { Nav.SelectedItem = Nav.MenuItems[1]; e.Handled = true; });
        AddAccelerator(Windows.System.VirtualKey.Number3, Windows.System.VirtualKeyModifiers.Control,
            (_, e) => { Nav.SelectedItem = Nav.MenuItems[2]; e.Handled = true; });
        AddAccelerator(Windows.System.VirtualKey.Number4, Windows.System.VirtualKeyModifiers.Control,
            (_, e) => { Nav.SelectedItem = Nav.MenuItems[3]; e.Handled = true; });
        AddAccelerator(Windows.System.VirtualKey.Number5, Windows.System.VirtualKeyModifiers.Control,
            (_, e) => { Nav.SelectedItem = Nav.MenuItems[4]; e.Handled = true; });
        AddAccelerator(Windows.System.VirtualKey.Number6, Windows.System.VirtualKeyModifiers.Control,
            (_, e) => { Nav.SelectedItem = Nav.MenuItems[5]; e.Handled = true; });
        AddAccelerator(Windows.System.VirtualKey.Number7, Windows.System.VirtualKeyModifiers.Control,
            (_, e) => { Nav.SelectedItem = Nav.MenuItems[6]; e.Handled = true; });
        // Ctrl+L: focus the current page's search field (SPEC §14.3). Pages
        // without a search field hand off to Accounts.
        AddAccelerator(Windows.System.VirtualKey.L, Windows.System.VirtualKeyModifiers.Control,
            (_, e) =>
            {
                if (ContentFrame.Content is TransactionsPage transactions)
                {
                    transactions.FocusSearch();
                    e.Handled = true;
                    return;
                }
                if (ContentFrame.Content is not AccountsPage) Nav.SelectedItem = Nav.MenuItems[MenuIndex("accounts")];
                (ContentFrame.Content as AccountsPage)?.FocusSearch();
                e.Handled = true;
            });
        // Ctrl+, : Settings (SPEC §14.3).
        AddAccelerator((Windows.System.VirtualKey)0xBC, Windows.System.VirtualKeyModifiers.Control,
            (_, e) => { NavigateToSettings(); e.Handled = true; });
        // Alt+Left: back (SPEC §14.3).
        AddAccelerator(Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.Menu,
            (_, e) => { if (ContentFrame.CanGoBack) ContentFrame.GoBack(); e.Handled = true; });
    }

    private void AddAccelerator(
        Windows.System.VirtualKey key,
        Windows.System.VirtualKeyModifiers modifiers,
        TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += handler;
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SaveWindowSettings();
        if (_exitRequested) return;

        // The close button means "keep syncing in the notification area".
        // Only the tray Exit command tears down the sidecar and process.
        args.Cancel = true;
        AppWindow.Hide();
        AppLog.Info("App", "Main window hidden to the notification area.");
    }

    private void SaveWindowSettings()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                _session.Settings.WindowMaximized = p.State == OverlappedPresenterState.Maximized;
                if (p.State == OverlappedPresenterState.Restored)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    var scale = Math.Max(1.0, GetDpiForWindow(hwnd) / 96.0);
                    _session.Settings.WindowWidth = (int)(AppWindow.Size.Width / scale);
                    _session.Settings.WindowHeight = (int)(AppWindow.Size.Height / scale);
                }
            }
            _session.Settings.Save();
        }
        catch { /* never block shutdown on settings persistence */ }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        AppLog.Info("App", "Main window closing after explicit exit.");
        _session.PropertyChanged -= Session_PropertyChanged;
        SaveWindowSettings();

        // Kill the sidecar immediately. Waiting on the HTTP shutdown from the
        // UI thread froze the window for seconds after tray Exit.
        _session.ShutdownFast();
        AppLog.Info("App", "Shutdown completed.");
    }
}
