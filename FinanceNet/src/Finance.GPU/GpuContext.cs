using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.OpenCL;

namespace Finance.GPU;

/// <summary>
/// A compute context bound to one device. Kernels run through one of four engines with identical semantics:
/// ILGPU (CUDA, OpenCL, CPU accelerator), native HIP (ROCm), Vulkan compute (finance_gpu / wgpu) and Direct3D 12
/// compute (the DirectML backend). <see cref="CreateBest"/> prefers CUDA, then ROCm, then an OpenCL device with
/// double precision, then Vulkan, then Direct3D 12, then the CPU accelerator. Launches are serialised.
/// </summary>
public sealed class GpuContext : IDisposable
{
    private readonly object _gate = new();
    private readonly IGpuEngine _engine;
    private bool _disposed;

    private GpuContext(IGpuEngine engine, GpuDeviceInfo device, string? fallbackReason)
    {
        _engine = engine;
        Device = device;
        FallbackReason = fallbackReason;
    }

    /// <summary>The device kernels run on.</summary>
    public GpuDeviceInfo Device { get; }

    /// <summary>Backend in use.</summary>
    public GpuBackend Backend => Device.Backend;

    /// <summary>True when running on ILGPU's CPU accelerator.</summary>
    public bool IsCpu => Device.Backend == GpuBackend.Cpu;

    /// <summary>Why a better device was skipped, when it was.</summary>
    public string? FallbackReason { get; }

    /// <summary>
    /// Lists every device each engine can see: ILGPU (CUDA, OpenCL, CPU), HIP (ROCm), Vulkan and Direct3D 12
    /// (DirectML). Backends that cannot run on this machine appear once, unusable, with the reason.
    /// </summary>
    public static IReadOnlyList<GpuDeviceInfo> ListDevices()
    {
        using var context = CreateIlgpuContext();
        var list = DescribeIlgpu(context);
        var hip = HipEngine.Describe();
        if (hip.Count > 0) list.AddRange(hip);
        else if (!list.Any(IsAmdOpenCl)) list.Add(Unsupported(GpuBackend.Rocm));
        else list.AddRange(list.Where(IsAmdOpenCl).ToList().Select(d => d with { Backend = GpuBackend.Rocm, Note = "AMD GPU through OpenCL (HIP runtime not installed)" }));
        var vk = VulkanEngine.Describe();
        list.AddRange(vk.Count > 0 ? vk : new List<GpuDeviceInfo> { Unsupported(GpuBackend.Vulkan) });
        var dx = D3D12Engine.Describe();
        list.AddRange(dx.Count > 0 ? dx : new List<GpuDeviceInfo> { Unsupported(GpuBackend.DirectML) });
        return list;
    }

    /// <summary>Creates a context on the best usable device (CUDA &gt; ROCm &gt; OpenCL fp64 &gt; Vulkan &gt; Direct3D 12 &gt; CPU).</summary>
    /// <param name="allowCpu">When false, throws instead of falling back to the CPU accelerator.</param>
    public static GpuContext CreateBest(bool allowCpu = true)
    {
        var reasons = new List<string>();
        var context = CreateIlgpuContext();
        var devices = DescribeIlgpu(context);

        foreach (var d in devices.Where(d => d.Backend == GpuBackend.Cuda))
            if (TryIlgpu(context, d, reasons, out var ctx)) return ctx!;
        foreach (var d in HipEngine.Describe())
            if (TryOpen(d, () => HipEngine.Open(d.ContextIndex), reasons, out var ctx)) { context.Dispose(); return ctx!; }
        foreach (var d in devices.Where(d => d.Backend == GpuBackend.OpenCL))
            if (TryIlgpu(context, d, reasons, out var ctx)) return ctx!;
        foreach (var d in VulkanEngine.Describe())
            if (TryOpen(d, () => VulkanEngine.Open(d.ContextIndex), reasons, out var ctx)) { context.Dispose(); return ctx!; }
        foreach (var d in D3D12Engine.Describe().Where(d => !d.Name.Contains("(WARP")))
            if (TryOpen(d, () => D3D12Engine.Open((uint)d.ContextIndex), reasons, out var ctx)) { context.Dispose(); return ctx!; }

        if (reasons.Count == 0) reasons.Add("no GPU found");
        if (!allowCpu)
        {
            context.Dispose();
            throw new NotSupportedException("No usable GPU: " + string.Join("; ", reasons));
        }
        var cpu = devices.First(d => d.Backend == GpuBackend.Cpu);
        var acc = context.Devices[cpu.ContextIndex].CreateAccelerator(context);
        return new GpuContext(new IlgpuEngine(context, acc), cpu, string.Join("; ", reasons));
    }

    /// <summary>
    /// Creates a context on a specific backend, throwing <see cref="NotSupportedException"/> with the reason when it
    /// cannot run here. <see cref="GpuBackend.Rocm"/> uses native HIP, else an AMD OpenCL device;
    /// <see cref="GpuBackend.DirectML"/> uses a hardware Direct3D 12 adapter, else WARP (software).
    /// </summary>
    public static GpuContext Create(GpuBackend backend)
    {
        var reasons = new List<string>();
        switch (backend)
        {
            case GpuBackend.Vulkan:
                foreach (var d in VulkanEngine.Describe())
                    if (TryOpen(d, () => VulkanEngine.Open(d.ContextIndex), reasons, out var vk)) return vk!;
                throw new NotSupportedException($"Cannot create a Vulkan context: {Why(reasons, GpuBackend.Vulkan)}");
            case GpuBackend.DirectML:
                foreach (var d in D3D12Engine.Describe())
                    if (TryOpen(d, () => D3D12Engine.Open((uint)d.ContextIndex), reasons, out var dx))
                        return d.Name.Contains("(WARP") && reasons.Count > 0 ? dx!.WithFallback(string.Join("; ", reasons)) : dx!;
                throw new NotSupportedException($"Cannot create a DirectML (Direct3D 12) context: {Why(reasons, GpuBackend.DirectML)}");
            case GpuBackend.Rocm:
                foreach (var d in HipEngine.Describe())
                    if (TryOpen(d, () => HipEngine.Open(d.ContextIndex), reasons, out var hip)) return hip!;
                if (HipEngine.Unavailable is { } hipWhy) reasons.Add(hipWhy);
                break;
        }

        var context = CreateIlgpuContext();
        var devices = DescribeIlgpu(context);
        IEnumerable<GpuDeviceInfo> candidates = backend == GpuBackend.Rocm ? devices.Where(IsAmdOpenCl) : devices.Where(d => d.Backend == backend);
        foreach (var d in candidates)
        {
            if (TryIlgpu(context, d, reasons, out var ctx))
                return backend == GpuBackend.Rocm ? ctx!.WithFallback(string.Join("; ", reasons)) : ctx!;
        }
        context.Dispose();
        throw new NotSupportedException($"Cannot create a {backend} context: {Why(reasons, backend)}");
    }

    /// <summary>Creates a context on one device from <see cref="ListDevices"/> (for example WARP, or a second GPU).</summary>
    public static GpuContext Create(GpuDeviceInfo device)
    {
        var reasons = new List<string>();
        GpuContext? ctx;
        bool ok = device.Backend switch
        {
            GpuBackend.Vulkan => TryOpen(device, () => VulkanEngine.Open(device.ContextIndex), reasons, out ctx),
            GpuBackend.DirectML => TryOpen(device, () => D3D12Engine.Open((uint)device.ContextIndex), reasons, out ctx),
            GpuBackend.Rocm when device.Note?.Contains("HIP", StringComparison.Ordinal) == true && HipEngine.Unavailable is null
                => TryOpen(device, () => HipEngine.Open(device.ContextIndex), reasons, out ctx),
            _ => TryIlgpuDevice(device, reasons, out ctx),
        };
        return ok ? ctx! : throw new NotSupportedException($"Cannot open {device.Backend} {device.Name}: {string.Join("; ", reasons)}");
    }

    private static bool TryIlgpuDevice(GpuDeviceInfo device, List<string> reasons, out GpuContext? ctx)
    {
        var context = CreateIlgpuContext();
        var match = DescribeIlgpu(context).FirstOrDefault(d => d.ContextIndex == device.ContextIndex && d.Name == device.Name);
        if (match is not null && TryIlgpu(context, match, reasons, out ctx)) return true;
        if (match is null) reasons.Add("device not found");
        context.Dispose();
        ctx = null;
        return false;
    }

    private GpuContext WithFallback(string reason) => FallbackReason is null && reason.Length > 0 ? new GpuContext(_engine, Device, reason) : this;

    private static string Why(List<string> reasons, GpuBackend backend)
        => reasons.Count > 0 ? string.Join("; ", reasons) : GpuBackendSupport.UnsupportedReason(backend) ?? $"no {backend} device found";

    private static bool IsAmdOpenCl(GpuDeviceInfo d) => d.Backend == GpuBackend.OpenCL && d.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase);

    private static Context CreateIlgpuContext()
        => Context.Create(builder => builder.Default().EnableAlgorithms().Math(MathMode.Default));

    private static bool TryIlgpu(Context context, GpuDeviceInfo d, List<string> reasons, out GpuContext? result)
    {
        if (!d.IsUsable)
        {
            reasons.Add($"{d.Name}: {d.Note}");
            result = null;
            return false;
        }
        return TryOpen(d, () => new IlgpuEngine(context, context.Devices[d.ContextIndex].CreateAccelerator(context)), reasons, out result, ownsContext: false);
    }

    private static bool TryOpen(GpuDeviceInfo d, Func<IGpuEngine> open, List<string> reasons, out GpuContext? result, bool ownsContext = true)
    {
        result = null;
        if (!d.IsUsable)
        {
            reasons.Add($"{d.Backend} {d.Name}: {d.Note}");
            return false;
        }
        IGpuEngine? engine = null;
        try
        {
            engine = open();
            var ctx = new GpuContext(engine, d, reasons.Count > 0 ? string.Join("; ", reasons) : null);
            // Run one double-precision kernel now so a driver that cannot run it fails here, not mid-pricing.
            var check = new GpuOptionPricer(ctx).BlackScholesBatch(new double[] { 100 }, new double[] { 100 }, new double[] { 1 },
                new double[] { 0.05 }, new double[] { 0 }, new double[] { 0.2 }, new[] { true });
            if (Math.Abs(check.Price[0] - 10.450583572185565) > 1e-9)
                throw new InvalidOperationException($"self-test priced the reference call at {check.Price[0]} instead of 10.4506");
            result = ctx;
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            reasons.Add($"{d.Backend} {d.Name}: {e.GetType().Name}: {e.Message}");
            if (ownsContext) engine?.Dispose();
            else (engine as IlgpuEngine)?.DisposeAcceleratorOnly();
            return false;
        }
    }

    private static List<GpuDeviceInfo> DescribeIlgpu(Context context)
    {
        var list = new List<GpuDeviceInfo>();
        for (int i = 0; i < context.Devices.Length; i++)
        {
            var d = context.Devices[i];
            var backend = d.AcceleratorType switch
            {
                AcceleratorType.Cuda => GpuBackend.Cuda,
                AcceleratorType.OpenCL => GpuBackend.OpenCL,
                _ => GpuBackend.Cpu,
            };
            bool fp64 = true;
            string? note = null;
            string name = d.Name;
            if (d is CLDevice cl)
            {
                fp64 = cl.Capabilities.Float64;
                if (!fp64) note = "device has no double-precision (cl_khr_fp64) support";
                if (cl.Vendor == CLDeviceVendor.AMD && !name.Contains("AMD", StringComparison.OrdinalIgnoreCase)) name = "AMD " + name;
            }
            if (backend == GpuBackend.Cpu) note = "ILGPU CPU accelerator (emulated SIMT on CPU threads)";
            list.Add(new GpuDeviceInfo(name, backend, d.MemorySize, d.MaxNumThreadsPerGroup, d.NumMultiprocessors, fp64, fp64, note) { ContextIndex = i });
        }
        return list;
    }

    private static GpuDeviceInfo Unsupported(GpuBackend backend)
        => new(backend.ToString(), backend, 0, 0, 0, false, false, GpuBackendSupport.UnsupportedReason(backend));

    /// <summary>Default number of GPU threads for Monte Carlo launches.</summary>
    internal int McThreads => IsCpu ? Environment.ProcessorCount * 64 : 1 << 16;

    /// <summary>Runs <paramref name="work"/> on the engine under the context lock.</summary>
    internal T Run<T>(Func<IGpuEngine, T> work)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return work(_engine);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _engine.Dispose();
        }
    }

    public override string ToString() => FallbackReason is null ? Device.ToString() : $"{Device} (fallback: {FallbackReason})";
}
