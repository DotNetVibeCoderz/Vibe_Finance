using Finance.Core.Market;
using Finance.RustBridge;
using Xunit;

namespace Finance.Native.Tests;

public class RustBridgeTests
{
    private static readonly double[] Prices = SyntheticMarket.GeometricBrownianBars(2_000, seed: 11).Select(b => b.Close).ToArray();

    private static void AssertClose(ReadOnlySpan<double> expected, ReadOnlySpan<double> actual, double tol)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (double.IsNaN(expected[i])) { Assert.True(double.IsNaN(actual[i]), $"index {i}: expected NaN, got {actual[i]}"); continue; }
            double scale = Math.Max(1, Math.Abs(expected[i]));
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tol * scale, $"index {i}: {expected[i]} vs {actual[i]}");
        }
    }

    [Fact]
    public void NativeLibraryIsLoaded()
    {
        Assert.True(RustAccelerator.IsLoaded, RustAccelerator.LoadError);
        Assert.Equal(Finance.Core.FinanceInfo.Version, RustAccelerator.NativeVersion); // crate and assemblies move in lockstep
        Assert.Contains("Rust accelerator", RustAccelerator.Describe());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(200)]
    public void WindowIndicatorsMatchManaged(int period)
    {
        var a = new double[Prices.Length];
        var b = new double[Prices.Length];
        NativeKernels.Sma(Prices, period, a); ManagedKernels.Sma(Prices, period, b); AssertClose(b, a, 1e-13);
        NativeKernels.Ema(Prices, period, a); ManagedKernels.Ema(Prices, period, b); AssertClose(b, a, 1e-13);
        NativeKernels.Rsi(Prices, period, a); ManagedKernels.Rsi(Prices, period, b); AssertClose(b, a, 1e-12);
        if (period > 1)
        {
            NativeKernels.RollingStd(Prices, period, 1, a); ManagedKernels.RollingStd(Prices, period, 1, b); AssertClose(b, a, 1e-12);
        }
    }

    [Fact]
    public void SmaMatchesNaiveDefinition()
    {
        var sma = FastIndicators.Sma(Prices, 10);
        for (int i = 9; i < Prices.Length; i += 37)
            Assert.Equal(Prices.Skip(i - 9).Take(10).Average(), sma[i], 1e-9);
        Assert.True(double.IsNaN(sma[8]));
    }

    [Fact]
    public void BollingerMatchesManagedAndBracketsMean()
    {
        var (m, u, l) = FastIndicators.Bollinger(Prices, 20, 2);
        var mm = new double[Prices.Length];
        var uu = new double[Prices.Length];
        var ll = new double[Prices.Length];
        ManagedKernels.Bollinger(Prices, 20, 2, mm, uu, ll);
        AssertClose(mm, m, 1e-13);
        AssertClose(uu, u, 1e-12);
        AssertClose(ll, l, 1e-12);
        for (int i = 19; i < Prices.Length; i++) Assert.True(l[i] <= m[i] && m[i] <= u[i]);
    }

    [Fact]
    public void RsiStaysInRange()
    {
        var rsi = FastIndicators.Rsi(Prices, 14);
        Assert.True(double.IsNaN(rsi[13]));
        for (int i = 14; i < rsi.Length; i++) Assert.InRange(rsi[i], 0, 100);
    }

    [Fact]
    public void LogReturnsAndDrawdownMatchManaged()
    {
        var a = FastIndicators.LogReturns(Prices);
        var b = new double[Prices.Length - 1];
        ManagedKernels.LogReturns(Prices, b);
        AssertClose(b, a, 1e-15);
        Assert.Equal(ManagedKernels.MaxDrawdown(Prices), NativeKernels.MaxDrawdown(Prices));
        var dd = FastIndicators.MaxDrawdown(new double[] { 100, 120, 90, 110, 60, 130 });
        Assert.Equal(0.5, dd.Depth, 15);
        Assert.Equal(1, dd.PeakIndex);
        Assert.Equal(4, dd.TroughIndex);
    }

    [Theory]
    [InlineData(0.95)]
    [InlineData(0.99)]
    [InlineData(0.975)]
    public void HistoricalVaRMatchesSortedDefinition(double confidence)
    {
        var r = FastIndicators.LogReturns(Prices);
        var sorted = r.OrderBy(x => x).ToArray();
        int k = ManagedKernels.TailIndex(r.Length, confidence);
        double var = -sorted[k], cvar = -sorted.Take(k + 1).Average();
        var native = NativeKernels.HistoricalVaR(r, confidence);
        var managed = ManagedKernels.HistoricalVaR(r, confidence);
        Assert.Equal(var, native.VaR, 15);
        Assert.Equal(var, managed.VaR, 15);
        Assert.Equal(cvar, native.CVaR, 12);
        Assert.Equal(cvar, managed.CVaR, 12);
        Assert.True(native.CVaR >= native.VaR);
    }

    [Fact]
    public void TailIndexIgnoresRepresentationError()
    {
        Assert.Equal(4, ManagedKernels.TailIndex(100, 0.95)); // (1-0.95)*100 = 5.000000000000004
        Assert.Equal(0, ManagedKernels.TailIndex(10, 0.999));
    }

    [Fact]
    public void QuickselectPlacesKthElement()
    {
        var rng = new Finance.Core.FastRandom(5);
        for (int trial = 0; trial < 50; trial++)
        {
            int n = 1 + rng.NextInt(300);
            var a = Enumerable.Range(0, n).Select(_ => Math.Round(rng.NextGaussian(), 1)).ToArray(); // many ties
            int k = rng.NextInt(n);
            var sorted = a.OrderBy(x => x).ToArray();
            ManagedKernels.Select(a, k);
            Assert.Equal(sorted[k], a[k]);
            for (int i = 0; i < k; i++) Assert.True(a[i] <= a[k]);
            for (int i = k + 1; i < n; i++) Assert.True(a[i] >= a[k]);
        }
    }

    [Fact]
    public void CovarianceMatchesManagedAndFinanceMath()
    {
        var prices = SyntheticMarket.CorrelatedPrices(500, new[] { 0.05, 0.08, 0.1 }, new[] { 0.2, 0.3, 0.25 },
            new double[,] { { 1, 0.5, 0.2 }, { 0.5, 1, 0.3 }, { 0.2, 0.3, 1 } });
        var flat = prices.SelectMany(r => r).ToArray();
        var native = FastRisk.Covariance(flat, 500, 3);
        var managed = new double[9];
        ManagedKernels.Covariance(flat, 500, 3, 1, managed);
        AssertClose(managed, native, 1e-13);
        var reference = Finance.Mathematics.Stats.CovarianceMatrix(Finance.Mathematics.Matrix.FromRows(prices));
        AssertClose(reference.Data, native, 1e-11);
    }

    [Fact]
    public void BlackScholesTextbookValuesAndParity()
    {
        var c = ManagedKernels.BlackScholes(42, 40, 0.5, 0.1, 0, 0.2, true);
        var p = ManagedKernels.BlackScholes(42, 40, 0.5, 0.1, 0, 0.2, false);
        Assert.Equal(4.7594, c.Price, 4);
        Assert.Equal(0.8086, p.Price, 4);
        Assert.Equal(42 - 40 * Math.Exp(-0.05), c.Price - p.Price, 12);
    }

    [Fact]
    public void BlackScholesGreeksMatchFiniteDifferences()
    {
        double s = 100, k = 105, t = 0.8, r = 0.03, q = 0.01, v = 0.25, h = 1e-4;
        foreach (bool call in new[] { true, false })
        {
            var g = ManagedKernels.BlackScholes(s, k, t, r, q, v, call);
            double P(double ss, double tt, double rr, double vv) => ManagedKernels.BlackScholes(ss, k, tt, rr, q, vv, call).Price;
            Assert.Equal((P(s + h, t, r, v) - P(s - h, t, r, v)) / (2 * h), g.Delta, 6);
            Assert.Equal((P(s + h, t, r, v) - 2 * g.Price + P(s - h, t, r, v)) / (h * h), g.Gamma, 4);
            Assert.Equal((P(s, t, r, v + h) - P(s, t, r, v - h)) / (2 * h), g.Vega, 5);
            Assert.Equal(-(P(s, t + h, r, v) - P(s, t - h, r, v)) / (2 * h), g.Theta, 5);
            Assert.Equal((P(s, t, r + h, v) - P(s, t, r - h, v)) / (2 * h), g.Rho, 5);
        }
    }

    [Fact]
    public void BatchBlackScholesNativeMatchesManaged()
    {
        int n = 1000;
        var rng = new Finance.Core.FastRandom(9);
        double[] s = new double[n], k = new double[n], t = new double[n], r = new double[n], q = new double[n], v = new double[n];
        var call = new bool[n];
        for (int i = 0; i < n; i++)
        {
            s[i] = rng.NextDouble(50, 150); k[i] = rng.NextDouble(50, 150); t[i] = rng.NextDouble(0, 3);
            r[i] = rng.NextDouble(0, 0.08); q[i] = rng.NextDouble(0, 0.03); v[i] = rng.NextDouble(0, 0.8); call[i] = i % 2 == 0;
        }
        t[0] = 0; v[1] = 0; // degenerate branches
        var batch = new OptionBatch(s, k, t, r, q, v, call);
        var native = new BlackScholesBatchResult(n);
        var managed = new BlackScholesBatchResult(n);
        NativeKernels.BlackScholesBatch(batch, native.Price, native.Delta, native.Gamma, native.Vega, native.Theta, native.Rho);
        ManagedKernels.BlackScholesBatch(batch, managed.Price, managed.Delta, managed.Gamma, managed.Vega, managed.Theta, managed.Rho);
        AssertClose(managed.Price, native.Price, 1e-12);
        AssertClose(managed.Delta, native.Delta, 1e-12);
        AssertClose(managed.Gamma, native.Gamma, 1e-12);
        AssertClose(managed.Vega, native.Vega, 1e-12);
        AssertClose(managed.Theta, native.Theta, 1e-12);
        AssertClose(managed.Rho, native.Rho, 1e-12);

        // Skipped outputs: only price requested.
        var priceOnly = new double[n];
        NativeKernels.BlackScholesBatch(batch, priceOnly);
        AssertClose(managed.Price, priceOnly, 1e-12);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void MonteCarloNativeReproducesManagedAndConverges(bool call, bool antithetic)
    {
        var spec = new EuropeanOptionSpec(100, 100, 1, 0.05, 0.01, 0.2, call);
        var native = NativeKernels.MonteCarloEuropean(spec, 300_000, seed: 123, antithetic);
        var nativeOneThread = NativeKernels.MonteCarloEuropean(spec, 300_000, seed: 123, antithetic, threads: 1);
        var managed = ManagedKernels.MonteCarloEuropean(spec, 300_000, seed: 123, antithetic);
        Assert.Equal(native.Price, nativeOneThread.Price);
        Assert.Equal(managed.Price, native.Price, 10);
        Assert.Equal(managed.StdError, native.StdError, 10);
        double exact = ManagedKernels.BlackScholes(100, 100, 1, 0.05, 0.01, 0.2, call).Price;
        Assert.True(Math.Abs(native.Price - exact) < 4 * native.StdError, $"{native.Price} vs {exact} (se {native.StdError})");
    }

    [Fact]
    public void GbmNativeMatchesManaged()
    {
        int steps = 50, paths = 300;
        var native = new double[paths * (steps + 1)];
        NativeKernels.SimulateGbm(100, 0.05, 0.2, 1.0 / 252, steps, paths, 77, native);
        var managedSerial = new double[native.Length];
        ManagedKernels.SimulateGbm(100, 0.05, 0.2, 1.0 / 252, steps, paths, 77, managedSerial.AsSpan());
        var managedParallel = new double[native.Length];
        ManagedKernels.SimulateGbm(100, 0.05, 0.2, 1.0 / 252, steps, paths, 77, managedParallel);
        AssertClose(managedSerial, native, 1e-12);
        Assert.Equal(managedSerial, managedParallel);
        Assert.All(Enumerable.Range(0, paths), p => Assert.Equal(100, native[p * (steps + 1)]));
    }

    [Fact]
    public void InvalidArgumentsThrow()
    {
        var o = new double[Prices.Length];
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeKernels.Sma(Prices, 0, o));
        Assert.Throws<ArgumentException>(() => NativeKernels.Sma(Prices, 5, new double[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ManagedKernels.RollingStd(Prices, 5, 5, o));
        Assert.Throws<ArgumentOutOfRangeException>(() => FastRisk.HistoricalVaR(Prices, 1.0));
        Assert.Throws<ArgumentException>(() => FastPricing.MonteCarloEuropean(new EuropeanOptionSpec(-1, 100, 1, 0, 0, 0.2, true)));
    }
}

[CollectionDefinition("ForceManaged", DisableParallelization = true)]
public class ForceManagedCollection;

[Collection("ForceManaged")]
public class FallbackTests
{
    [Fact]
    public void FacadesFallBackToManagedWhenForced()
    {
        var prices = SyntheticMarket.GeometricBrownianBars(500, seed: 2).Select(b => b.Close).ToArray();
        var nativeSma = FastIndicators.Sma(prices, 20);
        var nativeMc = FastPricing.MonteCarloEuropean(new EuropeanOptionSpec(100, 95, 0.5, 0.02, 0, 0.3, false), 50_000, 9);
        try
        {
            RustAccelerator.ForceManaged = true;
            Assert.False(RustAccelerator.IsAvailable);
            Assert.Contains("disabled", RustAccelerator.Describe());
            var managedSma = FastIndicators.Sma(prices, 20);
            var managedMc = FastPricing.MonteCarloEuropean(new EuropeanOptionSpec(100, 95, 0.5, 0.02, 0, 0.3, false), 50_000, 9);
            for (int i = 19; i < prices.Length; i++) Assert.Equal(nativeSma[i], managedSma[i], 12);
            Assert.Equal(nativeMc.Price, managedMc.Price, 10);
        }
        finally
        {
            RustAccelerator.ForceManaged = false;
        }
    }
}
