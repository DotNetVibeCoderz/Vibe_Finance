namespace Finance.TimeSeries.Indicators;

public static partial class Ta
{
    /// <summary>Relative Strength Index with Wilder smoothing (first average is a simple mean).</summary>
    public static double[] Rsi(ReadOnlySpan<double> x, int period = 14)
    {
        var r = NaNs(x.Length);
        int s = Rolling.FirstValid(x);
        if (x.Length - s <= period) return r;
        double gain = 0, loss = 0;
        for (int i = s + 1; i <= s + period; i++)
        {
            double d = x[i] - x[i - 1];
            if (d > 0) gain += d; else loss -= d;
        }
        gain /= period;
        loss /= period;
        r[s + period] = RsiValue(gain, loss);
        for (int i = s + period + 1; i < x.Length; i++)
        {
            double d = x[i] - x[i - 1];
            gain = (gain * (period - 1) + Math.Max(d, 0)) / period;
            loss = (loss * (period - 1) + Math.Max(-d, 0)) / period;
            r[i] = RsiValue(gain, loss);
        }
        return r;
    }

    private static double RsiValue(double gain, double loss)
        => loss == 0 ? (gain == 0 ? 50 : 100) : 100 - 100 / (1 + gain / loss);

    /// <summary>Stochastic RSI: stochastic oscillator applied to RSI, smoothed %K and %D.</summary>
    public static (double[] K, double[] D) StochRsi(ReadOnlySpan<double> x, int rsiPeriod = 14, int stochPeriod = 14, int kSmooth = 3, int dSmooth = 3)
    {
        var rsi = Rsi(x, rsiPeriod);
        var hi = Rolling.Max(rsi, stochPeriod);
        var lo = Rolling.Min(rsi, stochPeriod);
        var raw = NaNs(x.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            double range = hi[i] - lo[i];
            if (!double.IsNaN(range)) raw[i] = range > 0 ? 100 * (rsi[i] - lo[i]) / range : 0;
        }
        var k = Sma(raw, kSmooth);
        return (k, Sma(k, dSmooth));
    }

    /// <summary>Stochastic oscillator: raw %K over <paramref name="kPeriod"/>, smoothed by <paramref name="kSmooth"/>; %D = SMA(%K).</summary>
    public static (double[] K, double[] D) Stochastic(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int kPeriod = 14, int kSmooth = 3, int dPeriod = 3)
    {
        var hh = Rolling.Max(high, kPeriod);
        var ll = Rolling.Min(low, kPeriod);
        var raw = NaNs(close.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            double range = hh[i] - ll[i];
            if (!double.IsNaN(range)) raw[i] = range > 0 ? 100 * (close[i] - ll[i]) / range : 50;
        }
        var k = kSmooth > 1 ? Sma(raw, kSmooth) : raw;
        return (k, Sma(k, dPeriod));
    }

    /// <summary>Williams %R in [-100, 0].</summary>
    public static double[] WilliamsR(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 14)
    {
        var hh = Rolling.Max(high, period);
        var ll = Rolling.Min(low, period);
        var r = NaNs(close.Length);
        for (int i = 0; i < r.Length; i++)
        {
            double range = hh[i] - ll[i];
            if (!double.IsNaN(range)) r[i] = range > 0 ? -100 * (hh[i] - close[i]) / range : -50;
        }
        return r;
    }

    /// <summary>Commodity Channel Index: (TP - SMA(TP)) / (0.015 · mean absolute deviation).</summary>
    public static double[] Cci(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 20)
    {
        var tp = TypicalPrice(high, low, close);
        var sma = Sma(tp, period);
        var r = NaNs(tp.Length);
        for (int i = period - 1; i < tp.Length; i++)
        {
            double mad = 0;
            for (int k = i - period + 1; k <= i; k++) mad += Math.Abs(tp[k] - sma[i]);
            mad /= period;
            r[i] = mad > 0 ? (tp[i] - sma[i]) / (0.015 * mad) : 0;
        }
        return r;
    }

    /// <summary>Rate of change in percent: 100·(x[t]/x[t-n] - 1).</summary>
    public static double[] Roc(ReadOnlySpan<double> x, int period = 10)
    {
        var r = NaNs(x.Length);
        for (int i = period; i < x.Length; i++) r[i] = 100 * (x[i] / x[i - period] - 1);
        return r;
    }

    /// <summary>Rate of change ratio x[t]/x[t-n].</summary>
    public static double[] RocRatio(ReadOnlySpan<double> x, int period = 10)
    {
        var r = NaNs(x.Length);
        for (int i = period; i < x.Length; i++) r[i] = x[i] / x[i - period];
        return r;
    }

    /// <summary>Momentum x[t] - x[t-n].</summary>
    public static double[] Momentum(ReadOnlySpan<double> x, int period = 10) => Rolling.Diff(x, period);

    /// <summary>TRIX: 1-period percent change of a triple-smoothed EMA.</summary>
    public static double[] Trix(ReadOnlySpan<double> x, int period = 15)
    {
        var e3 = Ema(Ema(Ema(x, period), period), period);
        var r = NaNs(x.Length);
        for (int i = 1; i < x.Length; i++) r[i] = 100 * (e3[i] / e3[i - 1] - 1);
        return r;
    }

    /// <summary>Ultimate Oscillator (Williams) over 7/14/28 periods.</summary>
    public static double[] UltimateOscillator(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int p1 = 7, int p2 = 14, int p3 = 28)
    {
        int n = close.Length;
        var bp = NaNs(n);
        var tr = NaNs(n);
        for (int i = 1; i < n; i++)
        {
            double lowTrue = Math.Min(low[i], close[i - 1]), highTrue = Math.Max(high[i], close[i - 1]);
            bp[i] = close[i] - lowTrue;
            tr[i] = highTrue - lowTrue;
        }
        var b1 = Rolling.Sum(bp, p1); var t1 = Rolling.Sum(tr, p1);
        var b2 = Rolling.Sum(bp, p2); var t2 = Rolling.Sum(tr, p2);
        var b3 = Rolling.Sum(bp, p3); var t3 = Rolling.Sum(tr, p3);
        var r = NaNs(n);
        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(b3[i]) || t1[i] == 0 || t2[i] == 0 || t3[i] == 0) continue;
            r[i] = 100 * (4 * b1[i] / t1[i] + 2 * b2[i] / t2[i] + b3[i] / t3[i]) / 7;
        }
        return r;
    }

    /// <summary>Awesome Oscillator: SMA5(median) - SMA34(median).</summary>
    public static double[] AwesomeOscillator(ReadOnlySpan<double> high, ReadOnlySpan<double> low, int fast = 5, int slow = 34)
    {
        var mp = MedianPrice(high, low);
        var f = Sma(mp, fast);
        var s = Sma(mp, slow);
        for (int i = 0; i < f.Length; i++) f[i] -= s[i];
        return f;
    }

    /// <summary>Accelerator Oscillator: AO - SMA5(AO).</summary>
    public static double[] AcceleratorOscillator(ReadOnlySpan<double> high, ReadOnlySpan<double> low)
    {
        var ao = AwesomeOscillator(high, low);
        var s = Sma(ao, 5);
        for (int i = 0; i < ao.Length; i++) ao[i] -= s[i];
        return ao;
    }

    /// <summary>MACD line, signal and histogram.</summary>
    public static (double[] Macd, double[] Signal, double[] Histogram) Macd(ReadOnlySpan<double> x, int fast = 12, int slow = 26, int signal = 9)
    {
        var f = Ema(x, fast);
        var s = Ema(x, slow);
        var line = new double[x.Length];
        for (int i = 0; i < line.Length; i++) line[i] = f[i] - s[i];
        var sig = Ema(line, signal);
        var hist = new double[x.Length];
        for (int i = 0; i < hist.Length; i++) hist[i] = line[i] - sig[i];
        return (line, sig, hist);
    }

    /// <summary>Percentage Price Oscillator with signal and histogram.</summary>
    public static (double[] Ppo, double[] Signal, double[] Histogram) Ppo(ReadOnlySpan<double> x, int fast = 12, int slow = 26, int signal = 9)
    {
        var f = Ema(x, fast);
        var s = Ema(x, slow);
        var line = new double[x.Length];
        for (int i = 0; i < line.Length; i++) line[i] = 100 * (f[i] - s[i]) / s[i];
        var sig = Ema(line, signal);
        var hist = new double[x.Length];
        for (int i = 0; i < hist.Length; i++) hist[i] = line[i] - sig[i];
        return (line, sig, hist);
    }

    /// <summary>Absolute Price Oscillator: EMA(fast) - EMA(slow).</summary>
    public static double[] Apo(ReadOnlySpan<double> x, int fast = 12, int slow = 26)
    {
        var f = Ema(x, fast);
        var s = Ema(x, slow);
        for (int i = 0; i < f.Length; i++) f[i] -= s[i];
        return f;
    }

    /// <summary>Chande Momentum Oscillator in [-100, 100].</summary>
    public static double[] Cmo(ReadOnlySpan<double> x, int period = 14)
    {
        int n = x.Length;
        var up = NaNs(n);
        var dn = NaNs(n);
        int s = Rolling.FirstValid(x);
        for (int i = s + 1; i < n; i++)
        {
            double d = x[i] - x[i - 1];
            up[i] = Math.Max(d, 0);
            dn[i] = Math.Max(-d, 0);
        }
        var su = Rolling.Sum(up, period);
        var sd = Rolling.Sum(dn, period);
        var r = NaNs(n);
        for (int i = 0; i < n; i++)
        {
            double t = su[i] + sd[i];
            if (!double.IsNaN(t)) r[i] = t > 0 ? 100 * (su[i] - sd[i]) / t : 0;
        }
        return r;
    }

    /// <summary>Detrended Price Oscillator: x[t - (n/2+1)] - SMA(n)[t] (non-centred form, usable in real time).</summary>
    public static double[] Dpo(ReadOnlySpan<double> x, int period = 20)
    {
        var sma = Sma(x, period);
        int shift = period / 2 + 1;
        var r = NaNs(x.Length);
        for (int i = Math.Max(shift, period - 1); i < x.Length; i++) r[i] = x[i - shift] - sma[i];
        return r;
    }

    /// <summary>Know Sure Thing (Pring) with its 9-period signal.</summary>
    public static (double[] Kst, double[] Signal) Kst(ReadOnlySpan<double> x)
    {
        var r1 = Sma(Roc(x, 10), 10);
        var r2 = Sma(Roc(x, 15), 10);
        var r3 = Sma(Roc(x, 20), 10);
        var r4 = Sma(Roc(x, 30), 15);
        var k = new double[x.Length];
        for (int i = 0; i < k.Length; i++) k[i] = r1[i] + 2 * r2[i] + 3 * r3[i] + 4 * r4[i];
        return (k, Sma(k, 9));
    }

    /// <summary>True Strength Index: double-smoothed momentum over double-smoothed |momentum|.</summary>
    public static (double[] Tsi, double[] Signal) Tsi(ReadOnlySpan<double> x, int longPeriod = 25, int shortPeriod = 13, int signal = 7)
    {
        var m = Rolling.Diff(x, 1);
        var a = new double[m.Length];
        for (int i = 0; i < a.Length; i++) a[i] = Math.Abs(m[i]);
        var num = Ema(Ema(m, longPeriod), shortPeriod);
        var den = Ema(Ema(a, longPeriod), shortPeriod);
        var t = NaNs(x.Length);
        for (int i = 0; i < t.Length; i++) if (den[i] > 0) t[i] = 100 * num[i] / den[i];
        return (t, Ema(t, signal));
    }

    /// <summary>Balance of Power: (close - open) / (high - low).</summary>
    public static double[] BalanceOfPower(ReadOnlySpan<double> open, ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close)
    {
        var r = new double[close.Length];
        for (int i = 0; i < r.Length; i++)
        {
            double range = high[i] - low[i];
            r[i] = range > 0 ? (close[i] - open[i]) / range : 0;
        }
        return r;
    }

    /// <summary>Coppock curve: WMA10(ROC14 + ROC11) (monthly-data periods by default).</summary>
    public static double[] Coppock(ReadOnlySpan<double> x, int wmaPeriod = 10, int longRoc = 14, int shortRoc = 11)
    {
        var a = Roc(x, longRoc);
        var b = Roc(x, shortRoc);
        for (int i = 0; i < a.Length; i++) a[i] += b[i];
        return Wma(a, wmaPeriod);
    }

    /// <summary>Ehlers Fisher transform of the median price, and its one-bar trigger.</summary>
    public static (double[] Fisher, double[] Trigger) FisherTransform(ReadOnlySpan<double> high, ReadOnlySpan<double> low, int period = 10)
    {
        var mp = MedianPrice(high, low);
        var hh = Rolling.Max(mp, period);
        var ll = Rolling.Min(mp, period);
        var f = NaNs(mp.Length);
        var trig = NaNs(mp.Length);
        double value = 0, fish = 0;
        for (int i = period - 1; i < mp.Length; i++)
        {
            double range = hh[i] - ll[i];
            double raw = range > 0 ? (mp[i] - ll[i]) / range - 0.5 : 0;
            value = Math.Clamp(0.66 * raw * 2 + 0.67 * value, -0.999, 0.999);
            double prev = fish;
            fish = 0.5 * Math.Log((1 + value) / (1 - value)) + 0.5 * fish;
            f[i] = fish;
            trig[i] = prev;
        }
        return (f, trig);
    }

    /// <summary>Relative Vigor Index and its signal line.</summary>
    public static (double[] Rvi, double[] Signal) RelativeVigor(ReadOnlySpan<double> open, ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 10)
    {
        int n = close.Length;
        var num = NaNs(n);
        var den = NaNs(n);
        for (int i = 3; i < n; i++)
        {
            num[i] = ((close[i] - open[i]) + 2 * (close[i - 1] - open[i - 1]) + 2 * (close[i - 2] - open[i - 2]) + (close[i - 3] - open[i - 3])) / 6;
            den[i] = ((high[i] - low[i]) + 2 * (high[i - 1] - low[i - 1]) + 2 * (high[i - 2] - low[i - 2]) + (high[i - 3] - low[i - 3])) / 6;
        }
        var sn = Sma(num, period);
        var sd = Sma(den, period);
        var rvi = NaNs(n);
        for (int i = 0; i < n; i++) if (sd[i] != 0 && !double.IsNaN(sd[i])) rvi[i] = sn[i] / sd[i];
        var sig = NaNs(n);
        for (int i = 3; i < n; i++)
            if (!double.IsNaN(rvi[i - 3])) sig[i] = (rvi[i] + 2 * rvi[i - 1] + 2 * rvi[i - 2] + rvi[i - 3]) / 6;
        return (rvi, sig);
    }

    /// <summary>Elder ray: bull power = high - EMA, bear power = low - EMA.</summary>
    public static (double[] BullPower, double[] BearPower) ElderRay(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 13)
    {
        var e = Ema(close, period);
        var bull = new double[close.Length];
        var bear = new double[close.Length];
        for (int i = 0; i < e.Length; i++) { bull[i] = high[i] - e[i]; bear[i] = low[i] - e[i]; }
        return (bull, bear);
    }

    /// <summary>Schaff Trend Cycle: double stochastic of the MACD line, smoothed with factor 0.5.</summary>
    public static double[] SchaffTrendCycle(ReadOnlySpan<double> x, int cycle = 10, int fast = 23, int slow = 50)
    {
        var macd = Apo(x, fast, slow);
        var pf = StochSmooth(macd, cycle);
        return StochSmooth(pf, cycle);
    }

    private static double[] StochSmooth(double[] v, int cycle)
    {
        var hi = Rolling.Max(v, cycle);
        var lo = Rolling.Min(v, cycle);
        var r = NaNs(v.Length);
        double prev = double.NaN;
        for (int i = 0; i < v.Length; i++)
        {
            if (double.IsNaN(hi[i])) continue;
            double range = hi[i] - lo[i];
            double k = range > 0 ? 100 * (v[i] - lo[i]) / range : (double.IsNaN(prev) ? 50 : prev);
            prev = double.IsNaN(prev) ? k : prev + 0.5 * (k - prev);
            r[i] = prev;
        }
        return r;
    }

    /// <summary>Connors RSI: mean of RSI(3), RSI of the up/down streak (2) and the percent rank of ROC(1) over 100.</summary>
    public static double[] ConnorsRsi(ReadOnlySpan<double> x, int rsiPeriod = 3, int streakPeriod = 2, int rankPeriod = 100)
    {
        int n = x.Length;
        var streak = new double[n];
        for (int i = 1; i < n; i++)
        {
            if (x[i] > x[i - 1]) streak[i] = streak[i - 1] > 0 ? streak[i - 1] + 1 : 1;
            else if (x[i] < x[i - 1]) streak[i] = streak[i - 1] < 0 ? streak[i - 1] - 1 : -1;
            else streak[i] = 0;
        }
        var a = Rsi(x, rsiPeriod);
        var b = Rsi(streak, streakPeriod);
        var roc = Roc(x, 1);
        var c = Rolling.PercentRank(roc, rankPeriod + 1);
        var r = new double[n];
        for (int i = 0; i < n; i++) r[i] = (a[i] + b[i] + c[i]) / 3;
        return r;
    }

    /// <summary>QStick: SMA of (close - open).</summary>
    public static double[] QStick(ReadOnlySpan<double> open, ReadOnlySpan<double> close, int period = 8)
    {
        var d = new double[close.Length];
        for (int i = 0; i < d.Length; i++) d[i] = close[i] - open[i];
        return Sma(d, period);
    }

    /// <summary>Vortex indicator (+VI, -VI).</summary>
    public static (double[] Plus, double[] Minus) Vortex(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 14)
    {
        int n = close.Length;
        var vp = NaNs(n);
        var vm = NaNs(n);
        var tr = TrueRange(high, low, close);
        tr[0] = double.NaN;
        for (int i = 1; i < n; i++)
        {
            vp[i] = Math.Abs(high[i] - low[i - 1]);
            vm[i] = Math.Abs(low[i] - high[i - 1]);
        }
        var sp = Rolling.Sum(vp, period);
        var sm = Rolling.Sum(vm, period);
        var st = Rolling.Sum(tr, period);
        for (int i = 0; i < n; i++) { sp[i] /= st[i]; sm[i] /= st[i]; }
        return (sp, sm);
    }

    /// <summary>Mass Index (Dorsey): sum over 25 of EMA9(range)/EMA9(EMA9(range)).</summary>
    public static double[] MassIndex(ReadOnlySpan<double> high, ReadOnlySpan<double> low, int emaPeriod = 9, int sumPeriod = 25)
    {
        var range = new double[high.Length];
        for (int i = 0; i < range.Length; i++) range[i] = high[i] - low[i];
        var e1 = Ema(range, emaPeriod);
        var e2 = Ema(e1, emaPeriod);
        var ratio = NaNs(range.Length);
        for (int i = 0; i < ratio.Length; i++) if (e2[i] > 0) ratio[i] = e1[i] / e2[i];
        return Rolling.Sum(ratio, sumPeriod);
    }
}
