using AssetScope.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace AssetScope.App.Controls;

/// <summary>
/// Local security mark used by every holdings surface. No remote image request
/// is made, so rendering cannot disclose the user's portfolio to an image CDN.
/// </summary>
public sealed class SecurityLogo : Grid
{
    private static readonly IReadOnlyDictionary<SecurityBrand, string> BrandAssets =
        new Dictionary<SecurityBrand, string>
        {
            [SecurityBrand.Vanguard] = "ms-appx:///Assets/Securities/vanguard.svg",
            [SecurityBrand.Invesco] = "ms-appx:///Assets/Securities/invesco.svg",
            [SecurityBrand.Fidelity] = "ms-appx:///Assets/Securities/fidelity.svg",
            [SecurityBrand.Apple] = "ms-appx:///Assets/Securities/apple.svg",
            [SecurityBrand.Microsoft] = "ms-appx:///Assets/Securities/microsoft.svg",
            [SecurityBrand.Nvidia] = "ms-appx:///Assets/Securities/nvidia.svg",
            [SecurityBrand.Cash] = "ms-appx:///Assets/Securities/cash.svg",
        };

    private static readonly Color[] FallbackColors =
    [
        Color.FromArgb(255, 0x12, 0x67, 0xF4),
        Color.FromArgb(255, 0x7C, 0x4D, 0xDB),
        Color.FromArgb(255, 0x03, 0x83, 0x87),
        Color.FromArgb(255, 0xC2, 0x39, 0xB3),
        Color.FromArgb(255, 0xE0, 0x7A, 0x18),
    ];

    public SecurityLogo(string? symbol, string? name, double size = 32)
    {
        Width = size;
        Height = size;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetAccessibilityView(
            this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        var brand = SecurityBrands.Match(symbol, name);
        if (BrandAssets.TryGetValue(brand, out var asset))
        {
            Children.Add(new Image
            {
                Width = size,
                Height = size,
                Source = new SvgImageSource(new Uri(asset)),
                Stretch = Stretch.Uniform,
            });
            return;
        }

        var monogram = string.IsNullOrWhiteSpace(symbol)
            ? "?"
            : symbol.Trim()[..Math.Min(2, symbol.Trim().Length)].ToUpperInvariant();
        var colorIndex = monogram.Aggregate(0, (sum, character) => sum + character) % FallbackColors.Length;
        Children.Add(new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = new SolidColorBrush(FallbackColors[colorIndex]),
            Child = new TextBlock
            {
                Text = monogram,
                FontSize = monogram.Length > 1 ? size * 0.31 : size * 0.4,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
    }
}
