using BenchmarkDotNet.Attributes;
using Finance.Core.Market;
using Finance.GPU;
using Finance.Mathematics;
using Finance.RustBridge;

namespace Finance.Benchmarks;

/// <summary>Shared deterministic inputs.</summary>
internal static class Inputs
{
    public static double[] Prices(int n) => SyntheticMarket.GeometricBrownianBars(n, seed: 1).Select(b => b.Close).ToArray();

    public static (double[] S, double[] K, double[] T, double[] R, double[] Q, double[] V, bool[] C) Options(int n)
    {
        var rng = new Finance.Core.FastRandom(2);
        double[] s = new double[n], k = new double[n], t = new double[n], r = new double[n], q = new double[n], v = new double[n];
        var c = new bool[n];
        for (int i = 0; i < n; i++)
        {
            s[i] = rng.NextDouble(50, 150); k[i] = rng.NextDouble(50, 150); t[i] = rng.NextDouble(0.05, 3);
            r[i] = rng.NextDouble(0, 0.08); q[i] = rng.NextDouble(0, 0.03); v[i] = rng.NextDouble(0.05, 0.8); c[i] = i % 2 == 0;
        }
        return (s, k, t, r, q, v, c);
    }
}

[MemoryDiagnoser]
public class IndicatorBenchmarks
{
    private double[] _x = null!, _out = null!;

    [Params(100_000, 1_000_000)]
    public int N { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _x = Inputs.Prices(N);
        _out = new double[N];
    }

    [Benchmark(Baseline = true)] public void SmaManaged() => ManagedKernels.Sma(_x, 20, _out);
    [Benchmark] public void SmaRust() => NativeKernels.Sma(_x, 20, _out);
    [Benchmark] public void EmaManaged() => ManagedKernels.Ema(_x, 20, _out);
    [Benchmark] public void EmaRust() => NativeKernels.Ema(_x, 20, _out);
    [Benchmark] public void RsiManaged() => ManagedKernels.Rsi(_x, 14, _out);
    [Benchmark] public void RsiRust() => NativeKernels.Rsi(_x, 14, _out);
}

[MemoryDiagnoser]
public class PricingBenchmarks
{
    private (double[] S, double[] K, double[] T, double[] R, double[] Q, double[] V, bool[] C) _o;
    private double[] _price = null!, _delta = null!;
    private GpuContext _gpu = null!;
    private GpuOptionPricer _pricer = null!;
    private static readonly EuropeanOptionSpec Spec = new(100, 100, 1, 0.05, 0, 0.2, true);

    [GlobalSetup]
    public void Setup()
    {
        _o = Inputs.Options(1_000_000);
        _price = new double[_o.S.Length];
        _delta = new double[_o.S.Length];
        _gpu = GpuContext.CreateBest();
        _pricer = new GpuOptionPricer(_gpu);
    }

    [GlobalCleanup]
    public void Cleanup() => _gpu.Dispose();

    private OptionBatch Batch => new(_o.S, _o.K, _o.T, _o.R, _o.Q, _o.V, _o.C);

    [Benchmark(Baseline = true)] public void BlackScholesManaged() => ManagedKernels.BlackScholesBatch(Batch, _price, _delta);
    [Benchmark] public void BlackScholesRust() => NativeKernels.BlackScholesBatch(Batch, _price, _delta);
    [Benchmark] public GpuGreeksBatch BlackScholesGpu() => _pricer.BlackScholesBatch(_o.S, _o.K, _o.T, _o.R, _o.Q, _o.V, _o.C);

    [Benchmark] public MonteCarloEstimate MonteCarloManaged() => ManagedKernels.MonteCarloEuropean(Spec, 2_000_000);
    [Benchmark] public MonteCarloEstimate MonteCarloRust() => NativeKernels.MonteCarloEuropean(Spec, 2_000_000);
    [Benchmark] public GpuMonteCarloResult MonteCarloGpu() => _pricer.MonteCarloEuropean(100, 100, 1, 0.05, 0, 0.2, true, 2_000_000);
}

[MemoryDiagnoser]
public class RiskBenchmarks
{
    private double[] _returns = null!;

    [GlobalSetup]
    public void Setup() => _returns = FastIndicators.LogReturns(Inputs.Prices(1_000_001));

    [Benchmark(Baseline = true)] public TailRisk HistoricalVaRManaged() => ManagedKernels.HistoricalVaR(_returns, 0.99);
    [Benchmark] public TailRisk HistoricalVaRRust() => NativeKernels.HistoricalVaR(_returns, 0.99);
}
