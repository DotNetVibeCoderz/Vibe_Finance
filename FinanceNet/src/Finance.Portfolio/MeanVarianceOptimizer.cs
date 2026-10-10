using Finance.Mathematics;

namespace Finance.Portfolio;

/// <summary>
/// Markowitz mean-variance optimisation (PyPortfolioOpt's <c>EfficientFrontier</c>) with a full-investment
/// constraint and per-weight bounds. Set <c>lowerBound</c> negative to allow short positions.
/// </summary>
/// <remarks>
/// Every problem is reduced to the parametric QP min ½wᵀΣw − λμᵀw on the capped simplex, whose solutions
/// trace the constrained efficient frontier as λ grows. Target-return and target-risk problems bisect on λ
/// (return and volatility are monotone in λ); max-Sharpe searches λ for the tangency point, which is unique
/// because the constrained frontier is concave in (σ, μ) space.
/// </remarks>
public sealed class MeanVarianceOptimizer
{
    private const double LambdaMax = 1e9;
    private readonly double[] _mu;
    private readonly Matrix _cov;
    private readonly BoxSimplexQp _qp;
    private double[]? _minVol;

    public MeanVarianceOptimizer(double[] expectedReturns, Matrix covariance, double lowerBound = 0, double upperBound = 1, IReadOnlyList<string>? assets = null)
    {
        ArgumentNullException.ThrowIfNull(expectedReturns);
        ArgumentNullException.ThrowIfNull(covariance);
        if (covariance.Rows != expectedReturns.Length || !covariance.IsSquare)
            throw new ArgumentException("Covariance must be n x n with n = number of expected returns.");
        _mu = (double[])expectedReturns.Clone();
        _cov = covariance;
        LowerBound = lowerBound;
        UpperBound = upperBound;
        Assets = assets ?? Enumerable.Range(0, _mu.Length).Select(i => $"Asset{i + 1}").ToArray();
        _qp = new BoxSimplexQp(covariance, lowerBound, upperBound);
    }

    public IReadOnlyList<string> Assets { get; }
    public double LowerBound { get; }
    public double UpperBound { get; }
    public ReadOnlySpan<double> ExpectedReturns => _mu;
    public Matrix Covariance => _cov;

    /// <summary>The highest return attainable under the constraints (fill the best assets to their upper bound).</summary>
    public double MaxAttainableReturn
    {
        get
        {
            int n = _mu.Length;
            var order = Enumerable.Range(0, n).OrderByDescending(i => _mu[i]).ToArray();
            var w = Enumerable.Repeat(LowerBound, n).ToArray();
            double remaining = 1 - n * LowerBound;
            foreach (int i in order)
            {
                double add = Math.Min(UpperBound - LowerBound, remaining);
                w[i] += add;
                remaining -= add;
                if (remaining <= 0) break;
            }
            return Matrix.Dot(w, _mu);
        }
    }

    /// <summary>Global minimum-variance portfolio.</summary>
    public PortfolioWeights MinVolatility() => Wrap(MinVolRaw());

    /// <summary>Maximises μᵀw − (δ/2) wᵀΣw for risk aversion δ.</summary>
    public PortfolioWeights MaxQuadraticUtility(double riskAversion = 1)
    {
        Finance.Core.Guard.Positive(riskAversion);
        return Wrap(SolveLambda(1 / riskAversion, null));
    }

    /// <summary>Minimum-variance portfolio whose expected return is at least <paramref name="targetReturn"/>.</summary>
    public PortfolioWeights EfficientReturn(double targetReturn) => Wrap(EfficientReturnRaw(targetReturn, null));

    /// <summary>Maximum-return portfolio whose volatility does not exceed <paramref name="targetVolatility"/>.</summary>
    public PortfolioWeights EfficientRisk(double targetVolatility)
    {
        var minVol = MinVolRaw();
        double vMin = Volatility(minVol);
        if (targetVolatility < vMin - 1e-10)
            throw new ArgumentException($"Target volatility {targetVolatility:P3} is below the minimum attainable {vMin:P3}.");
        var top = SolveLambda(LambdaMax, minVol);
        if (Volatility(top) <= targetVolatility) return Wrap(top);
        double lo = 0, hi = LambdaMax;
        double[] best = minVol, warm = minVol;
        for (int it = 0; it < 200 && hi - lo > 1e-14 * (1 + hi); it++)
        {
            double mid = it < 60 ? Math.Sqrt(Math.Max(lo, 1e-12) * hi) : 0.5 * (lo + hi);
            var w = SolveLambda(mid, warm);
            if (Volatility(w) <= targetVolatility) { lo = mid; best = w; warm = w; }
            else hi = mid;
        }
        return Wrap(best);
    }

    /// <summary>
    /// Tangency (maximum Sharpe ratio) portfolio. Throws when no asset beats the risk-free rate.
    /// </summary>
    public PortfolioWeights MaxSharpe(double riskFreeRate = 0) => Wrap(MaxSharpeRaw(riskFreeRate));

    /// <summary>
    /// Samples <paramref name="points"/> portfolios on the efficient frontier, from the minimum-variance
    /// portfolio to (just below) the maximum attainable return.
    /// </summary>
    public IReadOnlyList<FrontierPoint> EfficientFrontier(int points = 50, double riskFreeRate = 0)
    {
        Finance.Core.Guard.Positive(points);
        var minVol = MinVolRaw();
        double rMin = Matrix.Dot(minVol, _mu), rMax = MaxAttainableReturn;
        var result = new List<FrontierPoint>(points);
        double[] warm = minVol;
        for (int i = 0; i < points; i++)
        {
            double target = points == 1 ? rMin : rMin + (rMax - rMin) * i / (points - 1) * 0.999;
            var w = i == 0 ? minVol : EfficientReturnRaw(target, warm);
            warm = w;
            var perf = PortfolioPerformance.Compute(w, _mu, _cov, riskFreeRate);
            result.Add(new FrontierPoint(perf.ExpectedReturn, perf.Volatility, perf.SharpeRatio, Wrap(w)));
        }
        return result;
    }

    /// <summary>Performance of <paramref name="weights"/> under this optimiser's μ and Σ.</summary>
    public PortfolioPerformance Performance(PortfolioWeights weights, double riskFreeRate = 0)
        => PortfolioPerformance.Compute(weights.AsSpan(), _mu, _cov, riskFreeRate);

    private double[] MinVolRaw() => _minVol ??= _qp.Solve(new double[_mu.Length]);

    private double[] SolveLambda(double lambda, double[]? warm)
    {
        var c = new double[_mu.Length];
        for (int i = 0; i < c.Length; i++) c[i] = -lambda * _mu[i];
        return _qp.Solve(c, warm);
    }

    private double Volatility(double[] w) => Math.Sqrt(Math.Max(0, _cov.QuadraticForm(w)));

    private double[] EfficientReturnRaw(double target, double[]? warm)
    {
        var minVol = MinVolRaw();
        if (Matrix.Dot(minVol, _mu) >= target) return minVol;
        double rMax = MaxAttainableReturn;
        if (target > rMax + 1e-12)
            throw new ArgumentException($"Target return {target:P3} exceeds the maximum attainable {rMax:P3}.");
        double lo = 0, hi = LambdaMax;
        double[]? best = null;
        warm ??= minVol;
        for (int it = 0; it < 200 && hi - lo > 1e-14 * (1 + hi); it++)
        {
            double mid = it < 60 ? Math.Sqrt(Math.Max(lo, 1e-12) * hi) : 0.5 * (lo + hi);
            var w = SolveLambda(mid, warm);
            if (Matrix.Dot(w, _mu) >= target) { hi = mid; best = w; }
            else { lo = mid; warm = w; }
        }
        return best ?? SolveLambda(LambdaMax, warm);
    }

    private double[] MaxSharpeRaw(double rf)
    {
        if (_mu.All(m => m <= rf))
            throw new InvalidOperationException("At least one asset must have an expected return above the risk-free rate.");

        double Sharpe(double[] w)
        {
            double v = Volatility(w);
            return v > 0 ? (Matrix.Dot(w, _mu) - rf) / v : double.NegativeInfinity;
        }

        // Coarse scan in log(λ), then golden-section refinement around the best grid point.
        const int grid = 81;
        const double sLo = -14, sHi = 14;
        var minVol = MinVolRaw();
        double[] warm = minVol;
        double bestS = double.NegativeInfinity;
        int bestK = -1;
        double[] bestW = minVol;
        double sharpeMinVol = Sharpe(minVol);
        for (int k = 0; k < grid; k++)
        {
            double s = sLo + (sHi - sLo) * k / (grid - 1);
            var w = SolveLambda(Math.Exp(s), warm);
            warm = w;
            double sh = Sharpe(w);
            if (sh > bestS) { bestS = sh; bestK = k; bestW = w; }
        }
        if (sharpeMinVol >= bestS) return minVol;

        double step = (sHi - sLo) / (grid - 1);
        double a = sLo + step * (bestK - 1), b = sLo + step * (bestK + 1);
        double gr = (Math.Sqrt(5) - 1) / 2;
        double c1 = b - gr * (b - a), c2 = a + gr * (b - a);
        var w1 = SolveLambda(Math.Exp(c1), bestW);
        var w2 = SolveLambda(Math.Exp(c2), bestW);
        double f1 = Sharpe(w1), f2 = Sharpe(w2);
        for (int it = 0; it < 80 && b - a > 1e-11; it++)
        {
            if (f1 > f2)
            {
                b = c2; c2 = c1; f2 = f1; w2 = w1;
                c1 = b - gr * (b - a);
                w1 = SolveLambda(Math.Exp(c1), w2);
                f1 = Sharpe(w1);
            }
            else
            {
                a = c1; c1 = c2; f1 = f2; w1 = w2;
                c2 = a + gr * (b - a);
                w2 = SolveLambda(Math.Exp(c2), w1);
                f2 = Sharpe(w2);
            }
        }
        var candidate = f1 > f2 ? w1 : w2;
        return Math.Max(f1, f2) >= bestS ? candidate : bestW;
    }

    private PortfolioWeights Wrap(double[] w) => new(w, Assets);
}
