using Finance.Core;
using Finance.Mathematics;
using Finance.Risk;
using Xunit;

namespace Finance.Quant.Tests;

public class RiskTests
{
    private static double[] NormalReturns(int n, double mu, double sigma, ulong seed = 11)
    {
        var rng = new FastRandom(seed);
        var r = new double[n];
        for (int i = 0; i < n; i++) r[i] = mu + sigma * rng.NextGaussian();
        return r;
    }

    [Fact]
    public void ParametricNormal_KnownValues()
    {
        var v = ValueAtRisk.ParametricNormal(0, 0.02, 0.99);
        Assert.Equal(0.02 * 2.326347874, v.ValueAtRisk, 8);
        Assert.Equal(0.02 * 2.665214220, v.ExpectedShortfall, 7);
    }

    [Fact]
    public void Historical_AgreesWithParametricOnNormalData()
    {
        var r = NormalReturns(200_000, 0.0005, 0.01);
        var h = ValueAtRisk.Historical(r, 0.99);
        var p = ValueAtRisk.ParametricNormal(r, 0.99);
        Assert.Equal(p.ValueAtRisk, h.ValueAtRisk, 3);
        Assert.Equal(p.ExpectedShortfall, h.ExpectedShortfall, 3);
        Assert.True(h.ExpectedShortfall > h.ValueAtRisk);
    }

    [Fact]
    public void StudentT_HasFatterTailsThanNormalAtHighConfidence()
    {
        var t = ValueAtRisk.ParametricStudentT(0, 0.01, 4, 0.995);
        var n = ValueAtRisk.ParametricNormal(0, 0.01, 0.995);
        Assert.True(t.ValueAtRisk > n.ValueAtRisk);
        Assert.True(t.ExpectedShortfall > t.ValueAtRisk);
        // Large ν converges to normal.
        Assert.Equal(n.ValueAtRisk, ValueAtRisk.ParametricStudentT(0, 0.01, 10_000, 0.995).ValueAtRisk, 5);
    }

    [Fact]
    public void CornishFisher_ReducesToNormalForGaussianData_AndPenalisesNegativeSkew()
    {
        var r = NormalReturns(300_000, 0, 0.01, 3);
        var cf = ValueAtRisk.CornishFisher(r, 0.99);
        var n = ValueAtRisk.ParametricNormal(r, 0.99);
        Assert.Equal(n.ValueAtRisk, cf.ValueAtRisk, 4);
        Assert.Equal(n.ExpectedShortfall, cf.ExpectedShortfall, 3);
        var skewed = r.Select(x => x < 0 ? 1.5 * x : x).ToArray();
        Assert.True(ValueAtRisk.CornishFisher(skewed, 0.99).ValueAtRisk > ValueAtRisk.ParametricNormal(skewed, 0.99).ValueAtRisk);
    }

    [Fact]
    public void MonteCarloPortfolioVar_MatchesAnalytic()
    {
        var cov = Matrix.FromArray(new[,] { { 0.0004, 0.0001 }, { 0.0001, 0.0009 } });
        double[] w = { 0.6, 0.4 }, mu = { 0.0005, 0.0003 };
        var mc = ValueAtRisk.MonteCarlo(w, mu, cov, 0.99, 400_000);
        double sigma = Math.Sqrt(cov.QuadraticForm(w));
        var exact = ValueAtRisk.ParametricNormal(0.6 * 0.0005 + 0.4 * 0.0003, sigma, 0.99);
        Assert.Equal(exact.ValueAtRisk, mc.ValueAtRisk, 3);
        Assert.Equal(VarMethod.MonteCarlo, mc.Method);
    }

    [Fact]
    public void ComponentVar_SumsToTotal_AndIncrementalMatchesMarginal()
    {
        var cov = Matrix.FromArray(new[,] { { 0.04, 0.006, 0.01 }, { 0.006, 0.09, 0.012 }, { 0.01, 0.012, 0.0225 } });
        double[] w = { 0.5, 0.2, 0.3 };
        var d = VarAttribution.Decompose(w, cov, 0.95);
        Assert.Equal(d.PortfolioVar, d.Component.Sum(), 12);
        Assert.Equal(1, d.PercentContribution.Sum(), 12);
        double h = 1e-6;
        Assert.Equal(d.Marginal[1], VarAttribution.Incremental(w, new[] { 0, h, 0 }, cov) / h, 4);
        var rc = RiskAttribution.VolatilityContributions(w, cov);
        Assert.Equal(rc.PortfolioVolatility, rc.Contributions.Sum(), 12);
    }

    [Fact]
    public void Kupiec_KnownStatistic_AndBaselZones()
    {
        // 250 days at 99%: 2.5 expected; 8 exceptions → LR = 7.7336 (hand-computed from the likelihoods).
        double lr = VarBacktest.Kupiec(250, 8, 0.01);
        Assert.Equal(7.7336, lr, 3);
        var hits = Enumerable.Range(0, 250).Select(i => i % 31 == 0).ToList(); // 9 isolated exceptions
        var res = VarBacktest.FromExceptions(hits, 0.99);
        Assert.Equal(9, res.Exceptions);
        Assert.Equal(BaselZone.Yellow, res.Zone);
        Assert.False(res.Passes());
        Assert.Equal(BaselZone.Green, VarBacktest.FromExceptions(Enumerable.Repeat(false, 250).ToList()).Zone);
    }

    [Fact]
    public void Christoffersen_DetectsClusteredExceptions()
    {
        var spread = Enumerable.Range(0, 500).Select(i => i % 100 == 0).ToList();
        var clustered = Enumerable.Range(0, 500).Select(i => i is >= 200 and < 205).ToList();
        var a = VarBacktest.FromExceptions(spread, 0.99);
        var b = VarBacktest.FromExceptions(clustered, 0.99);
        Assert.Equal(a.Exceptions, b.Exceptions);
        Assert.True(b.ChristoffersenPValue < 0.01);
        Assert.True(a.ChristoffersenPValue > b.ChristoffersenPValue);
    }

    [Fact]
    public void Backtest_CorrectVarPassesOnNormalData()
    {
        var r = NormalReturns(2000, 0, 0.01, 21);
        var v = ValueAtRisk.ParametricNormal(0, 0.01, 0.99).ValueAtRisk;
        var res = VarBacktest.Run(r, v, 0.99);
        Assert.True(res.KupiecPValue > 0.01);
    }

    [Fact]
    public void StressTest_LinearAndConvexPnl()
    {
        var positions = new[]
        {
            StressPosition.Equity("SPY", 1_000_000, 1.0),
            StressPosition.Bond("UST10", 500_000, 8.0, 0, 80),
            StressPosition.Single("BTC", 100_000, RiskFactors.Crypto),
        };
        var gfc = StressScenario.Historical.First(s => s.Name.Contains("2008"));
        var res = StressTester.Run(positions, gfc);
        double bond = 500_000 * (-8.0 * -0.02 + 0.5 * 80 * 0.0004);
        Assert.Equal(-500_000 + bond, res.TotalPnl, 6);
        var all = StressTester.RunAll(positions);
        Assert.Equal(StressScenario.Historical.Count, all.Count);
        Assert.True(all[0].TotalPnl <= all[^1].TotalPnl);
        var rates = StressTester.Run(positions, StressScenario.Custom("Rates +200bp", (RiskFactors.Rates, 0.02)));
        Assert.True(rates.PositionPnl["UST10"] < 0);
        double k = StressTester.ReverseStressMultiplier(positions, StressScenario.Custom("Eq", (RiskFactors.Equity, -0.1)), 250_000);
        Assert.Equal(2.5, k, 10);
    }

    [Fact]
    public void PortfolioSimulation_MeanGrowthAndFanOrdering()
    {
        var cov = Matrix.FromArray(new[,] { { 0.04, 0.01 }, { 0.01, 0.0225 } });
        var res = PortfolioSimulation.Run(1_000_000, new[] { 0.6, 0.4 }, new[] { 0.08, 0.05 }, cov, 1, 52, 20_000);
        double expected = 1_000_000 * Math.Exp(0.6 * 0.08 + 0.4 * 0.05);
        Assert.InRange(res.MeanTerminalValue, expected * 0.99, expected * 1.01);
        for (int t = 0; t < res.Times.Length; t++)
            for (int k = 1; k < res.Fan.Length; k++) Assert.True(res.Fan[k][t] >= res.Fan[k - 1][t]);
        Assert.InRange(res.ProbabilityOfLoss, 0.25, 0.45);
        Assert.True(res.ValueAtRisk(0.95).ValueAtRisk > 0);
        Assert.Equal(20, res.SamplePaths.Length);
    }

    [Fact]
    public void PerformanceMetrics_KnownSeries()
    {
        double[] r = { 0.10, -0.05, 0.02, -0.10, 0.08 };
        Assert.Equal(1.1 * 0.95 * 1.02 * 0.9 * 1.08 - 1, PerformanceMetrics.CumulativeReturn(r), 12);
        // Peak after first period 1.1; trough 1.1*.95*1.02*.9 = 0.95931 → dd = -12.79%.
        Assert.Equal(0.95931 / 1.1 - 1, PerformanceMetrics.MaxDrawdown(r), 10);
        Assert.Equal(4, PerformanceMetrics.MaxDrawdownDuration(r));
        Assert.Equal(0.6, PerformanceMetrics.HitRate(r), 12);
        Assert.Equal((0.10 + 0.02 + 0.08) / (0.05 + 0.10), PerformanceMetrics.OmegaRatio(r), 10);
        double mean = r.Average(), sd = Stats.StdDev(r);
        Assert.Equal(mean / sd * Math.Sqrt(252), PerformanceMetrics.SharpeRatio(r), 10);
        double dd = Math.Sqrt((0.05 * 0.05 + 0.1 * 0.1) / 5) * Math.Sqrt(252);
        Assert.Equal(dd, PerformanceMetrics.DownsideDeviation(r), 10);
    }

    [Fact]
    public void AlphaBeta_RecoversConstruction()
    {
        var bench = NormalReturns(5000, 0.0004, 0.01, 2);
        var noise = NormalReturns(5000, 0, 0.0003, 3);
        var port = bench.Select((b, i) => 0.0001 + 1.3 * b + noise[i]).ToArray();
        var (alpha, beta) = PerformanceMetrics.AlphaBeta(port, bench);
        Assert.Equal(1.3, beta, 2);
        Assert.Equal(Math.Pow(1.0001, 252) - 1, alpha, 2);
        var rep = PerformanceReport.Create(port, bench);
        Assert.NotNull(rep.Beta);
        Assert.True(rep.TrackingError > 0);
        Assert.Contains(rep.ToRows(), row => row.Metric == "Sharpe ratio");
        Assert.Contains("Max drawdown", rep.ToString());
    }

    [Fact]
    public void RollingStats_MatchDirectComputation()
    {
        var r = NormalReturns(300, 0, 0.01, 8);
        var rv = PerformanceMetrics.RollingVolatility(r, 20);
        Assert.True(double.IsNaN(rv[18]));
        Assert.Equal(Stats.StdDev(r.AsSpan(100, 20)) * Math.Sqrt(252), rv[119], 10);
        var rs = PerformanceMetrics.RollingSharpe(r, 50);
        Assert.Equal(PerformanceMetrics.SharpeRatio(r.AsSpan(200, 50)), rs[249], 10);
    }

    [Fact]
    public void Garch_RecoversSimulatedParameters()
    {
        var r = VolatilityModels.SimulateGarch11(8000, 0.0003, 2e-6, 0.08, 0.90, 17);
        var m = VolatilityModels.FitGarch11(r);
        Assert.InRange(m.Alpha, 0.05, 0.11);
        Assert.InRange(m.Beta, 0.86, 0.94);
        Assert.InRange(m.Persistence, 0.95, 0.995);
        var f = m.ForecastVariance(500);
        Assert.Equal(m.LongRunVariance, f[^1], 7);
        Assert.Equal(r.Length, m.ConditionalVariances.Length);
    }

    [Fact]
    public void Ewma_FollowsRecursion()
    {
        double[] r = { 0.01, -0.02, 0.015, 0.0 };
        var v = VolatilityModels.Ewma(r, 0.94);
        double v1 = 0.94 * 0.0001 + 0.06 * 0.0001;
        double v2 = 0.94 * v1 + 0.06 * 0.0004;
        Assert.Equal(Math.Sqrt(v2), v[2], 12);
    }

    [Fact]
    public void FactorDecomposition_AddsUp()
    {
        var b = Matrix.FromArray(new[,] { { 1.0, 0.2 }, { 0.8, -0.1 } });
        var f = Matrix.FromArray(new[,] { { 0.03, 0.002 }, { 0.002, 0.01 } });
        var (total, sys, spec) = RiskAttribution.FactorDecomposition(new[] { 0.5, 0.5 }, b, f, new[] { 0.01, 0.02 });
        Assert.Equal(sys + spec, total, 14);
        Assert.Equal(0.25 * 0.01 + 0.25 * 0.02, spec, 14);
    }
}
