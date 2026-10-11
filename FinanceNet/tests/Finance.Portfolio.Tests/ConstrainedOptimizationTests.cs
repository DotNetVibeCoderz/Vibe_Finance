using Finance.Core;
using Finance.Mathematics;
using Finance.Portfolio;

namespace Finance.Portfolio.Tests;

/// <summary>
/// The interior-point QP and the optimisers built on it, checked against an independent KKT solve, a problem
/// solved by hand, the existing projected-gradient optimiser, and the defining properties of each constraint.
/// </summary>
public class ConstrainedOptimizationTests
{
    private static readonly double[] Mu = PortfolioOptimizationTests.Mu;
    private static Matrix Cov() => PortfolioOptimizationTests.Cov();

    [Fact]
    public void EqualityConstrainedQpMatchesDirectKktSolve()
    {
        // min ½xᵀQx + cᵀx s.t. Ax = b  ⇔  [Q Aᵀ; A 0][x; y] = [−c; b].
        var q = Matrix.FromRows(new[] { new[] { 4.0, 1, 0 }, new[] { 1.0, 3, 0.5 }, new[] { 0.0, 0.5, 2 } });
        double[] c = { -1, 2, -3 }, a1 = { 1, 1, 1 }, a2 = { 1, -1, 0 };
        var kkt = new Matrix(5, 5);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) kkt[i, j] = q[i, j];
        for (int i = 0; i < 3; i++) { kkt[i, 3] = kkt[3, i] = a1[i]; kkt[i, 4] = kkt[4, i] = a2[i]; }
        var direct = kkt.Solve(new[] { 1.0, -2, 3, 1, 0.25 });

        var sol = new QuadraticProgram(q, c).AddEquality(a1, 1).AddEquality(a2, 0.25).Solve();
        Assert.True(sol.Converged);
        for (int i = 0; i < 3; i++) Assert.Equal(direct[i], sol.X[i], 8);
    }

    [Fact]
    public void InequalityQpHandSolution()
    {
        // min (x − 2)² + (y − 1)² s.t. x + y ≤ 2: projection of (2, 1) onto the half-plane is (1.5, 0.5), multiplier 1.
        var sol = new QuadraticProgram(Matrix.Diagonal(new[] { 2.0, 2 }), new[] { -4.0, -2 })
            .AddInequality(new[] { 1.0, 1 }, 2).Solve();
        Assert.Equal(1.5, sol.X[0], 8);
        Assert.Equal(0.5, sol.X[1], 8);
        Assert.Equal(1.0, sol.InequalityDuals[0], 6);
    }

    [Fact]
    public void BoundsOnlyAgreesWithProjectedGradientOptimizer()
    {
        var fista = new MeanVarianceOptimizer(Mu, Cov(), 0.05, 0.6).MaxQuadraticUtility(3);
        var ipm = new ConstrainedPortfolioOptimizer(Mu, Cov()).WithBounds(0.05, 0.6).MaxQuadraticUtility(3);
        for (int i = 0; i < Mu.Length; i++) Assert.Equal(fista[i], ipm.Weights[i], 6);
    }

    [Fact]
    public void MinimumReturnMatchesEfficientReturn()
    {
        var reference = new MeanVarianceOptimizer(Mu, Cov()).EfficientReturn(0.12);
        var ipm = new ConstrainedPortfolioOptimizer(Mu, Cov()).EfficientReturn(0.12);
        Assert.Equal(0.12, ipm.ExpectedReturn, 7);
        for (int i = 0; i < Mu.Length; i++) Assert.Equal(reference[i], ipm.Weights[i], 5);
    }

    [Fact]
    public void SectorAndFactorLimitsBind()
    {
        string[] sector = { "Tech", "Tech", "Bonds", "Tech" };
        var free = new ConstrainedPortfolioOptimizer(Mu, Cov()).MaxQuadraticUtility(2);
        double freeTech = free.Weights[0] + free.Weights[1] + free.Weights[3];
        Assert.True(freeTech > 0.5);

        double[] beta = { 1.1, 1.3, 0.2, 1.6 };
        var opt = new ConstrainedPortfolioOptimizer(Mu, Cov())
            .AddSectorLimit(sector, "Tech", 0, 0.5)
            .AddFactorExposure("market", beta, 0.6, 0.8);
        var r = opt.MaxQuadraticUtility(2);
        double tech = r.Weights[0] + r.Weights[1] + r.Weights[3];
        double exposure = Enumerable.Range(0, 4).Sum(i => beta[i] * r.Weights[i]);
        Assert.True(tech <= 0.5 + 1e-8, $"tech {tech}");
        Assert.InRange(exposure, 0.6 - 1e-8, 0.8 + 1e-8);
        Assert.Equal(1.0, r.Weights.Sum(), 9);
    }

    [Fact]
    public void TransactionCostsCreateANoTradeRegion()
    {
        double[] w0 = { 0.25, 0.25, 0.25, 0.25 };
        var free = new ConstrainedPortfolioOptimizer(Mu, Cov()) { CurrentWeights = w0 }.MaxQuadraticUtility(4);
        var noCurrent = new ConstrainedPortfolioOptimizer(Mu, Cov()).MaxQuadraticUtility(4);
        for (int i = 0; i < 4; i++) Assert.Equal(noCurrent.Weights[i], free.Weights[i], 6); // zero cost: same answer

        var cheap = new ConstrainedPortfolioOptimizer(Mu, Cov()) { CurrentWeights = w0, BuyCosts = Enumerable.Repeat(0.002, 4).ToArray() }.MaxQuadraticUtility(4);
        Assert.True(cheap.Turnover < free.Turnover - 1e-6, $"{cheap.Turnover} vs {free.Turnover}");
        Assert.True(cheap.TransactionCost > 0);

        var prohibitive = new ConstrainedPortfolioOptimizer(Mu, Cov()) { CurrentWeights = w0, BuyCosts = Enumerable.Repeat(0.5, 4).ToArray() }.MaxQuadraticUtility(4);
        for (int i = 0; i < 4; i++) Assert.Equal(w0[i], prohibitive.Weights[i], 6);   // nothing is worth trading
    }

    [Fact]
    public void TurnoverCapBinds()
    {
        double[] w0 = { 0.25, 0.25, 0.25, 0.25 };
        var free = new ConstrainedPortfolioOptimizer(Mu, Cov()) { CurrentWeights = w0 }.MaxQuadraticUtility(2);
        Assert.True(free.Turnover > 0.3);
        var capped = new ConstrainedPortfolioOptimizer(Mu, Cov()) { CurrentWeights = w0, MaxTurnover = 0.2 }.MaxQuadraticUtility(2);
        Assert.Equal(0.2, capped.Turnover, 6);
    }

    [Fact]
    public void LargeUniverseConverges()
    {
        // 200 assets from a 5-factor model: Σ = BBᵀ + D. 600 QP variables with costs and turnover.
        const int n = 200;
        var rng = new FastRandom(4);
        var b = new double[n, 5];
        for (int i = 0; i < n; i++) for (int k = 0; k < 5; k++) b[i, k] = 0.1 * rng.NextGaussian();
        var cov = new Matrix(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double v = 0;
                for (int k = 0; k < 5; k++) v += b[i, k] * b[j, k];
                cov[i, j] = v + (i == j ? 0.02 + 0.02 * rng.NextDouble() : 0);
            }
        var mu = Enumerable.Range(0, n).Select(_ => 0.04 + 0.08 * rng.NextDouble()).ToArray();
        var opt = new ConstrainedPortfolioOptimizer(mu, cov)
        {
            CurrentWeights = Enumerable.Repeat(1.0 / n, n).ToArray(),
            BuyCosts = Enumerable.Repeat(0.001, n).ToArray(),
            MaxTurnover = 0.5,
        }.WithBounds(0, 0.03);
        var r = opt.MaxQuadraticUtility(5);
        Assert.True(r.Solution.Converged);
        Assert.True(r.Solution.Iterations < 80, $"{r.Solution.Iterations} iterations");
        Assert.Equal(1.0, r.Weights.Sum(), 7);
        Assert.True(r.Turnover <= 0.5 + 1e-7);
        Assert.True(r.Weights.Max() <= 0.03 + 1e-8);
    }

    [Fact]
    public void ResampledPortfoliosLieInsideTheFrontierAndDiversifyMore()
    {
        var cov = Cov();
        var resampled = ResampledFrontier.Compute(Mu, cov, observations: 60, simulations: 100, points: 10, seed: 3);
        var classic = new MeanVarianceOptimizer(Mu, cov);
        double Effective(IEnumerable<double> w) => 1 / w.Sum(x => x * x);
        foreach (var p in resampled)
        {
            Assert.Equal(1.0, p.Weights.Sum(), 9);
            if (p.ExpectedReturn >= classic.MaxAttainableReturn - 1e-9) continue;
            double bestVol = PortfolioPerformance.Compute(classic.EfficientReturn(p.ExpectedReturn).ToArray(), Mu, cov).Volatility;
            Assert.True(p.Volatility >= bestVol - 1e-7, "a resampled portfolio beat the true frontier");
        }
        var frontier = classic.EfficientFrontier(10);
        Assert.True(resampled.Average(p => Effective(p.Weights)) > frontier.Average(p => Effective(p.Weights)));
    }
}
