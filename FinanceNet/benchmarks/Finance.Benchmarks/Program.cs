using System.Diagnostics;
using BenchmarkDotNet.Running;
using Finance.Benchmarks;
using Finance.Core;
using Finance.GPU;
using Finance.Mathematics;
using Finance.RustBridge;

// Usage:
//   dotnet run -c Release --project benchmarks/Finance.Benchmarks -- --quick     (stopwatch table, ~1 minute)
//   dotnet run -c Release --project benchmarks/Finance.Benchmarks -- --filter *  (BenchmarkDotNet suites)
if (args.Contains("--quick"))
{
    QuickBench.Run();
    return;
}
BenchmarkSwitcher.FromAssembly(typeof(IndicatorBenchmarks).Assembly).Run(args);

/// <summary>
/// Stopwatch comparison. Every variant is warmed up with 35 calls on a small input (past the
/// tiered-JIT recompilation point and GPU kernel compilation), then the variants of a case are
/// run alternately in this one process and the best of several rounds is reported, so a laptop
/// that throttles mid-run penalises all variants alike.
/// </summary>
internal static class QuickBench
{
    private const int WarmupCalls = 35;
    private const int Rounds = 7;

    private sealed record Variant(string Name, Func<int, Action> Make);

    public static void Run()
    {
        Console.WriteLine($"Finance.Net quick benchmark - {FinanceInfo.CreditEnglish}");
        Console.WriteLine($"{Environment.ProcessorCount} logical cores, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine(RustAccelerator.Describe());
        foreach (var d in GpuContext.ListDevices()) Console.WriteLine("  device: " + d);
        using var gpu = GpuContext.CreateBest();
        Console.WriteLine($"GPU context: {gpu}");
        var pricer = new GpuOptionPricer(gpu);
        var risk = new GpuRiskEngine(gpu);
        string gpuName = gpu.IsCpu ? "ILGPU CPU accel." : $"GPU ({gpu.Backend})";
        Console.WriteLine();
        Console.WriteLine($"{"case",-34} {"variant",-18} {"best ms",10} {"vs first",9}");
        Console.WriteLine(new string('-', 74));

        Case("SMA(20), 1M prices", 1_000_000, 2_000,
            new("Managed", n => { var x = Inputs.Prices(n); var o = new double[n]; return () => ManagedKernels.Sma(x, 20, o); }),
            new("Rust", n => { var x = Inputs.Prices(n); var o = new double[n]; return () => NativeKernels.Sma(x, 20, o); }));
        Case("EMA(20), 1M prices", 1_000_000, 2_000,
            new("Managed", n => { var x = Inputs.Prices(n); var o = new double[n]; return () => ManagedKernels.Ema(x, 20, o); }),
            new("Rust", n => { var x = Inputs.Prices(n); var o = new double[n]; return () => NativeKernels.Ema(x, 20, o); }));
        Case("RSI(14), 1M prices", 1_000_000, 2_000,
            new("Managed", n => { var x = Inputs.Prices(n); var o = new double[n]; return () => ManagedKernels.Rsi(x, 14, o); }),
            new("Rust", n => { var x = Inputs.Prices(n); var o = new double[n]; return () => NativeKernels.Rsi(x, 14, o); }));
        Case("Rolling std(20), 1M prices", 1_000_000, 2_000,
            new("Managed", n => { var x = Inputs.Prices(n); var o = new double[n]; return () => ManagedKernels.RollingStd(x, 20, 1, o); }),
            new("Rust", n => { var x = Inputs.Prices(n); var o = new double[n]; return () => NativeKernels.RollingStd(x, 20, 1, o); }));

        Case("Black-Scholes + 5 greeks, 1M", 1_000_000, 1_000,
            new("Managed", n => BsAction(n, native: false)),
            new("Rust", n => BsAction(n, native: true)),
            new(gpuName, n => { var o = Inputs.Options(n); return () => pricer.BlackScholesBatch(o.S, o.K, o.T, o.R, o.Q, o.V, o.C); }));

        var spec = new EuropeanOptionSpec(100, 100, 1, 0.05, 0, 0.2, true);
        Case("MC European, 4M paths", 4_000_000, 2_000,
            new("Managed (parallel)", n => () => ManagedKernels.MonteCarloEuropean(spec, n)),
            new("Rust (all threads)", n => () => NativeKernels.MonteCarloEuropean(spec, n)),
            new("Rust (1 thread)", n => () => NativeKernels.MonteCarloEuropean(spec, n, threads: 1)),
            new(gpuName, n => () => pricer.MonteCarloEuropean(100, 100, 1, 0.05, 0, 0.2, true, n)));

        Case("Historical VaR 99%, 1M returns", 1_000_000, 2_000,
            new("Managed", n => { var r = FastIndicators.LogReturns(Inputs.Prices(n + 1)); return () => ManagedKernels.HistoricalVaR(r, 0.99); }),
            new("Rust", n => { var r = FastIndicators.LogReturns(Inputs.Prices(n + 1)); return () => NativeKernels.HistoricalVaR(r, 0.99); }));

        var (w, mu, cov) = Portfolio(10);
        Case("MC portfolio VaR, 10 assets, 200k", 200_000, 1_000,
            new("Host threads", n => () => GpuRiskEngine.MonteCarloVaRReference(w, mu, cov, 10 / 252.0, n)),
            new(gpuName, n => () => risk.MonteCarloVaR(w, mu, cov, 10 / 252.0, n)));
    }

    private static Action BsAction(int n, bool native)
    {
        var o = Inputs.Options(n);
        var r = new BlackScholesBatchResult(n);
        return () =>
        {
            var batch = new OptionBatch(o.S, o.K, o.T, o.R, o.Q, o.V, o.C);
            if (native) NativeKernels.BlackScholesBatch(batch, r.Price, r.Delta, r.Gamma, r.Vega, r.Theta, r.Rho);
            else ManagedKernels.BlackScholesBatch(batch, r.Price, r.Delta, r.Gamma, r.Vega, r.Theta, r.Rho);
        };
    }

    private static (double[] W, double[] Mu, Matrix Cov) Portfolio(int n)
    {
        var w = Enumerable.Repeat(1.0 / n, n).ToArray();
        var mu = Enumerable.Range(0, n).Select(i => 0.04 + 0.01 * i).ToArray();
        var cov = new Matrix(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double si = 0.15 + 0.02 * i, sj = 0.15 + 0.02 * j;
                cov[i, j] = (i == j ? 1 : 0.3) * si * sj;
            }
        return (w, mu, cov);
    }

    private static void Case(string name, int size, int warmSize, params Variant[] variants)
    {
        var big = new Action[variants.Length];
        for (int v = 0; v < variants.Length; v++)
        {
            var small = variants[v].Make(warmSize);
            for (int i = 0; i < WarmupCalls; i++) small();
            big[v] = variants[v].Make(size);
            big[v](); // first full-size call (allocations, page faults) is not timed
        }
        var best = Enumerable.Repeat(double.MaxValue, variants.Length).ToArray();
        for (int round = 0; round < Rounds; round++)
            for (int v = 0; v < variants.Length; v++)
            {
                var sw = Stopwatch.StartNew();
                big[v]();
                best[v] = Math.Min(best[v], sw.Elapsed.TotalMilliseconds);
            }
        for (int v = 0; v < variants.Length; v++)
            Console.WriteLine($"{(v == 0 ? name : ""),-34} {variants[v].Name,-18} {best[v],10:F2} {best[0] / best[v],8:F2}x");
    }
}
