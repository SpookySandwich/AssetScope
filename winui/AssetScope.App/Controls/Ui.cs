using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AssetScope.App.Controls;

/// <summary>
/// Building blocks for rows that pages assemble in code. They read the same
/// styles and brushes as the XAML, so a chip, divider or caption built here
/// matches one declared in markup and follows the density setting.
/// </summary>
public static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static Rectangle Divider() => new() { Style = Style("DividerStyle") };

    /// <summary>Secondary 12px text, trimmed to one line unless it may wrap.</summary>
    public static TextBlock Caption(string text, bool wrap = false) => new()
    {
        Text = text,
        Style = Style("CaptionSecondaryStyle"),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    /// <summary>Body text that trims rather than overflowing its column.</summary>
    public static TextBlock Label(string text, bool strong = false) => new()
    {
        Text = text,
        FontWeight = strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>
    /// A tinted rounded square holding a glyph. Corner radius and glyph size
    /// scale with the chip so every size has the same proportions.
    /// </summary>
    public static Border IconChip(string glyph, string tintKey, string foregroundKey, double size = 32) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(Math.Round(size / 4)),
        Background = Brush(tintKey),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new FontIcon
        {
            Glyph = glyph,
            FontSize = Math.Round(size * 0.44),
            Foreground = Brush(foregroundKey),
        },
    };

    /// <summary>A rounded track with a filled share, for proportions and progress.</summary>
    public static Grid Meter(double fill, string fillKey, double height = 6) =>
        Meter(fill, Brush(fillKey), height);

    public static Grid Meter(double fill, Brush fillBrush, double height = 6)
    {
        fill = double.IsNaN(fill) ? 0 : Math.Clamp(fill, 0, 1);
        var radius = new CornerRadius(height / 2);
        var meter = new Grid { Height = height, CornerRadius = radius, Background = Brush("ReferenceNeutralTintBrush") };
        meter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(fill, 0.0001), GridUnitType.Star) });
        meter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - fill, 0.0001), GridUnitType.Star) });
        if (fill > 0)
            meter.Children.Add(new Border { CornerRadius = radius, Background = fillBrush });
        return meter;
    }

    /// <summary>A small rounded label, such as a rate or a status.</summary>
    public static Border Badge(string text, string tintKey, string foregroundKey) => new()
    {
        Background = Brush(tintKey),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(7, 2, 7, 3),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(foregroundKey),
        },
    };

    /// <summary>A glyph and wrapping caption, for warnings and tips under a row; an optional link ends the text.</summary>
    public static Grid Note(string glyph, string text, bool caution, (string Text, Uri Uri)? link = null)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var brush = Brush(caution ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
        grid.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 12,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        });
        var caption = Caption(text, wrap: true);
        if (caution) caption.Foreground = Brush("TextFillColorPrimaryBrush");
        if (link is { } l)
        {
            caption.Inlines.Add(new Run { Text = " " });
            caption.Inlines.Add(Link(l.Text, l.Uri));
        }
        Grid.SetColumn(caption, 1);
        grid.Children.Add(caption);
        return grid;
    }

    /// <summary>An inline link that opens in the default browser.</summary>
    public static Hyperlink Link(string text, Uri uri)
    {
        var link = new Hyperlink { Foreground = Brush("ReferenceBlueTextBrush") };
        link.Inlines.Add(new Run { Text = text });
        AutomationProperties.SetName(link, text);
        link.Click += (_, _) => _ = Windows.System.Launcher.LaunchUriAsync(uri);
        return link;
    }

    /// <summary>A caption-sized line of links, separated by dots.</summary>
    public static TextBlock Links(params (string Text, Uri Uri)[] links)
    {
        var line = Caption(string.Empty, wrap: true);
        for (var i = 0; i < links.Length; i++)
        {
            if (i > 0) line.Inlines.Add(new Run { Text = "  ·  " });
            line.Inlines.Add(Link(links[i].Text, links[i].Uri));
        }
        return line;
    }

    /// <summary>Grid columns from a list of widths; null means Auto.</summary>
    public static void Columns(Grid grid, params double?[] stars)
    {
        foreach (var star in stars)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = star is { } value ? new GridLength(value, GridUnitType.Star) : GridLength.Auto,
            });
    }

    public static T At<T>(this T element, int column, int row = 0) where T : FrameworkElement
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        return element;
    }
}
