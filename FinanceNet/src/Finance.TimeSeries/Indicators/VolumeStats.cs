using Finance.Mathematics;

namespace Finance.TimeSeries.Indicators;

public static partial class Ta
{
    // ---------------- Volume ----------------

    /// <summary>On-Balance Volume (starts at the first bar's volume).</summary>
    public static double[] Obv(ReadOnlySpan<double> close, ReadOnlySpan<double> volume)
    {
        var r = new double[close.Length];
        if (r.Length == 0) return r;
        r[0] = volume[0];
        for (int i = 1; i < r.Length; i++)
            r[i] = r[i - 1] + (close[i] > close[i - 1] ? volume[i] : close[i] < close[i - 1] ? -volume[i] : 0);
        return r;
    }

    /// <summary>Cumulative VWAP from the first bar (session VWAP when the input is one session).</summary>
    public static double[] Vwap(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, ReadOnlySpan<double> volume)
    {
        var r = NaNs(close.Length);
        double pv = 0, v = 0;
        for (int i = 0; i < r.Length; i++)
        {
            pv += (high[i] + low[i] + close[i]) / 3 * volume[i];
            v += volume[i];
            if (v > 0) r[i] = pv / v;
        }
        return r;
    }

    /// <summary>VWAP that resets whenever the calendar date changes (intraday sessions).</summary>
    public static double[] SessionVwap(ReadOnlySpan<DateTime> time, ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, ReadOnlySpan<double> volume)
    {
        var r = NaNs(close.Length);
        double pv = 0, v = 0;
        for (int i = 0; i < r.Length; i++)
        {
            if (i > 0 && time[i].Date != time[i - 1].Date) { pv = 0; v = 0; }
            pv += (high[i] + low[i] + close[i]) / 3 * volume[i];
            v += volume[i];
            if (v > 0) r[i] = pv / v;
        }
        return r;
    }

    /// <summary>Rolling VWAP over a window of bars.</summary>
    public static double[] RollingVwap(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, ReadOnlySpan<double> volume, int period = 20)
        => Vwma(TypicalPrice(high, low, close), volume, period);

    /// <summary>Money Flow Index (volume-weighted RSI of the typical price).</summary>
    public static double[] Mfi(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, ReadOnlySpan<double> volume, int period = 14)
    {
        var tp = TypicalPrice(high, low, close);
        int n = tp.Length;
        var pos = NaNs(n);
        var neg = NaNs(n);
        for (int i = 1; i < n; i++)
        {
            double mf = tp[i] * volume[i];
            pos[i] = tp[i] > tp[i - 1] ? mf : 0;
            neg[i] = tp[i] < tp[i - 1] ? mf : 0;
        }
        var sp = Rolling.Sum(pos, period);
        var sn = Rolling.Sum(neg, period);
        var r = NaNs(n);
        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(sp[i])) continue;
            double t = sp[i] + sn[i];
            r[i] = t > 0 ? 100 * sp[i] / t : 50;
        }
        return r;
    }

    private static double ClvVolume(double h, double l, double c, double v)
        => h > l ? ((c - l) - (h - c)) / (h - l) * v : 0;

    /// <summary>Chaikin Accumulation/Distribution line.</summary>
    public static double[] AccumulationDistribution(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, ReadOnlySpan<double> volume)
    {
        var r = new double[close.Length];
        double ad = 0;
        for (int i = 0; i < r.Length; i++)
        {
            ad += ClvVolume(high[i], low[i], close[i], volume[i]);
            r[i] = ad;
        }
        return r;
    }

    /// <summary>Chaikin Money Flow: Σ(CLV·V)/ΣV over the window.</summary>
    public static double[] Cmf(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, ReadOnlySpan<double> volume, int period = 20)
    {
        var mfv = new double[close.Length];
        for (int i = 0; i < mfv.Length; i++) mfv[i] = ClvVolume(high[i], low[i], close[i], volume[i]);
        var a = Rolling.Sum(mfv, period);
        var b = Rolling.Sum(volume, period);
        for (int i = 0; i < a.Length; i++) a[i] = b[i] > 0 ? a[i] / b[i] : 0;
        for (int i = 0; i < Math.Min(period - 1, a.Length); i++) a[i] = double.NaN;
        return a;
    }

    /// <summary>Chaikin Oscillator: EMA3(A/D) - EMA10(A/D).</summary>
    public static double[] ChaikinOscillator(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, ReadOnlySpan<double> volume, int fast = 3, int slow = 10)
        => Apo(AccumulationDistribution(high, low, close, volume), fast, slow);

    /// <summary>Elder's Force Index: EMA of (close change × volume).</summary>
    public static double[] ForceIndex(ReadOnlySpan<double> close, ReadOnlySpan<double> volume, int period = 13)
    {
        var f = NaNs(close.Length);
        for (int i = 1; i < f.Length; i++) f[i] = (close[i] - close[i - 1]) * volume[i];
        return Ema(f, period);
    }

    /// <summary>Ease of Movement (scaled by 1e8 / volume), smoothed by an SMA.</summary>
    public static double[] EaseOfMovement(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> volume, int period = 14, double scale = 1e8)
    {
        var e = NaNs(high.Length);
        for (int i = 1; i < e.Length; i++)
        {
            double move = (high[i] + low[i]) / 2 - (high[i - 1] + low[i - 1]) / 2;
            double range = high[i] - low[i];
            e[i] = volume[i] > 0 && range > 0 ? move / (volume[i] / scale / range) : 0;
        }
        return Sma(e, period);
    }

    /// <summary>Volume-Price Trend.</summary>
    public static double[] Vpt(ReadOnlySpan<double> close, ReadOnlySpan<double> volume)
    {
        var r = new double[close.Length];
        for (int i = 1; i < r.Length; i++) r[i] = r[i - 1] + volume[i] * (close[i] - close[i - 1]) / close[i - 1];
        return r;
    }

    /// <summary>Negative Volume Index (starts at 1000): changes only on volume-down days.</summary>
    public static double[] Nvi(ReadOnlySpan<double> close, ReadOnlySpan<double> volume)
    {
        var r = new double[close.Length];
        if (r.Length == 0) return r;
        r[0] = 1000;
        for (int i = 1; i < r.Length; i++)
            r[i] = volume[i] < volume[i - 1] ? r[i - 1] * close[i] / close[i - 1] : r[i - 1];
        return r;
    }

    /// <summary>Positive Volume Index (starts at 1000): changes only on volume-up days.</summary>
    public static double[] Pvi(ReadOnlySpan<double> close, ReadOnlySpan<double> volume)
    {
        var r = new double[close.Length];
        if (r.Length == 0) return r;
        r[0] = 1000;
        for (int i = 1; i < r.Length; i++)
            r[i] = volume[i] > volume[i - 1] ? r[i - 1] * close[i] / close[i - 1] : r[i - 1];
        return r;
    }

    /// <summary>Volume oscillator: 100·(EMA fast - EMA slow)/EMA slow of volume (= PVO line).</summary>
    public static double[] VolumeOscillator(ReadOnlySpan<double> volume, int fast = 12, int slow = 26)
        => Ppo(volume, fast, slow).Ppo;

    /// <summary>Klinger Volume Oscillator and its 13-period signal.</summary>
    public static (double[] Kvo, double[] Signal) Klinger(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, ReadOnlySpan<double> volume, int fast = 34, int slow = 55, int signal = 13)
    {
        int n = close.Length;
        var vf = NaNs(n);
        for (int i = 1; i < n; i++)
        {
            double tp = high[i] + low[i] + close[i], prev = high[i - 1] + low[i - 1] + close[i - 1];
            vf[i] = tp > prev ? volume[i] : -volume[i];
        }
        var kvo = Apo(vf, fast, slow);
        return (kvo, Ema(kvo, signal));
    }

    // ---------------- Statistics ----------------

    /// <summary>Rolling z-score of the series.</summary>
    public static double[] ZScore(ReadOnlySpan<double> x, int period = 20) => Rolling.ZScore(x, period);

    /// <summary>Rolling (sample) variance.</summary>
    public static double[] Variance(ReadOnlySpan<double> x, int period = 20) => Rolling.Variance(x, period, ddof: 0);

    /// <summary>Rolling skewness.</summary>
    public static double[] RollingSkewness(ReadOnlySpan<double> x, int period = 20) => Rolling.Apply(x, period, w => Stats.Skewness(w));

    /// <summary>Rolling excess kurtosis.</summary>
    public static double[] RollingKurtosis(ReadOnlySpan<double> x, int period = 20) => Rolling.Apply(x, period, w => Stats.ExcessKurtosis(w));

    /// <summary>Rolling median.</summary>
    public static double[] RollingMedian(ReadOnlySpan<double> x, int period = 20) => Rolling.Apply(x, period, w => Stats.Median(w));

    /// <summary>Rolling percentile rank (0-100).</summary>
    public static double[] PercentRank(ReadOnlySpan<double> x, int period = 20) => Rolling.PercentRank(x, period);

    /// <summary>Rolling correlation of the series with time (the Pearson r of a linear trend).</summary>
    public static double[] TrendCorrelation(ReadOnlySpan<double> x, int period = 20)
    {
        var t = new double[x.Length];
        for (int i = 0; i < t.Length; i++) t[i] = i;
        return Rolling.Correlation(x, t, period);
    }

    /// <summary>Rolling Hurst exponent by the rescaled-range method on log returns (≈0.5 random walk).</summary>
    public static double[] Hurst(ReadOnlySpan<double> close, int period = 100)
    {
        var lr = NaNs(close.Length);
        for (int i = 1; i < close.Length; i++) lr[i] = Math.Log(close[i] / close[i - 1]);
        return Rolling.Apply(lr, period, HurstRs);
    }

    private static double HurstRs(ReadOnlySpan<double> r)
    {
        var sizes = new List<int>();
        for (int s = 8; s <= r.Length / 2; s *= 2) sizes.Add(s);
        if (sizes.Count < 2) return double.NaN;
        var xs = new double[sizes.Count];
        var ys = new double[sizes.Count];
        for (int k = 0; k < sizes.Count; k++)
        {
            int size = sizes[k], chunks = r.Length / size;
            double rsSum = 0;
            int used = 0;
            for (int c = 0; c < chunks; c++)
            {
                var w = r.Slice(c * size, size);
                double m = Stats.Mean(w), cum = 0, max = double.NegativeInfinity, min = double.PositiveInfinity;
                foreach (var v in w)
                {
                    cum += v - m;
                    max = Math.Max(max, cum);
                    min = Math.Min(min, cum);
                }
                double sd = Stats.StdDev(w, 0);
                if (sd > 0) { rsSum += (max - min) / sd; used++; }
            }
            xs[k] = Math.Log(size);
            ys[k] = used > 0 ? Math.Log(rsSum / used) : double.NaN;
        }
        if (ys.Any(double.IsNaN)) return double.NaN;
        return Regression.Simple(xs, ys).Coefficients[1];
    }
}
