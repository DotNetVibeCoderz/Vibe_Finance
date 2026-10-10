namespace Finance.TimeSeries;

/// <summary>
/// O(n) rolling-window statistics. Outputs are aligned with the input: element i covers
/// [i - window + 1, i] and is NaN until the window is full. Leading NaNs in the input are skipped
/// (the window starts at the first finite value), so results can be chained.
/// </summary>
public static class Rolling
{
    internal static double[] NaNs(int n)
    {
        var a = new double[n];
        a.AsSpan().Fill(double.NaN);
        return a;
    }

    /// <summary>Index of the first non-NaN element (or length if none).</summary>
    public static int FirstValid(ReadOnlySpan<double> x)
    {
        int i = 0;
        while (i < x.Length && double.IsNaN(x[i])) i++;
        return i;
    }

    /// <summary>Rolling sum.</summary>
    public static double[] Sum(ReadOnlySpan<double> x, int window)
    {
        if (window <= 0) throw new ArgumentOutOfRangeException(nameof(window));
        var r = NaNs(x.Length);
        int s = FirstValid(x);
        double acc = 0, comp = 0;
        for (int i = s; i < x.Length; i++)
        {
            // Kahan-compensated add and remove, so long series do not drift.
            double add = x[i] - (i - window >= s ? x[i - window] : 0);
            double y = add - comp, t = acc + y;
            comp = (t - acc) - y;
            acc = t;
            if (i - s + 1 >= window) r[i] = acc;
        }
        return r;
    }

    /// <summary>Rolling mean.</summary>
    public static double[] Mean(ReadOnlySpan<double> x, int window)
    {
        var r = Sum(x, window);
        double inv = 1.0 / window;
        for (int i = 0; i < r.Length; i++) r[i] *= inv;
        return r;
    }

    /// <summary>Rolling variance (Welford add/remove form) with <paramref name="ddof"/>.</summary>
    public static double[] Variance(ReadOnlySpan<double> x, int window, int ddof = 1)
    {
        if (window - ddof <= 0) throw new ArgumentOutOfRangeException(nameof(window));
        var r = NaNs(x.Length);
        int s = FirstValid(x);
        double mean = 0, m2 = 0;
        int count = 0;
        for (int i = s; i < x.Length; i++)
        {
            double v = x[i];
            if (count < window)
            {
                count++;
                double d = v - mean;
                mean += d / count;
                m2 += d * (v - mean);
            }
            else
            {
                double old = x[i - window];
                double oldMean = mean;
                mean += (v - old) / window;
                m2 += (v - old) * (v - mean + old - oldMean);
            }
            if (count == window) r[i] = Math.Max(0, m2) / (window - ddof);
        }
        return r;
    }

    /// <summary>Rolling standard deviation.</summary>
    public static double[] StdDev(ReadOnlySpan<double> x, int window, int ddof = 1)
    {
        var r = Variance(x, window, ddof);
        for (int i = 0; i < r.Length; i++) r[i] = Math.Sqrt(r[i]);
        return r;
    }

    /// <summary>Rolling maximum via a monotonic deque (amortised O(1) per element).</summary>
    public static double[] Max(ReadOnlySpan<double> x, int window) => Extreme(x, window, max: true);

    /// <summary>Rolling minimum via a monotonic deque.</summary>
    public static double[] Min(ReadOnlySpan<double> x, int window) => Extreme(x, window, max: false);

    /// <summary>Index of the rolling maximum (absolute index into x), or -1 during warm-up.</summary>
    public static int[] ArgMax(ReadOnlySpan<double> x, int window) => ExtremeIndex(x, window, max: true);

    /// <summary>Index of the rolling minimum (absolute index into x), or -1 during warm-up.</summary>
    public static int[] ArgMin(ReadOnlySpan<double> x, int window) => ExtremeIndex(x, window, max: false);

    private static double[] Extreme(ReadOnlySpan<double> x, int window, bool max)
    {
        var idx = ExtremeIndex(x, window, max);
        var r = NaNs(x.Length);
        for (int i = 0; i < r.Length; i++) if (idx[i] >= 0) r[i] = x[idx[i]];
        return r;
    }

    private static int[] ExtremeIndex(ReadOnlySpan<double> x, int window, bool max)
    {
        if (window <= 0) throw new ArgumentOutOfRangeException(nameof(window));
        var result = new int[x.Length];
        result.AsSpan().Fill(-1);
        var dq = new int[x.Length];
        int head = 0, tail = 0;
        int s = FirstValid(x);
        for (int i = s; i < x.Length; i++)
        {
            while (head < tail && dq[head] <= i - window) head++;
            // Ties keep the most recent index, matching pandas/TA-Lib "last extreme" semantics.
            while (head < tail && (max ? x[dq[tail - 1]] <= x[i] : x[dq[tail - 1]] >= x[i])) tail--;
            dq[tail++] = i;
            if (i - s + 1 >= window) result[i] = dq[head];
        }
        return result;
    }

    /// <summary>Rolling covariance of two aligned series.</summary>
    public static double[] Covariance(ReadOnlySpan<double> x, ReadOnlySpan<double> y, int window, int ddof = 1)
    {
        if (x.Length != y.Length) throw new ArgumentException("Length mismatch.");
        var r = NaNs(x.Length);
        int s = Math.Max(FirstValid(x), FirstValid(y));
        double sx = 0, sy = 0, sxy = 0;
        for (int i = s; i < x.Length; i++)
        {
            sx += x[i]; sy += y[i]; sxy += x[i] * y[i];
            if (i - window >= s) { sx -= x[i - window]; sy -= y[i - window]; sxy -= x[i - window] * y[i - window]; }
            if (i - s + 1 >= window) r[i] = (sxy - sx * sy / window) / (window - ddof);
        }
        return r;
    }

    /// <summary>Rolling Pearson correlation.</summary>
    public static double[] Correlation(ReadOnlySpan<double> x, ReadOnlySpan<double> y, int window)
    {
        if (x.Length != y.Length) throw new ArgumentException("Length mismatch.");
        var r = NaNs(x.Length);
        int s = Math.Max(FirstValid(x), FirstValid(y));
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        for (int i = s; i < x.Length; i++)
        {
            double a = x[i], b = y[i];
            sx += a; sy += b; sxx += a * a; syy += b * b; sxy += a * b;
            if (i - window >= s)
            {
                double c = x[i - window], d = y[i - window];
                sx -= c; sy -= d; sxx -= c * c; syy -= d * d; sxy -= c * d;
            }
            if (i - s + 1 >= window)
            {
                double cov = sxy - sx * sy / window, vx = sxx - sx * sx / window, vy = syy - sy * sy / window;
                r[i] = vx > 0 && vy > 0 ? cov / Math.Sqrt(vx * vy) : double.NaN;
            }
        }
        return r;
    }

    /// <summary>Rolling beta of <paramref name="asset"/> returns against <paramref name="benchmark"/> returns.</summary>
    public static double[] Beta(ReadOnlySpan<double> asset, ReadOnlySpan<double> benchmark, int window)
    {
        var cov = Covariance(asset, benchmark, window);
        var v = Variance(benchmark, window);
        for (int i = 0; i < cov.Length; i++) cov[i] = v[i] > 0 ? cov[i] / v[i] : double.NaN;
        return cov;
    }

    /// <summary>Rolling z-score (x - mean) / std.</summary>
    public static double[] ZScore(ReadOnlySpan<double> x, int window)
    {
        var m = Mean(x, window);
        var sd = StdDev(x, window);
        var r = new double[x.Length];
        for (int i = 0; i < r.Length; i++) r[i] = sd[i] > 0 ? (x[i] - m[i]) / sd[i] : double.NaN;
        return r;
    }

    /// <summary>Exponentially weighted moving average with smoothing factor <paramref name="alpha"/>, seeded with the first value.</summary>
    public static double[] Ewma(ReadOnlySpan<double> x, double alpha)
    {
        if (!(alpha > 0 && alpha <= 1)) throw new ArgumentOutOfRangeException(nameof(alpha));
        var r = NaNs(x.Length);
        int s = FirstValid(x);
        if (s == x.Length) return r;
        double v = x[s];
        r[s] = v;
        for (int i = s + 1; i < x.Length; i++)
        {
            v += alpha * (x[i] - v);
            r[i] = v;
        }
        return r;
    }

    /// <summary>
    /// RiskMetrics EWMA volatility of returns: σ²[t] = λ σ²[t-1] + (1-λ) r²[t-1]; returns per-period σ.
    /// </summary>
    public static double[] EwmaVolatility(ReadOnlySpan<double> returns, double lambda = 0.94)
    {
        var r = NaNs(returns.Length);
        if (returns.Length == 0) return r;
        double var = returns[0] * returns[0];
        r[0] = Math.Sqrt(var);
        for (int i = 1; i < returns.Length; i++)
        {
            var = lambda * var + (1 - lambda) * returns[i - 1] * returns[i - 1];
            r[i] = Math.Sqrt(var);
        }
        return r;
    }

    /// <summary>Rolling percentile rank of the latest value within its window, in [0, 100].</summary>
    public static double[] PercentRank(ReadOnlySpan<double> x, int window)
    {
        var r = NaNs(x.Length);
        int s = FirstValid(x);
        for (int i = s + window - 1; i < x.Length; i++)
        {
            int below = 0;
            for (int k = i - window + 1; k < i; k++) if (x[k] < x[i]) below++;
            r[i] = 100.0 * below / (window - 1);
        }
        return r;
    }

    /// <summary>Generic rolling apply (O(n·window)); use for statistics without an O(n) form.</summary>
    public static double[] Apply(ReadOnlySpan<double> x, int window, Func<ReadOnlySpan<double>, double> f)
    {
        var r = NaNs(x.Length);
        int s = FirstValid(x);
        for (int i = s + window - 1; i < x.Length; i++) r[i] = f(x.Slice(i - window + 1, window));
        return r;
    }

    /// <summary>Shift by <paramref name="periods"/> (positive = lag), filling with NaN.</summary>
    public static double[] Shift(ReadOnlySpan<double> x, int periods)
    {
        var r = NaNs(x.Length);
        for (int i = 0; i < x.Length; i++)
        {
            int j = i - periods;
            if (j >= 0 && j < x.Length) r[i] = x[j];
        }
        return r;
    }

    /// <summary>Difference x[t] - x[t - lag].</summary>
    public static double[] Diff(ReadOnlySpan<double> x, int lag = 1)
    {
        var r = NaNs(x.Length);
        for (int i = lag; i < x.Length; i++) r[i] = x[i] - x[i - lag];
        return r;
    }
}

/// <summary>Helpers to align time-indexed series.</summary>
public static class SeriesAlignment
{
    /// <summary>Inner join of two time-indexed series on exact timestamps (both sorted ascending).</summary>
    public static (DateTime[] Time, double[] Left, double[] Right) InnerJoin(
        ReadOnlySpan<DateTime> leftTime, ReadOnlySpan<double> left, ReadOnlySpan<DateTime> rightTime, ReadOnlySpan<double> right)
    {
        var t = new List<DateTime>();
        var a = new List<double>();
        var b = new List<double>();
        int i = 0, j = 0;
        while (i < leftTime.Length && j < rightTime.Length)
        {
            int c = leftTime[i].CompareTo(rightTime[j]);
            if (c == 0) { t.Add(leftTime[i]); a.Add(left[i]); b.Add(right[j]); i++; j++; }
            else if (c < 0) i++;
            else j++;
        }
        return (t.ToArray(), a.ToArray(), b.ToArray());
    }

    /// <summary>
    /// As-of alignment: for each target time, the last source value at or before it (NaN if none).
    /// </summary>
    public static double[] AsOf(ReadOnlySpan<DateTime> targetTime, ReadOnlySpan<DateTime> sourceTime, ReadOnlySpan<double> source)
    {
        var r = new double[targetTime.Length];
        int j = -1;
        for (int i = 0; i < targetTime.Length; i++)
        {
            while (j + 1 < sourceTime.Length && sourceTime[j + 1] <= targetTime[i]) j++;
            r[i] = j >= 0 ? source[j] : double.NaN;
        }
        return r;
    }

    /// <summary>Forward-fills NaN values in place and returns the array.</summary>
    public static double[] ForwardFill(double[] x)
    {
        double last = double.NaN;
        for (int i = 0; i < x.Length; i++)
        {
            if (double.IsNaN(x[i])) x[i] = last;
            else last = x[i];
        }
        return x;
    }
}
