using System.Xml.Linq;
using Finance.Core;
using Finance.Core.Market;
using Finance.Mathematics;
using Finance.TimeSeries;
using Finance.TimeSeries.Indicators;
using Finance.Visualization;
using Xunit;

namespace Finance.Analytics.Tests;

public class VisualizationTests
{
    private static readonly Bar[] Bars = SyntheticMarket.GeometricBrownianBars(250, seed: 8);

    private static XDocument Parse(string svg)
    {
        var doc = XDocument.Parse(svg);
        Assert.Equal("svg", doc.Root!.Name.LocalName);
        return doc;
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(-3.7, 12.2)]
    [InlineData(99.5, 100.5)]
    [InlineData(5.0, 5.0)]
    public void NiceTicksCoverRangeWithRoundSteps(double lo, double hi)
    {
        var (min, max, ticks) = AxisTicks.Nice(lo, hi);
        Assert.True(min <= lo && max >= hi);
        Assert.InRange(ticks.Length, 3, 12);
        double step = ticks[1] - ticks[0];
        double mant = step / Math.Pow(10, Math.Floor(Math.Log10(step)));
        Assert.Contains(Math.Round(mant, 6), new[] { 1.0, 2.0, 5.0, 10.0 });
    }

    [Fact]
    public void LineChartIsValidSvgWithTitleAndLegend()
    {
        var close = Bars.Select(b => b.Close).ToArray();
        var svg = Charts.Line(new[] { new LineSeries("Close", close) { Area = true }, new LineSeries("SMA 20", Ta.Sma(close, 20)) },
            new ChartOptions { Title = "Prices" }, Bars.Select(b => b.Time).ToList());
        var doc = Parse(svg);
        Assert.Contains("Prices", svg);
        Assert.Contains("SMA 20", svg);
        Assert.True(doc.Descendants().Count(e => e.Name.LocalName == "path") >= 3);
    }

    [Fact]
    public void CandlestickDrawsOneBodyPerBarPlusVolume()
    {
        var close = Bars.Select(b => b.Close).ToArray();
        var svg = Charts.Candlestick(Bars, new ChartOptions { Title = "Candles", Theme = ChartTheme.Light, Height = 600 },
            new[] { new LineSeries("EMA", Ta.Ema(close, 10)) }, true,
            new[] { new TradeMarker(30, Bars[30].Close, Side.Buy, "B"), new TradeMarker(60, Bars[60].Close, Side.Sell) });
        var doc = Parse(svg);
        int rects = doc.Descendants().Count(e => e.Name.LocalName == "rect");
        Assert.True(rects >= 2 * Bars.Length, $"{rects} rects");
        Assert.Equal(2, doc.Descendants().Count(e => e.Name.LocalName == "polygon"));
    }

    [Fact]
    public void ScatterHeatmapHistogramBarAndDrawdownRender()
    {
        var x = new[] { 0.1, 0.12, 0.15, 0.2 };
        var y = new[] { 0.05, 0.07, 0.09, 0.1 };
        Parse(Charts.Scatter(new[] { new ScatterSeries("Frontier", x, y) { Connect = true } }, new ChartOptions { Title = "EF", XFormat = "P0", YFormat = "P0" },
            new[] { new ChartMarker(0.15, 0.09, "Max Sharpe") }));

        var corr = new Matrix(3, 3, new double[] { 1, 0.5, -0.3, 0.5, 1, 0.2, -0.3, 0.2, 1 });
        var heat = Charts.Heatmap(corr, new[] { "AAA", "BBB", "CCC" }, new ChartOptions { Title = "Correlation" });
        Parse(heat);
        Assert.Contains("-0.30", heat);

        var rng = new FastRandom(1);
        var r = Enumerable.Range(0, 1000).Select(_ => 0.01 * rng.NextGaussian()).ToArray();
        Parse(Charts.Histogram(r, 40, new ChartOptions { Title = "Returns" }, new[] { new ReferenceLine(-0.0165, "VaR 95%") }));
        Parse(Charts.Bar(new[] { "2021", "2022", "2023" }, new[] { 0.1, -0.05, 0.2 }, new ChartOptions { YFormat = "P0" }));
        var dates = Enumerable.Range(0, r.Length).Select(i => new DateTime(2020, 1, 1).AddDays(i)).ToList();
        Parse(Charts.Drawdown(dates, Returns.DrawdownFromReturns(r)));
    }

    [Fact]
    public void LineHandlesNaNGapsAndEmptyInput()
    {
        Parse(Charts.Line(new[] { new LineSeries("x", new[] { double.NaN, 1, 2, double.NaN, 3 }) }));
        Parse(Charts.Line(Array.Empty<LineSeries>()));
    }

    [Fact]
    public void TearsheetHtmlContainsMetricsChartsAndCredit()
    {
        var rng = new FastRandom(4);
        int n = 600;
        var r = Enumerable.Range(0, n).Select(_ => 0.0004 + 0.011 * rng.NextGaussian()).ToArray();
        var b = r.Select(v => 0.6 * v + 0.004 * rng.NextGaussian()).ToArray();
        var dates = Enumerable.Range(0, n).Select(i => new DateTime(2021, 1, 4).AddDays(i * 7 / 5.0)).ToList();
        var html = Tearsheet.Build("Momentum strategy", dates, r, b).Build();
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("Sharpe", html);
        Assert.Contains("Monthly returns", html);
        Assert.Contains(FinanceInfo.CreditIndonesian, html);
        Assert.True(html.Split("<svg").Length - 1 >= 6);

        var stats = Tearsheet.ComputeStats(r, b);
        Assert.Equal(Returns.Sharpe(r), stats.Sharpe, 10);
        Assert.Equal(Returns.MaxDrawdown(Returns.EquityCurve(r)).MaxDrawdown, stats.MaxDrawdown, 12);
        Assert.InRange(stats.Beta!.Value, 1.2, 2.0); // b ≈ 0.6 r, so r ≈ 1.6 b
    }

    [Fact]
    public void ReportSavesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fn-report-{Guid.NewGuid():N}.html");
        new HtmlReport("Test").Paragraph("<b>escaped</b>").Metrics(new[] { new MetricCard("A", "1") }).Save(path);
        var text = File.ReadAllText(path);
        Assert.Contains("&lt;b&gt;escaped&lt;/b&gt;", text);
        File.Delete(path);
    }
}
