using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System.Globalization;
using AssetScope.App.Controls;
using AssetScope.App.Services;
using AssetScope.Core;
using AssetScope.Core.Api;
using AssetScope.Core.Diagnostics;

namespace AssetScope.App.Pages;

public sealed partial class InvestmentsPage : Page
{
    private const double StackedBreakpoint = 1050;
    /// <summary>Smallest list row that still shows several trades at once.</summary>
    private const double ListRowFloor = 200;
    /// <summary>
    /// Page height below which the summary row, a usable list row and the page
    /// chrome stop fitting together. Measured from the content: roughly 250px of
    /// summary cards, <see cref="ListRowFloor"/> of list, and ~150px of header,
    /// row spacing and page padding.
    /// </summary>
    private const double FittedHeightFloor = 600;
    private readonly AppSession _session = AppSession.Current;
    private InvestmentOverviewDto? _overview;
    private InvestmentRealizedResponseDto? _realized;
    private CancellationTokenSource? _loadCts;
    // Cached ledgers for the Activity / Orders views; reset on every reload.
    private string _holdingsView = "holdings";
    private InvestmentActivitiesResponseDto? _activities;
    private InvestmentOrdersResponseDto? _orders;

    public InvestmentsPage()
    {
        InitializeComponent();
        Header.Title = Display.S("InvestmentsTitle");
        Header.Subtitle = Display.S("InvestmentsSubtitle");
        LedgerNoteText.Text = Display.S("InvestmentActivityReadOnly");
        HoldingsViewBar.SelectedItem = HoldingsViewItem;
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width, e.NewSize.Height);
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
        AutomationProperties.SetAutomationId(TotalValueText, "InvestmentsContentLoading");
        AutomationProperties.SetAutomationId(InvestmentsEmptyTitleText, "InvestmentsEmptyLoading");
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        try
        {
            _overview = await _session.Api.GetInvestmentOverviewAsync("1y", _loadCts.Token);
            _activities = null;
            _orders = null;
            // Realized P/L is a separate ledger over cached activity. A
            // brokerage that reports holdings but no activity history is
            // ordinary, so its absence must not blank the whole page.
            try
            {
                _realized = await _session.Api.GetInvestmentRealizedAsync(ct: _loadCts.Token);
            }
            catch (ApiException ex)
            {
                _realized = null;
                AppLog.Error("Investments", "Realized profit and loss could not be loaded.", ex);
            }
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

    private void Render()
    {
        if (_overview is null) return;
        var overview = _overview;
        var empty = overview.Accounts.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        InvestmentContent.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetAutomationId(
            empty ? InvestmentsEmptyTitleText : TotalValueText, "InvestmentsContentReady");
        if (empty) return;

        var primary = overview.Accounts[0];
        Header.Subtitle = string.IsNullOrWhiteSpace(primary.InstitutionName)
            ? Display.S("InvestmentsSubtitle")
            : string.Format(Display.S("InvestmentsSubtitleForInstitution"), primary.InstitutionName);
        PortfolioLogoHost.Children.Clear();
        PortfolioLogoHost.Children.Add(new InstitutionLogo(primary.InstitutionName, "investment", 36));
        PortfolioNameText.Text = overview.Accounts.Count == 1 ? primary.Name : Display.S("InvestmentsAllAccounts");
        TotalValueText.Text = _session.Fmt(overview.TotalValue.Amount);
        // Only cost-basis P/L is presented as gain/loss. A balance increase can
        // be caused by deposits and is never labeled as investment return.
        SetSignedChange(TotalGainText, overview.TotalGainLoss);

        var cashSlice = overview.Allocation.FirstOrDefault(slice => slice.Bucket == "Cash");
        CashText.Text = _session.Fmt(cashSlice?.Amount ?? "0");
        CashShareText.Text = string.Format(Display.S("InvestmentsShare"),
            cashSlice?.Share is { } share
                ? decimal.Parse(share, CultureInfo.InvariantCulture) * 100
                : 0m);

        var points = overview.Performance
            .Select(point => DateOnly.TryParse(point.Date, out var date)
                ? (Date: date, Value: Money.Parse(point.Value))
                : default)
            .Where(point => point != default)
            .ToList();
        PerformanceChart.SetSeries(points.Count >= 2
            ? [new ChartSeriesData(Display.S("Series_investmentPortfolio"), points, ColorHelper.FromArgb(255, 18, 103, 244), true)]
            : []);

        RenderAllocation(overview);
        _ = RenderHoldingsViewAsync();
        RenderRealized();
    }


    // ── Holdings / Activity / Orders views ──

    private void HoldingsViewBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _holdingsView = sender.SelectedItem?.Tag as string ?? "holdings";
        _ = RenderHoldingsViewAsync();
    }

    private async Task RenderHoldingsViewAsync()
    {
        var ledger = _holdingsView != "holdings";
        HoldingsHeaderGrid.Visibility = ledger ? Visibility.Collapsed : Visibility.Visible;
        LedgerNoteText.Visibility = ledger ? Visibility.Visible : Visibility.Collapsed;
        if (!ledger)
        {
            if (_overview is not null) RenderHoldings(_overview);
            return;
        }
        if (_session.Api is null) return;
        HoldingsRows.Children.Clear();
        try
        {
            if (_holdingsView == "activity")
            {
                _activities ??= await _session.Api.GetInvestmentActivitiesAsync(limit: 100);
                if (_holdingsView != "activity") return;
                HoldingsRows.Children.Add(ActivityList(_activities.Activities));
            }
            else
            {
                _orders ??= await _session.Api.GetInvestmentOrdersAsync(limit: 100);
                if (_holdingsView != "orders") return;
                HoldingsRows.Children.Add(OrderList(_orders.Orders));
            }
        }
        catch (ApiException ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }

    private void SetSignedChange(TextBlock textBlock, InvestmentChangeDto change)
    {
        var value = Money.Parse(change.Amount);
        var ratio = decimal.TryParse(
            change.Ratio,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : (decimal?)null;
        textBlock.Text = ratio is null
            ? _session.FmtSigned(change.Amount)
            : $"{(value >= 0 ? "▲" : "▼")} {_session.FmtSigned(change.Amount)} ({Math.Abs(ratio.Value) * 100:0.00}%)";
        textBlock.Foreground = value == 0
            ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            : (Brush)Application.Current.Resources[value < 0 ? "ReferenceRedBrush" : "ReferenceGreenBrush"];
    }

    private void RenderAllocation(InvestmentOverviewDto overview)
    {
        AllocationLegend.Children.Clear();
        var accountFallback = overview.Positions.Count == 0 && overview.Accounts.Count > 0;
        var allocationItems = accountFallback
            ? overview.Accounts
                .Where(account => Money.Parse(account.Balance.Amount) > 0)
                .Select(account => (
                    Label: account.Name,
                    Amount: Money.Parse(account.Balance.Amount),
                    Share: Money.Parse(overview.TotalValue.Amount) == 0
                        ? (decimal?)null
                        : Money.Parse(account.Balance.Amount) / Money.Parse(overview.TotalValue.Amount)))
                .ToList()
            : overview.Allocation
                .Select(slice => (
                    Label: BucketName(slice.Bucket),
                    Amount: Money.Parse(slice.Amount),
                    Share: slice.Share is null
                        ? (decimal?)null
                        : decimal.Parse(slice.Share, CultureInfo.InvariantCulture)))
                .ToList();
        var slices = new List<DonutSliceData>();
        for (var i = 0; i < allocationItems.Count; i++)
        {
            var slice = allocationItems[i];
            var brush = accountFallback
                ? Display.PortfolioBucketBrush(i switch
                {
                    0 => "ETFs",
                    1 => "Stocks",
                    2 => "Cash",
                    3 => "MutualFunds",
                    4 => "Crypto",
                    _ => "Other",
                })
                : Display.PortfolioBucketBrush(overview.Allocation[i].Bucket);
            slices.Add(new DonutSliceData((double)slice.Amount, brush, slice.Label));
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = brush, VerticalAlignment = VerticalAlignment.Center });
            var labels = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock { Text = slice.Label, TextTrimming = TextTrimming.CharacterEllipsis });
            labels.Children.Add(new TextBlock
            {
                Text = _session.Fmt(slice.Amount.ToString(CultureInfo.InvariantCulture)),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            Grid.SetColumn(labels, 1); row.Children.Add(labels);
            var share = new TextBlock
            {
                Text = slice.Share is null ? "—" : $"{slice.Share.Value * 100:0.0}%",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            Grid.SetColumn(share, 2); row.Children.Add(share);
            AllocationLegend.Children.Add(row);
        }
        AllocationDonut.SetData(slices, Display.S("InvestmentsDonutTotal"), _session.Fmt(overview.TotalValue.Amount));
    }

    private void RenderHoldings(InvestmentOverviewDto overview)
    {
        HoldingsRows.Children.Clear();
        var total = Math.Max(1m, Money.Parse(overview.TotalValue.Amount));
        if (overview.Positions.Count > 0)
        {
            // Cash-equivalent instruments and broker cash are reconciled into
            // one authoritative Cash slice by the service. Rendering those
            // raw positions again would duplicate the same dollars in the
            // holdings table and could even put cash in “top securities”.
            var securityPositions = overview.Positions
                .Where(position => !position.CashEquivalent)
                .ToList();
            foreach (var position in securityPositions)
                HoldingsRows.Children.Add(CreateHoldingRow(position, total));
            var cash = overview.Allocation.FirstOrDefault(slice => slice.Bucket == "Cash");
            if (cash is not null && Money.Parse(cash.Amount) > 0)
                HoldingsRows.Children.Add(CreateAllocationHoldingRow(cash, total));
            return;
        }

        foreach (var account in overview.Accounts.Where(account => Money.Parse(account.Balance.Amount) > 0))
            HoldingsRows.Children.Add(CreateInvestmentAccountRow(account, total));
    }

    /// <summary>
    /// Realized profit and loss: summary figures plus the trades that produced
    /// them, newest first. Buys appear because they establish the cost basis a
    /// later sale is measured against, but only sales realize anything.
    /// </summary>
    private void RenderRealized()
    {
        RealizedRows.Children.Clear();
        var realized = _realized;
        if (realized is null || realized.Trades.Count == 0)
        {
            RealizedTotalText.Text = _session.Fmt("0");
            RealizedTotalText.Foreground =
                (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            RealizedYtdText.Text = _session.Fmt("0");
            RealizedMtdText.Text = _session.Fmt("0");
            RealizedBasisWarning.Visibility = Visibility.Collapsed;
            RealizedEmptyText.Visibility = Visibility.Visible;
            return;
        }

        RealizedEmptyText.Visibility = Visibility.Collapsed;
        SetSignedAmount(RealizedTotalText, realized.Summary.Total);
        SetSignedAmount(RealizedYtdText, realized.Summary.YearToDate);
        SetSignedAmount(RealizedMtdText, realized.Summary.MonthToDate);

        // Cached brokerage history is finite. Saying so is the honest
        // alternative to quietly reporting a total that omits those shares.
        if (realized.Summary.TradesMissingBasis > 0)
        {
            RealizedBasisWarning.Text = string.Format(
                Display.S("InvestmentsRealizedMissingBasis"), realized.Summary.TradesMissingBasis);
            RealizedBasisWarning.Visibility = Visibility.Visible;
        }
        else
        {
            RealizedBasisWarning.Visibility = Visibility.Collapsed;
        }

        foreach (var trade in realized.Trades)
            RealizedRows.Children.Add(CreateRealizedRow(trade));
    }

    private void SetSignedAmount(TextBlock textBlock, string amount)
    {
        var value = Money.Parse(amount);
        textBlock.Text = _session.FmtSigned(amount);
        textBlock.Foreground = value == 0
            ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            : (Brush)Application.Current.Resources[value < 0 ? "ReferenceRedBrush" : "ReferenceGreenBrush"];
    }

    private UIElement CreateRealizedRow(InvestmentRealizedTradeDto trade)
    {
        var isSell = string.Equals(trade.Action, "sell", StringComparison.OrdinalIgnoreCase);
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 9, 0, 9) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            row, $"InvestmentRealizedTrade_{trade.Id}");
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var actionLabel = Display.S(isSell ? "InvestmentsRealizedSell" : "InvestmentsRealizedBuy");
        var date = DateTime.TryParse(
            trade.TradeDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToString("d", CultureInfo.CurrentCulture)
            : "—";
        var units = decimal.TryParse(trade.Units, NumberStyles.Number, CultureInfo.InvariantCulture, out var u)
            ? u.ToString("0.####", CultureInfo.CurrentCulture)
            : trade.Units;

        var labels = new StackPanel();
        labels.Children.Add(new TextBlock
        {
            Text = $"{actionLabel} {trade.Symbol}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        labels.Children.Add(new TextBlock
        {
            Text = $"{date} · {units} @ {_session.Fmt(trade.Price ?? "0")}",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        row.Children.Add(labels);

        var values = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        if (isSell && trade.Realized is not null)
        {
            var realizedText = new TextBlock
            {
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextAlignment = TextAlignment.Right,
            };
            SetSignedAmount(realizedText, trade.Realized);
            values.Children.Add(realizedText);
            values.Children.Add(new TextBlock
            {
                // A realized figure is meaningless without the basis it was
                // measured against, so the row always shows both.
                Text = string.Format(Display.S("InvestmentsRealizedCostBasis"),
                    _session.Fmt(trade.CostBasis ?? "0")),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextAlignment = TextAlignment.Right,
            });
            if (!trade.BasisComplete)
                values.Children.Add(new TextBlock
                {
                    Text = Display.S("InvestmentsRealizedPartialBasis"),
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    TextAlignment = TextAlignment.Right,
                });
        }
        else
        {
            values.Children.Add(new TextBlock
            {
                Text = _session.Fmt(trade.Amount),
                TextAlignment = TextAlignment.Right,
            });
            values.Children.Add(new TextBlock
            {
                Text = Display.S("InvestmentsRealizedOpensPosition"),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextAlignment = TextAlignment.Right,
            });
        }
        Grid.SetColumn(values, 1); row.Children.Add(values);

        var wrapper = new StackPanel();
        wrapper.Children.Add(row);
        wrapper.Children.Add(new Rectangle
        {
            Height = 1,
            Fill = (Brush)Application.Current.Resources["ReferenceCardStrokeBrush"],
        });
        return wrapper;
    }

    private UIElement CreateInvestmentAccountRow(InvestmentAccountDto account, decimal total)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 9, 4, 9) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            grid, $"InvestmentAccountBalance_{account.Id}");
        AddHoldingColumns(grid);
        var identity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        identity.Children.Add(new InstitutionLogo(account.InstitutionName ?? account.Name, "investment", 30));
        identity.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = account.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock
                {
                    Text = account.InstitutionName ?? Display.S("InvestmentsAccountBalanceType"),
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                },
            },
        });
        grid.Children.Add(identity);
        AddCell(grid, 1, Display.S("InvestmentsAccountBalanceType"), false);
        AddCell(grid, 2, "—", true);
        AddCell(grid, 3, "—", true);
        AddCell(grid, 4, _session.Fmt(account.Balance.Amount), true, true);
        AddCell(grid, 5, "—", true);
        AddCell(grid, 6, $"{Money.Parse(account.Balance.Amount) / total * 100:0.0}%", true);
        var wrapper = new StackPanel();
        wrapper.Children.Add(grid);
        wrapper.Children.Add(new Rectangle { Height = 1, Fill = (Brush)Application.Current.Resources["ReferenceCardStrokeBrush"] });
        return wrapper;
    }


    private UIElement CreateAllocationHoldingRow(InvestmentAllocationDto allocation, decimal total)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 11, 4, 11) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            grid, $"InvestmentHolding_{allocation.Bucket}");
        AddHoldingColumns(grid);

        var label = BucketName(allocation.Bucket);
        var symbol = allocation.Bucket == "Cash" ? "USD" : "?";
        var identity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        identity.Children.Add(new SecurityLogo(symbol, label, 30));
        identity.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = label },
                new TextBlock
                {
                    Text = allocation.Bucket == "Cash" ? "USD" : Display.S("InvestmentsProviderBreakdownUnavailable"),
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                },
            },
        });
        grid.Children.Add(identity);
        AddCell(grid, 1, allocation.Bucket == "Cash" ? label : Display.S("Instrument_other"), false);
        AddCell(grid, 2, "—", true);
        AddCell(grid, 3, _session.Fmt("1"), true);
        AddCell(grid, 4, _session.Fmt(allocation.Amount), true, true);
        AddCell(grid, 5, "—", true);
        AddCell(grid, 6, $"{Money.Parse(allocation.Amount) / total * 100:0.0}%", true);

        var wrapper = new StackPanel();
        wrapper.Children.Add(grid);
        return wrapper;
    }

    private UIElement CreateHoldingRow(InvestmentPositionDto position, decimal total)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 11, 4, 11) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            grid, $"InvestmentHolding_{position.Id}");
        AddHoldingColumns(grid);

        var identity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        identity.Children.Add(new SecurityLogo(position.Symbol, position.Name, 30));
        identity.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = position.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = position.Symbol, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] },
            },
        });
        grid.Children.Add(identity);
        AddCell(grid, 1, BucketName(position.InstrumentType), false);
        AddCell(grid, 2, position.Units ?? "—", true);
        AddCell(grid, 3, position.CurrentPrice is null ? "—" : _session.Fmt(position.CurrentPrice), true);
        AddCell(grid, 4, _session.Fmt(position.Value), true, true);

        var changePanel = HoldingChange(position);
        Grid.SetColumn(changePanel, 5); grid.Children.Add(changePanel);
        AddCell(grid, 6, $"{Money.Parse(position.Value) / total * 100:0.0}%", true);

        var wrapper = new StackPanel();
        wrapper.Children.Add(grid);
        wrapper.Children.Add(new Rectangle { Height = 1, Fill = (Brush)Application.Current.Resources["ReferenceCardStrokeBrush"] });
        return wrapper;
    }


    private StackPanel HoldingChange(InvestmentPositionDto position)
    {
        var amount = Money.Parse(position.GainLoss ?? "0");
        var brush = (Brush)Application.Current.Resources[
            amount < 0 ? "ReferenceRedBrush" : "ReferenceGreenBrush"];
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 1,
        };
        panel.Children.Add(new TextBlock
        {
            Text = $"{(amount >= 0 ? "▲" : "▼")} {_session.FmtSigned(position.GainLoss ?? "0")}",
            Foreground = brush,
            FontSize = 12,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var ratioText = decimal.TryParse(
            position.GainLossRatio,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var ratio)
            ? $"({Math.Abs(ratio) * 100:0.00}%)"
            : "—";
        panel.Children.Add(new TextBlock
        {
            Text = ratioText,
            Foreground = brush,
            FontSize = 12,
            TextAlignment = TextAlignment.Right,
        });
        return panel;
    }

    // Shared with the XAML header. The gain column needs enough room for a
    // signed currency value and percentage without bleeding into allocation.
    private static readonly double[] HoldingColumnWidths = [2.0, .65, .72, .78, 1.0, 1.3, .7];

    private static void AddHoldingColumns(Grid grid)
    {
        foreach (var width in HoldingColumnWidths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
    }

    private static void AddCell(Grid grid, int column, string value, bool right, bool bold = false)
    {
        var text = new TextBlock
        {
            Text = value,
            TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        };
        Grid.SetColumn(text, column);
        grid.Children.Add(text);
    }

    private static string BucketName(string bucket) => Display.S(Display.NormalizePortfolioBucket(bucket) switch
    {
        "Stocks" => "Instrument_stock",
        "ETFs" => "Instrument_etf",
        "MutualFunds" => "Instrument_mutualFund",
        "Crypto" => "Instrument_crypto",
        "Cash" => "Instrument_cash",
        "Unclassified" => "Instrument_unclassified",
        _ => "Instrument_other",
    });

    private UIElement ActivityList(IReadOnlyList<InvestmentActivityDto> activities)
    {
        if (activities.Count == 0) return EmptySnapshotMessage("InvestmentActivityEmpty");
        var rows = new StackPanel();
        foreach (var activity in activities)
        {
            var title = string.IsNullOrWhiteSpace(activity.Symbol)
                ? activity.Type
                : $"{activity.Type} · {activity.Symbol}";
            var amount = activity.Amount is null ? null : _session.Fmt(activity.Amount);
            rows.Children.Add(SnapshotRow(
                title,
                activity.Description ?? activity.AccountName,
                amount,
                SnapshotDate(activity.TradeDate ?? activity.SettlementDate)));
        }
        return rows;
    }

    private UIElement OrderList(IReadOnlyList<InvestmentOrderDto> orders)
    {
        if (orders.Count == 0) return EmptySnapshotMessage("InvestmentOrdersEmpty");
        var rows = new StackPanel();
        foreach (var order in orders)
        {
            var symbol = string.IsNullOrWhiteSpace(order.Symbol) ? "—" : order.Symbol;
            var title = string.IsNullOrWhiteSpace(order.Action)
                ? $"{symbol} · {order.Status}"
                : $"{order.Action} {symbol} · {order.Status}";
            var quantity = order.FilledQuantity ?? order.TotalQuantity;
            var execution = order.ExecutionPrice is null ? null : _session.Fmt(order.ExecutionPrice);
            var detail = string.Join(" · ", new[]
            {
                order.AccountName,
                order.OrderType,
                quantity is null ? null : $"{Display.S("InvestmentsColUnits")}: {quantity}",
            }.Where(value => !string.IsNullOrWhiteSpace(value))!);
            rows.Children.Add(SnapshotRow(
                title,
                detail,
                execution,
                SnapshotDate(order.TimeExecuted ?? order.TimePlaced ?? order.TimeUpdated)));
        }
        return rows;
    }

    private static UIElement EmptySnapshotMessage(string resourceKey) => new TextBlock
    {
        Text = Display.S(resourceKey),
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(20, 80, 20, 20),
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static UIElement SnapshotRow(string title, string detail, string? value, string? date)
    {
        var grid = new Grid { ColumnSpacing = 16, Padding = new Thickness(12, 11, 12, 11) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        left.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        grid.Children.Add(left);
        var right = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        if (!string.IsNullOrWhiteSpace(value))
            right.Children.Add(new TextBlock { Text = value, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Right });
        if (!string.IsNullOrWhiteSpace(date))
            right.Children.Add(new TextBlock
            {
                Text = date,
                FontSize = 12,
                TextAlignment = TextAlignment.Right,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        var wrapper = new StackPanel();
        wrapper.Children.Add(grid);
        wrapper.Children.Add(new Rectangle
        {
            Height = 1,
            Fill = (Brush)Application.Current.Resources["ReferenceCardStrokeBrush"],
        });
        return wrapper;
    }

    private static string? SnapshotDate(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToLocalTime().ToString("g")
            : value;

    private void ApplyResponsiveLayout(double width, double height)
    {
        var stacked = PageLayoutMetrics.ShouldStack(width, StackedBreakpoint);
        // Fitting the window is only possible while the summary cards, the
        // minimum useful list and the page chrome all have room. Below that the
        // choice is between clipping rows the user cannot reach and letting the
        // page scroll; scrolling is the honest one.
        var fits = !stacked && height >= FittedHeightFloor;
        MainColumn.MinWidth = stacked ? 0 : 620;
        SideColumn.MinWidth = stacked ? 0 : 300;
        MainColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(2.05, GridUnitType.Star);
        SideColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumnSpan(PortfolioCard, stacked ? 2 : 1);
        Grid.SetRow(AllocationCard, stacked ? 1 : 0);
        Grid.SetColumn(AllocationCard, stacked ? 0 : 1);
        Grid.SetColumnSpan(AllocationCard, stacked ? 2 : 1);
        Grid.SetRow(HoldingsCard, stacked ? 2 : 1);
        Grid.SetColumnSpan(HoldingsCard, stacked ? 2 : 1);
        Grid.SetRow(RealizedCard, stacked ? 3 : 1);
        Grid.SetColumn(RealizedCard, stacked ? 0 : 1);
        Grid.SetColumnSpan(RealizedCard, stacked ? 2 : 1);

        // When it fits, the page owns the viewport: the summary row takes its
        // natural height, the list row absorbs every remaining pixel so both
        // list cards end on the same baseline, and growth goes into the lists'
        // own scrollbars instead of into the page. Otherwise the cards size to
        // their content and the page scrolls.
        //
        // The explicit height is what makes the star rows mean anything. A
        // ScrollViewer measures its content against infinite height, so a star
        // row inside one resolves to its natural size and silently overflows the
        // window; pinning the grid to the viewport gives the rows a real budget
        // to divide. NaN restores automatic sizing for the scrolling case.
        PageRoot.Height = fits ? height : double.NaN;
        ContentRow.Height = fits ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        ListRow.Height = fits ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        ListRow.MinHeight = fits ? ListRowFloor : 0;
        PageScroll.VerticalScrollMode = fits ? ScrollMode.Disabled : ScrollMode.Auto;
        PageScroll.VerticalScrollBarVisibility = fits
            ? ScrollBarVisibility.Hidden
            : ScrollBarVisibility.Auto;
        foreach (var scroll in new[] { HoldingsScroll, RealizedScroll })
        {
            scroll.VerticalScrollMode = fits ? ScrollMode.Auto : ScrollMode.Disabled;
            scroll.VerticalScrollBarVisibility = fits
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled;
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) =>
        (App.Window as MainWindow)?.NavigateToSettings();
}
