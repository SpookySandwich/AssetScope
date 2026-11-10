using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;
using AssetScope.App.Controls;
using AssetScope.App.Services;
using AssetScope.Core;
using AssetScope.Core.Api;

namespace AssetScope.App.Pages;

public sealed partial class TrendsPage : Page
{
    private const double StackedLayoutWidth = 1000;
    // The reference composition keeps all four KPI cards in one row beside
    // the insight column. Four compact cards remain readable down to 920 DIPs;
    // wrapping at 1360 produced an unnecessary 2x2 dashboard on desktop.
    private const double WrappedSummaryCardsWidth = 920;
    private readonly AppSession _session = AppSession.Current;
    private List<HistoryPointDto> _history = [];
    private SummaryDto? _summary;
    private List<AccountSummaryDto> _accounts = [];
    private TrendsDto? _trends;
    private InsightsResponseDto? _insights;
    private string _range = "1y";

    public TrendsPage()
    {
        InitializeComponent();
        RangeBar.SelectedItem = RangeOneYearItem;
        Header.Title = Display.S("TrendsTitle");
        Header.Subtitle = Display.S("TrendsSubtitle");
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        var stacked = PageLayoutMetrics.ShouldStack(width, StackedLayoutWidth);
        var primaryWidth = PageLayoutMetrics.PrimaryColumnWidth(
            width,
            stacked ? 0 : 360,
            stacked);
        var wrapSummaryCards = primaryWidth < WrappedSummaryCardsWidth;
        TrendsPrimaryColumn.MinWidth = stacked ? 0 : 480;
        TrendsSummaryColumn.Width = stacked ? new GridLength(0) : new GridLength(360);
        Chart.Height = stacked ? 200 : 190;
        CategoryChart.Height = stacked ? 160 : 170;

        Grid.SetColumn(PrimaryContentPanel, 0);
        Grid.SetColumnSpan(PrimaryContentPanel, stacked ? 2 : 1);
        Grid.SetRow(TrendsRightRail, stacked ? 1 : 0);
        Grid.SetColumn(TrendsRightRail, stacked ? 0 : 1);
        Grid.SetColumnSpan(TrendsRightRail, stacked ? 2 : 1);

        for (var i = 0; i < SummaryCardsGrid.ColumnDefinitions.Count; i++)
            SummaryCardsGrid.ColumnDefinitions[i].Width = wrapSummaryCards && i >= 2
                ? new GridLength(0)
                : new GridLength(1, GridUnitType.Star);

        Grid.SetRow(NetWorthSummaryCard, 0);
        Grid.SetColumn(NetWorthSummaryCard, 0);
        Grid.SetRow(BankSummaryCard, 0);
        Grid.SetColumn(BankSummaryCard, 1);
        Grid.SetRow(InvestmentSummaryCard, wrapSummaryCards ? 1 : 0);
        Grid.SetColumn(InvestmentSummaryCard, wrapSummaryCards ? 0 : 2);
        Grid.SetRow(LiabilitiesSummaryCard, wrapSummaryCards ? 1 : 0);
        Grid.SetColumn(LiabilitiesSummaryCard, wrapSummaryCards ? 1 : 3);
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
            NetWorthText, "TrendsContentLoading");
        try
        {
            var historyTask = _session.Api.GetNetWorthHistoryAsync(_range);
            var dashboardTask = _session.Api.GetDashboardAsync(_range);
            var accountsTask = _session.Api.GetAccountsAsync(includeExcluded: false);
            var trendsTask = _session.Api.GetTrendsAsync(_range);
            var insightsTask = _session.Api.GetInsightsAsync(_range);
            await Task.WhenAll(historyTask, dashboardTask, accountsTask, trendsTask, insightsTask);
            _history = (await historyTask).Points;
            _summary = (await dashboardTask).Summary;
            _accounts = (await accountsTask).Accounts;
            _trends = await trendsTask;
            _insights = await insightsTask;
            Render();
        }
        catch (ApiException) { }
    }

    private void Render()
    {
        if (_summary is null) return;
        var privacy = _session.PrivacyMode;
        var decimals = _session.ShowDecimals;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            NetWorthText, "TrendsContentReady");

        NetWorthText.Text = Money.Format(_summary.NetWorth.Amount, decimals, privacy);
        var rangeChange = RangeChange();
        NetWorthChange.SetChange(
            rangeChange,
            showComparisonLabel: true,
            comparisonText: Display.S($"RangeLabel_{_range}"));

        RenderChart();
        RenderCategoryChart();
        RenderSummaryCards();
        RenderSummaryLines();
    }

    private ChangeDto? RangeChange()
    {
        if (_history.Count < 2) return null;
        var first = Money.Parse(_history[0].NetWorth.Amount);
        var last = Money.Parse(_history[^1].NetWorth.Amount);
        var delta = last - first;
        var ratio = first == 0 ? (decimal?)null : delta / Math.Abs(first);
        return new ChangeDto(
            delta.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ratio?.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture),
            "rangeStart",
            string.Empty);
    }

    /// <summary>Signed net worth by institution across the selected range.</summary>
    private void RenderCategoryChart()
    {
        CategoryLegend.Children.Clear();
        // Use a stable brand order and keep connected institutions whose
        // balance is flat for the selected range.
        var institutions = _trends?.InstitutionSeries
            .OrderBy(institution => InstitutionOrder(institution.Institution))
            .ThenByDescending(institution => Math.Abs(
                institution.Points.Count == 0 ? 0m : Money.Parse(institution.Points[^1].Amount)))
            .Take(5).ToList() ?? [];
        if (institutions.Count == 0 || institutions[0].Points.Count < 2)
        {
            CategoryChart.Visibility = Visibility.Collapsed;
            return;
        }
        CategoryChart.Visibility = Visibility.Visible;

        var series = institutions.Select((institution, index) => new ContributionSeriesData(
            InstitutionLabel(institution.Institution),
            institution.Points.Select(point => (DateOnly.Parse(point.Date), Money.Parse(point.Amount))).ToList(),
            InstitutionColor(institution.Institution, index))).ToList();
        CategoryChart.SetSeries(series);

        for (var index = 0; index < institutions.Count; index++)
        {
            var institution = institutions[index];
            var item = new Grid { ColumnSpacing = 8, MinWidth = 190 };
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            item.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(InstitutionColor(institution.Institution, index)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var institutionName = new TextBlock
            {
                Text = InstitutionLabel(institution.Institution),
                Style = Ui.Style("CaptionStyle"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(institutionName, 1);
            item.Children.Add(institutionName);
            var final = institution.Points.Count == 0 ? 0m : Money.Parse(institution.Points[^1].Amount);
            var finalText = new TextBlock
            {
                Text = Money.FormatSigned(final, false, _session.PrivacyMode),
                Style = Ui.Style("CaptionStyle"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources[
                    final >= 0 ? "ReferenceGreenBrush" : "ReferenceRedBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
            };
            Grid.SetColumn(finalText, 2);
            item.Children.Add(finalText);
            CategoryLegend.Children.Add(item);
        }
    }

    private static string InstitutionLabel(string institution) =>
        institution == "manual" ? Display.S("ManualAccountsGroup") : institution;

    private static int InstitutionOrder(string institution) =>
        InstitutionBrands.Match(institution) switch
        {
            InstitutionBrand.Robinhood => 0,
            InstitutionBrand.Chase => 1,
            InstitutionBrand.BankOfAmerica => 2,
            _ => 10,
        };

    private static Color InstitutionColor(string institution, int index) =>
        InstitutionBrands.Match(institution) switch
        {
            InstitutionBrand.Robinhood => Color.FromArgb(255, 0x12, 0xA0, 0x44),
            InstitutionBrand.Chase => Color.FromArgb(255, 0x12, 0x67, 0xF4),
            InstitutionBrand.BankOfAmerica => Color.FromArgb(255, 0xFF, 0x3B, 0x30),
            _ => new[]
            {
                Color.FromArgb(255, 0x7C, 0x4D, 0xDB),
                Color.FromArgb(255, 0xEA, 0xA3, 0x00),
                Color.FromArgb(255, 0x03, 0x83, 0x87),
            }[index % 3],
        };

    private async void ExportCsvButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Api is null) return;
        try
        {
            var result = await _session.Api.ExportNetWorthCsvAsync(_range);
            var dialog = new ContentDialog
            {
                Title = Display.S("ExportDone"),
                Content = string.Format(Display.S("ExportDoneBody"), result.Rows, result.Path),
                PrimaryButtonText = Display.S("OpenFolder/Content"),
                CloseButtonText = Display.S("OK"),
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await Windows.System.Launcher.LaunchFolderPathAsync(System.IO.Path.GetDirectoryName(result.Path)!);
        }
        catch (ApiException ex)
        {
            await new ContentDialog { Title = Display.S("Error"), Content = ex.Message, CloseButtonText = Display.S("OK"), XamlRoot = XamlRoot }.ShowAsync();
        }
    }

    private void RenderChart()
    {
        var accent = ((SolidColorBrush)Application.Current.Resources["ChartLineBrush"]).Color;
        var hasEstimated = Chart.SetNetWorthHistory(_history, Display.S("Series_netWorth"), accent);
        ApplyNetWorthInfoTip(hasEstimated);
        if (_history.Count < 2)
        {
            Chart.Visibility = Visibility.Collapsed;
            return;
        }
        Chart.Visibility = Visibility.Visible;
    }

    private void ApplyNetWorthInfoTip(bool hasEstimated)
    {
        var text = Display.S("NetWorthTooltip");
        if (hasEstimated)
            text += "\n\n" + Display.S("ChartEstimateBar/Title") + ". " + Display.S("ChartEstimateBar/Message");
        NetWorthInfoFlyoutText.Text = text;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NetWorthInfoButton, text);
    }

    private sealed record CategoryTotal(decimal Balance, decimal Delta, decimal? Ratio, bool HasChange);

    private sealed record TrendInsight(
        string Title,
        string Body,
        string Glyph,
        bool IsTextGlyph,
        string BackgroundResource,
        string ForegroundResource);

    /// <summary>
    /// Per-category totals from the server's as-of series (SPEC §9.7): the delta
    /// compares the range's first and last complete portfolio states, never a
    /// sum of mismatched per-account row deltas.
    /// </summary>
    private CategoryTotal SeriesTotal(List<SparkPointDto>? points)
    {
        if (points is null || points.Count == 0) return new CategoryTotal(0, 0, null, false);
        var last = Money.Parse(points[^1].Amount);
        if (points.Count < 2) return new CategoryTotal(last, 0, null, false);
        var first = Money.Parse(points[0].Amount);
        var delta = last - first;
        decimal? ratio = first == 0 ? null : delta / Math.Abs(first);
        return new CategoryTotal(last, delta, ratio, true);
    }

    private List<SparkPointDto>? BucketSeries(string name) =>
        _trends?.AssetBuckets.FirstOrDefault(bucket => bucket.Bucket == name)?.Points;

    private static ChangeDto? CategoryChange(CategoryTotal total) =>
        total.HasChange
            ? new ChangeDto(
                total.Delta.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                total.Ratio?.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture),
                "previousRecord",
                "")
            : null;

    private void RenderSummaryCards()
    {
        var privacy = _session.PrivacyMode;
        var decimals = _session.ShowDecimals;

        CategoryTotal Fill(TextBlock text, ChangeBadge badge, Sparkline spark, List<SparkPointDto>? points)
        {
            var total = SeriesTotal(points);
            text.Text = Money.Format(total.Balance, decimals, privacy);
            badge.SetChange(CategoryChange(total), showComparisonLabel: false);
            spark.SetValues(points?.Select(p => Money.Parse(p.Amount)).ToList() ?? []);
            return total;
        }

        KpiNetWorthText.Text = NetWorthText.Text;
        var netWorthValues = _history.Select(point => Money.Parse(point.NetWorth.Amount)).ToList();
        var netWorthDelta = netWorthValues.Count < 2 ? 0 : netWorthValues[^1] - netWorthValues[0];
        var netWorthRatio = netWorthValues.Count < 2 || netWorthValues[0] == 0
            ? (decimal?)null
            : netWorthDelta / Math.Abs(netWorthValues[0]);
        var netWorthChange = netWorthValues.Count < 2
            ? null
            : new ChangeDto(
                netWorthDelta.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                netWorthRatio?.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture),
                "previousRecord",
                "");
        KpiNetWorthChange.SetChange(netWorthChange, showComparisonLabel: false);
        NetWorthSpark.SetValues(netWorthValues);
        Fill(BankText, BankChange, BankSpark, BucketSeries("bankCash"));
        Fill(InvestmentText, InvestmentChange, InvestmentSpark, BucketSeries("investment"));
        Fill(LiabilitiesText, LiabilitiesChange, LiabilitiesSpark, _trends?.Liabilities);
        var rangeLabel = Display.S($"RangeLabel_{_range}");
        NetWorthPeriodText.Text = rangeLabel;
        BankPeriodText.Text = rangeLabel;
        InvestmentPeriodText.Text = rangeLabel;
        LiabilitiesPeriodText.Text = rangeLabel;
    }

    // Rule-based factual summaries only — no advice, no "returns" (SPEC §20.4).
    private void RenderSummaryLines()
    {
        SummaryLines.Children.Clear();
        var privacy = _session.PrivacyMode;
        var insights = new List<TrendInsight>();

        if (_insights is not null)
            insights.AddRange(_insights.Items
                .Where(insight => insight.Kind == "spendingIncrease")
                .Select(FormatInsight));

        if (insights.Count == 0)
        {
        if (_history.Count >= 2)
        {
            var first = Money.Parse(_history[0].NetWorth.Amount);
            var last = Money.Parse(_history[^1].NetWorth.Amount);
            var delta = last - first;
            var rangeLabel = Display.S($"RangeLabel_{_range}");
            insights.Add(new TrendInsight(
                Display.S(delta >= 0 ? "TrendInsightNetWorthUp" : "TrendInsightNetWorthDown"),
                string.Format(
                    Display.S(delta >= 0 ? "Summary_netWorthUp" : "Summary_netWorthDown"),
                    Money.Format(Math.Abs(delta), false, privacy), rangeLabel),
                delta >= 0 ? "↗" : "↘",
                true,
                delta >= 0 ? "SystemFillColorSuccessBackgroundBrush" : "SystemFillColorCriticalBackgroundBrush",
                delta >= 0 ? "ReferenceGreenBrush" : "ReferenceRedBrush"));

            var investment = SeriesTotal(BucketSeries("investment"));
            if (investment.HasChange && investment.Delta != 0)
            {
                insights.Add(new TrendInsight(
                    Display.S(investment.Delta >= 0 ? "TrendInsightInvestmentUp" : "TrendInsightInvestmentDown"),
                    string.Format(
                        Display.S(investment.Delta >= 0 ? "Summary_investmentUp" : "Summary_investmentDown"),
                        Money.Format(Math.Abs(investment.Delta), false, privacy), rangeLabel),
                    "\uEB05",
                    false,
                    "ReferenceBlueTintBrush",
                    "ReferencePurpleBrush"));
            }

            var firstLiab = Money.Parse(_history[0].Liabilities.Amount);
            var lastLiab = Money.Parse(_history[^1].Liabilities.Amount);
            var liabDelta = lastLiab - firstLiab;
            if (liabDelta != 0)
            {
                insights.Add(new TrendInsight(
                    Display.S(liabDelta < 0 ? "TrendInsightLiabilitiesDown" : "TrendInsightLiabilitiesUp"),
                    string.Format(
                        Display.S(liabDelta < 0 ? "Summary_liabilitiesDown" : "Summary_liabilitiesUp"),
                        Money.Format(Math.Abs(liabDelta), false, privacy), rangeLabel),
                    "\uE8C7",
                    false,
                    "SystemFillColorCriticalBackgroundBrush",
                    "ReferenceRedBrush"));
            }
        }

        var biggestMove = _accounts
            .Where(a => a.Change is not null && Money.TryParse(a.Change.Amount, out _))
            .OrderByDescending(a => Math.Abs(Money.Parse(a.Change!.Amount)))
            .FirstOrDefault();
        if (biggestMove is not null)
        {
            var delta = Money.Parse(biggestMove.Change!.Amount);
            insights.Add(new TrendInsight(
                Display.S("TrendInsightLargestMove"),
                string.Format(
                    Display.S(delta >= 0 ? "Summary_biggestUp" : "Summary_biggestDown"),
                    biggestMove.DisplayName, Money.Format(Math.Abs(delta), false, privacy)),
                "\uE9D2",
                false,
                "ReferenceBlueTintBrush",
                "ReferenceBlueTextBrush"));
        }

        var staleCount = _accounts.Count(a => a.Freshness is "stale" or "very_stale");
        if (staleCount > 0)
            insights.Add(new TrendInsight(
                Display.S("TrendInsightDataFreshness"),
                string.Format(Display.S("Summary_stale"), staleCount),
                "\uE7BA",
                false,
                "SystemFillColorCautionBackgroundBrush",
                "SystemFillColorCautionBrush"));
        }

        foreach (var insight in insights.Take(3))
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = insight.IsTextGlyph
                ? (UIElement)new TextBlock
                {
                    Text = insight.Glyph,
                    FontFamily = new FontFamily("Segoe UI Variable Display"),
                    FontSize = 25,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = (Brush)Application.Current.Resources[insight.ForegroundResource],
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
                : new FontIcon
                {
                    Glyph = insight.Glyph,
                    FontSize = 19,
                    Foreground = (Brush)Application.Current.Resources[insight.ForegroundResource],
                };
            row.Children.Add(new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(10),
                VerticalAlignment = VerticalAlignment.Top,
                Background = (Brush)Application.Current.Resources[insight.BackgroundResource],
                Child = icon,
            });
            var copy = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            copy.Children.Add(new TextBlock
            {
                Text = insight.Title,
                Style = Ui.Style("SectionLabelStyle"),
                TextWrapping = TextWrapping.Wrap,
            });
            copy.Children.Add(Ui.Caption(insight.Body, wrap: true));
            Grid.SetColumn(copy, 1);
            row.Children.Add(copy);
            SummaryLines.Children.Add(row);
        }
    }

    private TrendInsight FormatInsight(InsightDto insight)
    {
        string Amount(string? value) => value is null ? "—" : _session.FmtSigned(value);
        return new TrendInsight(
            string.Format(Display.S("InsightSpendingIncreaseTitle"),
                Display.SpendingCategoryLabel(insight.Category ?? "other")),
            string.Format(Display.S("InsightSpendingIncreaseBody"),
                Amount(insight.PrimaryAmount), insight.TransactionCount),
            "\uE81E", false, "SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush");
    }

    private void RangeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = sender.SelectedItem?.Tag as string;
        if (string.IsNullOrEmpty(tag)) return;
        if (tag == _range) return;
        _range = tag;
        _ = LoadAsync();
    }

    private void SeriesToggled(object sender, RoutedEventArgs e) => RenderChart();
}
