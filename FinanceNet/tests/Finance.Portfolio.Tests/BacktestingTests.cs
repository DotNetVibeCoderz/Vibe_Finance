using Finance.Backtesting;
using Finance.Backtesting.Strategies;
using Finance.Core.Market;

namespace Finance.Portfolio.Tests;

public class BacktestingTests
{
    private static readonly DateTime T0 = new(2024, 1, 1);

    private static Bar B(int day, double o, double h, double l, double c, double v = 1_000_000)
        => new(T0.AddDays(day), o, h, l, c, v);

    private static Bar[] Flat(params double[] closes)
        => closes.Select((c, i) => B(i, c, c, c, c)).ToArray();

    /// <summary>Runs an arbitrary action per bar.</summary>
    private sealed class Script : Strategy
    {
        private readonly Action<StrategyContext, BarSlice, int> _onBar;
        private int _i;
        public Script(Action<StrategyContext, BarSlice, int> onBar) => _onBar = onBar;
        public override void OnBar(StrategyContext context, BarSlice slice) => _onBar(context, slice, _i++);
    }

    [Fact]
    public void BuyAndHold_NextBarOpen_IsCappedByCash()
    {
        var bars = new[] { B(0, 10, 10, 10, 10), B(1, 11, 12, 11, 12), B(2, 12, 13, 12, 13) };
        var r = new BacktestEngine(new BacktestSettings { InitialCash = 1000 }).Run(new BuyAndHoldStrategy(), "X", bars);
        // Ordered 100 shares at close 10, filled at next open 11: only 90 affordable.
        Assert.Single(r.Fills);
        Assert.Equal(90, r.Fills[0].Quantity);
        Assert.Equal(11, r.Fills[0].Price);
        Assert.Equal(new[] { 1000.0, 1090, 1180 }, r.Equity);
    }

    [Fact]
    public void BuyAndHold_CurrentBarClose_FillsAtClose()
    {
        var bars = new[] { B(0, 10, 10, 10, 10), B(1, 11, 12, 11, 12), B(2, 12, 13, 12, 13) };
        var r = new BacktestEngine(new BacktestSettings { InitialCash = 1000, FillTiming = FillTiming.CurrentBarClose })
            .Run(new BuyAndHoldStrategy(), "X", bars);
        Assert.Equal(100, r.Fills[0].Quantity);
        Assert.Equal(1300, r.FinalEquity, 9);
    }

    [Theory]
    [InlineData(9.5, 9.0)]   // opens above the limit, trades down through it
    [InlineData(8.8, 8.8)]   // gaps below the limit: better price
    public void LimitBuy_FillsAtLimitOrBetter(double open, double expected)
    {
        var bars = new[] { B(0, 10, 10, 10, 10), B(1, open, 10, 8.5, 9.6) };
        var r = new BacktestEngine().Run(new Script((ctx, _, i) => { if (i == 0) ctx.BuyLimit("X", 10, 9.0); }), "X", bars);
        Assert.Equal(expected, r.Fills.Single().Price, 12);
    }

    [Fact]
    public void LimitBuy_NotTouched_DoesNotFill()
    {
        var bars = new[] { B(0, 10, 10, 10, 10), B(1, 9.6, 10, 9.1, 9.6) };
        var r = new BacktestEngine().Run(new Script((ctx, _, i) => { if (i == 0) ctx.BuyLimit("X", 10, 9.0); }), "X", bars);
        Assert.Empty(r.Fills);
        Assert.Equal(OrderStatus.New, r.Orders.Single().Status);
    }

    [Theory]
    [InlineData(9.5, 9.0)]
    [InlineData(8.5, 8.5)]   // gap through the stop fills at the open
    public void SellStop_FillsAtStopOrGapOpen(double open, double expected)
    {
        var bars = new[] { B(0, 10, 10, 10, 10), B(1, 10, 10, 10, 10), B(2, open, 9.6, 8.4, 9) };
        var r = new BacktestEngine(new BacktestSettings { FillTiming = FillTiming.CurrentBarClose }).Run(new Script((ctx, _, i) =>
        {
            if (i == 0) ctx.Buy("X", 10);
            if (i == 1) ctx.SellStop("X", 10, 9.0);
        }), "X", bars);
        Assert.Equal(2, r.Fills.Count);
        Assert.Equal(expected, r.Fills[1].Price, 12);
    }

    [Fact]
    public void CommissionAndSlippageModels()
    {
        Assert.Equal(1.0, new PercentageCommission(0.001).Calculate(100, 10), 12);
        Assert.Equal(1.0, new PerShareCommission(0.01, 1).Calculate(50, 10), 12);
        Assert.Equal(2.0, new PerShareCommission(0.01, 1).Calculate(200, 10), 12);
        Assert.Equal(5.0, new FixedCommission(5).Calculate(1, 10));
        var bar = B(0, 100, 100, 100, 100, 1000);
        Assert.Equal(100.1, new FixedBpsSlippage(10).AdjustPrice(Side.Buy, 100, 1, bar), 12);
        Assert.Equal(99.9, new FixedBpsSlippage(10).AdjustPrice(Side.Sell, 100, 1, bar), 12);
        var vs = new VolumeShareSlippage(0.025, 0.1);
        Assert.Equal(25, vs.MaxFillQuantity(bar, 60));
        Assert.Equal(100 * (1 + 0.1 * 0.025 * 0.025), vs.AdjustPrice(Side.Buy, 100, 25, bar), 12);
    }

    [Fact]
    public void VolumeShare_SplitsLargeOrdersAcrossBars()
    {
        var bars = Enumerable.Range(0, 5).Select(i => B(i, 10, 10, 10, 10, 1000)).ToArray();
        var settings = new BacktestSettings { Slippage = new VolumeShareSlippage(0.025, 0) };
        var r = new BacktestEngine(settings).Run(new Script((ctx, _, i) => { if (i == 0) ctx.Buy("X", 60); }), "X", bars);
        Assert.Equal(new[] { 25.0, 25, 10 }, r.Fills.Select(f => f.Quantity));
        Assert.Equal(OrderStatus.Filled, r.Orders.Single().Status);
    }

    [Fact]
    public void Shorting_RejectedUnlessAllowed()
    {
        var bars = Flat(10, 10, 10);
        var strategy = () => new Script((ctx, _, i) => { if (i == 0) ctx.Sell("X", 10); });
        var noShort = new BacktestEngine().Run(strategy(), "X", bars);
        Assert.Empty(noShort.Fills);
        Assert.Equal(OrderStatus.Rejected, noShort.Orders.Single().Status);

        var withShort = new BacktestEngine(new BacktestSettings { AllowShort = true }).Run(strategy(), "X", bars);
        Assert.Equal(-10, withShort.FinalPositions["X"].Quantity);
    }

    [Fact]
    public void RoundTrip_PnlIncludesCommissions()
    {
        var bars = Flat(10, 10, 12, 12);
        var settings = new BacktestSettings { InitialCash = 1000, Commission = new FixedCommission(1) };
        var r = new BacktestEngine(settings).Run(new Script((ctx, _, i) =>
        {
            if (i == 0) ctx.Buy("X", 10);
            if (i == 2) ctx.Sell("X", 10);
        }), "X", bars);
        var trip = Assert.Single(r.Trades);
        Assert.Equal(10, trip.EntryPrice);
        Assert.Equal(12, trip.ExitPrice);
        Assert.Equal(18, trip.Pnl, 12);
        Assert.Equal(1018, r.FinalEquity, 12);
        Assert.Equal(1, r.Statistics.WinRate);
    }

    [Fact]
    public void RoundTrip_FlipLongToShortSplitsTrades()
    {
        var bars = Flat(10, 10, 12, 12, 11, 11);
        var r = new BacktestEngine(new BacktestSettings { AllowShort = true }).Run(new Script((ctx, _, i) =>
        {
            if (i == 0) ctx.Buy("X", 10);
            if (i == 2) ctx.Sell("X", 20); // closes the long and opens a 10-share short
            if (i == 4) ctx.Buy("X", 10);
        }), "X", bars);
        Assert.Equal(2, r.Trades.Count);
        Assert.Equal((1, 20.0), (r.Trades[0].Direction, r.Trades[0].Pnl));
        Assert.Equal((-1, 10.0), (r.Trades[1].Direction, r.Trades[1].Pnl));
    }

    [Fact]
    public void OrderTargetQuantity_AccountsForWorkingOrders()
    {
        Order? second = null;
        var r = new BacktestEngine().Run(new Script((ctx, _, i) =>
        {
            if (i != 0) return;
            ctx.OrderTargetQuantity("X", 50);
            second = ctx.OrderTargetQuantity("X", 50);
        }), "X", Flat(10, 10));
        Assert.Null(second);
        Assert.Equal(50, r.FinalPositions["X"].Quantity);
    }

    [Fact]
    public void Statistics_KnownEquityCurve()
    {
        var eq = new[] { 100.0, 120, 90, 130 };
        var s = PerformanceStatistics.Compute(eq, periodsPerYear: 3);
        Assert.Equal(0.3, s.TotalReturn, 12);
        Assert.Equal(0.25, s.MaxDrawdown, 12);
        Assert.Equal(1, s.MaxDrawdownDuration);
        Assert.Equal(0.3, s.Cagr, 12); // exactly one year of 3 periods
        var r = new[] { 0.2, -0.25, 130.0 / 90 - 1 };
        double mean = r.Average();
        double std = Math.Sqrt(r.Sum(x => (x - mean) * (x - mean)) / 2);
        Assert.Equal(mean / std * Math.Sqrt(3), s.SharpeRatio, 12);
        double down = Math.Sqrt(0.25 * 0.25 / 3);
        Assert.Equal(mean / down * Math.Sqrt(3), s.SortinoRatio, 12);
    }

    [Fact]
    public void Vectorized_MatchesEventDrivenBuyAndHold()
    {
        var bars = SyntheticMarket.GeometricBrownianBars(300, seed: 4);
        var closes = bars.Select(b => b.Close).ToArray();
        var v = VectorizedBacktester.Run(closes, Enumerable.Repeat(1.0, closes.Length).ToArray(), initialCapital: 10_000);
        var e = new BacktestEngine(new BacktestSettings { InitialCash = 10_000, FillTiming = FillTiming.CurrentBarClose, FractionalQuantities = true })
            .Run(new BuyAndHoldStrategy(), "X", bars);
        for (int t = 0; t < closes.Length; t++) Assert.Equal(e.Equity[t], v.Equity[t], 6);

        var withCost = VectorizedBacktester.Run(closes, Enumerable.Repeat(1.0, closes.Length).ToArray(), costBps: 10);
        Assert.Equal(-0.001, withCost.Returns[0], 12);
    }

    [Fact]
    public void SmaCrossover_TradesTheTrendAndAccountsConsistently()
    {
        var up = Enumerable.Range(0, 60).Select(i => 100 + i * 1.0);
        var down = Enumerable.Range(0, 60).Select(i => 159 - i * 1.5);
        var bars = Flat(up.Concat(down).ToArray());
        var r = new BacktestEngine(new BacktestSettings { InitialCash = 10_000 }).Run(new SmaCrossoverStrategy(5, 20), "X", bars);
        Assert.Single(r.Trades);
        Assert.True(r.FinalPositions["X"].IsFlat);
        Assert.Equal(r.FinalEquity - 10_000, r.Trades.Sum(t => t.Pnl), 6);
        Assert.True(r.Trades[0].Pnl > 0);
    }

    [Fact]
    public void Rsi_MatchesWilderDefinition()
    {
        var closes = new double[] { 44.34, 44.09, 44.15, 43.61, 44.33, 44.83, 45.10, 45.42, 45.84, 46.08, 45.89, 46.03, 45.61, 46.28, 46.28, 46.00 };
        var rsi = Indicators.Rsi(closes, 14);
        Assert.True(double.IsNaN(rsi[13]));
        // Classic StockCharts example: the first RSI(14) is 70.53.
        Assert.Equal(70.53, rsi[14], 1);
        // Wilder smoothing for the next value, computed by hand from the definition.
        double gain = 0, loss = 0;
        for (int i = 1; i <= 14; i++) { double d = closes[i] - closes[i - 1]; gain += Math.Max(d, 0); loss += Math.Max(-d, 0); }
        gain /= 14; loss /= 14;
        double d15 = closes[15] - closes[14];
        gain = (gain * 13 + Math.Max(d15, 0)) / 14;
        loss = (loss * 13 + Math.Max(-d15, 0)) / 14;
        Assert.Equal(100 - 100 / (1 + gain / loss), rsi[15], 10);
    }

    [Fact]
    public void MomentumRotation_HoldsStrongestAsset()
    {
        var data = new Dictionary<string, Bar[]>
        {
            ["UP"] = Flat(Enumerable.Range(0, 80).Select(i => 100 * Math.Pow(1.002, i)).ToArray()),
            ["MID"] = Flat(Enumerable.Range(0, 80).Select(i => 100 * Math.Pow(1.0005, i)).ToArray()),
            ["DOWN"] = Flat(Enumerable.Range(0, 80).Select(i => 100 * Math.Pow(0.998, i)).ToArray()),
        };
        var strategy = new MomentumRotationStrategy(lookback: 20, topN: 1, rebalanceEvery: 10);
        var r = new BacktestEngine().Run(strategy, data);
        Assert.Equal(new[] { "UP" }, strategy.Holdings);
        Assert.True(r.FinalPositions["UP"].Quantity > 0);
        Assert.False(r.FinalPositions.ContainsKey("DOWN"));
    }

    [Fact]
    public void PortfolioBacktest_EqualWeightStaysBalanced()
    {
        var prices = SyntheticMarket.CorrelatedPrices(260, new[] { 0.08, 0.05 }, new[] { 0.2, 0.15 }, new[,] { { 1, 0.3 }, { 0.3, 1 } });
        var data = new Dictionary<string, Bar[]>
        {
            ["A"] = prices.Select((p, i) => B(i, p[0], p[0], p[0], p[0])).ToArray(),
            ["B"] = prices.Select((p, i) => B(i, p[1], p[1], p[1], p[1])).ToArray(),
        };
        var strategy = new RebalancingStrategy(PortfolioBacktester.EqualWeight(), lookback: 20, frequency: RebalanceFrequency.EveryNBars, everyNBars: 20);
        var r = new BacktestEngine(new BacktestSettings { FractionalQuantities = true }).Run(strategy, data);
        Assert.True(strategy.RebalanceCount >= 11);
        double eq = r.FinalEquity;
        double wa = r.FinalPositions["A"].Quantity * prices[^1][0] / eq;
        Assert.InRange(wa, 0.4, 0.6);
    }

    [Fact]
    public void ParameterSweep_IsSortedBestFirst()
    {
        var data = new Dictionary<string, Bar[]> { ["X"] = SyntheticMarket.GeometricBrownianBars(400, seed: 12) };
        var results = ParameterSweep.Run(ParameterSweep.Grid(new[] { 5, 10 }, new[] { 30, 60 }), p => new SmaCrossoverStrategy(p.Item1, p.Item2), data);
        Assert.Equal(4, results.Count);
        for (int i = 1; i < results.Count; i++) Assert.True(results[i - 1].Score >= results[i].Score);
    }

    [Fact]
    public void WalkForward_StitchesOutOfSampleWindows()
    {
        var data = new Dictionary<string, Bar[]> { ["X"] = SyntheticMarket.GeometricBrownianBars(600, seed: 2) };
        var wf = new WalkForwardOptimizer<(int, int)>(new[] { (5, 20), (10, 50) }, p => new SmaCrossoverStrategy(p.Item1, p.Item2),
            new BacktestSettings { InitialCash = 10_000 });
        var result = wf.Run(data, inSampleBars: 200, outOfSampleBars: 100);
        Assert.Equal(4, result.Windows.Count);
        Assert.Equal(400, result.Times.Length);
        Assert.Equal(10_000, result.Equity[0], 9);
        for (int i = 1; i < result.Times.Length; i++) Assert.True(result.Times[i] > result.Times[i - 1]);
        Assert.Equal(data["X"][200].Time, result.Windows[0].OutOfSampleStart);
    }

    [Fact]
    public void StartTradingAt_IgnoresWarmupOrders()
    {
        var bars = Flat(10, 10, 10, 10);
        var r = new BacktestEngine(new BacktestSettings { StartTradingAt = bars[2].Time })
            .Run(new Script((ctx, _, i) => ctx.OrderTargetQuantity("X", 5)), "X", bars);
        Assert.Equal(2, r.Equity.Length);
        Assert.All(r.Fills, f => Assert.True(f.Time >= bars[2].Time));
        Assert.Equal(OrderStatus.Cancelled, r.Orders[0].Status);
    }
}
