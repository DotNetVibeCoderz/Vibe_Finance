using Finance.Core.Market;
using Finance.DataFrame;
using Finance.Mathematics;
using Finance.TimeSeries.Indicators;
using Xunit;

namespace Finance.Analytics.Tests;

public class IndicatorTests
{
    private static readonly OhlcvFrame F = OhlcvFrame.Synthetic(400, "IND", seed: 21);

    private static void Close(double expected, double actual, double tol = 1e-9)
    {
        if (double.IsNaN(expected)) Assert.True(double.IsNaN(actual), $"expected NaN, got {actual}");
        else Assert.True(Math.Abs(expected - actual) <= tol * (1 + Math.Abs(expected)), $"expected {expected}, got {actual}");
    }

    [Fact]
    public void SmaAndEmaKnownValues()
    {
        double[] x = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var sma = Ta.Sma(x, 3);
        Assert.True(double.IsNaN(sma[1]));
        Assert.Equal(2, sma[2]);
        Assert.Equal(9, sma[9]);
        var ema = Ta.Ema(x, 3);
        // Seed = SMA(1,2,3) = 2; α = 0.5 → 3, 4, ... (linear input keeps a lag of 1).
        Assert.Equal(2, ema[2]);
        Assert.Equal(3, ema[3]);
        Assert.Equal(9, ema[9], 12);
    }

    [Fact]
    public void WmaMatchesNaive()
    {
        var w = Ta.Wma(F.Close, 10);
        for (int i = 9; i < F.Count; i++)
        {
            double num = 0;
            for (int k = 0; k < 10; k++) num += (k + 1) * F.Close[i - 9 + k];
            Close(num / 55, w[i]);
        }
    }

    [Fact]
    public void RsiMatchesIndependentWilderLoop()
    {
        var c = F.Close;
        var rsi = Ta.Rsi(c, 14);
        double ag = 0, al = 0;
        for (int i = 1; i <= 14; i++) { double d = c[i] - c[i - 1]; ag += Math.Max(d, 0); al += Math.Max(-d, 0); }
        ag /= 14; al /= 14;
        Close(100 - 100 / (1 + ag / al), rsi[14]);
        for (int i = 15; i < c.Length; i++)
        {
            double d = c[i] - c[i - 1];
            ag = (13 * ag + Math.Max(d, 0)) / 14;
            al = (13 * al + Math.Max(-d, 0)) / 14;
            Close(100 - 100 / (1 + ag / al), rsi[i]);
        }
        Assert.True(double.IsNaN(rsi[13]));
        Assert.All(rsi.Skip(14), v => Assert.InRange(v, 0, 100));
    }

    [Fact]
    public void RsiOfMonotoneSeriesIsHundred()
    {
        var x = Enumerable.Range(1, 30).Select(i => (double)i).ToArray();
        Assert.Equal(100, Ta.Rsi(x, 14)[^1]);
    }

    [Fact]
    public void MacdIsDifferenceOfEmas()
    {
        var (macd, signal, hist) = Ta.Macd(F.Close);
        var f = Ta.Ema(F.Close, 12);
        var s = Ta.Ema(F.Close, 26);
        for (int i = 25; i < F.Count; i++) Close(f[i] - s[i], macd[i]);
        Assert.True(double.IsNaN(signal[25 + 7]));
        Assert.False(double.IsNaN(signal[25 + 8]));
        for (int i = 40; i < F.Count; i++) Close(macd[i] - signal[i], hist[i]);
    }

    [Fact]
    public void BollingerUsesPopulationStdDev()
    {
        var (up, mid, lo, pb, _) = Ta.Bollinger(F.Close, 20, 2);
        for (int i = 19; i < F.Count; i++)
        {
            var w = F.Close[(i - 19)..(i + 1)];
            double sd = Stats.StdDev(w, 0);
            Close(w.Average(), mid[i]);
            Close(w.Average() + 2 * sd, up[i], 1e-8);
            Close(w.Average() - 2 * sd, lo[i], 1e-8);
            Close((F.Close[i] - lo[i]) / (up[i] - lo[i]), pb[i], 1e-8);
        }
    }

    [Fact]
    public void AtrMatchesNaiveWilder()
    {
        var atr = Ta.Atr(F.High, F.Low, F.Close, 14);
        var tr = Ta.TrueRange(F.High, F.Low, F.Close);
        double a = tr.Skip(1).Take(14).Average();
        Close(a, atr[14]);
        for (int i = 15; i < F.Count; i++)
        {
            a = (a * 13 + tr[i]) / 14;
            Close(a, atr[i]);
        }
    }

    [Fact]
    public void StochasticAndWilliamsMatchDefinitions()
    {
        var (k, _) = Ta.Stochastic(F.High, F.Low, F.Close, 14, 1, 3);
        var wr = Ta.WilliamsR(F.High, F.Low, F.Close, 14);
        for (int i = 13; i < F.Count; i++)
        {
            double hh = F.High[(i - 13)..(i + 1)].Max(), ll = F.Low[(i - 13)..(i + 1)].Min();
            Close(100 * (F.Close[i] - ll) / (hh - ll), k[i]);
            Close(-100 * (hh - F.Close[i]) / (hh - ll), wr[i]);
            Close(k[i] - 100, wr[i], 1e-9);
        }
    }

    [Fact]
    public void ObvKnownSequence()
    {
        double[] c = { 10, 11, 11, 10, 12 };
        double[] v = { 100, 200, 300, 400, 500 };
        Assert.Equal(new double[] { 100, 300, 300, -100, 400 }, Ta.Obv(c, v));
    }

    [Fact]
    public void LinearRegressionMatchesOls()
    {
        var (value, slope, icpt, _, tsf) = Ta.LinearRegression(F.Close, 14);
        var xs = Enumerable.Range(0, 14).Select(i => (double)i).ToArray();
        for (int i = 13; i < F.Count; i += 17)
        {
            var reg = Regression.Simple(xs, F.Close[(i - 13)..(i + 1)]);
            Close(reg.Coefficients[1], slope[i], 1e-7);
            Close(reg.Coefficients[0], icpt[i], 1e-7);
            Close(reg.Coefficients[0] + 13 * reg.Coefficients[1], value[i], 1e-7);
            Close(reg.Coefficients[0] + 14 * reg.Coefficients[1], tsf[i], 1e-7);
        }
    }

    [Fact]
    public void AroonOnKnownPattern()
    {
        // High peaks at index 7, low trough at index 2, window 5 (+1).
        double[] h = { 1, 2, 3, 4, 5, 6, 7, 9, 8, 7 };
        double[] l = { 5, 4, 1, 4, 4, 4, 4, 4, 4, 4 };
        var (up, down, _) = Ta.Aroon(h, l, 5);
        Assert.Equal(100.0 * (5 - 2) / 5, up[9], 12);  // 2 bars since the high
        Assert.Equal(100.0 * (5 - 0) / 5, down[9], 12); // ties keep most recent low (index 9)
    }

    [Fact]
    public void DmiAndAdxAreBounded()
    {
        var r = Ta.Dmi(F.High, F.Low, F.Close, 14);
        Assert.True(double.IsNaN(r.Adx[26]));
        Assert.False(double.IsNaN(r.Adx[27]));
        foreach (var arr in new[] { r.PlusDi, r.MinusDi, r.Adx })
            Assert.All(arr.Where(v => !double.IsNaN(v)), v => Assert.InRange(v, 0, 100));
    }

    [Fact]
    public void ParabolicSarStaysOnCorrectSideInStrongTrend()
    {
        var h = Enumerable.Range(0, 50).Select(i => 100.0 + i + 0.5).ToArray();
        var l = Enumerable.Range(0, 50).Select(i => 100.0 + i - 0.5).ToArray();
        var sar = Ta.ParabolicSar(h, l);
        for (int i = 2; i < 50; i++) Assert.True(sar[i] < l[i]);
    }

    [Fact]
    public void KamaAndOtherAveragesTrackConstantSeries()
    {
        var x = Enumerable.Repeat(42.0, 120).ToArray();
        foreach (var arr in new[] { Ta.Kama(x), Ta.Hma(x), Ta.Tema(x), Ta.Dema(x), Ta.T3(x), Ta.Trima(x), Ta.Zlema(x), Ta.Alma(x), Ta.McGinley(x) })
            Close(42, arr[^1], 1e-9);
    }

    [Fact]
    public void StreamingMatchesBatch()
    {
        var sma = new StreamingSma(20);
        var ema = new StreamingEma(20);
        var rsi = new StreamingRsi(14);
        var atr = new StreamingAtr(14);
        var bb = new StreamingBollinger(20, 2);
        var macd = new StreamingMacd();
        var bSma = Ta.Sma(F.Close, 20);
        var bEma = Ta.Ema(F.Close, 20);
        var bRsi = Ta.Rsi(F.Close, 14);
        var bAtr = Ta.Atr(F.High, F.Low, F.Close, 14);
        var bBb = Ta.Bollinger(F.Close, 20, 2);
        var bMacd = Ta.Macd(F.Close);
        for (int i = 0; i < F.Count; i++)
        {
            var bar = F[i];
            Close(bSma[i], sma.Update(bar), 1e-9);
            Close(bEma[i], ema.Update(bar), 1e-9);
            Close(bRsi[i], rsi.Update(bar), 1e-9);
            Close(bAtr[i], atr.Update(bar), 1e-9);
            Close(bBb.Middle[i], bb.Update(bar), 1e-9);
            if (bb.IsReady) Close(bBb.Upper[i], bb.Upper, 1e-7);
            Close(bMacd.Macd[i], macd.Update(bar), 1e-9);
            if (i > 60) Close(bMacd.Signal[i], macd.Signal, 1e-9);
        }
    }

    [Fact]
    public void CatalogIsLargeUniqueAndEveryEntryRuns()
    {
        Assert.True(IndicatorCatalog.Count >= 100, $"only {IndicatorCatalog.Count} indicators");
        Assert.Equal(IndicatorCatalog.Count, IndicatorCatalog.All.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var d in IndicatorCatalog.All)
        {
            var outputs = d.Compute(F);
            Assert.NotEmpty(outputs);
            foreach (var o in outputs)
            {
                Assert.Equal(F.Count, o.Values.Length);
                if (d.Category != IndicatorCategory.Pattern)
                    Assert.True(o.Values.Any(double.IsFinite), $"{d.Id}/{o.Name} produced no finite values");
            }
        }
    }

    [Fact]
    public void CatalogHonoursParameterOverrides()
    {
        var sma = IndicatorCatalog.Get("sma");
        var a = sma.Compute(F, new Dictionary<string, double> { ["period"] = 5 })[0].Values;
        Assert.Equal(Ta.Sma(F.Close, 5), a);
        Assert.Contains(IndicatorCatalog.ByCategory(IndicatorCategory.Pattern), d => d.Id == "CDL_BULLISHENGULFING");
    }
}

public class CandlestickTests
{
    private static Bar B(double o, double h, double l, double c) => new(DateTime.MinValue, o, h, l, c, 1000);

    private static Bar[] DownTrend(params Bar[] tail)
    {
        var bars = new List<Bar>();
        for (int i = 0; i < 6; i++) bars.Add(B(110 - 2 * i, 111 - 2 * i, 107 - 2 * i, 108 - 2 * i));
        bars.AddRange(tail);
        return bars.ToArray();
    }

    [Fact]
    public void DetectsDoji()
    {
        var bars = new[] { B(10, 11, 9, 10.05) };
        Assert.Equal(100, Candlesticks.Detect(bars, CandlePattern.Doji)[0]);
    }

    [Fact]
    public void DetectsBullishEngulfing()
    {
        var bars = DownTrend(B(97, 97.5, 95.5, 96), B(95.8, 98.5, 95.5, 98));
        Assert.Equal(100, Candlesticks.At(bars, bars.Length - 1, CandlePattern.BullishEngulfing));
        Assert.Equal(0, Candlesticks.At(bars, bars.Length - 1, CandlePattern.BearishEngulfing));
    }

    [Fact]
    public void DetectsHammerOnlyInDownTrend()
    {
        var hammer = B(95, 95.6, 92, 95.5);
        var bars = DownTrend(hammer);
        Assert.Equal(100, Candlesticks.At(bars, bars.Length - 1, CandlePattern.Hammer));
        Assert.Equal(0, Candlesticks.At(bars, bars.Length - 1, CandlePattern.HangingMan));
    }

    [Fact]
    public void DetectsMorningStar()
    {
        var bars = DownTrend(B(100, 100.5, 94.5, 95), B(94, 94.6, 93.4, 94.2), B(94.5, 99.5, 94.2, 99));
        Assert.Equal(100, Candlesticks.At(bars, bars.Length - 1, CandlePattern.MorningStar));
    }

    [Fact]
    public void ScanFindsPatternsOnSyntheticData()
    {
        var bars = SyntheticMarket.GeometricBrownianBars(1000, seed: 3);
        var hits = Candlesticks.Scan(bars);
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.True(h.Signal is 100 or -100));
    }

    [Fact]
    public void HeikinAshiFirstAndRecursiveValues()
    {
        var bars = new[] { B(10, 12, 9, 11), B(11, 13, 10, 12) };
        var ha = Ta.HeikinAshi(bars);
        Assert.Equal(10.5, ha[0].Open);
        Assert.Equal(10.5, ha[0].Close);
        Assert.Equal((10.5 + 10.5) / 2, ha[1].Open);
        Assert.Equal((11 + 13 + 10 + 12) / 4.0, ha[1].Close);
    }
}
