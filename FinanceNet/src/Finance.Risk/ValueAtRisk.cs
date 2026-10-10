using Finance.Core;
using Finance.Mathematics;

namespace Finance.Risk;

/// <summary>VaR estimation method.</summary>
public enum VarMethod { Historical, ParametricNormal, ParametricStudentT, CornishFisher, MonteCarlo }

/// <summary>
/// A VaR / Expected Shortfall estimate. Both are reported as positive loss fractions
/// (0.025 = a 2.5% loss) over the horizon of the input returns.
/// </summary>
public readonly record struct VarEstimate(double ValueAtRisk, double ExpectedShortfall, double Confidence, VarMethod Method)
{
    /// <summary>VaR in currency for a position of <paramref name="portfolioValue"/>.</summary>
    public double ValueAtRiskAmount(double portfolioValue) => ValueAtRisk * portfolioValue;

    /// <summary>Expected Shortfall in currency.</summary>
    public double ExpectedShortfallAmount(double portfolioValue) => ExpectedShortfall * portfolioValue;
}

/// <summary>
/// Value-at-Risk and Expected Shortfall (CVaR) estimators on a series of periodic returns.
/// </summary>
public static class ValueAtRisk
{
    /// <summary>Historical simulation: the empirical (1 - confidence) quantile of returns.</summary>
    public static VarEstimate Historical(ReadOnlySpan<double> returns, double confidence = 0.95)
    {
        Guard.MinLength(returns.Length, 2);
        var sorted = returns.ToArray();
        Array.Sort(sorted);
        double q = Stats.QuantileSorted(sorted, 1 - confidence);
        double sum = 0;
        int n = 0;
        foreach (var r in sorted)
        {
            if (r > q) break;
            sum += r;
            n++;
        }
        double es = n > 0 ? -sum / n : -q;
        return new VarEstimate(-q, es, confidence, VarMethod.Historical);
    }

    /// <summary>Parametric normal VaR/ES from the sample mean and standard deviation.</summary>
    public static VarEstimate ParametricNormal(ReadOnlySpan<double> returns, double confidence = 0.95)
        => ParametricNormal(Stats.Mean(returns), Stats.StdDev(returns), confidence);

    /// <summary>Parametric normal VaR/ES from moments.</summary>
    public static VarEstimate ParametricNormal(double mean, double stdDev, double confidence = 0.95)
    {
        double z = Normal.InverseCdf(confidence);
        double var = -mean + stdDev * z;
        double es = -mean + stdDev * Normal.Pdf(z) / (1 - confidence);
        return new VarEstimate(var, es, confidence, VarMethod.ParametricNormal);
    }

    /// <summary>
    /// Student-t VaR/ES with <paramref name="degreesOfFreedom"/> (ν &gt; 2), scaled so that the distribution
    /// has the sample standard deviation.
    /// </summary>
    public static VarEstimate ParametricStudentT(ReadOnlySpan<double> returns, double degreesOfFreedom = 5, double confidence = 0.95)
        => ParametricStudentT(Stats.Mean(returns), Stats.StdDev(returns), degreesOfFreedom, confidence);

    /// <summary>Student-t VaR/ES from moments.</summary>
    public static VarEstimate ParametricStudentT(double mean, double stdDev, double degreesOfFreedom, double confidence = 0.95)
    {
        double nu = degreesOfFreedom;
        if (nu <= 2) throw new ArgumentOutOfRangeException(nameof(degreesOfFreedom), "Needs ν > 2 for a finite variance.");
        double scale = stdDev * Math.Sqrt((nu - 2) / nu);
        double t = StudentT.InverseCdf(confidence, nu);
        double var = -mean + scale * t;
        double es = -mean + scale * StudentT.Pdf(t, nu) / (1 - confidence) * (nu + t * t) / (nu - 1);
        return new VarEstimate(var, es, confidence, VarMethod.ParametricStudentT);
    }

    /// <summary>
    /// Modified (Cornish-Fisher) VaR, adjusting the normal quantile for sample skewness and excess kurtosis.
    /// ES integrates the Cornish-Fisher quantile over the tail.
    /// </summary>
    public static VarEstimate CornishFisher(ReadOnlySpan<double> returns, double confidence = 0.95)
    {
        double m = Stats.Mean(returns), s = Stats.StdDev(returns);
        double skew = Stats.Skewness(returns), kurt = Stats.ExcessKurtosis(returns);
        double Q(double p)
        {
            double z = Normal.InverseCdf(p);
            double zcf = z + (z * z - 1) * skew / 6 + (z * z * z - 3 * z) * kurt / 24 - (2 * z * z * z - 5 * z) * skew * skew / 36;
            return m + s * zcf;
        }
        double alpha = 1 - confidence;
        double var = -Q(alpha);
        double es = -Integration.GaussLegendre(Q, 1e-9, alpha, 16) / alpha;
        return new VarEstimate(var, es, confidence, VarMethod.CornishFisher);
    }

    /// <summary>
    /// Monte Carlo VaR/ES for a portfolio with asset weights, mean returns and covariance of returns over the horizon
    /// (multivariate normal simple returns).
    /// </summary>
    public static VarEstimate MonteCarlo(double[] weights, double[] meanReturns, Matrix covariance, double confidence = 0.95, int simulations = 100_000, ulong seed = 42)
    {
        int n = weights.Length;
        Guard.SameLength(n, meanReturns.Length, "weights and means");
        var l = covariance.Cholesky();
        double mu = 0;
        for (int i = 0; i < n; i++) mu += weights[i] * meanReturns[i];
        // Portfolio loading on each independent shock: (Lᵀ w)_k.
        var load = new double[n];
        for (int k = 0; k < n; k++)
        {
            double s = 0;
            for (int i = k; i < n; i++) s += weights[i] * l[i, k];
            load[k] = s;
        }
        var sims = new double[simulations];
        const int block = 8192;
        int blocks = (simulations + block - 1) / block;
        var root = new FastRandom(seed);
        var seeds = new ulong[blocks];
        for (int b = 0; b < blocks; b++) seeds[b] = root.NextUInt64();
        Parallel.For(0, blocks, b =>
        {
            var rng = new FastRandom(seeds[b]);
            int end = Math.Min(simulations, (b + 1) * block);
            for (int p = b * block; p < end; p++)
            {
                double r = mu;
                for (int k = 0; k < n; k++) r += load[k] * rng.NextGaussian();
                sims[p] = r;
            }
        });
        return Historical(sims, confidence) with { Method = VarMethod.MonteCarlo };
    }

    /// <summary>Monte Carlo VaR/ES by resampling any return simulator.</summary>
    public static VarEstimate MonteCarlo(Func<FastRandom, double> simulateReturn, double confidence = 0.95, int simulations = 100_000, ulong seed = 42)
    {
        var rng = new FastRandom(seed);
        var sims = new double[simulations];
        for (int i = 0; i < simulations; i++) sims[i] = simulateReturn(rng);
        return Historical(sims, confidence) with { Method = VarMethod.MonteCarlo };
    }

    /// <summary>Estimate by method name.</summary>
    public static VarEstimate Estimate(ReadOnlySpan<double> returns, VarMethod method, double confidence = 0.95) => method switch
    {
        VarMethod.Historical => Historical(returns, confidence),
        VarMethod.ParametricNormal => ParametricNormal(returns, confidence),
        VarMethod.ParametricStudentT => ParametricStudentT(returns, 5, confidence),
        VarMethod.CornishFisher => CornishFisher(returns, confidence),
        VarMethod.MonteCarlo => ParametricNormalMonteCarlo(returns, confidence),
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    private static VarEstimate ParametricNormalMonteCarlo(ReadOnlySpan<double> returns, double confidence)
    {
        double m = Stats.Mean(returns), s = Stats.StdDev(returns);
        return MonteCarlo(rng => m + s * rng.NextGaussian(), confidence);
    }

    /// <summary>Square-root-of-time scaling of a one-period VaR to <paramref name="periods"/> periods (i.i.d., zero mean).</summary>
    public static double ScaleToHorizon(double onePeriodVar, int periods) => onePeriodVar * Math.Sqrt(periods);
}

/// <summary>Per-asset decomposition of parametric portfolio VaR.</summary>
/// <param name="PortfolioVar">Total portfolio VaR (zero mean, normal).</param>
/// <param name="PortfolioVolatility">Portfolio standard deviation.</param>
/// <param name="Marginal">∂VaR/∂w_i.</param>
/// <param name="Component">w_i · marginal_i; sums to <paramref name="PortfolioVar"/>.</param>
/// <param name="PercentContribution">Component / total.</param>
public sealed record VarDecomposition(double PortfolioVar, double PortfolioVolatility, double[] Marginal, double[] Component, double[] PercentContribution);

/// <summary>Marginal, component and incremental VaR.</summary>
public static class VarAttribution
{
    /// <summary>Euler decomposition of normal VaR for weights <paramref name="weights"/> and covariance <paramref name="covariance"/>.</summary>
    public static VarDecomposition Decompose(double[] weights, Matrix covariance, double confidence = 0.95)
    {
        int n = weights.Length;
        var sw = covariance.Multiply(weights);
        double sigma = Math.Sqrt(covariance.QuadraticForm(weights));
        double z = Normal.InverseCdf(confidence);
        var marginal = new double[n];
        var component = new double[n];
        var pct = new double[n];
        double total = z * sigma;
        for (int i = 0; i < n; i++)
        {
            marginal[i] = z * sw[i] / sigma;
            component[i] = weights[i] * marginal[i];
            pct[i] = component[i] / total;
        }
        return new VarDecomposition(total, sigma, marginal, component, pct);
    }

    /// <summary>Incremental VaR of adding <paramref name="delta"/> to the weights (full revaluation).</summary>
    public static double Incremental(double[] weights, double[] delta, Matrix covariance, double confidence = 0.95)
    {
        var w2 = new double[weights.Length];
        for (int i = 0; i < w2.Length; i++) w2[i] = weights[i] + delta[i];
        double z = Normal.InverseCdf(confidence);
        return z * (Math.Sqrt(covariance.QuadraticForm(w2)) - Math.Sqrt(covariance.QuadraticForm(weights)));
    }
}
