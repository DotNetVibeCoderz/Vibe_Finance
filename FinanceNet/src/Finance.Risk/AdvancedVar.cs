using Finance.Core;
using Finance.Mathematics;

namespace Finance.Risk;

/// <summary>Volatility filter used by <see cref="AdvancedVar.FilteredHistorical"/>.</summary>
public enum VolatilityFilter { Garch11, Ewma }

/// <summary>Filtered historical simulation result: the estimate plus the filter state it was scaled to.</summary>
/// <param name="Estimate">VaR / ES as positive loss fractions.</param>
/// <param name="CurrentVolatility">σ forecast for the next period the residuals were rescaled to.</param>
/// <param name="StandardizedResiduals">zₜ = (rₜ − μ) / σₜ, the filtered innovations.</param>
public sealed record FilteredHistoricalResult(VarEstimate Estimate, double CurrentVolatility, double[] StandardizedResiduals);

/// <summary>Peaks-over-threshold fit of a Generalized Pareto distribution to the loss tail.</summary>
/// <param name="Threshold">u, the loss level above which the tail is modelled.</param>
/// <param name="Xi">Shape ξ (tail index; ξ &gt; 0 is a heavy, Pareto-type tail, ξ = 0 exponential).</param>
/// <param name="Beta">Scale β &gt; 0.</param>
/// <param name="Exceedances">Number of losses above the threshold.</param>
/// <param name="Observations">Total number of observations.</param>
public sealed record GpdTailFit(double Threshold, double Xi, double Beta, int Exceedances, int Observations)
{
    /// <summary>Tail VaR at <paramref name="confidence"/> (a positive loss), valid above the threshold.</summary>
    public double ValueAtRisk(double confidence)
    {
        double tail = (double)Observations / Exceedances * (1 - confidence);
        return Math.Abs(Xi) < 1e-9
            ? Threshold - Beta * Math.Log(tail)
            : Threshold + Beta / Xi * (Math.Pow(tail, -Xi) - 1);
    }

    /// <summary>Tail Expected Shortfall at <paramref name="confidence"/>; finite only for ξ &lt; 1.</summary>
    public double ExpectedShortfall(double confidence)
    {
        if (Xi >= 1) return double.PositiveInfinity;
        double v = ValueAtRisk(confidence);
        return v / (1 - Xi) + (Beta - Xi * Threshold) / (1 - Xi);
    }
}

/// <summary>
/// v0.2 VaR models: filtered historical simulation, extreme-value (peaks over threshold), liquidity
/// adjustments and simulation-based attribution of what-if trades.
/// </summary>
public static class AdvancedVar
{
    /// <summary>
    /// Filtered historical simulation (Barone-Adesi, Giannopoulos and Vosper, 1999). Returns are
    /// de-volatilised with a GARCH(1,1) or EWMA filter, and the standardized residuals are rescaled
    /// to the next period's volatility forecast. Today's risk then reflects today's volatility, while
    /// the shape of the distribution is still taken from history.
    /// </summary>
    public static FilteredHistoricalResult FilteredHistorical(ReadOnlySpan<double> returns, double confidence = 0.99,
        VolatilityFilter filter = VolatilityFilter.Garch11, double lambda = 0.94)
    {
        Guard.MinLength(returns.Length, 30);
        int n = returns.Length;
        var z = new double[n];
        double mu, next;
        if (filter == VolatilityFilter.Garch11)
        {
            var g = VolatilityModels.FitGarch11(returns);
            mu = g.Mu;
            for (int t = 0; t < n; t++) z[t] = (returns[t] - mu) / Math.Sqrt(g.ConditionalVariances[t]);
            next = Math.Sqrt(g.ForecastVariance(1)[0]);
        }
        else
        {
            // RiskMetrics: σ²ₜ = λσ²ₜ₋₁ + (1 − λ)r²ₜ₋₁, zero mean, seeded with the first squared return.
            mu = 0;
            double v = returns[0] * returns[0];
            for (int t = 0; t < n; t++)
            {
                if (t > 0) v = lambda * v + (1 - lambda) * returns[t - 1] * returns[t - 1];
                z[t] = v > 0 ? returns[t] / Math.Sqrt(v) : 0;
            }
            v = lambda * v + (1 - lambda) * returns[n - 1] * returns[n - 1];
            next = Math.Sqrt(v);
        }

        var scenarios = new double[n];
        for (int t = 0; t < n; t++) scenarios[t] = mu + next * z[t];
        var h = ValueAtRisk.Historical(scenarios, confidence);
        return new FilteredHistoricalResult(h with { Method = VarMethod.FilteredHistorical }, next, z);
    }

    /// <summary>
    /// Multi-period FHS: bootstraps standardized GARCH residuals through the variance recursion for
    /// <paramref name="horizon"/> periods, so volatility clustering inside the horizon is simulated rather than
    /// assumed away by √h scaling. Returns VaR/ES of the cumulative (summed log) return.
    /// </summary>
    public static VarEstimate FilteredHistoricalMultiPeriod(ReadOnlySpan<double> returns, int horizon, double confidence = 0.99,
        int simulations = 20_000, ulong seed = 42)
    {
        Guard.Positive(horizon);
        var g = VolatilityModels.FitGarch11(returns);
        int n = returns.Length;
        var z = new double[n];
        for (int t = 0; t < n; t++) z[t] = (returns[t] - g.Mu) / Math.Sqrt(g.ConditionalVariances[t]);
        double v1 = g.ForecastVariance(1)[0];
        var rng = new FastRandom(seed);
        var totals = new double[simulations];
        for (int s = 0; s < simulations; s++)
        {
            double v = v1, sum = 0;
            for (int k = 0; k < horizon; k++)
            {
                double eps = Math.Sqrt(v) * z[rng.NextInt(n)];
                sum += g.Mu + eps;
                v = g.Omega + g.Alpha * eps * eps + g.Beta * v;
            }
            totals[s] = sum;
        }
        return ValueAtRisk.Historical(totals, confidence) with { Method = VarMethod.FilteredHistorical };
    }

    /// <summary>
    /// Fits a Generalized Pareto distribution by maximum likelihood to the losses (−returns) above the
    /// <paramref name="thresholdQuantile"/> quantile of losses (peaks over threshold, McNeil and Frey, 2000).
    /// </summary>
    public static GpdTailFit FitTail(ReadOnlySpan<double> returns, double thresholdQuantile = 0.95)
    {
        Guard.MinLength(returns.Length, 50);
        var losses = new double[returns.Length];
        for (int i = 0; i < losses.Length; i++) losses[i] = -returns[i];
        Array.Sort(losses);
        double u = Stats.QuantileSorted(losses, thresholdQuantile);
        var excess = losses.Where(l => l > u).Select(l => l - u).ToArray();
        if (excess.Length < 10)
            throw new ArgumentException($"Only {excess.Length} exceedances above the {thresholdQuantile:P0} threshold; at least 10 are needed.");

        // Probability-weighted-moment start (Hosking and Wallis), then MLE in (ξ, log β).
        double m = excess.Average(), s2 = excess.Sum(e => (e - m) * (e - m)) / (excess.Length - 1);
        double xi0 = Math.Clamp(0.5 * (1 - m * m / s2), -0.4, 0.8);
        double beta0 = Math.Max(1e-12, 0.5 * m * (m * m / s2 + 1));

        double NegLogLik(double[] p)
        {
            double xi = p[0], beta = Math.Exp(p[1]);
            if (xi < -0.5 || xi > 1.5) return double.MaxValue;
            double ll = -excess.Length * Math.Log(beta);
            if (Math.Abs(xi) < 1e-9)
            {
                foreach (var e in excess) ll -= e / beta;
                return -ll;
            }
            foreach (var e in excess)
            {
                double arg = 1 + xi * e / beta;
                if (arg <= 0) return double.MaxValue;
                ll -= (1 + 1 / xi) * Math.Log(arg);
            }
            return -ll;
        }

        var fit = Optimizer.NelderMead(NegLogLik, new[] { xi0, Math.Log(beta0) }, initialStep: 0.1, maxIterations: 4000, tolerance: 1e-12);
        return new GpdTailFit(u, fit.X[0], Math.Exp(fit.X[1]), excess.Length, returns.Length);
    }

    /// <summary>Extreme-value VaR/ES from a peaks-over-threshold GPD fit (<see cref="FitTail"/>).</summary>
    public static VarEstimate ExtremeValue(ReadOnlySpan<double> returns, double confidence = 0.99, double thresholdQuantile = 0.95)
    {
        var fit = FitTail(returns, thresholdQuantile);
        if (1 - confidence >= (double)fit.Exceedances / fit.Observations)
            throw new ArgumentException("Confidence must lie beyond the threshold quantile for a tail estimate.");
        return new VarEstimate(fit.ValueAtRisk(confidence), fit.ExpectedShortfall(confidence), confidence, VarMethod.ExtremeValue);
    }

    /// <summary>
    /// Liquidity-adjusted VaR (Bangia, Diebold, Schuermann and Stroughair, 1999): market VaR plus half the
    /// relative bid-ask spread at a stressed level, ½·(μₛ + k·σₛ). All inputs are fractions of position value.
    /// </summary>
    public static double LiquidityAdjusted(double marketVar, double spreadMean, double spreadStdDev, double k = 3)
        => marketVar + 0.5 * (spreadMean + k * spreadStdDev);

    /// <summary>
    /// Orderly-liquidation adjustment: a position sold in equal slices over <paramref name="days"/> days is exposed
    /// for a shrinking amount, so its VaR is the one-day VaR times √((1 + T)(1 + 2T) / (6T)) rather than √T.
    /// </summary>
    public static double OrderlyLiquidation(double oneDayVar, int days)
    {
        Guard.Positive(days);
        return oneDayVar * Math.Sqrt((1.0 + days) * (1 + 2.0 * days) / (6.0 * days));
    }

    /// <summary>
    /// Historical-simulation VaR of a portfolio of instruments with scenario returns
    /// <paramref name="scenarios"/> (rows = scenarios, columns = instruments) and weights <paramref name="weights"/>.
    /// </summary>
    public static VarEstimate PortfolioHistorical(double[] weights, Matrix scenarios, double confidence = 0.99)
        => ValueAtRisk.Historical(PortfolioPnl(weights, scenarios), confidence);

    /// <summary>
    /// Incremental VaR of a what-if trade by full revaluation over historical scenarios:
    /// VaR(w + Δ) − VaR(w). Unlike the parametric version, it captures fat tails and non-linear payoffs.
    /// </summary>
    public static double IncrementalHistorical(double[] weights, double[] delta, Matrix scenarios, double confidence = 0.99)
    {
        var w2 = new double[weights.Length];
        for (int i = 0; i < w2.Length; i++) w2[i] = weights[i] + delta[i];
        return PortfolioHistorical(w2, scenarios, confidence).ValueAtRisk - PortfolioHistorical(weights, scenarios, confidence).ValueAtRisk;
    }

    /// <summary>
    /// Euler component VaR under historical simulation: each instrument's loss averaged over the scenarios in
    /// a small window around the VaR scenario (Hallerbach's kernel-free estimator). The components sum
    /// to that window's average portfolio loss, which approximates VaR.
    /// </summary>
    public static double[] ComponentHistorical(double[] weights, Matrix scenarios, double confidence = 0.99, int window = 5)
    {
        var pnl = PortfolioPnl(weights, scenarios);
        var order = Enumerable.Range(0, pnl.Length).OrderBy(i => pnl[i]).ToArray();
        int k = (int)Math.Floor((1 - confidence) * (pnl.Length - 1));
        int lo = Math.Max(0, k - window / 2), hi = Math.Min(pnl.Length - 1, lo + window - 1);
        var comp = new double[weights.Length];
        for (int j = lo; j <= hi; j++)
        {
            int s = order[j];
            for (int i = 0; i < comp.Length; i++) comp[i] -= weights[i] * scenarios[s, i];
        }
        for (int i = 0; i < comp.Length; i++) comp[i] /= hi - lo + 1;
        return comp;
    }

    /// <summary>First-order incremental VaR, Σ marginalᵢ·Δᵢ, from the parametric Euler decomposition.</summary>
    public static double IncrementalMarginal(double[] weights, double[] delta, Matrix covariance, double confidence = 0.99)
    {
        var d = VarAttribution.Decompose(weights, covariance, confidence);
        double s = 0;
        for (int i = 0; i < delta.Length; i++) s += d.Marginal[i] * delta[i];
        return s;
    }

    private static double[] PortfolioPnl(double[] weights, Matrix scenarios)
    {
        if (scenarios.Columns != weights.Length) throw new ArgumentException("Scenario columns must match the weights.");
        var pnl = new double[scenarios.Rows];
        for (int s = 0; s < pnl.Length; s++)
        {
            double v = 0;
            for (int i = 0; i < weights.Length; i++) v += weights[i] * scenarios[s, i];
            pnl[s] = v;
        }
        return pnl;
    }
}
