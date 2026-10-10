using Finance.Core;
using Finance.Core.Market;
using Finance.Core.Time;
using Xunit;

namespace Finance.Foundation.Tests;

public class DayCountTests
{
    [Fact]
    public void Actual360_And365()
    {
        var s = new DateOnly(2024, 1, 15);
        var e = new DateOnly(2024, 7, 15);
        Assert.Equal(182 / 360.0, DayCount.YearFraction(s, e, DayCountConvention.Actual360), 12);
        Assert.Equal(182 / 365.0, DayCount.YearFraction(s, e, DayCountConvention.Actual365Fixed), 12);
    }

    [Fact]
    public void Thirty360_EndOfMonthRules()
    {
        // 30/360 US: Jan 31 -> Mar 31 counts as 60 days.
        Assert.Equal(60 / 360.0, DayCount.YearFraction(new DateOnly(2023, 1, 31), new DateOnly(2023, 3, 31), DayCountConvention.Thirty360), 12);
    }

    [Fact]
    public void ActualActualIsda_SplitsAcrossLeapYear()
    {
        double yf = DayCount.YearFraction(new DateOnly(2023, 11, 1), new DateOnly(2024, 5, 1), DayCountConvention.ActualActualIsda);
        Assert.Equal(61 / 365.0 + 121 / 366.0, yf, 12);
    }

    [Theory]
    [InlineData("3M", 3, TenorUnit.Months)]
    [InlineData("10Y", 10, TenorUnit.Years)]
    [InlineData("2w", 2, TenorUnit.Weeks)]
    [InlineData("ON", 1, TenorUnit.Days)]
    public void Tenor_Parses(string text, int n, TenorUnit unit)
    {
        Assert.Equal(new Tenor(n, unit), Tenor.Parse(text));
    }
}

public class CalendarTests
{
    [Fact]
    public void Nyse_KnowsFixedRuleHolidays()
    {
        var cal = BusinessCalendar.NewYorkStockExchange;
        Assert.False(cal.IsBusinessDay(new DateOnly(2024, 7, 4)));
        Assert.False(cal.IsBusinessDay(new DateOnly(2024, 11, 28))); // Thanksgiving
        Assert.False(cal.IsBusinessDay(new DateOnly(2024, 1, 15)));  // MLK
        Assert.True(cal.IsBusinessDay(new DateOnly(2024, 7, 5)));
    }

    [Fact]
    public void ModifiedFollowing_StaysInMonth()
    {
        var cal = BusinessCalendar.WeekendsOnly;
        // 2024-08-31 is a Saturday; following would roll into September.
        Assert.Equal(new DateOnly(2024, 8, 30), cal.Adjust(new DateOnly(2024, 8, 31), BusinessDayConvention.ModifiedFollowing));
        Assert.Equal(new DateOnly(2024, 9, 2), cal.Adjust(new DateOnly(2024, 8, 31), BusinessDayConvention.Following));
    }

    [Fact]
    public void AddBusinessDays_SkipsWeekend()
    {
        var cal = BusinessCalendar.WeekendsOnly;
        Assert.Equal(new DateOnly(2024, 3, 11), cal.AddBusinessDays(new DateOnly(2024, 3, 8), 1));
        Assert.Equal(5, cal.BusinessDaysBetween(new DateOnly(2024, 3, 1), new DateOnly(2024, 3, 8)));
    }
}

public class TradingTypeTests
{
    [Fact]
    public void Position_AverageCostAndRealisedPnl()
    {
        var p = new Position("AAPL");
        p.Apply(new Fill(1, "AAPL", Side.Buy, 10, 100, default));
        p.Apply(new Fill(2, "AAPL", Side.Buy, 10, 110, default));
        Assert.Equal(105, p.AveragePrice, 12);
        p.Apply(new Fill(3, "AAPL", Side.Sell, 5, 120, default, Commission: 1));
        Assert.Equal(15, p.Quantity, 12);
        Assert.Equal(5 * 15 - 1, p.RealizedPnl, 12);
        Assert.Equal(15 * 15, p.UnrealizedPnl(120), 12);
    }

    [Fact]
    public void Position_FlipsThroughFlat()
    {
        var p = new Position("X");
        p.Apply(new Fill(1, "X", Side.Buy, 10, 50, default));
        p.Apply(new Fill(2, "X", Side.Sell, 15, 60, default));
        Assert.Equal(-5, p.Quantity, 12);
        Assert.Equal(60, p.AveragePrice, 12);
        Assert.Equal(100, p.RealizedPnl, 12);
    }

    [Fact]
    public void Order_PartialFillsAverage()
    {
        var o = Order.LimitBuy("BTCUSDT", 2, 50_000);
        o.ApplyFill(1, 49_990);
        Assert.Equal(OrderStatus.PartiallyFilled, o.Status);
        o.ApplyFill(1, 50_010);
        Assert.Equal(OrderStatus.Filled, o.Status);
        Assert.Equal(50_000, o.AverageFillPrice, 9);
    }

    [Fact]
    public void OrderBook_SweepAndImbalance()
    {
        var book = new OrderBook("X");
        book.SetBids(new[] { new BookLevel(99, 10), new BookLevel(100, 5) });
        book.SetAsks(new[] { new BookLevel(102, 10), new BookLevel(101, 5) });
        Assert.Equal(100, book.BestBid);
        Assert.Equal(101, book.BestAsk);
        Assert.Equal((5 * 101 + 5 * 102) / 10.0, book.SweepPrice(Side.Buy, 10), 12);
        Assert.Equal(0, book.Imbalance(), 12);
        book.Update(Side.Buy, 100.5, 20);
        Assert.Equal(100.5, book.BestBid);
        book.Update(Side.Buy, 100.5, 0);
        Assert.Equal(100, book.BestBid);
    }
}

public class SyntheticTests
{
    [Fact]
    public void GbmBars_AreReproducibleAndConsistent()
    {
        var a = SyntheticMarket.GeometricBrownianBars(300, seed: 1);
        var b = SyntheticMarket.GeometricBrownianBars(300, seed: 1);
        Assert.Equal(a, b);
        Assert.All(a, bar =>
        {
            Assert.True(bar.High >= Math.Max(bar.Open, bar.Close));
            Assert.True(bar.Low <= Math.Min(bar.Open, bar.Close));
            Assert.True(bar.Time.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
        });
    }

    [Fact]
    public void CorrelatedPrices_HaveTargetCorrelation()
    {
        var corr = new double[,] { { 1, 0.7 }, { 0.7, 1 } };
        var prices = SyntheticMarket.CorrelatedPrices(5000, new[] { 0.05, 0.05 }, new[] { 0.2, 0.3 }, corr);
        var r0 = new double[prices.Length - 1];
        var r1 = new double[prices.Length - 1];
        for (int t = 1; t < prices.Length; t++)
        {
            r0[t - 1] = Math.Log(prices[t][0] / prices[t - 1][0]);
            r1[t - 1] = Math.Log(prices[t][1] / prices[t - 1][1]);
        }
        Assert.InRange(Finance.Mathematics.Stats.Correlation(r0, r1), 0.66, 0.74);
    }

    [Fact]
    public void FastRandom_UniformMoments()
    {
        var rng = new FastRandom(123);
        double s = 0;
        const int n = 100_000;
        for (int i = 0; i < n; i++) s += rng.NextDouble();
        Assert.InRange(s / n, 0.495, 0.505);
    }
}
