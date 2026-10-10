using Avalonia;
using Avalonia.Media;

namespace Finance.Gallery.Controls;

/// <summary>A line (or area) series. X may be omitted, in which case the index is used.</summary>
public sealed class LineSeries
{
    public LineSeries(string name, double[] y, Color? color = null)
    {
        Name = name;
        Y = y;
        Color = color;
    }

    public string Name { get; }
    public double[] Y { get; }
    public double[]? X { get; init; }
    public Color? Color { get; set; }
    public double Thickness { get; init; } = 2;
    public bool Fill { get; init; }
    public bool Dashed { get; init; }
}

/// <summary>Multi-series line/area chart with crosshair read-out.</summary>
public sealed class LineChart : ChartBase
{
    public List<LineSeries> Series { get; } = new();

    /// <summary>Optional dates for an index-based x axis.</summary>
    public IReadOnlyList<DateTime>? Dates { get; set; }

    public Func<double, string> YFormat { get; set; } = Compact;
    public Func<double, string> XFormat { get; set; } = Compact;

    /// <summary>Horizontal reference lines (value, label, colour).</summary>
    public List<(double Value, string Label, Color Color)> ReferenceLines { get; } = new();

    public bool ShowLegend { get; set; } = true;
    public double? YMinOverride { get; set; }
    public double? YMaxOverride { get; set; }

    public void SetSeries(params LineSeries[] series)
    {
        Series.Clear();
        Series.AddRange(series);
        InvalidateVisual();
    }

    protected override void RenderChart(DrawingContext ctx, Rect bounds)
    {
        var plot = PlotArea(bounds);
        if (Series.Count == 0 || Series.All(s => s.Y.Length == 0)) return;

        double xmin = double.PositiveInfinity, xmax = double.NegativeInfinity;
        double ymin = double.PositiveInfinity, ymax = double.NegativeInfinity;
        foreach (var s in Series)
            for (int i = 0; i < s.Y.Length; i++)
            {
                double y = s.Y[i];
                if (!double.IsFinite(y)) continue;
                double x = s.X?[i] ?? i;
                xmin = Math.Min(xmin, x);
                xmax = Math.Max(xmax, x);
                ymin = Math.Min(ymin, y);
                ymax = Math.Max(ymax, y);
            }
        foreach (var r in ReferenceLines) { ymin = Math.Min(ymin, r.Value); ymax = Math.Max(ymax, r.Value); }
        (ymin, ymax) = Pad(ymin, ymax);
        if (YMinOverride is { } a) ymin = a;
        if (YMaxOverride is { } b) ymax = b;
        if (xmax <= xmin) xmax = xmin + 1;

        DrawYAxis(ctx, plot, ymin, ymax, YFormat);
        if (Dates is { Count: > 0 } dates) DrawXAxisDates(ctx, plot, dates, 0, dates.Count - 1);
        else DrawXAxisNumeric(ctx, plot, xmin, xmax, XFormat);

        Point Map(double x, double y) => new(plot.Left + (x - xmin) / (xmax - xmin) * plot.Width, plot.Bottom - (y - ymin) / (ymax - ymin) * plot.Height);

        using (ctx.PushClip(plot))
        {
            foreach (var r in ReferenceLines)
            {
                var p = Map(xmin, r.Value);
                ctx.DrawLine(new Pen(Palette.Brush(r.Color, 200), 1.2, DashStyle.Dash), new Point(plot.Left, p.Y), new Point(plot.Right, p.Y));
            }

            for (int si = 0; si < Series.Count; si++)
            {
                var s = Series[si];
                var color = s.Color ?? Palette.SeriesColor(si);
                var pts = new List<Point>(s.Y.Length);
                // Decimate to roughly two points per pixel to keep long series cheap.
                int stride = Math.Max(1, s.Y.Length / (int)Math.Max(1, plot.Width * 2));
                for (int i = 0; i < s.Y.Length; i += stride)
                {
                    if (!double.IsFinite(s.Y[i]))
                    {
                        Flush(ctx, s, color, pts, plot);
                        continue;
                    }
                    pts.Add(Map(s.X?[i] ?? i, s.Y[i]));
                }
                Flush(ctx, s, color, pts, plot);
            }
        }

        foreach (var r in ReferenceLines)
        {
            var p = Map(xmin, r.Value);
            DrawText(ctx, r.Label, new Point(plot.Right - 4, p.Y - 15), 10, Palette.Brush(r.Color), anchor: TextAnchor.Right);
        }

        if (ShowLegend && Series.Count > 1)
        {
            double lx = plot.Left + 8, ly = plot.Top + 6;
            for (int si = 0; si < Series.Count; si++)
            {
                var color = Series[si].Color ?? Palette.SeriesColor(si);
                ctx.DrawRectangle(Palette.Brush(color), null, new Rect(lx, ly + 4, 10, 3), 1.5, 1.5);
                var sz = DrawText(ctx, Series[si].Name, new Point(lx + 14, ly - 1), 10.5, Palette.InkSoft);
                lx += sz.Width + 28;
                if (lx > plot.Right - 80) { lx = plot.Left + 8; ly += 16; }
            }
        }

        if (Pointer is { } ptr && plot.Contains(ptr))
        {
            ctx.DrawLine(CrossPen, new Point(ptr.X, plot.Top), new Point(ptr.X, plot.Bottom));
            double xv = xmin + (ptr.X - plot.Left) / plot.Width * (xmax - xmin);
            var lines = new List<(string, IBrush)>();
            if (Dates is { Count: > 0 } d)
            {
                int idx = Math.Clamp((int)Math.Round(xv), 0, d.Count - 1);
                lines.Add((d[idx].ToString("yyyy-MM-dd"), Palette.InkSoft));
            }
            else lines.Add((XFormat(xv), Palette.InkSoft));
            for (int si = 0; si < Series.Count; si++)
            {
                var s = Series[si];
                int idx = Nearest(s, xv);
                if (idx < 0 || !double.IsFinite(s.Y[idx])) continue;
                var color = s.Color ?? Palette.SeriesColor(si);
                var p = Map(s.X?[idx] ?? idx, s.Y[idx]);
                ctx.DrawEllipse(Palette.Brush(color), new Pen(Palette.Ground, 2), p, 4, 4);
                lines.Add(($"{s.Name}: {YFormat(s.Y[idx])}", Palette.Brush(color)));
            }
            DrawTooltip(ctx, bounds, ptr, lines);
        }
    }

    private static void Flush(DrawingContext ctx, LineSeries s, Color color, List<Point> pts, Rect plot)
    {
        if (pts.Count < 2) { pts.Clear(); return; }
        if (s.Fill) FillUnder(ctx, pts, plot.Bottom, color);
        var pen = new Pen(Palette.Brush(color), s.Thickness, s.Dashed ? DashStyle.Dash : null, PenLineCap.Round, PenLineJoin.Round);
        DrawPolyline(ctx, pen, pts);
        pts.Clear();
    }

    private static int Nearest(LineSeries s, double x)
    {
        if (s.Y.Length == 0) return -1;
        if (s.X is null) return Math.Clamp((int)Math.Round(x), 0, s.Y.Length - 1);
        int best = 0;
        double bd = double.PositiveInfinity;
        for (int i = 0; i < s.X.Length; i++)
        {
            double d = Math.Abs(s.X[i] - x);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }
}
