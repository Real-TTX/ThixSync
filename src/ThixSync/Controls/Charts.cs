using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace ThixSync.Controls;

/// <summary>Drawing in the colours of the theme: a brush of MatDesktop by its key, in the light or dark variant shown now.</summary>
public abstract class ThemedDrawing : Control
{
    protected ThemedDrawing()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>The brush of a key of the theme ("Mat.Accent"), or null when there is none.</summary>
    protected IBrush? BrushOf(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    /// <summary>The colour of a key of the theme with an opacity (a soft area under a line).</summary>
    protected IBrush? SoftBrushOf(string key, double opacity) =>
        BrushOf(key) is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, opacity) : null;
}

/// <summary>
/// The speed of the last minute as a small area chart (the activity panel): one value per second, oldest first, scaled to the highest;
/// the line in the accent and the area below it in a tint of it. No axes – the big numbers beside it say the values.
/// </summary>
public sealed class Sparkline : ThemedDrawing
{
    private IReadOnlyList<double> _values = [];

    public Sparkline()
    {
        Height = 64;
    }

    public IReadOnlyList<double> Values
    {
        get => _values;
        set
        {
            _values = value ?? [];
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (BrushOf("Mat.Border") is { } baseline)
        {
            context.DrawLine(new Pen(baseline, 1), new Point(0, height - 0.5), new Point(width, height - 0.5));
        }

        if (_values.Count < 2 || width <= 0 || height <= 0)
        {
            return;
        }

        var highest = Math.Max(_values.Max(), 1);
        var step = width / (_values.Count - 1);
        var points = _values.Select((value, index) => new Point(index * step, height - 2 - (height - 6) * Math.Max(0, value) / highest)).ToList();

        var area = new StreamGeometry();
        using (var geometry = area.Open())
        {
            geometry.BeginFigure(new Point(0, height), true);
            foreach (var point in points)
            {
                geometry.LineTo(point);
            }

            geometry.LineTo(new Point(width, height));
            geometry.EndFigure(true);
        }

        var line = new StreamGeometry();
        using (var geometry = line.Open())
        {
            geometry.BeginFigure(points[0], false);
            foreach (var point in points.Skip(1))
            {
                geometry.LineTo(point);
            }

            geometry.EndFigure(false);
        }

        context.DrawGeometry(SoftBrushOf("Mat.Accent", 0.16), null, area);
        if (BrushOf("Mat.Accent") is { } accent)
        {
            context.DrawGeometry(null, new Pen(accent, 2, lineJoin: PenLineJoin.Round), line);
        }
    }
}

/// <summary>
/// Bars of values per day (the overview: bytes copied in the last 14 days): rounded bars in the accent, the day below each bar, the
/// highest value as a quiet label at the top left. Today's bar is the strong accent, the others a softer one.
/// </summary>
public sealed class BarChart : ThemedDrawing
{
    private IReadOnlyList<(string Label, double Value)> _bars = [];

    public BarChart()
    {
        Height = 180;
    }

    /// <summary>How the highest value is written ("2,3 GB").</summary>
    public Func<double, string> FormatValue { get; set; } = value => value.ToString("0", Formats.German);

    public IReadOnlyList<(string Label, double Value)> Bars
    {
        get => _bars;
        set
        {
            _bars = value ?? [];
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        const double labelHeight = 22;
        const double topSpace = 22;
        if (_bars.Count == 0 || width <= 0 || BrushOf("Mat.TextMuted") is not { } muted)
        {
            return;
        }

        var highest = Math.Max(_bars.Max(bar => bar.Value), 1);
        var chartHeight = height - labelHeight - topSpace;
        var slot = width / _bars.Count;
        var barWidth = Math.Min(28, slot * 0.6);
        var grid = BrushOf("Mat.Border");
        if (grid is not null)
        {
            var pen = new Pen(grid, 1, dashStyle: new DashStyle([3, 3], 0));
            foreach (var level in new[] { 0.0, 0.5, 1.0 })
            {
                var y = topSpace + chartHeight * (1 - level) + 0.5;
                context.DrawLine(level == 0 ? new Pen(grid, 1) : pen, new Point(0, y), new Point(width, y));
            }
        }

        var top = new FormattedText(FormatValue(highest), Formats.German, FlowDirection.LeftToRight, Typeface.Default, 11, muted);
        context.DrawText(top, new Point(0, 2));

        for (var index = 0; index < _bars.Count; index++)
        {
            var (label, value) = _bars[index];
            var x = slot * index + (slot - barWidth) / 2;
            var barHeight = value <= 0 ? 0 : Math.Max(3, chartHeight * value / highest);
            var isLast = index == _bars.Count - 1;
            var fill = isLast ? BrushOf("Mat.Accent") : SoftBrushOf("Mat.Accent", 0.45);
            if (barHeight > 0 && fill is not null)
            {
                context.DrawRectangle(fill, null, new RoundedRect(new Rect(x, topSpace + chartHeight - barHeight, barWidth, barHeight), 4, 4, 0, 0));
            }

            var text = new FormattedText(label, Formats.German, FlowDirection.LeftToRight, Typeface.Default, 11, muted);
            context.DrawText(text, new Point(slot * index + (slot - text.Width) / 2, height - labelHeight + 6));
        }
    }
}

/// <summary>
/// The direction of a job between its two boxes: ⇄ (two-way: two arrows, one each way) or → (mirror, backup), drawn in the accent.
/// </summary>
public sealed class SyncArrow : ThemedDrawing
{
    private bool _isTwoWay = true;

    public SyncArrow()
    {
        Width = 40;
        Height = 32;
    }

    public bool IsTwoWay
    {
        get => _isTwoWay;
        set
        {
            _isTwoWay = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        if (BrushOf("Mat.AccentText") is not { } accent)
        {
            return;
        }

        var pen = new Pen(accent, 2.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var width = Bounds.Width;
        var middle = Bounds.Height / 2;
        if (!_isTwoWay)
        {
            Arrow(context, pen, new Point(4, middle), new Point(width - 4, middle));
            return;
        }

        Arrow(context, pen, new Point(4, middle - 6), new Point(width - 4, middle - 6));
        Arrow(context, pen, new Point(width - 4, middle + 6), new Point(4, middle + 6));
    }

    private static void Arrow(DrawingContext context, Pen pen, Point from, Point to)
    {
        context.DrawLine(pen, from, to);
        var direction = to.X > from.X ? -1 : 1;
        context.DrawLine(pen, to, new Point(to.X + direction * 6, to.Y - 5));
        context.DrawLine(pen, to, new Point(to.X + direction * 6, to.Y + 5));
    }
}

/// <summary>
/// A turning ring: a job runs (the state of a card, the title bar, the activity). It turns only while it is shown; the pictures of the
/// snapshots show it at rest.
/// </summary>
public sealed class Spinner : ThemedDrawing
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private double _angle;

    public Spinner()
    {
        Width = Height = 20;
        _timer.Tick += (_, _) =>
        {
            _angle = (_angle + 12) % 360;
            InvalidateVisual();
        };
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    /// <summary>The key of the colour of the ring.</summary>
    public string ColorKey { get; set; } = "Mat.Accent";

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var thickness = Math.Max(2, size / 8);
        var radius = (size - thickness) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (SoftBrushOf(ColorKey, 0.22) is { } track)
        {
            context.DrawEllipse(null, new Pen(track, thickness), center, radius, radius);
        }

        if (BrushOf(ColorKey) is not { } brush)
        {
            return;
        }

        var start = _angle * Math.PI / 180;
        var end = start + Math.PI * 1.4;
        var arc = new StreamGeometry();
        using (var geometry = arc.Open())
        {
            geometry.BeginFigure(new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start)), false);
            geometry.ArcTo(new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end)), new Size(radius, radius), 0, true, SweepDirection.Clockwise);
            geometry.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(brush, thickness, lineCap: PenLineCap.Round), arc);
    }
}
