using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using AssetScope.App.Controls;
using AssetScope.App.Services;
using AssetScope.Core;
using AssetScope.Core.Api;
using AssetScope.Core.Diagnostics;

namespace AssetScope.App.Pages;

public sealed partial class AccountsPage : Page
{
    // Master/detail needs enough room for a readable account table plus the
    // 380-DIP detail rail. Below this width, use the focused list/detail flow
    // instead of compressing seven columns into unreadable fragments.
    private const double CompactWidth = 1120;
    private const double CompactToolbarWidth = 820;

    public static string? PendingSelection;

    private readonly AppSession _session = AppSession.Current;
    private List<AccountSummaryDto> _accounts = [];
    private string _filter = "";
    private string _search = "";
    private string _sort = "default";
    private string? _selectedId;
    private bool _isCompact;
    private bool _isToolbarCompact;
    private bool _showCompactDetail;
    private bool _changingGroupSelection;
    private readonly List<ListView> _accountGroupLists = [];
    private readonly Dictionary<ListViewItem, ListView> _accountItemOwners = [];
    private readonly Dictionary<ListViewItem, Expander> _accountItemGroups = [];
    private readonly HashSet<string> _collapsedInstitutions = new(StringComparer.CurrentCultureIgnoreCase);

    public void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);

    public AccountsPage()
    {
        InitializeComponent();
        FilterBar.SelectedItem = FilterAllItem;
        Header.Title = Display.S("AccountsTitle");
        Header.Subtitle = Display.S("AccountsSubtitle");
        AddAccountLabel.Text = Display.S("AddAccount");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AddAccountButton, Display.S("AddAccount"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SortButton, Display.S("SortLabel"));
        ToolTipService.SetToolTip(SortButton, Display.S("SortLabel"));
        AddSortOption("SortDefault/Content", "default", isChecked: true);
        AddSortOption("SortName/Content", "name");
        AddSortOption("SortBalance/Content", "balance");
        AddSortOption("SortRecent/Content", "recent");
        AddSortOption("SortCategory/Content", "category");
        SortLabel.Text = Display.S("SortDefault/Content");
    }

    private void AddAccountButton_Click(object sender, RoutedEventArgs e) => _ = AddAccountAsync();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _session.DataRefreshed += OnDataRefreshed;
        _session.PropertyChanged += OnSessionPropertyChanged;
        // A compact account detail is a child state of this page, not a
        // top-level navigation destination. Returning through the Accounts
        // navigation item should always land on the account list; an explicit
        // cross-page account selection still opens its requested detail.
        if (PendingSelection is null)
        {
            _showCompactDetail = false;
            ApplyResponsiveContentVisibility();
        }
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
            DispatcherQueue.TryEnqueue(() => { RenderList(); _ = RenderDetailAsync(); });
    }

    public void SelectAccount(string accountId)
    {
        _selectedId = accountId;
        _showCompactDetail = _isCompact;
        var item = FindAccountItem(accountId);
        if (item is not null && _accountItemOwners.TryGetValue(item, out var owner) &&
            !ReferenceEquals(owner.SelectedItem, item))
        {
            // SelectionChanged owns the detail request. Returning here avoids
            // issuing the same API request once directly and once from the
            // selection event.
            _accountItemGroups[item].IsExpanded = true;
            owner.SelectedItem = item;
            return;
        }
        ApplyResponsiveContentVisibility();
        _ = RenderDetailAsync();
    }

    private void AccountsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Base the breakpoint on the space the table actually receives inside
        // the shell padding, not the Frame.
        var contentWidth = PageLayoutMetrics.ContentWidth(e.NewSize.Width);
        var compact = contentWidth < CompactWidth;
        var toolbarCompact = contentWidth < CompactToolbarWidth;
        if (_isCompact == compact && _isToolbarCompact == toolbarCompact) return;
        _isCompact = compact;
        _isToolbarCompact = toolbarCompact;
        if (!compact) _showCompactDetail = false;
        if (!compact && _selectedId is null && _accounts.Count > 0)
            _selectedId = _accounts[0].Id;
        ApplyResponsiveLayout();
        ApplyResponsiveContentVisibility();
        RenderList();
        if (!compact && _selectedId is not null)
        {
            var item = FindAccountItem(_selectedId);
            if (item is not null)
            {
                // RenderList clears the ListView selection, so restoring it
                // raises SelectionChanged and renders the detail exactly once.
                _accountItemGroups[item].IsExpanded = true;
                _accountItemOwners[item].SelectedItem = item;
                return;
            }
        }

        if (DetailBorder.Visibility == Visibility.Visible)
            _ = RenderDetailAsync();
    }

    private void ApplyResponsiveLayout()
    {
        // Padding stays with the shared page shell so the header lines up
        // with every other page.
        SearchColumn.Width = _isToolbarCompact
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(360);
        Grid.SetColumnSpan(SearchBox, _isToolbarCompact ? 2 : 1);
        // Wide: search + actions on one row, filters beneath. Compact: each
        // on its own row so nothing is squeezed.
        Grid.SetRow(ActionsPanel, _isToolbarCompact ? 2 : 0);
        Grid.SetColumn(ActionsPanel, _isToolbarCompact ? 0 : 1);
        Grid.SetColumnSpan(ActionsPanel, _isToolbarCompact ? 2 : 1);
        ActionsPanel.HorizontalAlignment = _isToolbarCompact
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        AccountsColumn.MinWidth = _isCompact ? 0 : 420;
        DetailsColumn.Width = _isCompact ? new GridLength(0) : new GridLength(440);
        Grid.SetColumn(DetailBorder, _isCompact ? 0 : 1);
        AccountListHeader.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyResponsiveContentVisibility()
    {
        if (AccountListBorder is null || DetailBorder is null || ToolbarGrid is null) return;
        var showingDetail = _isCompact && _showCompactDetail;
        AccountListBorder.Visibility = showingDetail ? Visibility.Collapsed : Visibility.Visible;
        DetailBorder.Visibility = !_isCompact || showingDetail ? Visibility.Visible : Visibility.Collapsed;
        ToolbarGrid.Visibility = showingDetail ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadAsync()
    {
        if (_session.Api is null) return;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            AccountList, "AccountsListLoading");
        try
        {
            var manualFilter = _filter == "manual";
            var response = await _session.Api.GetAccountsAsync(
                category: manualFilter || _filter.Length == 0 ? null : _filter,
                search: _search.Length == 0 ? null : _search);
            _accounts = manualFilter
                ? response.Accounts.Where(a => a.Source == "manual").ToList()
                : response.Accounts;
            // Default: attention first, then |balance| descending; alternates per §17.4.
            _accounts = _sort switch
            {
                "name" => _accounts.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
                "balance" => _accounts.OrderByDescending(a => a.Balance is null ? 0m : Math.Abs(Money.Parse(a.Balance.Amount))).ToList(),
                "recent" => _accounts.OrderByDescending(a => a.LastSyncedAt ?? a.Balance?.AsOfDate ?? "").ToList(),
                "category" => _accounts.OrderBy(a => a.Category).ThenBy(a => a.DisplayName).ToList(),
                _ => _accounts
                    .OrderByDescending(a => a.Freshness == "attention" ? 1 : 0)
                    .ThenByDescending(a => a.Balance is null ? 0m : Math.Abs(Money.Parse(a.Balance.Amount)))
                    .ToList(),
            };
            // The account surface is institution-first like the reference UI.
            // Preserve the requested sort inside each institution and preserve
            // first-appearance order between institutions.
            _accounts = _accounts
                .GroupBy(a => a.InstitutionName ?? Display.S("ManualAccountsGroup"), StringComparer.CurrentCultureIgnoreCase)
                .SelectMany(group => group)
                .ToList();
            // Async data can return before the first SizeChanged event. Base
            // default selection on the measured viewport instead of the
            // field's initial value; an unmeasured page is never assumed wide.
            var isMeasuredWideViewport =
                PageLayoutMetrics.ContentWidth(ActualWidth) >= CompactWidth;
            if (_selectedId is null && isMeasuredWideViewport && _accounts.Count > 0)
                _selectedId = _accounts[0].Id;
            RenderList();
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                AccountList, "AccountsListReady");

            if (PendingSelection is not null)
            {
                var pending = PendingSelection;
                PendingSelection = null;
                SelectAccount(pending);
            }
            else if (_selectedId is not null)
            {
                var item = FindAccountItem(_selectedId);
                // On compact layouts, refreshing/filtering the list must not
                // reopen a detail view that the user has backed out of.
                if (item is not null && (isMeasuredWideViewport || _showCompactDetail))
                {
                    _accountItemGroups[item].IsExpanded = true;
                    _accountItemOwners[item].SelectedItem = item;
                }
            }
        }
        catch (ApiException) { /* page-level errors surface on Home; keep cached list */ }
    }

    private void RenderList()
    {
        var privacy = _session.PrivacyMode;
        var decimals = _session.ShowDecimals;
        AccountGroupsPanel.Children.Clear();
        _accountGroupLists.Clear();
        _accountItemOwners.Clear();
        _accountItemGroups.Clear();

        foreach (var group in _accounts.GroupBy(
                     account => account.InstitutionName ?? Display.S("ManualAccountsGroup"),
                     StringComparer.CurrentCultureIgnoreCase))
        {
            var institution = group.Key;
            var firstAccount = group.First();
            var header = new Grid { ColumnSpacing = 10, Padding = new Thickness(6, 5, 6, 5) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.Children.Add(new InstitutionLogo(institution, firstAccount.Category, 28));
            var title = new TextBlock
            {
                Text = institution,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(title, 1);
            header.Children.Add(title);

            var groupList = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            ScrollViewer.SetVerticalScrollMode(groupList, ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(groupList, ScrollBarVisibility.Hidden);
            ScrollViewer.SetHorizontalScrollMode(groupList, ScrollMode.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(groupList, ScrollBarVisibility.Disabled);
            groupList.Resources["ListViewItemBackgroundSelected"] = new SolidColorBrush(Colors.Transparent);
            groupList.Resources["ListViewItemBackgroundSelectedPointerOver"] = new SolidColorBrush(Color.FromArgb(10, 18, 103, 244));
            groupList.Resources["ListViewItemBackgroundSelectedPressed"] = new SolidColorBrush(Color.FromArgb(18, 18, 103, 244));
            groupList.SelectionChanged += AccountGroupList_SelectionChanged;

            var expander = new Expander
            {
                Header = header,
                Content = groupList,
                IsExpanded = !_collapsedInstitutions.Contains(institution),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            expander.Collapsed += (_, _) => _collapsedInstitutions.Add(institution);
            expander.Expanding += (_, _) => _collapsedInstitutions.Remove(institution);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                expander, $"AccountGroup_{institution}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expander, institution);
            AccountGroupsPanel.Children.Add(expander);
            _accountGroupLists.Add(groupList);

            foreach (var account in group)
            {
            var grid = new Grid { ColumnSpacing = _isCompact ? 10 : 12, Padding = new Thickness(8, 7, 8, 7) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // icon
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_isCompact ? 1.6 : 1.7, GridUnitType.Star) }); // account
            if (_isCompact)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // balance
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // freshness
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // overflow
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(.8, GridUnitType.Star) });  // type
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // balance
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // change
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // overflow
            }

            // The group header already carries the institution brand. Account
            // rows use a product/category glyph, except brokerage/retirement
            // rows where the broker mark is the product identity itself.
            var rowInstitution = account.Category is "investment" or "retirement"
                ? account.InstitutionName
                : null;
            var icon = new InstitutionLogo(rowInstitution, account.Category, 34);
            grid.Children.Add(icon);

            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            nameRow.Children.Add(new TextBlock
            {
                Text = account.DisplayName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (account.Excluded)
                nameRow.Children.Add(Ui.Badge(Display.S("ExcludedChip"), "ReferenceNeutralTintBrush", "TextFillColorSecondaryBrush"));
            names.Children.Add(nameRow);
            // Privacy Mode hides account masks (SPEC §14.4).
            var accountMeta = Ui.Caption(Display.CategoryLabel(account.Category)
                + (account.Mask is null || privacy ? "" : $" ····{account.Mask}"));
            accountMeta.Visibility = _isCompact ? Visibility.Visible : Visibility.Collapsed;
            names.Children.Add(accountMeta);
            Grid.SetColumn(names, 1);
            grid.Children.Add(names);

            var balance = new TextBlock
            {
                Text = account.Balance is null ? Display.S("NoBalance") : Money.Format(account.Balance.Amount, decimals, privacy),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetColumn(balance, _isCompact ? 2 : 3);
            grid.Children.Add(balance);

            if (!_isCompact)
            {
                var type = new TextBlock
                {
                    Text = Display.CategoryLabel(account.Category),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                };
                Grid.SetColumn(type, 2);
                grid.Children.Add(type);

                var change = new ChangeBadge { VerticalAlignment = VerticalAlignment.Center };
                change.SetChange(account.Change, showComparisonLabel: false);
                Grid.SetColumn(change, 4);
                grid.Children.Add(change);
            }

            // Row overflow menu (SPEC §17.3).
            var overflow = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 12 },
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(overflow,
                string.Format(Display.S("MoreOptionsFor"), account.DisplayName));
            var menu = new MenuFlyout();
            var excludeItem = new MenuFlyoutItem
            {
                Text = account.Excluded ? Display.S("Included") : Display.S("ExcludedChip"),
            };
            var accountId = account.Id;
            var wasExcluded = account.Excluded;
            excludeItem.Click += async (_, _) =>
            {
                try
                {
                    await _session.Api!.PatchAccountAsync(accountId, new { excluded = !wasExcluded });
                    await LoadAsync();
                    _session.NotifyDataRefreshed();
                }
                catch (ApiException) { }
            };
            menu.Items.Add(excludeItem);
            var nicknameItem = new MenuFlyoutItem { Text = Display.S("EditNickname") };
            var accountRef = account;
            nicknameItem.Click += async (_, _) => await ShowNicknameDialogAsync(accountRef);
            menu.Items.Add(nicknameItem);
            var archiveItem = new MenuFlyoutItem { Text = Display.S("Archive") };
            archiveItem.Click += async (_, _) =>
            {
                try { await _session.Api!.SetArchivedAsync(accountId, true); await LoadAsync(); _session.NotifyDataRefreshed(); }
                catch (ApiException) { }
            };
            menu.Items.Add(archiveItem);
            overflow.Flyout = menu;

            if (_isCompact)
            {
                var freshness = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                var freshnessLabel = Ui.Caption(Display.FreshnessLabel(account.Freshness));
                freshnessLabel.HorizontalAlignment = HorizontalAlignment.Right;
                if (account.Freshness is "attention" or "very_stale")
                    freshnessLabel.Foreground = Ui.Brush("NegativeChangeBrush");
                freshness.Children.Add(freshnessLabel);
                var freshnessTime = Ui.Caption(account.Source == "manual"
                    ? (account.Balance?.AsOfDate ?? "")
                    : Display.RelativeTime(account.LastSyncedAt));
                freshnessTime.HorizontalAlignment = HorizontalAlignment.Right;
                freshnessTime.Foreground = Ui.Brush("TextFillColorTertiaryBrush");
                freshness.Children.Add(freshnessTime);
                Grid.SetColumn(freshness, 3);
                grid.Children.Add(freshness);
            }
            Grid.SetColumn(overflow, _isCompact ? 4 : 5);
            grid.Children.Add(overflow);

            var selectionChrome = new Border
            {
                Child = grid,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                Background = new SolidColorBrush(Colors.Transparent),
                CornerRadius = new CornerRadius(7),
            };
            var item = new ListViewItem
            {
                Content = selectionChrome,
                Tag = account.Id,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 1, 0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, $"Account_{account.Id}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item,
                $"{account.DisplayName}, {Display.CategoryLabel(account.Category)}");
            groupList.Items.Add(item);
            _accountItemOwners[item] = groupList;
            _accountItemGroups[item] = expander;
            }
        }

        var selectedItem = _selectedId is null ? null : FindAccountItem(_selectedId);
        if (selectedItem is not null)
        {
            _accountItemGroups[selectedItem].IsExpanded = true;
            _accountItemOwners[selectedItem].SelectedItem = selectedItem;
        }
    }

    private ListViewItem? FindAccountItem(string accountId) =>
        _accountItemOwners.Keys
            .FirstOrDefault(item => item.Tag as string == accountId);

    private void AccountGroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingGroupSelection || sender is not ListView selectedList ||
            selectedList.SelectedItem is not ListViewItem { Tag: string id }) return;

        _changingGroupSelection = true;
        foreach (var list in _accountGroupLists)
            if (!ReferenceEquals(list, selectedList)) list.SelectedItem = null;
        _changingGroupSelection = false;

        UpdateAccountSelectionChrome();
        _selectedId = id;
        _showCompactDetail = _isCompact;
        ApplyResponsiveContentVisibility();
        _ = RenderDetailAsync();
    }

    private void UpdateAccountSelectionChrome()
    {
        var selectedBorder = (Brush)Application.Current.Resources["ReferenceBlueBrush"];
        var selectedBackground = (Brush)Application.Current.Resources["ReferenceBlueTintBrush"];
        foreach (var item in _accountItemOwners.Keys)
        {
            if (item.Tag is not string || item.Content is not Border chrome) continue;
            var selected = _accountItemOwners.TryGetValue(item, out var owner) &&
                           ReferenceEquals(item, owner.SelectedItem);
            chrome.BorderBrush = selected
                ? selectedBorder
                : new SolidColorBrush(Colors.Transparent);
            chrome.Background = selected
                ? selectedBackground
                : new SolidColorBrush(Colors.Transparent);
        }
    }

    private async Task RenderDetailAsync()
    {
        DetailPanel.Children.Clear();
        if (_isCompact && _showCompactDetail)
        {
            var back = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE72B", FontSize = 12 },
                        new TextBlock { Text = Display.S("Back") },
                    },
                },
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(back, "BackToAccountsButton");
            back.Click += (_, _) =>
            {
                _showCompactDetail = false;
                ApplyResponsiveContentVisibility();
            };
            DetailPanel.Children.Add(back);
        }
        var account = _accounts.FirstOrDefault(a => a.Id == _selectedId);
        if (account is null || _session.Api is null)
        {
            DetailPanel.Children.Add(DetailEmptyText);
            DetailEmptyText.Visibility = Visibility.Visible;
            return;
        }

        AccountDetailDto detail;
        try
        {
            detail = await _session.Api.GetAccountAsync(account.Id);
        }
        catch (ApiException) { return; }
        if (detail.Account.Id != _selectedId) return; // stale response

        List<InvestmentPositionDto> positions = [];
        if (detail.Account.Category is "investment" or "retirement")
        {
            try
            {
                positions = (await _session.Api.GetInvestmentPositionsAsync(detail.Account.Id)).Positions;
            }
            catch (ApiException)
            {
                // The account balance and chart remain useful if the provider
                // does not expose security-level positions for this account.
            }
            if (detail.Account.Id != _selectedId) return;
        }

        var privacy = _session.PrivacyMode;
        var decimals = _session.ShowDecimals;
        var a = detail.Account;

        // Reference header: institution identity left, freshness metadata right.
        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new InstitutionLogo(a.InstitutionName, a.Category, 40));
        var titleStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = a.DisplayName,
            Style = Ui.Style("CardTitleStyle"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        titleStack.Children.Add(Ui.Caption(Display.CategoryLabel(a.Category)));
        head.Children.Add(titleStack.At(1));
        var freshnessMeta = Ui.Caption(string.Format(
            Display.S("DetailUpdatedFormat"),
            a.Source == "manual" ? (a.Balance?.AsOfDate ?? Display.S("Time_never")) : Display.RelativeTime(a.LastSyncedAt)));
        freshnessMeta.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(freshnessMeta.At(2));
        DetailPanel.Children.Add(head);

        // Balance + change, grouped so the label sits tight on its figure.
        var balance = new StackPanel { Spacing = 4 };
        balance.Children.Add(Ui.Caption(Display.S("CurrentValueLabel")));
        balance.Children.Add(new TextBlock
        {
            Text = a.Balance is null ? Display.S("NoBalance") : Money.Format(a.Balance.Amount, decimals, privacy),
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        var changeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var changeLabel = Ui.Caption(Display.S("TodayChangeLabel"));
        changeLabel.VerticalAlignment = VerticalAlignment.Center;
        changeRow.Children.Add(changeLabel);
        var change = new ChangeBadge();
        change.SetChange(a.Change);
        changeRow.Children.Add(change);
        balance.Children.Add(changeRow);
        if (_isCompact && a.Balance is not null)
        {
            var asOf = Ui.Caption(string.Format(Display.S("BalanceAsOf"), a.Balance.AsOfDate));
            asOf.Foreground = Ui.Brush("TextFillColorTertiaryBrush");
            balance.Children.Add(asOf);
        }
        DetailPanel.Children.Add(balance);

        // Full metadata (SPEC §17.5): type/subtype, mask, owner and item state.
        var meta = new List<(string Key, string? Value)>
        {
            ("Detail_type", a.Subtype is null ? a.Type : $"{a.Type} / {a.Subtype}"),
            ("Detail_mask", a.Mask is null ? null : privacy ? Money.PrivacyMask : $"····{a.Mask}"),
            ("Detail_owner", a.Owner),
            ("Detail_freshness", Display.FreshnessLabel(a.Freshness)),
            ("Detail_lastError", a.Attention),
        };
        var metaGrid = new Grid { ColumnSpacing = 10, RowSpacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var metaRow = 0;
        foreach (var (key, value) in meta.Where(m => m.Value is not null))
        {
            metaGrid.RowDefinitions.Add(new RowDefinition());
            var k = Ui.Caption(Display.S(key));
            var v = new TextBlock { Text = value, Style = Ui.Style("CaptionStyle"), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(k, metaRow); Grid.SetRow(v, metaRow); Grid.SetColumn(v, 1);
            metaGrid.Children.Add(k); metaGrid.Children.Add(v);
            metaRow++;
        }
        // 30-day chart
        if (a.Sparkline.Count >= 2)
        {
            DetailPanel.Children.Add(new TextBlock { Text = Display.S("Trend30d"), Style = Ui.Style("SectionLabelStyle"), Margin = new Thickness(0, 8, 0, 0) });
            var chart = new TrendChart { Height = 140 };
            var accent = ((SolidColorBrush)Application.Current.Resources["ChartLineBrush"]).Color;
            chart.SetSeries([
                new ChartSeriesData(Display.S("Series_balance"),
                    a.Sparkline.Select(p => (DateOnly.Parse(p.Date), Money.Parse(p.Amount))).ToList(),
                    accent, Fill: true),
            ]);
            DetailPanel.Children.Add(chart);
        }

        if (positions.Count > 0)
            RenderInvestmentAccountDetail(a, positions, decimals, privacy);

        // Keep account management and diagnostic metadata behind one disclosure.
        // The primary rail is a financial summary; editing controls and raw
        // snapshots should not push the composition and holdings below the fold.
        var managementPanel = new StackPanel { Spacing = 10, Padding = new Thickness(0, 8, 0, 4) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var excludeToggle = new ToggleSwitch
        {
            Header = Display.S("IncludeInNetWorth"),
            IsOn = !a.Excluded,
            OnContent = Display.S("Included"),
            OffContent = Display.S("ExcludedChip"),
        };
        excludeToggle.Toggled += async (_, _) =>
        {
            try
            {
                await _session.Api!.PatchAccountAsync(a.Id, new { excluded = !excludeToggle.IsOn });
                await LoadAsync();
                _session.NotifyDataRefreshed();
            }
            catch (ApiException) { }
        };
        managementPanel.Children.Add(excludeToggle);

        if (a.Source == "manual")
        {
            var updateButton = new Button { Content = Display.S("UpdateValue"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            updateButton.Click += async (_, _) => await ShowUpdateValueDialogAsync(a);
            actions.Children.Add(updateButton);

            var deleteButton = new Button { Content = Display.S("DeleteAccount") };
            deleteButton.Click += async (_, _) => await ShowDeleteDialogAsync(a);
            actions.Children.Add(deleteButton);
        }
        var nicknameButton = new Button { Content = Display.S("EditNickname") };
        nicknameButton.Click += async (_, _) => await ShowNicknameDialogAsync(a);
        actions.Children.Add(nicknameButton);
        managementPanel.Children.Add(actions);
        managementPanel.Children.Add(metaGrid);

        // Recent snapshots (SPEC §17.5: last 10)
        if (detail.RecentSnapshots.Count > 0)
        {
            managementPanel.Children.Add(new TextBlock
            {
                Text = Display.S("RecentSnapshots"),
                Style = Ui.Style("SectionLabelStyle"),
                Margin = new Thickness(0, 8, 0, 0),
            });
            foreach (var snap in detail.RecentSnapshots)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var dateText = new TextBlock { Text = snap.Date, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
                var amountText = new TextBlock { Text = Money.Format(snap.Amount, decimals, privacy), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                Grid.SetColumn(amountText, 1);
                row.Children.Add(dateText);
                row.Children.Add(amountText);
                managementPanel.Children.Add(row);
            }
        }
        var managementExpander = new Expander
        {
            Header = Display.S("AccountDetailsTitle"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = false,
            Content = managementPanel,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            managementExpander, "AccountManagementDetails");
        DetailPanel.Children.Add(managementExpander);
    }

    private void RenderInvestmentAccountDetail(
        AccountSummaryDto account,
        IReadOnlyList<InvestmentPositionDto> positions,
        bool decimals,
        bool privacy)
    {
        var positionTotal = positions.Sum(position => Math.Max(0m, Money.Parse(position.Value)));
        var balanceTotal = account.Balance is null ? 0m : Math.Abs(Money.Parse(account.Balance.Amount));
        var denominator = Math.Max(positionTotal, balanceTotal);
        if (denominator <= 0) return;

        var divider = Ui.Divider();
        divider.Margin = new Thickness(0, 4, 0, 0);
        DetailPanel.Children.Add(divider);
        DetailPanel.Children.Add(new TextBlock
        {
            Text = Display.S("AccountCompositionTitle"),
            Style = Ui.Style("SectionLabelStyle"),
        });

        var bucketAmounts = positions
            .GroupBy(position => position.CashEquivalent
                ? "Cash"
                : Display.NormalizePortfolioBucket(position.InstrumentType))
            .ToDictionary(
                group => group.Key,
                group => group.Sum(position => Math.Max(0m, Money.Parse(position.Value))));
        var unallocatedCash = Math.Max(0m, balanceTotal - positionTotal);
        if (unallocatedCash > 0)
            bucketAmounts["Cash"] = bucketAmounts.GetValueOrDefault("Cash") + unallocatedCash;
        var buckets = bucketAmounts
            .Select(item => new
            {
                Bucket = item.Key,
                Name = InvestmentBucketName(item.Key),
                Amount = item.Value,
            })
            .OrderByDescending(bucket => bucket.Amount)
            .ToList();

        for (var index = 0; index < buckets.Count; index++)
        {
            var bucket = buckets[index];
            var percent = bucket.Amount / denominator * 100m;
            var row = new Grid { ColumnSpacing = 10, RowSpacing = 5 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.Children.Add(new Ellipse
            {
                Width = 8, Height = 8,
                Fill = Display.PortfolioBucketBrush(bucket.Bucket),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock { Text = bucket.Name, Style = Ui.Style("CaptionStyle") }.At(1));
            row.Children.Add(new TextBlock
            {
                Text = $"{percent:0.0}%   {Money.Format(bucket.Amount, decimals, privacy)}",
                Style = Ui.Style("CaptionStyle"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            }.At(2));
            var bar = Ui.Meter((double)(percent / 100m), Display.PortfolioBucketBrush(bucket.Bucket), 5).At(1, 1);
            Grid.SetColumnSpan(bar, 2);
            row.Children.Add(bar);
            DetailPanel.Children.Add(row);
        }

        var top = positions
            .Where(position => !position.CashEquivalent)
            .OrderByDescending(position => Money.Parse(position.Value))
            .Take(3)
            .ToList();
        if (top.Count == 0) return;

        DetailPanel.Children.Add(new TextBlock
        {
            Text = Display.S("TopThreeHoldingsTitle"),
            Style = Ui.Style("SectionLabelStyle"),
            Margin = new Thickness(0, 8, 0, 0),
        });
        foreach (var position in top)
        {
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var names = new StackPanel { Spacing = 1 };
            names.Children.Add(Ui.Label(position.Name));
            names.Children.Add(Ui.Caption(position.Symbol));
            row.Children.Add(names);
            var value = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Right };
            value.Children.Add(new TextBlock
            {
                Text = Money.Format(position.Value, decimals, privacy),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            var share = Ui.Caption(privacy ? string.Empty : $"{Money.Parse(position.Value) / denominator * 100m:0.0}%");
            share.HorizontalAlignment = HorizontalAlignment.Right;
            value.Children.Add(share);
            row.Children.Add(value.At(1));
            DetailPanel.Children.Add(row);
        }
    }

    private static string InvestmentBucketName(string bucket) =>
        Display.S(Display.NormalizePortfolioBucket(bucket) switch
        {
            "Stocks" => "Instrument_stock",
            "ETFs" => "Instrument_etf",
            "MutualFunds" => "Instrument_mutualFund",
            "Crypto" => "Instrument_crypto",
            "Cash" => "Instrument_cash",
            "Unclassified" => "Instrument_unclassified",
            _ => "Instrument_other",
        });

    // ── Dialogs ─────────────────────────────────────────────────────────────────

    private async Task ShowNicknameDialogAsync(AccountSummaryDto account)
    {
        var box = new TextBox { Text = account.Nickname ?? "", PlaceholderText = account.DisplayName };
        var dialog = new ContentDialog
        {
            Title = Display.S("EditNickname"),
            Content = box,
            PrimaryButtonText = Display.S("Save"),
            CloseButtonText = Display.S("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                await _session.Api!.PatchAccountAsync(account.Id,
                    new { nickname = box.Text.Trim().Length == 0 ? null : box.Text.Trim() });
                await LoadAsync();
            }
            catch (ApiException) { }
        }
    }

    private async Task ShowUpdateValueDialogAsync(AccountSummaryDto account)
    {
        var amountBox = new TextBox
        {
            Header = Display.S("NewValueHeader"),
            // Manual liabilities are edited as a positive amount owed, while
            // the account view shows their negative net-worth contribution.
            Text = account.Balance is null
                ? ""
                : account.AccountClass == "liability"
                    ? Math.Abs(Money.Parse(account.Balance.Amount)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                    : account.Balance.Amount,
            PlaceholderText = "12345.67",
        };
        var datePicker = new DatePicker { Header = Display.S("EffectiveDateHeader"), Date = DateTimeOffset.Now };
        var warning = Ui.Caption(Display.S("BackdateWarning"), wrap: true);
        var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
        panel.Children.Add(amountBox);
        panel.Children.Add(datePicker);
        panel.Children.Add(warning);

        var dialog = new ContentDialog
        {
            Title = Display.S("UpdateValue"),
            Content = panel,
            PrimaryButtonText = Display.S("Save"),
            CloseButtonText = Display.S("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var date = datePicker.Date.ToString("yyyy-MM-dd");
            await _session.Api!.UpdateManualValueAsync(account.Id, amountBox.Text.Trim(), date);
            await LoadAsync();
            _session.NotifyDataRefreshed();
        }
        catch (ApiException ex)
        {
            await new ContentDialog
            {
                Title = Display.S("Error"),
                Content = ex.Message,
                CloseButtonText = Display.S("OK"),
                XamlRoot = XamlRoot,
            }.ShowAsync();
        }
    }

    private async Task ShowDeleteDialogAsync(AccountSummaryDto account)
    {
        var dialog = new ContentDialog
        {
            Title = Display.S("DeleteAccount"),
            Content = string.Format(Display.S("DeleteConfirmBody"), account.DisplayName),
            PrimaryButtonText = Display.S("Delete"),
            CloseButtonText = Display.S("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _session.Api!.DeleteManualAccountAsync(account.Id);
            _selectedId = null;
            await LoadAsync();
            _session.NotifyDataRefreshed();
        }
        catch (ApiException) { }
    }

    private async Task AddAccountAsync()
    {
        if (!AddAccountButton.IsEnabled) return;
        AddAccountButton.IsEnabled = false;
        IReadOnlyList<PlaidItemDto> connectedItems = [];
        string? loadError = null;
        try
        {
            if (_session.Api is not null)
                connectedItems = (await _session.Api.GetPlaidItemsAsync()).Items;
        }
        catch (ApiException ex)
        {
            loadError = ex.Message;
            AppLog.Warn("Accounts", "Could not load connected institutions for Add account.");
        }
        finally
        {
            AddAccountButton.IsEnabled = true;
        }

        var dialog = new AddAccountChoiceDialog(connectedItems, loadError) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
        switch (dialog.Selection)
        {
            case AddAccountChoice.ConnectNewInstitution:
                AppLog.Breadcrumb("Accounts", "Connect another institution selected from Add account.");
                App.Window?.ShowSetupGate(allowCancel: true, startAutomatic: true);
                return;
            case AddAccountChoice.ManageConnections:
                AppLog.Breadcrumb("Accounts", "Manage existing connections selected from Add account.");
                App.Window?.NavigateToSettings();
                return;
            case AddAccountChoice.AddManualBalance:
                break;
            default:
                return;
        }

        var manualDialog = new AddManualAccountDialog { XamlRoot = XamlRoot };
        var created = await manualDialog.ShowAndCreateAsync(_session);
        if (created)
        {
            await LoadAsync();
            _session.NotifyDataRefreshed();
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _search = sender.Text.Trim();
        _ = LoadAsync();
    }

    private void FilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _filter = sender.SelectedItem?.Tag as string ?? string.Empty;
        _ = LoadAsync();
    }

    private void AddSortOption(string resourceKey, string value, bool isChecked = false)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = Display.S(resourceKey),
            GroupName = "accounts-sort",
            IsChecked = isChecked,
        };
        item.Click += (_, _) =>
        {
            _sort = value;
            SortLabel.Text = item.Text;
            _ = LoadAsync();
        };
        SortMenu.Items.Add(item);
    }
}
