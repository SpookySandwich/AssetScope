using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using AssetScope.App.Services;
using Windows.Foundation;

namespace AssetScope.App.Controls;

/// <summary>
/// Shared page header. Refresh is one native Button with an explicit visual
/// state machine: arrow, rotating arrow, brief success check, then arrow.
/// </summary>
public sealed partial class PageHeader : UserControl
{
    private enum RefreshVisualState
    {
        Idle,
        Syncing,
        Succeeded,
    }

    private readonly TextBlock _title = new();
    private readonly TextBlock _subtitle = new();
    private readonly TextBlock _updated = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _refresh = new();
    private readonly FontIcon _refreshIcon = new()
    {
        Glyph = "\uE72C",
        FontSize = 14,
        RenderTransformOrigin = new Point(0.5, 0.5),
    };
    private readonly RotateTransform _refreshRotation = new();
    private readonly SymbolIcon _completedIcon = new(Symbol.Accept);
    private readonly Storyboard _spinStoryboard = new();
    private readonly DispatcherTimer _successTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(1400),
    };
    private readonly Button _more = new();
    private readonly MenuFlyout _moreMenu = new();
    private readonly Border _demoChip;
    private readonly DispatcherTimer _relativeTimeTimer = new()
    {
        Interval = TimeSpan.FromSeconds(30),
    };
    private RefreshVisualState? _refreshState;
    private bool _refreshOperationActive;

    public string Title { set => _title.Text = value; }
    public string Subtitle { set => _subtitle.Text = value; }

    public PageHeader()
    {
        var session = AppSession.Current;

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            _updated, "HeaderSyncStatus");

        _title.Style = (Style)Application.Current.Resources["PageTitleStyle"];
        _subtitle.Style = (Style)Application.Current.Resources["PageSubtitleStyle"];
        _subtitle.Margin = new Thickness(0, 4, 0, 0);
        // Use a style containing ThemeResource instead of assigning a resolved
        // brush instance. The latter stayed dark after switching to light (and
        // vice versa) because this shared header is kept alive in cached pages.
        _updated.Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"];

        _demoChip = new Border
        {
            Background = (Brush)Application.Current.Resources["SystemFillColorCautionBackgroundBrush"],
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3, 10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = Display.S("DemoModeChip"), FontSize = 12 },
            Visibility = session.IsDemoMode ? Visibility.Visible : Visibility.Collapsed,
        };

        _refreshIcon.RenderTransform = _refreshRotation;
        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromMilliseconds(700)),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(spin, _refreshRotation);
        Storyboard.SetTargetProperty(spin, "Angle");
        _spinStoryboard.Children.Add(spin);

        _completedIcon.Foreground = (Brush)Application.Current.Resources["ReferenceGreenBrush"];
        _refresh.Width = 32;
        _refresh.Height = 32;
        _refresh.Padding = new Thickness(0);
        _refresh.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _refresh.BorderThickness = new Thickness(0);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            _refresh, "HeaderRefreshButton");
        _refresh.Click += async (_, _) => await RefreshAsync();
        _refresh.Content = _refreshIcon;

        _more.Content = new FontIcon { Glyph = "\uE712", FontSize = 14 };
        _more.Width = 32;
        _more.Height = 32;
        _more.Padding = new Thickness(0);
        _more.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _more.BorderThickness = new Thickness(0);
        _more.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(_more, Display.S("MoreOptions"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_more, Display.S("MoreOptions"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            _more, "HeaderMoreButton");
        _more.Flyout = _moreMenu;

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(_demoChip);
        right.Children.Add(_updated);
        right.Children.Add(_refresh);
        right.Children.Add(_more);

        var left = new StackPanel();
        left.Children.Add(_title);
        left.Children.Add(_subtitle);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        Content = grid;

        _successTimer.Tick += (_, _) =>
        {
            _successTimer.Stop();
            SetRefreshState(RefreshVisualState.Idle);
        };
        SetRefreshState(RefreshVisualState.Idle);
        UpdateStatus();
        _relativeTimeTimer.Tick += (_, _) => UpdateStatus();
        Loaded += (_, _) =>
        {
            _relativeTimeTimer.Start();
            session.PropertyChanged += SessionChanged;
            UpdateStatus();
        };
        Unloaded += (_, _) =>
        {
            _relativeTimeTimer.Stop();
            _successTimer.Stop();
            _spinStoryboard.Stop();
            session.PropertyChanged -= SessionChanged;
        };
    }

    public MenuFlyoutItem AddMenuItem(string text, Action invoked)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => invoked();
        _moreMenu.Items.Add(item);
        _more.Visibility = Visibility.Visible;
        return item;
    }

    public MenuFlyoutSubItem AddSubMenu(string text)
    {
        var item = new MenuFlyoutSubItem { Text = text };
        _moreMenu.Items.Add(item);
        _more.Visibility = Visibility.Visible;
        return item;
    }

    private async Task RefreshAsync()
    {
        if (_refreshState == RefreshVisualState.Syncing) return;

        _successTimer.Stop();
        _refreshOperationActive = true;
        SetRefreshState(RefreshVisualState.Syncing);

        var session = AppSession.Current;
        bool succeeded;
        if (session.IsDemoMode)
        {
            session.NotifyDataRefreshed();
            succeeded = true;
        }
        else
        {
            succeeded = await session.TriggerSyncAsync();
        }

        _refreshOperationActive = false;
        SetRefreshState(succeeded
            ? RefreshVisualState.Succeeded
            : RefreshVisualState.Idle);
        if (succeeded) _successTimer.Start();
    }

    private void SessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSession.IsDemoMode))
        {
            DispatcherQueue.TryEnqueue(() =>
                _demoChip.Visibility = AppSession.Current.IsDemoMode
                    ? Visibility.Visible
                    : Visibility.Collapsed);
            return;
        }
        if (e.PropertyName is nameof(AppSession.IsSyncing)
            or nameof(AppSession.LastSyncedAt)
            or nameof(AppSession.IsOffline)
            or nameof(AppSession.LastSyncError))
            DispatcherQueue.TryEnqueue(UpdateStatus);
    }

    private void UpdateStatus()
    {
        var session = AppSession.Current;
        _updated.Text = session.IsSyncing
            ? Display.S("SyncInProgress")
            : session.LastSyncError is { Length: > 0 } errorKey
                ? Display.S(errorKey)
                : session.IsOffline
                    ? Display.S("OfflineStatus")
                    : string.Format(Display.S("DataUpdated"), Display.RelativeTime(session.LastSyncedAt));

        if (session.IsSyncing)
            SetRefreshState(RefreshVisualState.Syncing);
        else if (_refreshState == RefreshVisualState.Syncing && !_refreshOperationActive)
            SetRefreshState(RefreshVisualState.Idle);
    }

    private void SetRefreshState(RefreshVisualState state)
    {
        if (_refreshState == state) return;
        _refreshState = state;

        _spinStoryboard.Stop();
        _refreshRotation.Angle = 0;
        _refresh.IsEnabled = state != RefreshVisualState.Syncing;

        if (state == RefreshVisualState.Succeeded)
        {
            _refresh.Content = _completedIcon;
            var completed = Display.S("RefreshCompleted");
            ToolTipService.SetToolTip(_refresh, completed);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_refresh, completed);
            return;
        }

        _refresh.Content = _refreshIcon;
        var refresh = state == RefreshVisualState.Syncing
            ? Display.S("SyncInProgress")
            : Display.S("RefreshTooltip");
        ToolTipService.SetToolTip(_refresh, refresh);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_refresh, refresh);

        if (state == RefreshVisualState.Syncing
            && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled
            && AppSession.Current.Settings.AnimationsEnabled)
            _spinStoryboard.Begin();
    }
}
