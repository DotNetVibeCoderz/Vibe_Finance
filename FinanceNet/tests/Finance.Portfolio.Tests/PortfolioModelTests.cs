using Finance.Core;
using Finance.Core.Market;
using Finance.Mathematics;
using Finance.Portfolio;

namespace Finance.Portfolio.Tests;

public class PortfolioModelTests
{
    private static readonly string[] Names = { "AAA", "BBB", "CCC", "DDD" };

    [Fact]
    public void BlackLitterman_NoViews_PosteriorIsPrior()
    {
        var cov = PortfolioOptimizationTests.Cov();
        var prior = new[] { 0.05, 0.06, 0.04, 0.08 };
        var bl = BlackLittermanModel.Create(cov, prior, new BlackLittermanViews(Names));
        Assert.Equal(prior, bl.Posterior);
    }

    [Fact]
    public void BlackLitterman_HeLitterman_MatchesInverseFormula()
    {
        var cov = PortfolioOptimizationTests.Cov();
        var prior = new[] { 0.05, 0.06, 0.04, 0.08 };
        var views = new BlackLittermanViews(Names).Absolute("AAA", 0.10).Relative("DDD", "BBB", 0.03);
        double tau = 0.05;
        var bl = BlackLittermanModel.Create(cov, prior, views, tau);

        // μ = [(τΣ)⁻¹ + PᵀΩ⁻¹P]⁻¹ [(τΣ)⁻¹π + PᵀΩ⁻¹Q]
        var p = views.P;
        var omegaInv = bl.Omega.Inverse();
        var tsInv = (tau * cov).Inverse();
        var a = tsInv + p.Transpose() * omegaInv * p;
        var rhs = tsInv.Multiply(prior);
        var pq = (p.Transpose() * omegaInv).Multiply(views.Q);
        for (int i = 0; i < rhs.Length; i++) rhs[i] += pq[i];
        var expected = a.Solve(rhs);
        for (int i = 0; i < 4; i++) Assert.Equal(expected[i], bl.Posterior[i], 10);

        // Posterior covariance = Σ + [(τΣ)⁻¹ + PᵀΩ⁻¹P]⁻¹
        var m = a.Inverse();
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) Assert.Equal(cov[i, j] + m[i, j], bl.PosteriorCovariance[i, j], 10);
    }

    [Fact]
    public void BlackLitterman_IdzorekFullConfidence_MatchesView()
    {
        var cov = PortfolioOptimizationTests.Cov();
        var prior = new[] { 0.05, 0.06, 0.04, 0.08 };
        var views = new BlackLittermanViews(Names).Absolute("CCC", 0.12, confidence: 1.0);
        var bl = BlackLittermanModel.Create(cov, prior, views, omegaMethod: OmegaMethod.Idzorek);
        Assert.Equal(0.12, bl.Posterior[2], 12);

        var none = BlackLittermanModel.Create(cov, prior, new BlackLittermanViews(Names).Absolute("CCC", 0.12, confidence: 0), omegaMethod: OmegaMethod.Idzorek);
        Assert.Equal(prior[2], none.Posterior[2], 4);

        var half = BlackLittermanModel.Create(cov, prior, new BlackLittermanViews(Names).Absolute("CCC", 0.12, confidence: 0.5), omegaMethod: OmegaMethod.Idzorek);
        Assert.InRange(half.Posterior[2], prior[2], 0.12);
    }

    [Fact]
    public void MarketImpliedPrior_IsDeltaSigmaW()
    {
        var cov = PortfolioOptimizationTests.Cov();
        var caps = new[] { 100.0, 200, 300, 400 };
        var pi = BlackLittermanModel.MarketImpliedPriorReturns(caps, cov, 2.5, 0.01);
        var w = caps.Select(c => c / 1000).ToArray();
        var sw = cov.Multiply(w);
        for (int i = 0; i < 4; i++) Assert.Equal(2.5 * sw[i] + 0.01, pi[i], 12);

        // With no views, the BL weights recover the market portfolio.
        var bl = new BlackLittermanModel(cov, BlackLittermanModel.MarketImpliedPriorReturns(caps, cov, 2.5));
        for (int i = 0; i < 4; i++) Assert.Equal(w[i], bl.Weights(2.5)[i], 10);
    }

    [Fact]
    public void CVaR_ExactForEvenTail()
    {
        var rng = new FastRandom(5);
        var s = new Matrix(100, 2);
        for (int i = 0; i < 100; i++) { s[i, 0] = rng.NextGaussian(0.001, 0.02); s[i, 1] = rng.NextGaussian(0.0005, 0.01); }
        var opt = new CVaROptimizer(s, 0.95);
        var w = new[] { 0.6, 0.4 };
        var losses = Enumerable.Range(0, 100).Select(i => -(0.6 * s[i, 0] + 0.4 * s[i, 1])).OrderByDescending(x => x).ToArray();
        Assert.Equal(losses.Take(5).Average(), opt.CVaR(w), 12);
        Assert.Equal(losses[5], opt.VaR(w), 12);
    }

    [Fact]
    public void MinCVaR_MatchesBruteForce()
    {
        var rng = new FastRandom(9);
        var s = new Matrix(400, 2);
        for (int i = 0; i < 400; i++)
        {
            double common = rng.NextGaussian();
            s[i, 0] = 0.0008 + 0.02 * (0.3 * common + 0.95 * rng.NextGaussian());
            s[i, 1] = 0.0004 + 0.012 * (0.3 * common + 0.95 * rng.NextGaussian()) + (rng.NextDouble() < 0.03 ? -0.04 : 0);
        }
        var opt = new CVaROptimizer(s, 0.95);
        double bestCvar = double.PositiveInfinity, bestW = 0;
        for (int k = 0; k <= 4000; k++)
        {
            double w0 = k / 4000.0;
            double c = opt.CVaR(new[] { w0, 1 - w0 });
            if (c < bestCvar) { bestCvar = c; bestW = w0; }
        }
        var w = opt.MinCVaR();
        Assert.True(opt.CVaR(w.AsSpan()) <= bestCvar + 2e-6, $"optimizer {opt.CVaR(w.AsSpan())} vs grid {bestCvar}");
        Assert.Equal(bestW, w[0], 1);
        Assert.Equal(1, w.Sum, 10);
    }

    [Fact]
    public void CVaR_EfficientReturnAndRisk()
    {
        var rng = new FastRandom(21);
        var s = new Matrix(300, 3);
        for (int i = 0; i < 300; i++)
        {
            s[i, 0] = rng.NextGaussian(0.0002, 0.005);
            s[i, 1] = rng.NextGaussian(0.0006, 0.012);
            s[i, 2] = rng.NextGaussian(0.0012, 0.025);
        }
        var opt = new CVaROptimizer(s, 0.95);
        var min = opt.MinCVaR();
        double target = opt.ExpectedReturn(min.AsSpan()) + 0.0004;
        var er = opt.EfficientReturn(target);
        Assert.True(opt.ExpectedReturn(er.AsSpan()) >= target - 1e-9);
        Assert.True(opt.CVaR(er.AsSpan()) >= opt.CVaR(min.AsSpan()) - 1e-9);

        double cvarTarget = opt.CVaR(min.AsSpan()) * 1.5;
        var ek = opt.EfficientRisk(cvarTarget);
        Assert.True(opt.CVaR(ek.AsSpan()) <= cvarTarget + 1e-9);
        Assert.True(opt.ExpectedReturn(ek.AsSpan()) > opt.ExpectedReturn(min.AsSpan()));
    }

    [Fact]
    public void DiscreteAllocation_SpendsWithinBudget()
    {
        var w = new PortfolioWeights(new[] { 0.5, 0.3, 0.2 }, new[] { "X", "Y", "Z" });
        var prices = new[] { 100.0, 37, 251 };
        foreach (var a in new[] { DiscreteAllocation.Greedy(w, prices, 10_000), DiscreteAllocation.LargestRemainder(w, prices, 10_000) })
        {
            double spent = a.Shares.Sum(kv => kv.Value * prices[Array.IndexOf(new[] { "X", "Y", "Z" }, kv.Key)]);
            Assert.Equal(10_000 - spent, a.Leftover, 9);
            Assert.True(a.Leftover >= 0);
            for (int i = 0; i < 3; i++) Assert.Equal(w[i], a.AchievedWeights[i], 1);
        }
        var greedy = DiscreteAllocation.Greedy(w, prices, 10_000);
        Assert.True(greedy.Leftover < prices.Min()); // greedy stops only when nothing is affordable
        Assert.InRange(greedy.Shares["X"], 50, 51); // floor(0.5 * 10000 / 100) plus at most one greedy top-up
    }

    [Fact]
    public void Rebalancer_SellsFirstAndHitsTargets()
    {
        var current = new Dictionary<string, double> { ["A"] = 10, ["B"] = 0 };
        var prices = new Dictionary<string, double> { ["A"] = 100, ["B"] = 50 };
        var trades = Rebalancer.Trades(current, new PortfolioWeights(new[] { 0.5, 0.5 }, new[] { "A", "B" }), prices);
        Assert.Equal(2, trades.Count);
        Assert.Equal(("A", Side.Sell, 5.0), (trades[0].Asset, trades[0].Side, trades[0].Quantity));
        Assert.Equal(("B", Side.Buy, 10.0), (trades[1].Asset, trades[1].Side, trades[1].Quantity));
    }

    [Fact]
    public void ExpectedReturns_MeanHistoricalAndCapm()
    {
        var r = new Matrix(252, 2);
        for (int t = 0; t < 252; t++) { r[t, 0] = 0.001; r[t, 1] = t % 2 == 0 ? 0.01 : -0.008; }
        var mu = ExpectedReturns.MeanHistorical(r);
        Assert.Equal(Math.Pow(1.001, 252) - 1, mu[0], 10);
        var simple = ExpectedReturns.MeanHistorical(r, compounding: false);
        Assert.Equal(0.252, simple[0], 10);

        var rng = new FastRandom(3);
        var r2 = new Matrix(500, 2);
        var market = new double[500];
        for (int t = 0; t < 500; t++)
        {
            market[t] = rng.NextGaussian(0.0005, 0.01);
            r2[t, 0] = market[t];
            r2[t, 1] = 2 * market[t];
        }
        var capm = ExpectedReturns.Capm(r2, market, 0.01);
        var mm = ExpectedReturns.MeanHistorical(new Matrix(500, 1, (double[])market.Clone()))[0];
        Assert.Equal(mm, capm[0], 10);
        Assert.Equal(0.01 + 2 * (mm - 0.01), capm[1], 10);
    }

    [Fact]
    public void RiskModels_AreSymmetricAndConsistent()
    {
        var rng = new FastRandom(8);
        var r = new Matrix(300, 3);
        for (int t = 0; t < 300; t++)
            for (int j = 0; j < 3; j++) r[t, j] = rng.NextGaussian(0, 0.01 * (j + 1));
        var sample = RiskModels.SampleCovariance(r);
        var lw = RiskModels.LedoitWolf(r);
        var ew = RiskModels.ExponentialCovariance(r, span: 1_000_000);
        for (int a = 0; a < 3; a++)
            for (int b = 0; b < 3; b++)
            {
                Assert.Equal(lw[a, b], lw[b, a], 14);
                // An effectively infinite span is the biased sample covariance.
                Assert.True(Math.Abs(sample[a, b] * 299 / 300 - ew[a, b]) <= 1e-4 * Math.Sqrt(sample[a, a] * sample[b, b]));
            }
        Assert.True(lw.TryCholesky(out _));

        var notPsd = Matrix.FromArray(new[,] { { 1.0, 2 }, { 2, 1 } });
        var fixedCov = RiskModels.FixNonPositiveSemidefinite(notPsd);
        var (values, _) = fixedCov.SymmetricEigen();
        Assert.All(values, v => Assert.True(v >= -1e-12));
    }

    [Fact]
    public void PortfolioWeights_CleanAndLookup()
    {
        var w = new PortfolioWeights(new[] { 0.49999, 0.00001, 0.5 }, new[] { "A", "B", "C" });
        var c = w.Clean();
        Assert.Equal(0, c["B"]);
        Assert.Equal(1, c.Sum, 4);
        Assert.Equal(0.5, w["C"]);
    }
}
