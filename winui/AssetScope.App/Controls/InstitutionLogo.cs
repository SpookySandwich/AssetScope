using AssetScope.Core;
using AssetScope.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AssetScope.App.Controls;

/// <summary>
/// Shared institution mark with a deterministic account-category fallback.
/// Brand artwork is local, so rendering never makes a network request or
/// exposes the user's list of institutions to a third party.
/// </summary>
public sealed class InstitutionLogo : Grid
{
    private static readonly IReadOnlyDictionary<InstitutionBrand, string> BrandAssets =
        new Dictionary<InstitutionBrand, string>
        {
            [InstitutionBrand.BankOfAmerica] = "ms-appx:///Assets/Institutions/bank-of-america.svg",
            [InstitutionBrand.Chase] = "ms-appx:///Assets/Institutions/chase.svg",
            [InstitutionBrand.Robinhood] = "ms-appx:///Assets/Institutions/robinhood.svg",
            [InstitutionBrand.XMoney] = "ms-appx:///Assets/Institutions/x-money.svg",
        };

    public InstitutionLogo(string? institutionName, string category, double size = 36)
    {
        Width = size;
        Height = size;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetAccessibilityView(
            this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        var brand = InstitutionBrands.Match(institutionName);
        if (BrandAssets.TryGetValue(brand, out var asset))
        {
            // Keep the SVG inside an explicit optical safe area. A Viewbox
            // scales the complete 24×24 viewBox as one unit, so brand paths
            // that touch their source bounds (notably Chase) cannot be cut by
            // the fixed-size account-row host at any requested logo size.
            const double safeArea = 0.72;
            Children.Add(new Viewbox
            {
                Width = size * safeArea,
                Height = size * safeArea,
                Stretch = Stretch.Uniform,
                Child = new Image
                {
                    Width = 24,
                    Height = 24,
                    Source = new SvgImageSource(new Uri(asset)),
                    Stretch = Stretch.Uniform,
                },
            });
            return;
        }

        Children.Add(new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = Display.CategoryBrush(category),
            Child = new FontIcon
            {
                Glyph = Display.CategoryGlyph(category),
                FontSize = size * 0.42,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            },
        });
    }
}
