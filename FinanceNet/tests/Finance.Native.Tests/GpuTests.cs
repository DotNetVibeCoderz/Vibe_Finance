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
    public void ListsEveryBackendWithAReasonWhenUnusable()
    {
        var devices = GpuContext.ListDevices();
        Assert.Contains(devices, d => d.Backend == GpuBackend.Cpu && d.IsUsable);
        foreach (var backend in new[] { GpuBackend.Rocm, GpuBackend.Vulkan, GpuBackend.DirectML })
            Assert.Contains(devices, d => d.Backend == backend);
        Assert.All(devices.Where(d => !d.IsUsable), d => Assert.False(string.IsNullOrWhiteSpace(d.Note)));
    }

    [Fact]
    public void UnavailableBackendThrowsWithExplanation()
    {
        // Pick a backend this machine cannot run (none if every backend is present, e.g. a ROCm box with Vulkan).
        var devices = GpuContext.ListDevices();
        var missing = new[] { GpuBackend.Rocm, GpuBackend.Cuda, GpuBackend.Vulkan, GpuBackend.DirectML }
            .FirstOrDefault(b => !devices.Any(d => d.Backend == b && d.IsUsable), GpuBackend.Cpu);
        if (missing == GpuBackend.Cpu) Assert.Skip("every backend is available here");
        var e = Assert.Throws<NotSupportedException>(() => GpuContext.Create(missing));
        Assert.Contains(missing == GpuBackend.DirectML ? "DirectML" : missing.ToString(), e.Message);
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

/// <summary>
/// The Vulkan, Direct3D 12 (hardware and WARP) and HIP engines must reproduce the ILGPU kernels: same RNG counters,
/// so Monte Carlo sums agree to rounding, and batch Black-Scholes agrees with the managed closed form. Each case is
/// skipped when its device is not present.
/// </summary>
public class GpuBackendParityTests : IClassFixture<CpuGpuFixture>
{
    private readonly GpuContext _cpu;

    public GpuBackendParityTests(CpuGpuFixture fixture) => _cpu = fixture.Context;

    public static TheoryData<string> Engines() => new("Vulkan", "DirectML", "WARP", "Rocm");

    private static GpuContext? Open(string engine)
    {
        var devices = GpuContext.ListDevices().Where(d => d.IsUsable).ToList();
        GpuDeviceInfo? d = engine switch
        {
            "Vulkan" => devices.FirstOrDefault(x => x.Backend == GpuBackend.Vulkan),
            "DirectML" => devices.FirstOrDefault(x => x.Backend == GpuBackend.DirectML && !x.Name.Contains("(WARP")),
            "WARP" => devices.FirstOrDefault(x => x.Backend == GpuBackend.DirectML && x.Name.Contains("(WARP")),
            _ => devices.FirstOrDefault(x => x.Backend == GpuBackend.Rocm && x.Note?.Contains("HIP") == true),
        };
        return d is null ? null : GpuContext.Create(d);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void KernelsMatchTheReferenceImplementation(string engine)
    {
        using var ctx = Open(engine);
        if (ctx is null) Assert.Skip($"no {engine} device");
        var pricer = new GpuOptionPricer(ctx);

        // Batch Black-Scholes against the managed closed form, calls and puts, with expired and zero-vol rows.
        var rng = new Random(4);
        int n = 2000;
        double[] s = new double[n], k = new double[n], t = new double[n], r = new double[n], q = new double[n], v = new double[n];
        bool[] calls = new bool[n];
        for (int i = 0; i < n; i++)
        {
            s[i] = 50 + 100 * rng.NextDouble(); k[i] = 50 + 100 * rng.NextDouble();
            t[i] = i % 97 == 0 ? 0 : 0.01 + 3 * rng.NextDouble(); r[i] = 0.1 * rng.NextDouble(); q[i] = 0.05 * rng.NextDouble();
            v[i] = i % 89 == 0 ? 0 : 0.05 + 0.8 * rng.NextDouble(); calls[i] = i % 2 == 0;
        }
        var bs = pricer.BlackScholesBatch(s, k, t, r, q, v, calls);
        for (int i = 0; i < n; i++)
        {
            var m = ManagedKernels.BlackScholes(s[i], k[i], t[i], r[i], q[i], v[i], calls[i]);
            Assert.True(Math.Abs(m.Price - bs.Price[i]) <= 1e-10 * Math.Max(1, m.Price), $"{engine} row {i}: price {bs.Price[i]} vs {m.Price}");
            Assert.True(Math.Abs(m.Delta - bs.Delta[i]) <= 1e-10, $"{engine} row {i}: delta {bs.Delta[i]} vs {m.Delta}");
            Assert.True(Math.Abs(m.Vega - bs.Vega[i]) <= 1e-9 * Math.Max(1, Math.Abs(m.Vega)), $"{engine} row {i}: vega");
            Assert.True(Math.Abs(m.Theta - bs.Theta[i]) <= 1e-9 * Math.Max(1, Math.Abs(m.Theta)), $"{engine} row {i}: theta");
        }

        // Monte Carlo: same counters as the host reference, so the estimates agree to rounding.
        var euro = pricer.MonteCarloEuropean(100, 105, 1, 0.04, 0.01, 0.25, true, paths: 200_000, seed: 3);
        var host = pricer.MonteCarloEuropeanReference(100, 105, 1, 0.04, 0.01, 0.25, true, paths: 200_000, seed: 3);
        Assert.True(Math.Abs(euro.Price - host.Price) <= 1e-9 * host.Price, $"{engine} european {euro.Price:R} vs host {host.Price:R}");
        Assert.True(Math.Abs(euro.StdError - host.StdError) <= 1e-7 * host.StdError);

        var asian = pricer.MonteCarloAsian(100, 100, 1, 0.05, 0, 0.2, true, steps: 24, paths: 20_000, seed: 8);
        var asianCpu = new GpuOptionPricer(_cpu).MonteCarloAsian(100, 100, 1, 0.05, 0, 0.2, true, steps: 24, paths: 20_000, seed: 8);
        Assert.True(Math.Abs(asian.Price - asianCpu.Price) <= 1e-9 * asianCpu.Price, $"{engine} asian {asian.Price:R} vs cpu {asianCpu.Price:R}");

        var cov = Matrix.FromArray(new[,] { { 0.04, 0.012, 0.006 }, { 0.012, 0.09, 0.0135 }, { 0.006, 0.0135, 0.0625 } });
        var w = new[] { 0.5, 0.3, 0.2 };
        var mu = new[] { 0.07, 0.1, 0.08 };
        var var99 = new GpuRiskEngine(ctx).MonteCarloVaR(w, mu, cov, 10 / 252.0, 50_000, 0.99, 5);
        var varHost = GpuRiskEngine.MonteCarloVaRReference(w, mu, cov, 10 / 252.0, 50_000, 0.99, 5);
        Assert.True(Math.Abs(var99.VaR - varHost.VaR) <= 1e-12, $"{engine} VaR {var99.VaR:R} vs host {varHost.VaR:R}");
        Assert.True(Math.Abs(var99.CVaR - varHost.CVaR) <= 1e-12);
        Assert.Equal(ctx.Device.Name, var99.Device);
    }
}

public class GpuBufferReuseTests
{
    /// <summary>Engines reuse grow-only buffers between launches: shrinking and growing batches must stay exact.</summary>
    [Theory]
    [InlineData("Cpu")]
    [InlineData("OpenCL")]
    [InlineData("Vulkan")]
    [InlineData("DirectML")]
    [InlineData("WARP")]
    [InlineData("Rocm")]
    public void BatchesOfChangingSizeStayCorrect(string engine)
    {
        var devices = GpuContext.ListDevices().Where(d => d.IsUsable).ToList();
        GpuDeviceInfo? d = engine switch
        {
            "Vulkan" => devices.FirstOrDefault(x => x.Backend == GpuBackend.Vulkan),
            "DirectML" => devices.FirstOrDefault(x => x.Backend == GpuBackend.DirectML && !x.Name.Contains("(WARP")),
            "WARP" => devices.FirstOrDefault(x => x.Backend == GpuBackend.DirectML && x.Name.Contains("(WARP")),
            "Cpu" => devices.FirstOrDefault(x => x.Backend == GpuBackend.Cpu),
            "OpenCL" => devices.FirstOrDefault(x => x.Backend == GpuBackend.OpenCL),
            _ => devices.FirstOrDefault(x => x.Backend == GpuBackend.Rocm && x.Note?.Contains("HIP") == true),
        };
        if (d is null) Assert.Skip($"no {engine} device");
        using var ctx = GpuContext.Create(d);
        var pricer = new GpuOptionPricer(ctx);
        foreach (int n in new[] { 3000, 7, 1, 12_345, 50 })
        {
            var s = Enumerable.Range(0, n).Select(i => 80.0 + i % 41).ToArray();
            var k = Enumerable.Repeat(100.0, n).ToArray();
            var t = Enumerable.Range(0, n).Select(i => 0.25 + i % 5 * 0.5).ToArray();
            var r = Enumerable.Repeat(0.03, n).ToArray();
            var q = Enumerable.Repeat(0.01, n).ToArray();
            var v = Enumerable.Range(0, n).Select(i => 0.1 + i % 7 * 0.05).ToArray();
            var c = Enumerable.Range(0, n).Select(i => i % 3 != 0).ToArray();
            var bs = pricer.BlackScholesBatch(s, k, t, r, q, v, c);
            Assert.Equal(n, bs.Price.Length);
            for (int i = 0; i < n; i++)
            {
                double m = ManagedKernels.BlackScholes(s[i], k[i], t[i], r[i], q[i], v[i], c[i]).Price;
                Assert.True(Math.Abs(m - bs.Price[i]) <= 1e-10 * Math.Max(1, m), $"{engine} n={n} row {i}: {bs.Price[i]} vs {m}");
            }
            var mc = pricer.MonteCarloEuropean(100, 100, 1, 0.05, 0, 0.2, true, paths: 1000 + n, seed: 9);
            var host = pricer.MonteCarloEuropeanReference(100, 100, 1, 0.05, 0, 0.2, true, paths: 1000 + n, seed: 9);
            Assert.True(Math.Abs(mc.Price - host.Price) <= 1e-9 * host.Price, $"{engine} n={n}: MC {mc.Price} vs {host.Price}");
        }
    }
}

public class GpuKernelSourceTests
{
    /// <summary>The embedded DXIL must have been compiled from the current finance.hlsl (run Kernels/compile-dxil.ps1).</summary>
    [Fact]
    public void EmbeddedDxilMatchesTheHlslSource()
    {
        var text = D3D12Engine.KernelSource().Replace("\r\n", "\n");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        Assert.Equal(hash, D3D12Engine.DxilSourceHash());
    }
}
