namespace Finance.TimeSeries.Indicators;

/// <summary>Output of <see cref="Ta.HilbertTransform"/>: every series of the Hilbert-transform family.</summary>
public sealed record HilbertResult(
    double[] DominantPeriod, double[] DominantPhase, double[] InPhase, double[] Quadrature,
    double[] Sine, double[] LeadSine, double[] Trendline, double[] TrendMode);

// Ehlers' cycle tools ("Rocket Science for Traders", "Cycle Analytics for Traders"), following the TA-Lib
// formulation of the HT_* family. Results differ from TA-Lib only during warm-up: TA-Lib discards a fixed
// 63-bar lookback, here the first 50 bars (the recursions' settling time) are NaN.
public static partial class Ta
{
    private const int HilbertWarmup = 50;
    private const double Rad2Deg = 180.0 / Math.PI;

    /// <summary>
    /// Hilbert-transform cycle analysis: dominant cycle period and phase, phasor components, sine / lead sine,
    /// instantaneous trendline and trend-vs-cycle mode (1 = trending, 0 = cycling).
    /// </summary>
    public static HilbertResult HilbertTransform(ReadOnlySpan<double> x)
    {
        int n = x.Length;
        var period = NaNs(n); var phase = NaNs(n); var inPhase = NaNs(n); var quad = NaNs(n);
        var sine = NaNs(n); var lead = NaNs(n); var trend = NaNs(n); var mode = NaNs(n);
        int s = Rolling.FirstValid(x);
        if (n - s <= HilbertWarmup) return new(period, phase, inPhase, quad, sine, lead, trend, mode);

        var smooth = new double[n]; var det = new double[n]; var i1 = new double[n]; var q1 = new double[n];
        var smoothPeriod = new double[n]; var dcPhase = new double[n]; var itrend = new double[n];
        double per = 0, i2p = 0, q2p = 0, rep = 0, imp = 0, sp = 0;
        double prevSine = 0, prevLead = 0, prevPhase = 0;
        int daysInTrend = 0;
        for (int t = s; t < n; t++)
        {
            int k = t - s;
            smooth[t] = k >= 3 ? (4 * x[t] + 3 * x[t - 1] + 2 * x[t - 2] + x[t - 3]) / 10 : x[t];
            if (k < 6) { smoothPeriod[t] = sp; continue; }
            double adj = 0.075 * per + 0.54;
            det[t] = Hilbert(smooth, t, adj);
            q1[t] = Hilbert(det, t, adj);
            i1[t] = det[t - 3];
            double ji = Hilbert(i1, t, adj), jq = Hilbert(q1, t, adj);
            double i2 = 0.2 * (i1[t] - jq) + 0.8 * i2p;
            double q2 = 0.2 * (q1[t] + ji) + 0.8 * q2p;
            double re = 0.2 * (i2 * i2p + q2 * q2p) + 0.8 * rep;
            double im = 0.2 * (i2 * q2p - q2 * i2p) + 0.8 * imp;
            i2p = i2; q2p = q2; rep = re; imp = im;
            double newPer = per;
            if (im != 0 && re != 0) newPer = 360 / (Math.Atan(im / re) * Rad2Deg);
            if (newPer > 1.5 * per && per > 0) newPer = 1.5 * per;
            if (newPer < 0.67 * per) newPer = 0.67 * per;
            newPer = Math.Clamp(newPer, 6, 50);
            per = 0.2 * newPer + 0.8 * per;
            sp = 0.33 * per + 0.67 * sp;
            smoothPeriod[t] = sp;

            // Dominant cycle phase: one-period DFT of the smoothed price.
            int dc = Math.Max(1, (int)(sp + 0.5));
            double real = 0, imag = 0;
            for (int j = 0; j < dc && t - j >= s; j++)
            {
                double a = 2 * Math.PI * j / dc;
                real += Math.Sin(a) * smooth[t - j];
                imag += Math.Cos(a) * smooth[t - j];
            }
            double ph = Math.Abs(imag) > 0 ? Math.Atan(real / imag) * Rad2Deg : 90 * Math.Sign(real);
            ph += 90;
            if (sp > 0) ph += 360 / sp; // compensate the one-bar lag of the weighted moving average
            if (imag < 0) ph += 180;
            if (ph > 315) ph -= 360;
            dcPhase[t] = ph;

            // Instantaneous trendline: average over one dominant cycle, then a 4-bar WMA.
            double sum = 0; int cnt = 0;
            for (int j = 0; j < dc && t - j >= s; j++) { sum += x[t - j]; cnt++; }
            itrend[t] = sum / cnt;
            double tl = k >= 9 ? (4 * itrend[t] + 3 * itrend[t - 1] + 2 * itrend[t - 2] + itrend[t - 3]) / 10 : itrend[t];

            double sn = Math.Sin(ph / Rad2Deg), ld = Math.Sin((ph + 45) / Rad2Deg);
            // Trend mode: a sine/lead-sine crossover starts a new cycle; cycling while the phase advances at the
            // cycle rate; trending whenever price strays 1.5% from the trendline.
            int tm = 1;
            if ((sn > ld && prevSine <= prevLead) || (sn < ld && prevSine >= prevLead)) { daysInTrend = 0; tm = 0; }
            daysInTrend++;
            if (daysInTrend < 0.5 * sp) tm = 0;
            double dPhase = ph - prevPhase;
            if (sp != 0 && dPhase > 0.67 * 360 / sp && dPhase < 1.5 * 360 / sp) tm = 0;
            if (tl != 0 && Math.Abs((smooth[t] - tl) / tl) >= 0.015) tm = 1;
            prevSine = sn; prevLead = ld; prevPhase = ph;

            if (k < HilbertWarmup) continue;
            period[t] = sp; phase[t] = ph; inPhase[t] = i1[t]; quad[t] = q1[t];
            sine[t] = sn; lead[t] = ld; trend[t] = tl; mode[t] = tm;
        }
        return new(period, phase, inPhase, quad, sine, lead, trend, mode);
    }

    private static double Hilbert(double[] v, int t, double adj)
        => (0.0962 * v[t] + 0.5769 * v[t - 2] - 0.5769 * v[t - 4] - 0.0962 * v[t - 6]) * adj;

    /// <summary>
    /// Ehlers' MESA Adaptive Moving Average and its Following AMA. The smoothing factor is
    /// fastLimit / (rate of phase change), clamped to [slowLimit, fastLimit].
    /// </summary>
    public static (double[] Mama, double[] Fama) Mama(ReadOnlySpan<double> x, double fastLimit = 0.5, double slowLimit = 0.05)
    {
        int n = x.Length;
        var mama = NaNs(n); var fama = NaNs(n);
        int s = Rolling.FirstValid(x);
        if (n - s <= HilbertWarmup) return (mama, fama);
        var smooth = new double[n]; var det = new double[n]; var i1 = new double[n]; var q1 = new double[n];
        double per = 0, i2p = 0, q2p = 0, rep = 0, imp = 0, prevPhase = 0, m = x[s], f = x[s];
        for (int t = s; t < n; t++)
        {
            int k = t - s;
            smooth[t] = k >= 3 ? (4 * x[t] + 3 * x[t - 1] + 2 * x[t - 2] + x[t - 3]) / 10 : x[t];
            if (k < 6) continue;
            double adj = 0.075 * per + 0.54;
            det[t] = Hilbert(smooth, t, adj);
            q1[t] = Hilbert(det, t, adj);
            i1[t] = det[t - 3];
            double ji = Hilbert(i1, t, adj), jq = Hilbert(q1, t, adj);
            double i2 = 0.2 * (i1[t] - jq) + 0.8 * i2p;
            double q2 = 0.2 * (q1[t] + ji) + 0.8 * q2p;
            double re = 0.2 * (i2 * i2p + q2 * q2p) + 0.8 * rep;
            double im = 0.2 * (i2 * q2p - q2 * i2p) + 0.8 * imp;
            i2p = i2; q2p = q2; rep = re; imp = im;
            double newPer = per;
            if (im != 0 && re != 0) newPer = 360 / (Math.Atan(im / re) * Rad2Deg);
            if (newPer > 1.5 * per && per > 0) newPer = 1.5 * per;
            if (newPer < 0.67 * per) newPer = 0.67 * per;
            per = 0.2 * Math.Clamp(newPer, 6, 50) + 0.8 * per;

            double phase = i1[t] != 0 ? Math.Atan(q1[t] / i1[t]) * Rad2Deg : 0;
            double delta = Math.Max(1, prevPhase - phase);
            prevPhase = phase;
            double alpha = Math.Clamp(fastLimit / delta, slowLimit, fastLimit);
            m = alpha * x[t] + (1 - alpha) * m;
            f = 0.5 * alpha * m + (1 - 0.5 * alpha) * f;
            if (k < HilbertWarmup) continue;
            mama[t] = m; fama[t] = f;
        }
        return (mama, fama);
    }

    /// <summary>Ehlers' two-pole Super Smoother: a Butterworth low-pass with critical period <paramref name="period"/> and unit DC gain.</summary>
    public static double[] SuperSmoother(ReadOnlySpan<double> x, int period = 10)
    {
        var r = NaNs(x.Length);
        int s = Rolling.FirstValid(x);
        if (x.Length - s < 3) return r;
        double a = Math.Exp(-Math.Sqrt(2) * Math.PI / period);
        double c2 = 2 * a * Math.Cos(Math.Sqrt(2) * Math.PI / period), c3 = -a * a, c1 = 1 - c2 - c3;
        double f1 = x[s], f2 = x[s];
        for (int i = s; i < x.Length; i++)
        {
            double f = i < s + 2 ? x[i] : c1 * (x[i] + x[i - 1]) / 2 + c2 * f1 + c3 * f2;
            f2 = f1; f1 = f;
            if (i >= s + period - 1) r[i] = f;
        }
        return r;
    }

    /// <summary>Two-pole high-pass filter (Ehlers) with cut-off period <paramref name="period"/>; removes trend, zero DC gain.</summary>
    public static double[] HighPass(ReadOnlySpan<double> x, int period = 48)
    {
        var r = NaNs(x.Length);
        int s = Rolling.FirstValid(x);
        if (x.Length - s < 3) return r;
        double w = Math.Sqrt(2) * Math.PI / period;
        double a = (Math.Cos(w) + Math.Sin(w) - 1) / Math.Cos(w);
        double h1 = 0, h2 = 0;
        for (int i = s + 2; i < x.Length; i++)
        {
            double h = (1 - a / 2) * (1 - a / 2) * (x[i] - 2 * x[i - 1] + x[i - 2]) + 2 * (1 - a) * h1 - (1 - a) * (1 - a) * h2;
            h2 = h1; h1 = h;
            if (i >= s + period - 1) r[i] = h;
        }
        return r;
    }

    /// <summary>Ehlers' Roofing Filter: a high-pass (removes cycles longer than <paramref name="highPass"/>) then a Super Smoother.</summary>
    public static double[] RoofingFilter(ReadOnlySpan<double> x, int highPass = 48, int lowPass = 10)
    {
        var hp = HighPass(x, highPass);
        return SuperSmoother(hp, lowPass);
    }

    /// <summary>Ehlers' Simple Decycler: price minus a one-pole high-pass, i.e. the trend with cycles shorter than <paramref name="period"/> removed.</summary>
    public static double[] Decycler(ReadOnlySpan<double> x, int period = 60)
    {
        var r = NaNs(x.Length);
        int s = Rolling.FirstValid(x);
        if (x.Length - s < 2) return r;
        double w = 2 * Math.PI / period;
        double a = (Math.Cos(w) + Math.Sin(w) - 1) / Math.Cos(w);
        double hp = 0;
        for (int i = s + 1; i < x.Length; i++)
        {
            hp = (1 - a / 2) * (x[i] - x[i - 1]) + (1 - a) * hp;
            if (i >= s + period - 1) r[i] = x[i] - hp;
        }
        return r;
    }

    /// <summary>Ehlers' Laguerre RSI (0..1) with damping factor <paramref name="gamma"/>.</summary>
    public static double[] LaguerreRsi(ReadOnlySpan<double> x, double gamma = 0.5)
    {
        var r = NaNs(x.Length);
        int s = Rolling.FirstValid(x);
        if (s >= x.Length) return r;
        double l0 = x[s], l1 = x[s], l2 = x[s], l3 = x[s];
        for (int i = s; i < x.Length; i++)
        {
            double p0 = l0, p1 = l1, p2 = l2;
            l0 = (1 - gamma) * x[i] + gamma * p0;
            l1 = -gamma * l0 + p0 + gamma * p1;
            l2 = -gamma * l1 + p1 + gamma * p2;
            l3 = -gamma * l2 + p2 + gamma * l3;
            double cu = 0, cd = 0;
            Acc(l0 - l1); Acc(l1 - l2); Acc(l2 - l3);
            if (i >= s + 4) r[i] = cu + cd > 0 ? cu / (cu + cd) : 0.5;

            void Acc(double d) { if (d >= 0) cu += d; else cd -= d; }
        }
        return r;
    }

    /// <summary>Ehlers' Instantaneous Trendline (2-pole, α = <paramref name="alpha"/>), unit DC gain.</summary>
    public static double[] InstantaneousTrendline(ReadOnlySpan<double> x, double alpha = 0.07)
    {
        var r = NaNs(x.Length);
        int s = Rolling.FirstValid(x);
        if (x.Length - s < 3) return r;
        double a = alpha, it1 = 0, it2 = 0;
        for (int i = s; i < x.Length; i++)
        {
            int k = i - s;
            double it = k < 7
                ? (k >= 2 ? (x[i] + 2 * x[i - 1] + x[i - 2]) / 4 : x[i])
                : (a - a * a / 4) * x[i] + 0.5 * a * a * x[i - 1] - (a - 0.75 * a * a) * x[i - 2]
                  + 2 * (1 - a) * it1 - (1 - a) * (1 - a) * it2;
            it2 = it1; it1 = it;
            if (k >= 7) r[i] = it;
        }
        return r;
    }

    /// <summary>Ehlers' Center of Gravity oscillator: −Σ(j+1)·x[t−j] / Σx[t−j] over <paramref name="period"/> bars.</summary>
    public static double[] CenterOfGravity(ReadOnlySpan<double> x, int period = 10)
    {
        var r = NaNs(x.Length);
        int s = Rolling.FirstValid(x);
        for (int i = s + period - 1; i < x.Length; i++)
        {
            double num = 0, den = 0;
            for (int j = 0; j < period; j++) { num += (j + 1) * x[i - j]; den += x[i - j]; }
            r[i] = den != 0 ? -num / den : double.NaN;
        }
        return r;
    }
}
