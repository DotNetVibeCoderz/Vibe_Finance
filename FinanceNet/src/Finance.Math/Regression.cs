namespace Finance.Mathematics;

/// <summary>Ordinary least squares fit with inference statistics.</summary>
public sealed record RegressionResult(
    double[] Coefficients,
    double[] StandardErrors,
    double[] TStatistics,
    double RSquared,
    double AdjustedRSquared,
    double ResidualStdError,
    double[] Residuals,
    bool HasIntercept)
{
    /// <summary>Intercept (alpha) when the model was fitted with one, otherwise 0.</summary>
    public double Intercept => HasIntercept ? Coefficients[0] : 0;

    /// <summary>Slopes (betas), excluding the intercept.</summary>
    public ReadOnlySpan<double> Slopes => HasIntercept ? Coefficients.AsSpan(1) : Coefficients;

    /// <summary>Predicts y for one row of regressors (without the intercept column).</summary>
    public double Predict(ReadOnlySpan<double> x)
    {
        var slopes = Slopes;
        double y = Intercept;
        for (int i = 0; i < slopes.Length; i++) y += slopes[i] * x[i];
        return y;
    }
}

/// <summary>Linear regression (factor models, betas, hedge ratios).</summary>
public static class Regression
{
    /// <summary>
    /// OLS of <paramref name="y"/> on the columns of <paramref name="x"/> via the normal equations
    /// with a Cholesky solve (falls back to LU when XᵀX is ill-conditioned).
    /// </summary>
    public static RegressionResult Ols(Matrix x, ReadOnlySpan<double> y, bool intercept = true)
    {
        int n = x.Rows, k = x.Columns + (intercept ? 1 : 0);
        if (y.Length != n) throw new ArgumentException("y length must match the number of rows.");
        if (n <= k) throw new ArgumentException("Need more observations than parameters.");

        var design = new Matrix(n, k);
        for (int i = 0; i < n; i++)
        {
            int c = 0;
            if (intercept) design[i, c++] = 1;
            for (int j = 0; j < x.Columns; j++) design[i, c++] = x[i, j];
        }

        var xtx = new Matrix(k, k);
        var xty = new double[k];
        for (int i = 0; i < n; i++)
        {
            var row = design.Row(i);
            for (int a = 0; a < k; a++)
            {
                xty[a] += row[a] * y[i];
                for (int b = a; b < k; b++) xtx[a, b] += row[a] * row[b];
            }
        }
        for (int a = 0; a < k; a++)
            for (int b = 0; b < a; b++) xtx[a, b] = xtx[b, a];

        var inv = xtx.Inverse();
        var beta = inv.Multiply(xty);

        var residuals = new double[n];
        double ssr = 0, mean = Stats.Mean(y), sst = 0;
        for (int i = 0; i < n; i++)
        {
            double fit = Matrix.Dot(design.Row(i), beta);
            residuals[i] = y[i] - fit;
            ssr += residuals[i] * residuals[i];
            double d = y[i] - mean;
            sst += d * d;
        }
        int dof = n - k;
        double sigma2 = ssr / dof;
        var se = new double[k];
        var t = new double[k];
        for (int a = 0; a < k; a++)
        {
            se[a] = Math.Sqrt(Math.Max(0, sigma2 * inv[a, a]));
            t[a] = se[a] > 0 ? beta[a] / se[a] : double.NaN;
        }
        double r2 = sst > 0 ? 1 - ssr / sst : 0;
        int p = k - (intercept ? 1 : 0);
        double adj = 1 - (1 - r2) * (n - 1) / Math.Max(1, n - p - 1);
        return new RegressionResult(beta, se, t, r2, adj, Math.Sqrt(sigma2), residuals, intercept);
    }

    /// <summary>Simple regression y = α + β x.</summary>
    public static RegressionResult Simple(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        var m = new Matrix(x.Length, 1);
        for (int i = 0; i < x.Length; i++) m[i, 0] = x[i];
        return Ols(m, y);
    }

    /// <summary>Ridge regression (no intercept penalty) solving (XᵀX + λI) β = Xᵀy.</summary>
    public static double[] Ridge(Matrix x, ReadOnlySpan<double> y, double lambda, bool intercept = true)
    {
        int n = x.Rows, p = x.Columns;
        var xm = new double[p];
        double ym = intercept ? Stats.Mean(y) : 0;
        if (intercept)
            for (int j = 0; j < p; j++) xm[j] = Stats.Mean(x.Column(j));
        var xtx = new Matrix(p, p);
        var xty = new double[p];
        for (int i = 0; i < n; i++)
            for (int a = 0; a < p; a++)
            {
                double xa = x[i, a] - xm[a];
                xty[a] += xa * (y[i] - ym);
                for (int b = 0; b < p; b++) xtx[a, b] += xa * (x[i, b] - xm[b]);
            }
        for (int a = 0; a < p; a++) xtx[a, a] += lambda;
        var beta = xtx.Solve(xty);
        if (!intercept) return beta;
        double b0 = ym;
        for (int j = 0; j < p; j++) b0 -= beta[j] * xm[j];
        return new[] { b0 }.Concat(beta).ToArray();
    }
}
