# Finance.Visualization

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/visualization.md) · Package: `Gravicode.Finance.Visualization`

Dependency-free financial charts rendered as **SVG strings**, plus a builder for self-contained
**HTML reports** and a pyfolio-style **tearsheet**. SVG works everywhere: browsers, .NET
Interactive notebooks, Markdown, and as files you can embed in reports. Dark and light themes are
included.

Namespace: `Finance.Visualization`.

## API tour

### Charts

Every method returns an SVG string; `Charts.Save(svg, path)` writes it to disk.

```csharp
using Finance.Visualization;

var opts = new ChartOptions { Title = "Equity vs benchmark", Width = 960, Height = 420, YFormat = "0.00" };
string line = Charts.Line(new[]
{
    new LineSeries("Strategy", equity) { Area = true },
    new LineSeries("Benchmark", benchmark) { Dash = "6 4" },
}, opts, dates);

string candles = Charts.Candlestick(bars, new ChartOptions { Title = "DEMO" },
    overlays: new[] { new LineSeries("SMA 50", sma50) },
    showVolume: true,
    trades: new[] { new TradeMarker(120, bars[120].Close, Side.Buy, "entry") });

string bar = Charts.Bar(new[] { "2021", "2022", "2023" }, new[] { 0.12, -0.08, 0.21 });
string heat = Charts.Heatmap(correlationMatrix, assetNames, new ChartOptions { Title = "Correlation" });
string hist = Charts.Histogram(returns, bins: 60,
    lines: new[] { new ReferenceLine(-0.025, "VaR 99%"), new ReferenceLine(-0.031, "ES 99%") }, normalFit: true);
string dd = Charts.Drawdown(dates, drawdownSeries);

string frontier = Charts.Scatter(new[]
{
    new ScatterSeries("Efficient frontier", vols, rets) { Connect = true },
}, new ChartOptions { XLabel = "Volatility", YLabel = "Return" },
   markers: new[] { new ChartMarker(0.14, 0.11, "Max Sharpe") });

Charts.Save(line, "equity.svg");
```

`ChartOptions` controls `Width`, `Height`, `Title`, `Subtitle`, `XLabel`, `YLabel`, `Theme`
(`ChartTheme.Dark` default, `ChartTheme.Light`), `ShowLegend`, `ShowGrid`, `YFormat`, `XFormat`
and `IncludeZero`. `NaN` values break a line rather than drawing to zero. Axis ticks use "nice"
1/2/5×10ᵏ steps (`AxisTicks.Nice`) and date ticks adapt to the span (`AxisTicks.DateTicks`).

### HTML reports

```csharp
var report = new HtmlReport("Monthly risk report")
    .Heading("Summary")
    .Metrics(new[]
    {
        new MetricCard("Return", "12.4%", Positive: true),
        new MetricCard("Max drawdown", "-8.1%", Positive: false),
    })
    .ChartPair(line, dd)
    .Table(new[] { "Asset", "Weight" }, new[] { new[] { "AAA", "40%" }, new[] { "BBB", "60%" } })
    .Paragraph("Generated with Finance.Net.");
report.Save("report.html");          // one file, no external assets
```

The footer of every report carries the English and Indonesian credit lines.

### Tearsheet

```csharp
TearsheetStats stats = Tearsheet.ComputeStats(strategyReturns, benchmarkReturns);
Tearsheet.Build("SMA crossover", dates, strategyReturns, benchmarkReturns, strategyName: "SMA 20/50")
         .Save("tearsheet.html");
```

The tearsheet contains metric cards, equity versus benchmark, the underwater (drawdown) chart, a
return histogram with VaR/CVaR lines, rolling volatility, a monthly-returns heatmap, annual returns
and a statistics table.

## Notes and limitations

- Charts are static SVG; interactivity (crosshair, zoom) lives in the Avalonia Gallery's own
  controls.

---

Made by Gravicode Studios, led by Kang Fadhil
