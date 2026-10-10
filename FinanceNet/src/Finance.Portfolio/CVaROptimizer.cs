using Finance.Mathematics;

namespace Finance.Portfolio;

/// <summary>
/// Conditional Value-at-Risk optimisation on return scenarios (Rockafellar &amp; Uryasev, 2000).
/// </summary>
/// <remarks>
/// Historical CVaR is a convex, piecewise-linear function of the weights:
/// CVaR(w) = max { Σₜ qₜ Lₜ(w) : 0 ≤ qₜ ≤ 1/((1−β)T), Σ qₜ = 1 } with losses Lₜ = −rₜᵀw, and the maximising q
/// (the worst tail scenarios) gives an exact subgradient. Problems are solved by Kelley's cutting-plane method:
/// a small LP over (w, z) collects the linear pieces z ≥ Σ qₜ Lₜ(w) met so far, which terminates finitely at the
/// exact optimum because the function has finitely many pieces. Each master LP has only N + 2 variables, so the
/// cost scales with the number of assets, not the number of scenarios (each cut costs one O(T log T) sort).
/// Returns and CVaR are per-period (the scenario frequency), not annualised.
/// </remarks>
public sealed class CVaROptimizer
{
    private readonly Matrix _scenarios;
    private readonly double[] _mean;
    private readonly double _bound;

    /// <param name="scenarioReturns">T x N matrix: one row per scenario (e.g. historical day), one column per asset.</param>
    /// <param name="beta">Confidence level, e.g. 0.95.</param>
    /// <param name="lowerBound">Minimum weight per asset.</param>
    /// <param name="upperBound">Maximum weight per asset.</param>
    /// <param name="assets">Optional asset names.</param>
    public CVaROptimizer(Matrix scenarioReturns, double beta = 0.95, double lowerBound = 0, double upperBound = 1, IReadOnlyList<string>? assets = null)
    {
        Finance.Core.Guard.InRange(beta, 0.5, 0.9999);
        if (scenarioReturns.Rows < 10) throw new ArgumentException("Need at least ten scenarios.");
        int n = scenarioReturns.Columns;
        if (lowerBound * n > 1 + 1e-12 || upperBound * n < 1 - 1e-12) throw new ArgumentException("Bounds cannot sum to one.");
        _scenarios = scenarioReturns;
        Beta = beta;
        LowerBound = lowerBound;
        UpperBound = upperBound;
        Assets = assets ?? Enumerable.Range(0, n).Select(i => $"Asset{i + 1}").ToArray();
        _mean = Stats.ColumnMeans(scenarioReturns);
        double maxAbs = 0;
        foreach (var v in scenarioReturns.Data) maxAbs = Math.Max(maxAbs, Math.Abs(v));
        double gross = Math.Max(Math.Abs(lowerBound), Math.Abs(upperBound)) * n;
        _bound = maxAbs * Math.Max(gross, 1) + 1;
    }

    public double Beta { get; }
    public double LowerBound { get; }
    public double UpperBound { get; }
    public IReadOnlyList<string> Assets { get; }

    /// <summary>Iterations used by the last solve (for diagnostics).</summary>
    public int LastIterations { get; private set; }

    /// <summary>Per-period mean scenario return of each asset.</summary>
    public ReadOnlySpan<double> MeanReturns => _mean;

    /// <summary>Portfolio with the lowest CVaR.</summary>
    public PortfolioWeights MinCVaR() => Wrap(Solve(minimiseRisk: true, targetReturn: null, targetCVaR: null));

    /// <summary>Lowest-CVaR portfolio whose mean scenario return is at least <paramref name="targetReturn"/>.</summary>
    public PortfolioWeights EfficientReturn(double targetReturn)
    {
        double max = MaxAttainableReturn();
        if (targetReturn > max + 1e-12) throw new ArgumentException($"Target return {targetReturn} exceeds the maximum attainable {max}.");
        return Wrap(Solve(minimiseRisk: true, targetReturn: targetReturn, targetCVaR: null));
    }

    /// <summary>Highest-return portfolio whose CVaR does not exceed <paramref name="targetCVaR"/>.</summary>
    public PortfolioWeights EfficientRisk(double targetCVaR)
    {
        double minCvar = CVaR(Solve(minimiseRisk: true, targetReturn: null, targetCVaR: null));
        if (targetCVaR < minCvar - 1e-12) throw new ArgumentException($"Target CVaR {targetCVaR} is below the minimum attainable {minCvar}.");
        return Wrap(Solve(minimiseRisk: false, targetReturn: null, targetCVaR: Math.Max(targetCVaR, minCvar)));
    }

    /// <summary>Samples the mean-CVaR frontier from the minimum-CVaR portfolio up to the maximum return.</summary>
    public IReadOnlyList<(double ExpectedReturn, double CVaR, PortfolioWeights Weights)> EfficientFrontier(int points = 20)
    {
        var min = Solve(true, null, null);
        double r0 = ExpectedReturn(min), r1 = MaxAttainableReturn();
        var list = new List<(double, double, PortfolioWeights)>(points);
        for (int i = 0; i < points; i++)
        {
            double target = r0 + (r1 - r0) * i / Math.Max(1, points - 1);
            var w = i == 0 ? min : Solve(true, target, null);
            list.Add((ExpectedReturn(w), CVaR(w), Wrap(w)));
        }
        return list;
    }

    /// <summary>Mean scenario return of a portfolio.</summary>
    public double ExpectedReturn(ReadOnlySpan<double> weights) => Matrix.Dot(weights, _mean);

    /// <summary>Exact historical CVaR (expected loss beyond VaR, positive = loss) of a portfolio.</summary>
    public double CVaR(ReadOnlySpan<double> weights) => Evaluate(weights, null).CVaR;

    /// <summary>Exact historical VaR (positive = loss) of a portfolio.</summary>
    public double VaR(ReadOnlySpan<double> weights) => Evaluate(weights, null).VaR;

    /// <summary>
    /// VaR, CVaR and (optionally) a CVaR subgradient. The tail weights put 1/((1−β)T) on the largest losses and the
    /// remaining mass on the VaR scenario, which matches the Rockafellar-Uryasev definition exactly.
    /// </summary>
    private (double VaR, double CVaR) Evaluate(ReadOnlySpan<double> w, double[]? gradient)
    {
        int t = _scenarios.Rows;
        var losses = new double[t];
        var idx = new int[t];
        for (int i = 0; i < t; i++)
        {
            losses[i] = -Matrix.Dot(_scenarios.Row(i), w);
            idx[i] = i;
        }
        Array.Sort(losses, idx);
        double cap = 1.0 / ((1 - Beta) * t);
        int k = Math.Clamp((int)Math.Ceiling(Beta * t) - 1, 0, t - 1);
        double var = losses[k];
        double cvar = 0, mass = 0;
        if (gradient is not null) Array.Clear(gradient);
        for (int i = t - 1; i > k; i--)
        {
            cvar += cap * losses[i];
            mass += cap;
            if (gradient is not null) Matrix.Axpy(-cap, _scenarios.Row(idx[i]), gradient);
        }
        double rest = 1 - mass;
        cvar += rest * var;
        if (gradient is not null) Matrix.Axpy(-rest, _scenarios.Row(idx[k]), gradient);
        return (var, cvar);
    }

    private double MaxAttainableReturn()
    {
        int n = _mean.Length;
        var w = Enumerable.Repeat(LowerBound, n).ToArray();
        double remaining = 1 - n * LowerBound;
        foreach (int i in Enumerable.Range(0, n).OrderByDescending(i => _mean[i]))
        {
            double add = Math.Min(UpperBound - LowerBound, remaining);
            w[i] += add;
            remaining -= add;
        }
        return Matrix.Dot(w, _mean);
    }

    /// <summary>
    /// Kelley cutting planes. Variables of the master LP: w' = w − lower (n), z⁺, z⁻ with z = z⁺ − z⁻ ≥ −bound.
    /// </summary>
    private double[] Solve(bool minimiseRisk, double? targetReturn, double? targetCVaR, int maxIterations = 1000)
    {
        int n = _mean.Length, vars = n + 2;
        double lo = LowerBound, hi = UpperBound;
        var lp = new LinearProgram(vars);

        var sum = new double[vars];
        for (int i = 0; i < n; i++) sum[i] = 1;
        lp.Add(sum, ConstraintKind.Equal, 1 - n * lo);
        if (hi - lo < 1 - n * lo)
            for (int i = 0; i < n; i++)
            {
                var row = new double[vars];
                row[i] = 1;
                lp.Add(row, ConstraintKind.LessOrEqual, hi - lo);
            }
        var zneg = new double[vars];
        zneg[n + 1] = 1;
        lp.Add(zneg, ConstraintKind.LessOrEqual, _bound);

        double meanAtLower = lo * _mean.Sum();
        if (targetReturn is { } tr)
        {
            var row = new double[vars];
            for (int i = 0; i < n; i++) row[i] = _mean[i];
            lp.Add(row, ConstraintKind.GreaterOrEqual, tr - meanAtLower);
        }
        if (targetCVaR is { } tc)
        {
            var row = new double[vars];
            row[n] = 1;
            row[n + 1] = -1;
            lp.Add(row, ConstraintKind.LessOrEqual, tc);
        }

        var obj = new double[vars];
        if (minimiseRisk) { obj[n] = 1; obj[n + 1] = -1; }
        else for (int i = 0; i < n; i++) obj[i] = -_mean[i];
        lp.Objective = obj;

        // Seed with a cut at a feasible point.
        var w = Enumerable.Repeat(1.0 / n, n).ToArray();
        Optimizer.ProjectOntoSimplex(w, 1, lo, hi);
        var g = new double[n];
        double[]? best = null;
        double bestValue = double.PositiveInfinity;
        AddCut(lp, w, g, n, vars, lo);

        for (int it = 1; it <= maxIterations; it++)
        {
            LastIterations = it;
            var (status, x, _) = lp.Solve();
            if (status != LpStatus.Optimal)
            {
                if (status == LpStatus.Infeasible) throw new ArgumentException("The CVaR problem is infeasible for the given targets.");
                break;
            }
            for (int i = 0; i < n; i++) w[i] = x[i] + lo;
            double zModel = x[n] - x[n + 1];
            double cvar = Evaluate(w, g).CVaR;

            if (minimiseRisk)
            {
                if (cvar < bestValue) { bestValue = cvar; best = (double[])w.Clone(); }
                if (cvar - zModel <= 1e-12 + 1e-10 * Math.Abs(cvar)) break;
            }
            else
            {
                // Outer approximation: once the master's optimum is truly feasible it is optimal.
                if (cvar <= targetCVaR!.Value + 1e-12 + 1e-10 * Math.Abs(cvar)) { best = (double[])w.Clone(); break; }
            }
            AddCut(lp, w, g, n, vars, lo, cvar);
        }
        return best ?? w;
    }

    /// <summary>Adds z ≥ cvar + gᵀ(w − w₀), written as gᵀw' − z⁺ + z⁻ ≤ gᵀw₀ − cvar − lo·Σg.</summary>
    private void AddCut(LinearProgram lp, double[] w0, double[] g, int n, int vars, double lo, double? knownCvar = null)
    {
        double cvar = knownCvar ?? Evaluate(w0, g).CVaR;
        var row = new double[vars];
        double rhs = -cvar, gSum = 0;
        for (int i = 0; i < n; i++)
        {
            row[i] = g[i];
            rhs += g[i] * w0[i];
            gSum += g[i];
        }
        row[n] = -1;
        row[n + 1] = 1;
        lp.Add(row, ConstraintKind.LessOrEqual, rhs - lo * gSum);
    }

    private PortfolioWeights Wrap(double[] w) => new(w, Assets);
}
