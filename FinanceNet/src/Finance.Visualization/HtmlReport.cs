using System.Globalization;
using System.Net;
using System.Text;
using Finance.Core;
using Finance.Mathematics;

namespace Finance.Visualization;

/// <summary>A headline metric shown as a card.</summary>
public sealed record MetricCard(string Label, string Value, string? Hint = null, bool? Positive = null);

/// <summary>
/// Builds a self-contained HTML report (inline CSS, inline SVG charts, no external assets).
/// </summary>
public sealed class HtmlReport
{
    private readonly StringBuilder _body = new();

    public HtmlReport(string title, ChartTheme? theme = null)
    {
        Title = title;
        Theme = theme ?? ChartTheme.Dark;
    }

    public string Title { get; }
    public ChartTheme Theme { get; }

    /// <summary>Optional subtitle under the title.</summary>
    public string? Subtitle { get; set; }

    private static string E(string s) => WebUtility.HtmlEncode(s);

    public HtmlReport Heading(string text, int level = 2)
    {
        level = Math.Clamp(level, 2, 4);
        _body.Append($"<h{level}>{E(text)}</h{level}>");
        return this;
    }

    public HtmlReport Paragraph(string text)
    {
        _body.Append($"<p>{E(text)}</p>");
        return this;
    }

    /// <summary>A row of metric cards.</summary>
    public HtmlReport Metrics(IEnumerable<MetricCard> cards)
    {
        _body.Append("<div class=\"cards\">");
        foreach (var c in cards)
        {
            string cls = c.Positive switch { true => " pos", false => " neg", _ => "" };
            _body.Append($"<div class=\"card\"><div class=\"label\">{E(c.Label)}</div><div class=\"value{cls}\">{E(c.Value)}</div>");
            if (c.Hint is not null) _body.Append($"<div class=\"hint\">{E(c.Hint)}</div>");
            _body.Append("</div>");
        }
        _body.Append("</div>");
        return this;
    }

    /// <summary>A two-column key/value table.</summary>
    public HtmlReport KeyValues(IEnumerable<(string Key, string Value)> rows, string? caption = null)
        => Table(new[] { "Metric", "Value" }, rows.Select(r => new[] { r.Key, r.Value }), caption);

    /// <summary>A data table.</summary>
    public HtmlReport Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, string? caption = null)
    {
        _body.Append("<div class=\"panel\">");
        if (caption is not null) _body.Append($"<div class=\"caption\">{E(caption)}</div>");
        _body.Append("<table><thead><tr>");
        foreach (var h in headers) _body.Append($"<th>{E(h)}</th>");
        _body.Append("</tr></thead><tbody>");
        foreach (var r in rows)
        {
            _body.Append("<tr>");
            foreach (var c in r)
            {
                string cls = c.StartsWith('-') && c.Length > 1 && char.IsDigit(c[1]) ? " class=\"neg\"" : "";
                _body.Append($"<td{cls}>{E(c)}</td>");
            }
            _body.Append("</tr>");
        }
        _body.Append("</tbody></table></div>");
        return this;
    }

    /// <summary>Embeds an SVG chart produced by <see cref="Charts"/>.</summary>
    public HtmlReport Chart(string svg, string? caption = null)
    {
        _body.Append("<figure class=\"panel\">").Append(svg.Replace("<svg ", "<svg style=\"width:100%;height:auto\" "));
        if (caption is not null) _body.Append($"<figcaption>{E(caption)}</figcaption>");
        _body.Append("</figure>");
        return this;
    }

    /// <summary>Two charts side by side (stacked on narrow screens).</summary>
    public HtmlReport ChartPair(string left, string right)
    {
        _body.Append("<div class=\"pair\">");
        Chart(left);
        Chart(right);
        _body.Append("</div>");
        return this;
    }

    /// <summary>Appends raw, trusted HTML.</summary>
    public HtmlReport Html(string html)
    {
        _body.Append(html);
        return this;
    }

    /// <summary>The complete HTML document.</summary>
    public string Build()
    {
        var t = Theme;
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>{E(Title)}</title><style>");
        sb.Append($":root{{--bg:{t.Background};--panel:{t.Panel};--grid:{t.Grid};--text:{t.Text};--muted:{t.MutedText};--up:{t.Up};--down:{t.Down};--accent:{t.Accent}}}");
        sb.Append("*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font-family:Inter,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;line-height:1.5}");
        sb.Append("main{max-width:1180px;margin:0 auto;padding:32px 20px 48px}header{border-bottom:1px solid var(--grid);margin-bottom:24px;padding-bottom:16px}");
        sb.Append("h1{margin:0;font-size:28px;letter-spacing:-.02em}.sub{color:var(--muted);margin-top:4px}h2{font-size:19px;margin:32px 0 12px}h3{font-size:16px;margin:24px 0 8px}");
        sb.Append(".brand{display:inline-block;font-size:12px;color:var(--accent);font-weight:600;letter-spacing:.08em;text-transform:uppercase;margin-bottom:6px}");
        sb.Append(".cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:12px;margin:12px 0}");
        sb.Append(".card{background:var(--panel);border:1px solid var(--grid);border-radius:12px;padding:14px 16px}.label{color:var(--muted);font-size:12px;text-transform:uppercase;letter-spacing:.06em}");
        sb.Append(".value{font-size:22px;font-weight:650;margin-top:4px;font-variant-numeric:tabular-nums}.hint{color:var(--muted);font-size:12px}.pos{color:var(--up)}.neg{color:var(--down)}");
        sb.Append(".panel{background:var(--panel);border:1px solid var(--grid);border-radius:12px;padding:12px;margin:12px 0;overflow-x:auto}figure{margin:12px 0}figcaption,.caption{color:var(--muted);font-size:13px;padding:6px 4px}");
        sb.Append(".pair{display:grid;grid-template-columns:1fr 1fr;gap:12px}@media(max-width:820px){.pair{grid-template-columns:1fr}}");
        sb.Append("table{border-collapse:collapse;width:100%;font-size:13px;font-variant-numeric:tabular-nums}th,td{padding:7px 10px;text-align:right;border-bottom:1px solid var(--grid);white-space:nowrap}th:first-child,td:first-child{text-align:left}th{color:var(--muted);font-weight:600}");
        sb.Append("footer{margin-top:40px;color:var(--muted);font-size:12px;border-top:1px solid var(--grid);padding-top:12px}</style></head><body><main>");
        sb.Append($"<header><div class=\"brand\">{FinanceInfo.Product}</div><h1>{E(Title)}</h1>");
        if (Subtitle is not null) sb.Append($"<div class=\"sub\">{E(Subtitle)}</div>");
        sb.Append("</header>").Append(_body);
        sb.Append($"<footer>Generated {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC by {FinanceInfo.Product} {FinanceInfo.Version} · {FinanceInfo.CreditEnglish} · {FinanceInfo.CreditIndonesian}</footer>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    /// <summary>Writes the document to a file.</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Build(), new UTF8Encoding(false));
    }
}

/// <summary>Performance statistics computed for a tearsheet.</summary>
public sealed record TearsheetStats(
    double TotalReturn, double Cagr, double Volatility, double Sharpe, double Sortino, double MaxDrawdown, double Calmar,
    double Skewness, double Kurtosis, double VaR95, double CVaR95, double BestDay, double WorstDay, double WinRate,
    double? Beta, double? Alpha, double? Correlation);

/// <summary>
/// Pyfolio-style performance tearsheet: headline metrics, equity curve versus benchmark, drawdown,
/// return distribution with VaR, monthly returns heatmap, yearly returns, and rolling volatility.
/// </summary>
public static class Tearsheet
{
    /// <summary>Computes the statistics shown on a tearsheet.</summary>
    public static TearsheetStats ComputeStats(ReadOnlySpan<double> returns, ReadOnlySpan<double> benchmark = default, double riskFree = 0, double periodsPerYear = 252)
    {
        int n = returns.Length;
        if (n < 2) throw new ArgumentException("Need at least two returns.");
        double eq = 1, peak = 1, mdd = 0;
        foreach (var r in returns)
        {
            eq *= 1 + r;
            peak = Math.Max(peak, eq);
            mdd = Math.Min(mdd, eq / peak - 1);
        }
        double total = eq - 1;
        double cagr = eq > 0 ? Math.Pow(eq, periodsPerYear / n) - 1 : -1;
        double mean = Stats.Mean(returns), sd = Stats.StdDev(returns);
        double rf = riskFree / periodsPerYear;
        double sharpe = sd > 1e-12 ? (mean - rf) / sd * Math.Sqrt(periodsPerYear) : 0;
        double down = 0;
        foreach (var r in returns) if (r - rf < 0) down += (r - rf) * (r - rf);
        double dd = Math.Sqrt(down / n);
        double sortino = dd > 0 ? (mean - rf) / dd * Math.Sqrt(periodsPerYear) : 0;
        var sorted = returns.ToArray();
        Array.Sort(sorted);
        double var95 = -Stats.QuantileSorted(sorted, 0.05);
        int tail = Math.Max(1, (int)Math.Floor(0.05 * n));
        double cvar = 0;
        for (int i = 0; i < tail; i++) cvar += sorted[i];
        cvar = -cvar / tail;
        int wins = 0;
        foreach (var r in returns) if (r > 0) wins++;
        double? beta = null, alpha = null, corr = null;
        if (benchmark.Length == n)
        {
            var reg = Regression.Simple(benchmark, returns);
            beta = reg.Coefficients[1];
            alpha = reg.Coefficients[0] * periodsPerYear;
            corr = Stats.Correlation(returns, benchmark);
        }
        return new TearsheetStats(total, cagr, sd * Math.Sqrt(periodsPerYear), sharpe, sortino, mdd, mdd < 0 ? cagr / -mdd : 0,
            Stats.Skewness(returns), Stats.ExcessKurtosis(returns), var95, cvar, sorted[^1], sorted[0], (double)wins / n, beta, alpha, corr);
    }

    /// <summary>
    /// Builds the tearsheet HTML. <paramref name="dates"/> are the dates of each return.
    /// </summary>
    public static HtmlReport Build(string title, IReadOnlyList<DateTime> dates, double[] returns, double[]? benchmark = null,
        string strategyName = "Strategy", string benchmarkName = "Benchmark", ChartTheme? theme = null, double riskFree = 0)
    {
        if (dates.Count != returns.Length) throw new ArgumentException("One date per return is required.");
        var t = theme ?? ChartTheme.Dark;
        var s = ComputeStats(returns, benchmark ?? default, riskFree);
        var report = new HtmlReport(title, t)
        {
            Subtitle = $"{dates[0]:yyyy-MM-dd} → {dates[^1]:yyyy-MM-dd} · {returns.Length} periods",
        };
        static string P(double v) => v.ToString("P2", CultureInfo.InvariantCulture);
        static string N(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        report.Metrics(new[]
        {
            new MetricCard("Total return", P(s.TotalReturn), null, s.TotalReturn >= 0),
            new MetricCard("CAGR", P(s.Cagr), null, s.Cagr >= 0),
            new MetricCard("Sharpe", N(s.Sharpe), "annualised"),
            new MetricCard("Sortino", N(s.Sortino), "annualised"),
            new MetricCard("Max drawdown", P(s.MaxDrawdown), null, false),
            new MetricCard("Volatility", P(s.Volatility), "annualised"),
        });

        var equity = new double[returns.Length];
        var bench = benchmark is null ? null : new double[returns.Length];
        double e = 1, b = 1;
        for (int i = 0; i < returns.Length; i++)
        {
            e *= 1 + returns[i];
            equity[i] = e;
            if (bench is not null) { b *= 1 + benchmark![i]; bench[i] = b; }
        }
        var lines = new List<LineSeries> { new(strategyName, equity) { Area = true, Color = t.Accent } };
        if (bench is not null) lines.Add(new LineSeries(benchmarkName, bench) { Color = t.MutedText, Dash = "5 4", StrokeWidth = 1.5 });
        report.Heading("Cumulative performance");
        report.Chart(Charts.Line(lines, new ChartOptions { Theme = t, Title = "Growth of 1", Height = 380, YFormat = "0.00" }, dates));

        var ddSeries = new double[returns.Length];
        double peak = 1;
        for (int i = 0; i < equity.Length; i++) { peak = Math.Max(peak, equity[i]); ddSeries[i] = equity[i] / peak - 1; }
        report.Chart(Charts.Drawdown(dates, ddSeries, new ChartOptions { Theme = t, Title = "Underwater", Height = 240 }));

        report.Heading("Return distribution");
        var hist = Charts.Histogram(returns, 60, new ChartOptions { Theme = t, Title = "Daily returns", Width = 560, Height = 340, XFormat = "P1" },
            new[] { new ReferenceLine(-s.VaR95, $"VaR 95% {P(s.VaR95)}"), new ReferenceLine(-s.CVaR95, $"CVaR 95% {P(s.CVaR95)}", t.Series(4)) });
        var rollVol = new double[returns.Length];
        const int w = 63;
        for (int i = 0; i < returns.Length; i++)
            rollVol[i] = i + 1 >= w ? Stats.StdDev(returns.AsSpan(i + 1 - w, w)) * Math.Sqrt(252) : double.NaN;
        var vol = Charts.Line(new[] { new LineSeries("Rolling vol (63d)", rollVol) { Color = t.Series(2) } },
            new ChartOptions { Theme = t, Title = "Rolling volatility (63 periods)", Width = 560, Height = 340, YFormat = "P0", ShowLegend = false }, dates);
        report.ChartPair(hist, vol);

        // Monthly returns heatmap (years × months).
        var monthly = new SortedDictionary<int, double[]>();
        for (int i = 0; i < returns.Length; i++)
        {
            var d = dates[i];
            if (!monthly.TryGetValue(d.Year, out var row)) monthly[d.Year] = row = Enumerable.Repeat(double.NaN, 12).ToArray();
            row[d.Month - 1] = (double.IsNaN(row[d.Month - 1]) ? 1 : 1 + row[d.Month - 1]) * (1 + returns[i]) - 1;
        }
        var years = monthly.Keys.ToArray();
        var grid = new double[years.Length, 12];
        for (int y = 0; y < years.Length; y++)
            for (int m = 0; m < 12; m++) grid[y, m] = monthly[years[y]][m] * 100;
        report.Heading("Monthly returns (%)");
        report.Chart(Charts.Heatmap(grid, years.Select(y => y.ToString(CultureInfo.InvariantCulture)).ToArray(),
            CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames.Take(12).ToArray(),
            new ChartOptions { Theme = t, Width = 960, Height = Math.Max(220, 90 + 46 * years.Length) }, "0.0"));

        var yearly = years.Select(y => monthly[y].Where(v => !double.IsNaN(v)).Aggregate(1.0, (acc, v) => acc * (1 + v)) - 1).ToArray();
        report.Chart(Charts.Bar(years.Select(y => y.ToString(CultureInfo.InvariantCulture)).ToArray(), yearly,
            new ChartOptions { Theme = t, Title = "Annual returns", Height = 300, YFormat = "P0" }));

        var kv = new List<(string, string)>
        {
            ("Total return", P(s.TotalReturn)), ("CAGR", P(s.Cagr)), ("Annual volatility", P(s.Volatility)),
            ("Sharpe ratio", N(s.Sharpe)), ("Sortino ratio", N(s.Sortino)), ("Calmar ratio", N(s.Calmar)),
            ("Max drawdown", P(s.MaxDrawdown)), ("Skewness", N(s.Skewness)), ("Excess kurtosis", N(s.Kurtosis)),
            ("Daily VaR 95%", P(s.VaR95)), ("Daily CVaR 95%", P(s.CVaR95)), ("Best period", P(s.BestDay)),
            ("Worst period", P(s.WorstDay)), ("Win rate", P(s.WinRate)),
        };
        if (s.Beta is { } beta) kv.Add(("Beta", N(beta)));
        if (s.Alpha is { } alpha) kv.Add(("Alpha (annual)", P(alpha)));
        if (s.Correlation is { } corr) kv.Add(("Correlation", N(corr)));
        report.Heading("Statistics");
        report.KeyValues(kv);
        return report;
    }
}
