using Finance.Core;
using Finance.ML;

namespace Finance.AI.Tests;

/// <summary>
/// N-BEATS and TFT on series whose structure is known, scored out of sample against naive baselines, plus the
/// interpretability outputs (N-BEATS components, TFT quantiles, attention and variable importance).
/// </summary>
public class NeuralForecastingTests
{
    /// <summary>Level 10, slope 0.05 per step, a 12-step season of amplitude 2, Gaussian noise 0.2.</summary>
    private static double[] TrendSeason(int n, ulong seed)
    {
        var rng = new FastRandom(seed);
        return Enumerable.Range(0, n).Select(t => 10 + 0.05 * t + 2 * Math.Sin(2 * Math.PI * t / 12) + 0.2 * rng.NextGaussian()).ToArray();
    }

    private static double Mae(double[] a, ReadOnlySpan<double> b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]);
        return s / a.Length;
    }

    [Fact]
    public void NBeatsBeatsNaiveForecastsAndSeparatesTrendFromSeason()
    {
        var y = TrendSeason(400, 1);
        var model = new NBeatsForecaster(lookback: 36, horizon: 12, new NBeatsOptions { Steps = 800, Width = 32, BlocksPerStack = 1 });
        model.Fit(y.AsSpan(0, 340));

        double nbeats = 0, naive = 0, seasonalNaive = 0;
        int folds = 0;
        for (int origin = 340; origin + 12 <= 400; origin += 6, folds++)
        {
            var hist = y.AsSpan(0, origin);
            var actual = y.AsSpan(origin, 12);
            nbeats += Mae(model.Forecast(hist), actual);
            naive += Mae(Enumerable.Repeat(y[origin - 1], 12).ToArray(), actual);
            seasonalNaive += Mae(y[(origin - 12)..origin], actual);
        }
        nbeats /= folds; naive /= folds; seasonalNaive /= folds;
        Assert.True(nbeats < 0.6 * naive, $"N-BEATS MAE {nbeats:F3} vs last-value {naive:F3}");
        Assert.True(nbeats < seasonalNaive, $"N-BEATS MAE {nbeats:F3} vs seasonal naive {seasonalNaive:F3}");
        Assert.True(model.LossHistory[^1] < model.LossHistory[0]);

        var (total, parts) = model.Decompose(y.AsSpan(0, 388));
        var season = parts[NBeatsStackType.Seasonality];
        Assert.InRange(season.Max() - season.Min(), 2.0, 6.0);          // a 2-amplitude wave spans ~4 peak to trough
        for (int i = 0; i < total.Length; i++) Assert.Equal(total[i], parts.Values.Sum(p => p[i]), 9);
    }
}

public class TftTests
{
    [Fact]
    public void GradientsMatchFiniteDifferencesThroughTheWholeModel()
    {
        var tft = new TemporalFusionTransformer(lookback: 4, horizon: 2, pastCovariates: 1, knownCovariates: 1,
            new TftOptions { Hidden = 4, Heads = 2, Seed = 3 });
        var rng = new FastRandom(5);
        double[][] enc = Enumerable.Range(0, 4).Select(_ => Enumerable.Range(0, tft.EncoderVariables.Length).Select(_ => rng.NextGaussian()).ToArray()).ToArray();
        double[][] dec = Enumerable.Range(0, 2).Select(_ => Enumerable.Range(0, tft.DecoderVariables.Length).Select(_ => rng.NextGaussian()).ToArray()).ToArray();
        double[] truth = { 0.7, -0.4 };
        int checkedCount = 0;
        // Sample elements from every parameter tensor: embeddings, VSNs, LSTMs, attention, output head.
        for (int p = 0; p < tft.ParameterTensorCount; p += 3)
        {
            var (a, n) = tft.CheckGradient(enc, dec, truth, p, 0);
            Assert.True(Math.Abs(a - n) <= 1e-5 + 1e-3 * Math.Abs(n), $"parameter {p}: autodiff {a:E4} vs finite difference {n:E4}");
            checkedCount++;
        }
        Assert.True(checkedCount > 20);
    }

    [Fact]
    public void LearnsAKnownFutureDriverAndCalibratesItsQuantiles()
    {
        // y = 5 + 2·sin(2πt/7) + 3·promo + noise; promo is known in advance, "noise" is an irrelevant past covariate.
        const int n = 700;
        var rng = new FastRandom(9);
        var promo = new double[n, 1];
        var noise = new double[n, 1];
        var y = new double[n];
        for (int t = 0; t < n; t++)
        {
            promo[t, 0] = rng.NextDouble() < 0.25 ? 1 : 0;
            noise[t, 0] = rng.NextGaussian();
            y[t] = 5 + 2 * Math.Sin(2 * Math.PI * t / 7) + 3 * promo[t, 0] + 0.3 * rng.NextGaussian();
        }
        var tft = new TemporalFusionTransformer(lookback: 14, horizon: 3, pastCovariates: 1, knownCovariates: 1,
            new TftOptions { Hidden = 12, Heads = 2, Steps = 250, BatchSize = 16, LearningRate = 5e-3 }, new[] { "noise" }, new[] { "promo" });
        tft.Fit(y.AsSpan(0, 600), Slice(noise, 600), Slice(promo, 600));

        double tftMae = 0, naiveMae = 0, inside = 0, total = 0;
        TftForecast? last = null;
        for (int origin = 600; origin + 3 <= n; origin += 3)
        {
            var f = tft.Predict(y.AsSpan(0, origin), Slice(noise, origin), Slice(promo, origin + 3));
            last = f;
            var med = f.Median;
            var lo = f.At(0.1);
            var hi = f.At(0.9);
            for (int h = 0; h < 3; h++)
            {
                double actual = y[origin + h];
                tftMae += Math.Abs(med[h] - actual);
                naiveMae += Math.Abs(y[origin + h - 7] - actual);       // seasonal naive cannot see the promo
                if (actual >= lo[h] && actual <= hi[h]) inside++;
                total++;
            }
        }
        tftMae /= total;
        naiveMae /= total;
        Assert.True(tftMae < 0.6 * naiveMae, $"TFT MAE {tftMae:F3} vs seasonal naive {naiveMae:F3}");
        Assert.InRange(inside / total, 0.6, 0.97);                      // nominal 80% interval
        Assert.True(tft.LossHistory[^1] < tft.LossHistory[0]);

        // Interpretability: the irrelevant past covariate gets the smallest encoder weight, and switching the known
        // future promo on/off moves the forecast by about its true effect (3).
        int noiseIdx = Array.IndexOf(last!.EncoderVariables, "noise");
        Assert.Equal(last.EncoderImportance.Min(), last.EncoderImportance[noiseIdx]);
        var on = Slice(promo, n);
        var off = Slice(promo, n);
        for (int t = 690; t < 693; t++) { on[t, 0] = 1; off[t, 0] = 0; }
        var withPromo = tft.Predict(y.AsSpan(0, 690), Slice(noise, 690), Slice(on, 693)).Median;
        var without = tft.Predict(y.AsSpan(0, 690), Slice(noise, 690), Slice(off, 693)).Median;
        for (int h = 0; h < 3; h++) Assert.InRange(withPromo[h] - without[h], 2.0, 4.0);
        for (int h = 0; h < 3; h++)
        {
            double rowSum = 0;
            for (int j = 0; j < last.Attention.GetLength(1); j++) rowSum += last.Attention[h, j];
            Assert.Equal(1.0, rowSum, 6);
            Assert.Equal(0.0, last.Attention[0, 14 + 1], 9);             // causal: step 1 cannot see step 2
        }
    }

    private static double[,] Slice(double[,] a, int rows)
    {
        var r = new double[rows, a.GetLength(1)];
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < a.GetLength(1); j++) r[i, j] = a[i, j];
        return r;
    }
}
