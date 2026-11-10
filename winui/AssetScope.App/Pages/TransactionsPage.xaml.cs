using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System.Globalization;
using AssetScope.App.Controls;
using AssetScope.App.Services;
using AssetScope.Core;
using AssetScope.Core.Api;

namespace AssetScope.App.Pages;

/// <summary>
/// Row model for the transactions ListView. Rows are rebuilt on every render
/// (privacy toggle, filter change), so the template binds OneTime.
/// </summary>
public sealed class TransactionRow
{
    public required BankTransactionDto Transaction { get; init; }
    public required string Title { get; init; }
    public required string Meta { get; init; }
    public required string AmountText { get; init; }
    public required Brush AmountBrush { get; init; }
    public required string Glyph { get; init; }
    public required Brush TintBrush { get; init; }
    public required Brush GlyphBrush { get; init; }
    public string AccessibleName => $"{Title}, {AmountText}, {Meta}";
}

/// <summary>One posting day. The group itself is the row list so the
/// CollectionViewSource can group without a wrapper.</summary>
public sealed class TransactionGroup : List<TransactionRow>
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
}

public sealed partial class TransactionsPage : Page
{
    // The rail needs its full width to keep three stat tiles readable; below
    // this content width the rail moves above the list as a tile strip.
    private const double StackedLayoutWidth = 1060;
    private const double CompactToolbarWidth = 900;
    private const int FetchLimit = 1000;

    /// <summary>Filter requested by another page before navigating here (Living's category rows).</summary>
    public static string? PendingCategory;
    public static string? PendingRange;

    private readonly AppSession _session = AppSession.Current;
    private List<BankTransactionDto> _transactions = [];
    private TransactionsSummaryDto? _summary;
    private List<AccountSummaryDto> _accounts = [];
    private List<TransactionRow> _rows = [];
    // Rolling 30 days by default: a calendar month is empty on the 1st and
    // thin for the first week, which would make the page open on nothing.
    private string _range = "30d";
    private string _kind = "all";
    private string? _accountId;
    // Category drill-down is applied by the sidecar (it knows every legacy
    // spelling), so the rows always add up to the rail's category total even
    // past the fetch cap.
    private string? _category;
    private string _search = "";
    private string? _selectedId;
    private bool _stacked;
    private bool _toolbarCompact;
    private bool _suppressPickers;
    private bool _ready;
    private bool _loadedOnce;
    private CancellationTokenSource? _loadCts;

    public TransactionsPage()
    {
        InitializeComponent();
        Header.Title = Display.S("TransactionsTitle");
        Header.Subtitle = Display.S("TransactionsSubtitle");
        _suppressPickers = true;
        RangeBar.SelectedItem = Range30DaysItem;
        KindPicker.SelectedIndex = 0;
        PopulateAccountPicker();
        _suppressPickers = false;
        ListEmptyTitle.Text = Display.S("TransactionsListEmptyTitle");
        ListEmptyBody.Text = Display.S("TransactionsListEmptyBody");
        var clear = Display.S("TransactionsClearFilter");
        ToolTipService.SetToolTip(ClearFilterButton, clear);
        AutomationProperties.SetName(ClearFilterButton, clear);
    }

    public void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _ready = true;
        _session.DataRefreshed += OnDataRefreshed;
        _session.PropertyChanged += OnSessionPropertyChanged;
        ApplyPendingFilter(reload: false);
        _ = LoadAsync();
    }

    /// <summary>Adopts a filter handed over by another page; no-op when none is pending.</summary>
    public void ApplyPendingFilter(bool reload)
    {
        if (PendingCategory is null && PendingRange is null) return;
        if (PendingRange is not null)
        {
            _range = PendingRange;
            var item = RangeBar.Items.FirstOrDefault(i => i.Tag as string == PendingRange);
            if (item is not null)
            {
                _suppressPickers = true;
                RangeBar.SelectedItem = item;
                _suppressPickers = false;
            }
        }
        _category = PendingCategory;
        PendingCategory = null;
        PendingRange = null;
        if (reload) _ = LoadAsync();
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

    // ── Data ──

    private (string From, string To) CurrentWindow()
    {
        var today = DateTime.Today;
        var first = new DateTime(today.Year, today.Month, 1);
        var monthEnd = first.AddMonths(1).AddDays(-1);
        return _range switch
        {
            "30d" => (Ymd(today.AddDays(-29)), Ymd(today)),
            "lastMonth" => (Ymd(first.AddMonths(-1)), Ymd(first.AddDays(-1))),
            "3m" => (Ymd(first.AddMonths(-2)), Ymd(monthEnd)),
            "12m" => (Ymd(first.AddMonths(-11)), Ymd(monthEnd)),
            _ => (Ymd(first), Ymd(monthEnd)),
        };
    }

    private async Task LoadAsync()
    {
        if (_session.Api is null) return;
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        LoadingRing.IsActive = true;
        var (from, to) = CurrentWindow();
        try
        {
            var accountsTask = _accounts.Count == 0
                ? _session.Api.GetAccountsAsync(includeExcluded: false, ct: cts.Token)
                : null;
            var listTask = _session.Api.GetTransactionsAsync(from, to, FetchLimit, _accountId, _category, cts.Token);
            var summaryTask = _session.Api.GetTransactionsSummaryAsync(from, to, _accountId, cts.Token);
            if (accountsTask is not null)
            {
                _accounts = (await accountsTask).Accounts
                    .Where(a => a.Category is "bank" or "cash" or "credit" or "loan")
                    .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            _transactions = (await listTask).Transactions;
            _summary = await summaryTask;
            if (cts.IsCancellationRequested) return;
            ErrorBar.IsOpen = false;
            _loadedOnce = true;
            if (accountsTask is not null) PopulateAccountPicker();
            Render();
        }
        catch (OperationCanceledException) { }
        catch (ApiException ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            if (ReferenceEquals(cts, _loadCts)) LoadingRing.IsActive = false;
        }
    }

    private void PopulateAccountPicker()
    {
        var suppress = _suppressPickers;
        _suppressPickers = true;
        AccountPicker.Items.Clear();
        AccountPicker.Items.Add(new ComboBoxItem { Content = Display.S("TransactionsAllAccounts"), Tag = null });
        var selectedIndex = 0;
        foreach (var account in _accounts)
        {
            AccountPicker.Items.Add(new ComboBoxItem { Content = account.DisplayName, Tag = account.Id });
            if (account.Id == _accountId) selectedIndex = AccountPicker.Items.Count - 1;
        }
        AccountPicker.SelectedIndex = selectedIndex;
        _suppressPickers = suppress;
    }

    // ── Rendering ──

    private void Render()
    {
        if (!_loadedOnce || _summary is null) return;
        var visible = _transactions.Where(MatchesKind).Where(MatchesSearch).ToList();
        _rows = visible.Select(BuildRow).ToList();

        var groups = new List<TransactionGroup>();
        foreach (var day in _rows.GroupBy(r => r.Transaction.Date).OrderByDescending(g => g.Key))
        {
            var group = new TransactionGroup
            {
                Title = DayTitle(day.Key),
                Subtitle = DaySubtitle(day.Select(r => r.Transaction)),
            };
            group.AddRange(day);
            groups.Add(group);
        }
        GroupedSource.Source = groups;
        TransactionList.ItemsSource = GroupedSource.View;

        CountText.Text = visible.Count == _transactions.Count
            ? CountLabel(visible.Count)
            : string.Format(Display.S("TransactionsCountFiltered"), visible.Count, _transactions.Count);
        RenderFilterChip(_summary);
        RenderEmptyState(visible.Count);
        RenderSummary(_summary);
        RestoreSelection();
        AutomationProperties.SetAutomationId(CountText, "TransactionsContentReady");
    }

    private void RenderEmptyState(int visibleCount)
    {
        var empty = visibleCount == 0;
        ListEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        TransactionList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty) return;
        // No data at all in the widest window means nothing has synced yet;
        // otherwise the user has simply narrowed the view.
        var nothingSynced = _range == "12m" && _accountId is null && _kind == "all"
            && _category is null && _search.Length == 0 && _transactions.Count == 0;
        ListEmptyIcon.Glyph = nothingSynced ? "" : "";
        ListEmptyTitle.Text = Display.S(nothingSynced ? "TransactionsEmptyTitle" : "TransactionsListEmptyTitle");
        ListEmptyBody.Text = Display.S(nothingSynced ? "TransactionsEmptyBody" : "TransactionsListEmptyBody");
        ListEmptySettingsButton.Visibility = nothingSynced ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderSummary(TransactionsSummaryDto summary)
    {
        var decimals = _session.ShowDecimals;
        var privacy = _session.PrivacyMode;
        PeriodText.Text = PeriodCaption(summary.From, summary.To, summary.TransactionCount);
        MoneyOutText.Text = Money.Format(Money.Parse(summary.CashFlow.Totals.MoneyOut), decimals, privacy);
        MoneyInText.Text = Money.Format(Money.Parse(summary.CashFlow.Totals.MoneyIn), decimals, privacy);
        var net = Money.Parse(summary.CashFlow.Totals.Net);
        NetText.Text = Money.FormatSigned(net, decimals, privacy);
        NetText.Foreground = net < 0
            ? (Brush)Application.Current.Resources["NegativeChangeBrush"]
            : net > 0
                ? (Brush)Application.Current.Resources["PositiveChangeBrush"]
                : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        RenderMonthBars(summary);
        RenderCategories(summary);
    }

    private void RenderMonthBars(TransactionsSummaryDto summary)
    {
        MonthBars.Children.Clear();
        MonthBars.ColumnDefinitions.Clear();
        var months = MonthsInWindow(summary.From, summary.To);
        // Only calendar-aligned windows get per-month bars; a rolling window
        // would show two partial months that look like a drop.
        var show = months.Count > 1 && !_stacked && _range is "3m" or "12m";
        MonthsSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        var byMonth = summary.CashFlow.Months.ToDictionary(m => m.Month, m => Money.Parse(m.MoneyOut));
        var max = months.Select(m => byMonth.GetValueOrDefault(m.Key)).DefaultIfEmpty(0m).Max();
        const double barArea = 60;
        var culture = CultureInfo.CurrentCulture;
        var privacy = _session.PrivacyMode;
        for (var i = 0; i < months.Count; i++)
        {
            var (key, date) = months[i];
            var amount = byMonth.GetValueOrDefault(key);
            MonthBars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var column = new Grid { VerticalAlignment = VerticalAlignment.Bottom, RowSpacing = 4 };
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(barArea) });
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var track = new Grid
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Height = barArea,
            };
            var height = max <= 0 ? 0 : (double)(amount / max) * barArea;
            var bar = new Border
            {
                Height = Math.Max(height, amount > 0 ? 3 : 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(3, 3, 1, 1),
                Background = (Brush)Application.Current.Resources[
                    i == months.Count - 1 ? "ReferenceBlueBrush" : "ReferenceBlueTintPressedBrush"],
            };
            if (amount > 0)
                bar.Background = (Brush)Application.Current.Resources[
                    i == months.Count - 1 ? "ReferenceBlueBrush" : "ChartLineBrush"];
            bar.Opacity = i == months.Count - 1 ? 1 : 0.55;
            track.Children.Add(bar);
            ToolTipService.SetToolTip(column,
                $"{date.ToString("MMMM yyyy", culture)} · {Money.Format(amount, _session.ShowDecimals, privacy)}");
            var label = new TextBlock
            {
                Text = date.ToString("MMM", culture),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.Clip,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            Grid.SetRow(label, 1);
            column.Children.Add(track);
            column.Children.Add(label);
            Grid.SetColumn(column, i);
            MonthBars.Children.Add(column);
        }
    }

    private void RenderFilterChip(TransactionsSummaryDto summary)
    {
        FilterChip.Visibility = _category is null ? Visibility.Collapsed : Visibility.Visible;
        if (_category is null) return;
        var label = Display.SpendingCategoryLabel(_category);
        var match = summary.Spending.Categories.FirstOrDefault(c => c.Category == _category);
        FilterChipText.Text = match is null
            ? label
            : $"{label} · {Money.Format(Money.Parse(match.Amount), _session.ShowDecimals, _session.PrivacyMode)}";
    }

    private void RenderCategories(TransactionsSummaryDto summary)
    {
        CategoryList.Children.Clear();
        var categories = summary.Spending.Categories.Take(6).ToList();
        NoSpendingText.Visibility = categories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (categories.Count == 0) return;
        var total = Money.Parse(summary.Spending.Total);
        var max = categories.Select(c => Money.Parse(c.Amount)).Max();
        var filterTip = Display.S("TransactionsCategoryFilterTip");
        foreach (var category in categories)
        {
            var amount = Money.Parse(category.Amount);
            var share = total <= 0 ? 0 : amount / total;
            var fill = max <= 0 ? 0 : (double)(amount / max);
            var selected = category.Category == _category;
            var row = new StackPanel { Spacing = 5 };
            var head = new Grid { ColumnSpacing = 8 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = Display.SpendingCategoryLabel(category.Category);
            head.Children.Add(new TextBlock
            {
                Text = label,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 13,
                FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            });
            var value = new TextBlock
            {
                Text = _session.PrivacyMode
                    ? Money.PrivacyMask
                    : $"{Money.Format(amount, _session.ShowDecimals)} · {share:P0}",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            Grid.SetColumn(value, 1);
            head.Children.Add(value);
            row.Children.Add(head);
            row.Children.Add(Ui.Meter(fill, "ReferenceBlueBrush"));

            // Each row is a real button: click (or Enter) narrows the list to
            // exactly the rows behind this number; clicking again clears it.
            var button = new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 6, 8, 7),
                Margin = new Thickness(-8, 0, -8, 0),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Background = (Brush)Application.Current.Resources[
                    selected ? "ReferenceBlueTintBrush" : "SubtleFillColorTransparentBrush"],
            };
            var key = category.Category;
            button.Click += (_, _) => ToggleCategory(key);
            ToolTipService.SetToolTip(button,
                $"{string.Format(Display.S("TransactionsCategoryTip"), category.TransactionCount)} · {filterTip}");
            AutomationProperties.SetName(button, $"{label}, {value.Text}. {filterTip}");
            AutomationProperties.SetAutomationId(button, $"TransactionsCategory_{key}");
            CategoryList.Children.Add(button);
        }
    }

    private void ToggleCategory(string category)
    {
        _category = _category == category ? null : category;
        if (_summary is not null)
        {
            RenderCategories(_summary);
            RenderFilterChip(_summary);
        }
        _ = LoadAsync();
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_category is null) return;
        ToggleCategory(_category);
    }

    private TransactionRow BuildRow(BankTransactionDto txn)
    {
        var amount = Money.Parse(txn.Amount);
        var inflow = amount < 0;
        var transfer = Display.IsTransferCategory(txn.Category);
        var visual = CategoryVisual(txn.Category, inflow);
        var amountText = Money.Format(Math.Abs(amount), _session.ShowDecimals, _session.PrivacyMode);
        if (inflow && !_session.PrivacyMode) amountText = "+" + amountText;
        var amountBrush = (Brush)Application.Current.Resources[
            transfer ? "TextFillColorSecondaryBrush"
            : inflow ? "PositiveChangeBrush"
            : "TextFillColorPrimaryBrush"];
        return new TransactionRow
        {
            Transaction = txn,
            Title = Display.TransactionTitle(txn.MerchantName, txn.Name),
            Meta = $"{txn.AccountName} · {Display.SpendingCategoryLabel(txn.Category)}",
            AmountText = amountText,
            AmountBrush = amountBrush,
            Glyph = visual.Glyph,
            TintBrush = (Brush)Application.Current.Resources[visual.Tint],
            GlyphBrush = (Brush)Application.Current.Resources[visual.Foreground],
        };
    }

    private sealed record CategoryVisualSpec(string Glyph, string Tint, string Foreground);

    private static CategoryVisualSpec CategoryVisual(string category, bool inflow) => category.ToUpperInvariant() switch
    {
        "INCOME" => new("", "SystemFillColorSuccessBackgroundBrush", "ReferenceGreenBrush"),
        "TRANSFER_IN" or "TRANSFER_OUT" or "TRANSFER" =>
            new("", "ReferenceNeutralTintBrush", "TextFillColorSecondaryBrush"),
        "LOAN_PAYMENTS" => new("", "ReferenceNeutralTintBrush", "TextFillColorSecondaryBrush"),
        "FOOD_AND_DRINK" => new("", "ReferencePurpleTintBrush", "ReferencePurpleBrush"),
        "GENERAL_MERCHANDISE" => new("", "ReferenceBlueTintBrush", "ReferenceBlueTextBrush"),
        "TRANSPORTATION" => new("", "ReferenceTealTintBrush", "ReferenceGreenBrush"),
        "TRAVEL" => new("", "ReferenceTealTintBrush", "ReferenceGreenBrush"),
        "RENT_AND_UTILITIES" => new("", "ReferenceBlueTintBrush", "ReferenceBlueTextBrush"),
        "ENTERTAINMENT" => new("", "ReferencePurpleTintBrush", "ReferencePurpleBrush"),
        "MEDICAL" => new("", "ReferenceRedTintBrush", "ReferenceRedBrush"),
        "PERSONAL_CARE" => new("", "ReferencePurpleTintBrush", "ReferencePurpleBrush"),
        "GENERAL_SERVICES" or "HOME_IMPROVEMENT" => new("", "ReferenceBlueTintBrush", "ReferenceBlueTextBrush"),
        "BANK_FEES" => new("", "ReferenceRedTintBrush", "ReferenceRedBrush"),
        _ when inflow => new("", "SystemFillColorSuccessBackgroundBrush", "ReferenceGreenBrush"),
        _ => new("", "ReferenceNeutralTintBrush", "TextFillColorSecondaryBrush"),
    };

    // ── Filters ──

    private bool MatchesKind(BankTransactionDto txn)
    {
        var amount = Money.Parse(txn.Amount);
        var transfer = Display.IsTransferCategory(txn.Category);
        return _kind switch
        {
            "spending" => amount > 0 && !transfer && !txn.Category.Equals("INCOME", StringComparison.OrdinalIgnoreCase),
            "income" => amount < 0 && !transfer,
            "transfers" => transfer,
            _ => true,
        };
    }

    private bool MatchesSearch(BankTransactionDto txn)
    {
        if (_search.Length == 0) return true;
        return Contains(txn.MerchantName) || Contains(txn.Name) || Contains(txn.AccountName)
            || Contains(Display.SpendingCategoryLabel(txn.Category)) || Contains(txn.Amount);

        bool Contains(string? value) =>
            value is not null && value.Contains(_search, StringComparison.CurrentCultureIgnoreCase);
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _search = sender.Text.Trim();
        Render();
    }

    private void RangeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_suppressPickers) return;
        _range = sender.SelectedItem?.Tag as string ?? "30d";
        if (_ready) _ = LoadAsync();
    }

    private void AccountPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPickers) return;
        _accountId = (AccountPicker.SelectedItem as ComboBoxItem)?.Tag as string;
        if (_ready) _ = LoadAsync();
    }

    private void KindPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPickers) return;
        _kind = (KindPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        if (_ready) Render();
    }

    // ── Selection / detail ──

    private void RestoreSelection()
    {
        var row = _selectedId is null ? null : _rows.FirstOrDefault(r => r.Transaction.Id == _selectedId);
        if (row is null)
        {
            _selectedId = null;
            TransactionList.SelectedItem = null;
            RenderDetail(null);
            return;
        }
        TransactionList.SelectedItem = row;
        RenderDetail(row);
    }

    private void TransactionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = TransactionList.SelectedItem as TransactionRow;
        _selectedId = row?.Transaction.Id;
        RenderDetail(row);
    }

    // In the stacked layout there is no detail rail, so an explicit click (or
    // Enter on a focused row) opens the details beside the row. Selection that
    // merely follows keyboard focus must not pop a flyout.
    private void TransactionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_stacked && e.ClickedItem is TransactionRow row) ShowDetailFlyout(row);
    }

    private void RenderDetail(TransactionRow? row)
    {
        DetailContent.Children.Clear();
        DetailEmptyText.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        DetailContent.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is null) return;
        DetailContent.Children.Add(BuildDetailContent(row));
    }

    private void ShowDetailFlyout(TransactionRow row)
    {
        var flyout = new Flyout
        {
            Content = new StackPanel { Width = 320, Children = { BuildDetailContent(row) } },
            Placement = FlyoutPlacementMode.Bottom,
        };
        if (TransactionList.ContainerFromItem(row) is FrameworkElement anchor)
            flyout.ShowAt(anchor);
    }

    private FrameworkElement BuildDetailContent(TransactionRow row)
    {
        var txn = row.Transaction;
        var panel = new StackPanel { Spacing = 12 };

        var top = new Grid { ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.Children.Add(new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            Background = row.TintBrush,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon { Glyph = row.Glyph, FontSize = 18, Foreground = row.GlyphBrush },
        });
        var names = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock
        {
            Text = row.Title,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        names.Children.Add(new TextBlock
        {
            Text = row.AmountText,
            Style = (Style)Application.Current.Resources["StatAmountStyle"],
            Foreground = row.AmountBrush,
        });
        Grid.SetColumn(names, 1);
        top.Children.Add(names);
        panel.Children.Add(top);

        var facts = new StackPanel { Spacing = 6 };
        facts.Children.Add(FactRow(Display.S("TransactionsDetailDate"), LongDate(txn.Date)));
        facts.Children.Add(FactRow(Display.S("TransactionsDetailAccount"), txn.AccountName));
        facts.Children.Add(FactRow(Display.S("TransactionsDetailCategory"), Display.SpendingCategoryLabel(txn.Category)));
        if (!string.IsNullOrWhiteSpace(txn.MerchantName)
            && !string.Equals(txn.MerchantName, txn.Name, StringComparison.OrdinalIgnoreCase))
            facts.Children.Add(FactRow(Display.S("TransactionsDetailRawName"), txn.Name));
        if (txn.TripOrigin is not null && txn.TripDestination is not null)
            facts.Children.Add(FactRow(Display.S("TransactionsDetailTrip"), $"{txn.TripOrigin} → {txn.TripDestination}"));
        panel.Children.Add(facts);

        var viewAccount = new Button { Content = Display.S("TransactionsViewAccount") };
        viewAccount.Click += (_, _) => App.Window?.NavigateToAccounts(txn.AccountId);
        panel.Children.Add(viewAccount);
        return panel;
    }

    private static Grid FactRow(string label, string value)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"],
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        });
        var text = new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    // ── Layout ──

    private void TransactionsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = PageLayoutMetrics.ShouldStack(e.NewSize.Width, StackedLayoutWidth);
        var toolbarCompact = PageLayoutMetrics.ShouldStack(e.NewSize.Width, CompactToolbarWidth);
        if (stacked == _stacked && toolbarCompact == _toolbarCompact) return;
        _stacked = stacked;
        _toolbarCompact = toolbarCompact;
        ApplyResponsiveLayout();
        if (_summary is not null) RenderMonthBars(_summary);
    }

    private void ApplyResponsiveLayout()
    {
        if (_stacked)
        {
            RailColumn.Width = new GridLength(0);
            ListColumn.MinWidth = 0;
            Grid.SetColumn(RailScroller, 0);
            Grid.SetRow(RailScroller, 0);
            Grid.SetRowSpan(RailScroller, 1);
            Grid.SetRow(ListCard, 1);
            Grid.SetRowSpan(ListCard, 1);
            // Above the list the rail is a strip: totals only, so the list
            // keeps most of the height. Details open beside the row instead.
            RailScroller.VerticalScrollMode = ScrollMode.Disabled;
            CategorySection.Visibility = Visibility.Collapsed;
            DetailCard.Visibility = Visibility.Collapsed;
        }
        else
        {
            RailColumn.Width = new GridLength(352);
            ListColumn.MinWidth = 400;
            Grid.SetColumn(RailScroller, 1);
            Grid.SetRow(RailScroller, 0);
            Grid.SetRowSpan(RailScroller, 2);
            Grid.SetRow(ListCard, 0);
            Grid.SetRowSpan(ListCard, 2);
            RailScroller.VerticalScrollMode = ScrollMode.Auto;
            CategorySection.Visibility = Visibility.Visible;
            DetailCard.Visibility = Visibility.Visible;
        }

        if (_toolbarCompact)
        {
            SearchColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(RangeScroller, 1);
            Grid.SetColumn(RangeScroller, 0);
            Grid.SetColumnSpan(RangeScroller, 5);
            RangeScroller.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            SearchColumn.Width = new GridLength(320);
            Grid.SetRow(RangeScroller, 0);
            Grid.SetColumn(RangeScroller, 4);
            Grid.SetColumnSpan(RangeScroller, 1);
            RangeScroller.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateToSettings();

    // ── Formatting ──

    private static string Ymd(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime ParseYmd(string ymd) =>
        DateTime.TryParseExact(ymd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : DateTime.MinValue;

    private static string LongDate(string ymd)
    {
        var date = ParseYmd(ymd);
        return date == DateTime.MinValue ? ymd : date.ToString("D", CultureInfo.CurrentCulture);
    }

    private static string DayTitle(string ymd)
    {
        var date = ParseYmd(ymd);
        if (date == DateTime.MinValue) return ymd;
        var today = DateTime.Today;
        if (date == today) return Display.S("TransactionsDateToday");
        if (date == today.AddDays(-1)) return Display.S("TransactionsDateYesterday");
        var culture = CultureInfo.CurrentCulture;
        return date.Year == today.Year
            ? date.ToString("dddd, MMM d", culture)
            : date.ToString("MMM d, yyyy", culture);
    }

    private string DaySubtitle(IEnumerable<BankTransactionDto> rows)
    {
        // Subtotal of the rows shown for that day, so it always agrees with
        // what the reader can see beneath the header.
        decimal outSum = 0, inSum = 0;
        foreach (var txn in rows)
        {
            var amount = Money.Parse(txn.Amount);
            if (amount > 0) outSum += amount; else inSum -= amount;
        }
        var parts = new List<string>();
        if (outSum > 0)
            parts.Add(string.Format(Display.S("TransactionsDayOut"),
                Money.Format(outSum, _session.ShowDecimals, _session.PrivacyMode)));
        if (inSum > 0)
            parts.Add(string.Format(Display.S("TransactionsDayIn"),
                Money.Format(inSum, _session.ShowDecimals, _session.PrivacyMode)));
        return string.Join(" · ", parts);
    }

    private static string CountLabel(int count) =>
        count == 1 ? Display.S("TransactionsCountOne") : string.Format(Display.S("TransactionsCount"), count);

    private static string PeriodCaption(string from, string to, int count)
    {
        var f = ParseYmd(from);
        var t = ParseYmd(to);
        var culture = CultureInfo.CurrentCulture;
        var wholeMonths = f.Day == 1 && t.Day == DateTime.DaysInMonth(t.Year, t.Month);
        string range;
        if (!wholeMonths)
            range = f.Year == t.Year
                ? $"{f.ToString("MMM d", culture)} – {t.ToString("MMM d, yyyy", culture)}"
                : $"{f.ToString("MMM d, yyyy", culture)} – {t.ToString("MMM d, yyyy", culture)}";
        else if (f.Year == t.Year && f.Month == t.Month)
            range = f.ToString("MMMM yyyy", culture);
        else if (f.Year == t.Year)
            range = $"{f.ToString("MMM", culture)} – {t.ToString("MMM yyyy", culture)}";
        else
            range = $"{f.ToString("MMM yyyy", culture)} – {t.ToString("MMM yyyy", culture)}";
        return $"{range} · {CountLabel(count)}";
    }

    private static List<(string Key, DateTime Date)> MonthsInWindow(string from, string to)
    {
        var f = ParseYmd(from);
        var t = ParseYmd(to);
        var months = new List<(string, DateTime)>();
        if (f == DateTime.MinValue || t == DateTime.MinValue) return months;
        var cursor = new DateTime(f.Year, f.Month, 1);
        var end = new DateTime(t.Year, t.Month, 1);
        while (cursor <= end && months.Count < 24)
        {
            months.Add((cursor.ToString("yyyy-MM", CultureInfo.InvariantCulture), cursor));
            cursor = cursor.AddMonths(1);
        }
        return months;
    }
}
