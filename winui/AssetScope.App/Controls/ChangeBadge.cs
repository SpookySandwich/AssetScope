using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using AssetScope.App.Services;
using AssetScope.Core;
using AssetScope.Core.Api;

namespace AssetScope.App.Controls;

/// <summary>
/// "▲ +$230.45 (0.82%) vs. yesterday" — up/down glyph + amount + ratio + label.
/// Uses glyph + text, not color alone (SPEC §24.1), and honors Privacy Mode.
/// </summary>
public sealed partial class ChangeBadge : UserControl
{
    private readonly FontIcon _icon = new() { FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _amount = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly FontIcon _stackedIcon = new() { FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _stackedAmount = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _stackedRatio = new() { FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly StackPanel _stackedValueLine = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly StackPanel _stackedPanel = new() { Spacing = 1, Visibility = Visibility.Collapsed };
    private readonly Grid _layout = new();

    public bool StackAmountAndRatio
    {
        set
        {
            _panel.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            _stackedPanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public double AmountFontSize
    {
        set
        {
            _amount.FontSize = value;
            _label.FontSize = value;
            _icon.FontSize = value * 0.8;
            _stackedAmount.FontSize = value;
            _stackedRatio.FontSize = Math.Max(10, value - 1);
            _stackedIcon.FontSize = value * 0.8;
        }
    }

    public ChangeBadge()
    {
        _label.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _panel.Children.Add(_icon);
        _panel.Children.Add(_amount);
        _panel.Children.Add(_label);
        _stackedValueLine.Children.Add(_stackedIcon);
        _stackedValueLine.Children.Add(_stackedAmount);
        _stackedPanel.Children.Add(_stackedValueLine);
        _stackedPanel.Children.Add(_stackedRatio);
        _layout.Children.Add(_panel);
        _layout.Children.Add(_stackedPanel);
        Content = _layout;
    }

    public void SetChange(
        ChangeDto? change,
        bool showComparisonLabel = true,
        string? comparisonText = null)
    {
        if (change is null || !Money.TryParse(change.Amount, out var delta))
        {
            _icon.Visibility = Visibility.Collapsed;
            _amount.Visibility = Visibility.Collapsed;
            _label.Text = string.IsNullOrWhiteSpace(comparisonText)
                ? "—"
                : $"— {comparisonText}";
            _label.Visibility = Visibility.Visible;
            _stackedIcon.Visibility = Visibility.Collapsed;
            _stackedAmount.Text = "—";
            _stackedAmount.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            _stackedRatio.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(_layout, Display.S("Change_noComparison"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                _layout, Display.S("Change_noComparison"));
            return;
        }

        ToolTipService.SetToolTip(_layout, null);

        var session = AppSession.Current;
        var up = delta >= 0;
        var brush = (Brush)Application.Current.Resources[up ? "PositiveChangeBrush" : "NegativeChangeBrush"];

        _icon.Visibility = Visibility.Visible;
        _amount.Visibility = Visibility.Visible;
        _icon.Glyph = up ? "" : ""; // caret solid up / down
        _icon.Foreground = brush;
        _amount.Foreground = brush;
        _stackedIcon.Visibility = Visibility.Visible;
        _stackedIcon.Glyph = _icon.Glyph;
        _stackedIcon.Foreground = brush;
        _stackedAmount.Foreground = brush;
        _stackedRatio.Foreground = brush;

        var ratio = Money.FormatRatio(change.Ratio, session.PrivacyMode);
        var amountText = session.PrivacyMode ? Money.PrivacyMask : Money.FormatSigned(delta);
        _amount.Text = ratio.Length > 0 && !session.PrivacyMode ? $"{amountText} ({ratio})" : amountText;
        _stackedAmount.Text = amountText;
        _stackedRatio.Text = ratio.Length > 0 && !session.PrivacyMode ? ratio : string.Empty;
        _stackedRatio.Visibility = _stackedRatio.Text.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        _label.Visibility = showComparisonLabel ? Visibility.Visible : Visibility.Collapsed;
        _label.Text = showComparisonLabel
            ? comparisonText ?? Display.ComparisonLabel(change.Comparison)
            : string.Empty;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            _layout,
            showComparisonLabel && _label.Text.Length > 0
                ? $"{_amount.Text}, {_label.Text}"
                : _amount.Text);
    }
}
