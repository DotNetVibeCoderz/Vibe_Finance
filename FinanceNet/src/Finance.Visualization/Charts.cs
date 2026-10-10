using System.Globalization;
using System.Text;
using Finance.Core.Market;
using Finance.Mathematics;

namespace Finance.Visualization;

/// <summary>A line (or area) series. Points with NaN values break the line.</summary>
public sealed record LineSeries(string Name, double[] Values)
{
    /// <summary>Optional explicit colour; defaults to the theme palette.</summary>
    public string? Color { get; init; }

    /// <summary>Fill the area under the line with a gradient.</summary>
    public bool Area { get; init; }

    /// <summary>Dash pattern, e.g. "4 3".</summary>
    public string? Dash { get; init; }

    public double StrokeWidth { get; init; } = 1.8;
}

/// <summary>A scatter series; optionally connected (e.g. an efficient frontier).</summary>
public sealed record ScatterSeries(string Name, double[] X, double[] Y)
{
    public string? Color { get; init; }
    public double Radius { get; init; } = 3.5;
    public bool Connect { get; init; }

    /// <summary>Optional per-point labels (null entries are skipped).</summary>
    public string?[]? Labels { get; init; }
}

/// <summary>A highlighted point drawn above all series (e.g. "Max Sharpe").</summary>
public sealed record ChartMarker(double X, double Y, string Label, string? Color = null);

/// <summary>A trade marker on a candlestick chart.</summary>
public sealed record TradeMarker(int Index, double Price, Side Side, string? Label = null);

/// <summary>A vertical reference line (e.g. VaR threshold) on a histogram.</summary>
public sealed record ReferenceLine(double Value, string Label, string? Color = null);

/// <summary>Dependency-free SVG chart builders.</summary>
public static class Charts
{
    private const double Left = 70, Right = 24, Top = 56, Bottom = 44;

    private readonly record struct Plot(double X0, double Y0, double W, double H, double XMin, double XMax, double YMin, double YMax)
    {
        public double X(double v) => X0 + (XMax == XMin ? 0.5 : (v - XMin) / (XMax - XMin)) * W;
        public double Y(double v) => Y0 + H - (YMax == YMin ? 0.5 : (v - YMin) / (YMax - YMin)) * H;
    }

    // ------------------------------------------------------------------ line / area

    /// <summary>Multi-series line or area chart over an index or date axis.</summary>
    public static string Line(IReadOnlyList<LineSeries> series, ChartOptions? options = null, IReadOnlyList<DateTime>? dates = null, double[]? x = null)
    {
        var o = options ?? new ChartOptions();
        var svg = new SvgBuilder(o.Width, o.Height, o.Theme);
        int n = series.Count == 0 ? 0 : series.Max(s => s.Values.Length);
        var (ymin, ymax) = Range(series.SelectMany(s => s.Values), o.IncludeZero);
        double xmin = x is not null ? x.Min() : 0, xmax = x is not null ? x.Max() : Math.Max(1, n - 1);
        var (ylo, yhi, yt) = AxisTicks.Nice(ymin, ymax);
        var plot = new Plot(Left, Top, o.Width - Left - Right, o.Height - Top - Bottom, xmin, xmax, ylo, yhi);
        Header(svg, o);
        YAxis(svg, o, plot, yt);
        if (dates is not null) DateAxis(svg, o, plot, dates);
        else
        {
            var (_, _, xt) = AxisTicks.Nice(xmin, xmax, 8);
            NumericXAxis(svg, o, plot, xt.Where(t => t >= xmin - 1e-9 && t <= xmax + 1e-9).ToArray());
        }

        for (int k = 0; k < series.Count; k++)
        {
            var s = series[k];
            string color = s.Color ?? o.Theme.Series(k);
            var d = PathData(s.Values, i => plot.X(x is not null ? x[i] : i), plot.Y);
            if (s.Area)
            {
                string gid = $"g{k}{Math.Abs(s.Name.GetHashCode()) % 10000}";
                svg.Raw($"<defs><linearGradient id=\"{gid}\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"{color}\" stop-opacity=\"0.35\"/><stop offset=\"1\" stop-color=\"{color}\" stop-opacity=\"0.02\"/></linearGradient></defs>");
                double baseY = plot.Y(Math.Max(ylo, Math.Min(0, yhi)));
                if (ylo > 0) baseY = plot.Y(ylo);
                foreach (var seg in Segments(s.Values))
                {
                    var sb = new StringBuilder();
                    double fx = plot.X(x is not null ? x[seg.Start] : seg.Start);
                    sb.Append($"M{SvgBuilder.F(fx)},{SvgBuilder.F(baseY)}");
                    for (int i = seg.Start; i <= seg.End; i++)
                        sb.Append($"L{SvgBuilder.F(plot.X(x is not null ? x[i] : i))},{SvgBuilder.F(plot.Y(s.Values[i]))}");
                    sb.Append($"L{SvgBuilder.F(plot.X(x is not null ? x[seg.End] : seg.End))},{SvgBuilder.F(baseY)}Z");
                    svg.Raw($"<path d=\"{sb}\" fill=\"url(#{gid})\" stroke=\"none\"/>");
                }
            }
            svg.Path(d, color, s.StrokeWidth, dash: s.Dash);
        }
        if (o.ShowLegend && series.Count > 1)
            Legend(svg, o, series.Select((s, k) => (s.Name, s.Color ?? o.Theme.Series(k))).ToList());
        return svg.ToString();
    }

    // ------------------------------------------------------------------ candlestick

    /// <summary>
    /// Candlestick chart with optional volume pane, overlay lines (moving averages, bands) and trade markers.
    /// Overlays must be aligned with <paramref name="bars"/>.
    /// </summary>
    public static string Candlestick(IReadOnlyList<Bar> bars, ChartOptions? options = null, IReadOnlyList<LineSeries>? overlays = null,
        bool showVolume = true, IReadOnlyList<TradeMarker>? trades = null)
    {
        var o = options ?? new ChartOptions { Height = 560 };
        var t = o.Theme;
        var svg = new SvgBuilder(o.Width, o.Height, t);
        int n = bars.Count;
        Header(svg, o);
        double totalH = o.Height - Top - Bottom;
        double volH = showVolume ? totalH * 0.2 : 0;
        double priceH = totalH - volH - (showVolume ? 10 : 0);

        double lo = double.PositiveInfinity, hi = double.NegativeInfinity, vmax = 0;
        foreach (var b in bars)
        {
            lo = Math.Min(lo, b.Low);
            hi = Math.Max(hi, b.High);
            vmax = Math.Max(vmax, b.Volume);
        }
        if (overlays is not null)
            foreach (var s in overlays)
                foreach (var v in s.Values)
                    if (double.IsFinite(v)) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        if (n == 0) { lo = 0; hi = 1; }
        var (ylo, yhi, yt) = AxisTicks.Nice(lo, hi);
        var plot = new Plot(Left, Top, o.Width - Left - Right, priceH, -0.5, n - 0.5, ylo, yhi);
        YAxis(svg, o, plot, yt);
        var dates = bars.Select(b => b.Time).ToList();

        double slot = plot.W / Math.Max(1, n);
        double bodyW = Math.Max(1, slot * 0.68);
        for (int i = 0; i < n; i++)
        {
            var b = bars[i];
            string c = b.IsBullish ? t.Up : t.Down;
            double cx = plot.X(i);
            svg.Line(cx, plot.Y(b.High), cx, plot.Y(b.Low), c, Math.Max(1, Math.Min(1.4, slot * 0.15)));
            double y1 = plot.Y(Math.Max(b.Open, b.Close)), y2 = plot.Y(Math.Min(b.Open, b.Close));
            svg.Rect(cx - bodyW / 2, y1, bodyW, Math.Max(1, y2 - y1), c);
        }

        if (overlays is not null)
            for (int k = 0; k < overlays.Count; k++)
            {
                var s = overlays[k];
                svg.Path(PathData(s.Values, i => plot.X(i), plot.Y), s.Color ?? t.Series(k + 1), s.StrokeWidth, dash: s.Dash);
            }

        if (trades is not null)
            foreach (var tr in trades)
            {
                if (tr.Index < 0 || tr.Index >= n) continue;
                double cx = plot.X(tr.Index);
                bool buy = tr.Side == Side.Buy;
                double y = buy ? plot.Y(bars[tr.Index].Low) + 10 : plot.Y(bars[tr.Index].High) - 10;
                var pts = buy
                    ? new[] { (cx, y - 6), (cx - 6, y + 5), (cx + 6, y + 5) }
                    : new[] { (cx, y + 6), (cx - 6, y - 5), (cx + 6, y - 5) };
                svg.Polygon(pts, buy ? t.Up : t.Down);
                if (tr.Label is not null) svg.Text(cx, buy ? y + 17 : y - 10, tr.Label, t.MutedText, 10, "middle");
            }

        if (showVolume)
        {
            double vy0 = Top + priceH + 10;
            svg.Line(Left, vy0 - 5, o.Width - Right, vy0 - 5, t.Grid, 1);
            svg.Text(Left - 8, vy0 + 10, "Vol", t.MutedText, 10, "end");
            for (int i = 0; i < n; i++)
            {
                var b = bars[i];
                double h = vmax > 0 ? b.Volume / vmax * (volH - 4) : 0;
                svg.Rect(plot.X(i) - bodyW / 2, vy0 + volH - h, bodyW, h, b.IsBullish ? t.Up : t.Down, opacity: 0.45);
            }
            var volPlot = plot with { Y0 = vy0, H = volH };
            DateAxis(svg, o, volPlot, dates);
        }
        else DateAxis(svg, o, plot, dates);

        if (o.ShowLegend && overlays is { Count: > 0 })
            Legend(svg, o, overlays.Select((s, k) => (s.Name, s.Color ?? t.Series(k + 1))).ToList());
        return svg.ToString();
    }

    // ------------------------------------------------------------------ bar

    /// <summary>Vertical bar chart; negative values use the theme's down colour unless <paramref name="color"/> is set.</summary>
    public static string Bar(IReadOnlyList<string> labels, IReadOnlyList<double> values, ChartOptions? options = null, string? color = null)
    {
        var o = options ?? new ChartOptions();
        var t = o.Theme;
        var svg = new SvgBuilder(o.Width, o.Height, t);
        Header(svg, o);
        var (ymin, ymax) = Range(values, includeZero: true);
        var (ylo, yhi, yt) = AxisTicks.Nice(ymin, ymax);
        int n = values.Count;
        var plot = new Plot(Left, Top, o.Width - Left - Right, o.Height - Top - Bottom - (labels.Any(l => l.Length > 6) ? 14 : 0), -0.5, n - 0.5, ylo, yhi);
        YAxis(svg, o, plot, yt);
        double slot = plot.W / Math.Max(1, n), w = slot * 0.62, zero = plot.Y(0);
        int labelStride = Math.Max(1, (int)Math.Ceiling(n / 16.0));
        for (int i = 0; i < n; i++)
        {
            double v = values[i], y = plot.Y(v);
            string c = color ?? (v >= 0 ? t.Accent : t.Down);
            svg.Rect(plot.X(i) - w / 2, Math.Min(y, zero), w, Math.Abs(zero - y), c, rx: Math.Min(3, w / 4));
            if (i % labelStride == 0)
                svg.Text(plot.X(i), plot.Y0 + plot.H + 18, labels[i], t.MutedText, 11, "middle");
        }
        svg.Line(plot.X0, zero, plot.X0 + plot.W, zero, t.Axis, 1);
        AxisLabels(svg, o, plot);
        return svg.ToString();
    }

    // ------------------------------------------------------------------ scatter

    /// <summary>Scatter chart (e.g. efficient frontier) with optional highlighted markers.</summary>
    public static string Scatter(IReadOnlyList<ScatterSeries> series, ChartOptions? options = null, IReadOnlyList<ChartMarker>? markers = null)
    {
        var o = options ?? new ChartOptions();
        var t = o.Theme;
        var svg = new SvgBuilder(o.Width, o.Height, t);
        Header(svg, o);
        var xs = series.SelectMany(s => s.X).Concat(markers?.Select(m => m.X) ?? Enumerable.Empty<double>());
        var ys = series.SelectMany(s => s.Y).Concat(markers?.Select(m => m.Y) ?? Enumerable.Empty<double>());
        var (xmin, xmax) = Range(xs, false);
        var (ymin, ymax) = Range(ys, o.IncludeZero);
        var (xlo, xhi, xt) = AxisTicks.Nice(xmin, xmax, 8);
        var (ylo, yhi, yt) = AxisTicks.Nice(ymin, ymax);
        var plot = new Plot(Left, Top, o.Width - Left - Right, o.Height - Top - Bottom, xlo, xhi, ylo, yhi);
        YAxis(svg, o, plot, yt);
        NumericXAxis(svg, o, plot, xt);
        for (int k = 0; k < series.Count; k++)
        {
            var s = series[k];
            string color = s.Color ?? t.Series(k);
            if (s.Connect)
            {
                var order = Enumerable.Range(0, s.X.Length).ToArray();
                var sb = new StringBuilder();
                for (int i = 0; i < order.Length; i++)
                    sb.Append(i == 0 ? 'M' : 'L').Append(SvgBuilder.F(plot.X(s.X[order[i]]))).Append(',').Append(SvgBuilder.F(plot.Y(s.Y[order[i]])));
                svg.Path(sb.ToString(), color, 2.2);
            }
            for (int i = 0; i < s.X.Length; i++)
            {
                if (!double.IsFinite(s.X[i]) || !double.IsFinite(s.Y[i])) continue;
                svg.Circle(plot.X(s.X[i]), plot.Y(s.Y[i]), s.Radius, color, opacity: s.Connect ? 1 : 0.75);
                if (s.Labels is { } labels && i < labels.Length && labels[i] is { } lab)
                    svg.Text(plot.X(s.X[i]) + 6, plot.Y(s.Y[i]) - 6, lab, t.Text, 11);
            }
        }
        if (markers is not null)
            foreach (var m in markers)
            {
                string c = m.Color ?? t.Accent;
                double cx = plot.X(m.X), cy = plot.Y(m.Y);
                svg.Circle(cx, cy, 8, c, t.Background);
                svg.Rect(cx + 10, cy - 22, m.Label.Length * 6.6 + 12, 20, t.Panel, c, 4);
                svg.Text(cx + 16, cy - 8, m.Label, t.Text, 11, weight: "600");
            }
        if (o.ShowLegend && series.Count > 1)
            Legend(svg, o, series.Select((s, k) => (s.Name, s.Color ?? t.Series(k))).ToList());
        return svg.ToString();
    }

    // ------------------------------------------------------------------ heatmap

    /// <summary>
    /// Heatmap of a square or rectangular matrix (e.g. correlations) with row/column labels and cell values.
    /// Uses a diverging palette centred on zero when the data has both signs.
    /// </summary>
    public static string Heatmap(double[,] values, IReadOnlyList<string> rowLabels, IReadOnlyList<string> columnLabels, ChartOptions? options = null,
        string valueFormat = "0.00", double? min = null, double? max = null)
    {
        var o = options ?? new ChartOptions { Width = 720, Height = 640 };
        var t = o.Theme;
        var svg = new SvgBuilder(o.Width, o.Height, t);
        Header(svg, o);
        int rows = values.GetLength(0), cols = values.GetLength(1);
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var v in values) if (double.IsFinite(v)) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        lo = min ?? lo;
        hi = max ?? hi;
        bool diverging = lo < 0 && hi > 0;
        double absMax = Math.Max(Math.Abs(lo), Math.Abs(hi));

        int labelW = Math.Min(140, 12 + 7 * rowLabels.Max(l => l.Length));
        double x0 = labelW + 10, y0 = Top + 8, legendW = 70;
        double cw = (o.Width - x0 - Right - legendW) / cols, ch = (o.Height - y0 - Bottom - 20) / rows;
        double cell = Math.Min(cw, ch);
        for (int r = 0; r < rows; r++)
        {
            svg.Text(x0 - 8, y0 + r * cell + cell / 2 + 4, rowLabels[r], t.Text, 11, "end");
            for (int c = 0; c < cols; c++)
            {
                double v = values[r, c];
                string fill = double.IsFinite(v) ? CellColor(v, lo, hi, absMax, diverging, t) : t.Panel;
                svg.Rect(x0 + c * cell + 1, y0 + r * cell + 1, cell - 2, cell - 2, fill, rx: 3);
                if (cell >= 30 && double.IsFinite(v))
                {
                    double lum = Luminance(fill);
                    svg.Text(x0 + c * cell + cell / 2, y0 + r * cell + cell / 2 + 4, v.ToString(valueFormat, CultureInfo.InvariantCulture),
                        lum > 0.55 ? "#0F172A" : "#F8FAFC", Math.Min(12, cell / 3.6), "middle");
                }
            }
        }
        for (int c = 0; c < cols; c++)
            svg.Text(x0 + c * cell + cell / 2, y0 + rows * cell + 16, columnLabels[c], t.Text, 11, "middle");

        // Colour scale.
        double lx = x0 + cols * cell + 24, lh = rows * cell;
        for (int s = 0; s < 40; s++)
        {
            double v = hi - (hi - lo) * s / 39.0;
            svg.Rect(lx, y0 + lh * s / 40.0, 14, lh / 40.0 + 0.5, CellColor(v, lo, hi, absMax, diverging, t));
        }
        svg.Text(lx + 20, y0 + 10, hi.ToString(valueFormat, CultureInfo.InvariantCulture), t.MutedText, 10);
        svg.Text(lx + 20, y0 + lh, lo.ToString(valueFormat, CultureInfo.InvariantCulture), t.MutedText, 10);
        return svg.ToString();
    }

    /// <summary>Heatmap overload for a <see cref="Matrix"/>.</summary>
    public static string Heatmap(Matrix matrix, IReadOnlyList<string> labels, ChartOptions? options = null, string valueFormat = "0.00")
        => Heatmap(matrix.ToArray(), labels, labels, options, valueFormat, matrix.Data.Any(v => v < 0) ? -1 : null, null);

    // ------------------------------------------------------------------ histogram

    /// <summary>Histogram of values (e.g. daily returns) with optional reference lines (VaR, mean) and a normal fit.</summary>
    public static string Histogram(ReadOnlySpan<double> values, int bins = 50, ChartOptions? options = null, IReadOnlyList<ReferenceLine>? lines = null, bool normalFit = true)
    {
        var o = options ?? new ChartOptions();
        var t = o.Theme;
        var svg = new SvgBuilder(o.Width, o.Height, t);
        Header(svg, o);
        var data = new List<double>(values.Length);
        foreach (var v in values) if (double.IsFinite(v)) data.Add(v);
        if (data.Count == 0) return svg.ToString();
        double lo = data.Min(), hi = data.Max();
        if (hi <= lo) hi = lo + 1;
        var counts = new int[bins];
        double width = (hi - lo) / bins;
        foreach (var v in data) counts[Math.Min(bins - 1, (int)((v - lo) / width))]++;
        int cmax = counts.Max();
        var (xlo, xhi, xt) = AxisTicks.Nice(lo, hi, 8);
        var (ylo, yhi, yt) = AxisTicks.Nice(0, cmax * 1.08);
        var plot = new Plot(Left, Top, o.Width - Left - Right, o.Height - Top - Bottom, xlo, xhi, 0, yhi);
        YAxis(svg, o, plot, yt);
        NumericXAxis(svg, o, plot, xt);
        double threshold = lines?.Count > 0 ? lines.Min(l => l.Value) : double.NegativeInfinity;
        for (int b = 0; b < bins; b++)
        {
            double a = lo + b * width;
            double x1 = plot.X(a), x2 = plot.X(a + width);
            string c = a + width <= threshold ? t.Down : t.Accent;
            svg.Rect(x1 + 0.5, plot.Y(counts[b]), Math.Max(1, x2 - x1 - 1), plot.Y(0) - plot.Y(counts[b]), c, opacity: 0.85);
        }
        if (normalFit && data.Count > 2)
        {
            var arr = data.ToArray();
            double mu = Stats.Mean(arr), sd = Stats.StdDev(arr);
            if (sd > 0)
            {
                var sb = new StringBuilder();
                for (int k = 0; k <= 120; k++)
                {
                    double x = xlo + (xhi - xlo) * k / 120.0;
                    double y = Normal.Pdf(x, mu, sd) * data.Count * width;
                    sb.Append(k == 0 ? 'M' : 'L').Append(SvgBuilder.F(plot.X(x))).Append(',').Append(SvgBuilder.F(plot.Y(Math.Min(y, yhi))));
                }
                svg.Path(sb.ToString(), t.Series(1), 2, dash: "5 4");
            }
        }
        if (lines is not null)
            for (int k = 0; k < lines.Count; k++)
            {
                var l = lines[k];
                string c = l.Color ?? (k == 0 ? t.Down : t.Series(k + 2));
                double x = plot.X(l.Value);
                svg.Line(x, plot.Y0, x, plot.Y0 + plot.H, c, 2, "6 4");
                svg.Rect(x + 4, plot.Y0 + 4 + k * 22, l.Label.Length * 6.6 + 10, 18, t.Panel, c, 3);
                svg.Text(x + 9, plot.Y0 + 17 + k * 22, l.Label, t.Text, 11);
            }
        return svg.ToString();
    }

    // ------------------------------------------------------------------ drawdown

    /// <summary>Underwater (drawdown) chart; <paramref name="drawdown"/> values are ≤ 0 fractions.</summary>
    public static string Drawdown(IReadOnlyList<DateTime> dates, double[] drawdown, ChartOptions? options = null)
    {
        var o = options ?? new ChartOptions { Height = 300 };
        o = o with { YFormat = o.YFormat == "G4" ? "P0" : o.YFormat, IncludeZero = true, ShowLegend = false };
        return Line(new[] { new LineSeries("Drawdown", drawdown) { Area = true, Color = o.Theme.Down } }, o, dates);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Writes an SVG string to a file (UTF-8).</summary>
    public static void Save(string svg, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, svg, new UTF8Encoding(false));
    }

    private static (double Min, double Max) Range(IEnumerable<double> values, bool includeZero)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var v in values)
        {
            if (!double.IsFinite(v)) continue;
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        if (double.IsInfinity(lo)) return (0, 1);
        if (includeZero) { lo = Math.Min(lo, 0); hi = Math.Max(hi, 0); }
        return (lo, hi);
    }

    private static IEnumerable<(int Start, int End)> Segments(double[] v)
    {
        int i = 0;
        while (i < v.Length)
        {
            while (i < v.Length && !double.IsFinite(v[i])) i++;
            int s = i;
            while (i < v.Length && double.IsFinite(v[i])) i++;
            if (i > s) yield return (s, i - 1);
        }
    }

    private static string PathData(double[] values, Func<int, double> x, Func<double, double> y)
    {
        var sb = new StringBuilder(values.Length * 14);
        bool pen = false;
        for (int i = 0; i < values.Length; i++)
        {
            double v = values[i];
            if (!double.IsFinite(v)) { pen = false; continue; }
            sb.Append(pen ? 'L' : 'M').Append(SvgBuilder.F(x(i))).Append(',').Append(SvgBuilder.F(y(v)));
            pen = true;
        }
        return sb.Length == 0 ? "M0,0" : sb.ToString();
    }

    private static void Header(SvgBuilder svg, ChartOptions o)
    {
        if (o.Title is not null) svg.Text(Left - 46, 28, o.Title, o.Theme.Text, 17, weight: "600");
        if (o.Subtitle is not null) svg.Text(Left - 46, 46, o.Subtitle, o.Theme.MutedText, 12);
    }

    private static void YAxis(SvgBuilder svg, ChartOptions o, Plot p, double[] ticks)
    {
        var t = o.Theme;
        svg.Rect(p.X0, p.Y0, p.W, p.H, t.Panel, rx: 4);
        foreach (var v in ticks)
        {
            double y = p.Y(v);
            if (y < p.Y0 - 0.5 || y > p.Y0 + p.H + 0.5) continue;
            if (o.ShowGrid) svg.Line(p.X0, y, p.X0 + p.W, y, t.Grid, 1);
            svg.Text(p.X0 - 8, y + 4, AxisTicks.Format(v, o.YFormat), t.MutedText, 11, "end");
        }
        AxisLabels(svg, o, p);
    }

    private static void AxisLabels(SvgBuilder svg, ChartOptions o, Plot p)
    {
        if (o.YLabel is not null) svg.Text(16, p.Y0 + p.H / 2, o.YLabel, o.Theme.MutedText, 11, "middle", rotate: -90);
        if (o.XLabel is not null) svg.Text(p.X0 + p.W / 2, o.Height - 8, o.XLabel, o.Theme.MutedText, 11, "middle");
    }

    private static void NumericXAxis(SvgBuilder svg, ChartOptions o, Plot p, double[] ticks)
    {
        var t = o.Theme;
        foreach (var v in ticks)
        {
            double x = p.X(v);
            if (x < p.X0 - 0.5 || x > p.X0 + p.W + 0.5) continue;
            if (o.ShowGrid) svg.Line(x, p.Y0, x, p.Y0 + p.H, t.Grid, 1, opacity: 0.6);
            svg.Text(x, p.Y0 + p.H + 18, AxisTicks.Format(v, o.XFormat), t.MutedText, 11, "middle");
        }
    }

    private static void DateAxis(SvgBuilder svg, ChartOptions o, Plot p, IReadOnlyList<DateTime> dates)
    {
        var t = o.Theme;
        var (idx, fmt) = AxisTicks.DateTicks(dates);
        foreach (var i in idx)
        {
            double x = p.X(i);
            if (o.ShowGrid) svg.Line(x, Top, x, p.Y0 + p.H, t.Grid, 1, opacity: 0.5);
            svg.Text(x, p.Y0 + p.H + 18, dates[i].ToString(fmt, CultureInfo.InvariantCulture), t.MutedText, 11, "middle");
        }
    }

    private static void Legend(SvgBuilder svg, ChartOptions o, IReadOnlyList<(string Name, string Color)> items)
    {
        double x = o.Width - Right, y = 26;
        for (int k = items.Count - 1; k >= 0; k--)
        {
            var (name, color) = items[k];
            double w = name.Length * 6.6 + 26;
            x -= w;
            svg.Rect(x, y - 9, 14, 4, color, rx: 2);
            svg.Text(x + 19, y - 3, name, o.Theme.Text, 11.5);
        }
    }

    private static string CellColor(double v, double lo, double hi, double absMax, bool diverging, ChartTheme t)
    {
        if (diverging)
        {
            double s = absMax > 0 ? Math.Clamp(v / absMax, -1, 1) : 0;
            // Negative: coral, positive: teal-blue; centre: neutral slate.
            var neutral = t.Name == "light" ? (241, 245, 249) : (30, 41, 59);
            var pos = (56, 189, 248);
            var neg = (239, 68, 68);
            var target = s >= 0 ? pos : neg;
            double a = Math.Abs(s);
            return Hex(Lerp(neutral.Item1, target.Item1, a), Lerp(neutral.Item2, target.Item2, a), Lerp(neutral.Item3, target.Item3, a));
        }
        double f = hi > lo ? Math.Clamp((v - lo) / (hi - lo), 0, 1) : 0.5;
        // Sequential "viridis-like" ramp.
        (int R, int G, int B)[] ramp = { (68, 1, 84), (59, 82, 139), (33, 145, 140), (94, 201, 98), (253, 231, 37) };
        double pos2 = f * (ramp.Length - 1);
        int k = Math.Min(ramp.Length - 2, (int)pos2);
        double fr = pos2 - k;
        return Hex(Lerp(ramp[k].R, ramp[k + 1].R, fr), Lerp(ramp[k].G, ramp[k + 1].G, fr), Lerp(ramp[k].B, ramp[k + 1].B, fr));
    }

    private static int Lerp(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);

    private static string Hex(int r, int g, int b) => $"#{r:X2}{g:X2}{b:X2}";

    private static double Luminance(string hex)
    {
        int r = Convert.ToInt32(hex.Substring(1, 2), 16), g = Convert.ToInt32(hex.Substring(3, 2), 16), b = Convert.ToInt32(hex.Substring(5, 2), 16);
        return (0.299 * r + 0.587 * g + 0.114 * b) / 255;
    }
}
