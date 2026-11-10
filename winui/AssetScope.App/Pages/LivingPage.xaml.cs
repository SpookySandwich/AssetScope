using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Globalization;
using AssetScope.App.Controls;
using AssetScope.App.Services;
using AssetScope.Core;
using AssetScope.Core.Api;

namespace AssetScope.App.Pages;

/// <summary>
/// Cost of living, read-only. The yardstick is a typical month (the sidecar's
/// average of recent full months); this month is measured against it, and
/// cash is expressed in months of that spending. Anything unconfirmed is left
/// out of the numbers, never asked for here.
/// </summary>
public sealed partial class LivingPage : Page
{
    private readonly AppSession _session = AppSession.Current;
    private LivingOverviewDto? _overview;
    private CancellationTokenSource? _loadCts;

    // Two columns need their minimum widths to hold: below this page width the
    // star columns would clamp and overflow the right window edge instead.
    private const double StackedLayoutWidth = 1000;

    public LivingPage()
    {
        InitializeComponent();
        Header.Title = Display.S("LivingTitle");
        Header.Subtitle = Display.S("LivingSubtitle");
        TypicalIncomeLabel.Text = Display.S("LivingIncomeLabel");
        TypicalPerMonthText.Text = Display.S("LivingPerMonth");
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
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
        AutomationProperties.SetAutomationId(TypicalSpendText, "LivingContentLoading");
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        try
        {
            _overview = await _session.Api.GetLivingAsync(ct: _loadCts.Token);
            ErrorBar.IsOpen = false;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (ApiException ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }

    private void ApplyResponsiveLayout(double width)
    {
        var stacked = PageLayoutMetrics.ShouldStack(width, StackedLayoutWidth);
        if (stacked)
        {
            if (SecondaryPanel.Children.Count > 0)
            {
                var moved = SecondaryPanel.Children.ToArray();
                SecondaryPanel.Children.Clear();
                foreach (var card in moved) PrimaryPanel.Children.Add(card);
            }
            PrimaryColumn.Width = new GridLength(1, GridUnitType.Star);
            SecondaryColumn.Width = new GridLength(0);
            SecondaryColumn.MinWidth = 0;
            SecondaryScroller.Visibility = Visibility.Collapsed;
        }
        else
        {
            PrimaryColumn.Width = new GridLength(2, GridUnitType.Star);
            SecondaryColumn.Width = new GridLength(1, GridUnitType.Star);
            SecondaryColumn.MinWidth = 300;
            SecondaryScroller.Visibility = Visibility.Visible;
            if (RunwayCard.Parent is StackPanel panel && panel == PrimaryPanel)
            {
                foreach (var card in new FrameworkElement[] { RunwayCard, RecurringCard, RecentCard })
                {
                    PrimaryPanel.Children.Remove(card);
                    SecondaryPanel.Children.Add(card);
                }
            }
        }
    }

    // ── Rendering ──

    private void Render()
    {
        if (_overview is null) return;
        var o = _overview;
        var empty = !o.HasHistory;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ContentGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetAutomationId(TypicalSpendText, "LivingContentReady");
        if (empty) return;

        RenderTypical(o);
        RenderThisMonth(o);
        RenderCategories(o);
        RenderRunway(o);
        RenderRecurring(o);
        RenderRecent(o);
    }

    private bool HasTypical(LivingOverviewDto o) =>
        o.Typical is { Months.Count: > 0 } t && Money.Parse(t.Spend) > 0;

    private void RenderTypical(LivingOverviewDto o)
    {
        var has = HasTypical(o);
        TypicalBody.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        TypicalIncomePanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        TypicalEmptyText.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        TypicalPerMonthText.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has)
        {
            TypicalSpendText.Text = "—";
            TypicalBasisText.Text = string.Empty;
            return;
        }
        var t = o.Typical!;
        var privacy = _session.PrivacyMode;
        var income = Money.Parse(t.Income);
        var fixedAmount = Money.Parse(t.Fixed);
        var flexible = Money.Parse(t.Flexible);
        var kept = Money.Parse(t.Leftover);

        TypicalSpendText.Text = _session.Fmt(t.Spend);
        TypicalBasisText.Text = string.Format(Display.S("LivingTypicalBasis"), MonthsLabel(t.Months));
        TypicalIncomeText.Text = _session.Fmt(t.Income);
        TypicalKeepText.Text = kept > 0
            ? string.Format(Display.S("LivingKeepPositive"), Money.Format(kept, _session.ShowDecimals, privacy))
            : kept < 0
                ? string.Format(Display.S("LivingKeepNegative"), Money.Format(-kept, _session.ShowDecimals, privacy))
                : Display.S("LivingKeepEven");
        TypicalKeepText.Foreground = (Brush)Application.Current.Resources[
            kept < 0 ? "NegativeChangeBrush" : kept > 0 ? "PositiveChangeBrush" : "TextFillColorSecondaryBrush"];

        var spend = fixedAmount + flexible;
        var fixedShare = spend > 0 ? (double)(fixedAmount / spend) : 1;
        FixedShareColumn.Width = new GridLength(Math.Max(fixedShare, 0.0001), GridUnitType.Star);
        FlexibleShareColumn.Width = new GridLength(Math.Max(1 - fixedShare, 0.0001), GridUnitType.Star);
        FixedShareFill.CornerRadius = flexible <= 0 ? new CornerRadius(6) : new CornerRadius(6, 0, 0, 6);
        FlexibleShareFill.CornerRadius = fixedAmount <= 0 ? new CornerRadius(6) : new CornerRadius(0, 6, 6, 0);
        FixedLegendText.Text = $"{Display.S("LivingFixedLabel")} {_session.Fmt(t.Fixed)}";
        FlexibleLegendText.Text = $"{Display.S("LivingFlexibleLabel")} {_session.Fmt(t.Flexible)}";

        TypicalFixedText.Text = _session.Fmt(t.Fixed);
        TypicalFlexibleText.Text = _session.Fmt(t.Flexible);
        TypicalLeftoverText.Text = _session.FmtSigned(t.Leftover);
        TypicalLeftoverText.Foreground = (Brush)Application.Current.Resources[
            kept < 0 ? "NegativeChangeBrush" : "TextFillColorPrimaryBrush"];
        TypicalFixedShareText.Text = ShareOfIncome(fixedAmount, income);
        TypicalFlexibleShareText.Text = ShareOfIncome(flexible, income);
        TypicalLeftoverShareText.Text = kept > 0 ? ShareOfIncome(kept, income) : string.Empty;

        if (t.Goal is { } goal)
        {
            GoalText.Visibility = Visibility.Visible;
            GoalText.Text = goal.Months is int months
                ? string.Format(Display.S("LivingGoalTargetNote"), months, goal.Label)
                : string.Format(Display.S("LivingGoalContributionNote"), _session.Fmt(goal.Yearly), goal.Label);
        }
        else
        {
            GoalText.Visibility = Visibility.Collapsed;
        }
    }

    private string ShareOfIncome(decimal amount, decimal income) =>
        income <= 0 || _session.PrivacyMode
            ? string.Empty
            : string.Format(Display.S("LivingShareOfIncome"), (amount / income).ToString("P0", CultureInfo.CurrentCulture));

    private void RenderThisMonth(LivingOverviewDto o)
    {
        var culture = CultureInfo.CurrentCulture;
        var month = ParseMonth(o.Period.Month);
        var monthName = month?.ToString("MMMM", culture) ?? o.Period.Month;
        ThisMonthTitleText.Text = string.Format(Display.S("LivingThisMonthTitle"), monthName);
        DaysText.Text = string.Format(Display.S("LivingMonthProgress"), o.Period.DaysElapsed, o.Period.DaysInMonth);
        MonthProgress.Value = o.Period.DaysInMonth > 0
            ? Math.Clamp((double)o.Period.DaysElapsed / o.Period.DaysInMonth, 0, 1)
            : 0;
        AutomationProperties.SetName(MonthProgress, DaysText.Text);

        var m = o.ThisMonth;
        ThisMonthSpentText.Text = m is null ? _session.Fmt(o.Split.FixedPosted) : _session.Fmt(m.Spent);
        SpentBreakdownText.Text = string.Format(Display.S("LivingSpentBreakdown"),
            _session.Fmt(o.Split.FixedPosted), _session.Fmt(o.Split.FlexiblePosted));
        ExpectedRemainingText.Text = _session.Fmt(o.Split.FixedRemaining);

        var hasTypical = HasTypical(o);
        if (m?.FlexiblePace is { } paceText && decimal.TryParse(paceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var pace))
        {
            var diff = pace - 1;
            PaceText.Text = Math.Abs(diff) < 0.1m
                ? Display.S("LivingPaceOnTrack")
                : diff > 0
                    ? string.Format(Display.S("LivingPaceAbove"), diff.ToString("P0", culture))
                    : string.Format(Display.S("LivingPaceBelow"), (-diff).ToString("P0", culture));
            PaceText.Foreground = (Brush)Application.Current.Resources[
                diff >= 0.1m ? "NegativeChangeBrush" : diff <= -0.1m ? "PositiveChangeBrush" : "TextFillColorPrimaryBrush"];
            PaceHintText.Text = string.Format(Display.S("LivingPaceHint"), _session.Fmt(m!.FlexibleExpectedByNow));
        }
        else
        {
            PaceText.Text = Display.S("LivingPaceEarly");
            PaceText.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            PaceHintText.Text = hasTypical && m is not null && Money.Parse(m.FlexibleExpectedByNow) > 0
                ? string.Format(Display.S("LivingPaceHint"), _session.Fmt(m.FlexibleExpectedByNow))
                : Display.S("LivingPaceEarlyHint");
        }

        if (hasTypical && m is not null)
        {
            ProjectionPanel.Visibility = Visibility.Visible;
            ProjectionText.Text = string.Format(Display.S("LivingProjection"),
                monthName, _session.FmtSigned(m.ProjectedLeftoverIfTypical));
        }
        else
        {
            ProjectionPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderCategories(LivingOverviewDto o)
    {
        CategoryList.Children.Clear();
        var t = o.Typical;
        var categories = t?.Categories.Take(8).ToList() ?? [];
        CategoriesCard.Visibility = categories.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (categories.Count == 0) return;
        CategoriesHintText.Text = string.Format(Display.S("LivingCategoriesHint"), MonthsLabel(t!.Months));
        var max = categories.Max(c => Money.Parse(c.MonthlyAverage));
        var range = o.Period.UsingPriorMonth ? "3m" : "3m";
        var tip = Display.S("TransactionsCategoryFilterTip");
        foreach (var category in categories)
        {
            var amount = Money.Parse(category.MonthlyAverage);
            var fill = max <= 0 ? 0 : (double)(amount / max);
            var row = new StackPanel { Spacing = 5 };
            var head = new Grid { ColumnSpacing = 10 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock
            {
                Text = CategoryLabel(category.Category),
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 13,
            });
            // "Services" alone says little; "Fixed · HUB, Anthropic" says what it is.
            var merchants = (category.TopMerchants ?? [])
                .Select(m => Display.TransactionTitle(null, m))
                .Where(m => m.Length > 0)
                .ToList();
            var kindLabel = Display.S(category.Flexibility == "fixed" ? "LivingFixedLabel" : "LivingFlexibleLabel");
            name.Children.Add(new TextBlock
            {
                Text = merchants.Count == 0 ? kindLabel : $"{kindLabel} · {string.Join(", ", merchants)}",
                Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            head.Children.Add(name);
            var trend = o.CategoryTrends.FirstOrDefault(c => c.Category == category.Category);
            if (trend is not null && trend.Points.Count >= 2)
            {
                var spark = new Sparkline { Width = 64, Height = 20, VerticalAlignment = VerticalAlignment.Center };
                spark.SetValues(trend.Points.Select(p => Money.Parse(p.Amount)).ToList());
                Grid.SetColumn(spark, 1);
                head.Children.Add(spark);
            }
            var value = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = 96 };
            value.Children.Add(new TextBlock
            {
                Text = string.Format(Display.S("LivingPerMonthAmount"), _session.Fmt(category.MonthlyAverage)),
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            value.Children.Add(new TextBlock
            {
                Text = _session.PrivacyMode ? string.Empty : Money.FormatRatio(category.Share),
                Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"],
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            Grid.SetColumn(value, 2);
            head.Children.Add(value);
            row.Children.Add(head);
            row.Children.Add(Ui.Meter(fill, category.Flexibility == "fixed" ? "ReferencePurpleBrush" : "ReferenceBlueBrush"));

            var button = new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 7, 8, 8),
                Margin = new Thickness(-8, 0, -8, 0),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            };
            var key = category.Category;
            button.Click += (_, _) => App.Window?.NavigateToTransactions(key, range);
            ToolTipService.SetToolTip(button, tip);
            AutomationProperties.SetName(button, $"{CategoryLabel(key)}, {_session.Fmt(category.MonthlyAverage)}. {tip}");
            AutomationProperties.SetAutomationId(button, $"LivingCategory_{key}");
            CategoryList.Children.Add(button);
        }

        if (t.DiningMealsPerWeek is not null && t.DiningCostPerMeal is not null)
        {
            DiningNoteText.Visibility = Visibility.Visible;
            DiningNoteText.Text = string.Format(Display.S("LivingDiningNote"),
                t.DiningMealsPerWeek, _session.Fmt(t.DiningCostPerMeal));
        }
        else
        {
            DiningNoteText.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderRunway(LivingOverviewDto o)
    {
        var r = o.Runway;
        var has = r is not null && r.Months is not null && Money.Parse(r.LiquidCash) > 0 && HasTypical(o);
        RunwayMonthsText.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        RunwayBar.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        RunwayHintText.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        RunwayEmptyText.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (!has) return;
        var months = decimal.TryParse(r!.Months, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
        RunwayMonthsText.Text = _session.PrivacyMode
            ? Money.PrivacyMask
            : string.Format(Display.S("LivingRunwayMonths"), months.ToString("0.#", CultureInfo.CurrentCulture));
        RunwayBar.Value = (double)Math.Clamp(months, 0, 12);
        RunwayBar.Foreground = (Brush)Application.Current.Resources[
            months < 2 ? "NegativeChangeBrush" : months < 6 ? "CategoryBrushProperty" : "PositiveChangeBrush"];
        RunwayHintText.Text = string.Format(Display.S("LivingRunwayHint"),
            _session.Fmt(r.LiquidCash), _session.Fmt(o.Typical!.Spend));
    }

    private void RenderRecurring(LivingOverviewDto o)
    {
        DetectedList.Children.Clear();
        var unconfirmed = o.Detected.Count(d => d.MonthlyAmount is null);
        UnconfirmedPanel.Visibility = unconfirmed > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (unconfirmed > 0)
            UnconfirmedText.Text = string.Format(Display.S("LivingUnconfirmedNote"), unconfirmed);
        RecurringSummaryText.Text = string.Format(Display.S("LivingRecurringSummary"),
            _session.Fmt(o.Split.FixedProjected), _session.Fmt(o.Income.Projected));
        NoDetectedText.Visibility = o.Detected.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Confirmed rows first, largest first; unconfirmed last so the N/A
        // block reads as one group.
        var ordered = o.Detected
            .OrderBy(d => d.MonthlyAmount is null ? 1 : 0)
            .ThenByDescending(d => d.MonthlyAmount is null ? 0m : Money.Parse(d.MonthlyAmount))
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            if (i > 0) DetectedList.Children.Add(Ui.Divider());
            var status = item.PostedThisMonth
                ? Display.S("LivingDetectedPosted")
                : Display.S("LivingDetectedExpected");
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 7, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(BuildChip(LivingKindVisual(item.Kind), 32));
            var copy = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            copy.Children.Add(new TextBlock
            {
                Text = Display.TransactionTitle(null, item.Name),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            copy.Children.Add(new TextBlock
            {
                Text = $"{Display.LivingKindLabel(item.Kind)} · {status}",
                Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"],
            });
            Grid.SetColumn(copy, 1);
            row.Children.Add(copy);
            var unknown = item.MonthlyAmount is null;
            var amount = new TextBlock
            {
                Text = unknown
                    ? Display.S("LivingNotAvailable")
                    : string.Format(Display.S("LivingPerMonthAmount"), _session.Fmt(item.MonthlyAmount!)),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (unknown)
            {
                amount.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                ToolTipService.SetToolTip(amount, Display.S("LivingEstimateTip"));
            }
            else if (item.Kind == "income")
            {
                amount.Foreground = (Brush)Application.Current.Resources["PositiveChangeBrush"];
            }
            Grid.SetColumn(amount, 2);
            row.Children.Add(amount);
            DetectedList.Children.Add(row);
        }
    }

    private void RenderRecent(LivingOverviewDto o)
    {
        TransactionList.Children.Clear();
        // A short glance only; the Transactions page holds the full, filterable list.
        var rows = o.Transactions.Take(8).ToList();
        NoTransactionsText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0) TransactionList.Children.Add(Ui.Divider());
            TransactionList.Children.Add(BuildTransactionRow(rows[i]));
        }
    }

    private FrameworkElement BuildTransactionRow(LivingTransactionDto txn)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(BuildChip(LivingKindVisual(txn.LivingKind), 28));
        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = Display.TransactionTitle(txn.MerchantName, txn.Name),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        left.Children.Add(new TextBlock
        {
            Text = $"{Display.ShortDate(txn.Date)} · {Display.LivingKindLabel(txn.LivingKind)}",
            Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(left, 1);
        grid.Children.Add(left);
        // Money received reads as "+$x" in green, the same as on Transactions.
        var value = Money.Parse(txn.Amount);
        var amount = new TextBlock
        {
            Text = value < 0 && !_session.PrivacyMode
                ? "+" + Money.Format(-value, _session.ShowDecimals)
                : _session.Fmt(txn.Amount),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (value < 0)
            amount.Foreground = (Brush)Application.Current.Resources["PositiveChangeBrush"];
        Grid.SetColumn(amount, 2);
        grid.Children.Add(amount);
        return grid;
    }

    private void ViewAllTransactions_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateToTransactions();

    // ── Building blocks ──

    private sealed record KindVisual(string Glyph, string TintResource, string ForegroundResource);

    private static Border BuildChip(KindVisual visual, double size) =>
        Ui.IconChip(visual.Glyph, visual.TintResource, visual.ForegroundResource, size);

    private static KindVisual LivingKindVisual(string? kind) => kind switch
    {
        "rent" => new("", "ReferenceBlueTintBrush", "ReferenceBlueTextBrush"),
        "utilities" => new("", "ReferencePurpleTintBrush", "ReferencePurpleBrush"),
        "phone" => new("", "ReferenceTealTintBrush", "ReferenceGreenBrush"),
        "subscription" => new("", "ReferencePurpleTintBrush", "ReferencePurpleBrush"),
        "tuition" => new("", "ReferenceBlueTintBrush", "ReferenceBlueTextBrush"),
        "income" => new("", "SystemFillColorSuccessBackgroundBrush", "ReferenceGreenBrush"),
        "grocery" => new("", "ReferenceTealTintBrush", "ReferenceGreenBrush"),
        "dining" => new("", "ReferencePurpleTintBrush", "ReferencePurpleBrush"),
        "rideshare" => new("", "ReferenceBlueTintBrush", "ReferenceBlueTextBrush"),
        "transfer" => new("", "ReferenceNeutralTintBrush", "TextFillColorSecondaryBrush"),
        _ => new("", "ReferenceNeutralTintBrush", "TextFillColorSecondaryBrush"),
    };

    private static string CategoryLabel(string? category) =>
        string.IsNullOrEmpty(category) ? Display.S("LivingKindOther") : Display.SpendingCategoryLabel(category);

    // ── Formatting ──

    private static DateTime? ParseMonth(string yearMonth) =>
        DateTime.TryParseExact(yearMonth, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>"Jun–Aug 2026", "Jul 2026–Feb 2027", or "August 2026" for one month.</summary>
    private static string MonthsLabel(IReadOnlyList<string> months)
    {
        if (months.Count == 0) return string.Empty;
        var first = ParseMonth(months[0]);
        var last = ParseMonth(months[^1]);
        if (first is null || last is null) return string.Join("–", months);
        var culture = CultureInfo.CurrentCulture;
        if (months.Count == 1) return first.Value.ToString("MMMM yyyy", culture);
        return first.Value.Year == last.Value.Year
            ? $"{first.Value.ToString("MMM", culture)}–{last.Value.ToString("MMM yyyy", culture)}"
            : $"{first.Value.ToString("MMM yyyy", culture)}–{last.Value.ToString("MMM yyyy", culture)}";
    }
}
