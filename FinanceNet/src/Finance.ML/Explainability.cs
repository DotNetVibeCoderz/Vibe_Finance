using Finance.Core;
using Finance.Mathematics;

namespace Finance.ML;

/// <summary>SHAP values for one prediction.</summary>
public sealed record ShapExplanation(double BaseValue, double[] Values, double Prediction, string[]? FeatureNames)
{
    /// <summary>Features ordered by |SHAP| descending.</summary>
    public IEnumerable<(string Name, double Value)> Ranked() =>
        Values.Select((v, i) => (FeatureNames?[i] ?? $"x{i}", v)).OrderByDescending(t => Math.Abs(t.v)).Select(t => (t.Item1, t.v));
}

/// <summary>A partial dependence curve with optional ICE lines.</summary>
public sealed record PartialDependence(double[] Grid, double[] Average, double[][] Individual);

/// <summary>
/// Kernel SHAP (Lundberg &amp; Lee, 2017): model-agnostic Shapley value estimates from a weighted
/// linear regression over feature coalitions, with absent features replaced by background rows.
/// Exact enumeration is used up to 12 features; above that, coalitions are sampled from the Shapley kernel.
/// </summary>
public sealed class KernelShap
{
    private readonly Func<double[], double> _model;
    private readonly double[][] _background;
    private readonly double _baseValue;

    public KernelShap(IRegressor model, Matrix background, int maxBackground = 50, ulong seed = 11)
        : this(row => model.Predict(row), background, maxBackground, seed) { }

    public KernelShap(Func<double[], double> model, Matrix background, int maxBackground = 50, ulong seed = 11)
    {
        _model = model;
        var rng = new FastRandom(seed);
        var rows = Enumerable.Range(0, background.Rows).ToArray();
        if (rows.Length > maxBackground)
        {
            for (int i = 0; i < maxBackground; i++) { int j = i + rng.NextInt(rows.Length - i); (rows[i], rows[j]) = (rows[j], rows[i]); }
            rows = rows[..maxBackground];
        }
        _background = rows.Select(r => background.Row(r).ToArray()).ToArray();
        _baseValue = _background.Average(_model);
        Seed = seed;
    }

    public ulong Seed { get; }
    public double BaseValue => _baseValue;

    /// <summary>Explains one row. SHAP values sum to prediction − base value exactly (enforced constraint).</summary>
    public ShapExplanation Explain(ReadOnlySpan<double> row, string[]? featureNames = null, int samples = 2048)
    {
        int m = row.Length;
        var x = row.ToArray();
        double fx = _model(x);
        var coalitions = new List<(bool[] Mask, double Weight)>();
        if (m <= 12)
        {
            for (int s = 1; s < (1 << m) - 1; s++)
            {
                var mask = new bool[m];
                int size = 0;
                for (int j = 0; j < m; j++) if ((s >> j & 1) == 1) { mask[j] = true; size++; }
                coalitions.Add((mask, ShapleyKernel(m, size)));
            }
        }
        else
        {
            var rng = new FastRandom(Seed);
            var sizeWeights = Enumerable.Range(1, m - 1).Select(k => (m - 1.0) / (k * (double)(m - k))).ToArray();
            double total = sizeWeights.Sum();
            for (int s = 0; s < samples; s++)
            {
                double u = rng.NextDouble() * total, acc = 0;
                int size = 1;
                for (int q = 0; q < sizeWeights.Length; q++) { acc += sizeWeights[q]; if (u <= acc) { size = q + 1; break; } }
                var perm = Enumerable.Range(0, m).ToArray();
                for (int i = 0; i < size; i++) { int j = i + rng.NextInt(m - i); (perm[i], perm[j]) = (perm[j], perm[i]); }
                var mask = new bool[m];
                for (int i = 0; i < size; i++) mask[perm[i]] = true;
                coalitions.Add((mask, 1.0)); // sampled from the kernel, so equal weights
            }
        }

        // Expected model output for each coalition over the background.
        var ev = new double[coalitions.Count];
        Parallel.For(0, coalitions.Count, c =>
        {
            var mask = coalitions[c].Mask;
            var z = new double[m];
            double s = 0;
            foreach (var b in _background)
            {
                for (int j = 0; j < m; j++) z[j] = mask[j] ? x[j] : b[j];
                s += _model(z);
            }
            ev[c] = s / _background.Length;
        });

        // Weighted least squares with the efficiency constraint Σφ = f(x) − φ0, eliminating the last feature.
        double delta = fx - _baseValue;
        int k = m - 1;
        var phi = new double[m];
        if (m == 1) { phi[0] = delta; return new ShapExplanation(_baseValue, phi, fx, featureNames); }
        var a = new Matrix(k, k);
        var rhs = new double[k];
        for (int c = 0; c < coalitions.Count; c++)
        {
            var (mask, w) = coalitions[c];
            double last = mask[m - 1] ? 1 : 0;
            double target = ev[c] - _baseValue - last * delta;
            for (int i = 0; i < k; i++)
            {
                double zi = (mask[i] ? 1 : 0) - last;
                rhs[i] += w * zi * target;
                for (int j = 0; j < k; j++) a[i, j] += w * zi * ((mask[j] ? 1 : 0) - last);
            }
        }
        for (int i = 0; i < k; i++) a[i, i] += 1e-10;
        var sol = a.Solve(rhs);
        double sum = 0;
        for (int i = 0; i < k; i++) { phi[i] = sol[i]; sum += sol[i]; }
        phi[m - 1] = delta - sum;
        return new ShapExplanation(_baseValue, phi, fx, featureNames);
    }

    private static double ShapleyKernel(int m, int s)
    {
        double logBinom = SpecialFunctions.LogGamma(m + 1) - SpecialFunctions.LogGamma(s + 1) - SpecialFunctions.LogGamma(m - s + 1);
        return (m - 1) / (Math.Exp(logBinom) * s * (m - s));
    }
}

/// <summary>Model-agnostic explanation tools.</summary>
public static class Explain
{
    /// <summary>
    /// Permutation importance: increase in loss when a feature column is shuffled, averaged over
    /// <paramref name="repeats"/> shuffles. Uses MSE unless a loss is given.
    /// </summary>
    public static double[] PermutationImportance(IRegressor model, Matrix x, double[] y, int repeats = 5, Func<double[], double[], double>? loss = null, ulong seed = 5)
    {
        loss ??= (a, b) => Metrics.MeanSquaredError(a, b);
        double baseLoss = loss(y, model.PredictAll(x));
        var rng = new FastRandom(seed);
        var imp = new double[x.Columns];
        for (int j = 0; j < x.Columns; j++)
        {
            double acc = 0;
            for (int r = 0; r < repeats; r++)
            {
                var shuffled = x.Clone();
                var col = x.Column(j);
                for (int i = col.Length - 1; i > 0; i--) { int k = rng.NextInt(i + 1); (col[i], col[k]) = (col[k], col[i]); }
                for (int i = 0; i < col.Length; i++) shuffled[i, j] = col[i];
                acc += loss(y, model.PredictAll(shuffled)) - baseLoss;
            }
            imp[j] = acc / repeats;
        }
        return imp;
    }

    /// <summary>
    /// Partial dependence of the prediction on feature <paramref name="feature"/> over a quantile grid,
    /// together with individual conditional expectation (ICE) curves for up to <paramref name="maxIce"/> rows.
    /// </summary>
    public static PartialDependence PartialDependence(IRegressor model, Matrix x, int feature, int gridPoints = 20, int maxIce = 50)
    {
        var col = x.Column(feature);
        Array.Sort(col);
        var grid = Enumerable.Range(0, gridPoints).Select(g => Stats.QuantileSorted(col, gridPoints == 1 ? 0.5 : (double)g / (gridPoints - 1))).Distinct().ToArray();
        var avg = new double[grid.Length];
        int iceRows = Math.Min(maxIce, x.Rows);
        var ice = new double[iceRows][];
        for (int i = 0; i < iceRows; i++) ice[i] = new double[grid.Length];
        var row = new double[x.Columns];
        for (int i = 0; i < x.Rows; i++)
        {
            x.Row(i).CopyTo(row);
            for (int g = 0; g < grid.Length; g++)
            {
                row[feature] = grid[g];
                double p = model.Predict(row);
                avg[g] += p;
                if (i < iceRows) ice[i][g] = p;
            }
        }
        for (int g = 0; g < grid.Length; g++) avg[g] /= x.Rows;
        return new PartialDependence(grid, avg, ice);
    }

    /// <summary>
    /// Exact attribution for a linear model: contribution_j = β_j (x_j − mean_j), which equals the
    /// SHAP value of a linear model with independent features.
    /// </summary>
    public static double[] LinearAttribution(ReadOnlySpan<double> coefficients, ReadOnlySpan<double> row, ReadOnlySpan<double> featureMeans)
    {
        var c = new double[row.Length];
        for (int j = 0; j < row.Length; j++) c[j] = coefficients[j] * (row[j] - featureMeans[j]);
        return c;
    }

    /// <summary>
    /// Factor risk attribution: splits portfolio variance into factor contributions
    /// b_k (Σ_f b)_k and the specific (residual) variance. Contributions sum to total variance.
    /// </summary>
    /// <param name="exposures">Portfolio factor exposures b (one per factor).</param>
    /// <param name="factorCovariance">Factor covariance matrix Σ_f.</param>
    /// <param name="specificVariance">Residual variance of the portfolio.</param>
    public static (double[] FactorContributions, double Specific, double Total) FactorRiskAttribution(ReadOnlySpan<double> exposures, Matrix factorCovariance, double specificVariance = 0)
    {
        var sb = factorCovariance.Multiply(exposures);
        var contrib = new double[exposures.Length];
        double total = specificVariance;
        for (int k = 0; k < exposures.Length; k++) { contrib[k] = exposures[k] * sb[k]; total += contrib[k]; }
        return (contrib, specificVariance, total);
    }
}
