using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using System.Globalization;
using AssetScope.App.Controls;
using AssetScope.App.Services;
using AssetScope.Core;
using AssetScope.Core.Api;

namespace AssetScope.App.Pages;

/// <summary>
/// Which card to use for each kind of purchase, and what recent card spending
/// earned against the best card each time. The sidecar owns every rate and
/// total; this page only shows them and saves which card each account is.
/// </summary>
public sealed partial class RewardsPage : Page
{
    private readonly AppSession _session = AppSession.Current;
    private RewardsOverviewDto? _overview;
    private CancellationTokenSource? _loadCts;
    private string _range = "3m";
    // Rebuilding the card controls raises their change events; ignore those.
    private bool _rendering;

    private const double StackedLayoutWidth = 1000;
    // Narrowest a plan tile gets before the grid drops a column.
    private const double PlanTileMinWidth = 220;
    private const int MissRowLimit = 5;

    public RewardsPage()
    {
        InitializeComponent();
        Header.Title = Display.S("RewardsTitle");
        Header.Subtitle = Display.S("RewardsSubtitle");
        RangeBar.SelectedItem = DefaultRangeItem;
        var about = Display.S("RewardsAboutTitle/Text");
        ToolTipService.SetToolTip(AboutButton, about);
        AutomationProperties.SetName(AboutButton, about);
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
        PlanGrid.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width != e.PreviousSize.Width) LayoutPlanTiles();
        };
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
        AutomationProperties.SetAutomationId(PlanHintText, "RewardsContentLoading");
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        try
        {
            _overview = await _session.Api.GetRewardsAsync(_range, _loadCts.Token);
            ErrorBar.IsOpen = false;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task SaveAsync(Func<ApiClient, Task> save)
    {
        if (_session.Api is null) return;
        try
        {
            await save(_session.Api);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void RangeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is not string range || range == _range) return;
        _range = range;
        _ = LoadAsync();
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
            PrimaryColumn.MinWidth = 0;
            SecondaryColumn.Width = new GridLength(0);
            SecondaryColumn.MinWidth = 0;
            SecondaryScroller.Visibility = Visibility.Collapsed;
        }
        else
        {
            PrimaryColumn.Width = new GridLength(2, GridUnitType.Star);
            PrimaryColumn.MinWidth = 460;
            SecondaryColumn.Width = new GridLength(1, GridUnitType.Star);
            SecondaryColumn.MinWidth = 320;
            SecondaryScroller.Visibility = Visibility.Visible;
            if (CardsCard.Parent is StackPanel panel && panel == PrimaryPanel)
            {
                foreach (var card in new FrameworkElement[] { CardsCard, PointsCard })
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
        var empty = o.Cards.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ContentGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        // The marker sits at the top of the page; the review card can start below the fold.
        AutomationProperties.SetAutomationId(empty ? EmptyTitleText : PlanHintText, "RewardsContentReady");
        if (empty) return;

        _rendering = true;
        try
        {
            RenderPlan(o);
            RenderReview(o);
            RenderBest(o);
            RenderCards(o);
            RenderPointValues(o);
        }
        finally
        {
            _rendering = false;
        }
    }

    private void RenderPlan(RewardsOverviewDto o)
    {
        var unidentified = o.Cards.Where(c => c.ProductId is null).ToList();
        UnidentifiedBar.IsOpen = unidentified.Count > 0;
        UnidentifiedBar.Message = unidentified.Count > 0
            ? string.Format(Display.S("RewardsUnidentifiedNote"), string.Join(", ", unidentified.Select(AccountLabel)))
            : string.Empty;

        PlanGrid.Children.Clear();
        var plan = RewardsPlan.Build(o.Categories);
        // A card waiting on activation earns no bonus yet, so it may rank nowhere.
        // It still gets a tile, or the reminder would go with it.
        var waiting = o.Cards
            .Where(c => c.RotatingStatus == "unconfirmed" && c.Activation?.Quarter == o.Quarter
                && plan.All(p => p.AccountId != c.AccountId))
            .Select(c => new RewardsPlanCard(c.AccountId, [], [], false));
        var tiles = plan.Concat(waiting).ToList();
        PlanEmptyText.Visibility = tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlanGrid.Visibility = tiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var entry in tiles)
            PlanGrid.Children.Add(BuildPlanTile(o, entry));
        LayoutPlanTiles();
    }

    /// <summary>
    /// Equal-width tiles, as many to a row as fit at <see cref="PlanTileMinWidth"/>.
    /// Tiles in one row share its height, so their lists start on one line.
    /// </summary>
    private void LayoutPlanTiles()
    {
        var count = PlanGrid.Children.Count;
        if (count == 0) return;
        var width = PlanGrid.ActualWidth;
        var fit = width > 0
            ? (int)((width + PlanGrid.ColumnSpacing) / (PlanTileMinWidth + PlanGrid.ColumnSpacing))
            : 2;
        var columns = Math.Clamp(fit, 1, count);
        var rows = (count + columns - 1) / columns;

        if (PlanGrid.ColumnDefinitions.Count != columns)
        {
            PlanGrid.ColumnDefinitions.Clear();
            for (var i = 0; i < columns; i++)
                PlanGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        if (PlanGrid.RowDefinitions.Count != rows)
        {
            PlanGrid.RowDefinitions.Clear();
            for (var i = 0; i < rows; i++)
                PlanGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (var i = 0; i < count; i++)
            if (PlanGrid.Children[i] is FrameworkElement tile)
                tile.At(i % columns, i / columns);
    }

    private FrameworkElement BuildPlanTile(RewardsOverviewDto o, RewardsPlanCard entry)
    {
        var card = o.Cards.First(c => c.AccountId == entry.AccountId);
        var name = CardName(o, card.AccountId);
        var body = new StackPanel { Spacing = 12 };

        var head = new Grid { ColumnSpacing = 10 };
        Ui.Columns(head, null, 1);
        head.Children.Add(new InstitutionLogo(card.InstitutionName, "credit", 32));
        head.Children.Add(new TextBlock
        {
            Text = name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        }.At(1));
        body.Children.Add(head);
        body.Children.Add(Ui.Divider());

        if (entry.Items.Count > 0)
        {
            var rates = new StackPanel { Spacing = 10 };
            foreach (var item in entry.Items)
                rates.Children.Add(BuildPlanRate(item));
            body.Children.Add(rates);
        }

        foreach (var (glyph, text, link, activateQuarter) in PlanReminders(o, card, entry))
        {
            body.Children.Add(Ui.Note(glyph, text, caution: true, link));
            if (activateQuarter is null) continue;
            // Lines up with the note's text, just under it.
            var activated = ActivatedButton(card, activateQuarter, $"RewardsPlanActivated_{card.AccountId}");
            activated.Margin = new Thickness(18, -4, 0, 0);
            body.Children.Add(activated);
        }

        var tile = new Border
        {
            Background = Ui.Brush("ReferenceNeutralTintBrush"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 16),
            Child = body,
        };
        AutomationProperties.SetAutomationId(tile, $"RewardsPlan_{entry.AccountId}");
        AutomationProperties.SetName(tile, name);
        return tile;
    }

    /// <summary>"[glyph] Dining ......... 5x"</summary>
    private static Grid BuildPlanRate(RewardsPlanItem item)
    {
        var row = new Grid { ColumnSpacing = 10 };
        Ui.Columns(row, null, 1, null);
        row.Children.Add(new FontIcon
        {
            Glyph = CategoryGlyph(item.Category),
            FontSize = 14,
            Width = 16,
            Foreground = Ui.Brush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(Ui.Label(CategoryLabel(item.Category)).At(1));
        var rate = RateText(item.Rate, item.Unit);
        row.Children.Add(new TextBlock
        {
            Text = rate,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ui.Brush("ReferenceBlueTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        }.At(2));
        AutomationProperties.SetName(row, $"{CategoryLabel(item.Category)} {rate}");
        return row;
    }

    /// <summary>What it takes to actually earn the plan's rates on this card this quarter.</summary>
    private IEnumerable<(string Glyph, string Text, (string Text, Uri Uri)? Link, string? ActivateQuarter)> PlanReminders(RewardsOverviewDto o, RewardCardDto card, RewardsPlanCard entry)
    {
        // This quarter's issuer list earns nothing until the user confirms it, so
        // remind until its deadline or until they say it's done; after that, next
        // quarter's once its activation opens.
        if (card.Activation is { } activation && Uri.TryCreate(activation.Url, UriKind.Absolute, out var url)
            && (activation.Quarter != o.Quarter || card.RotatingStatus == "unconfirmed"))
            yield return ("\uE787",
                activation.Quarter == o.Quarter && card.RotatingRates.Count > 0
                    ? string.Format(Display.S("RewardsPlanActivateRates"), RotatingRatesText(card), ShortDate(activation.ActivateBy))
                    : string.Format(Display.S("RewardsPlanActivateBy"), QuarterLabel(activation.Quarter), ShortDate(activation.ActivateBy)),
                (string.Format(Display.S("RewardsActivateOn"), IssuerName(o, card)), url),
                activation.Quarter);

        // Categories sharing one cap carry the same warning; say it once.
        var caps = o.Categories
            .Where(c => entry.Categories.Contains(c.Category) && c.CapWarning?.AccountId == card.AccountId)
            .Select(c => c.CapWarning!)
            .DistinctBy(w => (w.Cap, w.QuarterlySpend));
        foreach (var cap in caps)
            yield return ("\uE7BA", string.Format(Display.S("RewardsCapWarning"),
                _session.Fmt(cap.Cap), _session.Fmt(cap.QuarterlySpend)), null, null);
    }

    private void RenderReview(RewardsOverviewDto o)
    {
        var r = o.Review;
        var spend = Money.Parse(r.Spend);
        var earned = Money.Parse(r.Earned);
        var missed = Money.Parse(r.Missed);
        ReviewSpentText.Text = _session.Fmt(r.Spend);
        ReviewCountText.Text = string.Format(Display.S("RewardsPurchaseCount"), r.TransactionCount, WindowLabel(o.Window.Range));
        ReviewEarnedText.Text = _session.Fmt(r.Earned);
        ReviewEarnedRateText.Text = spend > 0
            ? string.Format(Display.S("RewardsEarnedRate"), PercentText(earned / spend))
            : string.Empty;
        ReviewMissedText.Text = _session.Fmt(r.Missed);
        ReviewBestText.Text = missed > 0
            ? string.Format(Display.S("RewardsBestEachTime"), _session.Fmt(r.BestPossible))
            : Display.S("RewardsNothingMissed");

        var bestPossible = Money.Parse(r.BestPossible);
        CapturePanel.Visibility = bestPossible > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (bestPossible > 0)
        {
            var share = Math.Round(Math.Clamp(earned / bestPossible, 0m, 1m), 2);
            CaptureEarnedColumn.Width = new GridLength(Math.Max((double)share, 0.0001), GridUnitType.Star);
            CaptureMissedColumn.Width = new GridLength(Math.Max(1 - (double)share, 0.0001), GridUnitType.Star);
            CaptureFill.Visibility = share > 0 ? Visibility.Visible : Visibility.Collapsed;
            CaptureText.Text = string.Format(Display.S("RewardsCaptured"), PercentText(share));
            AutomationProperties.SetName(CaptureMeter, CaptureText.Text);
        }

        var unidentified = Money.Parse(r.UnidentifiedSpend);
        ReviewUnidentifiedText.Visibility = unidentified > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReviewUnidentifiedText.Text = unidentified > 0
            ? string.Format(Display.S("RewardsUnidentifiedSpend"), _session.Fmt(r.UnidentifiedSpend))
            : string.Empty;

        MissList.Children.Clear();
        var misses = r.TopMisses.Take(MissRowLimit).ToList();
        NoMissesText.Visibility = misses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < misses.Count; i++)
        {
            if (i > 0) MissList.Children.Add(Ui.Divider());
            MissList.Children.Add(BuildMissRow(o, misses[i]));
        }
    }

    private FrameworkElement BuildMissRow(RewardsOverviewDto o, RewardMissDto miss)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 10, 0, 10) };
        Ui.Columns(grid, null, 1, null);
        grid.Children.Add(Ui.IconChip(CategoryGlyph(miss.Category), "ReferenceNeutralTintBrush", "TextFillColorSecondaryBrush"));

        var title = Display.TransactionTitle(miss.MerchantName, miss.Name);
        var copy = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(Ui.Label(title));
        copy.Children.Add(Ui.Caption(string.Format(Display.S("RewardsMissLine"),
            ShortDate(miss.Date), _session.Fmt(miss.Amount), CardName(o, miss.AccountId, withMask: false))));
        grid.Children.Add(copy.At(1));

        // A miss is money not collected; it stays neutral rather than reading as a gain.
        var best = CardName(o, miss.BestAccountId, withMask: false);
        var value = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 180 };
        value.Children.Add(new TextBlock
        {
            Text = _session.Fmt(miss.Missed),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        var use = Ui.Caption(string.Format(Display.S("RewardsMissUse"), best));
        use.HorizontalAlignment = HorizontalAlignment.Right;
        value.Children.Add(use);
        grid.Children.Add(value.At(2));

        var better = string.Format(Display.S("RewardsMissBetter"), best, _session.Fmt(miss.BestPossible));
        ToolTipService.SetToolTip(grid, better);
        AutomationProperties.SetName(grid, $"{title}, {_session.Fmt(miss.Missed)}. {better}");
        return grid;
    }

    private void RenderBest(RewardsOverviewDto o)
    {
        BestHintText.Text = string.Format(Display.S("RewardsBestHint"), WindowLabel(o.Window.Range));
        CategoryList.Children.Clear();
        var review = o.Review.ByCategory.ToDictionary(c => c.Category);
        for (var i = 0; i < o.Categories.Count; i++)
        {
            if (i > 0) CategoryList.Children.Add(Ui.Divider());
            var category = o.Categories[i];
            review.TryGetValue(category.Category, out var reviewed);
            CategoryList.Children.Add(BuildCategoryRow(o, category, reviewed));
        }
    }

    /// <summary>One table row; the column widths match <c>CategoryHeader</c> in the XAML.</summary>
    private FrameworkElement BuildCategoryRow(RewardsOverviewDto o, RewardCategoryDto category, RewardReviewCategoryDto? reviewed)
    {
        var hasSpend = Money.Parse(category.Spend) > 0;
        var label = CategoryLabel(category.Category);
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 10, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

        row.Children.Add(Ui.IconChip(CategoryGlyph(category.Category),
            hasSpend ? "ReferenceBlueTintBrush" : "ReferenceNeutralTintBrush",
            hasSpend ? "ReferenceBlueTextBrush" : "TextFillColorTertiaryBrush", 28));

        var name = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var labelText = Ui.Label(label, strong: hasSpend);
        if (!hasSpend) labelText.Foreground = Ui.Brush("TextFillColorSecondaryBrush");
        name.Children.Add(labelText);
        name.Children.Add(Ui.Caption(hasSpend
            ? string.Format(Display.S("RewardsCategorySpend"), _session.Fmt(category.Spend), category.TransactionCount)
            : Display.S("RewardsCategoryNoSpend")));
        row.Children.Add(name.At(1));

        row.Children.Add(BuildBestCell(o, category).At(2));

        // The actionable number: where the card in hand was not the best one.
        var missed = reviewed is null ? 0m : Money.Parse(reviewed.Missed);
        var missedText = new TextBlock
        {
            Text = missed > 0 ? _session.Fmt(reviewed!.Missed) : "—",
            FontWeight = missed > 0 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = Ui.Brush(missed > 0 ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(missedText.At(3));

        var accessible = label;
        if (missed > 0 && reviewed!.MostUsedAccountId is { } used)
        {
            var usedIsBest = category.Best.Any(b => b.AccountId == used);
            var why = usedIsBest
                ? string.Format(Display.S("RewardsMissedSome"), _session.Fmt(reviewed.Missed))
                : string.Format(Display.S("RewardsMostlyOn"), CardName(o, used, withMask: false), _session.Fmt(reviewed.Missed));
            ToolTipService.SetToolTip(missedText, why);
            accessible = $"{label}. {why}";
        }
        AutomationProperties.SetName(row, accessible);
        AutomationProperties.SetAutomationId(row, $"RewardsCategory_{category.Category}");
        return row;
    }

    private FrameworkElement BuildBestCell(RewardsOverviewDto o, RewardCategoryDto category)
    {
        if (category.Best.Count == 0)
        {
            var none = Ui.Caption(Display.S("RewardsNoCardEarns"));
            none.VerticalAlignment = VerticalAlignment.Center;
            return none;
        }

        var lead = o.Cards.FirstOrDefault(c => c.AccountId == category.Best[0].AccountId);
        var names = category.Best.Take(2).Select(b => CardName(o, b.AccountId, withMask: false)).ToList();
        var cell = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        Ui.Columns(cell, null, 1);
        cell.Children.Add(new InstitutionLogo(lead?.InstitutionName, "credit", 22) { VerticalAlignment = VerticalAlignment.Top });

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(Ui.Label(names.Count == 1 ? names[0] : string.Format(Display.S("RewardsTie"), names[0], names[1])));
        text.Children.Add(Ui.Caption(RateDescription(category.Best[0].Rate, category.Best[0].Unit, category.Best[0].EffectiveRate)));
        if (category.Portal is { } portal)
        {
            var tip = Ui.Caption(string.Format(Display.S("RewardsPortalShort"), RateText(portal.Rate, portal.Unit), portal.Label));
            tip.Foreground = Ui.Brush("ReferenceBlueTextBrush");
            ToolTipService.SetToolTip(tip, string.Format(Display.S("RewardsPortalTip"),
                RateText(portal.Rate, portal.Unit), portal.Label, CardName(o, portal.AccountId, withMask: false)));
            text.Children.Add(tip);
        }
        cell.Children.Add(text.At(1));
        return cell;
    }

    private void RenderCards(RewardsOverviewDto o)
    {
        CardList.Children.Clear();
        for (var i = 0; i < o.Cards.Count; i++)
        {
            if (i > 0) CardList.Children.Add(Ui.Divider());
            var block = BuildCardBlock(o, o.Cards[i]);
            // The card's own padding already frames the first and last blocks.
            block.Padding = new Thickness(0, i == 0 ? 6 : 14, 0, i == o.Cards.Count - 1 ? 0 : 14);
            CardList.Children.Add(block);
        }
    }

    private StackPanel BuildCardBlock(RewardsOverviewDto o, RewardCardDto card)
    {
        var block = new StackPanel { Spacing = 10 };
        AutomationProperties.SetAutomationId(block, $"RewardsCard_{card.AccountId}");
        var name = CardName(o, card.AccountId);

        var head = new Grid { ColumnSpacing = 10 };
        Ui.Columns(head, null, 1, null);
        head.Children.Add(new InstitutionLogo(card.InstitutionName, "credit", 36));
        var title = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(Ui.Label(name, strong: true));
        title.Children.Add(Ui.Caption(card.Earned is null
            ? string.Format(Display.S("RewardsCardActivityUnknown"), _session.Fmt(card.Spend))
            : string.Format(Display.S("RewardsCardActivity"), _session.Fmt(card.Spend), _session.Fmt(card.Earned)), wrap: true));
        head.Children.Add(title.At(1));
        if (card.Unit is not null && card.BaseRate is not null && BuildNotesButton(card, name) is { } notes)
            head.Children.Add(notes.At(2));
        block.Children.Add(head);

        var product = new ComboBox
        {
            PlaceholderText = Display.S("RewardsChooseProduct"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        // A card the bank data cannot pin down lists the products that fit it first.
        var candidates = card.ProductCandidates ?? [];
        foreach (var p in o.Products.OrderBy(p => candidates.IndexOf(p.Id) is var rank && rank < 0 ? int.MaxValue : rank))
        {
            var item = new ComboBoxItem { Content = ProductName(p), Tag = p.Id };
            product.Items.Add(item);
            if (p.Id == card.ProductId) product.SelectedItem = item;
        }
        AutomationProperties.SetName(product, string.Format(Display.S("RewardsProductFor"), card.DisplayName));
        AutomationProperties.SetAutomationId(product, $"RewardsProduct_{card.AccountId}");
        product.SelectionChanged += (_, _) =>
        {
            if (_rendering || product.SelectedItem is not ComboBoxItem { Tag: string id } || id == card.ProductId && card.ProductSource == "chosen") return;
            _ = SaveAsync(api => api.PutRewardCardAsync(card.AccountId, new { productId = id }));
        };
        block.Children.Add(product);
        var origin = card switch
        {
            { ProductSource: "detected", ProductDetectedBy: "annualFee" } => Display.S("RewardsDetectedFee"),
            { ProductSource: "detected" } => Display.S("RewardsDetected"),
            { ProductId: null } when candidates.Count > 0 => string.Format(Display.S("RewardsCandidatesHint"), card.DisplayName),
            _ => null,
        };
        if (origin is not null)
        {
            var caption = Ui.Caption(origin, wrap: true);
            caption.Margin = new Thickness(0, -4, 0, 0);
            block.Children.Add(caption);
        }

        var selected = o.Products.FirstOrDefault(p => p.Id == card.ProductId);
        if (selected?.HasChoice == true)
        {
            var choice = new ComboBox
            {
                Header = Display.S("RewardsChoiceHeader"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            foreach (var option in o.ChoiceOptions)
            {
                var item = new ComboBoxItem { Content = Display.S($"RewardsChoice_{option}"), Tag = option };
                choice.Items.Add(item);
                if (option == card.ChoiceCategory) choice.SelectedItem = item;
            }
            AutomationProperties.SetAutomationId(choice, $"RewardsChoice_{card.AccountId}");
            choice.SelectionChanged += (_, _) =>
            {
                if (_rendering || choice.SelectedItem is not ComboBoxItem { Tag: string option } || option == card.ChoiceCategory) return;
                _ = SaveAsync(api => api.PutRewardCardAsync(card.AccountId, new { choiceCategory = option }));
            };
            block.Children.Add(choice);
        }
        if (selected?.HasRotating == true)
        {
            block.Children.Add(BuildRotatingPicker(o, card));
            var details = BuildRotatingDetails(o, card, selected.Issuer);
            if (details.Children.Count > 0) block.Children.Add(details);
        }

        // Every rate the card earns, as one quiet line under the controls.
        if (card.Unit is not null && card.BaseRate is not null)
        {
            var rates = card.Bonuses
                .Select(b => $"{CategoryLabel(b.Category)} {RateText(b.Rate, card.Unit)}")
                .Append($"{CategoryLabel(RewardsPlan.EverydayCategory)} {RateText(card.BaseRate, card.Unit)}");
            block.Children.Add(Ui.Caption(string.Join("  ·  ", rates), wrap: true));
        }
        return block;
    }

    /// <summary>The card's terms (caps, memberships, portals) behind an info button.</summary>
    private static Button? BuildNotesButton(RewardCardDto card, string name)
    {
        var lines = card.Notes.Select(n => NoteText(n, card)).Where(t => t.Length > 0).ToList();
        if (lines.Count == 0) return null;
        var list = new StackPanel { Spacing = 8, MaxWidth = 320 };
        list.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        foreach (var line in lines)
            list.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
        var button = new Button
        {
            Style = Ui.Style("InfoTipButtonStyle"),
            VerticalAlignment = VerticalAlignment.Top,
            Content = new FontIcon { Glyph = "\uE946", FontSize = 12, Foreground = Ui.Brush("TextFillColorTertiaryBrush") },
            Flyout = new Flyout { Content = list, Placement = FlyoutPlacementMode.Bottom },
        };
        var label = string.Format(Display.S("RewardsCardTerms"), name);
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetAutomationId(button, $"RewardsTerms_{card.AccountId}");
        return button;
    }

    private FrameworkElement BuildRotatingPicker(RewardsOverviewDto o, RewardCardDto card)
    {
        var current = card.RotatingCategories;
        var button = new DropDownButton
        {
            Content = current.Count == 0
                ? string.Format(Display.S("RewardsRotatingNone"), QuarterLabel(o.Quarter))
                : string.Format(Display.S("RewardsRotatingSet"), QuarterLabel(o.Quarter),
                    string.Join(", ", current.Select(CategoryLabel))),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetAutomationId(button, $"RewardsRotating_{card.AccountId}");
        // Checkboxes in a plain flyout stay open while several are ticked; a
        // menu flyout would close after each one.
        var list = new StackPanel { Spacing = 2, MinWidth = 220 };
        var hint = Ui.Caption(string.Format(Display.S("RewardsRotatingHint"), QuarterLabel(o.Quarter)), wrap: true);
        hint.MaxWidth = 260;
        hint.Margin = new Thickness(0, 0, 0, 6);
        list.Children.Add(hint);
        var picked = new HashSet<string>(current);
        foreach (var category in o.Categories.Select(c => c.Category).Where(c => c != RewardsPlan.EverydayCategory)
                     .OrderBy(c => Array.IndexOf(CategoryOrder, c)))
        {
            var box = new CheckBox { Content = CategoryLabel(category), IsChecked = picked.Contains(category), Tag = category };
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true) picked.Add(category); else picked.Remove(category);
            };
            list.Children.Add(box);
        }
        var flyout = new Flyout { Content = list, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        flyout.Closed += (_, _) =>
        {
            if (picked.SetEquals(current)) return;
            var ordered = CategoryOrder.Where(picked.Contains).ToArray();
            _ = SaveAsync(api => api.PutRewardCardAsync(card.AccountId, new { rotatingCategories = ordered }));
        };
        button.Flyout = flyout;
        return button;
    }

    /// <summary>
    /// Where this quarter's list came from, picks the app cannot score, next
    /// quarter's list, and links to activate and to the issuer's announcement.
    /// </summary>
    private StackPanel BuildRotatingDetails(RewardsOverviewDto o, RewardCardDto card, string issuer)
    {
        var details = new StackPanel { Spacing = 4, Margin = new Thickness(0, -4, 0, 0) };
        var quarter = QuarterLabel(o.Quarter);
        var status = card.RotatingSource switch
        {
            "calendar" when card.RotatingActivated => string.Format(Display.S("RewardsRotatingCalendarActivated"), quarter),
            "calendar" when card.RotatingActivateBy is { } by => string.CompareOrdinal(o.AsOf, by) <= 0
                ? string.Format(Display.S("RewardsRotatingCalendar"), quarter, ShortDate(by))
                : string.Format(Display.S("RewardsRotatingCalendarPast"), quarter),
            null when card.RotatingAnnouncement?.State == "unreadable" =>
                string.Format(Display.S("RewardsRotatingUnreadable"), issuer, quarter),
            null => string.Format(Display.S("RewardsRotatingNotPosted"), issuer, quarter),
            _ => null,
        };
        if (status is not null) details.Children.Add(Ui.Caption(status, wrap: true));

        if (card.RotatingStatus == "unconfirmed" && card.RotatingRates.Count > 0)
            details.Children.Add(Ui.Caption(string.Format(Display.S("RewardsRotatingRates"), RotatingRatesText(card)), wrap: true));
        if (card.CapUsage is { } cap)
            details.Children.Add(Ui.Caption(string.Format(Display.S("RewardsRotatingCapUsed"),
                _session.Fmt(cap.Used), _session.Fmt(cap.Cap)), wrap: true));

        if (card.RotatingUntracked.Count > 0)
            details.Children.Add(Ui.Caption(string.Format(Display.S("RewardsRotatingUntracked"),
                string.Join(", ", card.RotatingUntracked)), wrap: true));

        if (card.RotatingNext is { } next)
        {
            var picks = string.Join(", ", next.Categories.Select(CategoryLabel).Concat(next.Untracked));
            var line = next switch
            {
                { Activated: true } => Display.S("RewardsRotatingNextActivated"),
                { Open: true } => Display.S("RewardsRotatingNextOpen"),
                _ => Display.S("RewardsRotatingNext"),
            };
            details.Children.Add(Ui.Caption(
                string.Format(line, QuarterLabel(next.Quarter), picks, ShortDate(next.ActivateBy)), wrap: true));
        }

        var links = new List<(string Text, Uri Uri)>();
        if (card.Activation is { } activation && Uri.TryCreate(activation.Url, UriKind.Absolute, out var activate))
            links.Add((string.Format(Display.S("RewardsActivateOn"), issuer), activate));
        if (card.RotatingAnnouncement is { } posted && Uri.TryCreate(posted.Url, UriKind.Absolute, out var announcement))
            links.Add((string.Format(Display.S("RewardsAnnouncementLink"), issuer), announcement));
        if (links.Count > 0)
        {
            var line = Ui.Links([.. links]);
            line.Margin = new Thickness(0, 2, 0, 0);
            AutomationProperties.SetAutomationId(line, $"RewardsRotatingLinks_{card.AccountId}");
            details.Children.Add(line);
        }
        if (card.Activation is { } open)
        {
            var activated = ActivatedButton(card, open.Quarter, $"RewardsActivated_{card.AccountId}");
            activated.Margin = new Thickness(0, 4, 0, 0);
            details.Children.Add(activated);
        }
        // Past the deadline there is nothing left to activate, but it may have been done in time.
        if (card.RotatingStatus == "unconfirmed" && card.Activation?.Quarter != o.Quarter)
        {
            var confirmed = ActivatedButton(card, o.Quarter, $"RewardsActivatedThisQuarter_{card.AccountId}",
                string.Format(Display.S("RewardsActivatedQuarterButton"), quarter));
            confirmed.Margin = new Thickness(0, 4, 0, 0);
            details.Children.Add(confirmed);
        }
        return details;
    }

    /// <summary>
    /// "I've activated it": the app can't see activation with the issuer, so
    /// the user marks the quarter done, its bonus counts, and its reminder goes away.
    /// </summary>
    private Button ActivatedButton(RewardCardDto card, string quarter, string automationId, string? text = null)
    {
        var label = text ?? Display.S("RewardsActivatedButton");
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = "\uE73E", FontSize = 12 });
        content.Children.Add(new TextBlock { Text = label });
        var button = new Button
        {
            Content = content,
            FontSize = 12,
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) =>
        {
            button.IsEnabled = false;
            _ = SaveAsync(api => api.PutRewardCardAsync(card.AccountId, new { activation = new { quarter, activated = true } }));
        };
        return button;
    }

    private void RenderPointValues(RewardsOverviewDto o)
    {
        PointValueList.Children.Clear();
        PointsCard.Visibility = o.PointValues.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var pv in o.PointValues)
        {
            var row = new Grid { ColumnSpacing = 12 };
            Ui.Columns(row, 1, null);
            var label = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = pv.Name, TextWrapping = TextWrapping.Wrap });
            label.Children.Add(Ui.Caption(string.Format(Display.S("RewardsPointsDefault"), CentsText(pv.DefaultCentsPerPoint))));
            row.Children.Add(label);

            var box = new NumberBox
            {
                Value = (double)ParseInvariant(pv.CentsPerPoint),
                Minimum = (double)ParseInvariant(pv.Minimum),
                Maximum = (double)ParseInvariant(pv.Maximum),
                SmallChange = 0.05,
                LargeChange = 0.25,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Width = 120,
                VerticalAlignment = VerticalAlignment.Center,
                NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
                {
                    FractionDigits = 2,
                    IntegerDigits = 1,
                    NumberRounder = new Windows.Globalization.NumberFormatting.IncrementNumberRounder
                    {
                        Increment = 0.01,
                        RoundingAlgorithm = Windows.Globalization.NumberFormatting.RoundingAlgorithm.RoundHalfUp,
                    },
                },
            };
            AutomationProperties.SetName(box, string.Format(Display.S("RewardsPointsBoxName"), pv.Name));
            AutomationProperties.SetAutomationId(box, $"RewardsPointValue_{pv.Currency}");
            var currency = pv.Currency;
            var stored = ParseInvariant(pv.CentsPerPoint);
            box.ValueChanged += (sender, args) =>
            {
                if (_rendering || double.IsNaN(args.NewValue)) return;
                var cents = Math.Round((decimal)args.NewValue, 2);
                if (cents == stored) return;
                var value = cents.ToString("0.00", CultureInfo.InvariantCulture);
                _ = SaveAsync(api => api.PutRewardPreferencesAsync(new { pointValues = new Dictionary<string, string> { [currency] = value } }));
            };
            row.Children.Add(box.At(1));
            PointValueList.Children.Add(row);
        }
    }

    // ── Labels ──

    private static readonly string[] CategoryOrder =
    [
        "dining", "groceries", "gas", "travel", "transit", "drugstores", "streaming",
        "onlineShopping", "phoneInternet", "homeImprovement", "entertainment", "other",
    ];

    private static string CategoryLabel(string category) => Display.S($"RewardsCategory_{category}");

    private static string CategoryGlyph(string category) => category switch
    {
        "dining" => "\uEC32",
        "groceries" => "\uE7BF",
        "gas" => "\uE945",
        "travel" => "\uE709",
        "transit" => "\uE806",
        "drugstores" => "\uE95E",
        "streaming" => "\uE714",
        "onlineShopping" => "\uE719",
        "phoneInternet" => "\uE717",
        "homeImprovement" => "\uE90F",
        "entertainment" => "\uE7FC",
        _ => "\uE8C7",
    };

    private static string ProductName(RewardProductDto p) => p.Id switch
    {
        "flat-2" => Display.S("RewardsProductFlat2"),
        "flat-1-5" => Display.S("RewardsProductFlat15"),
        "flat-1" => Display.S("RewardsProductFlat1"),
        "no-rewards" => Display.S("RewardsProductNone"),
        _ => p.Name,
    };

    private static string IssuerName(RewardsOverviewDto o, RewardCardDto card) =>
        o.Products.FirstOrDefault(p => p.Id == card.ProductId)?.Issuer ?? string.Empty;

    private static string AccountLabel(RewardCardDto card) =>
        string.IsNullOrEmpty(card.Mask) ? card.DisplayName : $"{card.DisplayName} ••{card.Mask}";

    /// <summary>
    /// The card as the user knows it: its product name when identified, else the
    /// account name. Dense rows drop the last four digits unless two cards share a name.
    /// </summary>
    private static string CardName(RewardsOverviewDto o, string accountId, bool withMask = true)
    {
        var card = o.Cards.FirstOrDefault(c => c.AccountId == accountId);
        if (card is null) return string.Empty;
        var name = BaseCardName(card);
        if (string.IsNullOrEmpty(card.Mask) || !withMask && o.Cards.Count(c => BaseCardName(c) == name) == 1)
            return name;
        return $"{name} ••{card.Mask}";
    }

    private static string BaseCardName(RewardCardDto card) => card.ProductId switch
    {
        null => card.DisplayName,
        "flat-2" or "flat-1-5" or "flat-1" or "no-rewards" => card.DisplayName,
        _ => card.ProductName!,
    };

    private static string RotatingRatesText(RewardCardDto card) =>
        string.Join(", ", card.RotatingRates.Select(r => $"{CategoryLabel(r.Category)} {RateText(r.Rate, card.Unit!)}"));

    private static string RateText(string rate, string unit) =>
        string.Format(Display.S(unit == "percent" ? "RewardsRatePercent" : "RewardsRatePoints"), rate);

    private static string RateDescription(string rate, string unit, string effectiveRate) =>
        unit == "percent"
            ? string.Format(Display.S("RewardsRateCash"), rate)
            : string.Format(Display.S("RewardsRatePointsWorth"), rate, PercentText(ParseInvariant(effectiveRate)));

    private static string NoteText(RewardNoteDto note, RewardCardDto card) => note.Kind switch
    {
        "quarterlyCap" when note.Scope == "rotating" => string.Format(Display.S("RewardsNoteCapRotating"),
            Money.Format(ParseInvariant(note.Amount ?? "0"), false)),
        "quarterlyCap" => string.Format(Display.S("RewardsNoteCapBonus"),
            Money.Format(ParseInvariant(note.Amount ?? "0"), false), RateText(card.BaseRate!, card.Unit!)),
        "onlineGroceryOnly" => Display.S("RewardsNoteOnlineGrocery"),
        "membershipRequired" => string.Format(Display.S("RewardsNoteMembership"), note.Membership),
        "portal" => string.Format(Display.S("RewardsNotePortal"), RateText(note.Rate ?? "0", card.Unit!), note.Label),
        "pooledPoints" => Display.S("RewardsNotePooled"),
        "bofaBoost" => Display.S("RewardsNoteBofaBoost"),
        "travelDiningRedemption" => Display.S("RewardsNoteTravelDining"),
        _ => string.Empty,
    };

    private static string WindowLabel(string range) => range switch
    {
        "1m" => Display.S("RewardsWindow1m"),
        "1y" => Display.S("RewardsWindow1y"),
        _ => Display.S("RewardsWindow3m"),
    };

    /// <summary>"2026-Q4" → "Q4 2026".</summary>
    private static string QuarterLabel(string quarter) =>
        quarter.Length == 7 ? $"{quarter[5..]} {quarter[..4]}" : quarter;

    private static string PercentText(decimal ratio) =>
        string.Format(Display.S("RewardsPercent"), (ratio * 100).ToString("0.##", CultureInfo.CurrentCulture));

    private static string CentsText(string cents) =>
        string.Format(Display.S("RewardsCents"), ParseInvariant(cents).ToString("0.00", CultureInfo.CurrentCulture));

    private static decimal ParseInvariant(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;

    private static string ShortDate(string ymd) =>
        DateTime.TryParseExact(ymd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("MMM d", CultureInfo.CurrentCulture)
            : ymd;
}
