using ILGPU;
using ILGPU.Runtime;

namespace Finance.GPU;

/// <summary>
/// One way of running Finance.GPU's four kernels. Every engine implements the same computation with the same
/// counter-based RNG (see <see cref="GpuMath"/>), so results agree across backends to floating-point rounding.
/// Calls are serialised by <see cref="GpuContext"/>.
/// </summary>
internal interface IGpuEngine : IDisposable
{
    /// <summary>Per-thread payoff sums and sums of squares for the European (<paramref name="asian"/> false) or Asian kernel.</summary>
    void MonteCarlo(bool asian, McArgs args, int threads, double[] sums, double[] sumsq);

    /// <summary>Batch Black-Scholes: 7 input planes of n, 6 output planes.</summary>
    void BlackScholes(double[] packed, int n, double[] output);

    /// <summary>One simulated portfolio return per scenario.</summary>
    void PortfolioReturns(double[] chol, double[] weights, double[] drifts, VarArgs args, int scenarios, double[] returns);
}

/// <summary>ILGPU (CUDA, OpenCL, CPU accelerator).</summary>
internal sealed class IlgpuEngine : IGpuEngine
{
    private readonly Context _context;
    private readonly Accelerator _acc;
    private readonly Dictionary<string, Delegate> _kernels = new();

    public IlgpuEngine(Context context, Accelerator accelerator)
    {
        _context = context;
        _acc = accelerator;
    }

    private T Kernel<T>(string key, Func<Accelerator, T> load) where T : Delegate
    {
        if (!_kernels.TryGetValue(key, out var k)) _kernels[key] = k = load(_acc);
        return (T)k;
    }

    public void MonteCarlo(bool asian, McArgs args, int threads, double[] sums, double[] sumsq)
    {
        var kernel = asian
            ? Kernel("asian", a => a.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, McArgs>(GpuKernels.Asian))
            : Kernel("european", a => a.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, McArgs>(GpuKernels.European));
        using var s1 = _acc.Allocate1D<double>(threads);
        using var s2 = _acc.Allocate1D<double>(threads);
        kernel(threads, s1.View, s2.View, args);
        _acc.Synchronize();
        s1.CopyToCPU(sums);
        s2.CopyToCPU(sumsq);
    }

    public void BlackScholes(double[] packed, int n, double[] output)
    {
        var kernel = Kernel("bs", a => a.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, int>(GpuKernels.BlackScholes));
        using var input = _acc.Allocate1D<double>(packed.Length);
        using var outp = _acc.Allocate1D<double>(6L * n);
        input.CopyFromCPU(packed);
        kernel(n, input.View, outp.View, n);
        _acc.Synchronize();
        outp.CopyToCPU(output);
    }

    public void PortfolioReturns(double[] chol, double[] weights, double[] drifts, VarArgs args, int scenarios, double[] returns)
    {
        var kernel = Kernel("var", a => a.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, ArrayView<double>, ArrayView<double>, ArrayView<double>, VarArgs>(GpuKernels.PortfolioReturn));
        using var dChol = _acc.Allocate1D<double>(chol.Length);
        using var dW = _acc.Allocate1D<double>(weights.Length);
        using var dDrift = _acc.Allocate1D<double>(drifts.Length);
        using var dOut = _acc.Allocate1D<double>(scenarios);
        dChol.CopyFromCPU(chol);
        dW.CopyFromCPU(weights);
        dDrift.CopyFromCPU(drifts);
        kernel(scenarios, dChol.View, dW.View, dDrift.View, dOut.View, args);
        _acc.Synchronize();
        dOut.CopyToCPU(returns);
    }

    /// <summary>Releases the accelerator but keeps the shared ILGPU context (a failed probe among several devices).</summary>
    public void DisposeAcceleratorOnly() => _acc.Dispose();

    public void Dispose()
    {
        _acc.Dispose();
        _context.Dispose();
    }
}

/// <summary>Packs kernel arguments into the (f64, u32) parameter arrays shared by the Vulkan, D3D12 and HIP kernels.</summary>
internal static class KernelParams
{
    public static double[] Mc(McArgs a) => new[] { a.Spot, a.Strike, a.Time, a.Rate, a.Dividend, a.Vol };

    public static uint[] McInts(McArgs a, int threads) => new[]
    {
        (uint)a.IsCall, (uint)a.Steps, (uint)(ulong)a.PathsPerThread, (uint)((ulong)a.PathsPerThread >> 32),
        (uint)(ulong)a.TotalPaths, (uint)((ulong)a.TotalPaths >> 32), (uint)a.Seed, (uint)(a.Seed >> 32), (uint)threads,
    };

    public static double[] VarInput(double[] chol, double[] weights, double[] drifts)
    {
        var all = new double[chol.Length + weights.Length + drifts.Length];
        chol.CopyTo(all, 0);
        weights.CopyTo(all, chol.Length);
        drifts.CopyTo(all, chol.Length + weights.Length);
        return all;
    }

    public static uint[] VarInts(VarArgs a, int scenarios) => new[] { (uint)a.Assets, (uint)scenarios, (uint)a.Seed, (uint)(a.Seed >> 32) };
}
