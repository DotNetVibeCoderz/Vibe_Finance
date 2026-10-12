using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Finance.GPU;

/// <summary>
/// Vulkan compute through the <c>finance_gpu</c> Rust library (wgpu, WGSL kernels with the f64/i64 shader
/// extensions). Needs a Vulkan driver whose device has shaderFloat64 and shaderInt64.
/// </summary>
internal sealed unsafe partial class VulkanEngine : IGpuEngine
{
    private const string Library = "finance_gpu";
    private static readonly Lazy<string?> LoadError = new(Probe);
    private IntPtr _device;

    private VulkanEngine(IntPtr device, string name)
    {
        _device = device;
        Name = name;
    }

    public string Name { get; }

    /// <summary>Why the native library could not be used, or null when it loaded.</summary>
    public static string? Unavailable => LoadError.Value;

    [LibraryImport(Library, EntryPoint = "fgpu_version")] private static partial uint Version();
    [LibraryImport(Library, EntryPoint = "fgpu_last_error")] private static partial int LastError(byte* buf, nuint cap);
    [LibraryImport(Library, EntryPoint = "fgpu_list_devices")] private static partial int ListDevices(byte* buf, nuint cap);
    [LibraryImport(Library, EntryPoint = "fgpu_open")] private static partial IntPtr OpenDevice(uint index);
    [LibraryImport(Library, EntryPoint = "fgpu_close")] private static partial void CloseDevice(IntPtr device);
    [LibraryImport(Library, EntryPoint = "fgpu_device_name")] private static partial int DeviceName(IntPtr device, byte* buf, nuint cap);

    [LibraryImport(Library, EntryPoint = "fgpu_monte_carlo")]
    private static partial int MonteCarloNative(IntPtr device, uint asian, double spot, double strike, double time, double rate, double dividend, double vol,
        uint isCall, uint steps, ulong pathsPerThread, ulong totalPaths, ulong seed, uint threads, double* sums, double* sumsq);

    [LibraryImport(Library, EntryPoint = "fgpu_black_scholes")]
    private static partial int BlackScholesNative(IntPtr device, double* input, uint n, double* output);

    [LibraryImport(Library, EntryPoint = "fgpu_portfolio_returns")]
    private static partial int PortfolioReturnsNative(IntPtr device, double* chol, double* weights, double* drifts, uint assets, double sqrtHorizon,
        ulong seed, uint scenarios, double* returns);

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "Resolver must be installed before the first P/Invoke.")]
    internal static void InstallResolver() => NativeLibrary.SetDllImportResolver(typeof(VulkanEngine).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return IntPtr.Zero;
        string file = OperatingSystem.IsWindows() ? "finance_gpu.dll" : OperatingSystem.IsMacOS() ? "libfinance_gpu.dylib" : "libfinance_gpu.so";
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string rid = $"{os}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        var env = Environment.GetEnvironmentVariable("FINANCE_GPU_PATH");
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(env)) candidates.Add(Directory.Exists(env) ? Path.Combine(env, file) : env);
        foreach (var dir in new[] { Path.GetDirectoryName(assembly.Location), AppContext.BaseDirectory }.Where(d => !string.IsNullOrEmpty(d)).Distinct())
        {
            candidates.Add(Path.Combine(dir!, file));
            candidates.Add(Path.Combine(dir!, "runtimes", rid, "native", file));
        }
        foreach (var c in candidates)
            if (File.Exists(c) && NativeLibrary.TryLoad(c, out var h)) return h;
        return NativeLibrary.TryLoad(name, assembly, searchPath, out var handle) ? handle : IntPtr.Zero;
    }

    private static string? Probe()
    {
        try
        {
            uint v = Version();
            return v == 0 ? "finance_gpu reported version 0" : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return $"finance_gpu native library not found ({e.GetType().Name})";
        }
    }

    private static string Error()
    {
        int len = LastError(null, 0);
        if (len <= 0) return "unknown error";
        var buf = new byte[len];
        fixed (byte* b = buf) LastError(b, (nuint)len);
        return Encoding.UTF8.GetString(buf);
    }

    /// <summary>Vulkan adapters in enumeration order (index = position).</summary>
    public static List<GpuDeviceInfo> Describe()
    {
        var list = new List<GpuDeviceInfo>();
        if (Unavailable is not null) return list;
        int len = ListDevices(null, 0);
        if (len <= 0) return list;
        var buf = new byte[len];
        fixed (byte* b = buf) ListDevices(b, (nuint)len);
        int index = 0;
        foreach (var line in Encoding.UTF8.GetString(buf).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('|');
            bool fp64 = f.Length > 3 && f[2] == "1" && f[3] == "1";
            string note = fp64 ? $"Vulkan compute (wgpu), {f[1]}" : "device has no shaderFloat64/shaderInt64 support";
            list.Add(new GpuDeviceInfo(f[0], GpuBackend.Vulkan, 0, 256, 0, fp64, fp64, note) { ContextIndex = index++ });
        }
        return list;
    }

    public static VulkanEngine Open(int index)
    {
        if (Unavailable is { } why) throw new NotSupportedException(why);
        var device = OpenDevice((uint)index);
        if (device == IntPtr.Zero) throw new NotSupportedException(Error());
        int len = DeviceName(device, null, 0);
        var buf = new byte[Math.Max(len, 0)];
        fixed (byte* b = buf) DeviceName(device, b, (nuint)buf.Length);
        return new VulkanEngine(device, Encoding.UTF8.GetString(buf));
    }

    private static void Check(int status)
    {
        if (status != 0) throw new InvalidOperationException($"Vulkan kernel failed ({status}): {Error()}");
    }

    public void MonteCarlo(bool asian, McArgs a, int threads, double[] sums, double[] sumsq)
    {
        fixed (double* s1 = sums, s2 = sumsq)
            Check(MonteCarloNative(_device, asian ? 1u : 0u, a.Spot, a.Strike, a.Time, a.Rate, a.Dividend, a.Vol, (uint)a.IsCall, (uint)a.Steps,
                (ulong)a.PathsPerThread, (ulong)a.TotalPaths, a.Seed, (uint)threads, s1, s2));
    }

    public void BlackScholes(double[] packed, int n, double[] output)
    {
        fixed (double* i = packed, o = output) Check(BlackScholesNative(_device, i, (uint)n, o));
    }

    public void PortfolioReturns(double[] chol, double[] weights, double[] drifts, VarArgs a, int scenarios, double[] returns)
    {
        fixed (double* c = chol, w = weights, d = drifts, r = returns)
            Check(PortfolioReturnsNative(_device, c, w, d, (uint)a.Assets, a.SqrtHorizon, a.Seed, (uint)scenarios, r));
    }

    public void Dispose()
    {
        if (_device != IntPtr.Zero) CloseDevice(_device);
        _device = IntPtr.Zero;
    }
}
