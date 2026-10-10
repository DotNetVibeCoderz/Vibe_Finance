# Finance.Visualization

[← Indeks](../index.md) · [English](../../en/modules/visualization.md) · Paket: `Gravicode.Finance.Visualization`

Grafik keuangan tanpa dependensi yang dirender sebagai **string SVG**, ditambah pembangun **laporan
HTML** mandiri dan **tearsheet** ala pyfolio. SVG bisa dipakai di mana saja: browser, notebook .NET
Interactive, Markdown, dan sebagai file yang disematkan ke laporan. Tersedia tema gelap dan terang.

Namespace: `Finance.Visualization`.

## Tur API

### Grafik

Setiap metode mengembalikan string SVG; `Charts.Save(svg, path)` menyimpannya ke disk.

```csharp
using Finance.Visualization;

var opts = new ChartOptions { Title = "Ekuitas vs benchmark", Width = 960, Height = 420, YFormat = "0.00" };
string line = Charts.Line(new[]
{
    new LineSeries("Strategi", equity) { Area = true },
    new LineSeries("Benchmark", benchmark) { Dash = "6 4" },
}, opts, dates);

string candles = Charts.Candlestick(bars, new ChartOptions { Title = "DEMO" },
    overlays: new[] { new LineSeries("SMA 50", sma50) },
    showVolume: true,
    trades: new[] { new TradeMarker(120, bars[120].Close, Side.Buy, "entry") });

string bar = Charts.Bar(new[] { "2021", "2022", "2023" }, new[] { 0.12, -0.08, 0.21 });
string heat = Charts.Heatmap(correlationMatrix, assetNames, new ChartOptions { Title = "Korelasi" });
string hist = Charts.Histogram(returns, bins: 60,
    lines: new[] { new ReferenceLine(-0.025, "VaR 99%"), new ReferenceLine(-0.031, "ES 99%") }, normalFit: true);
string dd = Charts.Drawdown(dates, drawdownSeries);

string frontier = Charts.Scatter(new[]
{
    new ScatterSeries("Efficient frontier", vols, rets) { Connect = true },
}, new ChartOptions { XLabel = "Volatilitas", YLabel = "Return" },
   markers: new[] { new ChartMarker(0.14, 0.11, "Max Sharpe") });

Charts.Save(line, "equity.svg");
```

`ChartOptions` mengatur `Width`, `Height`, `Title`, `Subtitle`, `XLabel`, `YLabel`, `Theme`
(`ChartTheme.Dark` bawaan, `ChartTheme.Light`), `ShowLegend`, `ShowGrid`, `YFormat`, `XFormat`, dan
`IncludeZero`. Nilai `NaN` memutus garis alih-alih menariknya ke nol. Tick sumbu memakai langkah
"rapi" 1/2/5×10ᵏ (`AxisTicks.Nice`) dan tick tanggal menyesuaikan rentang waktu
(`AxisTicks.DateTicks`).

### Laporan HTML

```csharp
var report = new HtmlReport("Laporan risiko bulanan")
    .Heading("Ringkasan")
    .Metrics(new[]
    {
        new MetricCard("Return", "12,4%", Positive: true),
        new MetricCard("Max drawdown", "-8,1%", Positive: false),
    })
    .ChartPair(line, dd)
    .Table(new[] { "Aset", "Bobot" }, new[] { new[] { "AAA", "40%" }, new[] { "BBB", "60%" } })
    .Paragraph("Dibuat dengan Finance.Net.");
report.Save("report.html");          // satu file, tanpa aset eksternal
```

Footer setiap laporan memuat baris kredit berbahasa Inggris dan Indonesia.

### Tearsheet

```csharp
TearsheetStats stats = Tearsheet.ComputeStats(strategyReturns, benchmarkReturns);
Tearsheet.Build("SMA crossover", dates, strategyReturns, benchmarkReturns, strategyName: "SMA 20/50")
         .Save("tearsheet.html");
```

Tearsheet berisi kartu metrik, ekuitas vs benchmark, grafik underwater (drawdown), histogram return
dengan garis VaR/CVaR, volatilitas rolling, heatmap return bulanan, return tahunan, dan tabel
statistik.

## Catatan dan batasan

- Grafiknya SVG statis; interaktivitas (crosshair, zoom) ada di kontrol milik Gallery Avalonia.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
