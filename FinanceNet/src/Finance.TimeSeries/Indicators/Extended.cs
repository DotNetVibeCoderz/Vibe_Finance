namespace Finance.TimeSeries.Indicators;

/// <summary>Output of <see cref="Ta.VolumeProfile"/> and <see cref="Ta.MarketProfile"/>: point of control and value area.</summary>
public sealed record ProfileResult(double[] PointOfControl, double[] ValueAreaHigh, double[] ValueAreaLow);

// Second wave of the catalogue (v0.2): adaptive averages, Bill Williams tools, trend-quality gauges,
// further oscillators, range-based bands, volume-flow measures, rolling profiles and descriptive statistics.
public static partial class Ta
{
    // ---------------- Moving averages ----------------

    /// <summary>Ehlers' Fractal Adaptive MA: α = exp(−4.6·(D − 1)) from the fractal dimension D of the high/low range.</summary>
    public static double[] Frama(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 16)
    {
        int n = close.Length;
        var r = NaNs(n);
        if (period % 2 == 1) period++;
        int half = period / 2;
        if (n < period) return r;
        double f = close[period - 1];
        for (int i = period - 1; i < n; i++)
        {
            double n1 = Range(high, low, i - half + 1, i) / half;
            double n2 = Range(high, low, i - period + 1, i - half) / half;
            double n3 = Range(high, low, i - period + 1, i) / period;
            double alpha = 1;
            if (n1 + n2 > 0 && n3 > 0)
            {
                double d = (Math.Log(n1 + n2) - Math.Log(n3)) / Math.Log(2);
                alpha = Math.Clamp(Math.Exp(-4.6 * (d - 1)), 0.01, 1);
            }
            f = i == period - 1 ? close[i] : alpha * close[i] + (1 - alpha) * f;
            r[i] = f;
        }
        return r;

        static double Range(ReadOnlySpan<double> h, ReadOnlySpan<double> l, int from, int to)
        {
            double hi = double.MinValue, lo = double.MaxValue;
            for (int k = from; k <= to; k++) { hi = Math.Max(hi, h[k]); lo = Math.Min(lo, l[k]); }
            return hi - lo;
        }
    }

    /// <summary>Sine-weighted MA: weights sin(π·j/(n+1)), j = 1..n.</summary>
    public static double[] SineWma(ReadOnlySpan<double> x, int period = 14)
    {
        var w = new double[period];
        for (int j = 0; j < period; j++) w[j] = Math.Sin(Math.PI * (j + 1) / (period + 1));
        return Weighted(x, w);
    }

    /// <summary>Geometric moving average: exp of the mean log price (positive inputs).</summary>
    public static double[] GeometricMa(ReadOnlySpan<double> x, int period = 10)
    {
        var logs = new double[x.Length];
        for (int i = 0; i < x.Length; i++) logs[i] = x[i] > 0 ? Math.Log(x[i]) : double.NaN;
        var m = Rolling.Mean(logs, period);
        for (int i = 0; i < m.Length; i++) m[i] = Math.Exp(m[i]);
        return m;
    }

    /// <summary>Exponential Hull MA: EMA(2·EMA(n/2) − EMA(n), √n).</summary>
    public static double[] Ehma(ReadOnlySpan<double> x, int period = 20)
    {
        var half = Ema(x, Math.Max(1, period / 2));
        var full = Ema(x, period);
        var diff = new double[x.Length];
        for (int i = 0; i < diff.Length; i++) diff[i] = 2 * half[i] - full[i];
        return Ema(diff, Math.Max(1, (int)Math.Round(Math.Sqrt(period))));
    }

    /// <summary>Weighted window sum, weights[0] applied to the oldest value; normalised by Σweights.</summary>
    private static double[] Weighted(ReadOnlySpan<double> x, double[] weights)
    {
        int p = weights.Length;
        var r = NaNs(x.Length);
        int s = Rolling.FirstValid(x);
        double sw = weights.Sum();
        for (int i = s + p - 1; i < x.Length; i++)
        {
            double acc = 0;
            for (int j = 0; j < p; j++) acc += weights[j] * x[i - p + 1 + j];
            r[i] = acc / sw;
        }
        return r;
    }

    // ---------------- Bill Williams ----------------

    /// <summary>Williams Alligator: SMMAs of the median price (13/8/5) displaced 8/5/3 bars, lagged so no future data is used.</summary>
    public static (double[] Jaw, double[] Teeth, double[] Lips) Alligator(ReadOnlySpan<double> high, ReadOnlySpan<double> low,
        int jaw = 13, int jawShift = 8, int teeth = 8, int teethShift = 5, int lips = 5, int lipsShift = 3)
    {
        var mp = MedianPrice(high, low);
        return (Rolling.Shift(Rma(mp, jaw), jawShift), Rolling.Shift(Rma(mp, teeth), teethShift), Rolling.Shift(Rma(mp, lips), lipsShift));
    }

    /// <summary>Gator oscillator: |Jaw − Teeth| above zero and −|Teeth − Lips| below.</summary>
    public static (double[] Upper, double[] Lower) Gator(ReadOnlySpan<double> high, ReadOnlySpan<double> low)
    {
        var (j, t, l) = Alligator(high, low);
        var up = new double[j.Length];
        var lo = new double[j.Length];
        for (int i = 0; i < up.Length; i++) { up[i] = Math.Abs(j[i] - t[i]); lo[i] = -Math.Abs(t[i] - l[i]); }
        return (up, lo);
    }

    /// <summary>
    /// Williams fractals: a high (low) with <paramref name="wing"/> lower highs (higher lows) on each side. The level is
    /// reported on the bar that confirms it (<paramref name="wing"/> bars after the extreme), NaN elsewhere.
    /// </summary>
    public static (double[] Up, double[] Down) Fractals(ReadOnlySpan<double> high, ReadOnlySpan<double> low, int wing = 2)
    {
        int n = high.Length;
        var up = NaNs(n);
        var dn = NaNs(n);
        for (int i = 2 * wing; i < n; i++)
        {
            int c = i - wing;
            bool isUp = true, isDown = true;
            for (int k = 1; k <= wing; k++)
            {
                isUp &= high[c] > high[c - k] && high[c] > high[c + k];
                isDown &= low[c] < low[c - k] && low[c] < low[c + k];
            }
            if (isUp) up[i] = high[c];
            if (isDown) dn[i] = low[c];
        }
        return (up, dn);
    }

    /// <summary>
    /// ZigZag: swing points where price reverses by at least <paramref name="percent"/>%, linearly joined.
    /// It is drawn with hindsight (the last leg repaints) — a chart aid, never a trading signal.
    /// </summary>
    public static double[] ZigZag(ReadOnlySpan<double> high, ReadOnlySpan<double> low, double percent = 5)
    {
        int n = high.Length;
        var r = NaNs(n);
        if (n == 0) return r;
        var pivots = new List<(int Index, double Price)>();
        int dir = 0, extIdx = 0;
        double ext = (high[0] + low[0]) / 2, th = percent / 100;
        pivots.Add((0, ext));
        for (int i = 1; i < n; i++)
        {
            if (dir >= 0 && high[i] > ext && (dir == 1 || high[i] >= pivots[^1].Price * (1 + th))) { ext = high[i]; extIdx = i; dir = 1; }
            else if (dir <= 0 && low[i] < ext && (dir == -1 || low[i] <= pivots[^1].Price * (1 - th))) { ext = low[i]; extIdx = i; dir = -1; }
            else if (dir == 1 && low[i] <= ext * (1 - th)) { pivots.Add((extIdx, ext)); ext = low[i]; extIdx = i; dir = -1; }
            else if (dir == -1 && high[i] >= ext * (1 + th)) { pivots.Add((extIdx, ext)); ext = high[i]; extIdx = i; dir = 1; }
        }
        if (extIdx != pivots[^1].Index) pivots.Add((extIdx, ext));
        for (int p = 1; p < pivots.Count; p++)
        {
            var (i0, v0) = pivots[p - 1];
            var (i1, v1) = pivots[p];
            for (int i = i0; i <= i1; i++) r[i] = i1 == i0 ? v1 : v0 + (v1 - v0) * (i - i0) / (i1 - i0);
        }
        return r;
    }

    // ---------------- Trend quality ----------------

    /// <summary>Vertical Horizontal Filter: (highest − lowest close) / Σ|Δclose| over n bars. High = trending.</summary>
    public static double[] Vhf(ReadOnlySpan<double> close, int period = 28)
    {
        int n = close.Length;
        var r = NaNs(n);
        var hi = Rolling.Max(close, period);
        var lo = Rolling.Min(close, period);
        var absDiff = NaNs(n);
        for (int i = 1; i < n; i++) absDiff[i] = Math.Abs(close[i] - close[i - 1]);
        var sum = Rolling.Sum(absDiff, period);
        for (int i = 0; i < n; i++) r[i] = sum[i] > 0 ? (hi[i] - lo[i]) / sum[i] : double.NaN;
        return r;
    }

    /// <summary>Kaufman efficiency ratio: |close − close[n]| / Σ|Δclose| (0 = noise, 1 = straight line).</summary>
    public static double[] EfficiencyRatio(ReadOnlySpan<double> close, int period = 10)
    {
        int n = close.Length;
        var r = NaNs(n);
        var absDiff = NaNs(n);
        for (int i = 1; i < n; i++) absDiff[i] = Math.Abs(close[i] - close[i - 1]);
        var sum = Rolling.Sum(absDiff, period);
        for (int i = period; i < n; i++) r[i] = sum[i] > 0 ? Math.Abs(close[i] - close[i - period]) / sum[i] : 0;
        return r;
    }

    /// <summary>Trend Intensity Index (Pee): share of positive deviations from SMA(n) over the last <paramref name="window"/> bars, 0..100.</summary>
    public static double[] TrendIntensity(ReadOnlySpan<double> close, int period = 60, int window = 30)
    {
        var sma = Sma(close, period);
        int n = close.Length;
        var pos = NaNs(n); var neg = NaNs(n);
        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(sma[i])) continue;
            double d = close[i] - sma[i];
            pos[i] = Math.Max(d, 0);
            neg[i] = Math.Max(-d, 0);
        }
        var sp = Rolling.Sum(pos, window);
        var sn = Rolling.Sum(neg, window);
        var r = NaNs(n);
        for (int i = 0; i < n; i++) if (sp[i] + sn[i] > 0) r[i] = 100 * sp[i] / (sp[i] + sn[i]);
        return r;
    }

    /// <summary>Polarized Fractal Efficiency (Hannula): signed straight-line vs. path length over n bars, EMA-smoothed (−100..100).</summary>
    public static double[] Pfe(ReadOnlySpan<double> close, int period = 10, int smooth = 5)
    {
        int n = close.Length;
        var raw = NaNs(n);
        for (int i = period; i < n; i++)
        {
            double d = close[i] - close[i - period];
            double path = 0;
            for (int k = i - period + 1; k <= i; k++) path += Math.Sqrt(Sq(close[k] - close[k - 1]) + 1);
            raw[i] = Math.Sign(d) * 100 * Math.Sqrt(d * d + period * period) / path;
        }
        return Ema(raw, smooth);
    }

    /// <summary>Random Walk Index (Poulos): max over k = 2..n of the move divided by the random-walk range ATR_k·√k.</summary>
    public static (double[] High, double[] Low) RandomWalkIndex(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 14)
    {
        int n = close.Length;
        var tr = TrueRange(high, low, close);
        var rh = NaNs(n); var rl = NaNs(n);
        for (int i = period; i < n; i++)
        {
            double bh = 0, bl = 0, trSum = 0;
            for (int k = 1; k <= period; k++)
            {
                trSum += tr[i - k + 1];
                if (k < 2) continue;
                double denom = trSum / k * Math.Sqrt(k);
                if (denom <= 0) continue;
                bh = Math.Max(bh, (high[i] - low[i - k]) / denom);
                bl = Math.Max(bl, (high[i - k] - low[i]) / denom);
            }
            rh[i] = bh; rl[i] = bl;
        }
        return (rh, rl);
    }

    // ---------------- Momentum ----------------

    /// <summary>Wilder-smoothed +DM and −DM (TA-Lib PLUS_DM / MINUS_DM).</summary>
    public static (double[] Plus, double[] Minus) DirectionalMovement(ReadOnlySpan<double> high, ReadOnlySpan<double> low, int period = 14)
    {
        int n = high.Length;
        var p = NaNs(n); var m = NaNs(n);
        double sp = 0, sm = 0;
        for (int i = 1; i < n; i++)
        {
            double up = high[i] - high[i - 1], down = low[i - 1] - low[i];
            double pdm = up > down && up > 0 ? up : 0, mdm = down > up && down > 0 ? down : 0;
            // Same seeding as Dmi: plain sum of the first `period` moves, then Wilder's recursion.
            if (i <= period)
            {
                sp += pdm; sm += mdm;
                if (i < period) continue;
            }
            else { sp = sp - sp / period + pdm; sm = sm - sm / period + mdm; }
            p[i] = sp; m[i] = sm;
        }
        return (p, m);
    }

    /// <summary>Rate of change as a fraction: (close − close[n]) / close[n] (TA-Lib ROCP).</summary>
    public static double[] RocFraction(ReadOnlySpan<double> x, int period = 10)
    {
        var r = NaNs(x.Length);
        for (int i = period; i < x.Length; i++) r[i] = x[i - period] != 0 ? (x[i] - x[i - period]) / x[i - period] : double.NaN;
        return r;
    }

    /// <summary>DecisionPoint Price Momentum Oscillator: double custom-smoothed 1-bar ROC ×10, with an EMA signal.</summary>
    public static (double[] Pmo, double[] Signal) Pmo(ReadOnlySpan<double> close, int first = 35, int second = 20, int signal = 10)
    {
        var roc = Roc(close, 1);
        var s1 = EmaAlpha(roc, 1, 2.0 / first);
        for (int i = 0; i < s1.Length; i++) s1[i] *= 10;
        var pmo = EmaAlpha(s1, 1, 2.0 / second);
        return (pmo, Ema(pmo, signal));
    }

    /// <summary>Relative Momentum Index: RSI computed on <paramref name="momentum"/>-bar changes instead of 1-bar changes.</summary>
    public static double[] Rmi(ReadOnlySpan<double> close, int period = 14, int momentum = 5)
    {
        int n = close.Length;
        var up = NaNs(n); var dn = NaNs(n);
        for (int i = momentum; i < n; i++)
        {
            double d = close[i] - close[i - momentum];
            up[i] = Math.Max(d, 0);
            dn[i] = Math.Max(-d, 0);
        }
        var au = Rma(up, period); var ad = Rma(dn, period);
        var r = NaNs(n);
        for (int i = 0; i < n; i++)
            if (!double.IsNaN(au[i])) r[i] = au[i] + ad[i] > 0 ? 100 * au[i] / (au[i] + ad[i]) : 50;
        return r;
    }

    /// <summary>Stochastic Momentum Index (Blau): close relative to the range midpoint, double EMA-smoothed (−100..100).</summary>
    public static (double[] Smi, double[] Signal) Smi(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 10, int smooth1 = 3, int smooth2 = 3, int signal = 10)
    {
        int n = close.Length;
        var hh = Rolling.Max(high, period); var ll = Rolling.Min(low, period);
        var rel = NaNs(n); var rng = NaNs(n);
        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(hh[i])) continue;
            rel[i] = close[i] - (hh[i] + ll[i]) / 2;
            rng[i] = hh[i] - ll[i];
        }
        var num = Ema(Ema(rel, smooth1), smooth2);
        var den = Ema(Ema(rng, smooth1), smooth2);
        var smi = NaNs(n);
        for (int i = 0; i < n; i++) if (den[i] > 0) smi[i] = 100 * num[i] / (0.5 * den[i]);
        return (smi, Ema(smi, signal));
    }

    /// <summary>Relative Volatility Index (Dorsey): RSI-style ratio of the standard deviation on up vs. down closes (0..100).</summary>
    public static double[] RelativeVolatilityIndex(ReadOnlySpan<double> close, int stdPeriod = 10, int period = 14)
    {
        int n = close.Length;
        var sd = Rolling.StdDev(close, stdPeriod, ddof: 0);
        var up = NaNs(n); var dn = NaNs(n);
        for (int i = 1; i < n; i++)
        {
            if (double.IsNaN(sd[i])) continue;
            up[i] = close[i] > close[i - 1] ? sd[i] : 0;
            dn[i] = close[i] < close[i - 1] ? sd[i] : 0;
        }
        var u = Ema(up, period); var d = Ema(dn, period);
        var r = NaNs(n);
        for (int i = 0; i < n; i++) if (!double.IsNaN(u[i])) r[i] = u[i] + d[i] > 0 ? 100 * u[i] / (u[i] + d[i]) : 50;
        return r;
    }

    /// <summary>Inertia (Dorsey): linear-regression value of the Relative Volatility Index.</summary>
    public static double[] Inertia(ReadOnlySpan<double> close, int period = 20, int rviPeriod = 14)
        => LinearRegression(RelativeVolatilityIndex(close, 10, rviPeriod), period).Value;

    /// <summary>Psychological line: percent of the last n bars that closed up.</summary>
    public static double[] PsychologicalLine(ReadOnlySpan<double> close, int period = 12)
    {
        int n = close.Length;
        var up = NaNs(n);
        for (int i = 1; i < n; i++) up[i] = close[i] > close[i - 1] ? 100 : 0;
        return Rolling.Mean(up, period);
    }

    /// <summary>Intraday Momentum Index (Chande): RSI built from candle bodies (close − open), 0..100.</summary>
    public static double[] IntradayMomentum(ReadOnlySpan<double> open, ReadOnlySpan<double> close, int period = 14)
    {
        int n = close.Length;
        var g = new double[n]; var l = new double[n];
        for (int i = 0; i < n; i++) { double d = close[i] - open[i]; g[i] = Math.Max(d, 0); l[i] = Math.Max(-d, 0); }
        var sg = Rolling.Sum(g, period); var sl = Rolling.Sum(l, period);
        var r = NaNs(n);
        for (int i = 0; i < n; i++) if (!double.IsNaN(sg[i])) r[i] = sg[i] + sl[i] > 0 ? 100 * sg[i] / (sg[i] + sl[i]) : 50;
        return r;
    }

    /// <summary>Pretty Good Oscillator (Johnson): distance from SMA(n) in units of EMA(true range, n).</summary>
    public static double[] PrettyGoodOscillator(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 14)
    {
        var sma = Sma(close, period);
        var atr = Ema(TrueRange(high, low, close), period);
        var r = NaNs(close.Length);
        for (int i = 0; i < r.Length; i++) if (atr[i] > 0) r[i] = (close[i] - sma[i]) / atr[i];
        return r;
    }

    /// <summary>Chande Forecast Oscillator: percent gap between close and the time-series forecast.</summary>
    public static double[] ForecastOscillator(ReadOnlySpan<double> close, int period = 14)
    {
        var tsf = LinearRegression(close, period).Forecast;
        var r = NaNs(close.Length);
        for (int i = 0; i < r.Length; i++) if (close[i] != 0 && !double.IsNaN(tsf[i])) r[i] = 100 * (close[i] - tsf[i]) / close[i];
        return r;
    }

    /// <summary>BIAS / disparity index: percent distance of close from its SMA.</summary>
    public static double[] Bias(ReadOnlySpan<double> close, int period = 20)
    {
        var sma = Sma(close, period);
        var r = NaNs(close.Length);
        for (int i = 0; i < r.Length; i++) if (sma[i] != 0 && !double.IsNaN(sma[i])) r[i] = 100 * (close[i] - sma[i]) / sma[i];
        return r;
    }

    /// <summary>Double Smoothed Stochastic (Bressert): EMA-smoothed stochastic of an EMA-smoothed stochastic, 0..100.</summary>
    public static double[] DoubleSmoothedStochastic(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 13, int smooth = 8)
    {
        int n = close.Length;
        var hh = Rolling.Max(high, period); var ll = Rolling.Min(low, period);
        var k1 = NaNs(n);
        for (int i = 0; i < n; i++) if (hh[i] > ll[i]) k1[i] = 100 * (close[i] - ll[i]) / (hh[i] - ll[i]); else if (!double.IsNaN(hh[i])) k1[i] = 50;
        var e1 = Ema(k1, smooth);
        var h2 = Rolling.Max(e1, period); var l2 = Rolling.Min(e1, period);
        var k2 = NaNs(n);
        for (int i = 0; i < n; i++) if (h2[i] > l2[i]) k2[i] = 100 * (e1[i] - l2[i]) / (h2[i] - l2[i]); else if (!double.IsNaN(h2[i])) k2[i] = 50;
        return Ema(k2, smooth);
    }

    /// <summary>WaveTrend oscillator (LazyBear): channel index of HLC3, EMA-smoothed, with a 4-bar SMA signal.</summary>
    public static (double[] Wt1, double[] Wt2) WaveTrend(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int channel = 10, int average = 21)
    {
        var ap = TypicalPrice(high, low, close);
        var esa = Ema(ap, channel);
        int n = ap.Length;
        var dev = NaNs(n);
        for (int i = 0; i < n; i++) if (!double.IsNaN(esa[i])) dev[i] = Math.Abs(ap[i] - esa[i]);
        var d = Ema(dev, channel);
        var ci = NaNs(n);
        for (int i = 0; i < n; i++) if (d[i] > 0) ci[i] = (ap[i] - esa[i]) / (0.015 * d[i]);
        var wt1 = Ema(ci, average);
        return (wt1, Sma(wt1, 4));
    }

    /// <summary>KDJ: slow stochastic %K and %D plus J = 3K − 2D.</summary>
    public static (double[] K, double[] D, double[] J) Kdj(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 9, int smooth = 3, int signal = 3)
    {
        var (k, d) = Stochastic(high, low, close, period, smooth, signal);
        var j = new double[k.Length];
        for (int i = 0; i < j.Length; i++) j[i] = 3 * k[i] - 2 * d[i];
        return (k, d, j);
    }

    /// <summary>Williams Accumulation/Distribution: cumulative close minus true low (up bars) or true high (down bars).</summary>
    public static double[] WilliamsAd(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close)
    {
        int n = close.Length;
        var r = NaNs(n);
        if (n == 0) return r;
        double acc = 0;
        r[0] = 0;
        for (int i = 1; i < n; i++)
        {
            double trh = Math.Max(high[i], close[i - 1]), trl = Math.Min(low[i], close[i - 1]);
            if (close[i] > close[i - 1]) acc += close[i] - trl;
            else if (close[i] < close[i - 1]) acc += close[i] - trh;
            r[i] = acc;
        }
        return r;
    }

    /// <summary>
    /// TTM-style squeeze: Squeeze = 1 while the Bollinger Bands sit inside the Keltner Channels, and momentum = linear
    /// regression of close minus the mean of the Donchian midpoint and the SMA.
    /// </summary>
    public static (double[] Squeeze, double[] Momentum) Squeeze(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 20, double bbK = 2, double kcK = 1.5)
    {
        int n = close.Length;
        var bb = Bollinger(close, period, bbK);
        var sma = bb.Middle;
        var atr = Rma(TrueRange(high, low, close), period);
        var hh = Rolling.Max(high, period); var ll = Rolling.Min(low, period);
        var sq = NaNs(n); var delta = NaNs(n);
        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(sma[i]) || double.IsNaN(atr[i])) continue;
            sq[i] = bb.Upper[i] < sma[i] + kcK * atr[i] && bb.Lower[i] > sma[i] - kcK * atr[i] ? 1 : 0;
            delta[i] = close[i] - ((hh[i] + ll[i]) / 2 + sma[i]) / 2;
        }
        return (sq, LinearRegression(delta, period).Value);
    }

    // ---------------- Volatility ----------------

    /// <summary>Yang-Zhang volatility (annualised %, 252 bars/year): overnight + k·open-to-close + (1−k)·Rogers-Satchell variance.</summary>
    public static double[] YangZhangVolatility(ReadOnlySpan<double> open, ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 20, double periodsPerYear = 252)
    {
        int n = close.Length;
        var ov = NaNs(n); var oc = NaNs(n); var rs = NaNs(n);
        for (int i = 1; i < n; i++)
        {
            ov[i] = Math.Log(open[i] / close[i - 1]);
            oc[i] = Math.Log(close[i] / open[i]);
            rs[i] = Math.Log(high[i] / close[i]) * Math.Log(high[i] / open[i]) + Math.Log(low[i] / close[i]) * Math.Log(low[i] / open[i]);
        }
        var vo = Rolling.Variance(ov, period, ddof: 1);
        var vc = Rolling.Variance(oc, period, ddof: 1);
        var mrs = Rolling.Mean(rs, period);
        double k = 0.34 / (1.34 + (period + 1.0) / (period - 1.0));
        var r = NaNs(n);
        for (int i = 0; i < n; i++)
            if (!double.IsNaN(vo[i])) r[i] = 100 * Math.Sqrt(Math.Max(0, (vo[i] + k * vc[i] + (1 - k) * mrs[i]) * periodsPerYear));
        return r;
    }

    /// <summary>EWMA (RiskMetrics) volatility of log returns, annualised %.</summary>
    public static double[] EwmaVolatility(ReadOnlySpan<double> close, double lambda = 0.94, double periodsPerYear = 252)
    {
        int n = close.Length;
        var r = NaNs(n);
        if (n < 2) return r;
        var ret = new double[n - 1];
        for (int i = 1; i < n; i++) ret[i - 1] = Math.Log(close[i] / close[i - 1]);
        var v = Rolling.EwmaVolatility(ret, lambda);
        for (int i = 0; i < v.Length; i++) r[i + 1] = v[i] * 100 * Math.Sqrt(periodsPerYear);
        return r;
    }

    /// <summary>STARC bands (Stoller): SMA(close) ± k·ATR.</summary>
    public static (double[] Upper, double[] Middle, double[] Lower) Starc(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 6, int atrPeriod = 15, double k = 2)
    {
        var mid = Sma(close, period);
        var atr = Atr(high, low, close, atrPeriod);
        var up = new double[mid.Length]; var lo = new double[mid.Length];
        for (int i = 0; i < up.Length; i++) { up[i] = mid[i] + k * atr[i]; lo[i] = mid[i] - k * atr[i]; }
        return (up, mid, lo);
    }

    /// <summary>Acceleration bands (Headley): SMAs of high·(1 + 4·w) and low·(1 − 4·w), w = (H − L)/(H + L).</summary>
    public static (double[] Upper, double[] Middle, double[] Lower) AccelerationBands(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 20, double factor = 4)
    {
        int n = close.Length;
        var u = new double[n]; var l = new double[n];
        for (int i = 0; i < n; i++)
        {
            double w = high[i] + low[i] != 0 ? (high[i] - low[i]) / (high[i] + low[i]) : 0;
            u[i] = high[i] * (1 + factor * w);
            l[i] = low[i] * (1 - factor * w);
        }
        return (Sma(u, period), Sma(close, period), Sma(l, period));
    }

    /// <summary>Standard error of the rolling linear-regression fit: √(Σresidual² / (n − 2)).</summary>
    public static double[] StandardError(ReadOnlySpan<double> x, int period = 20)
    {
        var lr = LinearRegression(x, period);
        var r = NaNs(x.Length);
        for (int i = period - 1; i < x.Length; i++)
        {
            if (double.IsNaN(lr.Slope[i])) continue;
            double ss = 0;
            for (int j = 0; j < period; j++)
            {
                double fit = lr.Intercept[i] + lr.Slope[i] * j;
                ss += Sq(x[i - period + 1 + j] - fit);
            }
            r[i] = Math.Sqrt(ss / Math.Max(1, period - 2));
        }
        return r;
    }

    /// <summary>Standard-error bands: linear-regression value ± k·standard error.</summary>
    public static (double[] Upper, double[] Middle, double[] Lower) StandardErrorBands(ReadOnlySpan<double> x, int period = 21, double k = 2)
    {
        var mid = LinearRegression(x, period).Value;
        var se = StandardError(x, period);
        var up = new double[mid.Length]; var lo = new double[mid.Length];
        for (int i = 0; i < up.Length; i++) { up[i] = mid[i] + k * se[i]; lo[i] = mid[i] - k * se[i]; }
        return (up, mid, lo);
    }

    /// <summary>Average daily range: SMA of high − low.</summary>
    public static double[] AverageRange(ReadOnlySpan<double> high, ReadOnlySpan<double> low, int period = 14)
    {
        var rng = new double[high.Length];
        for (int i = 0; i < rng.Length; i++) rng[i] = high[i] - low[i];
        return Sma(rng, period);
    }

    // ---------------- Volume ----------------

    /// <summary>Twiggs Money Flow: Wilder-smoothed true-range A/D volume over smoothed volume (−1..1).</summary>
    public static double[] TwiggsMoneyFlow(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, ReadOnlySpan<double> volume, int period = 21)
    {
        int n = close.Length;
        var ad = NaNs(n); var vol = NaNs(n);
        for (int i = 1; i < n; i++)
        {
            double th = Math.Max(high[i], close[i - 1]), tl = Math.Min(low[i], close[i - 1]);
            ad[i] = th > tl ? volume[i] * ((close[i] - tl) - (th - close[i])) / (th - tl) : 0;
            vol[i] = volume[i];
        }
        var sa = Rma(ad, period); var sv = Rma(vol, period);
        var r = NaNs(n);
        for (int i = 0; i < n; i++) if (sv[i] > 0) r[i] = sa[i] / sv[i];
        return r;
    }

    /// <summary>Bill Williams Market Facilitation Index: (high − low) / volume.</summary>
    public static double[] MarketFacilitation(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> volume)
    {
        var r = NaNs(high.Length);
        for (int i = 0; i < r.Length; i++) if (volume[i] > 0) r[i] = (high[i] - low[i]) / volume[i];
        return r;
    }

    /// <summary>Volume Zone Oscillator (Khalil and Steckler): EMA of signed volume over EMA of volume, ×100.</summary>
    public static double[] VolumeZoneOscillator(ReadOnlySpan<double> close, ReadOnlySpan<double> volume, int period = 14)
    {
        int n = close.Length;
        var sv = NaNs(n); var v = NaNs(n);
        for (int i = 1; i < n; i++) { sv[i] = Math.Sign(close[i] - close[i - 1]) * volume[i]; v[i] = volume[i]; }
        var es = Ema(sv, period); var ev = Ema(v, period);
        var r = NaNs(n);
        for (int i = 0; i < n; i++) if (ev[i] > 0) r[i] = 100 * es[i] / ev[i];
        return r;
    }

    /// <summary>Relative volume: volume over the mean volume of the preceding n bars.</summary>
    public static double[] RelativeVolume(ReadOnlySpan<double> volume, int period = 20)
    {
        var avg = Rolling.Shift(Rolling.Mean(volume, period), 1);
        var r = NaNs(volume.Length);
        for (int i = 0; i < r.Length; i++) if (avg[i] > 0) r[i] = volume[i] / avg[i];
        return r;
    }

    /// <summary>Volume-weighted MACD: VWMA(fast) − VWMA(slow), EMA signal and histogram.</summary>
    public static (double[] Macd, double[] Signal, double[] Histogram) VwMacd(ReadOnlySpan<double> close, ReadOnlySpan<double> volume, int fast = 12, int slow = 26, int signal = 9)
    {
        var f = Vwma(close, volume, fast);
        var s = Vwma(close, volume, slow);
        var m = new double[f.Length];
        for (int i = 0; i < m.Length; i++) m[i] = f[i] - s[i];
        var sig = Ema(m, signal);
        var h = new double[m.Length];
        for (int i = 0; i < h.Length; i++) h[i] = m[i] - sig[i];
        return (m, sig, h);
    }

    /// <summary>Volume Price Confirmation Indicator (Dormeier): (VWMA−SMA)_long · (VWMA/SMA)_short · (Vavg_short/Vavg_long).</summary>
    public static double[] Vpci(ReadOnlySpan<double> close, ReadOnlySpan<double> volume, int shortPeriod = 5, int longPeriod = 20)
    {
        var vwL = Vwma(close, volume, longPeriod); var smL = Sma(close, longPeriod);
        var vwS = Vwma(close, volume, shortPeriod); var smS = Sma(close, shortPeriod);
        var vS = Sma(volume, shortPeriod); var vL = Sma(volume, longPeriod);
        var r = NaNs(close.Length);
        for (int i = 0; i < r.Length; i++)
            if (smS[i] != 0 && vL[i] > 0 && !double.IsNaN(smL[i])) r[i] = (vwL[i] - smL[i]) * (vwS[i] / smS[i]) * (vS[i] / vL[i]);
        return r;
    }

    /// <summary>
    /// Rolling volume profile over <paramref name="period"/> bars: each bar's volume is spread evenly over the price bins its
    /// high-low range touches; returns the point of control and the <paramref name="valueArea"/> value area.
    /// </summary>
    public static ProfileResult VolumeProfile(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> volume, int period = 50, int bins = 24, double valueArea = 0.70)
        => Profile(high, low, volume, period, bins, valueArea);

    /// <summary>
    /// Rolling market profile (TPO): like <see cref="VolumeProfile"/> but every bar counts one time-price opportunity per bin
    /// it touches, regardless of volume.
    /// </summary>
    public static ProfileResult MarketProfile(ReadOnlySpan<double> high, ReadOnlySpan<double> low, int period = 50, int bins = 24, double valueArea = 0.70)
        => Profile(high, low, default, period, bins, valueArea);

    private static ProfileResult Profile(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> volume, int period, int bins, double valueArea)
    {
        int n = high.Length;
        var poc = NaNs(n); var vah = NaNs(n); var val = NaNs(n);
        bool tpo = volume.IsEmpty;
        var hist = new double[bins];
        for (int i = period - 1; i < n; i++)
        {
            double hi = double.MinValue, lo = double.MaxValue;
            for (int k = i - period + 1; k <= i; k++) { hi = Math.Max(hi, high[k]); lo = Math.Min(lo, low[k]); }
            if (!(hi > lo)) continue;
            double width = (hi - lo) / bins;
            Array.Clear(hist);
            double total = 0;
            for (int k = i - period + 1; k <= i; k++)
            {
                int b0 = Math.Clamp((int)((low[k] - lo) / width), 0, bins - 1);
                int b1 = Math.Clamp((int)((high[k] - lo) / width), 0, bins - 1);
                double w = tpo ? 1 : volume[k] / (b1 - b0 + 1);
                for (int b = b0; b <= b1; b++) { hist[b] += w; total += w; }
            }
            int pocBin = 0;
            for (int b = 1; b < bins; b++) if (hist[b] > hist[pocBin]) pocBin = b;
            // Grow the value area from the POC towards the heavier neighbour until it holds valueArea of the total.
            int a = pocBin, z = pocBin;
            double acc = hist[pocBin];
            while (acc < valueArea * total && (a > 0 || z < bins - 1))
            {
                double below = a > 0 ? hist[a - 1] : -1, above = z < bins - 1 ? hist[z + 1] : -1;
                if (above >= below) acc += hist[++z]; else acc += hist[--a];
            }
            poc[i] = lo + (pocBin + 0.5) * width;
            vah[i] = lo + (z + 1) * width;
            val[i] = lo + a * width;
        }
        return new(poc, vah, val);
    }

    // ---------------- Statistics ----------------

    /// <summary>R² of the rolling linear regression of the series on time.</summary>
    public static double[] RSquared(ReadOnlySpan<double> x, int period = 20)
    {
        var c = TrendCorrelation(x, period);
        for (int i = 0; i < c.Length; i++) c[i] *= c[i];
        return c;
    }

    /// <summary>Mean absolute deviation around the rolling mean.</summary>
    public static double[] MeanAbsoluteDeviation(ReadOnlySpan<double> x, int period = 20)
    {
        var m = Rolling.Mean(x, period);
        var r = NaNs(x.Length);
        for (int i = 0; i < r.Length; i++)
        {
            if (double.IsNaN(m[i])) continue;
            double s = 0;
            for (int k = i - period + 1; k <= i; k++) s += Math.Abs(x[k] - m[i]);
            r[i] = s / period;
        }
        return r;
    }

    /// <summary>Bars since the highest (<paramref name="max"/> = true) or lowest value in the window.</summary>
    public static double[] BarsSinceExtreme(ReadOnlySpan<double> x, int period = 14, bool max = true)
    {
        var idx = max ? Rolling.ArgMax(x, period) : Rolling.ArgMin(x, period);
        var r = NaNs(x.Length);
        for (int i = 0; i < r.Length; i++) if (idx[i] >= 0) r[i] = i - idx[i];
        return r;
    }

    /// <summary>Shannon entropy (bits) of the window normalised to a probability distribution (positive inputs).</summary>
    public static double[] Entropy(ReadOnlySpan<double> x, int period = 10)
    {
        var r = NaNs(x.Length);
        int s = Rolling.FirstValid(x);
        for (int i = s + period - 1; i < x.Length; i++)
        {
            double sum = 0;
            for (int k = i - period + 1; k <= i; k++) sum += x[k];
            if (sum <= 0) continue;
            double h = 0;
            for (int k = i - period + 1; k <= i; k++)
            {
                double p = x[k] / sum;
                if (p > 0) h -= p * Math.Log2(p);
            }
            r[i] = h;
        }
        return r;
    }

    /// <summary>Rolling lag-<paramref name="lag"/> autocorrelation of log returns.</summary>
    public static double[] Autocorrelation(ReadOnlySpan<double> close, int period = 30, int lag = 1)
    {
        int n = close.Length;
        var ret = NaNs(n);
        for (int i = 1; i < n; i++) ret[i] = Math.Log(close[i] / close[i - 1]);
        var lagged = Rolling.Shift(ret, lag);
        return Rolling.Correlation(ret, lagged, period);
    }

    /// <summary>Coefficient of variation: rolling sample standard deviation over the rolling mean.</summary>
    public static double[] CoefficientOfVariation(ReadOnlySpan<double> x, int period = 20)
    {
        var m = Rolling.Mean(x, period);
        var sd = Rolling.StdDev(x, period);
        var r = NaNs(x.Length);
        for (int i = 0; i < r.Length; i++) if (m[i] != 0 && !double.IsNaN(m[i])) r[i] = sd[i] / m[i];
        return r;
    }

    /// <summary>Rolling quantile (linear interpolation between order statistics, as numpy's default).</summary>
    public static double[] RollingQuantile(ReadOnlySpan<double> x, int period = 20, double q = 0.5)
    {
        var r = NaNs(x.Length);
        int s = Rolling.FirstValid(x);
        var buf = new double[period];
        for (int i = s + period - 1; i < x.Length; i++)
        {
            x.Slice(i - period + 1, period).CopyTo(buf);
            Array.Sort(buf);
            double pos = q * (period - 1);
            int lo = (int)Math.Floor(pos), hi = Math.Min(period - 1, lo + 1);
            r[i] = buf[lo] + (pos - lo) * (buf[hi] - buf[lo]);
        }
        return r;
    }

    /// <summary>Rolling annualised Sharpe ratio of log returns (zero risk-free rate).</summary>
    public static double[] RollingSharpe(ReadOnlySpan<double> close, int period = 63, double periodsPerYear = 252)
    {
        int n = close.Length;
        var ret = NaNs(n);
        for (int i = 1; i < n; i++) ret[i] = Math.Log(close[i] / close[i - 1]);
        var m = Rolling.Mean(ret, period);
        var sd = Rolling.StdDev(ret, period);
        var r = NaNs(n);
        for (int i = 0; i < n; i++) if (sd[i] > 0) r[i] = m[i] / sd[i] * Math.Sqrt(periodsPerYear);
        return r;
    }

    /// <summary>Drawdown from the running maximum, in percent (≤ 0).</summary>
    public static double[] Drawdown(ReadOnlySpan<double> close)
    {
        var r = NaNs(close.Length);
        double peak = double.MinValue;
        for (int i = 0; i < r.Length; i++)
        {
            if (double.IsNaN(close[i])) continue;
            peak = Math.Max(peak, close[i]);
            r[i] = peak > 0 ? 100 * (close[i] / peak - 1) : 0;
        }
        return r;
    }

    /// <summary>One-bar log return.</summary>
    public static double[] LogReturn(ReadOnlySpan<double> close)
    {
        var r = NaNs(close.Length);
        for (int i = 1; i < r.Length; i++) r[i] = Math.Log(close[i] / close[i - 1]);
        return r;
    }

    private static double Sq(double v) => v * v;
}
