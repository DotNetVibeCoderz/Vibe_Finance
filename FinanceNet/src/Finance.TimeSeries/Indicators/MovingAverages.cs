namespace Finance.TimeSeries.Indicators;

/// <summary>
/// Technical indicators. Every function returns arrays aligned with its input (same length),
/// with NaN during the warm-up period. Leading NaNs in an input are skipped, so indicators can be
/// chained (e.g. an EMA of a MACD line).
/// </summary>
public static partial class Ta
{
    internal static double[] NaNs(int n) => Rolling.NaNs(n);

    /// <summary>Simple moving average.</summary>
    public static double[] Sma(ReadOnlySpan<double> x, int period = 20) => Rolling.Mean(x, period);

    /// <summary>
    /// Exponential moving average, α = 2/(period+1), seeded with the SMA of the first
    /// <paramref name="period"/> values (TA-Lib convention).
    /// </summary>
    public static double[] Ema(ReadOnlySpan<double> x, int period = 20) => EmaAlpha(x, period, 2.0 / (period + 1));

    /// <summary>Wilder's smoothing (RMA/SMMA), α = 1/period, SMA-seeded.</summary>
    public static double[] Rma(ReadOnlySpan<double> x, int period = 14) => EmaAlpha(x, period, 1.0 / period);

    /// <summary>EMA with an explicit smoothing factor and SMA seed over <paramref name="period"/> values.</summary>
    public static double[] EmaAlpha(ReadOnlySpan<double> x, int period, double alpha)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        var r = NaNs(x.Length);
        int s = Rolling.FirstValid(x);
        if (x.Length - s < period) return r;
        double v = 0;
        for (int i = s; i < s + period; i++) v += x[i];
        v /= period;
        r[s + period - 1] = v;
        for (int i = s + period; i < x.Length; i++)
        {
            v += alpha * (x[i] - v);
            r[i] = v;
        }
        return r;
    }

    /// <summary>Linearly weighted moving average (weights 1..period), O(n).</summary>
    public static double[] Wma(ReadOnlySpan<double> x, int period = 20)
    {
        var r = NaNs(x.Length);
        int s = Rolling.FirstValid(x);
        if (x.Length - s < period) return r;
        double denom = period * (period + 1) / 2.0, weighted = 0, plain = 0;
        for (int i = 0; i < period; i++)
        {
            weighted += (i + 1) * x[s + i];
            plain += x[s + i];
        }
        r[s + period - 1] = weighted / denom;
        for (int i = s + period; i < x.Length; i++)
        {
            weighted += period * x[i] - plain;
            plain += x[i] - x[i - period];
            r[i] = weighted / denom;
        }
        return r;
    }

    /// <summary>Double EMA: 2·EMA - EMA(EMA).</summary>
    public static double[] Dema(ReadOnlySpan<double> x, int period = 20)
    {
        var e1 = Ema(x, period);
        var e2 = Ema(e1, period);
        var r = new double[x.Length];
        for (int i = 0; i < r.Length; i++) r[i] = 2 * e1[i] - e2[i];
        return r;
    }

    /// <summary>Triple EMA: 3·EMA - 3·EMA² + EMA³.</summary>
    public static double[] Tema(ReadOnlySpan<double> x, int period = 20)
    {
        var e1 = Ema(x, period);
        var e2 = Ema(e1, period);
        var e3 = Ema(e2, period);
        var r = new double[x.Length];
        for (int i = 0; i < r.Length; i++) r[i] = 3 * e1[i] - 3 * e2[i] + e3[i];
        return r;
    }

    /// <summary>Triangular MA: SMA of an SMA.</summary>
    public static double[] Trima(ReadOnlySpan<double> x, int period = 20)
    {
        int n1 = (period + 1) / 2, n2 = period / 2 + 1;
        if (period % 2 == 0) { n1 = period / 2; n2 = period / 2 + 1; }
        return Sma(Sma(x, n1), n2);
    }

    /// <summary>Hull MA: WMA(2·WMA(n/2) - WMA(n), √n).</summary>
    public static double[] Hma(ReadOnlySpan<double> x, int period = 20)
    {
        var half = Wma(x, Math.Max(1, period / 2));
        var full = Wma(x, period);
        var diff = new double[x.Length];
        for (int i = 0; i < diff.Length; i++) diff[i] = 2 * half[i] - full[i];
        return Wma(diff, Math.Max(1, (int)Math.Round(Math.Sqrt(period))));
    }

    /// <summary>Kaufman adaptive MA with efficiency ratio over <paramref name="period"/>.</summary>
    public static double[] Kama(ReadOnlySpan<double> x, int period = 10, int fast = 2, int slow = 30)
    {
        var r = NaNs(x.Length);
        int s = Rolling.FirstValid(x);
        if (x.Length - s <= period) return r;
        double fsc = 2.0 / (fast + 1), ssc = 2.0 / (slow + 1);
        double volatility = 0;
        for (int i = s + 1; i <= s + period; i++) volatility += Math.Abs(x[i] - x[i - 1]);
        double k = x[s + period];
        r[s + period] = k;
        for (int i = s + period + 1; i < x.Length; i++)
        {
            volatility += Math.Abs(x[i] - x[i - 1]) - Math.Abs(x[i - period] - x[i - period - 1]);
            double er = volatility > 0 ? Math.Abs(x[i] - x[i - period]) / volatility : 0;
            double sc = er * (fsc - ssc) + ssc;
            k += sc * sc * (x[i] - k);
            r[i] = k;
        }
        return r;
    }

    /// <summary>Tillson T3 (six chained EMAs with volume factor <paramref name="vFactor"/>).</summary>
    public static double[] T3(ReadOnlySpan<double> x, int period = 5, double vFactor = 0.7)
    {
        var e1 = Ema(x, period);
        var e2 = Ema(e1, period);
        var e3 = Ema(e2, period);
        var e4 = Ema(e3, period);
        var e5 = Ema(e4, period);
        var e6 = Ema(e5, period);
        double a = vFactor, a2 = a * a, a3 = a2 * a;
        double c1 = -a3, c2 = 3 * a2 + 3 * a3, c3 = -6 * a2 - 3 * a - 3 * a3, c4 = 1 + 3 * a + a3 + 3 * a2;
        var r = new double[x.Length];
        for (int i = 0; i < r.Length; i++) r[i] = c1 * e6[i] + c2 * e5[i] + c3 * e4[i] + c4 * e3[i];
        return r;
    }

    /// <summary>Volume-weighted MA: Σ(p·v)/Σv over the window.</summary>
    public static double[] Vwma(ReadOnlySpan<double> price, ReadOnlySpan<double> volume, int period = 20)
    {
        var pv = new double[price.Length];
        for (int i = 0; i < pv.Length; i++) pv[i] = price[i] * volume[i];
        var num = Rolling.Sum(pv, period);
        var den = Rolling.Sum(volume, period);
        for (int i = 0; i < num.Length; i++) num[i] = den[i] > 0 ? num[i] / den[i] : double.NaN;
        return num;
    }

    /// <summary>Zero-lag EMA: EMA of (2·x[t] - x[t-lag]) with lag = (period-1)/2.</summary>
    public static double[] Zlema(ReadOnlySpan<double> x, int period = 20)
    {
        int lag = (period - 1) / 2;
        var adj = NaNs(x.Length);
        for (int i = lag; i < x.Length; i++) adj[i] = 2 * x[i] - x[i - lag];
        return Ema(adj, period);
    }

    /// <summary>Arnaud Legoux MA (Gaussian-weighted, offset 0.85, sigma 6).</summary>
    public static double[] Alma(ReadOnlySpan<double> x, int period = 9, double offset = 0.85, double sigma = 6)
    {
        var r = NaNs(x.Length);
        var w = new double[period];
        double m = offset * (period - 1), s = period / sigma, norm = 0;
        for (int i = 0; i < period; i++)
        {
            w[i] = Math.Exp(-(i - m) * (i - m) / (2 * s * s));
            norm += w[i];
        }
        int start = Rolling.FirstValid(x);
        for (int i = start + period - 1; i < x.Length; i++)
        {
            double acc = 0;
            for (int k = 0; k < period; k++) acc += w[k] * x[i - period + 1 + k];
            r[i] = acc / norm;
        }
        return r;
    }

    /// <summary>McGinley Dynamic.</summary>
    public static double[] McGinley(ReadOnlySpan<double> x, int period = 14)
    {
        var r = NaNs(x.Length);
        int s = Rolling.FirstValid(x);
        if (s >= x.Length) return r;
        double md = x[s];
        r[s] = md;
        for (int i = s + 1; i < x.Length; i++)
        {
            double ratio = x[i] / md;
            md += (x[i] - md) / (period * Math.Pow(ratio, 4));
            r[i] = md;
        }
        return r;
    }

    /// <summary>Chande's VIDYA: EMA whose α is scaled by |CMO|.</summary>
    public static double[] Vidya(ReadOnlySpan<double> x, int period = 14, int cmoPeriod = 9)
    {
        var cmo = Cmo(x, cmoPeriod);
        var r = NaNs(x.Length);
        double alpha = 2.0 / (period + 1);
        int s = Rolling.FirstValid(cmo);
        if (s >= x.Length) return r;
        double v = x[s];
        r[s] = v;
        for (int i = s + 1; i < x.Length; i++)
        {
            double k = alpha * Math.Abs(cmo[i]) / 100;
            v += k * (x[i] - v);
            r[i] = v;
        }
        return r;
    }

    /// <summary>Midpoint (highest + lowest) / 2 of a single series over the window.</summary>
    public static double[] Midpoint(ReadOnlySpan<double> x, int period = 14)
    {
        var hi = Rolling.Max(x, period);
        var lo = Rolling.Min(x, period);
        for (int i = 0; i < hi.Length; i++) hi[i] = 0.5 * (hi[i] + lo[i]);
        return hi;
    }

    /// <summary>Mid price (highest high + lowest low) / 2 over the window.</summary>
    public static double[] MidPrice(ReadOnlySpan<double> high, ReadOnlySpan<double> low, int period = 14)
    {
        var hi = Rolling.Max(high, period);
        var lo = Rolling.Min(low, period);
        for (int i = 0; i < hi.Length; i++) hi[i] = 0.5 * (hi[i] + lo[i]);
        return hi;
    }

    /// <summary>Moving-average envelopes: SMA ± percent.</summary>
    public static (double[] Upper, double[] Middle, double[] Lower) Envelopes(ReadOnlySpan<double> x, int period = 20, double percent = 2.5)
    {
        var mid = Sma(x, period);
        var up = new double[x.Length];
        var lo = new double[x.Length];
        for (int i = 0; i < mid.Length; i++)
        {
            up[i] = mid[i] * (1 + percent / 100);
            lo[i] = mid[i] * (1 - percent / 100);
        }
        return (up, mid, lo);
    }
}
