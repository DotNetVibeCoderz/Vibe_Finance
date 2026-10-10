using Finance.Core.Market;

namespace Finance.TimeSeries.Indicators;

public static partial class Ta
{
    // ---------------- Price transforms ----------------

    /// <summary>(H + L + C) / 3.</summary>
    public static double[] TypicalPrice(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close)
    {
        var r = new double[close.Length];
        for (int i = 0; i < r.Length; i++) r[i] = (high[i] + low[i] + close[i]) / 3;
        return r;
    }

    /// <summary>(H + L) / 2.</summary>
    public static double[] MedianPrice(ReadOnlySpan<double> high, ReadOnlySpan<double> low)
    {
        var r = new double[high.Length];
        for (int i = 0; i < r.Length; i++) r[i] = (high[i] + low[i]) / 2;
        return r;
    }

    /// <summary>(H + L + 2C) / 4.</summary>
    public static double[] WeightedClose(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close)
    {
        var r = new double[close.Length];
        for (int i = 0; i < r.Length; i++) r[i] = (high[i] + low[i] + 2 * close[i]) / 4;
        return r;
    }

    /// <summary>(O + H + L + C) / 4.</summary>
    public static double[] AveragePrice(ReadOnlySpan<double> open, ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close)
    {
        var r = new double[close.Length];
        for (int i = 0; i < r.Length; i++) r[i] = (open[i] + high[i] + low[i] + close[i]) / 4;
        return r;
    }

    /// <summary>Heikin-Ashi candles.</summary>
    public static Bar[] HeikinAshi(ReadOnlySpan<Bar> bars)
    {
        var r = new Bar[bars.Length];
        if (bars.Length == 0) return r;
        double prevOpen = (bars[0].Open + bars[0].Close) / 2, prevClose = (bars[0].Open + bars[0].High + bars[0].Low + bars[0].Close) / 4;
        for (int i = 0; i < bars.Length; i++)
        {
            var b = bars[i];
            double c = (b.Open + b.High + b.Low + b.Close) / 4;
            double o = i == 0 ? prevOpen : (prevOpen + prevClose) / 2;
            r[i] = new Bar(b.Time, o, Math.Max(b.High, Math.Max(o, c)), Math.Min(b.Low, Math.Min(o, c)), c, b.Volume);
            prevOpen = o;
            prevClose = c;
        }
        return r;
    }

    // ---------------- Volatility ----------------

    /// <summary>True range; element 0 is H - L.</summary>
    public static double[] TrueRange(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close)
    {
        var r = new double[close.Length];
        if (r.Length == 0) return r;
        r[0] = high[0] - low[0];
        for (int i = 1; i < r.Length; i++)
            r[i] = Math.Max(high[i] - low[i], Math.Max(Math.Abs(high[i] - close[i - 1]), Math.Abs(low[i] - close[i - 1])));
        return r;
    }

    /// <summary>Average True Range (Wilder). The first value averages TR[1..period] (TA-Lib convention).</summary>
    public static double[] Atr(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 14)
    {
        var tr = TrueRange(high, low, close);
        if (tr.Length > 0) tr[0] = double.NaN;
        return Rma(tr, period);
    }

    /// <summary>Normalised ATR: 100 · ATR / close.</summary>
    public static double[] Natr(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 14)
    {
        var a = Atr(high, low, close, period);
        for (int i = 0; i < a.Length; i++) a[i] = 100 * a[i] / close[i];
        return a;
    }

    /// <summary>Bollinger Bands (population standard deviation, as in Bollinger's definition), %B and bandwidth.</summary>
    public static (double[] Upper, double[] Middle, double[] Lower, double[] PercentB, double[] Bandwidth) Bollinger(ReadOnlySpan<double> x, int period = 20, double k = 2)
    {
        var mid = Sma(x, period);
        var sd = Rolling.StdDev(x, period, ddof: 0);
        int n = x.Length;
        var up = new double[n];
        var lo = new double[n];
        var pb = new double[n];
        var bw = new double[n];
        for (int i = 0; i < n; i++)
        {
            up[i] = mid[i] + k * sd[i];
            lo[i] = mid[i] - k * sd[i];
            double w = up[i] - lo[i];
            pb[i] = w > 0 ? (x[i] - lo[i]) / w : double.NaN;
            bw[i] = mid[i] != 0 ? w / mid[i] : double.NaN;
        }
        return (up, mid, lo, pb, bw);
    }

    /// <summary>Keltner Channels: EMA ± multiplier · ATR.</summary>
    public static (double[] Upper, double[] Middle, double[] Lower) Keltner(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 20, int atrPeriod = 10, double multiplier = 2)
    {
        var mid = Ema(close, period);
        var atr = Atr(high, low, close, atrPeriod);
        var up = new double[close.Length];
        var lo = new double[close.Length];
        for (int i = 0; i < up.Length; i++) { up[i] = mid[i] + multiplier * atr[i]; lo[i] = mid[i] - multiplier * atr[i]; }
        return (up, mid, lo);
    }

    /// <summary>Donchian channel: highest high, lowest low and their midpoint.</summary>
    public static (double[] Upper, double[] Middle, double[] Lower) Donchian(ReadOnlySpan<double> high, ReadOnlySpan<double> low, int period = 20)
    {
        var up = Rolling.Max(high, period);
        var lo = Rolling.Min(low, period);
        var mid = new double[up.Length];
        for (int i = 0; i < mid.Length; i++) mid[i] = 0.5 * (up[i] + lo[i]);
        return (up, mid, lo);
    }

    /// <summary>Chandelier exits: highest high - k·ATR (long), lowest low + k·ATR (short).</summary>
    public static (double[] LongExit, double[] ShortExit) Chandelier(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 22, double multiplier = 3)
    {
        var atr = Atr(high, low, close, period);
        var hh = Rolling.Max(high, period);
        var ll = Rolling.Min(low, period);
        for (int i = 0; i < atr.Length; i++)
        {
            double a = atr[i];
            hh[i] -= multiplier * a;
            ll[i] += multiplier * a;
        }
        return (hh, ll);
    }

    /// <summary>Chaikin volatility: percent change over <paramref name="rocPeriod"/> of EMA(high - low).</summary>
    public static double[] ChaikinVolatility(ReadOnlySpan<double> high, ReadOnlySpan<double> low, int emaPeriod = 10, int rocPeriod = 10)
    {
        var range = new double[high.Length];
        for (int i = 0; i < range.Length; i++) range[i] = high[i] - low[i];
        return Roc(Ema(range, emaPeriod), rocPeriod);
    }

    /// <summary>Annualised close-to-close historical volatility of log returns (in percent).</summary>
    public static double[] HistoricalVolatility(ReadOnlySpan<double> close, int period = 20, double periodsPerYear = 252)
    {
        var lr = NaNs(close.Length);
        for (int i = 1; i < close.Length; i++) lr[i] = Math.Log(close[i] / close[i - 1]);
        var sd = Rolling.StdDev(lr, period);
        double f = 100 * Math.Sqrt(periodsPerYear);
        for (int i = 0; i < sd.Length; i++) sd[i] *= f;
        return sd;
    }

    /// <summary>Parkinson high-low volatility estimator (annualised, percent).</summary>
    public static double[] ParkinsonVolatility(ReadOnlySpan<double> high, ReadOnlySpan<double> low, int period = 20, double periodsPerYear = 252)
    {
        var v = new double[high.Length];
        for (int i = 0; i < v.Length; i++) { double l = Math.Log(high[i] / low[i]); v[i] = l * l / (4 * Math.Log(2)); }
        var m = Rolling.Mean(v, period);
        for (int i = 0; i < m.Length; i++) m[i] = 100 * Math.Sqrt(m[i] * periodsPerYear);
        return m;
    }

    /// <summary>Garman-Klass OHLC volatility estimator (annualised, percent).</summary>
    public static double[] GarmanKlassVolatility(ReadOnlySpan<double> open, ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 20, double periodsPerYear = 252)
    {
        var v = new double[close.Length];
        double c = 2 * Math.Log(2) - 1;
        for (int i = 0; i < v.Length; i++)
        {
            double hl = Math.Log(high[i] / low[i]), co = Math.Log(close[i] / open[i]);
            v[i] = 0.5 * hl * hl - c * co * co;
        }
        var m = Rolling.Mean(v, period);
        for (int i = 0; i < m.Length; i++) m[i] = 100 * Math.Sqrt(Math.Max(0, m[i]) * periodsPerYear);
        return m;
    }

    /// <summary>Rogers-Satchell drift-independent volatility estimator (annualised, percent).</summary>
    public static double[] RogersSatchellVolatility(ReadOnlySpan<double> open, ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 20, double periodsPerYear = 252)
    {
        var v = new double[close.Length];
        for (int i = 0; i < v.Length; i++)
        {
            double hc = Math.Log(high[i] / close[i]), ho = Math.Log(high[i] / open[i]);
            double lc = Math.Log(low[i] / close[i]), lo = Math.Log(low[i] / open[i]);
            v[i] = hc * ho + lc * lo;
        }
        var m = Rolling.Mean(v, period);
        for (int i = 0; i < m.Length; i++) m[i] = 100 * Math.Sqrt(Math.Max(0, m[i]) * periodsPerYear);
        return m;
    }

    /// <summary>Ulcer Index: RMS of percent drawdown from the rolling max close.</summary>
    public static double[] UlcerIndex(ReadOnlySpan<double> close, int period = 14)
    {
        var hh = Rolling.Max(close, period);
        var sq = NaNs(close.Length);
        for (int i = 0; i < sq.Length; i++)
        {
            if (double.IsNaN(hh[i])) continue;
            double dd = 100 * (close[i] - hh[i]) / hh[i];
            sq[i] = dd * dd;
        }
        var m = Rolling.Mean(sq, period);
        for (int i = 0; i < m.Length; i++) m[i] = Math.Sqrt(m[i]);
        return m;
    }

    /// <summary>Choppiness Index (0-100; high = sideways, low = trending).</summary>
    public static double[] Choppiness(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 14)
    {
        var tr = TrueRange(high, low, close);
        var st = Rolling.Sum(tr, period);
        var hh = Rolling.Max(high, period);
        var ll = Rolling.Min(low, period);
        var r = NaNs(close.Length);
        double lg = Math.Log10(period);
        for (int i = 0; i < r.Length; i++)
        {
            double range = hh[i] - ll[i];
            if (range > 0) r[i] = 100 * Math.Log10(st[i] / range) / lg;
        }
        return r;
    }

    /// <summary>Rolling standard deviation (population, as TA-Lib STDDEV).</summary>
    public static double[] StdDev(ReadOnlySpan<double> x, int period = 20) => Rolling.StdDev(x, period, ddof: 0);

    // ---------------- Trend ----------------

    /// <summary>Wilder's directional movement system: +DI, -DI, DX, ADX and ADXR.</summary>
    public static (double[] PlusDi, double[] MinusDi, double[] Dx, double[] Adx, double[] Adxr) Dmi(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 14)
    {
        int n = close.Length;
        var plusDi = NaNs(n);
        var minusDi = NaNs(n);
        var dx = NaNs(n);
        if (n <= period) return (plusDi, minusDi, dx, NaNs(n), NaNs(n));
        double sTr = 0, sPlus = 0, sMinus = 0;
        for (int i = 1; i < n; i++)
        {
            double up = high[i] - high[i - 1], down = low[i - 1] - low[i];
            double pdm = up > down && up > 0 ? up : 0;
            double mdm = down > up && down > 0 ? down : 0;
            double tr = Math.Max(high[i] - low[i], Math.Max(Math.Abs(high[i] - close[i - 1]), Math.Abs(low[i] - close[i - 1])));
            if (i <= period)
            {
                sTr += tr; sPlus += pdm; sMinus += mdm;
                if (i < period) continue;
            }
            else
            {
                sTr = sTr - sTr / period + tr;
                sPlus = sPlus - sPlus / period + pdm;
                sMinus = sMinus - sMinus / period + mdm;
            }
            double p = sTr > 0 ? 100 * sPlus / sTr : 0, m = sTr > 0 ? 100 * sMinus / sTr : 0;
            plusDi[i] = p;
            minusDi[i] = m;
            dx[i] = p + m > 0 ? 100 * Math.Abs(p - m) / (p + m) : 0;
        }
        var adx = Rma(dx, period);
        var adxr = NaNs(n);
        for (int i = period - 1; i < n; i++)
            if (!double.IsNaN(adx[i]) && !double.IsNaN(adx[i - period + 1])) adxr[i] = 0.5 * (adx[i] + adx[i - period + 1]);
        return (plusDi, minusDi, dx, adx, adxr);
    }

    /// <summary>Average Directional Index.</summary>
    public static double[] Adx(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 14)
        => Dmi(high, low, close, period).Adx;

    /// <summary>Aroon up/down (0-100) and the Aroon oscillator.</summary>
    public static (double[] Up, double[] Down, double[] Oscillator) Aroon(ReadOnlySpan<double> high, ReadOnlySpan<double> low, int period = 25)
    {
        int n = high.Length;
        var imax = Rolling.ArgMax(high, period + 1);
        var imin = Rolling.ArgMin(low, period + 1);
        var up = NaNs(n);
        var dn = NaNs(n);
        var osc = NaNs(n);
        for (int i = 0; i < n; i++)
        {
            if (imax[i] < 0) continue;
            up[i] = 100.0 * (period - (i - imax[i])) / period;
            dn[i] = 100.0 * (period - (i - imin[i])) / period;
            osc[i] = up[i] - dn[i];
        }
        return (up, dn, osc);
    }

    /// <summary>Parabolic SAR (Wilder) with acceleration step and maximum.</summary>
    public static double[] ParabolicSar(ReadOnlySpan<double> high, ReadOnlySpan<double> low, double step = 0.02, double max = 0.2)
    {
        int n = high.Length;
        var r = NaNs(n);
        if (n < 2) return r;
        bool longPos = high[1] + low[1] >= high[0] + low[0];
        double af = step;
        double ep = longPos ? Math.Max(high[0], high[1]) : Math.Min(low[0], low[1]);
        double sar = longPos ? Math.Min(low[0], low[1]) : Math.Max(high[0], high[1]);
        r[1] = sar;
        for (int i = 2; i < n; i++)
        {
            sar += af * (ep - sar);
            if (longPos)
            {
                sar = Math.Min(sar, Math.Min(low[i - 1], low[i - 2]));
                if (low[i] < sar)
                {
                    longPos = false;
                    sar = ep;
                    ep = low[i];
                    af = step;
                }
                else if (high[i] > ep)
                {
                    ep = high[i];
                    af = Math.Min(af + step, max);
                }
            }
            else
            {
                sar = Math.Max(sar, Math.Max(high[i - 1], high[i - 2]));
                if (high[i] > sar)
                {
                    longPos = true;
                    sar = ep;
                    ep = high[i];
                    af = step;
                }
                else if (low[i] < ep)
                {
                    ep = low[i];
                    af = Math.Min(af + step, max);
                }
            }
            r[i] = sar;
        }
        return r;
    }

    /// <summary>Supertrend line and direction (+1 up-trend, -1 down-trend).</summary>
    public static (double[] Line, double[] Direction) Supertrend(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int period = 10, double multiplier = 3)
    {
        int n = close.Length;
        var atr = Atr(high, low, close, period);
        var line = NaNs(n);
        var dir = NaNs(n);
        double upper = double.NaN, lower = double.NaN;
        int d = 1;
        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(atr[i])) continue;
            double mid = (high[i] + low[i]) / 2;
            double bu = mid + multiplier * atr[i], bl = mid - multiplier * atr[i];
            if (double.IsNaN(upper))
            {
                upper = bu;
                lower = bl;
                d = close[i] >= mid ? 1 : -1;
            }
            else
            {
                upper = bu < upper || close[i - 1] > upper ? bu : upper;
                lower = bl > lower || close[i - 1] < lower ? bl : lower;
                if (d == 1 && close[i] < lower) d = -1;
                else if (d == -1 && close[i] > upper) d = 1;
            }
            line[i] = d == 1 ? lower : upper;
            dir[i] = d;
        }
        return (line, dir);
    }

    /// <summary>
    /// Ichimoku cloud. Senkou spans are returned at the bar they are computed on (not shifted forward);
    /// plot them <paramref name="displacement"/> bars ahead. Chikou is close shifted back (NaN for the last bars).
    /// </summary>
    public static (double[] Tenkan, double[] Kijun, double[] SenkouA, double[] SenkouB, double[] Chikou) Ichimoku(
        ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, int tenkan = 9, int kijun = 26, int senkouB = 52, int displacement = 26)
    {
        var t = MidPrice(high, low, tenkan);
        var k = MidPrice(high, low, kijun);
        var b = MidPrice(high, low, senkouB);
        var a = new double[close.Length];
        for (int i = 0; i < a.Length; i++) a[i] = 0.5 * (t[i] + k[i]);
        var chikou = NaNs(close.Length);
        for (int i = 0; i + displacement < close.Length; i++) chikou[i] = close[i + displacement];
        return (t, k, a, b, chikou);
    }

    /// <summary>Rolling linear regression over the window: fitted end value, slope, intercept, angle (degrees) and time-series forecast (one step ahead).</summary>
    public static (double[] Value, double[] Slope, double[] Intercept, double[] Angle, double[] Forecast) LinearRegression(ReadOnlySpan<double> x, int period = 14)
    {
        int n = x.Length;
        var value = NaNs(n);
        var slope = NaNs(n);
        var icpt = NaNs(n);
        var angle = NaNs(n);
        var tsf = NaNs(n);
        int s = Rolling.FirstValid(x);
        if (n - s < period) return (value, slope, icpt, angle, tsf);
        double sumX = period * (period - 1) / 2.0;
        double sumX2 = (period - 1.0) * period * (2.0 * period - 1) / 6.0;
        double div = sumX * sumX - period * sumX2;
        double sumY = 0, sumXY = 0;
        for (int k = 0; k < period; k++) { sumY += x[s + k]; sumXY += k * x[s + k]; }
        for (int i = s + period - 1; i < n; i++)
        {
            if (i > s + period - 1)
            {
                // Slide: drop the oldest point and re-index the rest down by one.
                double old = x[i - period];
                sumXY = sumXY - (sumY - old) + (period - 1) * x[i];
                sumY += x[i] - old;
            }
            double m = (period * sumXY - sumX * sumY) / -div;
            double b = (sumY - m * sumX) / period;
            slope[i] = m;
            icpt[i] = b;
            value[i] = b + m * (period - 1);
            tsf[i] = b + m * period;
            angle[i] = Math.Atan(m) * 180 / Math.PI;
        }
        return (value, slope, icpt, angle, tsf);
    }

    /// <summary>Classic floor-trader pivot points from the previous bar.</summary>
    public static (double[] Pivot, double[] R1, double[] S1, double[] R2, double[] S2, double[] R3, double[] S3) PivotPoints(ReadOnlySpan<double> high, ReadOnlySpan<double> low, ReadOnlySpan<double> close, PivotMethod method = PivotMethod.Classic)
    {
        int n = close.Length;
        var p = NaNs(n); var r1 = NaNs(n); var s1 = NaNs(n); var r2 = NaNs(n); var s2 = NaNs(n); var r3 = NaNs(n); var s3 = NaNs(n);
        for (int i = 1; i < n; i++)
        {
            double h = high[i - 1], l = low[i - 1], c = close[i - 1], range = h - l;
            double pp = (h + l + c) / 3;
            p[i] = pp;
            switch (method)
            {
                case PivotMethod.Fibonacci:
                    r1[i] = pp + 0.382 * range; s1[i] = pp - 0.382 * range;
                    r2[i] = pp + 0.618 * range; s2[i] = pp - 0.618 * range;
                    r3[i] = pp + range; s3[i] = pp - range;
                    break;
                case PivotMethod.Camarilla:
                    r1[i] = c + range * 1.1 / 12; s1[i] = c - range * 1.1 / 12;
                    r2[i] = c + range * 1.1 / 6; s2[i] = c - range * 1.1 / 6;
                    r3[i] = c + range * 1.1 / 4; s3[i] = c - range * 1.1 / 4;
                    break;
                default:
                    r1[i] = 2 * pp - l; s1[i] = 2 * pp - h;
                    r2[i] = pp + range; s2[i] = pp - range;
                    r3[i] = h + 2 * (pp - l); s3[i] = l - 2 * (h - pp);
                    break;
            }
        }
        return (p, r1, s1, r2, s2, r3, s3);
    }
}

/// <summary>Pivot point formula.</summary>
public enum PivotMethod { Classic, Fibonacci, Camarilla }
