using Finance.Core;
using Finance.Mathematics;

namespace Finance.Portfolio;

/// <summary>A linear constraint lower ≤ aᵀw ≤ upper on the portfolio weights.</summary>
public sealed record WeightConstraint(string Name, double[] Coefficients, double Lower, double Upper);

/// <summary>Result of a constrained optimisation.</summary>
/// <param name="Weights">Optimal weights.</param>
/// <param name="Trades">wᵢ − w0ᵢ (all zero when no current portfolio was given).</param>
/// <param name="Turnover">Σ|wᵢ − w0ᵢ|.</param>
/// <param name="TransactionCost">Σ cᵇᵤᵧ·buyᵢ + cˢₑₗₗ·sellᵢ, as a fraction of portfolio value.</param>
/// <param name="ExpectedReturn">μᵀw.</param>
/// <param name="Volatility">√(wᵀΣw).</param>
/// <param name="Solution">The underlying QP solution (iterations, duals, convergence).</param>
public sealed record ConstrainedPortfolio(PortfolioWeights Weights, double[] Trades, double Turnover, double TransactionCost,
    double ExpectedReturn, double Volatility, QpSolution Solution);

/// <summary>
/// Mean-variance optimisation with general linear constraints, solved by the interior-point
/// <see cref="QuadraticProgram"/>: per-asset bounds, sector and factor-exposure limits, a minimum return, and,
/// given the current portfolio, proportional transaction costs and a turnover cap.
/// </summary>
/// <remarks>
/// Trades are modelled with buy and sell variables b ≥ w − w0, s ≥ w0 − w, b, s ≥ 0 (two-entry rows, cheap for the
/// solver). Costs c·(b + s) enter the objective, so the optimiser trades only when the risk-return improvement pays
/// for it; that is the no-trade region of cost-aware rebalancing. Turnover is Σ(b + s) ≤ cap; at the optimum b and s
/// are the actual buys and sells whenever trading has a cost or the cap binds.
/// </remarks>
public sealed class ConstrainedPortfolioOptimizer
{
    private readonly double[] _mu;
    private readonly Matrix _cov;
    private readonly List<WeightConstraint> _constraints = new();

    public ConstrainedPortfolioOptimizer(double[] expectedReturns, Matrix covariance, IReadOnlyList<string>? assets = null)
    {
        if (!covariance.IsSquare || covariance.Rows != expectedReturns.Length)
            throw new ArgumentException("Covariance must be n x n with n = number of expected returns.");
        _mu = (double[])expectedReturns.Clone();
        _cov = covariance;
        int n = _mu.Length;
        Assets = assets ?? Enumerable.Range(0, n).Select(i => $"Asset{i + 1}").ToArray();
        LowerBounds = new double[n];
        UpperBounds = Enumerable.Repeat(1.0, n).ToArray();
    }

    public IReadOnlyList<string> Assets { get; }
    public int Count => _mu.Length;

    /// <summary>Per-asset lower bounds (default 0 — long only). Negative values allow shorting.</summary>
    public double[] LowerBounds { get; }

    /// <summary>Per-asset upper bounds (default 1).</summary>
    public double[] UpperBounds { get; }

    /// <summary>Σw (default 1, fully invested).</summary>
    public double Budget { get; set; } = 1;

    /// <summary>Current portfolio; required for costs and turnover.</summary>
    public double[]? CurrentWeights { get; set; }

    /// <summary>Proportional cost of buying (fraction of traded value), per asset; null = free.</summary>
    public double[]? BuyCosts { get; set; }

    /// <summary>Proportional cost of selling, per asset; null = same as <see cref="BuyCosts"/>.</summary>
    public double[]? SellCosts { get; set; }

    /// <summary>Maximum Σ|w − w0| (two-way turnover); null = unlimited.</summary>
    public double? MaxTurnover { get; set; }

    /// <summary>Minimum expected return μᵀw; null = none.</summary>
    public double? MinReturn { get; set; }

    public IReadOnlyList<WeightConstraint> Constraints => _constraints;

    /// <summary>Sets the same bounds on every asset.</summary>
    public ConstrainedPortfolioOptimizer WithBounds(double lower, double upper)
    {
        Array.Fill(LowerBounds, lower);
        Array.Fill(UpperBounds, upper);
        return this;
    }

    /// <summary>Adds lower ≤ aᵀw ≤ upper.</summary>
    public ConstrainedPortfolioOptimizer AddConstraint(string name, double[] coefficients, double lower, double upper)
    {
        if (coefficients.Length != Count) throw new ArgumentException("Coefficient count must match the number of assets.");
        _constraints.Add(new WeightConstraint(name, (double[])coefficients.Clone(), lower, upper));
        return this;
    }

    /// <summary>Limits the total weight of the assets whose sector is <paramref name="sector"/>.</summary>
    public ConstrainedPortfolioOptimizer AddSectorLimit(IReadOnlyList<string> sectorOfAsset, string sector, double lower, double upper)
    {
        if (sectorOfAsset.Count != Count) throw new ArgumentException("One sector label per asset is required.");
        var a = sectorOfAsset.Select(s => string.Equals(s, sector, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0).ToArray();
        return AddConstraint($"sector:{sector}", a, lower, upper);
    }

    /// <summary>Limits the portfolio's exposure Σ βᵢwᵢ to one factor.</summary>
    public ConstrainedPortfolioOptimizer AddFactorExposure(string factor, double[] loadings, double lower, double upper)
        => AddConstraint($"factor:{factor}", loadings, lower, upper);

    /// <summary>Maximises μᵀw − (δ/2)·wᵀΣw − costs.</summary>
    public ConstrainedPortfolio MaxQuadraticUtility(double riskAversion = 1)
    {
        Guard.Positive(riskAversion);
        return Solve(riskAversion, includeReturn: true);
    }

    /// <summary>Minimum variance (plus costs) under the constraints, optionally with <see cref="MinReturn"/>.</summary>
    public ConstrainedPortfolio MinVolatility() => Solve(2, includeReturn: false);

    /// <summary>Minimum variance subject to μᵀw ≥ <paramref name="targetReturn"/>.</summary>
    public ConstrainedPortfolio EfficientReturn(double targetReturn)
    {
        var keep = MinReturn;
        MinReturn = targetReturn;
        try { return Solve(2, includeReturn: false); }
        finally { MinReturn = keep; }
    }

    private ConstrainedPortfolio Solve(double riskAversion, bool includeReturn)
    {
        int n = Count;
        bool trades = CurrentWeights is not null;
        if (!trades && (BuyCosts is not null || SellCosts is not null || MaxTurnover is not null))
            throw new InvalidOperationException("Transaction costs and turnover need CurrentWeights.");
        int nv = trades ? 3 * n : n;

        // Objective ½·δ·wᵀΣw − μᵀw + c_b·b + c_s·s (weights first, then buys, then sells).
        var q = new Matrix(nv, nv);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) q[i, j] = riskAversion * _cov[i, j];
        var c = new double[nv];
        if (includeReturn) for (int i = 0; i < n; i++) c[i] = -_mu[i];
        var buy = BuyCosts ?? new double[n];
        var sell = SellCosts ?? buy;
        if (trades)
            for (int i = 0; i < n; i++) { c[n + i] = buy[i]; c[2 * n + i] = sell[i]; }

        var qp = new QuadraticProgram(q, c);
        var row = new double[nv];
        for (int i = 0; i < n; i++) row[i] = 1;
        qp.AddEquality(row, Budget);
        for (int i = 0; i < n; i++) qp.SetBounds(i, LowerBounds[i], UpperBounds[i]);

        if (trades)
        {
            var w0 = CurrentWeights!;
            if (w0.Length != n) throw new ArgumentException("CurrentWeights must have one entry per asset.");
            for (int i = 0; i < n; i++)
            {
                var up = new double[nv];
                up[i] = 1; up[n + i] = -1;                            // w − b ≤ w0   (b ≥ w − w0)
                qp.AddInequality(up, w0[i]);
                var down = new double[nv];
                down[i] = -1; down[2 * n + i] = -1;                   // −w − s ≤ −w0 (s ≥ w0 − w)
                qp.AddInequality(down, -w0[i]);
                qp.SetBounds(n + i, 0, double.PositiveInfinity);
                qp.SetBounds(2 * n + i, 0, double.PositiveInfinity);
            }
            if (MaxTurnover is double cap)
            {
                var r = new double[nv];
                for (int i = n; i < nv; i++) r[i] = 1;
                qp.AddInequality(r, cap);
            }
        }

        foreach (var con in _constraints)
        {
            var r = new double[nv];
            Array.Copy(con.Coefficients, r, n);
            qp.AddRange(r, con.Lower, con.Upper);
        }
        if (MinReturn is double target)
        {
            var r = new double[nv];
            for (int i = 0; i < n; i++) r[i] = -_mu[i];
            qp.AddInequality(r, -target);
        }

        var sol = qp.Solve();
        if (!sol.Converged)
            throw new InvalidOperationException(
                $"The constrained problem did not converge after {sol.Iterations} iterations (primal residual {sol.PrimalResidual:E2}, " +
                $"dual {sol.DualResidual:E2}, gap {sol.Gap:E2}); check that the constraints are feasible.");
        var w = sol.X.Take(n).ToArray();
        var dw = new double[n];
        double turnover = 0, cost = 0;
        if (trades)
            for (int i = 0; i < n; i++)
            {
                dw[i] = w[i] - CurrentWeights![i];
                turnover += Math.Abs(dw[i]);
                cost += dw[i] > 0 ? buy[i] * dw[i] : -sell[i] * dw[i];
            }
        double er = Matrix.Dot(_mu, w), vol = Math.Sqrt(Math.Max(0, _cov.QuadraticForm(w)));
        return new ConstrainedPortfolio(new PortfolioWeights(w, Assets), dw, turnover, cost, er, vol, sol);
    }
}

/// <summary>
/// Michaud resampled efficiency: draw <c>simulations</c> histories of <c>observations</c> returns from N(μ, Σ),
/// re-estimate the inputs, compute each history's frontier, and average the weights rank by rank. The averaged
/// portfolios are evaluated on the original inputs. They sit slightly inside the classical frontier but are
/// far more diversified and stable under estimation error.
/// </summary>
public static class ResampledFrontier
{
    public static IReadOnlyList<FrontierPoint> Compute(double[] expectedReturns, Matrix covariance, int observations,
        int simulations = 100, int points = 20, double lowerBound = 0, double upperBound = 1,
        IReadOnlyList<string>? assets = null, ulong seed = 42)
    {
        Guard.Positive(observations);
        Guard.Positive(simulations);
        int n = expectedReturns.Length;
        var chol = covariance.Cholesky();
        var rng = new FastRandom(seed);
        var sum = new double[points][];
        for (int p = 0; p < points; p++) sum[p] = new double[n];
        var sample = new double[observations][];
        var z = new double[n];

        for (int sim = 0; sim < simulations; sim++)
        {
            for (int t = 0; t < observations; t++)
            {
                for (int i = 0; i < n; i++) z[i] = rng.NextGaussian();
                var r = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double v = expectedReturns[i];
                    for (int k = 0; k <= i; k++) v += chol[i, k] * z[k];
                    r[i] = v;
                }
                sample[t] = r;
            }
            var mu = new double[n];
            for (int i = 0; i < n; i++) mu[i] = sample.Average(r => r[i]);
            var cov = new Matrix(n, n);
            for (int i = 0; i < n; i++)
                for (int j = 0; j <= i; j++)
                {
                    double c = 0;
                    for (int t = 0; t < observations; t++) c += (sample[t][i] - mu[i]) * (sample[t][j] - mu[j]);
                    cov[i, j] = cov[j, i] = c / (observations - 1);
                }
            // Rank-matched frontier of this history: min-variance portfolio up to (just below) the top attainable return,
            // each point solved with the interior-point optimiser.
            var opt = new ConstrainedPortfolioOptimizer(mu, cov).WithBounds(lowerBound, upperBound);
            var minVol = opt.MinVolatility();
            double rMin = minVol.ExpectedReturn, rMax = new MeanVarianceOptimizer(mu, cov, lowerBound, upperBound).MaxAttainableReturn;
            for (int p = 0; p < points; p++)
            {
                var w = p == 0 || rMax <= rMin ? minVol.Weights : opt.EfficientReturn(rMin + (rMax - rMin) * p / (points - 1) * 0.999).Weights;
                for (int i = 0; i < n; i++) sum[p][i] += w[i] / simulations;
            }
        }

        var names = assets ?? Enumerable.Range(0, n).Select(i => $"Asset{i + 1}").ToArray();
        return sum.Select(w =>
        {
            var perf = PortfolioPerformance.Compute(w, expectedReturns, covariance);
            return new FrontierPoint(perf.ExpectedReturn, perf.Volatility, perf.SharpeRatio, new PortfolioWeights(w, names));
        }).OrderBy(f => f.Volatility).ToList();
    }
}
