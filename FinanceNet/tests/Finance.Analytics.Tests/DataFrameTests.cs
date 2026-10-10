using Finance.Core.Market;
using Finance.DataFrame;
using Xunit;

namespace Finance.Analytics.Tests;

public class DataFrameTests
{
    private static OhlcvFrame Frame(int n = 300) => OhlcvFrame.Synthetic(n, "TEST", seed: 11);

    [Fact]
    public void BarsRoundTrip()
    {
        var bars = SyntheticMarket.GeometricBrownianBars(50);
        var f = OhlcvFrame.FromBars(bars);
        Assert.Equal(bars, f.ToBars());
        Assert.Equal(50, f.Count);
        Assert.Equal(bars[7], f[7]);
    }

    [Fact]
    public void CsvRoundTripIsExact()
    {
        var f = Frame(120);
        var g = OhlcvFrame.ParseCsv(f.ToCsv(), "TEST");
        Assert.Equal(f.ToBars(), g.ToBars());
    }

    [Fact]
    public void CsvParsesYahooStyleHeaderAndSkipsNullRows()
    {
        const string csv = "Date,Open,High,Low,Close,Adj Close,Volume\n" +
                           "2024-01-03,10,11,9,10.5,10.4,1000\n" +
                           "2024-01-02,9,10,8,9.5,9.4,900\n" +
                           "2024-01-04,null,null,null,null,null,null\n";
        var f = OhlcvFrame.ParseCsv(csv);
        Assert.Equal(2, f.Count);
        Assert.Equal(new DateTime(2024, 1, 2), f.Time[0]); // sorted
        Assert.Equal(10.5, f.Close[1]);
        Assert.Equal(900, f.Volume[0]);
    }

    [Fact]
    public void SliceIsInclusive()
    {
        var f = Frame();
        var from = f.Time[10];
        var to = f.Time[20];
        var s = f.Slice(from, to);
        Assert.Equal(11, s.Count);
        Assert.Equal(from, s.Time[0]);
        Assert.Equal(to, s.Time[^1]);
    }

    [Fact]
    public void MonthlyResampleMatchesNaiveGrouping()
    {
        var f = Frame(400);
        var m = f.Resample(ResamplePeriod.Month);
        var groups = f.ToBars().GroupBy(b => (b.Time.Year, b.Time.Month)).ToList();
        Assert.Equal(groups.Count, m.Count);
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i].ToList();
            Assert.Equal(g[0].Open, m.Open[i]);
            Assert.Equal(g.Max(b => b.High), m.High[i]);
            Assert.Equal(g.Min(b => b.Low), m.Low[i]);
            Assert.Equal(g[^1].Close, m.Close[i]);
            Assert.Equal(g.Sum(b => b.Volume), m.Volume[i], 6);
        }
    }

    [Fact]
    public void WeeklyResampleGroupsByMondayWeeks()
    {
        var f = Frame(60);
        var w = f.Resample(ResamplePeriod.Week);
        // Synthetic bars skip weekends, so every week has at most 5 bars.
        Assert.InRange(w.Count, 60 / 5, 60 / 5 + 1);
        Assert.All(w.Time, t => Assert.NotEqual(DayOfWeek.Saturday, t.DayOfWeek));
    }

    [Fact]
    public void PanelInnerJoinKeepsCommonDatesOnly()
    {
        var d = Enumerable.Range(0, 6).Select(i => new DateTime(2024, 1, 1).AddDays(i)).ToArray();
        var a = ("A", d, new double[] { 1, 2, 3, 4, 5, 6 });
        var b = ("B", new[] { d[1], d[2], d[4], d[5] }, new double[] { 20, 30, 50, 60 });
        var p = PricePanel.Align(new[] { a, b });
        Assert.Equal(new[] { d[1], d[2], d[4], d[5] }, p.Dates);
        Assert.Equal(new double[] { 2, 3, 5, 6 }, p["A"]);
        Assert.Equal(new double[] { 20, 30, 50, 60 }, p["B"]);
        var r = p.ToReturnsMatrix();
        Assert.Equal(3, r.Rows);
        Assert.Equal(3.0 / 2 - 1, r[0, 0], 12);
        Assert.Equal(Math.Log(60.0 / 50), p.ToReturnsMatrix(logReturns: true)[2, 1], 12);
    }

    [Fact]
    public void PanelCsvRoundTrip()
    {
        var rows = SyntheticMarket.CorrelatedPrices(30, new[] { 0.05, 0.1 }, new[] { 0.2, 0.3 }, new double[,] { { 1, 0.5 }, { 0.5, 1 } });
        var dates = Enumerable.Range(0, 30).Select(i => new DateTime(2023, 1, 2).AddDays(i)).ToArray();
        var p = PricePanel.FromRows(dates, new[] { "X", "Y" }, rows);
        var q = PricePanel.ParseCsv(p.ToCsv());
        Assert.Equal(p.Dates, q.Dates);
        Assert.Equal(p["Y"], q["Y"]);
    }

    [Fact]
    public void GraviFrameRoundTrip()
    {
        var f = Frame(40);
        var gf = f.ToGraviFrame();
        Assert.Equal(40, gf.RowCount);
        Assert.Equal(6, gf.ColumnCount);
        var back = gf.ToOhlcvFrame("TEST");
        Assert.Equal(f.ToBars(), back.ToBars());
    }
}
