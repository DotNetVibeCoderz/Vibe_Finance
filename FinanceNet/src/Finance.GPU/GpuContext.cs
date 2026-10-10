using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.OpenCL;

namespace Finance.GPU;

/// <summary>
/// An ILGPU context bound to one accelerator. <see cref="CreateBest"/> prefers CUDA, then an
/// OpenCL device with double precision, then the CPU accelerator, so code written against it
/// runs everywhere. Kernels are compiled once per context and launches are serialised.
/// </summary>
public sealed class GpuContext : IDisposable
{
    private readonly Context _context;
    private readonly Dictionary<string, Delegate> _kernels = new();
    private readonly object _gate = new();
    private bool _disposed;

    private GpuContext(Context context, Accelerator accelerator, GpuDeviceInfo device, string? fallbackReason)
    {
        _context = context;
        Accelerator = accelerator;
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

    internal Accelerator Accelerator { get; }

    internal object Gate => _gate;

    /// <summary>
    /// Lists every device ILGPU can see, followed by entries for DirectML and Vulkan (always
    /// unusable) and ROCm (usable only through an AMD OpenCL device).
    /// </summary>
    public static IReadOnlyList<GpuDeviceInfo> ListDevices()
    {
        using var context = CreateIlgpuContext();
        var list = Describe(context);
        if (!list.Any(d => d.Backend == GpuBackend.OpenCL && d.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase)))
            list.Add(Unsupported(GpuBackend.Rocm));
        list.Add(Unsupported(GpuBackend.DirectML));
        list.Add(Unsupported(GpuBackend.Vulkan));
        return list;
    }

    /// <summary>Creates a context on the best usable device (CUDA &gt; OpenCL fp64 &gt; CPU).</summary>
    /// <param name="allowCpu">When false, throws instead of falling back to the CPU accelerator.</param>
    public static GpuContext CreateBest(bool allowCpu = true)
    {
        var context = CreateIlgpuContext();
        var devices = Describe(context);
        var reasons = new List<string>();
        foreach (var backend in new[] { GpuBackend.Cuda, GpuBackend.OpenCL })
        {
            foreach (var d in devices.Where(d => d.Backend == backend))
            {
                if (!d.IsUsable)
                {
                    reasons.Add($"{d.Name}: {d.Note}");
                    continue;
                }
                if (TryOpen(context, d, reasons, out var ctx)) return ctx!;
            }
        }
        if (!devices.Any(d => d.Backend is GpuBackend.Cuda or GpuBackend.OpenCL))
            reasons.Add("no CUDA or OpenCL device found");
        if (!allowCpu)
        {
            context.Dispose();
            throw new NotSupportedException("No usable GPU: " + string.Join("; ", reasons));
        }
        var cpu = devices.First(d => d.Backend == GpuBackend.Cpu);
        var acc = context.Devices[cpu.ContextIndex].CreateAccelerator(context);
        return new GpuContext(context, acc, cpu, reasons.Count > 0 ? string.Join("; ", reasons) : null);
    }

    /// <summary>
    /// Creates a context on a specific backend. DirectML and Vulkan always throw
    /// <see cref="NotSupportedException"/>; ROCm selects an AMD OpenCL device when one exists.
    /// </summary>
    public static GpuContext Create(GpuBackend backend)
    {
        if (backend is GpuBackend.DirectML or GpuBackend.Vulkan)
            throw new NotSupportedException(GpuBackendSupport.UnsupportedReason(backend));
        var context = CreateIlgpuContext();
        var devices = Describe(context);
        IEnumerable<GpuDeviceInfo> candidates = backend == GpuBackend.Rocm
            ? devices.Where(d => d.Backend == GpuBackend.OpenCL && d.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase))
            : devices.Where(d => d.Backend == backend);
        var reasons = new List<string>();
        foreach (var d in candidates)
        {
            if (!d.IsUsable) { reasons.Add($"{d.Name}: {d.Note}"); continue; }
            if (TryOpen(context, d, reasons, out var ctx)) return ctx!;
        }
        context.Dispose();
        string why = reasons.Count > 0 ? string.Join("; ", reasons) : backend == GpuBackend.Rocm
            ? GpuBackendSupport.UnsupportedReason(GpuBackend.Rocm)!
            : $"no {backend} device found";
        throw new NotSupportedException($"Cannot create a {backend} context: {why}");
    }

    private static Context CreateIlgpuContext()
        => Context.Create(builder => builder.Default().EnableAlgorithms().Math(MathMode.Default));

    private static bool TryOpen(Context context, GpuDeviceInfo d, List<string> reasons, out GpuContext? result)
    {
        Accelerator? acc = null;
        try
        {
            acc = context.Devices[d.ContextIndex].CreateAccelerator(context);
            result = new GpuContext(context, acc, d, reasons.Count > 0 ? string.Join("; ", reasons) : null);
            // Compile one double-precision kernel now so a driver that cannot run it fails here,
            // not in the middle of a pricing call.
            new GpuOptionPricer(result).BlackScholesBatch(new double[] { 100 }, new double[] { 100 }, new double[] { 1 },
                new double[] { 0.01 }, new double[] { 0 }, new double[] { 0.2 }, new[] { true });
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            reasons.Add($"{d.Name}: {e.GetType().Name}: {e.Message}");
            acc?.Dispose();
            result = null;
            return false;
        }
    }

    private static List<GpuDeviceInfo> Describe(Context context)
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

    internal T Kernel<T>(string key, Func<Accelerator, T> load) where T : Delegate
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_kernels.TryGetValue(key, out var k))
            {
                k = load(Accelerator);
                _kernels[key] = k;
            }
            return (T)k;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Accelerator.Dispose();
            _context.Dispose();
        }
    }

    public override string ToString() => FallbackReason is null ? Device.ToString() : $"{Device} (fallback: {FallbackReason})";
}
