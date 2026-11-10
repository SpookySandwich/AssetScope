using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace AssetScope.App.Controls;

/// <summary>Tiny inline balance trend; green when up, red when down.</summary>
public sealed partial class Sparkline : UserControl
{
    private static readonly Color Up = Color.FromArgb(255, 0x0F, 0x7B, 0x0F);
    private static readonly Color Down = Color.FromArgb(255, 0xC4, 0x2B, 0x1C);

    private readonly Polyline _line = new()
    {
        StrokeThickness = 1.5,
        StrokeLineJoin = PenLineJoin.Round,
    };
    private readonly Grid _root = new();
    private IReadOnlyList<decimal> _values = [];

    public Sparkline()
    {
        _root.Children.Add(_line);
        Content = _root;
        Width = 72;
        Height = 26;
        SizeChanged += (_, _) => Redraw();
        // Decorative: the adjacent change badge carries the same information as
        // text, so screen readers should skip the drawing (SPEC §24.1).
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
            this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
    }

    public void SetValues(IReadOnlyList<decimal> values)
    {
        _values = values;
        Redraw();
    }

    private void Redraw()
    {
        _line.Points.Clear();
        if (_values.Count < 2 || ActualWidth < 10 || ActualHeight < 6)
        {
            _line.Stroke = null;
            return;
        }
        var min = _values.Min();
        var max = _values.Max();
        if (max == min) { max += 1; min -= 1; }
        var w = ActualWidth;
        var h = ActualHeight - 3;
        for (var i = 0; i < _values.Count; i++)
        {
            var x = i / (double)(_values.Count - 1) * w;
            var y = 1.5 + (1 - (double)((_values[i] - min) / (max - min))) * h;
            _line.Points.Add(new Windows.Foundation.Point(x, y));
        }
        _line.Stroke = new SolidColorBrush(_values[^1] >= _values[0] ? Up : Down);
    }
}
