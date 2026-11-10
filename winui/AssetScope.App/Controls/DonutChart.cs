using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace AssetScope.App.Controls;

public sealed record DonutSliceData(double Value, Brush Brush, string Label);

/// <summary>
/// Allocation donut (SPEC §16.6): ring segments plus center text. Callers own
/// the financial semantics; Home includes signed net-worth composition while
/// Investments passes only reconciled non-negative portfolio slices.
/// </summary>
public sealed partial class DonutChart : UserControl
{
    private readonly Grid _root = new();
    private readonly Canvas _canvas = new();
    private readonly StackPanel _center = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Spacing = 2,
    };
    private readonly TextBlock _centerTitle = new()
    {
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Opacity = 0.7,
    };
    private readonly TextBlock _centerValue = new()
    {
        FontSize = 18,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private IReadOnlyList<DonutSliceData> _slices = [];

    public double RingThickness { get; set; } = 26;

    public DonutChart()
    {
        _center.Children.Add(_centerTitle);
        _center.Children.Add(_centerValue);
        _root.Children.Add(_canvas);
        _root.Children.Add(_center);
        Content = _root;
        SizeChanged += (_, _) => Redraw();
    }

    public void SetData(IReadOnlyList<DonutSliceData> slices, string centerTitle, string centerValue)
    {
        _slices = slices.Where(s => s.Value > 0).ToList();
        _centerTitle.Text = centerTitle;
        _centerValue.Text = centerValue;
        // Textual equivalent for assistive tech (SPEC §24.1); the page legend
        // carries the amounts, so shares are enough here.
        var total = _slices.Sum(s => s.Value);
        var summary = total <= 0
            ? centerTitle
            : $"{centerTitle} {centerValue}: " + string.Join(", ",
                _slices.Select(s => $"{s.Label} {s.Value / total * 100:0}%"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, summary);
        Redraw();
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 40 || _slices.Count == 0) return;

        var cx = ActualWidth / 2;
        var cy = ActualHeight / 2;
        var outer = size / 2 - 2;
        var thickness = Math.Min(RingThickness, outer / 2);
        var radius = outer - thickness / 2; // stroke centerline
        var holeDiameter = Math.Max(0, 2 * (radius - thickness / 2) - 12);
        _center.MaxWidth = holeDiameter;
        _centerTitle.MaxWidth = holeDiameter;
        _centerValue.MaxWidth = holeDiameter;
        _centerValue.FontSize = holeDiameter < 86 ? 15 : 18;
        // XAML children are not clipped to Grid bounds by default. Explicitly
        // clip the chart so center text can never spill into the sibling legend.
        _root.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight),
        };

        var total = _slices.Sum(s => s.Value);
        if (total <= 0) return;

        if (_slices.Count == 1)
        {
            var ring = new Ellipse
            {
                Width = radius * 2 + thickness,
                Height = radius * 2 + thickness,
                Stroke = _slices[0].Brush,
                StrokeThickness = thickness,
            };
            Canvas.SetLeft(ring, cx - radius - thickness / 2);
            Canvas.SetTop(ring, cy - radius - thickness / 2);
            _canvas.Children.Add(ring);
            return;
        }

        // The reference uses crisp radial separators between slices. A narrow
        // angular gap preserves that separation without the rounded capsules
        // that made small slices look detached from the ring.
        const double gapDegrees = 0.8;
        var startAngle = -90.0;
        foreach (var slice in _slices)
        {
            var sweep = slice.Value / total * 360.0 - gapDegrees;
            if (sweep <= 0) { startAngle += slice.Value / total * 360.0; continue; }

            var a0 = startAngle * Math.PI / 180.0;
            var a1 = (startAngle + sweep) * Math.PI / 180.0;
            var p0 = new Windows.Foundation.Point(cx + radius * Math.Cos(a0), cy + radius * Math.Sin(a0));
            var p1 = new Windows.Foundation.Point(cx + radius * Math.Cos(a1), cy + radius * Math.Sin(a1));

            var figure = new PathFigure { StartPoint = p0 };
            figure.Segments.Add(new ArcSegment
            {
                Point = p1,
                Size = new Windows.Foundation.Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = sweep > 180,
            });

            _canvas.Children.Add(new Path
            {
                Data = new PathGeometry { Figures = { figure } },
                Stroke = slice.Brush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap = PenLineCap.Flat,
            });

            startAngle += slice.Value / total * 360.0;
        }
    }
}
