using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using AssetScope.App.Controls;
using AssetScope.App.Services;
using AssetScope.Core;
using AssetScope.Core.Api;
using AssetScope.Core.Diagnostics;

namespace AssetScope.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly AppSession _session = AppSession.Current;
    private bool _initializing = true;
    private bool _snapTradeConfigured;

    public SettingsPage()
    {
        InitializeComponent();
        Header.Title = Display.S("SettingsTitle");
        Header.Subtitle = Display.S("SettingsSubtitle");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            StartOnHomeToggle, Display.S("StartOnHomeLabel/Text"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            StartupToggle, Display.S("StartupToggleLabel/Text"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            LaunchSyncToggle, Display.S("LaunchSyncToggleLabel/Text"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            DailySyncToggle, Display.S("DailySyncToggleLabel/Text"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            DailySyncTime, Display.S("DailySyncTimeLabel/Text"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            LabelSearchBox, Display.S("SettingsLabelsSearch/PlaceholderText"));
        Loaded += (_, _) => NameExpanderHeaders();
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        // Page width excludes the NavigationView pane. Use a deterministic
        // content breakpoint rather than AdaptiveTrigger's window metric.
        var wide = PageLayoutMetrics.ContentWidth(width) >= 1080;
        PrimaryColumn.Width = wide
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(1, GridUnitType.Star);
        SecondaryColumn.Width = wide
            ? new GridLength(1.12, GridUnitType.Star)
            : new GridLength(0);
        Grid.SetColumn(SettingsPrimary, wide ? 1 : 0);
        Grid.SetRow(SettingsPrimary, wide ? 0 : 1);
        Grid.SetColumn(SettingsSecondary, 0);
        Grid.SetRow(SettingsSecondary, 0);
    }

    private void ManageManualAccounts_Click(object sender, RoutedEventArgs e) =>
        (App.Window as MainWindow)?.NavigateToAccounts();

    /// <summary>
    /// Expander headers here are icon+text panels; UIA does not derive a name
    /// from panels, so every expander and its internal header toggle gets the
    /// header's title text as its accessible name (SPEC §24.1).
    /// </summary>
    private void NameExpanderHeaders()
    {
        foreach (var expander in FindDescendants<Expander>(this))
        {
            var headerText = expander.Header is FrameworkElement header
                ? FindDescendants<TextBlock>(header).FirstOrDefault()?.Text
                : null;
            if (string.IsNullOrEmpty(headerText)) continue;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expander, headerText);
            foreach (var toggle in FindDescendants<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>(expander))
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, headerText);
        }
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindDescendants<T>(child)) yield return descendant;
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        AppLog.Breadcrumb("Settings", "Settings page navigated to.");
        _initializing = true;

        DataModeSubtext.Text = _session.IsDemoMode ? Display.S("ModeDemoActive") : Display.S("ModeRealActive");
        DataDirText.Text = _session.Bootstrap?.App.DataDir ?? "—";

        var theme = _session.Settings.Theme;
        SyncThemeSelection(theme);
        DisplayNameBox.Text = _session.Settings.DisplayName ?? string.Empty;
        LanguagePicker.SelectedItem = LanguagePicker.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string,
                _session.Settings.Language, StringComparison.Ordinal))
            ?? LanguagePicker.Items.OfType<ComboBoxItem>().Last();

        PrivacyToggle.IsOn = _session.PrivacyMode;
        DemoModeToggle.IsOn = _session.IsDemoMode;

        var version = typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        VersionText.Text = string.Format(Display.S("VersionFormat"), version);

        PlaidClientIdBox.Text = _session.Settings.PlaidClientId ?? "";
        SnapTradeClientIdBox.Text = _session.Settings.SnapTradeClientId ?? "";

        PrivacyDefaultToggle.IsOn = _session.Settings.StartInPrivacyMode;
        StartOnHomeToggle.IsOn = _session.Settings.StartOnHome;
        DensityToggle.IsOn = _session.Settings.Density == "compact";
        AnimationsToggle.IsOn = _session.Settings.AnimationsEnabled;
        StartupToggle.IsOn = StartupRegistrationService.IsEnabled;
        StartupToggle.IsEnabled = StartupRegistrationService.CanRegisterCurrentExecutable;
        RenderStartupState();
        RenderSecurityState();

        _ = LoadDiagnosticsAsync();
        _ = LoadPlaidAsync();
        _ = LoadSnapTradeAsync();
        _ = LoadBackupsAsync();
        _ = LoadSyncSettingsAsync();
        _ = LoadGoalAsync();
        _ = LoadLabelsAsync();
        _initializing = false;
    }

    private void RenderStartupState()
    {
        StartupStateText.Text = !StartupRegistrationService.CanRegisterCurrentExecutable
            ? Display.S("StartupNeedsInstall")
            : StartupRegistrationService.IsEnabled
                ? Display.S("StartupEnabledState")
                : Display.S("StartupDisabledState");
    }

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var requested = StartupToggle.IsOn;
        try
        {
            StartupRegistrationService.SetEnabled(requested);
            RenderStartupState();
        }
        catch (InvalidOperationException ex)
        {
            _initializing = true;
            StartupToggle.IsOn = !requested;
            _initializing = false;
            StartupStateText.Text = ex.Message;
            ShowSyncResult(InfoBarSeverity.Error, ex.Message);
        }
    }

    private void StartOnHomeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _session.Settings.StartOnHome = StartOnHomeToggle.IsOn;
        _session.Settings.Save();
    }

    // ── Backups, restore, exports (SPEC §21.4, UC-011) ──────────────────────────

    private async Task LoadBackupsAsync()
    {
        if (_session.Api is null) return;
        try
        {
            var response = await _session.Api.GetBackupsAsync();
            BackupsList.Children.Clear();
            foreach (var backup in response.Backups.Take(10))
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock
                {
                    Text = $"{backup.Id}  ·  {backup.SizeBytes / 1024} KB",
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var restore = new Button { Content = Display.S("Restore"), FontSize = 12, Padding = new Thickness(10, 2, 10, 2) };
                var id = backup.Id;
                restore.Click += async (_, _) => await RestoreBackupAsync(id);
                Grid.SetColumn(restore, 1);
                row.Children.Add(restore);
                BackupsList.Children.Add(row);
            }
            if (response.Backups.Count == 0)
            {
                BackupsList.Children.Add(new TextBlock
                {
                    Text = Display.S("NoBackups"), FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
            }
        }
        catch (ApiException) { }
    }

    private async void BackupNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Api is null) return;
        try
        {
            var backup = await _session.Api.CreateBackupAsync();
            ShowBackupResult(InfoBarSeverity.Success, string.Format(Display.S("BackupCreated"), backup.Id));
            await LoadBackupsAsync();
        }
        catch (ApiException ex) { ShowBackupResult(InfoBarSeverity.Error, ex.Message); }
    }

    private async Task RestoreBackupAsync(string backupId)
    {
        // UC-011: protective backup happens sidecar-side; the sidecar then exits
        // and we restart it against the restored database.
        var confirm = new ContentDialog
        {
            Title = Display.S("Restore"),
            Content = string.Format(Display.S("RestoreConfirmBody"), backupId),
            PrimaryButtonText = Display.S("Restore"),
            CloseButtonText = Display.S("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var result = await _session.Api!.RestoreBackupAsync(backupId);
            ShowBackupResult(InfoBarSeverity.Informational,
                string.Format(Display.S("RestoreRestarting"), result.ProtectiveBackupId));
            await _session.StartAsync(); // fresh sidecar over the restored database
            _session.NotifyDataRefreshed();
            ShowBackupResult(InfoBarSeverity.Success, Display.S("RestoreDone"));
            await LoadBackupsAsync();
        }
        catch (ApiException ex) { ShowBackupResult(InfoBarSeverity.Error, ex.Message); }
    }

    private async void ExportAccountsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Api is null) return;
        try
        {
            var result = await _session.Api.ExportAccountsCsvAsync();
            ShowBackupResult(InfoBarSeverity.Success, string.Format(Display.S("ExportDoneBody"), result.Rows, result.Path));
        }
        catch (ApiException ex) { ShowBackupResult(InfoBarSeverity.Error, ex.Message); }
    }

    private async void ExportNetWorthButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Api is null) return;
        try
        {
            var result = await _session.Api.ExportNetWorthCsvAsync("all");
            ShowBackupResult(InfoBarSeverity.Success, string.Format(Display.S("ExportDoneBody"), result.Rows, result.Path));
        }
        catch (ApiException ex) { ShowBackupResult(InfoBarSeverity.Error, ex.Message); }
    }

    private void ShowBackupResult(InfoBarSeverity severity, string message)
    {
        BackupResultBar.Severity = severity;
        BackupResultBar.Message = message;
        BackupResultBar.IsOpen = true;
    }

    // ── Sync settings (SPEC §21.2/§12.4) ────────────────────────────────────────

    private async Task LoadSyncSettingsAsync()
    {
        if (_session.Api is null) return;
        try
        {
            var prefs = await _session.Api.GetSettingsAsync();
            _initializing = true;
            LaunchSyncToggle.IsOn = prefs.SyncOnLaunch;
            DailySyncToggle.IsOn = prefs.DailySyncEnabled;
            if (TimeSpan.TryParse(prefs.DailySyncTime + ":00", out var t)) DailySyncTime.Time = t;
            DecimalsToggle.IsOn = prefs.ShowDecimals;
            _initializing = false;

            var runs = await _session.Api.GetDiagnosticsAsync();
            var scheduled = runs.RecentRuns.FirstOrDefault(r => r.Trigger == "scheduled");
            LastScheduledText.Text = scheduled is null
                ? Display.S("NoScheduledRuns")
                : string.Format(Display.S("LastScheduledRun"), scheduled.StartedAt, scheduled.Status,
                    scheduled.SuccessCount, scheduled.FailureCount);
        }
        catch (ApiException) { }
    }

    // ── Savings goal (read by the Living page) ──────────────────────────────

    private async Task LoadGoalAsync()
    {
        if (_session.Api is null) return;
        try
        {
            var living = await _session.Api.GetLivingAsync();
            _initializing = true;
            LoadGoal(living.Preferences);
            _initializing = false;
        }
        catch (ApiException) { _initializing = false; }
    }

    private void LoadGoal(LivingPreferencesDto preferences)
    {
        var kind = preferences.GoalKind ?? "none";
        GoalKindPicker.SelectedItem = GoalKindPicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag as string == kind)
            ?? GoalKindPicker.Items.OfType<ComboBoxItem>().First();
        GoalLabelBox.Text = preferences.GoalLabel ?? string.Empty;
        GoalAmountBox.Text = kind == "target"
            ? preferences.GoalTargetAmount ?? string.Empty
            : preferences.GoalMonthlyContribution ?? string.Empty;
        GoalErrorText.Visibility = Visibility.Collapsed;
        UpdateGoalFieldState();
    }

    private string SelectedGoalKind =>
        (GoalKindPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "none";

    private void UpdateGoalFieldState()
    {
        var kind = SelectedGoalKind;
        GoalLabelBox.IsEnabled = kind != "none";
        GoalAmountBox.IsEnabled = kind != "none";
        GoalAmountLabel.Text = Display.S(kind == "target" ? "GoalAmountTarget" : "GoalAmountMonthly");
    }

    private async void GoalKindPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        UpdateGoalFieldState();
        await SaveGoalAsync();
    }

    private async void GoalLabelBox_LostFocus(object sender, RoutedEventArgs e) => await SaveGoalAsync();

    private async void GoalAmountBox_LostFocus(object sender, RoutedEventArgs e) => await SaveGoalAsync();

    private async Task SaveGoalAsync()
    {
        if (_initializing || _session.Api is null) return;
        var kind = SelectedGoalKind;
        var amount = GoalAmountBox.Text.Trim();
        try
        {
            await _session.Api.PutLivingPreferencesAsync(new
            {
                goalKind = kind,
                goalLabel = GoalLabelBox.Text.Trim(),
                goalMonthlyContribution = kind == "contribution" && amount.Length > 0 ? amount : null,
                goalTargetAmount = kind == "target" && amount.Length > 0 ? amount : null,
            });
            GoalErrorText.Visibility = Visibility.Collapsed;
            _session.NotifyDataRefreshed();
        }
        catch (ApiException ex)
        {
            GoalErrorText.Text = ex.Message;
            GoalErrorText.Visibility = Visibility.Visible;
        }
    }

    // ── Bills and income labels (read by the Living page) ───────────────────

    // One grid shape for the heading and every row keeps the columns aligned.
    private const double LabelKindColumnWidth = 208;
    private const double LabelAmountColumnWidth = 104;

    private List<LivingMerchantDto> _labelMerchants = [];
    private readonly HashSet<string> _pinnedLabelKeys = new(StringComparer.Ordinal);

    private sealed record LabelSuggestion(string Key, string Text)
    {
        public override string ToString() => Text;
    }

    private async Task LoadLabelsAsync()
    {
        if (_session.Api is null) return;
        try
        {
            _labelMerchants = (await _session.Api.GetLivingMerchantsAsync()).Merchants;
            RenderLabels();
        }
        catch (ApiException) { }
    }

    private void RenderLabels(string? focusKey = null)
    {
        var labelled = LivingLabels.Labelled(_labelMerchants, _pinnedLabelKeys);
        var detected = LivingLabels.Detected(_labelMerchants, _pinnedLabelKeys);
        FillLabelRows(LabelRows, labelled, focusKey);
        FillLabelRows(DetectedLabelRows, detected, focusKey: null);
        NoLabelRowsText.Visibility = labelled.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DetectedLabelsExpander.Visibility = detected.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DetectedLabelsHeaderText.Text = string.Format(Display.S("SettingsLabelsDetected"), detected.Count);
        // The header text arrives after Loaded, so the expander is named again here.
        NameExpanderHeaders();
    }

    private void FillLabelRows(StackPanel host, IReadOnlyList<LivingMerchantDto> rows, string? focusKey)
    {
        host.Children.Clear();
        if (rows.Count == 0) return;

        var heading = NewLabelGrid();
        heading.Children.Add(Ui.Caption(Display.S("SettingsLabelsColMerchant")));
        heading.Children.Add(Ui.Caption(Display.S("SettingsLabelsColKind")).At(1));
        var amountHeading = Ui.Caption(Display.S("SettingsLabelsColAmount"));
        ToolTipService.SetToolTip(amountHeading, Display.S("SettingsLabelsAmountTip"));
        heading.Children.Add(amountHeading.At(2));
        host.Children.Add(heading);
        foreach (var merchant in rows)
        {
            host.Children.Add(Ui.Divider());
            host.Children.Add(BuildLabelRow(merchant, focus: merchant.Key == focusKey));
        }
    }

    private static Grid NewLabelGrid()
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelKindColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelAmountColumnWidth) });
        return grid;
    }

    private Grid BuildLabelRow(LivingMerchantDto merchant, bool focus)
    {
        var name = Display.TransactionTitle(null, merchant.Name);
        var row = NewLabelGrid();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(row, $"LivingLabel_{merchant.Key}");

        var copy = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(Ui.Label(name));
        copy.Children.Add(Ui.Caption(LastChargeText(merchant)));
        row.Children.Add(copy);

        var kind = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var automatic = new ComboBoxItem
        {
            Content = string.Format(Display.S("SettingsLabelsAutomatic"), LabelKindText(merchant.AutomaticKind)),
            Tag = string.Empty,
        };
        kind.Items.Add(automatic);
        kind.SelectedItem = automatic;
        foreach (var option in LivingLabels.Kinds)
        {
            var item = new ComboBoxItem { Content = LabelKindText(option), Tag = option };
            kind.Items.Add(item);
            if (option == merchant.LabelledKind) kind.SelectedItem = item;
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            kind, string.Format(Display.S("SettingsLabelsKindFor"), name));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(kind, $"LivingLabelKind_{merchant.Key}");
        kind.SelectionChanged += async (_, _) =>
        {
            if (kind.SelectedItem is not ComboBoxItem { Tag: string chosen }
                || CurrentLabel(merchant.Key) is not { } current
                || chosen == (current.LabelledKind ?? string.Empty)) return;
            await SaveLabelAsync(current, LivingLabels.KindPatch(chosen));
        };
        row.Children.Add(kind.At(1));

        var amount = new TextBox
        {
            Text = merchant.MonthlyAmount ?? string.Empty,
            PlaceholderText = Display.S("SettingsLabelsAmountPlaceholder"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            amount, string.Format(Display.S("SettingsLabelsAmountFor"), name));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(amount, $"LivingLabelAmount_{merchant.Key}");
        amount.LostFocus += async (_, _) =>
        {
            if (CurrentLabel(merchant.Key) is { } current
                && LivingLabels.AmountPatch(amount.Text, current.MonthlyAmount) is { } patch)
                await SaveLabelAsync(current, patch);
        };
        row.Children.Add(amount.At(2));

        // A merchant just picked from search lands on its type picker.
        if (focus) kind.Loaded += (_, _) => kind.Focus(FocusState.Programmatic);
        return row;
    }

    private LivingMerchantDto? CurrentLabel(string key) => _labelMerchants.FirstOrDefault(m => m.Key == key);

    private static string LabelKindText(string kind) =>
        kind == "transfer" ? Display.S("SettingsLabelsKindTransfer") : Display.LivingKindLabel(kind);

    private string LastChargeText(LivingMerchantDto merchant)
    {
        if (merchant.LastDate is null || merchant.LastAmount is null) return Display.S("SettingsLabelsNoRecent");
        // Money received reads as "+$x", the same as on Living and Transactions.
        var value = Money.Parse(merchant.LastAmount);
        var amount = value < 0 && !_session.PrivacyMode
            ? "+" + Money.Format(-value, _session.ShowDecimals)
            : _session.Fmt(merchant.LastAmount);
        return $"{Display.ShortDate(merchant.LastDate)} · {amount}";
    }

    private async Task SaveLabelAsync(LivingMerchantDto merchant, Dictionary<string, object?> patch)
    {
        if (_session.Api is null) return;
        try
        {
            await _session.Api.PutLivingMerchantDefaultAsync(merchant.Key, patch);
            // The row keeps its place and its controls: rebuilding the list
            // here would drop keyboard focus in the middle of an edit.
            var index = _labelMerchants.FindIndex(m => m.Key == merchant.Key);
            if (index >= 0) _labelMerchants[index] = LivingLabels.Apply(merchant, patch);
            _pinnedLabelKeys.Add(merchant.Key);
            LabelErrorText.Visibility = Visibility.Collapsed;
            _session.NotifyDataRefreshed();
        }
        catch (ApiException ex)
        {
            LabelErrorText.Text = ex.Message;
            LabelErrorText.Visibility = Visibility.Visible;
        }
    }

    private void LabelSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        sender.ItemsSource = LivingLabels.Suggestions(_labelMerchants, _pinnedLabelKeys, sender.Text)
            .Select(m => new LabelSuggestion(m.Key, $"{Display.TransactionTitle(null, m.Name)} · {LastChargeText(m)}"))
            .ToList();
    }

    private void LabelSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // Enter with nothing highlighted takes the first match.
        var chosen = args.ChosenSuggestion as LabelSuggestion
            ?? (sender.ItemsSource as List<LabelSuggestion>)?.FirstOrDefault();
        if (chosen is null) return;
        _pinnedLabelKeys.Add(chosen.Key);
        sender.Text = string.Empty;
        sender.ItemsSource = null;
        RenderLabels(focusKey: chosen.Key);
    }

    private async void LaunchSyncToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _session.Api is null) return;
        try { await _session.Api.PatchSettingsAsync(new { syncOnLaunch = LaunchSyncToggle.IsOn }); }
        catch (ApiException ex) { ShowSyncResult(InfoBarSeverity.Error, ex.Message); }
    }

    private async void DailySyncToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _session.Api is null) return;
        var enable = DailySyncToggle.IsOn;
        try
        {
            if (enable)
            {
                await DailySyncScheduler.RegisterAsync(DailySyncTime.Time);
                ShowSyncResult(InfoBarSeverity.Success, Display.S("DailySyncEnabled"));
            }
            else
            {
                await DailySyncScheduler.UnregisterAsync();
                ShowSyncResult(InfoBarSeverity.Informational, Display.S("DailySyncDisabled"));
            }
            await _session.Api.PatchSettingsAsync(new
            {
                dailySyncEnabled = enable,
                dailySyncTime = $"{DailySyncTime.Time.Hours:00}:{DailySyncTime.Time.Minutes:00}",
            });
        }
        catch (Exception ex) when (ex is ApiException or InvalidOperationException)
        {
            // Keep the external task and persisted preference consistent. The
            // compensating action is best-effort because Task Scheduler and
            // SQLite cannot share one transaction.
            try
            {
                if (enable) await DailySyncScheduler.UnregisterAsync();
                else await DailySyncScheduler.RegisterAsync(DailySyncTime.Time);
            }
            catch { }
            _initializing = true;
            DailySyncToggle.IsOn = !enable;
            _initializing = false;
            ShowSyncResult(InfoBarSeverity.Error, ex.Message);
        }
    }

    private async void DailySyncTime_TimeChanged(object sender, TimePickerValueChangedEventArgs e)
    {
        if (_initializing || _session.Api is null) return;
        try
        {
            if (DailySyncToggle.IsOn) await DailySyncScheduler.RegisterAsync(e.NewTime);
            await _session.Api.PatchSettingsAsync(new { dailySyncTime = $"{e.NewTime.Hours:00}:{e.NewTime.Minutes:00}" });
        }
        catch (Exception ex) when (ex is ApiException or InvalidOperationException)
        {
            if (DailySyncToggle.IsOn)
            {
                try { await DailySyncScheduler.RegisterAsync(e.OldTime); } catch { }
            }
            ShowSyncResult(InfoBarSeverity.Error, ex.Message);
        }
    }

    private void ShowSyncResult(InfoBarSeverity severity, string message)
    {
        SyncResultBar.Severity = severity;
        SyncResultBar.Message = message;
        SyncResultBar.IsOpen = true;
    }

    // ── Appearance & privacy extras (SPEC §21.1) ────────────────────────────────

    private void PrivacyDefaultToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _session.Settings.StartInPrivacyMode = PrivacyDefaultToggle.IsOn;
        _session.Settings.Save();
    }

    private async void DecimalsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _session.Api is null) return;
        try
        {
            await _session.Api.PatchSettingsAsync(new { showDecimals = DecimalsToggle.IsOn });
            _session.NotifyDataRefreshed();
        }
        catch (ApiException) { }
    }

    private void DensityToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _session.Settings.Density = DensityToggle.IsOn ? "compact" : "comfortable";
        _session.Settings.Save();
        // Density is chosen before the first control renders, so it cannot be
        // reapplied to a live visual tree. Restart for the user rather than
        // leaving the toggle visibly disagreeing with the window, as theme and
        // language already do.
        RestartToApplyPresentation();
    }

    private void AnimationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _session.Settings.AnimationsEnabled = AnimationsToggle.IsOn;
        _session.Settings.Save();
    }

    // ── Privacy & security (SPEC §21.5) ─────────────────────────────────────────

    private void RenderSecurityState()
    {
        var secretStored = AssetScope.Core.Security.WindowsCredentials.Read(
            AssetScope.Core.Security.WindowsCredentials.PlaidSecretTarget) is not null;
        var snapTradeStored = AssetScope.Core.Security.WindowsCredentials.Read(
            AssetScope.Core.Security.WindowsCredentials.SnapTradeConsumerKeyTarget) is not null;
        var state =
            (secretStored ? Display.S("SecretInCredMan") : Display.S("SecretNotStored")) + "\n"
            + Display.S(snapTradeStored ? "SnapTradeConsumerKeyStored" : "SnapTradeConsumerKeyNotStored") + "\n"
            + Display.S("TokenEncryptionState");
        PrivacySecurityTooltip.Text = state + "\n\n" + Display.S("SecurityExplainer");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            PrivacySecurityInfo, Display.S("AdvancedSettingsHeader/Text"));
    }

    private async Task<bool> ConfirmClearCredentialsAsync(string title, string body)
    {
        var confirm = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = Display.S("Delete"),
            CloseButtonText = Display.S("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await confirm.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void ClearCredentialsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmClearCredentialsAsync(
                Display.S("ClearCredentials/Content"),
                Display.S("ClearCredentialsBody"))) return;
        _session.ClearPlaidSecret();
        RenderSecurityState();
        await LoadPlaidAsync();
    }

    private async void ClearSnapTradeCredentialsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmClearCredentialsAsync(
                Display.S("ClearSnapTradeCredentials/Content"),
                Display.S("ClearSnapTradeCredentialsBody"))) return;
        _session.ClearSnapTradeCredentials();
        RenderSecurityState();
        await LoadSnapTradeAsync();
    }

    private async void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Api is null) return;
        try
        {
            await _session.Api.ClearLogsAsync();
            await LoadDiagnosticsAsync();
        }
        catch (ApiException) { }
    }

    private async void DeleteAllDataButton_Click(object sender, RoutedEventArgs e)
    {
        // High-risk wipe (§21.5): secondary confirmation typing the product name.
        var box = new TextBox { PlaceholderText = "AssetScope" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(new TextBlock { Text = Display.S("DeleteAllBody"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            Title = Display.S("DeleteAllData/Content"),
            Content = panel,
            PrimaryButtonText = Display.S("Delete"),
            CloseButtonText = Display.S("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!string.Equals(box.Text.Trim(), "AssetScope", StringComparison.OrdinalIgnoreCase))
        {
            ShowBackupResult(InfoBarSeverity.Warning, Display.S("DeleteAllNameMismatch"));
            return;
        }
        try
        {
            await _session.Api!.DeleteAllDataAsync();
            _session.ClearPlaidSecret();
            _session.ClearSnapTradeCredentials();
            _session.Settings.OnboardingDismissed = false;
            _session.Settings.Save();
            await _session.StartAsync(); // fresh sidecar over an empty directory
            _session.NotifyDataRefreshed();
            App.Window?.ShowSetupGate(allowCancel: false);
        }
        catch (ApiException ex) { ShowBackupResult(InfoBarSeverity.Error, ex.Message); }
    }

    private async void ImportEnvButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Api is null) return;
        try
        {
            // Secret crosses the authed loopback once, then lives in Credential
            // Manager (SPEC §13.1).
            var imported = await _session.Api.ImportEnvCredentialsAsync();
            await _session.SavePlaidConfigAsync(imported.Environment, imported.ClientId, imported.Secret);
            ShowPlaidResult(InfoBarSeverity.Success, Display.S("ImportEnvDone"));
            RenderSecurityState();
            await LoadPlaidAsync();
        }
        catch (ApiException ex) { ShowPlaidResult(InfoBarSeverity.Error, ex.Message); }
    }

    // ── Bank connections (SPEC §13) ─────────────────────────────────────────────

    private async Task LoadPlaidAsync()
    {
        if (_session.Api is null) return;
        var demo = _session.IsDemoMode;
        BankDemoHint.Visibility = demo ? Visibility.Visible : Visibility.Collapsed;
        PlaidAdvancedExpander.Visibility = demo ? Visibility.Collapsed : Visibility.Visible;
        ConnectBankButton.IsEnabled = !demo;
        if (demo)
        {
            BankStatusSubtext.Text = Display.S("BankDemoStatus");
            InstitutionsList.Children.Clear();
            return;
        }
        try
        {
            var status = await _session.Api.GetPlaidConfigStatusAsync();
            BankStatusSubtext.Text = status.Configured
                ? string.Format(Display.S("BankStatusConfigured"), status.Environment, status.ClientIdSuffix ?? "????")
                : Display.S("BankStatusNotConfigured");
            ConnectBankButton.Content = status.Configured
                ? Display.S("ConnectBank/Content")
                : Display.S("SetUpAutomaticSync");
            ConnectBankButton.IsEnabled = true;
            // Offer .env import when upstream credentials exist (SPEC §13.1 MAY).
            ImportEnvButton.Visibility = status.EnvFileDetected ? Visibility.Visible : Visibility.Collapsed;
            var institutionCount = await RenderInstitutionsAsync();
            if (status.Configured && institutionCount > 0)
            {
                BankStatusSubtext.Text = string.Format(
                    Display.S("BankInstitutionsConnectedStatus"), institutionCount);
                ConnectBankButton.Content = Display.S("AddAnotherBank");
            }
        }
        catch (ApiException ex)
        {
            BankStatusSubtext.Text = ex.Message;
        }
    }

    private async Task<int> RenderInstitutionsAsync()
    {
        if (_session.Api is null) return 0;
        var response = await _session.Api.GetPlaidItemsAsync();
        InstitutionsList.Children.Clear();

        if (response.Items.Count == 0)
        {
            InstitutionsList.Children.Add(new TextBlock
            {
                Text = Display.S("NoInstitutions"),
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            return 0;
        }

        foreach (var item in response.Items)
        {
            var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new InstitutionLogo(item.InstitutionName, "bank", 34));

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = item.InstitutionName ?? item.ItemId,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13,
            });
            var statusText = Display.S($"ItemStatus_{item.Status}");
            info.Children.Add(new TextBlock
            {
                Text = $"{statusText} · {string.Format(Display.S("ItemAccounts"), item.AccountCount)} · {Display.RelativeTime(item.LastSuccessAt)}",
                Style = Ui.Style("CaptionStyle"),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                    item.Status == "attention" ? "NegativeChangeBrush" : "TextFillColorSecondaryBrush"],
            });
            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (item.Status != "disconnected")
            {
                var reconnect = new Button { Content = Display.S("Reconnect"), FontSize = 12, Padding = new Thickness(10, 4, 10, 4) };
                var itemId = item.ItemId;
                reconnect.Click += async (_, _) => await RunLinkFlowAsync(() => _session.Api!.ReconnectItemAsync(itemId));
                buttons.Children.Add(reconnect);

                var disconnect = new Button { Content = Display.S("Disconnect"), FontSize = 12, Padding = new Thickness(10, 4, 10, 4) };
                disconnect.Click += async (_, _) => await DisconnectItemAsync(item);
                buttons.Children.Add(disconnect);
            }
            var deleteData = new Button { Content = Display.S("DeleteData"), FontSize = 12, Padding = new Thickness(10, 4, 10, 4) };
            deleteData.Click += async (_, _) => await DeleteItemDataAsync(item);
            buttons.Children.Add(deleteData);

            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);
            InstitutionsList.Children.Add(grid);
        }
        return response.Items.Count;
    }

    private async void SaveTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Api is null) return;
        // Production is the product; settings.json can override for development.
        var environment = _session.Settings.PlaidEnvironment == "sandbox" ? "sandbox" : "production";
        var clientId = PlaidClientIdBox.Text.Trim();
        var secret = PlaidSecretBox.Password;
        if (clientId.Length == 0 || secret.Length == 0)
        {
            ShowPlaidResult(InfoBarSeverity.Warning, Display.S("PlaidMissingFields"));
            return;
        }
        PlaidBusyRing.IsActive = true;
        SaveTestButton.IsEnabled = false;
        try
        {
            await _session.SavePlaidConfigAsync(environment, clientId, secret);
            PlaidSecretBox.Password = "";
            ShowPlaidResult(InfoBarSeverity.Success, Display.S("PlaidTestOk"));
            await LoadPlaidAsync();
        }
        catch (ApiException ex)
        {
            ShowPlaidResult(InfoBarSeverity.Error, ex.Message);
        }
        finally
        {
            PlaidBusyRing.IsActive = false;
            SaveTestButton.IsEnabled = true;
        }
    }

    private void ConnectBankButton_Click(object sender, RoutedEventArgs e) =>
        OpenBankSetup();

    private static void OpenBankSetup()
    {
        AppLog.Breadcrumb("Settings", "Connect bank invoked.");
        App.Window?.ShowSetupGate(allowCancel: true, startAutomatic: true);
    }

    /// <summary>
    /// Shared connect/reconnect flow: create a session, open the one-time URL in
    /// the system browser (SPEC §13.3 — never an embedded WebView), poll once per
    /// second until a terminal state, then refresh.
    /// </summary>
    private async Task RunLinkFlowAsync(Func<Task<LinkSessionDto>> createSession)
    {
        ConnectBankButton.IsEnabled = false;
        LinkStatusText.Visibility = Visibility.Visible;
        try
        {
            var session = await createSession();
            await Windows.System.Launcher.LaunchUriAsync(new Uri(session.Url));
            LinkStatusText.Text = Display.S("LinkWaiting");

            for (var i = 0; i < 620; i++)
            {
                await Task.Delay(1000);
                session = await _session.Api!.GetLinkSessionAsync(session.Id);
                if (session.State != "waitingForUser") break;
            }

            switch (session.State)
            {
                case "completed":
                    var summary = PlaidLinkPresentation.InstitutionSummary(session);
                    var completed = string.Format(Display.S("LinkCompleted"), summary);
                    var partialFailure = PlaidLinkPresentation.PartialFailure(session);
                    LinkStatusText.Text = partialFailure is null
                        ? completed
                        : $"{completed} {partialFailure}";
                    ShowPlaidResult(partialFailure is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                        LinkStatusText.Text);
                    _session.NotifyDataRefreshed();
                    break;
                case "expired":
                    LinkStatusText.Text = Display.S("LinkExpired");
                    break;
                case "cancelled":
                    LinkStatusText.Text = Display.S("LinkCancelled");
                    break;
                default:
                    LinkStatusText.Text = string.Format(Display.S("LinkFailed"), session.Error ?? "");
                    break;
            }
            await LoadPlaidAsync();
        }
        catch (ApiException ex)
        {
            LinkStatusText.Text = ex.Message;
        }
        finally
        {
            ConnectBankButton.IsEnabled = true;
        }
    }

    private async Task DisconnectItemAsync(PlaidItemDto item)
    {
        var dialog = new ContentDialog
        {
            Title = Display.S("Disconnect"),
            Content = string.Format(Display.S("DisconnectConfirmBody"), item.InstitutionName ?? item.ItemId),
            PrimaryButtonText = Display.S("Disconnect"),
            CloseButtonText = Display.S("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _session.Api!.DisconnectItemAsync(item.ItemId);
            await LoadPlaidAsync();
        }
        catch (ApiException ex) { ShowPlaidResult(InfoBarSeverity.Error, ex.Message); }
    }

    private async Task DeleteItemDataAsync(PlaidItemDto item)
    {
        try
        {
            // Two-phase delete (SPEC §13.5.2): show real counts before confirming.
            var preview = await _session.Api!.DeleteItemDataAsync(item.ItemId, confirm: false);
            var p = preview.Preview;
            var dialog = new ContentDialog
            {
                Title = Display.S("DeleteData"),
                Content = string.Format(Display.S("DeleteDataConfirmBody"),
                    item.InstitutionName ?? item.ItemId, p.AccountCount, p.TransactionCount, p.SnapshotCount),
                PrimaryButtonText = Display.S("Delete"),
                CloseButtonText = Display.S("Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await _session.Api!.DeleteItemDataAsync(item.ItemId, confirm: true);
            ShowPlaidResult(InfoBarSeverity.Success, Display.S("DeleteDataDone"));
            await LoadPlaidAsync();
            _session.NotifyDataRefreshed();
        }
        catch (ApiException ex) { ShowPlaidResult(InfoBarSeverity.Error, ex.Message); }
    }

    private void ShowPlaidResult(InfoBarSeverity severity, string message)
    {
        PlaidResultBar.Severity = severity;
        PlaidResultBar.Message = message;
        PlaidResultBar.IsOpen = true;
    }

    // ── Investment connections (SnapTrade Personal API Key) ──────────────────

    private async Task LoadSnapTradeAsync()
    {
        if (_session.Api is null) return;
        var demo = _session.IsDemoMode;
        SnapTradeAdvancedExpander.Visibility = demo ? Visibility.Collapsed : Visibility.Visible;
        SnapTradeOnboardingPanel.Visibility = demo ? Visibility.Collapsed : Visibility.Visible;
        ConnectBrokerageButton.IsEnabled = !demo;
        if (demo)
        {
            _snapTradeConfigured = false;
            SnapTradeStatusText.Text = Display.S("SnapTradeDemoStatus");
            SnapTradeConnectionsList.Children.Clear();
            return;
        }

        try
        {
            var status = await _session.Api.GetSnapTradeConfigStatusAsync();
            _snapTradeConfigured = status.Configured;
            SnapTradeOnboardingPanel.Visibility = status.Configured
                ? Visibility.Collapsed
                : Visibility.Visible;
            // The API key is required setup, not an optional advanced tweak.
            // Pre-expand it while unconfigured so opening the SnapTrade row
            // immediately reveals the four-step Personal flow and both fields.
            SnapTradeAdvancedExpander.IsExpanded = !status.Configured;
            SnapTradeStatusText.Text = status.Configured
                ? string.Format(Display.S("SnapTradeConfiguredStatus"), status.ClientIdSuffix ?? "????")
                : Display.S("SnapTradeNotConfigured");
            ConnectBrokerageButton.Content = Display.S(status.Configured ? "ConnectBrokerage/Content" : "SnapTradeConfigureFirst");
            await RenderSnapTradeConnectionsAsync();
        }
        catch (ApiException ex)
        {
            SnapTradeStatusText.Text = ex.Message;
        }
    }

    private async Task RenderSnapTradeConnectionsAsync()
    {
        if (_session.Api is null) return;
        SnapTradeConnectionsList.Children.Clear();
        var response = await _session.Api.GetSnapTradeConnectionsAsync();
        if (_snapTradeConfigured && response.Connections.Count > 0)
        {
            SnapTradeStatusText.Text = string.Format(
                Display.S("SnapTradeBrokeragesConnectedStatus"),
                response.Connections.Count);
            ConnectBrokerageButton.Content = Display.S("AddBrokerage");
        }
        if (response.Connections.Count == 0)
        {
            SnapTradeConnectionsList.Children.Add(new TextBlock
            {
                Text = Display.S(_snapTradeConfigured ? "SnapTradeNoConnections" : "SnapTradeConnectAfterKey"),
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            return;
        }

        foreach (var connection in response.Connections)
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 8, 4, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new InstitutionLogo(connection.BrokerageName, "investment", 34));

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = connection.DisplayName ?? connection.BrokerageName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            info.Children.Add(new TextBlock
            {
                Text = connection.Disabled
                    ? Display.S("SnapTradeReconnectRequired")
                    : string.Format(Display.S("SnapTradeConnectionSummary"), connection.AccountCount,
                        Display.RelativeTime(connection.LastSuccessAt)),
                Style = Ui.Style("CaptionStyle"),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                    connection.Disabled ? "ReferenceRedBrush" : "TextFillColorSecondaryBrush"],
            });
            Grid.SetColumn(info, 1); row.Children.Add(info);

            var reconnect = new Button
            {
                Content = Display.S(connection.Disabled ? "Reconnect" : "Manage"),
                FontSize = 12,
                Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var connectionId = connection.ConnectionId;
            reconnect.Click += async (_, _) => await BeginSnapTradePortalAsync(connectionId);
            Grid.SetColumn(reconnect, 2); row.Children.Add(reconnect);
            SnapTradeConnectionsList.Children.Add(row);
        }
    }

    private async void SaveSnapTradeButton_Click(object sender, RoutedEventArgs e)
    {
        var clientId = SnapTradeClientIdBox.Text.Trim();
        var consumerKey = SnapTradeConsumerKeyBox.Password;
        if (clientId.Length == 0 || consumerKey.Length == 0)
        {
            ShowSnapTradeResult(InfoBarSeverity.Warning, Display.S("SnapTradeMissingFields"));
            return;
        }
        SnapTradeBusyRing.IsActive = true;
        SaveSnapTradeButton.IsEnabled = false;
        try
        {
            var status = await _session.SaveSnapTradeConfigAsync(clientId, consumerKey);
            SnapTradeConsumerKeyBox.Password = "";
            _snapTradeConfigured = true;
            ShowSnapTradeResult(InfoBarSeverity.Success,
                status.CanAccessAccountHistory
                    ? Display.S("SnapTradeCredentialsFullAccess")
                    : Display.S("SnapTradeCredentialsLimitedAccess"));
            RenderSecurityState();
            await LoadSnapTradeAsync();
            SnapTradeAdvancedExpander.IsExpanded = false;
        }
        catch (ApiException ex)
        {
            ShowSnapTradeResult(InfoBarSeverity.Error, ex.Message);
        }
        finally
        {
            SnapTradeBusyRing.IsActive = false;
            SaveSnapTradeButton.IsEnabled = true;
        }
    }

    private async void ConnectBrokerageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_snapTradeConfigured)
        {
            SnapTradeExpander.IsExpanded = true;
            SnapTradeAdvancedExpander.IsExpanded = true;
            SnapTradeClientIdBox.Focus(FocusState.Programmatic);
            return;
        }
        await BeginSnapTradePortalAsync(null);
    }

    private async Task BeginSnapTradePortalAsync(string? reconnectConnectionId)
    {
        if (_session.Api is null) return;
        SnapTradeBusyRing.IsActive = true;
        ConnectBrokerageButton.IsEnabled = false;
        try
        {
            var portal = reconnectConnectionId is null
                ? await _session.Api.CreateSnapTradePortalAsync()
                : await _session.Api.ReconnectSnapTradeAsync(reconnectConnectionId);
            var launched = await Windows.System.Launcher.LaunchUriAsync(new Uri(portal.Url));
            if (!launched) throw new InvalidOperationException(Display.S("SnapTradePortalLaunchFailed"));
            CompleteSnapTradeButton.Visibility = Visibility.Visible;
            ShowSnapTradeResult(InfoBarSeverity.Informational,
                Display.S("SnapTradePortalOpened"));
        }
        catch (ApiException ex)
        {
            ShowSnapTradeResult(InfoBarSeverity.Error, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            ShowSnapTradeResult(InfoBarSeverity.Error, ex.Message);
        }
        finally
        {
            SnapTradeBusyRing.IsActive = false;
            ConnectBrokerageButton.IsEnabled = true;
        }
    }

    private async void CompleteSnapTradeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Api is null) return;
        SnapTradeBusyRing.IsActive = true;
        CompleteSnapTradeButton.IsEnabled = false;
        try
        {
            var run = await _session.Api.StartInvestmentSyncAsync("portal-return");
            for (var i = 0; i < 600 && run.Status == "running"; i++)
            {
                await Task.Delay(1000);
                run = (await _session.Api.GetInvestmentSyncStatusAsync()).Run ?? run;
            }
            if (run.Status == "failed")
                throw new InvalidOperationException(string.Format(Display.S("SnapTradeSyncFailed"),
                    run.ErrorCode ?? Display.S("UnknownError")));
            _session.Bootstrap = await _session.Api.GetBootstrapAsync();
            _session.LastSyncedAt = _session.Bootstrap.Sync.InvestmentLastSuccessAt
                ?? _session.Bootstrap.Sync.LastSuccessAt;
            _session.NotifyDataRefreshed();
            CompleteSnapTradeButton.Visibility = Visibility.Collapsed;
            ShowSnapTradeResult(InfoBarSeverity.Success,
                string.Format(Display.S("SnapTradeSyncComplete"), run.AccountCount, run.PositionCount));
            await LoadSnapTradeAsync();
        }
        catch (ApiException ex)
        {
            ShowSnapTradeResult(InfoBarSeverity.Error, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            ShowSnapTradeResult(InfoBarSeverity.Error, ex.Message);
        }
        finally
        {
            SnapTradeBusyRing.IsActive = false;
            CompleteSnapTradeButton.IsEnabled = true;
        }
    }

    private void ShowSnapTradeResult(InfoBarSeverity severity, string message)
    {
        SnapTradeResultBar.Severity = severity;
        SnapTradeResultBar.Message = message;
        SnapTradeResultBar.IsOpen = true;
    }

    private async Task LoadDiagnosticsAsync()
    {
        if (_session.Api is null) return;
        try
        {
            var d = await _session.Api.GetDiagnosticsAsync();
            // Port may be shown; the session token never (SPEC §21.6).
            var lines = new List<string>
            {
                $"sidecar   {d.SidecarVersion} (node {d.NodeVersion}, pid {d.Pid})",
                $"protocol  {d.ProtocolVersion} · schema v{d.SchemaVersion}",
                $"mode      {d.Mode}",
                $"dataDir   {d.DataDir}",
            };
            if (d.RecentRuns.Count > 0)
            {
                lines.Add("recent sync runs:");
                foreach (var run in d.RecentRuns)
                    lines.Add($"  {run.StartedAt}  {run.Trigger,-9} {run.Status,-9} ok:{run.SuccessCount} fail:{run.FailureCount}");
            }
            DiagnosticsText.Text = string.Join('\n', lines);

            var logs = await _session.Api.GetLogsAsync(50);
            LogsText.Text = logs.Entries.Count == 0
                ? Display.S("NoLogs")
                : string.Join('\n', logs.Entries.Select(l => $"{l.At} [{l.Level}] {l.Message}"));
        }
        catch (ApiException ex)
        {
            DiagnosticsText.Text = ex.Message;
        }
    }

    private void ThemePicker_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_initializing) return;
        var theme = sender.SelectedItem?.Tag as string;
        if (string.IsNullOrEmpty(theme)) return;
        _session.Settings.Theme = theme;
        _session.Settings.Save();
        RestartToApplyPresentation();
    }

    internal void SyncThemeSelection(string theme)
    {
        var wasInitializing = _initializing;
        _initializing = true;
        ThemePicker.SelectedItem = theme switch
        {
            "light" => ThemeLightItem,
            "dark" => ThemeDarkItem,
            _ => ThemeSystemItem,
        };
        _initializing = wasInitializing;
    }

    internal void SyncPrivacySelection(bool enabled)
    {
        var wasInitializing = _initializing;
        _initializing = true;
        PrivacyToggle.IsOn = enabled;
        _initializing = wasInitializing;
    }

    private void DisplayNameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _session.Settings.DisplayName = string.IsNullOrWhiteSpace(DisplayNameBox.Text)
            ? null
            : DisplayNameBox.Text.Trim();
        _session.Settings.Save();
        _session.NotifyDataRefreshed();
    }

    private void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguagePicker.SelectedItem is not ComboBoxItem item
            || item.Tag is not string language) return;
        _session.Settings.Language = language;
        _session.Settings.Save();
        RestartToApplyPresentation();
    }

    private void RestartToApplyPresentation()
    {
        _session.Settings.ResumeLastPageOnce = true;
        _session.Settings.LastPage = "settings";
        _session.Settings.Save();
        (Application.Current as App)?.RestartForSettingsChange();
    }

    private void PrivacyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _session.PrivacyMode = PrivacyToggle.IsOn;
    }

    private async void DemoModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var demo = DemoModeToggle.IsOn;
        DemoModeToggle.IsEnabled = false;
        try
        {
            await _session.SwitchModeAsync(demo ? "demo" : "real");
            DataModeSubtext.Text = _session.IsDemoMode
                ? Display.S("ModeDemoActive")
                : Display.S("ModeRealActive");
            DataDirText.Text = _session.Bootstrap?.App.DataDir ?? "—";
            await LoadPlaidAsync();
            await LoadSnapTradeAsync();
            _session.NotifyDataRefreshed();
        }
        catch (Exception ex)
        {
            AppLog.Error("Settings", "Failed to switch data mode.", ex);
            _initializing = true;
            DemoModeToggle.IsOn = _session.IsDemoMode;
            _initializing = false;
            ShowBackupResult(InfoBarSeverity.Error,
                string.IsNullOrWhiteSpace(ex.Message)
                    ? Display.S("DemoModeSwitchFailed")
                    : ex.Message);
        }
        finally
        {
            DemoModeToggle.IsEnabled = true;
        }
    }

    private async void OpenDataDirButton_Click(object sender, RoutedEventArgs e)
    {
        var dir = _session.Bootstrap?.App.DataDir;
        if (dir is not null && Directory.Exists(dir))
            await Windows.System.Launcher.LaunchFolderPathAsync(dir);
    }

    private async void ViewLicense_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.txt");
        if (File.Exists(path))
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            await Windows.System.Launcher.LaunchFileAsync(file);
            return;
        }

        await new ContentDialog
        {
            Title = Display.S("OpenSourceLicenseTitle/Text"),
            Content = Display.S("LicenseUnavailable"),
            CloseButtonText = Display.S("OK"),
            XamlRoot = XamlRoot,
        }.ShowAsync();
    }

    private async void ViewPrivacy_Click(object sender, RoutedEventArgs e)
    {
        await new ContentDialog
        {
            Title = Display.S("PrivacyPolicyTitle/Text"),
            Content = Display.S("PrivacyPolicyBody"),
            CloseButtonText = Display.S("OK"),
            XamlRoot = XamlRoot,
        }.ShowAsync();
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(DiagnosticsText.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }
}
