using Finance.Core;
using Finance.Mathematics;

namespace Finance.Risk;

/// <summary>Fitted GARCH(1,1) model σ²ₜ = ω + α ε²ₜ₋₁ + β σ²ₜ₋₁ with Gaussian innovations.</summary>
public sealed record Garch11Model(double Mu, double Omega, double Alpha, double Beta, double LogLikelihood, double[] ConditionalVariances, double LastResidual)
{
    /// <summary>α + β.</summary>
    public double Persistence => Alpha + Beta;

    /// <summary>Unconditional variance ω / (1 − α − β).</summary>
    public double LongRunVariance => Omega / (1 - Alpha - Beta);

    /// <summary>Annualised long-run volatility.</summary>
    public double LongRunVolatility(double periodsPerYear = 252) => Math.Sqrt(LongRunVariance * periodsPerYear);

    /// <summary>Half-life of a volatility shock in periods.</summary>
    public double HalfLife => Math.Log(0.5) / Math.Log(Persistence);

    /// <summary>Variance forecasts for the next <paramref name="horizon"/> periods.</summary>
    public double[] ForecastVariance(int horizon)
    {
        var f = new double[horizon];
        double next = Omega + Alpha * LastResidual * LastResidual + Beta * ConditionalVariances[^1];
        double vl = LongRunVariance, p = Persistence;
        for (int k = 0; k < horizon; k++) f[k] = vl + Math.Pow(p, k) * (next - vl);
        return f;
    }

    /// <summary>Volatility forecasts (square roots of <see cref="ForecastVariance"/>).</summary>
    public double[] ForecastVolatility(int horizon) => ForecastVariance(horizon).Select(Math.Sqrt).ToArray();
}

/// <summary>Conditional volatility models.</summary>
public static class VolatilityModels
{
    /// <summary>
    /// Fits GARCH(1,1) by maximum likelihood (Nelder-Mead in an unconstrained reparameterisation that
    /// enforces ω &gt; 0, α, β ≥ 0 and α + β &lt; 1). The variance recursion starts at the sample variance.
    /// </summary>
    public static Garch11Model FitGarch11(ReadOnlySpan<double> returns, int maxIterations = 4000)
    {
        Guard.MinLength(returns.Length, 30);
        double mu = Stats.Mean(returns);
        var eps = new double[returns.Length];
        for (int i = 0; i < eps.Length; i++) eps[i] = returns[i] - mu;
        double v0 = Stats.Variance(eps, 0);
        var buffer = new double[eps.Length];

        static double Logistic(double x) => 1 / (1 + Math.Exp(-x));
        static double Logit(double p) => Math.Log(p / (1 - p));
        (double w, double a, double b) Map(double[] x)
        {
            double persistence = 0.9999 * Logistic(x[1]);
            double a = persistence * Logistic(x[2]);
            return (Math.Exp(x[0]), a, persistence - a);
        }
        double NegLogLik(double[] x)
        {
            var (w, a, b) = Map(x);
            return -LogLikelihood(eps, w, a, b, v0, buffer);
        }

        double p0 = 0.95, a0 = 0.06;
        double[] start = { Math.Log(v0 * (1 - p0)), Logit(p0 / 0.9999), Logit(a0 / p0) };
        var res = Optimizer.NelderMead(NegLogLik, start, 0.3, maxIterations, 1e-12);
        res = Optimizer.NelderMead(NegLogLik, res.X, 0.05, maxIterations, 1e-13); // restart polishes the simplex
        var (omega, alpha, beta) = Map(res.X);
        var sig = new double[eps.Length];
        double ll = LogLikelihood(eps, omega, alpha, beta, v0, sig);
        return new Garch11Model(mu, omega, alpha, beta, ll, sig, eps[^1]);
    }

    private static double LogLikelihood(double[] eps, double omega, double alpha, double beta, double v0, double[] sig)
    {
        const double log2Pi = 1.8378770664093453;
        double v = v0, ll = 0;
        for (int t = 0; t < eps.Length; t++)
        {
            if (t > 0) v = omega + alpha * eps[t - 1] * eps[t - 1] + beta * v;
            sig[t] = v;
            ll += -0.5 * (log2Pi + Math.Log(v) + eps[t] * eps[t] / v);
        }
        return ll;
    }

    /// <summary>Simulates a GARCH(1,1) return series (for testing and scenario generation).</summary>
    public static double[] SimulateGarch11(int length, double mu, double omega, double alpha, double beta, ulong seed = 1)
    {
        var rng = new FastRandom(seed);
        var r = new double[length];
        double v = omega / (1 - alpha - beta), e = 0;
        for (int t = 0; t < length; t++)
        {
            v = omega + alpha * e * e + beta * v;
            e = Math.Sqrt(v) * rng.NextGaussian();
            r[t] = mu + e;
        }
        return r;
    }

    /// <summary>
    /// RiskMetrics EWMA volatility: σ²ₜ = λσ²ₜ₋₁ + (1 − λ) r²ₜ₋₁. Element t is the forecast for period t made
    /// with information up to t − 1 (element 0 is seeded with the first squared return).
    /// </summary>
    public static double[] Ewma(ReadOnlySpan<double> returns, double lambda = 0.94)
    {
        var vol = new double[returns.Length];
        if (returns.Length == 0) return vol;
        double v = returns[0] * returns[0];
        vol[0] = Math.Sqrt(v);
        for (int t = 1; t < returns.Length; t++)
        {
            v = lambda * v + (1 - lambda) * returns[t - 1] * returns[t - 1];
            vol[t] = Math.Sqrt(v);
        }
        return vol;
    }

    /// <summary>EWMA covariance matrix of the columns of <paramref name="returns"/> (rows = time).</summary>
    public static Matrix EwmaCovariance(Matrix returns, double lambda = 0.94)
    {
        int n = returns.Columns;
        var cov = new Matrix(n, n);
        double weightSum = 0, w = 1;
        for (int t = returns.Rows - 1; t >= 0; t--)
        {
            var row = returns.Row(t);
            for (int a = 0; a < n; a++)
                for (int b = a; b < n; b++) cov[a, b] += w * row[a] * row[b];
            weightSum += w;
            w *= lambda;
        }
        for (int a = 0; a < n; a++)
            for (int b = a; b < n; b++)
            {
                cov[a, b] /= weightSum;
                cov[b, a] = cov[a, b];
            }
        return cov;
    }

    /// <summary>Parkinson high-low range volatility estimator (annualised).</summary>
    public static double Parkinson(ReadOnlySpan<Finance.Core.Market.Bar> bars, double periodsPerYear = 252)
    {
        double s = 0;
        foreach (var b in bars)
        {
            double l = Math.Log(b.High / b.Low);
            s += l * l;
        }
        return Math.Sqrt(s / (4 * Math.Log(2) * bars.Length) * periodsPerYear);
    }
}

/// <summary>Euler risk attribution.</summary>
public static class RiskAttribution
{
    /// <summary>
    /// Volatility contributions RCᵢ = wᵢ (Σw)ᵢ / σₚ; they sum to σₚ. Returns absolute and percentage contributions.
    /// </summary>
    public static (double PortfolioVolatility, double[] Contributions, double[] Percent) VolatilityContributions(double[] weights, Matrix covariance)
    {
        var sw = covariance.Multiply(weights);
        double sigma = Math.Sqrt(covariance.QuadraticForm(weights));
        var rc = new double[weights.Length];
        var pct = new double[weights.Length];
        for (int i = 0; i < rc.Length; i++)
        {
            rc[i] = weights[i] * sw[i] / sigma;
            pct[i] = rc[i] / sigma;
        }
        return (sigma, rc, pct);
    }

    /// <summary>
    /// Expected-shortfall contributions from scenario returns (rows = scenarios, columns = assets):
    /// the average asset P&amp;L in the scenarios beyond the portfolio VaR. Contributions sum to portfolio ES.
    /// </summary>
    public static double[] ExpectedShortfallContributions(double[] weights, Matrix scenarioReturns, double confidence = 0.95)
    {
        int s = scenarioReturns.Rows, n = weights.Length;
        var port = new double[s];
        for (int i = 0; i < s; i++) port[i] = Matrix.Dot(scenarioReturns.Row(i), weights);
        double q = Stats.Quantile(port, 1 - confidence);
        var contrib = new double[n];
        int k = 0;
        for (int i = 0; i < s; i++)
        {
            if (port[i] > q) continue;
            k++;
            var row = scenarioReturns.Row(i);
            for (int j = 0; j < n; j++) contrib[j] -= weights[j] * row[j];
        }
        for (int j = 0; j < n; j++) contrib[j] /= Math.Max(1, k);
        return contrib;
    }

    /// <summary>
    /// Factor risk decomposition for exposures B (assets × factors), factor covariance F and specific variances D:
    /// returns the systematic and idiosyncratic shares of portfolio variance.
    /// </summary>
    public static (double TotalVariance, double SystematicVariance, double SpecificVariance) FactorDecomposition(
        double[] weights, Matrix exposures, Matrix factorCovariance, double[] specificVariances)
    {
        int k = exposures.Columns;
        var fe = new double[k]; // Bᵀ w
        for (int j = 0; j < k; j++)
            for (int i = 0; i < weights.Length; i++) fe[j] += exposures[i, j] * weights[i];
        double sys = factorCovariance.QuadraticForm(fe), spec = 0;
        for (int i = 0; i < weights.Length; i++) spec += weights[i] * weights[i] * specificVariances[i];
        return (sys + spec, sys, spec);
    }
}
