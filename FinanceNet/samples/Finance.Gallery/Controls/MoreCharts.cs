using Avalonia;
using Avalonia.Media;
using Finance.Core.Market;

namespace Finance.Gallery.Controls;

/// <summary>A labelled point for scatter charts.</summary>
public sealed record ScatterPoint(double X, double Y, string? Label = null, Color? Color = null, double Radius = 3);

/// <summary>Scatter chart with optional connecting curves (e.g. efficient frontier) and highlighted points.</summary>
public sealed class ScatterChart : ChartBase
{
    public List<ScatterPoint> Points { get; } = new();
    public List<ScatterPoint> Highlights { get; } = new();
    public List<LineSeries> Curves { get; } = new();
    public Func<double, string> XFormat { get; set; } = Percent;
    public Func<double, string> YFormat { get; set; } = Percent;
    public string XLabel { get; set; } = "";
    public string YLabel { get; set; } = "";

    protected override void RenderChart(DrawingContext ctx, Rect bounds)
    {
        var plot = PlotArea(bounds);
        var all = Points.Concat(Highlights).Select(p => (p.X, p.Y))
            .Concat(Curves.SelectMany(c => c.Y.Select((y, i) => (c.X![i], y)))).Where(p => double.IsFinite(p.Item1) && double.IsFinite(p.Item2)).ToList();
        if (all.Count == 0) return;
        var (xmin, xmax) = Pad(all.Min(p => p.Item1), all.Max(p => p.Item1), 0.08);
        var (ymin, ymax) = Pad(all.Min(p => p.Item2), all.Max(p => p.Item2), 0.08);
        DrawYAxis(ctx, plot, ymin, ymax, YFormat);
        DrawXAxisNumeric(ctx, plot, xmin, xmax, XFormat, grid: true);
        if (XLabel.Length > 0) DrawText(ctx, XLabel, new Point(plot.Right, plot.Bottom - 16), 10.5, Palette.InkSoft, anchor: TextAnchor.Right);
        if (YLabel.Length > 0) DrawText(ctx, YLabel, new Point(plot.Left + 6, plot.Top + 2), 10.5, Palette.InkSoft);
        Point Map(double x, double y) => new(plot.Left + (x - xmin) / (xmax - xmin) * plot.Width, plot.Bottom - (y - ymin) / (ymax - ymin) * plot.Height);

        using (ctx.PushClip(plot))
        {
            foreach (var p in Points)
                ctx.DrawEllipse(Palette.Brush(p.Color ?? Palette.SkyColor, 110), null, Map(p.X, p.Y), p.Radius, p.Radius);
            for (int ci = 0; ci < Curves.Count; ci++)
            {
                var c = Curves[ci];
                var pts = c.Y.Select((y, i) => Map(c.X![i], y)).ToList();
                DrawPolyline(ctx, new Pen(Palette.Brush(c.Color ?? Palette.GoldColor), c.Thickness, c.Dashed ? DashStyle.Dash : null, PenLineCap.Round, PenLineJoin.Round), pts);
            }
        }
        foreach (var h in Highlights)
        {
            var p = Map(h.X, h.Y);
            var color = h.Color ?? Palette.AccentColor;
            ctx.DrawEllipse(Palette.Brush(color, 60), null, p, h.Radius + 6, h.Radius + 6);
            ctx.DrawEllipse(Palette.Brush(color), new Pen(Palette.Ground, 2), p, h.Radius + 1, h.Radius + 1);
            if (h.Label is { } l) DrawText(ctx, l, new Point(p.X + 10, p.Y - 18), 11, Palette.Brush(color), FontWeight.SemiBold);
        }
        if (Pointer is { } ptr && plot.Contains(ptr))
        {
            double xv = xmin + (ptr.X - plot.Left) / plot.Width * (xmax - xmin);
            double yv = ymax - (ptr.Y - plot.Top) / plot.Height * (ymax - ymin);
            DrawTooltip(ctx, bounds, ptr, new[] { ($"{XLabel} {XFormat(xv)}", Palette.InkSoft), ($"{YLabel} {YFormat(yv)}", Palette.Ink) });
        }
    }
}

/// <summary>Labelled matrix heatmap (correlations, IC tables) with a diverging palette.</summary>
public sealed class HeatmapChart : ChartBase
{
    public double[,] Values { get; set; } = new double[0, 0];
    public string[] RowLabels { get; set; } = Array.Empty<string>();
    public string[] ColumnLabels { get; set; } = Array.Empty<string>();
    public double Scale { get; set; } = 1.0;
    public bool ShowValues { get; set; } = true;

    public HeatmapChart() => PlotPadding = new Thickness(70, 28, 12, 34);

    protected override void RenderChart(DrawingContext ctx, Rect bounds)
    {
        int r = Values.GetLength(0), c = Values.GetLength(1);
        if (r == 0 || c == 0) return;
        var plot = PlotArea(bounds);
        double cw = plot.Width / c, ch = plot.Height / r;
        for (int i = 0; i < r; i++)
        {
            if (i < RowLabels.Length)
                DrawText(ctx, RowLabels[i], new Point(plot.Left - 8, plot.Top + i * ch + ch / 2 - 7), 10.5, Palette.InkSoft, anchor: TextAnchor.Right);
            for (int j = 0; j < c; j++)
            {
                double v = Values[i, j];
                var cell = new Rect(plot.Left + j * cw + 1, plot.Top + i * ch + 1, cw - 2, ch - 2);
                ctx.DrawRectangle(new SolidColorBrush(Palette.Diverging(v / Scale)), null, cell, 4, 4);
                if (ShowValues && cw > 34 && ch > 16)
                    DrawText(ctx, v.ToString("0.00"), new Point(cell.Center.X, cell.Center.Y - 7), 10.5,
                        Math.Abs(v / Scale) > 0.55 ? Brushes.White : Palette.InkSoft, anchor: TextAnchor.Center);
            }
        }
        for (int j = 0; j < c && j < ColumnLabels.Length; j++)
            DrawText(ctx, ColumnLabels[j], new Point(plot.Left + j * cw + cw / 2, plot.Bottom + 8), 10.5, Palette.InkSoft, anchor: TextAnchor.Center);
    }
}

/// <summary>Histogram with vertical markers (VaR / CVaR) and an optional density overlay.</summary>
public sealed class HistogramChart : ChartBase
{
    public double[] Values { get; set; } = Array.Empty<double>();
    public int Bins { get; set; } = 60;
    public List<(double Value, string Label, Color Color)> Markers { get; } = new();
    public Func<double, double>? Density { get; set; }
    public Func<double, string> XFormat { get; set; } = Percent;

    /// <summary>Values below this threshold are drawn in the "loss tail" colour.</summary>
    public double? TailThreshold { get; set; }

    protected override void RenderChart(DrawingContext ctx, Rect bounds)
    {
        var finite = Values.Where(double.IsFinite).ToArray();
        if (finite.Length == 0) return;
        var plot = PlotArea(bounds);
        double min = finite.Min(), max = finite.Max();
        if (max - min < 1e-12) max = min + 1;
        double w = (max - min) / Bins;
        var counts = new int[Bins];
        foreach (var v in finite) counts[Math.Clamp((int)((v - min) / w), 0, Bins - 1)]++;
        double peak = counts.Max();
        double densityScale = finite.Length * w;
        if (Density is { } dens)
            for (int i = 0; i <= 100; i++) peak = Math.Max(peak, dens(min + (max - min) * i / 100) * densityScale);
        peak *= 1.08;

        DrawYAxis(ctx, plot, 0, peak / finite.Length, v => Percent(v));
        DrawXAxisNumeric(ctx, plot, min, max, XFormat);
        double bw = plot.Width / Bins;
        for (int i = 0; i < Bins; i++)
        {
            double h = counts[i] / peak * plot.Height;
            double left = min + i * w;
            bool tail = TailThreshold is { } t && left + w <= t;
            var color = tail ? Palette.DownColor : Palette.AccentColor;
            ctx.DrawRectangle(Palette.Brush(color, (byte)(tail ? 220 : 170)), null, new Rect(plot.Left + i * bw + 0.5, plot.Bottom - h, Math.Max(1, bw - 1), h), 1.5, 1.5);
        }
        if (Density is { } d)
        {
            var pts = Enumerable.Range(0, 161).Select(i =>
            {
                double x = min + (max - min) * i / 160;
                return new Point(plot.Left + (x - min) / (max - min) * plot.Width, plot.Bottom - d(x) * densityScale / peak * plot.Height);
            }).ToList();
            DrawPolyline(ctx, new Pen(Palette.Gold, 2), pts);
        }
        int row = 0;
        foreach (var m in Markers)
        {
            double x = plot.Left + (m.Value - min) / (max - min) * plot.Width;
            ctx.DrawLine(new Pen(Palette.Brush(m.Color), 1.6, DashStyle.Dash), new Point(x, plot.Top), new Point(x, plot.Bottom));
            DrawText(ctx, $"{m.Label} {XFormat(m.Value)}", new Point(x + 5, plot.Top + 4 + row++ * 15), 10.5, Palette.Brush(m.Color), FontWeight.SemiBold);
        }
    }
}

/// <summary>Vertical or horizontal bar chart with signed colouring.</summary>
public sealed class BarChart : ChartBase
{
    public string[] Labels { get; set; } = Array.Empty<string>();
    public double[] Values { get; set; } = Array.Empty<double>();
    public bool Horizontal { get; set; }
    public Func<double, string> Format { get; set; } = Compact;
    public Color? BarColor { get; set; }

    protected override void RenderChart(DrawingContext ctx, Rect bounds)
    {
        int n = Values.Length;
        if (n == 0) return;
        if (Horizontal) PlotPadding = new Thickness(Math.Min(150, 12 + Labels.DefaultIfEmpty("").Max(l => l.Length) * 6.6), PlotPadding.Top, 56, 16);
        var plot = PlotArea(bounds);
        double lo = Math.Min(0, Values.Min()), hi = Math.Max(0, Values.Max());
        (lo, hi) = (lo < 0 ? lo * 1.1 : 0, hi * 1.1 + (hi == 0 ? 1e-9 : 0));
        if (hi - lo < 1e-12) hi = lo + 1;
        if (!Horizontal) DrawYAxis(ctx, plot, lo, hi, Format);
        double slot = (Horizontal ? plot.Height : plot.Width) / n;
        double thick = Math.Max(2, slot * 0.62);
        for (int i = 0; i < n; i++)
        {
            double v = Values[i];
            var color = BarColor ?? (v >= 0 ? Palette.TealColor : Palette.DownColor);
            if (Horizontal)
            {
                double zero = plot.Left + (0 - lo) / (hi - lo) * plot.Width;
                double end = plot.Left + (v - lo) / (hi - lo) * plot.Width;
                double y = plot.Top + i * slot + (slot - thick) / 2;
                ctx.DrawRectangle(Palette.Brush(color, 220), null, new Rect(Math.Min(zero, end), y, Math.Abs(end - zero), thick), 3, 3);
                if (i < Labels.Length) DrawText(ctx, Labels[i], new Point(plot.Left - 8, y + thick / 2 - 7), 10.5, Palette.InkSoft, anchor: TextAnchor.Right);
                DrawText(ctx, Format(v), new Point(Math.Max(zero, end) + 5, y + thick / 2 - 7), 10.5, Palette.Ink);
            }
            else
            {
                double zero = plot.Bottom - (0 - lo) / (hi - lo) * plot.Height;
                double top = plot.Bottom - (v - lo) / (hi - lo) * plot.Height;
                double x = plot.Left + i * slot + (slot - thick) / 2;
                ctx.DrawRectangle(Palette.Brush(color, 220), null, new Rect(x, Math.Min(zero, top), thick, Math.Abs(zero - top)), 3, 3);
                if (i < Labels.Length && slot > 26)
                    DrawText(ctx, Labels[i], new Point(x + thick / 2, plot.Bottom + 7), 10, Palette.InkFaint, anchor: TextAnchor.Center);
            }
        }
    }
}

/// <summary>Cumulative depth chart for an order book.</summary>
public sealed class DepthChart : ChartBase
{
    public OrderBook? Book { get; set; }
    public Func<double, string> PriceFormat { get; set; } = Compact;

    protected override void RenderChart(DrawingContext ctx, Rect bounds)
    {
        if (Book is null || Book.Bids.Count == 0 || Book.Asks.Count == 0) return;
        var plot = PlotArea(bounds);
        double pmin = Book.Bids[^1].Price, pmax = Book.Asks[^1].Price;
        double depth = Math.Max(Book.Depth(Side.Buy), Book.Depth(Side.Sell)) * 1.08;
        DrawYAxis(ctx, plot, 0, depth, Compact);
        DrawXAxisNumeric(ctx, plot, pmin, pmax, PriceFormat);
        Point Map(double p, double q) => new(plot.Left + (p - pmin) / (pmax - pmin) * plot.Width, plot.Bottom - q / depth * plot.Height);

        var bid = new List<Point>();
        double cum = 0;
        foreach (var l in Book.Bids) { bid.Add(Map(l.Price, cum)); cum += l.Size; bid.Add(Map(l.Price, cum)); }
        bid.Reverse();
        var ask = new List<Point>();
        cum = 0;
        foreach (var l in Book.Asks) { ask.Add(Map(l.Price, cum)); cum += l.Size; ask.Add(Map(l.Price, cum)); }
        FillUnder(ctx, bid, plot.Bottom, Palette.UpColor, 120);
        FillUnder(ctx, ask, plot.Bottom, Palette.DownColor, 120);
        DrawPolyline(ctx, new Pen(Palette.Up, 2), bid);
        DrawPolyline(ctx, new Pen(Palette.Down, 2), ask);
        var mid = Map(Book.Mid, 0);
        ctx.DrawLine(CrossPen, new Point(mid.X, plot.Top), new Point(mid.X, plot.Bottom));
        DrawText(ctx, $"mid {PriceFormat(Book.Mid)}", new Point(mid.X, plot.Top + 2), 10.5, Palette.InkSoft, anchor: TextAnchor.Center);
    }
}

/// <summary>A tiny trend line for stat cards.</summary>
public sealed class Sparkline : ChartBase
{
    public double[] Values { get; set; } = Array.Empty<double>();
    public Color? LineColor { get; set; }

    public Sparkline()
    {
        MinHeight = 28;
        PlotPadding = new Thickness(0, 2, 0, 2);
    }

    protected override void RenderChart(DrawingContext ctx, Rect bounds)
    {
        var v = Values.Where(double.IsFinite).ToArray();
        if (v.Length < 2) return;
        var plot = PlotArea(bounds);
        double lo = v.Min(), hi = v.Max();
        if (hi - lo < 1e-12) hi = lo + 1;
        var color = LineColor ?? (v[^1] >= v[0] ? Palette.UpColor : Palette.DownColor);
        var pts = v.Select((y, i) => new Point(plot.Left + i * plot.Width / (v.Length - 1), plot.Bottom - (y - lo) / (hi - lo) * plot.Height)).ToList();
        FillUnder(ctx, pts, plot.Bottom, color, 70);
        DrawPolyline(ctx, new Pen(Palette.Brush(color), 1.6), pts);
    }
}

/// <summary>Horizontal gauge from -1 to +1 (sentiment) with a marker.</summary>
public sealed class SentimentGauge : ChartBase
{
    public double Value { get; set; }
    public string Caption { get; set; } = "";

    public SentimentGauge()
    {
        MinHeight = 56;
        PlotPadding = new Thickness(8, 22, 8, 16);
    }

    protected override void RenderChart(DrawingContext ctx, Rect bounds)
    {
        var plot = PlotArea(bounds);
        var bar = new Rect(plot.Left, plot.Top + 4, plot.Width, 10);
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Palette.DownColor, 0), new GradientStop(Color.Parse("#334155"), 0.5), new GradientStop(Palette.UpColor, 1) },
        };
        ctx.DrawRectangle(brush, null, bar, 5, 5);
        double x = bar.Left + (Math.Clamp(Value, -1, 1) + 1) / 2 * bar.Width;
        ctx.DrawEllipse(Brushes.White, new Pen(Palette.Ground, 3), new Point(x, bar.Center.Y), 8, 8);
        DrawText(ctx, Caption, new Point(plot.Left, 2), 11, Palette.InkSoft);
        DrawText(ctx, Value.ToString("+0.00;-0.00;0.00"), new Point(plot.Right, 2), 11.5, Value >= 0 ? Palette.Up : Palette.Down, FontWeight.SemiBold, TextAnchor.Right);
        DrawText(ctx, "bearish", new Point(bar.Left, bar.Bottom + 3), 9.5, Palette.InkFaint);
        DrawText(ctx, "bullish", new Point(bar.Right, bar.Bottom + 3), 9.5, Palette.InkFaint, anchor: TextAnchor.Right);
    }
}
