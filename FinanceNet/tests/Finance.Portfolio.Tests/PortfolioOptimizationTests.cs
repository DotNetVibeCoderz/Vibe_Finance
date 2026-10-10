using Finance.Core;
using Finance.Mathematics;
using Finance.Portfolio;

namespace Finance.Portfolio.Tests;

public class PortfolioOptimizationTests
{
    internal static readonly double[] Mu = { 0.10, 0.12, 0.07, 0.15 };
    internal static readonly double[] Vols = { 0.20, 0.25, 0.15, 0.30 };
    internal static readonly double[,] Corr =
    {
        { 1.00, 0.30, 0.20, 0.40 },
        { 0.30, 1.00, 0.10, 0.35 },
        { 0.20, 0.10, 1.00, 0.15 },
        { 0.40, 0.35, 0.15, 1.00 },
    };

    internal static Matrix Cov()
    {
        var c = new Matrix(4, 4);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) c[i, j] = Corr[i, j] * Vols[i] * Vols[j];
        return c;
    }

    private static double[] Ones(int n) => Enumerable.Repeat(1.0, n).ToArray();

    private static void AssertClose(ReadOnlySpan<double> expected, ReadOnlySpan<double> actual, double tol)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tol, $"index {i}: expected {expected[i]}, got {actual[i]}");
    }

    [Fact]
    public void MinVolatility_TwoAssets_MatchesClosedForm()
    {
        double s1 = 0.2, s2 = 0.3, rho = 0.25;
        var cov = Matrix.FromArray(new[,] { { s1 * s1, rho * s1 * s2 }, { rho * s1 * s2, s2 * s2 } });
        var w = new MeanVarianceOptimizer(new[] { 0.05, 0.08 }, cov, -5, 5).MinVolatility();
        double w1 = (s2 * s2 - rho * s1 * s2) / (s1 * s1 + s2 * s2 - 2 * rho * s1 * s2);
        Assert.Equal(w1, w[0], 8);
        Assert.Equal(1 - w1, w[1], 8);
    }

    [Fact]
    public void MinVolatility_Unconstrained_MatchesAnalytic()
    {
        var cov = Cov();
        var inv1 = cov.Solve(Ones(4));
        double s = inv1.Sum();
        var expected = inv1.Select(v => v / s).ToArray();
        var w = new MeanVarianceOptimizer(Mu, cov, -10, 10).MinVolatility();
        AssertClose(expected, w.AsSpan(), 1e-7);
    }

    [Fact]
    public void MaxSharpe_Unconstrained_IsTangencyPortfolio()
    {
        var cov = Cov();
        double rf = 0.02;
        var excess = Mu.Select(m => m - rf).ToArray();
        var raw = cov.Solve(excess);
        double s = raw.Sum();
        var expected = raw.Select(v => v / s).ToArray();
        var w = new MeanVarianceOptimizer(Mu, cov, -10, 10).MaxSharpe(rf);
        AssertClose(expected, w.AsSpan(), 1e-5);
    }

    [Fact]
    public void MaxSharpe_LongOnly_BeatsRandomPortfoliosAndFrontier()
    {
        var cov = Cov();
        var opt = new MeanVarianceOptimizer(Mu, cov);
        var w = opt.MaxSharpe(0.02);
        double best = opt.Performance(w, 0.02).SharpeRatio;
        Assert.True(w.All(x => x >= -1e-12));
        Assert.Equal(1.0, w.Sum, 10);

        var rng = new FastRandom(11);
        for (int k = 0; k < 3000; k++)
        {
            var r = Enumerable.Range(0, 4).Select(_ => -Math.Log(rng.NextDouble() + 1e-300)).ToArray();
            double t = r.Sum();
            var p = PortfolioPerformance.Compute(r.Select(v => v / t).ToArray(), Mu, cov, 0.02);
            Assert.True(p.SharpeRatio <= best + 1e-9);
        }
        foreach (var pt in opt.EfficientFrontier(30, 0.02)) Assert.True(pt.SharpeRatio <= best + 1e-7);
    }

    [Fact]
    public void EfficientReturn_Unconstrained_MatchesTwoFundSolution()
    {
        var cov = Cov();
        double target = 0.11;
        var inv1 = cov.Solve(Ones(4));
        var invMu = cov.Solve(Mu);
        double a = inv1.Sum(), b = invMu.Sum(), c = Matrix.Dot(Mu, invMu), d = a * c - b * b;
        var expected = new double[4];
        for (int i = 0; i < 4; i++) expected[i] = ((c - b * target) * inv1[i] + (a * target - b) * invMu[i]) / d;

        var opt = new MeanVarianceOptimizer(Mu, cov, -10, 10);
        var w = opt.EfficientReturn(target);
        Assert.Equal(target, opt.Performance(w).ExpectedReturn, 7);
        AssertClose(expected, w.AsSpan(), 1e-5);
    }

    [Fact]
    public void EfficientRisk_HitsTargetVolatility()
    {
        var opt = new MeanVarianceOptimizer(Mu, Cov());
        double minVol = opt.Performance(opt.MinVolatility()).Volatility;
        double target = minVol + 0.03;
        var w = opt.EfficientRisk(target);
        var perf = opt.Performance(w);
        Assert.Equal(target, perf.Volatility, 6);
        // On the frontier: no cheaper way to that return.
        var er = opt.EfficientReturn(perf.ExpectedReturn);
        Assert.True(opt.Performance(er).Volatility <= perf.Volatility + 1e-6);
        Assert.Throws<ArgumentException>(() => opt.EfficientRisk(minVol * 0.5));
    }

    [Fact]
    public void Bounds_AreRespected()
    {
        var opt = new MeanVarianceOptimizer(Mu, Cov(), 0.05, 0.35);
        foreach (var w in new[] { opt.MinVolatility(), opt.MaxSharpe(), opt.EfficientReturn(0.11), opt.MaxQuadraticUtility(2) })
        {
            Assert.Equal(1.0, w.Sum, 9);
            Assert.All(w, x => Assert.InRange(x, 0.05 - 1e-12, 0.35 + 1e-12));
        }
        Assert.Throws<ArgumentException>(() => new MeanVarianceOptimizer(Mu, Cov(), 0.3, 1));
    }

    [Fact]
    public void MaxQuadraticUtility_Unconstrained_MatchesAnalytic()
    {
        var cov = Cov();
        double delta = 3;
        var inv1 = cov.Solve(Ones(4));
        var invMu = cov.Solve(Mu);
        double gamma = (1 - invMu.Sum() / delta) / inv1.Sum();
        var expected = Enumerable.Range(0, 4).Select(i => invMu[i] / delta + gamma * inv1[i]).ToArray();
        var w = new MeanVarianceOptimizer(Mu, cov, -10, 10).MaxQuadraticUtility(delta);
        AssertClose(expected, w.AsSpan(), 1e-7);
    }

    [Fact]
    public void EfficientFrontier_IsMonotone()
    {
        var pts = new MeanVarianceOptimizer(Mu, Cov()).EfficientFrontier(25);
        Assert.Equal(25, pts.Count);
        for (int i = 1; i < pts.Count; i++)
        {
            Assert.True(pts[i].ExpectedReturn >= pts[i - 1].ExpectedReturn - 1e-9);
            Assert.True(pts[i].Volatility >= pts[i - 1].Volatility - 1e-7);
        }
    }

    [Fact]
    public void MaxSharpe_ThrowsWhenNothingBeatsRiskFree()
        => Assert.Throws<InvalidOperationException>(() => new MeanVarianceOptimizer(Mu, Cov()).MaxSharpe(0.5));

    [Fact]
    public void RiskParity_EqualContributions()
    {
        var cov = Cov();
        var w = RiskParity.EqualRiskContribution(cov);
        var rc = RiskParity.RiskContributions(w.AsSpan(), cov);
        Assert.All(rc, x => Assert.Equal(0.25, x, 9));
        Assert.Equal(1, w.Sum, 12);
    }

    [Fact]
    public void RiskBudget_ContributionsMatchBudgets()
    {
        var cov = Cov();
        var budgets = new[] { 0.4, 0.3, 0.2, 0.1 };
        var rc = RiskParity.RiskContributions(RiskParity.RiskBudget(cov, budgets).AsSpan(), cov);
        AssertClose(budgets, rc, 1e-9);
    }

    [Fact]
    public void RiskParity_UncorrelatedIsInverseVolatility()
    {
        var cov = Matrix.Diagonal(new[] { 0.04, 0.09, 0.01 });
        var w = RiskParity.EqualRiskContribution(cov);
        AssertClose(HeuristicPortfolios.InverseVolatility(cov).AsSpan(), w.AsSpan(), 1e-10);
    }

    [Fact]
    public void Hrp_DiagonalCovariance_IsInverseVariance()
    {
        var cov = Matrix.Diagonal(new[] { 0.04, 0.09, 0.01, 0.0225, 0.0625 });
        var hrp = HierarchicalRiskParity.Optimize(cov);
        AssertClose(HeuristicPortfolios.InverseVariance(cov).AsSpan(), hrp.Weights.AsSpan(), 1e-12);
    }

    [Fact]
    public void Hrp_GroupsCorrelatedAssets()
    {
        double[,] corr =
        {
            { 1.0, 0.1, 0.9, 0.1 },
            { 0.1, 1.0, 0.1, 0.9 },
            { 0.9, 0.1, 1.0, 0.1 },
            { 0.1, 0.9, 0.1, 1.0 },
        };
        var vols = new[] { 0.1, 0.2, 0.15, 0.25 };
        var cov = new Matrix(4, 4);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) cov[i, j] = corr[i, j] * vols[i] * vols[j];
        var hrp = HierarchicalRiskParity.Optimize(cov, new[] { "A", "B", "C", "D" });
        int pa = Array.IndexOf(hrp.Order, 0), pc = Array.IndexOf(hrp.Order, 2);
        int pb = Array.IndexOf(hrp.Order, 1), pd = Array.IndexOf(hrp.Order, 3);
        Assert.Equal(1, Math.Abs(pa - pc));
        Assert.Equal(1, Math.Abs(pb - pd));
        Assert.Equal(3, hrp.Linkage.Length);
        Assert.Equal(4, hrp.Linkage[^1].Size);
        Assert.Equal(1, hrp.Weights.Sum, 12);
        Assert.All(hrp.Weights, w => Assert.True(w > 0));
        // The tight pairs merge first, at distance sqrt(0.5 * (1 - 0.9)).
        Assert.Equal(Math.Sqrt(0.05), hrp.Linkage[0].Distance, 12);
    }

    [Fact]
    public void MaxDiversification_TwoUncorrelatedAssets_WeightsInverseVol()
    {
        var cov = Matrix.Diagonal(new[] { 0.04, 0.16 });
        var w = HeuristicPortfolios.MaxDiversification(cov);
        // For uncorrelated assets the most diversified portfolio is w ∝ 1/σ.
        Assert.Equal(2.0 / 3, w[0], 5);
        Assert.Equal(Math.Sqrt(2), HeuristicPortfolios.DiversificationRatio(w.AsSpan(), cov), 5);
    }
}
