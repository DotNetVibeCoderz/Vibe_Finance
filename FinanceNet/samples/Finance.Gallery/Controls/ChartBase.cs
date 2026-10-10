using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Finance.Gallery.Controls;

/// <summary>
/// Shared plumbing for the Gallery's hand-drawn charts: plot-area layout, "nice" axis ticks,
/// grid, text and a crosshair that follows the pointer.
/// </summary>
public abstract class ChartBase : Control
{
    protected static readonly Typeface Font = new("Inter, Segoe UI");
    protected static readonly IPen GridPen = new Pen(Palette.Brush(Palette.LineColor, 150), 1, DashStyle.Dash);
    protected static readonly IPen AxisPen = new Pen(Palette.Line, 1);
    protected static readonly IPen CrossPen = new Pen(Palette.Brush(Palette.InkSoftColor, 120), 1, DashStyle.Dash);

    private Point? _pointer;

    protected ChartBase()
    {
        ClipToBounds = true;
        MinHeight = 160;
    }

    /// <summary>Optional title drawn in the top-left corner.</summary>
    public string? Title { get; set; }

    /// <summary>Pixel padding around the plot area: left, top, right, bottom.</summary>
    public Thickness PlotPadding { get; set; } = new(58, 28, 16, 30);

    protected Point? Pointer => _pointer;

    protected Rect PlotArea(Rect bounds) => new(
        bounds.X + PlotPadding.Left,
        bounds.Y + PlotPadding.Top,
        Math.Max(1, bounds.Width - PlotPadding.Left - PlotPadding.Right),
        Math.Max(1, bounds.Height - PlotPadding.Top - PlotPadding.Bottom));

    public void Refresh() => InvalidateVisual();

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        InvalidateVisual();
    }

    public sealed override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.DrawRectangle(Brushes.Transparent, null, bounds);
        if (!string.IsNullOrEmpty(Title))
            DrawText(ctx, Title!, new Point(PlotPadding.Left, 6), 12.5, Palette.Ink, FontWeight.SemiBold);
        RenderChart(ctx, bounds);
    }

    protected abstract void RenderChart(DrawingContext ctx, Rect bounds);

    // ------------------------------------------------------------------ helpers

    protected static FormattedText Text(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal)
        => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Font.FontFamily, FontStyle.Normal, weight), size, brush);

    protected static Size DrawText(DrawingContext ctx, string s, Point at, double size, IBrush brush,
        FontWeight weight = FontWeight.Normal, TextAnchor anchor = TextAnchor.Left)
    {
        var t = Text(s, size, brush, weight);
        var p = anchor switch
        {
            TextAnchor.Center => new Point(at.X - t.Width / 2, at.Y),
            TextAnchor.Right => new Point(at.X - t.Width, at.Y),
            _ => at,
        };
        ctx.DrawText(t, p);
        return new Size(t.Width, t.Height);
    }

    /// <summary>Tick values at 1/2/5 × 10^k spacing covering [min, max].</summary>
    public static double[] NiceTicks(double min, double max, int target = 6)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max)) return Array.Empty<double>();
        if (max - min < 1e-12) { min -= 1; max += 1; }
        double raw = (max - min) / Math.Max(1, target);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
        double start = Math.Ceiling(min / step) * step;
        var ticks = new List<double>();
        for (double v = start; v <= max + step * 1e-9; v += step) ticks.Add(Math.Abs(v) < step * 1e-9 ? 0 : v);
        return ticks.ToArray();
    }

    /// <summary>Expands a range by a fraction on both sides.</summary>
    protected static (double Min, double Max) Pad(double min, double max, double fraction = 0.06)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max)) return (0, 1);
        if (max - min < 1e-12) return (min - 1, max + 1);
        double d = (max - min) * fraction;
        return (min - d, max + d);
    }

    protected void DrawYAxis(DrawingContext ctx, Rect plot, double min, double max, Func<double, string> format, bool grid = true)
    {
        foreach (var v in NiceTicks(min, max, Math.Max(2, (int)(plot.Height / 48))))
        {
            double y = plot.Bottom - (v - min) / (max - min) * plot.Height;
            if (y < plot.Top - 0.5 || y > plot.Bottom + 0.5) continue;
            if (grid) ctx.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var t = Text(format(v), 10.5, Palette.InkFaint);
            ctx.DrawText(t, new Point(plot.Left - t.Width - 8, y - t.Height / 2));
        }
    }

    protected void DrawXAxisNumeric(DrawingContext ctx, Rect plot, double min, double max, Func<double, string> format, bool grid = false)
    {
        foreach (var v in NiceTicks(min, max, Math.Max(2, (int)(plot.Width / 90))))
        {
            double x = plot.Left + (v - min) / (max - min) * plot.Width;
            if (x < plot.Left - 0.5 || x > plot.Right + 0.5) continue;
            if (grid) ctx.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            DrawText(ctx, format(v), new Point(x, plot.Bottom + 7), 10.5, Palette.InkFaint, anchor: TextAnchor.Center);
        }
    }

    /// <summary>Date labels for index-based x axes.</summary>
    protected void DrawXAxisDates(DrawingContext ctx, Rect plot, IReadOnlyList<DateTime> dates, int first, int last)
    {
        if (dates.Count == 0 || last <= first) return;
        int count = last - first + 1;
        int labels = Math.Max(2, (int)(plot.Width / 95));
        int step = Math.Max(1, count / labels);
        bool intraday = count > 1 && (dates[Math.Min(last, first + 1)] - dates[first]).TotalHours < 20;
        for (int i = first; i <= last; i += step)
        {
            double x = plot.Left + (i - first + 0.5) / count * plot.Width;
            string s = intraday ? dates[i].ToString("HH:mm", CultureInfo.InvariantCulture) : dates[i].ToString("MMM yy", CultureInfo.InvariantCulture);
            DrawText(ctx, s, new Point(x, plot.Bottom + 7), 10.5, Palette.InkFaint, anchor: TextAnchor.Center);
        }
    }

    protected static void DrawPolyline(DrawingContext ctx, IPen pen, IReadOnlyList<Point> pts)
    {
        if (pts.Count < 2) return;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], false);
            for (int i = 1; i < pts.Count; i++) c.LineTo(pts[i]);
            c.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, g);
    }

    protected static void FillUnder(DrawingContext ctx, IReadOnlyList<Point> pts, double baseline, Color color, byte topAlpha = 90)
    {
        if (pts.Count < 2) return;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(pts[0].X, baseline), true);
            foreach (var p in pts) c.LineTo(p);
            c.LineTo(new Point(pts[^1].X, baseline));
            c.EndFigure(true);
        }
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(topAlpha, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            },
        };
        ctx.DrawGeometry(brush, null, g);
    }

    /// <summary>A small rounded tooltip box with lines of text.</summary>
    protected static void DrawTooltip(DrawingContext ctx, Rect bounds, Point at, IReadOnlyList<(string Text, IBrush Brush)> lines)
    {
        if (lines.Count == 0) return;
        var texts = lines.Select(l => Text(l.Text, 11, l.Brush)).ToList();
        double w = texts.Max(t => t.Width) + 18, h = texts.Sum(t => t.Height + 2) + 12;
        double x = at.X + 14, y = at.Y + 12;
        if (x + w > bounds.Right - 4) x = at.X - w - 14;
        if (y + h > bounds.Bottom - 4) y = at.Y - h - 12;
        var box = new Rect(x, y, w, h);
        ctx.DrawRectangle(Palette.Brush(Palette.RaisedColor, 240), new Pen(Palette.Line, 1), box, 7, 7);
        double ty = y + 6;
        foreach (var t in texts)
        {
            ctx.DrawText(t, new Point(x + 9, ty));
            ty += t.Height + 2;
        }
    }

    public static string Compact(double v)
    {
        double a = Math.Abs(v);
        if (a >= 1e9) return (v / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + "B";
        if (a >= 1e6) return (v / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (a >= 1e4) return (v / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        if (a >= 100) return v.ToString("0", CultureInfo.InvariantCulture);
        if (a >= 1) return v.ToString("0.##", CultureInfo.InvariantCulture);
        return v.ToString("0.####", CultureInfo.InvariantCulture);
    }

    public static string Percent2(double v) => (v * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    public static string Percent(double v) => (v * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}

public enum TextAnchor { Left, Center, Right }
