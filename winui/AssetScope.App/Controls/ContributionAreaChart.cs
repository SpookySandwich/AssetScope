using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Globalization;
using Windows.UI;
using AssetScope.App.Services;
using AssetScope.Core;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace AssetScope.App.Controls;

public sealed record ContributionSeriesData(
    string Label,
    IReadOnlyList<(DateOnly Date, decimal Value)> Points,
    Color Color);

/// <summary>
/// Diverging stacked-area infographic for institution net-worth contribution.
/// Positive and negative institutions are stacked independently around a true
/// zero baseline, so a liability never visually cancels an asset layer.
/// </summary>
public sealed class ContributionAreaChart : UserControl
{
    private readonly Canvas _canvas = new();
    private readonly Border _tooltip;
    private readonly TextBlock _tooltipText = new() { FontSize = 11 };
    private readonly Line _hoverLine;
    private IReadOnlyList<ContributionSeriesData> _series = [];
    private List<DateOnly> _dates = [];
    private decimal _minValue = -1;
    private decimal _maxValue = 1;
    private const double PadLeft = 50;
    private const double PadRight = 8;
    private const double PadTop = 8;
    private const double PadBottom = 26;

    public ContributionAreaChart()
    {
        var root = new Grid();
        _hoverLine = new Line
        {
            Stroke = new SolidColorBrush(Color.FromArgb(150, 100, 100, 100)),
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
            Padding = new Thickness(9, 5, 9, 5),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _tooltipText,
        };
        root.Children.Add(_canvas);
        Content = root;
        MinHeight = 140;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        SizeChanged += (_, _) => Redraw();
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => HideTooltip();
        KeyDown += OnKeyDown;
        GotFocus += (_, _) => ShowTooltipAt(Math.Max(0, _dates.Count - 1));
        LostFocus += (_, _) => HideTooltip();
    }

    public void SetSeries(IReadOnlyList<ContributionSeriesData> series)
    {
        _series = series
            .Where(item => item.Points.Count >= 2)
            .Select(item => item with { Points = item.Points.OrderBy(point => point.Date).ToList() })
            .ToList();
        _dates = _series.SelectMany(item => item.Points.Select(point => point.Date)).Distinct().Order().ToList();
        if (_series.Count > 0 && _dates.Count > 0)
        {
            var privacy = AppSession.Current.PrivacyMode;
            var start = _series.Sum(item => ValueAt(item, _dates[0]));
            var end = _series.Sum(item => ValueAt(item, _dates[^1]));
            AutomationProperties.SetName(this,
                $"{Display.S("InstitutionContributionTitle")}: {_dates[0]:MMM yyyy} "
                + $"{Money.Format(start, false, privacy)} to {_dates[^1]:MMM yyyy} "
                + $"{Money.Format(end, false, privacy)}. Use arrow keys to explore.");
        }
        Redraw();
    }

    private static decimal ValueAt(ContributionSeriesData series, DateOnly date)
    {
        var exact = series.Points.FirstOrDefault(point => point.Date == date);
        if (exact != default || series.Points.Any(point => point.Date == date)) return exact.Value;
        var prior = series.Points.LastOrDefault(point => point.Date <= date);
        return prior == default ? 0 : prior.Value;
    }

    private double X(int index, double width) =>
        PadLeft + index / (double)Math.Max(1, _dates.Count - 1) * (width - PadLeft - PadRight);

    private double Y(decimal value, double height)
    {
        var fraction = (double)((value - _minValue) / (_maxValue - _minValue));
        return PadTop + (1 - fraction) * (height - PadTop - PadBottom);
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 120 || height < 80 || _series.Count == 0 || _dates.Count < 2) return;

        var positive = new decimal[_dates.Count];
        var negative = new decimal[_dates.Count];
        foreach (var item in _series)
        {
            for (var i = 0; i < _dates.Count; i++)
            {
                var value = ValueAt(item, _dates[i]);
                if (value >= 0) positive[i] += value;
                else negative[i] += value;
            }
        }
        var rawMin = Math.Min(0, negative.Min());
        var rawMax = Math.Max(0, positive.Max());
        var span = Math.Max(1, rawMax - rawMin);
        _minValue = rawMin - span * 0.06m;
        _maxValue = rawMax + span * 0.06m;
        if (rawMin == 0) _minValue = -span * 0.12m;
        if (rawMax == 0) _maxValue = span * 0.12m;

        DrawAxes(width, height);
        DrawAreas(width, height);
        DrawFlatSeriesMarkers(width, height);
        _canvas.Children.Add(_hoverLine);
        _canvas.Children.Add(_tooltip);
    }

    private void DrawAxes(double width, double height)
    {
        var gridBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        var labelBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var privacy = AppSession.Current.PrivacyMode;
        const int intervals = 4;
        for (var i = 0; i <= intervals; i++)
        {
            var value = _minValue + (_maxValue - _minValue) * i / intervals;
            var y = Y(value, height);
            _canvas.Children.Add(new Line
            {
                X1 = PadLeft,
                X2 = width - PadRight,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1,
                Opacity = 0.82,
            });
            var label = new TextBlock
            {
                Text = privacy ? "•••" : Money.FormatCompact(value),
                FontSize = 10,
                Foreground = labelBrush,
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 7);
            _canvas.Children.Add(label);
        }

        var zeroY = Y(0, height);
        _canvas.Children.Add(new Line
        {
            X1 = PadLeft,
            X2 = width - PadRight,
            Y1 = zeroY,
            Y2 = zeroY,
            Stroke = new SolidColorBrush(Color.FromArgb(185, 90, 90, 90)),
            StrokeThickness = 1.25,
        });

        var totalDays = Math.Max(1, _dates[^1].DayNumber - _dates[0].DayNumber);
        var preferredTicks = totalDays > 200 ? 11 : totalDays > 60 ? 6 : 4;
        var plotWidth = Math.Max(1, width - PadLeft - PadRight);
        var minimumLabelWidth = totalDays > 200 ? 88d : 64d;
        var widthLimitedTicks = Math.Max(1, (int)Math.Floor(plotWidth / minimumLabelWidth));
        var tickCount = Math.Min(Math.Min(preferredTicks, widthLimitedTicks), _dates.Count - 1);
        for (var i = 0; i <= tickCount; i++)
        {
            var index = (int)Math.Round(i * (_dates.Count - 1d) / tickCount);
            var date = _dates[index];
            var label = new TextBlock
            {
                Text = FormatDate(date, totalDays),
                FontSize = 10,
                Foreground = labelBrush,
            };
            var x = X(index, width);
            var estimatedWidth = totalDays > 200 ? 70d : 54d;
            Canvas.SetLeft(label, Math.Clamp(x - estimatedWidth / 2, 0, width - estimatedWidth));
            Canvas.SetTop(label, height - PadBottom + 6);
            _canvas.Children.Add(label);
        }
    }

    private void DrawAreas(double width, double height)
    {
        var positiveBase = new decimal[_dates.Count];
        var negativeBase = new decimal[_dates.Count];
        foreach (var item in _series)
        {
            var lower = new decimal[_dates.Count];
            var upper = new decimal[_dates.Count];
            for (var i = 0; i < _dates.Count; i++)
            {
                var value = ValueAt(item, _dates[i]);
                if (value >= 0)
                {
                    lower[i] = positiveBase[i];
                    upper[i] = positiveBase[i] + value;
                    positiveBase[i] = upper[i];
                }
                else
                {
                    lower[i] = negativeBase[i];
                    upper[i] = negativeBase[i] + value;
                    negativeBase[i] = upper[i];
                }
            }

            var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(X(0, width), Y(lower[0], height)) };
            var polygon = new PolyLineSegment();
            for (var i = 0; i < _dates.Count; i++)
                polygon.Points.Add(new Windows.Foundation.Point(X(i, width), Y(upper[i], height)));
            for (var i = _dates.Count - 1; i >= 0; i--)
                polygon.Points.Add(new Windows.Foundation.Point(X(i, width), Y(lower[i], height)));
            figure.Segments.Add(polygon);
            figure.IsClosed = true;
            _canvas.Children.Add(new Path
            {
                Data = new PathGeometry { Figures = { figure } },
                Stroke = new SolidColorBrush(item.Color),
                StrokeThickness = 1.25,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Color.FromArgb(242, item.Color.R, item.Color.G, item.Color.B), Offset = 0 },
                        new GradientStop { Color = Color.FromArgb(220, item.Color.R, item.Color.G, item.Color.B), Offset = 1 },
                    },
                },
            });
        }
    }

    /// <summary>
    /// A zero-width area is mathematically invisible. Show a small endpoint
    /// marker for every all-zero institution so its brand color remains
    /// discoverable without inventing a non-zero contribution.
    /// </summary>
    private void DrawFlatSeriesMarkers(double width, double height)
    {
        var flat = _series.Where(item => _dates.All(date => ValueAt(item, date) == 0)).ToList();
        var zeroY = Y(0, height);
        for (var i = 0; i < flat.Count; i++)
        {
            var marker = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(flat[i].Color),
                Stroke = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(marker, Math.Max(PadLeft, width - PadRight - 8 - i * 10));
            Canvas.SetTop(marker, zeroY - 3.5);
            _canvas.Children.Add(marker);
        }
    }

    private static string FormatDate(DateOnly date, int totalDays)
    {
        var language = AppSession.Current.Settings.Language;
        var isChinese = language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            || (language.Equals("system", StringComparison.OrdinalIgnoreCase)
                && Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride
                    .StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        return isChinese
            ? totalDays > 200 ? $"{date.Year}年{date.Month}月" : $"{date.Month}月{date.Day}日"
            : date.ToString(totalDays > 200 ? "MMM yyyy" : "MMM d", CultureInfo.GetCultureInfo("en-US"));
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dates.Count == 0 || ActualWidth <= PadLeft + PadRight) return;
        var x = e.GetCurrentPoint(_canvas).Position.X;
        if (x < PadLeft || x > ActualWidth - PadRight) { HideTooltip(); return; }
        var fraction = (x - PadLeft) / (ActualWidth - PadLeft - PadRight);
        ShowTooltipAt((int)Math.Round(fraction * (_dates.Count - 1)));
    }

    private int _focusIndex;

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_dates.Count == 0) return;
        if (e.Key == Windows.System.VirtualKey.Left) _focusIndex--;
        else if (e.Key == Windows.System.VirtualKey.Right) _focusIndex++;
        else if (e.Key == Windows.System.VirtualKey.Home) _focusIndex = 0;
        else if (e.Key == Windows.System.VirtualKey.End) _focusIndex = _dates.Count - 1;
        else return;
        ShowTooltipAt(Math.Clamp(_focusIndex, 0, _dates.Count - 1));
        e.Handled = true;
    }

    private void ShowTooltipAt(int index)
    {
        if (_dates.Count == 0) return;
        index = Math.Clamp(index, 0, _dates.Count - 1);
        _focusIndex = index;
        var x = X(index, ActualWidth);
        _hoverLine.X1 = _hoverLine.X2 = x;
        _hoverLine.Y1 = PadTop;
        _hoverLine.Y2 = ActualHeight - PadBottom;
        _hoverLine.Visibility = Visibility.Visible;
        var privacy = AppSession.Current.PrivacyMode;
        var totalDays = Math.Max(1, _dates[^1].DayNumber - _dates[0].DayNumber);
        var lines = new List<string> { FormatDate(_dates[index], totalDays) };
        lines.AddRange(_series.Select(item =>
            $"{item.Label}: {Money.FormatSigned(ValueAt(item, _dates[index]), false, privacy)}"));
        _tooltipText.Text = string.Join('\n', lines);
        _tooltip.Visibility = Visibility.Visible;
        Canvas.SetLeft(_tooltip, Math.Clamp(x + 8, 0, Math.Max(0, ActualWidth - 190)));
        Canvas.SetTop(_tooltip, PadTop + 3);
    }

    private void HideTooltip()
    {
        _hoverLine.Visibility = Visibility.Collapsed;
        _tooltip.Visibility = Visibility.Collapsed;
    }
}
