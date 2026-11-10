using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Globalization;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using AssetScope.App.Services;
using AssetScope.Core;
using AssetScope.Core.Api;

namespace AssetScope.App.Controls;

public sealed record ChartSeriesData(
    string Label,
    IReadOnlyList<(DateOnly Date, decimal Value)> Points,
    Color Color,
    bool Fill,
    IReadOnlyList<double>? DashArray = null);

/// <summary>
/// Line/area chart used by Home, Trends and account details. Pure XAML shapes —
/// no charting dependency (SPEC §5.4 keeps ViewModels renderer-agnostic; pages
/// hand this control plain series data). Hover shows date + one value per series.
/// </summary>
public sealed partial class TrendChart : UserControl
{
    private readonly Canvas _canvas = new();
    private readonly Grid _root = new();
    private readonly Border _tooltip;
    private readonly TextBlock _tooltipText = new() { FontSize = 12 };
    private readonly Line _hoverLine;

    private IReadOnlyList<ChartSeriesData> _series = [];
    private double _padLeft = 48, _padRight = 12, _padTop = 10, _padBottom = 26;
    private DateOnly _minDate, _maxDate;
    private decimal _minValue, _maxValue;

    public bool ShowYAxis { get; set; } = true;
    public bool ShowXAxis { get; set; } = true;
    public int XTickCount { get; set; } = 5;
    public int YTickCount { get; set; } = 4;
    public double LineThickness { get; set; } = 2;
    public double FillOpacity { get; set; } = 70;
    public bool ShowEndMarker { get; set; }

    public TrendChart()
    {
        _hoverLine = new Line
        {
            Stroke = new SolidColorBrush(Colors.Gray),
            StrokeThickness = 1,
            StrokeDashArray = [2.0, 2.0],
            Visibility = Visibility.Collapsed,
        };
        _tooltip = new Border
        {
            Background = (Brush)Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _tooltipText,
        };
        _root.Children.Add(_canvas);
        Content = _root;
        SizeChanged += (_, _) => Redraw();
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => HideHover();
        MinHeight = 120;

        // Keyboard access + screen-reader summary (SPEC §16.4/§24.1): the chart
        // is focusable, arrow keys walk the data points, and the automation
        // name carries the values color and pixels cannot.
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
            this, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        GotFocus += (_, _) => { if (_series.Count > 0) MoveFocusIndex(0); };
        LostFocus += (_, _) => { _focusIndex = null; HideHover(); };
        KeyDown += OnKeyDown;
    }

    private int? _focusIndex;

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_series.Count == 0) return;
        var points = _series[0].Points;
        var index = _focusIndex ?? points.Count - 1;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left: index--; break;
            case Windows.System.VirtualKey.Right: index++; break;
            case Windows.System.VirtualKey.Home: index = 0; break;
            case Windows.System.VirtualKey.End: index = points.Count - 1; break;
            default: return;
        }
        MoveFocusIndex(Math.Clamp(index, 0, points.Count - 1));
        e.Handled = true;
    }

    private void MoveFocusIndex(int index)
    {
        var points = _series[0].Points.OrderBy(p => p.Date).ToList();
        if (points.Count == 0) return;
        _focusIndex = index;
        var (date, value) = points[Math.Clamp(index, 0, points.Count - 1)];
        ShowMarkerAt(date);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this,
            $"{_series[0].Label}, {date:MMMM d yyyy}: {Money.Format(value, true, AppSession.Current.PrivacyMode)}");
    }

    public void SetSeries(IReadOnlyList<ChartSeriesData> series)
    {
        _series = series.Where(s => s.Points.Count >= 2).ToList();
        _focusIndex = null;
        if (_series.Count > 0)
        {
            // Textual chart summary for assistive tech (SPEC §24.1).
            var primary = _series[0].Points.OrderBy(p => p.Date).ToList();
            var privacy = AppSession.Current.PrivacyMode;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this,
                $"{_series[0].Label}: {primary[0].Date:MMM yyyy} {Money.Format(primary[0].Value, false, privacy)}"
                + $" to {primary[^1].Date:MMM yyyy} {Money.Format(primary[^1].Value, false, privacy)},"
                + $" {primary.Count} points. Use arrow keys to explore.");
        }
        Redraw();
    }

    /// <summary>
    /// Render a net-worth history as one continuous visual series. Provider
    /// provenance remains metadata for the page's explanation icon; it must
    /// never split the line or make most of the area fill disappear.
    /// </summary>
    public bool SetNetWorthHistory(
        IReadOnlyList<HistoryPointDto> history,
        string label,
        Color color)
    {
        var points = history
            .Select(point => (DateOnly.Parse(point.Date), Money.Parse(point.NetWorth.Amount)))
            .ToList();
        SetSeries(points.Count >= 2
            ? [new ChartSeriesData(label, points, color, Fill: true)]
            : []);
        return history.Any(point => point.Source == "transactionEstimate");
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 40 || height < 40 || _series.Count == 0) return;

        _padLeft = ShowYAxis ? 48 : 4;
        _padBottom = ShowXAxis ? 26 : 4;

        var all = _series.SelectMany(s => s.Points).ToList();
        _minDate = all.Min(p => p.Date);
        _maxDate = all.Max(p => p.Date);
        _minValue = all.Min(p => p.Value);
        _maxValue = all.Max(p => p.Value);
        if (_maxValue == _minValue) { _maxValue += 1; _minValue -= 1; }
        // Breathing room so lines don't hug the frame.
        var span = _maxValue - _minValue;
        _minValue -= span * 0.08m;
        _maxValue += span * 0.08m;

        DrawGridAndAxes(width, height);
        foreach (var s in _series) DrawSeries(s, width, height);

        _canvas.Children.Add(_hoverLine);
        _canvas.Children.Add(_tooltip);
    }

    private double X(DateOnly d, double width)
    {
        var total = Math.Max(1, _maxDate.DayNumber - _minDate.DayNumber);
        return _padLeft + (d.DayNumber - _minDate.DayNumber) / (double)total * (width - _padLeft - _padRight);
    }

    private double Y(decimal v, double height)
    {
        var frac = (double)((v - _minValue) / (_maxValue - _minValue));
        return _padTop + (1 - frac) * (height - _padTop - _padBottom);
    }

    private void DrawGridAndAxes(double width, double height)
    {
        var gridBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        var labelBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var privacy = AppSession.Current.PrivacyMode;

        var yTicks = Math.Max(1, YTickCount);
        for (var i = 0; i <= yTicks; i++)
        {
            var value = _minValue + (_maxValue - _minValue) * i / yTicks;
            var y = Y(value, height);
            _canvas.Children.Add(new Line
            {
                X1 = _padLeft, X2 = width - _padRight, Y1 = y, Y2 = y,
                Stroke = gridBrush, StrokeThickness = 1,
            });
            if (ShowYAxis)
            {
                // Privacy Mode hides monetary axis values (SPEC §14.4).
                var label = new TextBlock
                {
                    Text = privacy ? "•••" : Money.FormatCompact(value),
                    FontSize = 11,
                    Foreground = labelBrush,
                };
                Canvas.SetLeft(label, 2);
                Canvas.SetTop(label, y - 8);
                _canvas.Children.Add(label);
            }
        }

        if (ShowXAxis)
        {
            var totalDays = Math.Max(1, _maxDate.DayNumber - _minDate.DayNumber);
            // Month labels are substantially wider than points. Keep the
            // temporal meaning of the requested range while deriving label
            // density from the actual plot width so the final months never
            // collide at the right edge.
            var plotWidth = Math.Max(1, width - _padLeft - _padRight);
            // Chinese year-month labels occupy roughly 6–8 Latin glyphs.
            // Reserve their full optical width, including a small gutter;
            // the previous 78-DIP estimate still let the final two labels
            // touch when the last label was right-aligned to the plot edge.
            var minimumLabelWidth = totalDays > 200 ? 92d : 70d;
            var widthLimitedTicks = Math.Max(1, (int)Math.Floor(plotWidth / minimumLabelWidth));
            var xTicks = Math.Max(1, Math.Min(Math.Min(XTickCount, widthLimitedTicks), totalDays));
            var labelWidth = totalDays > 200 ? 82d : 62d;
            var lastRight = double.NegativeInfinity;
            TextBlock? previousXAxisLabel = null;
            for (var i = 0; i <= xTicks; i++)
            {
                var d = DateOnly.FromDayNumber(_minDate.DayNumber + totalDays * i / xTicks);
                var x = X(d, width);
                var left = Math.Clamp(x - labelWidth / 2, 0, width - labelWidth);
                // Integer date interpolation can place the last two ticks much
                // closer than the nominal spacing. Skip any interior label
                // that would collide; endpoints remain authoritative.
                if (i is > 0 && i < xTicks && left < lastRight + 8) continue;
                if (i == xTicks && left < lastRight + 8)
                {
                    // Only replace the preceding X-axis label. Looking up the
                    // canvas' last TextBlock could remove a Y-axis label when
                    // the plot is narrow enough to render only two X ticks.
                    if (previousXAxisLabel is not null)
                        _canvas.Children.Remove(previousXAxisLabel);
                }
                var label = new TextBlock
                {
                    Text = FormatAxisDate(d, totalDays),
                    FontSize = 11,
                    Foreground = labelBrush,
                    Width = labelWidth,
                    TextAlignment = TextAlignment.Center,
                };
                Canvas.SetLeft(label, left);
                Canvas.SetTop(label, height - _padBottom + 6);
                _canvas.Children.Add(label);
                previousXAxisLabel = label;
                lastRight = left + labelWidth;
            }
        }
    }

    private void DrawSeries(ChartSeriesData s, double width, double height)
    {
        var points = s.Points.OrderBy(p => p.Date).ToList();
        var lineFigure = new PathFigure { StartPoint = new Windows.Foundation.Point(X(points[0].Date, width), Y(points[0].Value, height)) };
        var lineSegment = new PolyLineSegment();
        foreach (var (date, value) in points.Skip(1))
            lineSegment.Points.Add(new Windows.Foundation.Point(X(date, width), Y(value, height)));
        lineFigure.Segments.Add(lineSegment);

        if (s.Fill)
        {
            var baseline = height - _padBottom;
            var areaFigure = new PathFigure { StartPoint = new Windows.Foundation.Point(X(points[0].Date, width), baseline) };
            var areaSegment = new PolyLineSegment();
            foreach (var (date, value) in points)
                areaSegment.Points.Add(new Windows.Foundation.Point(X(date, width), Y(value, height)));
            areaSegment.Points.Add(new Windows.Foundation.Point(X(points[^1].Date, width), baseline));
            areaFigure.Segments.Add(areaSegment);
            areaFigure.IsClosed = true;

            _canvas.Children.Add(new Path
            {
                Data = new PathGeometry { Figures = { areaFigure } },
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop
                        {
                            Color = Color.FromArgb((byte)Math.Clamp(FillOpacity, 0, 255), s.Color.R, s.Color.G, s.Color.B),
                            Offset = 0,
                        },
                        new GradientStop
                        {
                            Color = Color.FromArgb(
                                (byte)Math.Clamp(FillOpacity * 0.45, 0, 255),
                                s.Color.R, s.Color.G, s.Color.B),
                            Offset = 0.55,
                        },
                        new GradientStop
                        {
                            Color = Color.FromArgb(
                                (byte)Math.Clamp(FillOpacity * 0.08, 0, 255),
                                s.Color.R, s.Color.G, s.Color.B),
                            Offset = 1,
                        },
                    },
                },
            });
        }

        var linePath = new Path
        {
            Data = new PathGeometry { Figures = { lineFigure } },
            Stroke = new SolidColorBrush(s.Color),
            StrokeThickness = LineThickness,
            StrokeLineJoin = PenLineJoin.Round,
        };
        if (s.DashArray is not null)
        {
            foreach (var length in s.DashArray) linePath.StrokeDashArray.Add(length);
        }
        _canvas.Children.Add(linePath);

        if (ShowEndMarker)
        {
            var last = points[^1];
            var marker = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(s.Color),
                Stroke = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(marker, X(last.Date, width) - marker.Width / 2);
            Canvas.SetTop(marker, Y(last.Value, height) - marker.Height / 2);
            _canvas.Children.Add(marker);
        }
    }

    private static string FormatAxisDate(DateOnly date, int totalDays)
    {
        var culture = DisplayCulture();
        if (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return totalDays > 200 ? $"{date.Year}年{date.Month}月" : $"{date.Month}月{date.Day}日";
        return totalDays > 200
            ? date.ToString("MMM yyyy", culture)
            : date.ToString("MMM d", culture);
    }

    private static CultureInfo DisplayCulture()
    {
        var language = AppSession.Current.Settings.Language;
        if (language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)) return CultureInfo.GetCultureInfo("zh-CN");
        if (language.Equals("en-US", StringComparison.OrdinalIgnoreCase)) return CultureInfo.GetCultureInfo("en-US");
        var primary = Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride;
        return primary.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("zh-CN")
            : primary.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? CultureInfo.GetCultureInfo("en-US")
                : CultureInfo.CurrentUICulture;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_series.Count == 0 || ActualWidth < 40) return;
        var pos = e.GetCurrentPoint(_canvas).Position;
        if (pos.X < _padLeft || pos.X > ActualWidth - _padRight) { HideHover(); return; }

        // Nearest date on the first series.
        var primary = _series[0].Points;
        var totalDays = Math.Max(1, _maxDate.DayNumber - _minDate.DayNumber);
        var frac = (pos.X - _padLeft) / (ActualWidth - _padLeft - _padRight);
        var targetDay = _minDate.DayNumber + (int)Math.Round(frac * totalDays);
        var nearest = primary.OrderBy(p => Math.Abs(p.Date.DayNumber - targetDay)).First();
        ShowMarkerAt(nearest.Date);
    }

    /// <summary>Shared marker + tooltip rendering for pointer hover and keyboard focus.</summary>
    private void ShowMarkerAt(DateOnly date)
    {
        if (_series.Count == 0 || ActualWidth < 40) return;
        var primary = _series[0].Points;
        var nearest = primary.FirstOrDefault(p => p.Date == date);
        if (nearest == default) nearest = primary.OrderBy(p => Math.Abs(p.Date.DayNumber - date.DayNumber)).First();

        var x = X(nearest.Date, ActualWidth);
        _hoverLine.X1 = _hoverLine.X2 = x;
        _hoverLine.Y1 = _padTop;
        _hoverLine.Y2 = ActualHeight - _padBottom;
        _hoverLine.Visibility = Visibility.Visible;

        var privacy = AppSession.Current.PrivacyMode;
        var lines = new List<string> { nearest.Date.ToString("d", DisplayCulture()) };
        foreach (var s in _series)
        {
            var match = s.Points.FirstOrDefault(p => p.Date == nearest.Date);
            var value = match == default && s.Points.Count > 0
                ? s.Points.Where(p => p.Date <= nearest.Date).OrderByDescending(p => p.Date).FirstOrDefault()
                : match;
            if (value != default || s.Points.Any(p => p.Date == nearest.Date))
                lines.Add($"{s.Label}: {Money.Format(value.Value, true, privacy)}");
        }
        _tooltipText.Text = string.Join('\n', lines);
        _tooltip.Visibility = Visibility.Visible;
        Canvas.SetLeft(_tooltip, Math.Min(x + 10, Math.Max(0, ActualWidth - 170)));
        Canvas.SetTop(_tooltip, _padTop + 4);
    }

    private void HideHover()
    {
        _hoverLine.Visibility = Visibility.Collapsed;
        _tooltip.Visibility = Visibility.Collapsed;
    }
}
