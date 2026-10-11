using Finance.Core.Market;
using Finance.DataFrame;
using Finance.TimeSeries;
using Finance.TimeSeries.Indicators;
using Xunit;

namespace Finance.Analytics.Tests;

/// <summary>
/// The v0.2 catalogue additions, checked against closed-form properties (filter gains, perfect fits, known cycle
/// lengths) or naive re-computations written independently of the implementation.
/// </summary>
public class ExtendedIndicatorTests
{
    private static readonly OhlcvFrame F = OhlcvFrame.Synthetic(400, "EXT", seed: 7);

    private static void Close(double expected, double actual, double tol = 1e-9)
    {
        if (double.IsNaN(expected)) Assert.True(double.IsNaN(actual), $"expected NaN, got {actual}");
        else Assert.True(Math.Abs(expected - actual) <= tol * (1 + Math.Abs(expected)), $"expected {expected}, got {actual}");
    }

    private static double[] Sine(int n, double period, double amp = 5, double level = 100)
        => Enumerable.Range(0, n).Select(i => level + amp * Math.Sin(2 * Math.PI * i / period)).ToArray();

    private static double[] Const(int n, double v) => Enumerable.Repeat(v, n).ToArray();

    // ---------------- Cycles ----------------

    [Theory]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(30)]
    public void HilbertFindsTheDominantPeriodOfAPureSine(double period)
    {
        var h = Ta.HilbertTransform(Sine(800, period));
        var tail = h.DominantPeriod[^200..];
        Assert.InRange(tail.Average(), period * 0.93, period * 1.07);
    }

    [Fact]
    public void HilbertTrendModeSeparatesSineFromTrend()
    {
        var cyc = Ta.HilbertTransform(Sine(600, 20, amp: 1)).TrendMode[^300..].Average();
        var trend = Ta.HilbertTransform(Enumerable.Range(0, 600).Select(i => 100 * Math.Pow(1.01, i)).ToArray()).TrendMode[^300..].Average();
        Assert.True(cyc < 0.35, $"sine wave flagged trending {cyc:P0} of the time");
        Assert.True(trend > 0.95, $"exponential trend flagged trending only {trend:P0} of the time");
    }

    [Fact]
    public void HilbertSineCompletesOneCyclePerPeriod()
    {
        var s = Ta.HilbertTransform(Sine(800, 20)).Sine[^400..];
        int crossings = 0;
        for (int i = 1; i < s.Length; i++) if (Math.Sign(s[i]) != Math.Sign(s[i - 1])) crossings++;
        Assert.InRange(crossings, 36, 44); // 400 bars / 20 = 20 cycles = 40 zero crossings
    }

    [Fact]
    public void FiltersHaveUnitDcGain()
    {
        var c = Const(300, 42);
        foreach (var r in new[] { Ta.SuperSmoother(c, 10), Ta.Decycler(c, 60), Ta.InstantaneousTrendline(c), Ta.Mama(c).Mama, Ta.Mama(c).Fama, Ta.HilbertTransform(c).Trendline })
            Close(42, r[^1], 1e-9);
    }

    [Fact]
    public void HighPassAndRoofingRemoveLevelAndLinearTrend()
    {
        var ramp = Enumerable.Range(0, 400).Select(i => 50 + 0.3 * i).ToArray();
        Assert.True(Math.Abs(Ta.HighPass(ramp, 48)[^1]) < 1e-6);
        Assert.True(Math.Abs(Ta.RoofingFilter(Const(400, 7))[^1]) < 1e-9);
    }

    [Fact]
    public void SuperSmootherKillsTheNyquistFrequency()
    {
        var alt = Enumerable.Range(0, 300).Select(i => i % 2 == 0 ? 1.0 : -1.0).ToArray();
        Assert.True(Math.Abs(Ta.SuperSmoother(alt, 10)[^1]) < 1e-3);
    }

    [Fact]
    public void LaguerreRsiSaturatesOnMonotonicSeries()
    {
        var up = Enumerable.Range(0, 100).Select(i => 10.0 + i).ToArray();
        Close(1, Ta.LaguerreRsi(up)[^1], 1e-12);
        Close(0, Ta.LaguerreRsi(up.Reverse().ToArray())[^1], 1e-12);
    }

    [Fact]
    public void CenterOfGravityHandValue()
    {
        var cg = Ta.CenterOfGravity(new double[] { 1, 2, 3 }, 3);
        Close(-(1 * 3 + 2 * 2 + 3 * 1) / 6.0, cg[2]); // newest bar weighted 1
    }

    // ---------------- Trend ----------------

    [Fact]
    public void FramaFollowsAStraightLineExactly()
    {
        // A line has fractal dimension 1, so α saturates at 1 and FRAMA equals price.
        var line = Enumerable.Range(0, 100).Select(i => 10 + 0.5 * i).ToArray();
        var f = Ta.Frama(line, line, line, 16);
        for (int i = 15; i < line.Length; i++) Close(line[i], f[i]);
    }

    [Fact]
    public void SineWmaAndGeometricMaMatchNaive()
    {
        var x = F.Close;
        var s = Ta.SineWma(x, 7);
        var g = Ta.GeometricMa(x, 5);
        for (int i = 10; i < x.Length; i += 13)
        {
            double num = 0, den = 0, prod = 1;
            for (int j = 1; j <= 7; j++) { double w = Math.Sin(Math.PI * j / 8); num += w * x[i - 7 + j]; den += w; }
            for (int j = 0; j < 5; j++) prod *= x[i - j];
            Close(num / den, s[i]);
            Close(Math.Pow(prod, 1.0 / 5), g[i], 1e-12);
        }
    }

    [Fact]
    public void AlligatorIsDisplacedSmma()
    {
        var (jaw, teeth, lips) = Ta.Alligator(F.High, F.Low);
        var mp = F.High.Zip(F.Low, (h, l) => (h + l) / 2).ToArray();
        var j = Ta.Rma(mp, 13);
        var l5 = Ta.Rma(mp, 5);
        for (int i = 30; i < F.Count; i++) { Close(j[i - 8], jaw[i]); Close(l5[i - 3], lips[i]); }
        Assert.True(double.IsNaN(teeth[11]));
    }

    [Fact]
    public void FractalsReportConfirmedSwings()
    {
        double[] high = { 1, 2, 5, 2, 1, 1, 1 };
        double[] low = { 3, 2, 4, 2, 0, 2, 3 };
        var (up, down) = Ta.Fractals(high, low, 2);
        Assert.Equal(5, up[4]);   // high at index 2, confirmed 2 bars later
        Assert.Equal(0, down[6]); // low at index 4, confirmed at 6
        Assert.Equal(1, up.Count(double.IsFinite));
        Assert.Equal(1, down.Count(double.IsFinite));
    }

    [Fact]
    public void ZigZagJoinsReversalsAboveThreshold()
    {
        double[] p = { 100, 104, 110, 107, 99, 101, 108, 112 };
        var z = Ta.ZigZag(p, p, 5);
        Assert.Equal(110, z[2]);
        Assert.Equal(99, z[4]);
        Assert.Equal(112, z[7]);
        Close(104.5, z[3]); // linear between the 110 top and the 99 bottom
    }

    [Fact]
    public void TrendQualityGaugesOnAStraightLine()
    {
        var line = Enumerable.Range(0, 80).Select(i => 20 + 0.25 * i).ToArray();
        Close(1, Ta.EfficiencyRatio(line, 10)[^1]);
        Close(27.0 / 28, Ta.Vhf(line, 28)[^1]); // 28 closes span 27 steps; the denominator sums 28 changes
        Close(1, Ta.RSquared(line, 20)[^1], 1e-9);
        Close(100, Ta.TrendIntensity(line, 20, 10)[^1]);
    }

    [Fact]
    public void EfficiencyRatioAndVhfMatchNaive()
    {
        var x = F.Close;
        var er = Ta.EfficiencyRatio(x, 10);
        var vhf = Ta.Vhf(x, 28);
        for (int i = 40; i < x.Length; i += 11)
        {
            double path = 0;
            for (int k = i - 9; k <= i; k++) path += Math.Abs(x[k] - x[k - 1]);
            Close(Math.Abs(x[i] - x[i - 10]) / path, er[i]);
            double hi = double.MinValue, lo = double.MaxValue, path28 = 0;
            for (int k = i - 27; k <= i; k++) { hi = Math.Max(hi, x[k]); lo = Math.Min(lo, x[k]); path28 += Math.Abs(x[k] - x[k - 1]); }
            Close((hi - lo) / path28, vhf[i]);
        }
    }

    // ---------------- Momentum ----------------

    [Fact]
    public void DirectionalMovementIsConsistentWithDmi()
    {
        var (pdm, mdm) = Ta.DirectionalMovement(F.High, F.Low, 14);
        var dmi = Ta.Dmi(F.High, F.Low, F.Close, 14);
        // Independent Wilder-smoothed true range, same seeding as Wilder (plain sum of the first 14).
        double str = 0;
        for (int i = 1; i < F.Count; i++)
        {
            double tr = Math.Max(F.High[i] - F.Low[i], Math.Max(Math.Abs(F.High[i] - F.Close[i - 1]), Math.Abs(F.Low[i] - F.Close[i - 1])));
            str = i <= 14 ? str + tr : str - str / 14 + tr;
            if (i < 14) continue;
            Close(dmi.PlusDi[i], 100 * pdm[i] / str, 1e-9);
            Close(dmi.MinusDi[i], 100 * mdm[i] / str, 1e-9);
        }
    }

    [Fact]
    public void RmiWithUnitMomentumIsRsi()
    {
        var rmi = Ta.Rmi(F.Close, 14, 1);
        var rsi = Ta.Rsi(F.Close, 14);
        for (int i = 15; i < F.Count; i++) Close(rsi[i], rmi[i], 1e-9);
    }

    [Fact]
    public void SimpleOscillatorsMatchNaive()
    {
        var c = F.Close;
        var psl = Ta.PsychologicalLine(c, 12);
        var imi = Ta.IntradayMomentum(F.Open, c, 14);
        var bias = Ta.Bias(c, 20);
        for (int i = 30; i < c.Length; i += 9)
        {
            int ups = 0;
            for (int k = i - 11; k <= i; k++) if (c[k] > c[k - 1]) ups++;
            Close(100.0 * ups / 12, psl[i]);
            double g = 0, l = 0;
            for (int k = i - 13; k <= i; k++) { double d = c[k] - F.Open[k]; if (d > 0) g += d; else l -= d; }
            Close(100 * g / (g + l), imi[i]);
            double sma = 0;
            for (int k = i - 19; k <= i; k++) sma += c[k];
            sma /= 20;
            Close(100 * (c[i] - sma) / sma, bias[i]);
        }
    }

    [Fact]
    public void KdjAndRocVariantsFollowDefinitions()
    {
        var (k, d, j) = Ta.Kdj(F.High, F.Low, F.Close);
        for (int i = 20; i < F.Count; i++) Close(3 * k[i] - 2 * d[i], j[i]);
        var rocp = Ta.RocFraction(F.Close, 10);
        Close((F.Close[50] - F.Close[40]) / F.Close[40], rocp[50]);
        Close(100 * F.Close[50] / F.Close[40], IndicatorCatalog.Get("ROCR100").Compute(F)[0].Values[50]);
    }

    [Fact]
    public void WilliamsAdHandValues()
    {
        double[] h = { 10, 12, 11 }, l = { 9, 10, 9 }, c = { 9.5, 11.5, 10 };
        var w = Ta.WilliamsAd(h, l, c);
        Close(0, w[0]);
        Close(11.5 - Math.Min(10, 9.5), w[1]);               // up bar: close − true low
        Close(w[1] + (10 - Math.Max(11, 11.5)), w[2]);        // down bar: close − true high
    }

    // ---------------- Volatility ----------------

    [Fact]
    public void YangZhangMatchesNaiveFormula()
    {
        var yz = Ta.YangZhangVolatility(F.Open, F.High, F.Low, F.Close, 20);
        int n = 20, i = 250;
        double k = 0.34 / (1.34 + (n + 1.0) / (n - 1.0));
        var o = new double[n]; var cc = new double[n]; var rs = new double[n];
        for (int j = 0; j < n; j++)
        {
            int t = i - n + 1 + j;
            o[j] = Math.Log(F.Open[t] / F.Close[t - 1]);
            cc[j] = Math.Log(F.Close[t] / F.Open[t]);
            rs[j] = Math.Log(F.High[t] / F.Close[t]) * Math.Log(F.High[t] / F.Open[t]) + Math.Log(F.Low[t] / F.Close[t]) * Math.Log(F.Low[t] / F.Open[t]);
        }
        double Var(double[] a) { double m = a.Average(); return a.Sum(v => (v - m) * (v - m)) / (a.Length - 1); }
        double expected = 100 * Math.Sqrt((Var(o) + k * Var(cc) + (1 - k) * rs.Average()) * 252);
        Close(expected, yz[i], 1e-9);
    }

    [Fact]
    public void EwmaVolatilityFollowsRiskMetricsRecursion()
    {
        var v = Ta.EwmaVolatility(F.Close, 0.94);
        double r1 = Math.Log(F.Close[1] / F.Close[0]);
        double var = r1 * r1;
        for (int i = 2; i < 60; i++)
        {
            double prev = Math.Log(F.Close[i - 1] / F.Close[i - 2]);
            var = 0.94 * var + 0.06 * prev * prev;
            Close(100 * Math.Sqrt(var * 252), v[i], 1e-9);
        }
    }

    [Fact]
    public void StandardErrorMatchesNaiveOls()
    {
        var se = Ta.StandardError(F.Close, 20);
        for (int i = 30; i < F.Count; i += 23)
        {
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int j = 0; j < 20; j++) { double y = F.Close[i - 19 + j]; sx += j; sy += y; sxx += j * j; sxy += j * y; }
            double b = (20 * sxy - sx * sy) / (20 * sxx - sx * sx), a = (sy - b * sx) / 20, ss = 0;
            for (int j = 0; j < 20; j++) ss += Math.Pow(F.Close[i - 19 + j] - (a + b * j), 2);
            Close(Math.Sqrt(ss / 18), se[i], 1e-7);
        }
        var line = Enumerable.Range(0, 40).Select(i => 3.0 * i).ToArray();
        Assert.True(Ta.StandardError(line, 20)[^1] < 1e-9);
    }

    // ---------------- Volume ----------------

    [Fact]
    public void VolumeProfileFindsTheHighVolumeShelf()
    {
        // 40 bars spread over 90..110, but the heavy volume trades in a narrow 104-105 shelf.
        var bars = new List<Bar>();
        for (int i = 0; i < 40; i++)
        {
            bool shelf = i % 4 == 0;
            double lo = shelf ? 104 : 90 + (i % 10) * 2, hi = shelf ? 105 : lo + 2;
            bars.Add(new Bar(DateTime.MinValue.AddDays(i), lo, hi, lo, hi, shelf ? 10_000 : 100));
        }
        var f = OhlcvFrame.FromBars(bars.ToArray());
        var vp = Ta.VolumeProfile(f.High, f.Low, f.Volume, 40, 20);
        Assert.InRange(vp.PointOfControl[^1], 104, 105);
        Assert.True(vp.ValueAreaLow[^1] <= vp.PointOfControl[^1] && vp.PointOfControl[^1] <= vp.ValueAreaHigh[^1]);
        // By time at price the shelf is no longer special: the TPO POC can sit elsewhere, but the value area holds 70% of TPOs.
        var tpo = Ta.MarketProfile(f.High, f.Low, 40, 20);
        Assert.True(tpo.ValueAreaHigh[^1] - tpo.ValueAreaLow[^1] < 20);
    }

    [Fact]
    public void VolumeFlowMeasuresMatchNaive()
    {
        var rv = Ta.RelativeVolume(F.Volume, 20);
        var mfi = Ta.MarketFacilitation(F.High, F.Low, F.Volume);
        for (int i = 25; i < F.Count; i += 17)
        {
            double avg = 0;
            for (int k = i - 20; k < i; k++) avg += F.Volume[k];
            Close(F.Volume[i] / (avg / 20), rv[i]);
            Close((F.High[i] - F.Low[i]) / F.Volume[i], mfi[i]);
        }
        var up = Enumerable.Range(0, 60).Select(i => 10.0 + i).ToArray();
        Close(100, Ta.VolumeZoneOscillator(up, Const(60, 500), 14)[^1], 1e-12);
    }

    // ---------------- Statistics ----------------

    [Fact]
    public void DescriptiveStatisticsMatchNaive()
    {
        var x = F.Close;
        var mad = Ta.MeanAbsoluteDeviation(x, 20);
        var q = Ta.RollingQuantile(x, 20, 0.75);
        var cv = Ta.CoefficientOfVariation(x, 20);
        var ac = Ta.Autocorrelation(x, 30, 1);
        for (int i = 40; i < x.Length; i += 19)
        {
            var w = x[(i - 19)..(i + 1)];
            double m = w.Average();
            Close(w.Average(v => Math.Abs(v - m)), mad[i]);
            var sorted = w.OrderBy(v => v).ToArray();
            Close(sorted[14] + 0.25 * (sorted[15] - sorted[14]), q[i]); // numpy linear: pos = 0.75·19 = 14.25
            Close(Math.Sqrt(w.Sum(v => (v - m) * (v - m)) / 19) / m, cv[i]);

            var r = Enumerable.Range(i - 29, 30).Select(t => Math.Log(x[t] / x[t - 1])).ToArray();
            var rl = Enumerable.Range(i - 29, 30).Select(t => Math.Log(x[t - 1] / x[t - 2])).ToArray();
            double ma = r.Average(), mb = rl.Average();
            double cov = r.Zip(rl, (a, b) => (a - ma) * (b - mb)).Sum();
            double corr = cov / Math.Sqrt(r.Sum(a => (a - ma) * (a - ma)) * rl.Sum(b => (b - mb) * (b - mb)));
            Close(corr, ac[i], 1e-7);
        }
    }

    [Fact]
    public void EntropyDrawdownAndExtremeIndexHandValues()
    {
        Close(Math.Log2(8), Ta.Entropy(Const(8, 3), 8)[^1], 1e-12);
        var dd = Ta.Drawdown(new double[] { 100, 120, 90, 130 });
        Close(0, dd[1]);
        Close(-25, dd[2]);
        Close(0, dd[3]);
        double[] x = { 1, 5, 2, 3, 4 };
        Close(3, Ta.BarsSinceExtreme(x, 5, max: true)[4]);
        Close(4, Ta.BarsSinceExtreme(x, 5, max: false)[4]);
    }

    [Fact]
    public void RollingSharpeMatchesNaive()
    {
        var sh = Ta.RollingSharpe(F.Close, 63);
        int i = 300;
        var r = Enumerable.Range(i - 62, 63).Select(t => Math.Log(F.Close[t] / F.Close[t - 1])).ToArray();
        double m = r.Average(), sd = Math.Sqrt(r.Sum(v => (v - m) * (v - m)) / 62);
        Close(m / sd * Math.Sqrt(252), sh[i], 1e-9);
    }

    // ---------------- Catalogue ----------------

    [Fact]
    public void CatalogueReachesTheDesignTarget()
    {
        Assert.True(IndicatorCatalog.Count >= 200, $"only {IndicatorCatalog.Count} entries");
        Assert.True(IndicatorCatalog.ByCategory(IndicatorCategory.Cycle).Count() >= 10);
        Assert.Equal(60, IndicatorCatalog.ByCategory(IndicatorCategory.Pattern).Count());
    }
}

public class ExtendedCandlestickTests
{
    private static Bar B(double o, double h, double l, double c) => new(DateTime.MinValue, o, h, l, c, 1000);

    /// <summary>Six falling bars (bodies of 2) to give down-trend context; last close 100.</summary>
    private static Bar[] DownTrend(params Bar[] tail)
    {
        var bars = new List<Bar>();
        for (int i = 0; i < 6; i++) bars.Add(B(112 - 2 * i, 113 - 2 * i, 109 - 2 * i, 110 - 2 * i));
        bars.AddRange(tail);
        return bars.ToArray();
    }

    private static int Last(Bar[] bars, CandlePattern p) => Candlesticks.At(bars, bars.Length - 1, p);

    [Fact]
    public void ThreeOutsideUp()
    {
        var bars = DownTrend(B(100, 100.5, 98.5, 99), B(98.8, 100.5, 98.5, 100.2), B(100.2, 101.5, 100, 101.2));
        Assert.Equal(100, Last(bars, CandlePattern.ThreeOutsideUp));
        Assert.Equal(0, Last(bars, CandlePattern.ThreeOutsideDown));
    }

    [Fact]
    public void AbandonedBabyAndMorningDojiStar()
    {
        var bars = DownTrend(B(100, 100.2, 95.8, 96), B(95, 95.3, 94.7, 95.02), B(95.5, 99, 95.4, 98.5));
        Assert.Equal(100, Last(bars, CandlePattern.BullishAbandonedBaby));
        Assert.Equal(100, Last(bars, CandlePattern.MorningDojiStar));
        // Without the island gap it is still a morning doji star, but no longer an abandoned baby.
        var noGap = DownTrend(B(100, 100.2, 95.8, 96), B(95, 96.1, 94.7, 95.02), B(95.5, 99, 95.4, 98.5));
        Assert.Equal(0, Last(noGap, CandlePattern.BullishAbandonedBaby));
        Assert.Equal(100, Last(noGap, CandlePattern.MorningDojiStar));
    }

    [Fact]
    public void ThreeLineStrikeTakesTheColourOfTheFirstThree()
    {
        var bars = new[] { B(98, 100.2, 97.9, 100), B(100, 102.2, 99.9, 102), B(102, 104.2, 101.9, 104), B(104.5, 104.6, 97.5, 97.8) };
        Assert.Equal(100, Last(bars, CandlePattern.ThreeLineStrike));
        var notWiped = new[] { bars[0], bars[1], bars[2], B(104.5, 104.6, 99, 99.5) };
        Assert.Equal(0, Last(notWiped, CandlePattern.ThreeLineStrike));
    }

    [Fact]
    public void OnNeckVersusInNeck()
    {
        var onNeck = DownTrend(B(100, 100.2, 96, 96.2), B(95.5, 96.1, 95.3, 96.05));
        Assert.Equal(-100, Last(onNeck, CandlePattern.OnNeck));
        Assert.Equal(0, Last(onNeck, CandlePattern.InNeck));
        var inNeck = DownTrend(B(100, 100.2, 96, 96.2), B(95.5, 96.6, 95.3, 96.4));
        Assert.Equal(-100, Last(inNeck, CandlePattern.InNeck));
    }

    [Fact]
    public void UpsideTasukiGap()
    {
        var bars = new[] { B(100, 101, 99.8, 100.8), B(101.5, 103, 101.4, 102.8), B(102.2, 102.3, 101.1, 101.2) };
        Assert.Equal(100, Last(bars, CandlePattern.UpsideTasukiGap));
        var gapFilled = new[] { bars[0], bars[1], B(102.2, 102.3, 100.5, 100.6) };
        Assert.Equal(0, Last(gapFilled, CandlePattern.UpsideTasukiGap));
    }

    [Fact]
    public void HighWaveAndTakuri()
    {
        Assert.Equal(100, Candlesticks.At(new[] { B(100, 103, 97, 100.5) }, 0, CandlePattern.HighWave));
        Assert.Equal(-100, Candlesticks.At(new[] { B(100.5, 103, 97, 100) }, 0, CandlePattern.HighWave));
        Assert.Equal(100, Candlesticks.At(new[] { B(100, 100.05, 96, 100.02) }, 0, CandlePattern.Takuri));
    }
}
