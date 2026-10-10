using System.Numerics;
using System.Runtime.InteropServices;

namespace Finance.Mathematics;

/// <summary>
/// Descriptive statistics over spans. Sums are SIMD-vectorised; variance uses a two-pass
/// algorithm (mean first) because the one-pass formula loses precision on price-level data.
/// </summary>
public static class Stats
{
    /// <summary>Sum of the values (SIMD).</summary>
    public static double Sum(ReadOnlySpan<double> x)
    {
        double s = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && x.Length >= Vector<double>.Count * 2)
        {
            var acc = Vector<double>.Zero;
            var v = MemoryMarshal.Cast<double, Vector<double>>(x);
            for (int k = 0; k < v.Length; k++) acc += v[k];
            s = Vector.Sum(acc);
            i = v.Length * Vector<double>.Count;
        }
        for (; i < x.Length; i++) s += x[i];
        return s;
    }

    public static double Mean(ReadOnlySpan<double> x)
    {
        if (x.Length == 0) return double.NaN;
        return Sum(x) / x.Length;
    }

    /// <summary>Variance with <paramref name="ddof"/> delta degrees of freedom (1 = sample).</summary>
    public static double Variance(ReadOnlySpan<double> x, int ddof = 1)
    {
        int n = x.Length;
        if (n - ddof <= 0) return double.NaN;
        double m = Mean(x), s = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<double>.Count * 2)
        {
            var mv = new Vector<double>(m);
            var acc = Vector<double>.Zero;
            var v = MemoryMarshal.Cast<double, Vector<double>>(x);
            for (int k = 0; k < v.Length; k++)
            {
                var d = v[k] - mv;
                acc += d * d;
            }
            s = Vector.Sum(acc);
            i = v.Length * Vector<double>.Count;
        }
        for (; i < n; i++)
        {
            double d = x[i] - m;
            s += d * d;
        }
        return s / (n - ddof);
    }

    public static double StdDev(ReadOnlySpan<double> x, int ddof = 1) => Math.Sqrt(Variance(x, ddof));

    /// <summary>Sample skewness (Fisher-Pearson, bias-adjusted).</summary>
    public static double Skewness(ReadOnlySpan<double> x)
    {
        int n = x.Length;
        if (n < 3) return double.NaN;
        double m = Mean(x), m2 = 0, m3 = 0;
        foreach (var v in x)
        {
            double d = v - m;
            m2 += d * d;
            m3 += d * d * d;
        }
        m2 /= n;
        m3 /= n;
        if (m2 == 0) return 0;
        double g1 = m3 / Math.Pow(m2, 1.5);
        return Math.Sqrt(n * (n - 1.0)) / (n - 2.0) * g1;
    }

    /// <summary>Sample excess kurtosis (bias-adjusted; 0 for a normal distribution).</summary>
    public static double ExcessKurtosis(ReadOnlySpan<double> x)
    {
        int n = x.Length;
        if (n < 4) return double.NaN;
        double m = Mean(x), m2 = 0, m4 = 0;
        foreach (var v in x)
        {
            double d = v - m, d2 = d * d;
            m2 += d2;
            m4 += d2 * d2;
        }
        m2 /= n;
        m4 /= n;
        if (m2 == 0) return 0;
        double g2 = m4 / (m2 * m2) - 3.0;
        return (n - 1.0) / ((n - 2.0) * (n - 3.0)) * ((n + 1.0) * g2 + 6.0);
    }

    public static double Min(ReadOnlySpan<double> x)
    {
        double m = double.PositiveInfinity;
        foreach (var v in x) if (v < m) m = v;
        return m;
    }

    public static double Max(ReadOnlySpan<double> x)
    {
        double m = double.NegativeInfinity;
        foreach (var v in x) if (v > m) m = v;
        return m;
    }

    /// <summary>Sample covariance.</summary>
    public static double Covariance(ReadOnlySpan<double> x, ReadOnlySpan<double> y, int ddof = 1)
    {
        if (x.Length != y.Length) throw new ArgumentException("Length mismatch.");
        int n = x.Length;
        if (n - ddof <= 0) return double.NaN;
        double mx = Mean(x), my = Mean(y), s = 0;
        for (int i = 0; i < n; i++) s += (x[i] - mx) * (y[i] - my);
        return s / (n - ddof);
    }

    /// <summary>Pearson correlation.</summary>
    public static double Correlation(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        if (x.Length != y.Length) throw new ArgumentException("Length mismatch.");
        double mx = Mean(x), my = Mean(y), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }
        return sxx > 0 && syy > 0 ? sxy / Math.Sqrt(sxx * syy) : double.NaN;
    }

    /// <summary>Spearman rank correlation (average ranks for ties).</summary>
    public static double SpearmanCorrelation(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
        => Correlation(Rank(x), Rank(y));

    /// <summary>1-based ranks with ties averaged.</summary>
    public static double[] Rank(ReadOnlySpan<double> x)
    {
        int n = x.Length;
        var idx = new int[n];
        for (int i = 0; i < n; i++) idx[i] = i;
        var copy = x.ToArray();
        Array.Sort(idx, (a, b) => copy[a].CompareTo(copy[b]));
        var ranks = new double[n];
        int j = 0;
        while (j < n)
        {
            int k = j;
            while (k + 1 < n && copy[idx[k + 1]] == copy[idx[j]]) k++;
            double r = (j + k) / 2.0 + 1;
            for (int m = j; m <= k; m++) ranks[idx[m]] = r;
            j = k + 1;
        }
        return ranks;
    }

    /// <summary>
    /// Quantile with linear interpolation between order statistics (NumPy's default "linear" method).
    /// </summary>
    public static double Quantile(ReadOnlySpan<double> x, double p)
    {
        if (x.Length == 0) return double.NaN;
        var sorted = x.ToArray();
        Array.Sort(sorted);
        return QuantileSorted(sorted, p);
    }

    /// <summary>Quantile of already-sorted data.</summary>
    public static double QuantileSorted(ReadOnlySpan<double> sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        if (p <= 0) return sorted[0];
        if (p >= 1) return sorted[^1];
        double h = (sorted.Length - 1) * p;
        int lo = (int)Math.Floor(h);
        double frac = h - lo;
        return lo + 1 < sorted.Length ? sorted[lo] + frac * (sorted[lo + 1] - sorted[lo]) : sorted[lo];
    }

    public static double Median(ReadOnlySpan<double> x) => Quantile(x, 0.5);

    /// <summary>Z-scores (x - mean) / std.</summary>
    public static double[] ZScore(ReadOnlySpan<double> x)
    {
        double m = Mean(x), s = StdDev(x);
        var z = new double[x.Length];
        for (int i = 0; i < x.Length; i++) z[i] = s > 0 ? (x[i] - m) / s : 0;
        return z;
    }

    /// <summary>
    /// Sample covariance matrix of the columns of <paramref name="data"/> (rows = observations).
    /// </summary>
    public static Matrix CovarianceMatrix(Matrix data, int ddof = 1)
    {
        int n = data.Rows, k = data.Columns;
        if (n - ddof <= 0) throw new ArgumentException("Not enough observations.");
        var means = new double[k];
        for (int i = 0; i < n; i++)
        {
            var row = data.Row(i);
            for (int j = 0; j < k; j++) means[j] += row[j];
        }
        for (int j = 0; j < k; j++) means[j] /= n;

        var cov = new Matrix(k, k);
        var centered = new double[k];
        for (int i = 0; i < n; i++)
        {
            var row = data.Row(i);
            for (int j = 0; j < k; j++) centered[j] = row[j] - means[j];
            for (int a = 0; a < k; a++)
            {
                double ca = centered[a];
                var covRow = cov.Row(a);
                for (int b = a; b < k; b++) covRow[b] += ca * centered[b];
            }
        }
        double scale = 1.0 / (n - ddof);
        for (int a = 0; a < k; a++)
            for (int b = a; b < k; b++)
            {
                double v = cov[a, b] * scale;
                cov[a, b] = v;
                cov[b, a] = v;
            }
        return cov;
    }

    /// <summary>Correlation matrix of the columns of <paramref name="data"/>.</summary>
    public static Matrix CorrelationMatrix(Matrix data) => CovarianceToCorrelation(CovarianceMatrix(data));

    /// <summary>Converts a covariance matrix to a correlation matrix.</summary>
    public static Matrix CovarianceToCorrelation(Matrix cov)
    {
        int k = cov.Rows;
        var corr = new Matrix(k, k);
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++)
            {
                double d = Math.Sqrt(cov[a, a] * cov[b, b]);
                corr[a, b] = d > 0 ? cov[a, b] / d : (a == b ? 1 : 0);
            }
        return corr;
    }

    /// <summary>Column means of <paramref name="data"/>.</summary>
    public static double[] ColumnMeans(Matrix data)
    {
        var m = new double[data.Columns];
        for (int i = 0; i < data.Rows; i++)
        {
            var row = data.Row(i);
            for (int j = 0; j < m.Length; j++) m[j] += row[j];
        }
        for (int j = 0; j < m.Length; j++) m[j] /= data.Rows;
        return m;
    }

    /// <summary>
    /// Ledoit-Wolf shrinkage of the sample covariance towards a scaled identity
    /// (Ledoit &amp; Wolf, 2004). Returns the shrunk matrix and the shrinkage intensity.
    /// </summary>
    public static (Matrix Covariance, double Shrinkage) LedoitWolf(Matrix data)
    {
        int n = data.Rows, p = data.Columns;
        var means = ColumnMeans(data);
        var x = new Matrix(n, p);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < p; j++) x[i, j] = data[i, j] - means[j];

        var s = new Matrix(p, p); // biased (1/n) sample covariance
        for (int i = 0; i < n; i++)
            for (int a = 0; a < p; a++)
                for (int b = a; b < p; b++) s[a, b] += x[i, a] * x[i, b];
        for (int a = 0; a < p; a++)
            for (int b = a; b < p; b++)
            {
                s[a, b] /= n;
                s[b, a] = s[a, b];
            }

        double mu = 0;
        for (int a = 0; a < p; a++) mu += s[a, a];
        mu /= p;

        double d2 = 0;
        for (int a = 0; a < p; a++)
            for (int b = 0; b < p; b++)
            {
                double v = s[a, b] - (a == b ? mu : 0);
                d2 += v * v;
            }
        d2 /= p;

        double b2 = 0;
        for (int i = 0; i < n; i++)
        {
            double acc = 0;
            for (int a = 0; a < p; a++)
                for (int b = 0; b < p; b++)
                {
                    double v = x[i, a] * x[i, b] - s[a, b];
                    acc += v * v;
                }
            b2 += acc / p;
        }
        b2 /= (double)n * n;
        b2 = Math.Min(b2, d2);
        double shrink = d2 > 0 ? b2 / d2 : 0;

        var result = new Matrix(p, p);
        for (int a = 0; a < p; a++)
            for (int b = 0; b < p; b++)
                result[a, b] = (1 - shrink) * s[a, b] + (a == b ? shrink * mu : 0);
        return (result, shrink);
    }
}
