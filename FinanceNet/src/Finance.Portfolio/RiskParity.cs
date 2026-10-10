using Finance.Mathematics;

namespace Finance.Portfolio;

/// <summary>
/// Risk parity and risk budgeting (long-only, fully invested).
/// </summary>
/// <remarks>
/// Solved by cyclical coordinate descent on the Spinu (2013) convex formulation
/// min ½xᵀΣx − Σ bᵢ ln xᵢ, whose closed-form coordinate update is
/// xᵢ = (−sᵢ + √(sᵢ² + 4Σᵢᵢbᵢ)) / (2Σᵢᵢ) with sᵢ = Σⱼ≠ᵢ Σᵢⱼxⱼ (Griveau-Billion, Richard &amp; Roncalli, 2013).
/// The optimum satisfies xᵢ(Σx)ᵢ = bᵢ, so after normalisation each asset's risk contribution is ∝ bᵢ.
/// </remarks>
public static class RiskParity
{
    /// <summary>Equal-risk-contribution portfolio.</summary>
    public static PortfolioWeights EqualRiskContribution(Matrix covariance, IReadOnlyList<string>? assets = null)
        => RiskBudget(covariance, Enumerable.Repeat(1.0 / covariance.Rows, covariance.Rows).ToArray(), assets);

    /// <summary>Portfolio whose fractional risk contributions equal <paramref name="budgets"/> (normalised).</summary>
    public static PortfolioWeights RiskBudget(Matrix covariance, double[] budgets, IReadOnlyList<string>? assets = null, double tolerance = 1e-12, int maxSweeps = 10_000)
    {
        int n = covariance.Rows;
        if (!covariance.IsSquare || budgets.Length != n) throw new ArgumentException("Budgets must have one entry per asset.");
        if (budgets.Any(b => !(b > 0))) throw new ArgumentException("Risk budgets must be positive.");
        double bSum = budgets.Sum();
        var b = budgets.Select(v => v / bSum).ToArray();

        var x = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (!(covariance[i, i] > 0)) throw new ArgumentException("Every asset needs positive variance.");
            x[i] = 1 / Math.Sqrt(covariance[i, i]);
        }
        double xs = x.Sum();
        for (int i = 0; i < n; i++) x[i] /= xs;

        for (int sweep = 0; sweep < maxSweeps; sweep++)
        {
            double maxRel = 0;
            for (int i = 0; i < n; i++)
            {
                double sii = covariance[i, i];
                double s = Matrix.Dot(covariance.Row(i), x) - sii * x[i];
                double updated = (-s + Math.Sqrt(s * s + 4 * sii * b[i])) / (2 * sii);
                maxRel = Math.Max(maxRel, Math.Abs(updated - x[i]) / Math.Max(updated, 1e-300));
                x[i] = updated;
            }
            if (maxRel < tolerance) break;
        }
        double total = x.Sum();
        for (int i = 0; i < n; i++) x[i] /= total;
        return new PortfolioWeights(x, assets);
    }

    /// <summary>
    /// Fractional risk contributions wᵢ(Σw)ᵢ / wᵀΣw (they sum to one).
    /// </summary>
    public static double[] RiskContributions(ReadOnlySpan<double> weights, Matrix covariance)
    {
        var sw = covariance.Multiply(weights);
        double variance = Matrix.Dot(weights, sw);
        var rc = new double[weights.Length];
        for (int i = 0; i < rc.Length; i++) rc[i] = weights[i] * sw[i] / variance;
        return rc;
    }
}

/// <summary>Simple heuristic allocations.</summary>
public static class HeuristicPortfolios
{
    /// <summary>1/N.</summary>
    public static PortfolioWeights EqualWeight(int n, IReadOnlyList<string>? assets = null)
        => new(Enumerable.Repeat(1.0 / n, n).ToArray(), assets);

    /// <summary>Weights ∝ 1/σᵢ.</summary>
    public static PortfolioWeights InverseVolatility(Matrix covariance, IReadOnlyList<string>? assets = null)
    {
        var w = Enumerable.Range(0, covariance.Rows).Select(i => 1 / Math.Sqrt(covariance[i, i])).ToArray();
        double s = w.Sum();
        return new PortfolioWeights(w.Select(v => v / s).ToArray(), assets);
    }

    /// <summary>Weights ∝ 1/σᵢ² (the HRP leaf allocation).</summary>
    public static PortfolioWeights InverseVariance(Matrix covariance, IReadOnlyList<string>? assets = null)
    {
        var w = Enumerable.Range(0, covariance.Rows).Select(i => 1 / covariance[i, i]).ToArray();
        double s = w.Sum();
        return new PortfolioWeights(w.Select(v => v / s).ToArray(), assets);
    }

    /// <summary>
    /// Maximum diversification portfolio (Choueifaty &amp; Coignard): maximises (wᵀσ)/√(wᵀΣw).
    /// Equivalent to a max-Sharpe problem with μ = σ and rf = 0.
    /// </summary>
    public static PortfolioWeights MaxDiversification(Matrix covariance, double lowerBound = 0, double upperBound = 1, IReadOnlyList<string>? assets = null)
    {
        var sigma = Enumerable.Range(0, covariance.Rows).Select(i => Math.Sqrt(covariance[i, i])).ToArray();
        return new MeanVarianceOptimizer(sigma, covariance, lowerBound, upperBound, assets).MaxSharpe(0);
    }

    /// <summary>Diversification ratio (wᵀσ)/√(wᵀΣw); 1 for a single asset, higher is more diversified.</summary>
    public static double DiversificationRatio(ReadOnlySpan<double> weights, Matrix covariance)
    {
        double num = 0;
        for (int i = 0; i < weights.Length; i++) num += weights[i] * Math.Sqrt(covariance[i, i]);
        return num / Math.Sqrt(covariance.QuadraticForm(weights));
    }
}
