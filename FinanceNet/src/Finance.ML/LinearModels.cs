using Finance.Mathematics;

namespace Finance.ML;

/// <summary>Ordinary least squares regressor (wraps <see cref="Regression.Ols"/>).</summary>
public sealed class LinearRegressionModel : IRegressor
{
    /// <summary>Fitted statistics (coefficients, t-stats, R²).</summary>
    public RegressionResult? Result { get; private set; }

    public void Fit(Matrix x, ReadOnlySpan<double> y) => Result = Regression.Ols(x, y);

    public double Predict(ReadOnlySpan<double> row) =>
        (Result ?? throw new InvalidOperationException("Model is not fitted.")).Predict(row);
}

/// <summary>Ridge (L2-penalised) regressor.</summary>
public sealed class RidgeRegressionModel(double lambda = 1.0) : IRegressor
{
    /// <summary>Coefficients with the intercept first.</summary>
    public double[] Coefficients { get; private set; } = Array.Empty<double>();

    public double Lambda { get; } = lambda;

    public void Fit(Matrix x, ReadOnlySpan<double> y) => Coefficients = Regression.Ridge(x, y, Lambda);

    public double Predict(ReadOnlySpan<double> row)
    {
        if (Coefficients.Length == 0) throw new InvalidOperationException("Model is not fitted.");
        double s = Coefficients[0];
        for (int j = 0; j < row.Length; j++) s += Coefficients[j + 1] * row[j];
        return s;
    }
}

/// <summary>
/// L2-regularised logistic regression fitted by Newton-Raphson (IRLS) — converges in a handful of
/// iterations for the feature counts typical in finance.
/// </summary>
public sealed class LogisticRegressionModel(double l2 = 1e-4, int maxIterations = 50) : IClassifier
{
    /// <summary>Coefficients with the intercept first.</summary>
    public double[] Coefficients { get; private set; } = Array.Empty<double>();

    public void Fit(Matrix x, ReadOnlySpan<double> y)
    {
        int n = x.Rows, k = x.Columns + 1;
        var w = new double[k];
        var row = new double[k];
        for (int it = 0; it < maxIterations; it++)
        {
            var h = new Matrix(k, k);
            var g = new double[k];
            for (int i = 0; i < n; i++)
            {
                row[0] = 1;
                x.Row(i).CopyTo(row.AsSpan(1));
                double p = Sigmoid(Matrix.Dot(row, w));
                double r = p - y[i], s = p * (1 - p);
                for (int a = 0; a < k; a++)
                {
                    g[a] += r * row[a];
                    for (int b = a; b < k; b++) h[a, b] += s * row[a] * row[b];
                }
            }
            for (int a = 0; a < k; a++)
            {
                for (int b = 0; b < a; b++) h[a, b] = h[b, a];
                h[a, a] += l2 * n + 1e-10;
                g[a] += l2 * n * (a == 0 ? 0 : w[a]);
            }
            var step = h.Solve(g);
            double change = 0;
            for (int a = 0; a < k; a++) { w[a] -= step[a]; change = Math.Max(change, Math.Abs(step[a])); }
            if (change < 1e-9) break;
        }
        Coefficients = w;
    }

    public double PredictProba(ReadOnlySpan<double> row)
    {
        if (Coefficients.Length == 0) throw new InvalidOperationException("Model is not fitted.");
        double z = Coefficients[0];
        for (int j = 0; j < row.Length; j++) z += Coefficients[j + 1] * row[j];
        return Sigmoid(z);
    }

    internal static double Sigmoid(double z) => z >= 0 ? 1 / (1 + Math.Exp(-z)) : Math.Exp(z) / (1 + Math.Exp(z));
}
