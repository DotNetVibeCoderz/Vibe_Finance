using Finance.Core.Market;
using Finance.Mathematics;

namespace Finance.ML;

/// <summary>Configuration of <see cref="FeatureEngineering.FromBars"/>.</summary>
public sealed record FeatureOptions
{
    /// <summary>Lags of one-bar log returns to include (1 = previous bar's return).</summary>
    public int[] ReturnLags { get; init; } = { 1, 2, 3, 5, 10 };
    public int[] VolatilityWindows { get; init; } = { 10, 20 };
    public int[] MomentumWindows { get; init; } = { 5, 20, 60 };
    public int RsiPeriod { get; init; } = 14;
    public int ZScoreWindow { get; init; } = 20;
    public int VolumeWindow { get; init; } = 20;
    public bool IncludeMacd { get; init; } = true;
}

/// <summary>
/// Turns OHLCV bars into a leakage-free feature matrix: every feature at row t uses data up to and
/// including bar t only. Rows before the longest look-back are dropped.
/// </summary>
public static class FeatureEngineering
{
    /// <summary>Builds the feature matrix.</summary>
    public static FeatureMatrix FromBars(ReadOnlySpan<Bar> bars, FeatureOptions? options = null)
    {
        options ??= new FeatureOptions();
        int n = bars.Length;
        var close = new double[n];
        var volume = new double[n];
        var high = new double[n];
        var low = new double[n];
        for (int i = 0; i < n; i++) { close[i] = bars[i].Close; volume[i] = bars[i].Volume; high[i] = bars[i].High; low[i] = bars[i].Low; }

        var ret = new double[n];
        ret[0] = double.NaN;
        for (int i = 1; i < n; i++) ret[i] = Math.Log(close[i] / close[i - 1]);

        var cols = new List<(string Name, double[] Values)>();
        foreach (int lag in options.ReturnLags)
            cols.Add(($"ret_lag{lag}", Shift(ret, lag - 1)));
        foreach (int w in options.VolatilityWindows)
            cols.Add(($"vol_{w}", RollingStd(ret, w)));
        foreach (int w in options.MomentumWindows)
        {
            var m = new double[n];
            for (int i = 0; i < n; i++) m[i] = i >= w ? close[i] / close[i - w] - 1 : double.NaN;
            cols.Add(($"mom_{w}", m));
        }
        cols.Add(($"rsi_{options.RsiPeriod}", Rsi(close, options.RsiPeriod)));
        if (options.IncludeMacd)
        {
            var e12 = Ema(close, 12);
            var e26 = Ema(close, 26);
            var macd = new double[n];
            for (int i = 0; i < n; i++) macd[i] = (e12[i] - e26[i]) / close[i];
            var signal = Ema(macd, 9);
            var hist = new double[n];
            for (int i = 0; i < n; i++) { hist[i] = macd[i] - signal[i]; if (i < 34) { macd[i] = double.NaN; hist[i] = double.NaN; } }
            cols.Add(("macd", macd));
            cols.Add(("macd_hist", hist));
        }
        cols.Add(($"zscore_{options.ZScoreWindow}", RollingZ(close, options.ZScoreWindow)));
        var logVol = volume.Select(v => Math.Log(1 + v)).ToArray();
        cols.Add(($"volume_z_{options.VolumeWindow}", RollingZ(logVol, options.VolumeWindow)));
        var hl = new double[n];
        for (int i = 0; i < n; i++) hl[i] = Math.Log(high[i] / low[i]);
        cols.Add(("hl_range", hl));

        int first = 0;
        for (int i = 0; i < n; i++)
        {
            bool ok = true;
            foreach (var c in cols) if (double.IsNaN(c.Values[i])) { ok = false; break; }
            if (ok) { first = i; break; }
            first = n;
        }
        int rows = n - first;
        var m2 = new Matrix(rows, cols.Count);
        var times = new DateTime[rows];
        for (int i = 0; i < rows; i++)
        {
            times[i] = bars[first + i].Time;
            for (int j = 0; j < cols.Count; j++) m2[i, j] = cols[j].Values[first + i];
        }
        return new FeatureMatrix(m2, cols.Select(c => c.Name).ToArray(), times);
    }

    /// <summary>Forward log return over <paramref name="horizon"/> bars aligned to each feature row's time (NaN at the end).</summary>
    public static double[] ForwardReturns(ReadOnlySpan<Bar> bars, FeatureMatrix features, int horizon = 1)
    {
        var pos = new Dictionary<DateTime, int>(bars.Length);
        for (int i = 0; i < bars.Length; i++) pos[bars[i].Time] = i;
        var y = new double[features.Rows];
        for (int r = 0; r < features.Rows; r++)
        {
            int i = pos[features.Times[r]];
            y[r] = i + horizon < bars.Length ? Math.Log(bars[i + horizon].Close / bars[i].Close) : double.NaN;
        }
        return y;
    }

    internal static double[] Shift(double[] x, int k)
    {
        var r = new double[x.Length];
        for (int i = 0; i < x.Length; i++) r[i] = i - k >= 0 ? x[i - k] : double.NaN;
        return r;
    }

    internal static double[] RollingStd(double[] x, int w)
    {
        var r = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            if (i < w) { r[i] = double.NaN; continue; }
            r[i] = Stats.StdDev(x.AsSpan(i - w + 1, w));
        }
        return r;
    }

    internal static double[] RollingZ(double[] x, int w)
    {
        var r = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            if (i < w - 1) { r[i] = double.NaN; continue; }
            var s = x.AsSpan(i - w + 1, w);
            double sd = Stats.StdDev(s);
            r[i] = sd > 0 ? (x[i] - Stats.Mean(s)) / sd : 0;
        }
        return r;
    }

    internal static double[] Ema(double[] x, int period)
    {
        var r = new double[x.Length];
        double a = 2.0 / (period + 1);
        r[0] = x[0];
        for (int i = 1; i < x.Length; i++) r[i] = a * x[i] + (1 - a) * r[i - 1];
        return r;
    }

    /// <summary>Wilder RSI.</summary>
    internal static double[] Rsi(double[] close, int period)
    {
        int n = close.Length;
        var r = new double[n];
        Array.Fill(r, double.NaN);
        if (n <= period) return r;
        double gain = 0, loss = 0;
        for (int i = 1; i <= period; i++)
        {
            double d = close[i] - close[i - 1];
            if (d > 0) gain += d; else loss -= d;
        }
        gain /= period;
        loss /= period;
        r[period] = loss == 0 ? 100 : 100 - 100 / (1 + gain / loss);
        for (int i = period + 1; i < n; i++)
        {
            double d = close[i] - close[i - 1];
            gain = (gain * (period - 1) + Math.Max(d, 0)) / period;
            loss = (loss * (period - 1) + Math.Max(-d, 0)) / period;
            r[i] = loss == 0 ? 100 : 100 - 100 / (1 + gain / loss);
        }
        return r;
    }
}

/// <summary>
/// Fractional differentiation with a fixed-width window (López de Prado, AFML ch. 5): keeps
/// memory in the series while making it closer to stationary.
/// </summary>
public static class FractionalDifferentiation
{
    /// <summary>Binomial weights w_k = -w_{k-1} (d - k + 1) / k, truncated when |w_k| &lt; threshold.</summary>
    public static double[] Weights(double d, double threshold = 1e-4, int maxLength = 10_000)
    {
        var w = new List<double> { 1.0 };
        for (int k = 1; k < maxLength; k++)
        {
            double next = -w[^1] * (d - k + 1) / k;
            if (Math.Abs(next) < threshold) break;
            w.Add(next);
        }
        return w.ToArray();
    }

    /// <summary>Fractionally differenced series; the first (window - 1) values are NaN.</summary>
    public static double[] Differentiate(ReadOnlySpan<double> series, double d, double threshold = 1e-4)
    {
        var w = Weights(d, threshold);
        var r = new double[series.Length];
        for (int t = 0; t < series.Length; t++)
        {
            if (t < w.Length - 1) { r[t] = double.NaN; continue; }
            double s = 0;
            for (int k = 0; k < w.Length; k++) s += w[k] * series[t - k];
            r[t] = s;
        }
        return r;
    }
}
