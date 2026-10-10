using Finance.Mathematics;

namespace Finance.Portfolio;

/// <summary>
/// Expected-return estimators. Inputs are matrices with one row per period and one column per asset.
/// Outputs are annualised.
/// </summary>
public static class ExpectedReturns
{
    /// <summary>Simple (or log) period returns from a price matrix; the result has one row fewer.</summary>
    public static Matrix ReturnsFromPrices(Matrix prices, bool logReturns = false)
    {
        if (prices.Rows < 2) throw new ArgumentException("Need at least two price rows.");
        var r = new Matrix(prices.Rows - 1, prices.Columns);
        for (int t = 1; t < prices.Rows; t++)
            for (int j = 0; j < prices.Columns; j++)
            {
                double ratio = prices[t, j] / prices[t - 1, j];
                r[t - 1, j] = logReturns ? Math.Log(ratio) : ratio - 1;
            }
        return r;
    }

    /// <summary>
    /// Annualised mean historical return. With <paramref name="compounding"/> the geometric mean is used:
    /// (Π(1 + r))^(periods/n) − 1; otherwise the arithmetic mean times the periods per year.
    /// </summary>
    public static double[] MeanHistorical(Matrix returns, double periodsPerYear = 252, bool compounding = true)
    {
        int n = returns.Rows, k = returns.Columns;
        var mu = new double[k];
        for (int j = 0; j < k; j++)
        {
            if (compounding)
            {
                double logSum = 0;
                for (int t = 0; t < n; t++) logSum += double.LogP1(returns[t, j]);
                mu[j] = Math.Exp(logSum * periodsPerYear / n) - 1;
            }
            else
            {
                double s = 0;
                for (int t = 0; t < n; t++) s += returns[t, j];
                mu[j] = s / n * periodsPerYear;
            }
        }
        return mu;
    }

    /// <summary>
    /// Exponentially weighted mean return (pandas <c>ewm(span).mean()</c> with adjust=True, last value), annualised.
    /// </summary>
    public static double[] EmaHistorical(Matrix returns, int span = 500, double periodsPerYear = 252, bool compounding = true)
    {
        Finance.Core.Guard.Positive(span);
        int n = returns.Rows, k = returns.Columns;
        double alpha = 2.0 / (span + 1), decay = 1 - alpha;
        var weights = new double[n];
        double w = 1, total = 0;
        for (int t = n - 1; t >= 0; t--)
        {
            weights[t] = w;
            total += w;
            w *= decay;
        }
        var mu = new double[k];
        for (int j = 0; j < k; j++)
        {
            double s = 0;
            for (int t = 0; t < n; t++) s += weights[t] * returns[t, j];
            double m = s / total;
            mu[j] = compounding ? Math.Pow(1 + m, periodsPerYear) - 1 : m * periodsPerYear;
        }
        return mu;
    }

    /// <summary>
    /// CAPM expected returns: rf + β (E[R_m] − rf). When <paramref name="marketReturns"/> is null an
    /// equal-weighted market of the given assets is used.
    /// </summary>
    public static double[] Capm(Matrix returns, double[]? marketReturns = null, double riskFreeRate = 0, double periodsPerYear = 252, bool compounding = true)
    {
        int n = returns.Rows, k = returns.Columns;
        var market = marketReturns;
        if (market is null)
        {
            market = new double[n];
            for (int t = 0; t < n; t++)
            {
                double s = 0;
                for (int j = 0; j < k; j++) s += returns[t, j];
                market[t] = s / k;
            }
        }
        if (market.Length != n) throw new ArgumentException("Market returns must have one value per row.");

        double varM = Stats.Variance(market);
        var mm = new Matrix(n, 1, (double[])market.Clone());
        double marketMean = MeanHistorical(mm, periodsPerYear, compounding)[0];
        var mu = new double[k];
        for (int j = 0; j < k; j++)
        {
            double beta = Stats.Covariance(returns.Column(j), market) / varM;
            mu[j] = riskFreeRate + beta * (marketMean - riskFreeRate);
        }
        return mu;
    }
}

/// <summary>Covariance estimators (annualised).</summary>
public static class RiskModels
{
    /// <summary>Annualised sample covariance of period returns.</summary>
    public static Matrix SampleCovariance(Matrix returns, double periodsPerYear = 252)
        => periodsPerYear * Stats.CovarianceMatrix(returns);

    /// <summary>Annualised Ledoit-Wolf shrinkage covariance (towards a scaled identity).</summary>
    public static Matrix LedoitWolf(Matrix returns, double periodsPerYear = 252)
        => periodsPerYear * Stats.LedoitWolf(returns).Covariance;

    /// <summary>
    /// Annualised exponentially weighted covariance: the exponentially weighted mean (span
    /// <paramref name="span"/>, newest observation weighted most) of the products of de-meaned returns.
    /// </summary>
    public static Matrix ExponentialCovariance(Matrix returns, int span = 180, double periodsPerYear = 252)
    {
        Finance.Core.Guard.Positive(span);
        int n = returns.Rows, k = returns.Columns;
        double alpha = 2.0 / (span + 1), decay = 1 - alpha;
        var weights = new double[n];
        double w = 1, total = 0;
        for (int t = n - 1; t >= 0; t--)
        {
            weights[t] = w;
            total += w;
            w *= decay;
        }
        var means = Stats.ColumnMeans(returns);
        var cov = new Matrix(k, k);
        var d = new double[k];
        for (int t = 0; t < n; t++)
        {
            for (int j = 0; j < k; j++) d[j] = returns[t, j] - means[j];
            double wt = weights[t] / total;
            for (int a = 0; a < k; a++)
                for (int b = a; b < k; b++) cov[a, b] += wt * d[a] * d[b];
        }
        for (int a = 0; a < k; a++)
            for (int b = a; b < k; b++)
            {
                cov[a, b] *= periodsPerYear;
                cov[b, a] = cov[a, b];
            }
        return cov;
    }

    /// <summary>
    /// Repairs a covariance matrix that is not positive semi-definite by clipping negative eigenvalues to zero.
    /// </summary>
    public static Matrix FixNonPositiveSemidefinite(Matrix covariance)
    {
        var (values, vectors) = covariance.SymmetricEigen();
        if (values.All(v => v >= 0)) return covariance.Clone();
        int n = values.Length;
        var fixedCov = new Matrix(n, n);
        for (int e = 0; e < n; e++)
        {
            double lambda = Math.Max(values[e], 0);
            if (lambda == 0) continue;
            for (int a = 0; a < n; a++)
                for (int b = 0; b < n; b++) fixedCov[a, b] += lambda * vectors[a, e] * vectors[b, e];
        }
        return fixedCov;
    }
}
