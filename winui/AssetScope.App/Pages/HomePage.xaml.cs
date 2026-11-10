using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;
using AssetScope.App.Controls;
using AssetScope.App.Services;
using AssetScope.Core;
using AssetScope.Core.Api;

namespace AssetScope.App.Pages;

public sealed partial class HomePage : Page
{
    private const double StackedLayoutWidth = 1000;
    private readonly AppSession _session = AppSession.Current;
    private DashboardDto? _dashboard;
    private InvestmentOverviewDto? _investmentOverview;
    private List<AccountSummaryDto> _allAccounts = [];
    private string _range = "1y";
    private CancellationTokenSource? _loadCts;

    public HomePage()
    {
        InitializeComponent();
        Header.Title = Display.S("HomeTitle");
        Header.Subtitle = Display.S("HomeSubtitle");
        DemoBanner.Closed += (_, _) => _demoBannerDismissed = true;
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        var stacked = PageLayoutMetrics.ShouldStack(width, StackedLayoutWidth);
        PrimaryColumn.MinWidth = stacked ? 0 : 420;
        SecondaryColumn.MinWidth = stacked ? 0 : 300;
        PrimaryColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(2, GridUnitType.Star);
        SecondaryColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Chart.Height = stacked ? 200 : 190;

        Grid.SetColumn(TrendCard, 0);
        Grid.SetColumnSpan(TrendCard, stacked ? 2 : 1);
        Grid.SetRow(SummaryPanel, stacked ? 1 : 0);
        Grid.SetColumn(SummaryPanel, stacked ? 0 : 1);
        Grid.SetColumnSpan(SummaryPanel, stacked ? 2 : 1);
        Grid.SetRowSpan(SummaryPanel, stacked ? 1 : 2);
        Grid.SetRow(AccountOverviewPanel, stacked ? 2 : 1);
        Grid.SetColumnSpan(AccountOverviewPanel, stacked ? 2 : 1);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _session.DataRefreshed += OnDataRefreshed;
        _session.PropertyChanged += OnSessionPropertyChanged;
        _ = LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _session.DataRefreshed -= OnDataRefreshed;
        _session.PropertyChanged -= OnSessionPropertyChanged;
        _loadCts?.Cancel();
    }

    private void OnDataRefreshed(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => _ = LoadAsync());

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSession.PrivacyMode))
            DispatcherQueue.TryEnqueue(Render);
    }

    private async Task LoadAsync()
    {
        if (_session.Api is null) return;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            NetWorthText, "HomeDashboardLoading");
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        try
        {
            var dashboardTask = _session.Api.GetDashboardAsync(_range, _loadCts.Token);
            var accountsTask = _session.Api.GetAccountsAsync(includeExcluded: false, ct: _loadCts.Token);
            var investmentsTask = _session.Api.GetInvestmentOverviewAsync(_range, _loadCts.Token);
            await Task.WhenAll(dashboardTask, accountsTask, investmentsTask);
            _dashboard = await dashboardTask;
            _allAccounts = (await accountsTask).Accounts;
            _investmentOverview = await investmentsTask;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (ApiException ex)
        {
            QualityBar.Severity = InfoBarSeverity.Error;
            QualityBar.Message = ex.Message;
            QualityBar.IsOpen = true;
        }
    }

    private bool _demoBannerDismissed;

    private void Render()
    {
        if (_dashboard is null) return;
        var d = _dashboard;
        var privacy = _session.PrivacyMode;
        var decimals = _session.ShowDecimals;

        var owner = _allAccounts
            .Select(account => account.Owner?.Trim())
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? _session.Settings.DisplayName?.Trim();
        Header.Title = owner is null
            ? Display.S("HomeTitle")
            : string.Format(Display.S("HomeGreeting"), owner);

        DemoBanner.IsOpen = _session.IsDemoMode && !_demoBannerDismissed;

        var hasAccounts = _allAccounts.Count > 0 || d.Summary.AsOfDate is not null;
        EmptyState.Visibility = hasAccounts ? Visibility.Collapsed : Visibility.Visible;
        ContentGrid.Visibility = hasAccounts ? Visibility.Visible : Visibility.Collapsed;
        if (!hasAccounts) return;

        NetWorthText.Text = Money.Format(d.Summary.NetWorth.Amount, decimals, privacy);
        NetWorthChange.SetChange(d.Summary.Change);
        AssetsText.Text = Money.Format(d.Summary.Assets.Amount, decimals, privacy);
        var creditCardLiabilities = _allAccounts
            .Where(account => account.Type == "credit")
            .Sum(account => account.Balance is null
                ? 0m
                : Math.Abs(Money.Parse(account.Balance.Amount)));
        LiabilitiesText.Text = Money.Format(creditCardLiabilities, decimals, privacy);
        BigChange.SetChange(AggregateChange(
            _allAccounts.Where(account => account.AccountClass == "asset"),
            Money.Parse(d.Summary.Assets.Amount)));
        LiabilitiesChange.SetChange(AggregateChange(
            _allAccounts.Where(account => account.Type == "credit"),
            creditCardLiabilities));
        AsOfText.Text = d.Summary.AsOfDate ?? "—";

        // Full-precision tooltips when decimals are hidden (SPEC §9.1).
        ToolTipService.SetToolTip(NetWorthText, !decimals && !privacy ? Money.Format(d.Summary.NetWorth.Amount, true) : null);
        ToolTipService.SetToolTip(AssetsText, !decimals && !privacy ? Money.Format(d.Summary.Assets.Amount, true) : null);
        ToolTipService.SetToolTip(LiabilitiesText, !decimals && !privacy ? Money.Format(creditCardLiabilities, true) : null);

        RenderChart(d.History);
        RenderAllocation(d.Allocation, d.Summary);
        RenderAccountCards(_allAccounts);
        RenderQualityBar(d.DataQuality);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            NetWorthText, "HomeDashboardReady");
    }

    private static ChangeDto? AggregateChange(IEnumerable<AccountSummaryDto> accounts, decimal current)
    {
        var changes = accounts
            .Select(account => account.Change)
            .Where(change => change is not null && Money.TryParse(change.Amount, out _))
            .Cast<ChangeDto>()
            .ToList();
        if (changes.Count == 0) return null;
        var delta = changes.Sum(change => Money.Parse(change.Amount));
        var previous = current - delta;
        return new ChangeDto(
            delta.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            previous == 0
                ? null
                : (delta / Math.Abs(previous)).ToString("0.########", System.Globalization.CultureInfo.InvariantCulture),
            "previousRecord",
            string.Empty);
    }

    private void RenderChart(List<HistoryPointDto> history)
    {
        var accent = ((SolidColorBrush)Application.Current.Resources["ChartLineBrush"]).Color;
        var hasEstimated = Chart.SetNetWorthHistory(history, Display.S("Series_netWorth"), accent);
        ApplyNetWorthInfoTip(hasEstimated);
        if (history.Count < 2)
        {
            Chart.Visibility = Visibility.Collapsed;
            ChartEmptyText.Visibility = Visibility.Visible;
            return;
        }
        Chart.Visibility = Visibility.Visible;
        ChartEmptyText.Visibility = Visibility.Collapsed;
    }

    private void ApplyNetWorthInfoTip(bool hasEstimated)
    {
        var text = Display.S("NetWorthTooltip");
        if (hasEstimated)
            text += "\n\n" + Display.S("ChartEstimateBar/Title") + ". " + Display.S("ChartEstimateBar/Message");
        NetWorthInfoFlyoutText.Text = text;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NetWorthInfoButton, text);
    }

    private void RenderAllocation(AllocationDto allocation, SummaryDto summary)
    {
        var privacy = _session.PrivacyMode;
        var securityLevel = _investmentOverview is not null
            && _investmentOverview.Positions.Count > 0;
        IEnumerable<CompositionAmount> assetAmounts = allocation.Slices.Select(slice =>
            new CompositionAmount(slice.Bucket, Money.Parse(slice.Amount.Amount)));
        if (securityLevel)
        {
            // Replace the coarse investment/retirement account totals with the
            // reconciled SnapTrade allocation. Bank cash and brokerage cash are
            // intentionally merged into the reference's single orange Cash
            // segment; every other investment segment remains security-level.
            assetAmounts = assetAmounts
                .Where(slice => slice.Bucket is not "investment" and not "retirement")
                .Select(slice => slice.Bucket == "bankCash"
                    ? slice with { Bucket = "Cash" }
                    : slice)
                .Concat(_investmentOverview!.Allocation.Select(slice =>
                    new CompositionAmount(
                        HomeInvestmentBucket(slice.Bucket),
                        Money.Parse(slice.Amount))))
                .GroupBy(slice => slice.Bucket, StringComparer.Ordinal)
                .Select(group => new CompositionAmount(
                    group.Key,
                    group.Sum(slice => slice.Amount)));
        }
        var slices = NetWorthComposition.Build(
            assetAmounts,
            Money.Parse(summary.Liabilities.Amount));

        Donut.SetData(
            slices
                .Select(slice => new DonutSliceData((double)slice.Amount, Display.BucketBrush(slice.Bucket), Display.BucketLabel(slice.Bucket)))
                .ToList(),
            Display.S("AllocationCenterLabel"),
            Money.Format(summary.NetWorth.Amount, false, privacy));

        AllocationLegend.Children.Clear();
        foreach (var slice in slices)
        {
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(2, 3, 2, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 8, Height = 8,
                Fill = Display.BucketBrush(slice.Bucket),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            var label = new TextBlock
            {
                Text = Display.BucketLabel(slice.Bucket),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var share = $"{slice.Share * 100:0.0}%";
            var shareText = new TextBlock
            {
                Text = share,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 1);
            Grid.SetColumn(shareText, 2);
            row.Children.Add(dot);
            row.Children.Add(label);
            row.Children.Add(shareText);

            var button = new Button
            {
                Content = row,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                button,
                $"{Display.BucketLabel(slice.Bucket)}, " +
                $"{(privacy ? Money.PrivacyMask : Money.Format(slice.Amount, false))}, {shareText.Text}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                button, $"HomeAllocation_{slice.Bucket}");
            var bucket = slice.Bucket;
            button.Click += (_, _) =>
            {
                if (bucket == "liability") App.Window?.NavigateToAccounts();
                else if (bucket is "Stocks" or "ETFs" or "Crypto" or "MutualFunds" or "Unclassified" or "Other")
                    App.Window?.NavigateToInvestments();
                else App.Window?.NavigateToAccounts();
            };
            AllocationLegend.Children.Add(button);
        }
    }

    private static string HomeInvestmentBucket(string bucket) =>
        Display.NormalizePortfolioBucket(bucket);

    private void RenderAccountCards(List<AccountSummaryDto> accounts)
    {
        var cards = new List<Button>();
        var privacy = _session.PrivacyMode;
        var decimals = _session.ShowDecimals;

        var institutions = InstitutionOverview.Build(accounts.Select(account =>
            new InstitutionOverviewInput(
                account.Id,
                account.InstitutionName ?? Display.CategoryLabel(account.Category),
                account.DisplayName,
                account.Category,
                account.Balance is null ? null : Money.Parse(account.Balance.Amount),
                account.Change is null || !Money.TryParse(account.Change.Amount, out var change)
                    ? null
                    : change)));

        foreach (var institution in institutions)
        {
            var stack = new StackPanel { Spacing = 7, Padding = new Thickness(14, 10, 14, 11) };

            var top = new Grid { ColumnSpacing = 10 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.Children.Add(new InstitutionLogo(institution.Name, institution.Accounts[0].Category, 40));
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock
            {
                Text = institution.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 15,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(names, 1);
            top.Children.Add(names);

            var balance = new TextBlock
            {
                Text = Money.Format(institution.Total.ToString(System.Globalization.CultureInfo.InvariantCulture), decimals, privacy),
                FontSize = 26,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, 0),
            };

            var accountPreview = new StackPanel { Spacing = 4 };
            var overviewRows = institution.Rows.Take(3);
            foreach (var overviewRow in overviewRows)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(Ui.Caption(InstitutionBucketLabel(overviewRow.Bucket)));
                row.Children.Add(new TextBlock
                {
                    Text = Money.Format(overviewRow.Total, decimals, privacy),
                    Style = Ui.Style("CaptionStyle"),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Ui.Brush(overviewRow.Total < 0 ? "ReferenceRedBrush" : "TextFillColorPrimaryBrush"),
                }.At(1));
                accountPreview.Children.Add(row);
            }

            stack.Children.Add(top);
            stack.Children.Add(balance);
            var changeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var changeBadge = new ChangeBadge { AmountFontSize = 12 };
            changeBadge.SetChange(institution.Change is null
                ? null
                : new ChangeDto(
                    institution.Change.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    institution.ChangeRatio?.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture),
                    "yesterday",
                    ""), showComparisonLabel: false);
            changeRow.Children.Add(changeBadge);
            var todayLabel = Ui.Caption(Display.S("TodayChangeLabel"));
            todayLabel.VerticalAlignment = VerticalAlignment.Center;
            changeRow.Children.Add(todayLabel);
            stack.Children.Add(changeRow);
            stack.Children.Add(accountPreview);
            var divider = Ui.Divider();
            divider.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(divider);
            var details = new Grid { ColumnSpacing = 8 };
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            details.Children.Add(new TextBlock
            {
                Text = Display.S("HomeViewInstitutionDetails"),
                Style = Ui.Style("CaptionStyle"),
                Foreground = Ui.Brush("ReferenceBlueTextBrush"),
            });
            var detailsChevron = new FontIcon
            {
                Glyph = "",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            Grid.SetColumn(detailsChevron, 1);
            details.Children.Add(detailsChevron);
            stack.Children.Add(details);

            var cardContent = new Grid();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardContent.Children.Add(new Border
            {
                Height = 3,
                Background = InstitutionAccent(institution.Name, institution.Accounts[0].Category),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(11, 11, 0, 0),
            });
            Grid.SetRow(stack, 1);
            cardContent.Children.Add(stack);

            var card = new Button
            {
                Content = cardContent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                MinHeight = 236,
                Background = (Brush)Application.Current.Resources["ReferenceCardBrush"],
                BorderBrush = (Brush)Application.Current.Resources["ReferenceCardStrokeBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                UseSystemFocusVisuals = true,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                card, $"{institution.Name}, {balance.Text}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                card, $"HomeAccountCard_{cards.Count}");
            var id = institution.Accounts[0].AccountId;
            card.Click += (_, _) => App.Window?.NavigateToAccounts(id);
            cards.Add(card);
        }
        AccountCards.ItemsSource = cards;
    }

    private static Brush InstitutionAccent(string institutionName, string category) =>
        InstitutionBrands.Match(institutionName) switch
        {
            InstitutionBrand.Robinhood => (Brush)Application.Current.Resources["ReferenceGreenBrush"],
            InstitutionBrand.Chase => (Brush)Application.Current.Resources["ReferenceBlueBrush"],
            InstitutionBrand.BankOfAmerica => (Brush)Application.Current.Resources["ReferenceRedBrush"],
            InstitutionBrand.XMoney => (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            _ => Display.CategoryBrush(category),
        };

    private static string InstitutionBucketLabel(string bucket) => Display.S(bucket switch
    {
        "investment" => "HomeInstitutionInvestment",
        "cash" => "HomeInstitutionCash",
        "liability" => "HomeInstitutionLiability",
        _ => "HomeInstitutionOther",
    });

    private void RenderQualityBar(DataQualityDto q)
    {
        var messages = new List<string>();
        // Degraded read-only database beats everything else (SPEC §22.5).
        if (_session.DegradedReadOnly)
        {
            QualityBar.Severity = InfoBarSeverity.Error;
            QualityBar.Message = Display.S("Quality_readOnly");
            QualityBar.IsOpen = true;
            return;
        }
        // Do not flash cached attention/staleness while the launch sync is
        // actively repairing it. Sync completion raises DataRefreshed and the
        // committed post-sync quality state is rendered atomically.
        if (_session.IsSyncing)
        {
            QualityBar.IsOpen = false;
            return;
        }
        if (_session.IsOffline) messages.Add(Display.S("OfflineStatus"));
        // Provider-level reconnect/staleness is already shown per connection
        // in Settings and per account in Accounts. Repeating it as a full-width
        // dashboard banner both over-counts one Plaid Item as several accounts
        // and displaces the primary financial overview.
        if (q.MissingBalanceCount > 0) messages.Add(string.Format(Display.S("Quality_missingBalance"), q.MissingBalanceCount));
        if (q.NonUsdAccountCount > 0) messages.Add(string.Format(Display.S("Quality_nonUsd"), q.NonUsdAccountCount));
        if (q.ExcludedAccountCount > 0) messages.Add(string.Format(Display.S("Quality_excluded"), q.ExcludedAccountCount));

        if (messages.Count == 0)
        {
            QualityBar.IsOpen = false;
            return;
        }
        QualityBar.Severity = q.MissingBalanceCount > 0
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Informational;
        QualityBar.Message = string.Join("  ·  ", messages);
        QualityBar.IsOpen = true;
    }

    private void RangeBar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = (RangeBar.SelectedItem as ComboBoxItem)?.Tag as string;
        if (tag is null || tag == _range) return;
        _range = tag;
        _ = LoadAsync();
    }

    private void ConnectBankEmptyButton_Click(object sender, RoutedEventArgs e) =>
        App.Window?.ShowSetupGate(allowCancel: true);

    private async void DemoBannerAction_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_session.IsDemoMode)
                await _session.SwitchModeAsync("real");
            App.Window?.ShowSetupGate(allowCancel: true);
        }
        catch (Exception ex)
        {
            QualityBar.Severity = InfoBarSeverity.Error;
            QualityBar.Message = ex.Message;
            QualityBar.IsOpen = true;
        }
    }
}
