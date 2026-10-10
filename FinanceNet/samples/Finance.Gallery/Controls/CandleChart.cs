using Avalonia;
using Avalonia.Media;
using Finance.Core.Market;

namespace Finance.Gallery.Controls;

/// <summary>
/// Candlestick chart with a volume pane, price overlays (moving averages, bands) and an optional
/// lower indicator pane (RSI, MACD...). Shows the last <see cref="VisibleBars"/> bars.
/// </summary>
public sealed class CandleChart : ChartBase
{
    public Bar[] Bars { get; set; } = Array.Empty<Bar>();
    public List<LineSeries> Overlays { get; } = new();
    public List<LineSeries> LowerPane { get; } = new();
    public string LowerPaneTitle { get; set; } = "";
    public List<(double Value, Color Color)> LowerPaneLevels { get; } = new();
    public int VisibleBars { get; set; } = 160;

    /// <summary>Trade markers: bar index and whether it was a buy (▲ below the low) or a sell (▼ above the high).</summary>
    public List<(int Index, bool Buy)> Markers { get; } = new();
    public bool ShowVolume { get; set; } = true;
    public Func<double, string> PriceFormat { get; set; } = Compact;

    public CandleChart() => PlotPadding = new Thickness(16, 28, 66, 30);

    protected override void RenderChart(DrawingContext ctx, Rect bounds)
    {
        if (Bars.Length == 0) return;
        int last = Bars.Length - 1, first = Math.Max(0, Bars.Length - VisibleBars);
        int count = last - first + 1;

        var full = PlotArea(bounds);
        bool lower = LowerPane.Count > 0;
        double lowerH = lower ? full.Height * 0.24 : 0;
        double volH = ShowVolume ? full.Height * 0.16 : 0;
        var price = new Rect(full.X, full.Y, full.Width, full.Height - lowerH - (lower ? 10 : 0));
        var lowerRect = new Rect(full.X, price.Bottom + 10, full.Width, lowerH);

        double lo = double.PositiveInfinity, hi = double.NegativeInfinity, vmax = 0;
        for (int i = first; i <= last; i++)
        {
            lo = Math.Min(lo, Bars[i].Low);
            hi = Math.Max(hi, Bars[i].High);
            vmax = Math.Max(vmax, Bars[i].Volume);
        }
        foreach (var o in Overlays)
            for (int i = first; i <= last && i < o.Y.Length; i++)
                if (double.IsFinite(o.Y[i])) { lo = Math.Min(lo, o.Y[i]); hi = Math.Max(hi, o.Y[i]); }
        (lo, hi) = Pad(lo, hi, 0.05);
        var candleRect = new Rect(price.X, price.Y, price.Width, price.Height - volH);

        // Grid + right-hand price axis.
        foreach (var v in NiceTicks(lo, hi, Math.Max(2, (int)(candleRect.Height / 46))))
        {
            double y = candleRect.Bottom - (v - lo) / (hi - lo) * candleRect.Height;
            ctx.DrawLine(GridPen, new Point(candleRect.Left, y), new Point(candleRect.Right, y));
            DrawText(ctx, PriceFormat(v), new Point(candleRect.Right + 8, y - 7), 10.5, Palette.InkFaint);
        }
        DrawXAxisDates(ctx, lower ? new Rect(full.X, full.Y, full.Width, full.Height) : price, Bars.Select(b => b.Time).ToArray(), first, last);

        double slot = price.Width / count;
        double body = Math.Max(1, slot * 0.68);
        double Y(double v) => candleRect.Bottom - (v - lo) / (hi - lo) * candleRect.Height;
        double X(int i) => price.Left + (i - first + 0.5) * slot;

        using (ctx.PushClip(price))
        {
            if (ShowVolume && vmax > 0)
                for (int i = first; i <= last; i++)
                {
                    var b = Bars[i];
                    double h = b.Volume / vmax * volH;
                    var c = b.IsBullish ? Palette.UpColor : Palette.DownColor;
                    ctx.DrawRectangle(Palette.Brush(c, 70), null, new Rect(X(i) - body / 2, price.Bottom - h, body, h));
                }

            for (int i = first; i <= last; i++)
            {
                var b = Bars[i];
                var brush = b.IsBullish ? Palette.Up : Palette.Down;
                double x = X(i);
                ctx.DrawLine(new Pen(brush, 1), new Point(x, Y(b.High)), new Point(x, Y(b.Low)));
                double top = Y(Math.Max(b.Open, b.Close)), bottom = Y(Math.Min(b.Open, b.Close));
                ctx.DrawRectangle(brush, null, new Rect(x - body / 2, top, body, Math.Max(1, bottom - top)), 1, 1);
            }

            foreach (var (mi, buy) in Markers)
            {
                if (mi < first || mi > last) continue;
                var b = Bars[mi];
                double mx = X(mi), my = buy ? Y(b.Low) + 6 : Y(b.High) - 6;
                var tri = new StreamGeometry();
                using (var g = tri.Open())
                {
                    double s = Math.Max(4, Math.Min(7, body * 0.8));
                    g.BeginFigure(new Point(mx, my), true);
                    g.LineTo(new Point(mx - s, buy ? my + s * 1.6 : my - s * 1.6));
                    g.LineTo(new Point(mx + s, buy ? my + s * 1.6 : my - s * 1.6));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(buy ? Palette.Gold : Palette.Brush(Palette.SkyColor), new Pen(Palette.Ground, 1), tri);
            }

            for (int oi = 0; oi < Overlays.Count; oi++)
            {
                var o = Overlays[oi];
                var color = o.Color ?? Palette.SeriesColor(oi + 2);
                var pts = new List<Point>();
                for (int i = first; i <= last && i < o.Y.Length; i++)
                {
                    if (!double.IsFinite(o.Y[i])) { Stroke(ctx, o, color, pts); continue; }
                    pts.Add(new Point(X(i), Y(o.Y[i])));
                }
                Stroke(ctx, o, color, pts);
            }
        }

        // Last price tag.
        var lastBar = Bars[last];
        double ly = Y(lastBar.Close);
        var tagBrush = lastBar.IsBullish ? Palette.Up : Palette.Down;
        ctx.DrawLine(new Pen(tagBrush, 1, DashStyle.Dot), new Point(price.Left, ly), new Point(price.Right, ly));
        var tag = Text(PriceFormat(lastBar.Close), 10.5, Brushes.White, FontWeight.SemiBold);
        ctx.DrawRectangle(tagBrush, null, new Rect(price.Right + 2, ly - 9, tag.Width + 12, 18), 4, 4);
        ctx.DrawText(tag, new Point(price.Right + 8, ly - 7));

        // Legend.
        double lx = price.Left + 6;
        for (int oi = 0; oi < Overlays.Count; oi++)
        {
            var color = Overlays[oi].Color ?? Palette.SeriesColor(oi + 2);
            ctx.DrawRectangle(Palette.Brush(color), null, new Rect(lx, price.Top + 9, 10, 3), 1.5, 1.5);
            lx += DrawText(ctx, Overlays[oi].Name, new Point(lx + 14, price.Top + 3), 10.5, Palette.InkSoft).Width + 28;
        }

        if (lower) RenderLower(ctx, lowerRect, first, last, X);

        if (Pointer is { } p && price.Contains(p))
        {
            int i = Math.Clamp(first + (int)((p.X - price.Left) / slot), first, last);
            var b = Bars[i];
            ctx.DrawLine(CrossPen, new Point(X(i), full.Top), new Point(X(i), full.Bottom));
            ctx.DrawLine(CrossPen, new Point(price.Left, p.Y), new Point(price.Right, p.Y));
            var lines = new List<(string, IBrush)>
            {
                (b.Time.ToString("yyyy-MM-dd HH:mm"), Palette.InkSoft),
                ($"O {PriceFormat(b.Open)}   H {PriceFormat(b.High)}", Palette.Ink),
                ($"L {PriceFormat(b.Low)}   C {PriceFormat(b.Close)}", b.IsBullish ? Palette.Up : Palette.Down),
                ($"Vol {Compact(b.Volume)}", Palette.InkSoft),
            };
            foreach (var o in Overlays)
                if (i < o.Y.Length && double.IsFinite(o.Y[i])) lines.Add(($"{o.Name} {PriceFormat(o.Y[i])}", Palette.InkSoft));
            DrawTooltip(ctx, bounds, p, lines);
        }
    }

    private void RenderLower(DrawingContext ctx, Rect r, int first, int last, Func<int, double> x)
    {
        ctx.DrawRectangle(null, AxisPen, r, 0, 0);
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var s in LowerPane)
            for (int i = first; i <= last && i < s.Y.Length; i++)
                if (double.IsFinite(s.Y[i])) { lo = Math.Min(lo, s.Y[i]); hi = Math.Max(hi, s.Y[i]); }
        foreach (var l in LowerPaneLevels) { lo = Math.Min(lo, l.Value); hi = Math.Max(hi, l.Value); }
        (lo, hi) = Pad(lo, hi, 0.08);
        double Y(double v) => r.Bottom - (v - lo) / (hi - lo) * r.Height;
        foreach (var l in LowerPaneLevels)
        {
            ctx.DrawLine(new Pen(Palette.Brush(l.Color, 160), 1, DashStyle.Dash), new Point(r.Left, Y(l.Value)), new Point(r.Right, Y(l.Value)));
            DrawText(ctx, Compact(l.Value), new Point(r.Right + 8, Y(l.Value) - 7), 10, Palette.InkFaint);
        }
        if (!string.IsNullOrEmpty(LowerPaneTitle)) DrawText(ctx, LowerPaneTitle, new Point(r.Left + 6, r.Top + 4), 10.5, Palette.InkSoft, FontWeight.SemiBold);
        using (ctx.PushClip(r))
        {
            for (int si = 0; si < LowerPane.Count; si++)
            {
                var s = LowerPane[si];
                var color = s.Color ?? Palette.SeriesColor(si);
                var pts = new List<Point>();
                for (int i = first; i <= last && i < s.Y.Length; i++)
                {
                    if (!double.IsFinite(s.Y[i])) { Stroke(ctx, s, color, pts); continue; }
                    pts.Add(new Point(x(i), Y(s.Y[i])));
                }
                Stroke(ctx, s, color, pts);
            }
        }
    }

    private static void Stroke(DrawingContext ctx, LineSeries s, Color color, List<Point> pts)
    {
        if (pts.Count >= 2)
            DrawPolyline(ctx, new Pen(Palette.Brush(color), s.Thickness, s.Dashed ? DashStyle.Dash : null, PenLineCap.Round, PenLineJoin.Round), pts);
        pts.Clear();
    }
}
