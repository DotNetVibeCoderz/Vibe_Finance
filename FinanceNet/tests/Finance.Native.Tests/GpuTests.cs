using Finance.GPU;
using Finance.Mathematics;
using Finance.RustBridge;
using Xunit;

namespace Finance.Native.Tests;

/// <summary>One CPU-accelerator context shared by the GPU tests (kernel compilation is the slow part).</summary>
public sealed class CpuGpuFixture : IDisposable
{
    public GpuContext Context { get; } = GpuContext.Create(GpuBackend.Cpu);
    public void Dispose() => Context.Dispose();
}

public class GpuTests : IClassFixture<CpuGpuFixture>
{
    private readonly GpuContext _ctx;

    public GpuTests(CpuGpuFixture fixture) => _ctx = fixture.Context;

    [Fact]
    public void ListsCpuAndReportsUnsupportedBackends()
    {
        var devices = GpuContext.ListDevices();
        Assert.Contains(devices, d => d.Backend == GpuBackend.Cpu && d.IsUsable);
        var dml = Assert.Single(devices, d => d.Backend == GpuBackend.DirectML);
        Assert.False(dml.IsUsable);
        Assert.Contains("ILGPU", dml.Note);
        Assert.Contains(devices, d => d.Backend == GpuBackend.Vulkan && !d.IsUsable);
    }

    [Theory]
    [InlineData(GpuBackend.DirectML)]
    [InlineData(GpuBackend.Vulkan)]
    public void UnsupportedBackendsThrowWithExplanation(GpuBackend backend)
    {
        var e = Assert.Throws<NotSupportedException>(() => GpuContext.Create(backend));
        Assert.Contains("ILGPU", e.Message);
    }

    [Fact]
    public void CreateBestAlwaysYieldsAUsableContext()
    {
        using var best = GpuContext.CreateBest();
        var r = new GpuOptionPricer(best).BlackScholesBatch(new double[] { 42 }, new double[] { 40 }, new double[] { 0.5 },
            new double[] { 0.1 }, new double[] { 0 }, new double[] { 0.2 }, new[] { true });
        Assert.Equal(4.7594, r.Price[0], 4);
    }

    [Fact]
    public void BatchBlackScholesMatchesManagedKernels()
    {
        int n = 512;
        var rng = new Finance.Core.FastRandom(1);
        double[] s = new double[n], k = new double[n], t = new double[n], r = new double[n], q = new double[n], v = new double[n];
        var call = new bool[n];
        for (int i = 0; i < n; i++)
        {
            s[i] = rng.NextDouble(50, 150); k[i] = rng.NextDouble(50, 150); t[i] = rng.NextDouble(0.01, 3);
            r[i] = rng.NextDouble(0, 0.08); q[i] = rng.NextDouble(0, 0.03); v[i] = rng.NextDouble(0.05, 0.8); call[i] = i % 3 != 0;
        }
        var gpu = new GpuOptionPricer(_ctx).BlackScholesBatch(s, k, t, r, q, v, call);
        for (int i = 0; i < n; i++)
        {
            var m = ManagedKernels.BlackScholes(s[i], k[i], t[i], r[i], q[i], v[i], call[i]);
            Assert.Equal(m.Price, gpu.Price[i], 10);
            Assert.Equal(m.Delta, gpu.Delta[i], 10);
            Assert.Equal(m.Gamma, gpu.Gamma[i], 10);
            Assert.Equal(m.Vega, gpu.Vega[i], 9);
            Assert.Equal(m.Theta, gpu.Theta[i], 9);
            Assert.Equal(m.Rho, gpu.Rho[i], 9);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EuropeanMonteCarloMatchesClosedFormAndHostReference(bool call)
    {
        var pricer = new GpuOptionPricer(_ctx);
        var gpu = pricer.MonteCarloEuropean(100, 105, 1, 0.04, 0.01, 0.25, call, paths: 400_000, seed: 3);
        var host = pricer.MonteCarloEuropeanReference(100, 105, 1, 0.04, 0.01, 0.25, call, paths: 400_000, seed: 3);
        double exact = ManagedKernels.BlackScholes(100, 105, 1, 0.04, 0.01, 0.25, call).Price;
        Assert.Equal(host.Price, gpu.Price, 11);
        Assert.True(Math.Abs(gpu.Price - exact) < 4 * gpu.StdError, $"{gpu.Price} vs {exact} se {gpu.StdError}");
        Assert.True(gpu.StdError < 0.02);
    }

    [Fact]
    public void AsianCallLiesBetweenGeometricAsianAndEuropean()
    {
        double s = 100, k = 100, t = 1, r = 0.05, q = 0, v = 0.2;
        int m = 50;
        var asian = new GpuOptionPricer(_ctx).MonteCarloAsian(s, k, t, r, q, v, true, steps: m, paths: 100_000, seed: 8);

        // Discrete geometric-average Asian call (closed form) is a lower bound for the arithmetic one.
        double dt = t / m;
        double muG = Math.Log(s) + (r - q - 0.5 * v * v) * dt * (m + 1) / 2.0;
        double sigG = v * Math.Sqrt(dt * (m + 1) * (2.0 * m + 1) / (6.0 * m));
        double d2 = (muG - Math.Log(k)) / sigG, d1 = d2 + sigG;
        double geo = Math.Exp(-r * t) * (Math.Exp(muG + 0.5 * sigG * sigG) * Normal.Cdf(d1) - k * Normal.Cdf(d2));
        double euro = ManagedKernels.BlackScholes(s, k, t, r, q, v, true).Price;

        Assert.True(asian.Price > geo - 3 * asian.StdError, $"asian {asian.Price} geo {geo}");
        Assert.True(asian.Price < euro, $"asian {asian.Price} euro {euro}");
        Assert.True(asian.Price - geo < 0.25, $"arithmetic-geometric gap too large: {asian.Price - geo}");
    }

    [Fact]
    public void PortfolioVaRMatchesHostReference()
    {
        var cov = Matrix.FromArray(new[,] { { 0.04, 0.012, 0.006 }, { 0.012, 0.09, 0.0135 }, { 0.006, 0.0135, 0.0625 } });
        var w = new[] { 0.5, 0.3, 0.2 };
        var mu = new[] { 0.07, 0.1, 0.08 };
        var gpu = new GpuRiskEngine(_ctx).MonteCarloVaR(w, mu, cov, 10 / 252.0, 100_000, 0.99, 5);
        var host = GpuRiskEngine.MonteCarloVaRReference(w, mu, cov, 10 / 252.0, 100_000, 0.99, 5);
        Assert.Equal(host.VaR, gpu.VaR, 12);
        Assert.Equal(host.CVaR, gpu.CVaR, 12);
        Assert.True(gpu.CVaR > gpu.VaR && gpu.VaR > 0);
    }

    [Fact]
    public void SingleAssetVaRMatchesLognormalQuantile()
    {
        double sigma = 0.3, mu = 0.05, h = 1.0 / 12;
        var cov = Matrix.FromArray(new[,] { { sigma * sigma } });
        var res = new GpuRiskEngine(_ctx).MonteCarloVaR(new[] { 1.0 }, new[] { mu }, cov, h, 400_000, 0.99, 13);
        double exact = 1 - Math.Exp((mu - 0.5 * sigma * sigma) * h + sigma * Math.Sqrt(h) * Normal.InverseCdf(0.01));
        Assert.Equal(exact, res.VaR, 3);
    }
}
