using Finance.Mathematics;

namespace Finance.ML;

/// <summary>Time-series factor regression result for one asset.</summary>
public sealed record FactorExposure(double Alpha, double AlphaTStat, double[] Betas, double[] BetaTStats, double RSquared, double ResidualVolatility, string[] FactorNames)
{
    /// <summary>Annualised alpha given the number of periods per year.</summary>
    public double AnnualizedAlpha(double periodsPerYear = 252) => Alpha * periodsPerYear;
}

/// <summary>Fama-MacBeth cross-sectional regression result.</summary>
public sealed record FamaMacBethResult(double[] MeanPremia, double[] StandardErrors, double[] TStatistics, double[][] PremiaByPeriod, string[] FactorNames);

/// <summary>PCA statistical factor model.</summary>
public sealed record PcaFactorModel(double[] Eigenvalues, Matrix Loadings, double[] ExplainedVarianceRatio, double[] Means, Matrix FactorReturns)
{
    /// <summary>Cumulative explained variance of the first k components.</summary>
    public double CumulativeExplained(int k) => ExplainedVarianceRatio.Take(k).Sum();
}

/// <summary>Factor models: Fama-French style regressions, Fama-MacBeth and PCA factors.</summary>
public static class FactorModels
{
    /// <summary>
    /// Regresses asset (excess) returns on factor returns: r_t = α + Σ β_k f_{k,t} + ε_t.
    /// </summary>
    /// <param name="assetReturns">Asset returns (length T).</param>
    /// <param name="factorReturns">Factor returns, rows = periods (T), columns = factors.</param>
    /// <param name="factorNames">Optional names (e.g. "MKT", "SMB", "HML").</param>
    public static FactorExposure TimeSeriesRegression(ReadOnlySpan<double> assetReturns, Matrix factorReturns, string[]? factorNames = null)
    {
        var r = Regression.Ols(factorReturns, assetReturns);
        int k = factorReturns.Columns;
        factorNames ??= Enumerable.Range(1, k).Select(i => $"F{i}").ToArray();
        return new FactorExposure(r.Coefficients[0], r.TStatistics[0], r.Coefficients[1..], r.TStatistics[1..], r.RSquared, r.ResidualStdError, factorNames);
    }

    /// <summary>
    /// Fama-MacBeth: for every period, regress the cross-section of returns on exposures; the
    /// premia are the time-series means of the per-period slopes, with t-stats from their dispersion.
    /// </summary>
    /// <param name="returns">returns[t][i] for asset i at period t.</param>
    /// <param name="exposures">exposures[t] is an (assets × factors) matrix known at the start of period t.</param>
    /// <param name="factorNames">Optional factor names for reporting.</param>
    public static FamaMacBethResult FamaMacBeth(IReadOnlyList<double[]> returns, IReadOnlyList<Matrix> exposures, string[]? factorNames = null)
    {
        if (returns.Count != exposures.Count) throw new ArgumentException("One exposure matrix per period.");
        int k = exposures[0].Columns + 1;
        var premia = new List<double[]>();
        for (int t = 0; t < returns.Count; t++)
        {
            if (exposures[t].Rows <= k) continue;
            premia.Add(Regression.Ols(exposures[t], returns[t]).Coefficients);
        }
        int T = premia.Count;
        var mean = new double[k];
        var se = new double[k];
        var tstat = new double[k];
        for (int j = 0; j < k; j++)
        {
            var s = premia.Select(p => p[j]).ToArray();
            mean[j] = Stats.Mean(s);
            se[j] = Stats.StdDev(s) / Math.Sqrt(T);
            tstat[j] = se[j] > 0 ? mean[j] / se[j] : double.NaN;
        }
        var names = new[] { "Intercept" }.Concat(factorNames ?? Enumerable.Range(1, k - 1).Select(i => $"F{i}")).ToArray();
        return new FamaMacBethResult(mean, se, tstat, premia.ToArray(), names);
    }

    /// <summary>
    /// PCA of the return covariance matrix. Loadings columns are eigenvectors (assets × components);
    /// factor returns are the centred returns projected onto them.
    /// </summary>
    /// <param name="returns">rows = periods, columns = assets.</param>
    /// <param name="components">Number of principal components to keep.</param>
    public static PcaFactorModel Pca(Matrix returns, int components)
    {
        var cov = Stats.CovarianceMatrix(returns);
        var (values, vectors) = cov.SymmetricEigen();
        int p = returns.Columns;
        components = Math.Min(components, p);
        double total = values.Where(v => v > 0).Sum();
        var loadings = new Matrix(p, components);
        for (int i = 0; i < p; i++)
            for (int c = 0; c < components; c++) loadings[i, c] = vectors[i, c];
        // Sign convention: make the largest-magnitude loading of each component positive.
        for (int c = 0; c < components; c++)
        {
            int arg = 0;
            for (int i = 1; i < p; i++) if (Math.Abs(loadings[i, c]) > Math.Abs(loadings[arg, c])) arg = i;
            if (loadings[arg, c] < 0) for (int i = 0; i < p; i++) loadings[i, c] = -loadings[i, c];
        }
        var means = Stats.ColumnMeans(returns);
        var fr = new Matrix(returns.Rows, components);
        for (int t = 0; t < returns.Rows; t++)
            for (int c = 0; c < components; c++)
            {
                double s = 0;
                for (int i = 0; i < p; i++) s += (returns[t, i] - means[i]) * loadings[i, c];
                fr[t, c] = s;
            }
        var ratio = values.Take(components).Select(v => total > 0 ? Math.Max(v, 0) / total : 0).ToArray();
        return new PcaFactorModel(values.Take(components).ToArray(), loadings, ratio, means, fr);
    }
}

/// <summary>Alphalens-style factor tear sheet.</summary>
public sealed record FactorTearSheet(
    double[] InformationCoefficient,
    double[] RankInformationCoefficient,
    double MeanIc,
    double IcStdDev,
    double IcInformationRatio,
    double MeanRankIc,
    double[] IcDecay,
    double[] QuantileMeanReturns,
    double LongShortSpread,
    double TopQuantileTurnover,
    double HitRate);

/// <summary>
/// Factor research in the spirit of Alphalens: information coefficients, IC decay, quantile
/// returns, long-short spread and turnover. Inputs are panels indexed [period][asset]; NaN marks missing data.
/// </summary>
public static class FactorResearch
{
    /// <summary>Forward simple returns over <paramref name="horizon"/> periods from a price panel.</summary>
    public static double[][] ForwardReturns(IReadOnlyList<double[]> prices, int horizon = 1)
    {
        int T = prices.Count;
        var r = new double[T][];
        for (int t = 0; t < T; t++)
        {
            int n = prices[t].Length;
            r[t] = new double[n];
            for (int i = 0; i < n; i++)
                r[t][i] = t + horizon < T ? prices[t + horizon][i] / prices[t][i] - 1 : double.NaN;
        }
        return r;
    }

    /// <summary>Pearson (or Spearman when <paramref name="rank"/>) IC per period, NaN when fewer than 3 valid assets.</summary>
    public static double[] InformationCoefficient(IReadOnlyList<double[]> factor, IReadOnlyList<double[]> forwardReturns, bool rank = true)
    {
        var ic = new double[factor.Count];
        for (int t = 0; t < factor.Count; t++)
        {
            var (f, r) = Valid(factor[t], forwardReturns[t]);
            ic[t] = f.Length < 3 ? double.NaN : rank ? Stats.SpearmanCorrelation(f, r) : Stats.Correlation(f, r);
        }
        return ic;
    }

    /// <summary>Mean rank IC at horizons 1..<paramref name="maxHorizon"/> (how fast the signal decays).</summary>
    public static double[] IcDecay(IReadOnlyList<double[]> factor, IReadOnlyList<double[]> prices, int maxHorizon = 10)
    {
        var decay = new double[maxHorizon];
        for (int h = 1; h <= maxHorizon; h++)
            decay[h - 1] = NanMean(InformationCoefficient(factor, ForwardReturns(prices, h)));
        return decay;
    }

    /// <summary>Quantile (1 = lowest factor) of each asset per period; 0 for missing.</summary>
    public static int[][] Quantiles(IReadOnlyList<double[]> factor, int quantiles = 5)
    {
        var q = new int[factor.Count][];
        for (int t = 0; t < factor.Count; t++)
        {
            var row = factor[t];
            q[t] = new int[row.Length];
            var idx = Enumerable.Range(0, row.Length).Where(i => !double.IsNaN(row[i])).OrderBy(i => row[i]).ToArray();
            for (int k = 0; k < idx.Length; k++) q[t][idx[k]] = 1 + k * quantiles / idx.Length;
        }
        return q;
    }

    /// <summary>Full tear sheet.</summary>
    public static FactorTearSheet Analyze(IReadOnlyList<double[]> factor, IReadOnlyList<double[]> prices, int quantiles = 5, int horizon = 1, int maxDecay = 10)
    {
        var fwd = ForwardReturns(prices, horizon);
        var ic = InformationCoefficient(factor, fwd, rank: false);
        var ric = InformationCoefficient(factor, fwd, rank: true);
        var validIc = ric.Where(v => !double.IsNaN(v)).ToArray();
        double meanIc = NanMean(ic), meanRic = Stats.Mean(validIc), sd = Stats.StdDev(validIc);

        var q = Quantiles(factor, quantiles);
        var sums = new double[quantiles];
        var counts = new int[quantiles];
        for (int t = 0; t < factor.Count; t++)
        {
            var qMeans = new double[quantiles];
            var qCounts = new int[quantiles];
            for (int i = 0; i < q[t].Length; i++)
            {
                if (q[t][i] == 0 || double.IsNaN(fwd[t][i])) continue;
                qMeans[q[t][i] - 1] += fwd[t][i];
                qCounts[q[t][i] - 1]++;
            }
            for (int k = 0; k < quantiles; k++)
                if (qCounts[k] > 0) { sums[k] += qMeans[k] / qCounts[k]; counts[k]++; }
        }
        var qret = sums.Select((s, k) => counts[k] > 0 ? s / counts[k] : double.NaN).ToArray();

        double turnover = 0;
        int tn = 0;
        for (int t = 1; t < q.Length; t++)
        {
            var prev = Enumerable.Range(0, q[t - 1].Length).Where(i => q[t - 1][i] == quantiles).ToHashSet();
            var cur = Enumerable.Range(0, q[t].Length).Where(i => q[t][i] == quantiles).ToHashSet();
            if (cur.Count == 0) continue;
            turnover += (double)cur.Count(i => !prev.Contains(i)) / cur.Count;
            tn++;
        }
        double hit = validIc.Length > 0 ? (double)validIc.Count(v => v > 0) / validIc.Length : double.NaN;
        return new FactorTearSheet(ic, ric, meanIc, sd, sd > 0 ? meanRic / sd : double.NaN, meanRic,
            IcDecay(factor, prices, maxDecay), qret, qret[^1] - qret[0], tn > 0 ? turnover / tn : 0, hit);
    }

    private static (double[] F, double[] R) Valid(double[] f, double[] r)
    {
        var a = new List<double>();
        var b = new List<double>();
        for (int i = 0; i < f.Length; i++)
            if (!double.IsNaN(f[i]) && !double.IsNaN(r[i])) { a.Add(f[i]); b.Add(r[i]); }
        return (a.ToArray(), b.ToArray());
    }

    private static double NanMean(double[] x)
    {
        var v = x.Where(d => !double.IsNaN(d)).ToArray();
        return v.Length > 0 ? Stats.Mean(v) : double.NaN;
    }
}
