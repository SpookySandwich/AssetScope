using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using AssetScope.App.Services;
using AssetScope.Core.Api;

namespace AssetScope.App.Pages;

/// <summary>
/// Manual asset/liability creation (SPEC §18.1, condensed): class + category +
/// name + amount + effective date + include-in-net-worth. Amounts are always
/// entered as positive values; liabilities are "amount owed".
/// </summary>
public sealed partial class AddManualAccountDialog : ContentDialog
{
    private static readonly (string Value, string LabelKey)[] AssetCategories =
    [
        ("cash", "Manual_cash"),
        ("property", "Manual_property"),
        ("vehicle", "Manual_vehicle"),
        ("investment", "Manual_investment"),
        ("other", "Manual_other"),
    ];
    private static readonly (string Value, string LabelKey)[] LiabilityCategories =
    [
        ("loan", "Manual_loan"),
    ];

    private readonly RadioButtons _classPicker = new();
    private readonly ComboBox _categoryPicker = new() { MinWidth = 220 };
    private readonly TextBox _nameBox = new() { MaxLength = 100 };
    private readonly TextBox _amountBox = new() { PlaceholderText = "12345.67" };
    private readonly DatePicker _datePicker = new() { Date = DateTimeOffset.Now };
    private readonly ToggleSwitch _includeToggle = new() { IsOn = true };
    private readonly TextBlock _errorText = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _previewText = new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };
    private decimal? _currentNetWorth;

    public AddManualAccountDialog()
    {
        AutomationProperties.SetAutomationId(this, "AddManualAccountDialog");
        AutomationProperties.SetAutomationId(_classPicker, "ManualAccountClassPicker");
        AutomationProperties.SetAutomationId(_categoryPicker, "ManualAccountCategoryPicker");
        AutomationProperties.SetAutomationId(_nameBox, "ManualAccountNameInput");
        AutomationProperties.SetAutomationId(_amountBox, "ManualAccountBalanceInput");
        AutomationProperties.SetAutomationId(_datePicker, "ManualAccountDatePicker");
        AutomationProperties.SetAutomationId(_includeToggle, "IncludeManualAccountInNetWorthToggle");
        AutomationProperties.SetAutomationId(_errorText, "ManualAccountError");

        Title = Display.S("AddAccount");
        PrimaryButtonText = Display.S("Create");
        CloseButtonText = Display.S("Cancel");
        DefaultButton = ContentDialogButton.Primary;

        _classPicker.Header = Display.S("ManualClassHeader");
        _classPicker.Items.Add(Display.S("ManualClassAsset"));
        _classPicker.Items.Add(Display.S("ManualClassLiability"));
        _classPicker.SelectedIndex = 0;
        _classPicker.SelectionChanged += (_, _) => FillCategories();

        _categoryPicker.Header = Display.S("ManualCategoryHeader");
        _nameBox.Header = Display.S("ManualNameHeader");
        _nameBox.PlaceholderText = Display.S("ManualNamePlaceholder");
        _amountBox.Header = Display.S("ManualAmountHeader");
        _datePicker.Header = Display.S("EffectiveDateHeader");
        _includeToggle.Header = Display.S("IncludeInNetWorth");
        _errorText.Foreground = (Brush)Application.Current.Resources["NegativeChangeBrush"];

        FillCategories();

        var panel = new StackPanel { Spacing = 12, MinWidth = 340 };
        panel.Children.Add(_classPicker);
        panel.Children.Add(_categoryPicker);
        panel.Children.Add(_nameBox);
        panel.Children.Add(_amountBox);
        panel.Children.Add(_datePicker);
        panel.Children.Add(_includeToggle);
        panel.Children.Add(_previewText);
        panel.Children.Add(_errorText);
        Content = new ScrollViewer { Content = panel };

        // Net-worth impact preview (SPEC §18.1 step 5).
        _amountBox.TextChanged += (_, _) => UpdatePreview();
        _classPicker.SelectionChanged += (_, _) => UpdatePreview();
        _includeToggle.Toggled += (_, _) => UpdatePreview();
        _ = LoadCurrentNetWorthAsync();
    }

    private async Task LoadCurrentNetWorthAsync()
    {
        try
        {
            var dashboard = await AppSession.Current.Api!.GetDashboardAsync("1m");
            _currentNetWorth = AssetScope.Core.Money.Parse(dashboard.Summary.NetWorth.Amount);
            UpdatePreview();
        }
        catch (ApiException) { }
    }

    private void UpdatePreview()
    {
        if (_currentNetWorth is null
            || !decimal.TryParse(_amountBox.Text.Trim(), System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var amount)
            || amount < 0
            || !_includeToggle.IsOn)
        {
            _previewText.Visibility = Visibility.Collapsed;
            return;
        }
        var signed = _classPicker.SelectedIndex == 1 ? -amount : amount;
        var privacy = AppSession.Current.PrivacyMode;
        _previewText.Text = string.Format(Display.S("ManualPreview"),
            AssetScope.Core.Money.Format(_currentNetWorth.Value, true, privacy),
            AssetScope.Core.Money.Format(_currentNetWorth.Value + signed, true, privacy));
        _previewText.Visibility = Visibility.Visible;
    }

    private void FillCategories()
    {
        var categories = _classPicker.SelectedIndex == 1 ? LiabilityCategories : AssetCategories;
        _categoryPicker.Items.Clear();
        foreach (var (value, labelKey) in categories)
            _categoryPicker.Items.Add(new ComboBoxItem { Content = Display.S(labelKey), Tag = value });
        _categoryPicker.SelectedIndex = 0;
    }

    public async Task<bool> ShowAndCreateAsync(AppSession session)
    {
        while (true)
        {
            var result = await ShowAsync();
            if (result != ContentDialogResult.Primary) return false;

            var accountClass = _classPicker.SelectedIndex == 1 ? "liability" : "asset";
            var category = (_categoryPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "other";
            try
            {
                await session.Api!.CreateManualAccountAsync(new
                {
                    name = _nameBox.Text.Trim(),
                    accountClass,
                    manualCategory = category,
                    initialValue = new
                    {
                        amount = _amountBox.Text.Trim(),
                        currencyCode = "USD",
                        effectiveDate = _datePicker.Date.ToString("yyyy-MM-dd"),
                    },
                    includeInNetWorth = _includeToggle.IsOn,
                });
                return true;
            }
            catch (ApiException ex)
            {
                _errorText.Text = ex.Message;
                _errorText.Visibility = Visibility.Visible;
                // Re-open with preserved input so the user can correct the field.
            }
        }
    }
}
