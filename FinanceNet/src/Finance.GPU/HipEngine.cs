using System.Runtime.InteropServices;
using System.Text;

namespace Finance.GPU;

/// <summary>
/// Native AMD ROCm/HIP backend: C++ kernels (Kernels/finance.hip) compiled at run time with hiprtc for the device's
/// gfx architecture and launched through the HIP runtime. The runtime is loaded dynamically (amdhip64 / hiprtc from the
/// HIP SDK on Windows or ROCm on Linux); without it the backend reports itself unavailable.
/// </summary>
internal sealed unsafe class HipEngine : IGpuEngine
{
    private static readonly string[] EntryPoints = { "mc_european", "mc_asian", "black_scholes", "portfolio_return" };
    private static readonly Lazy<HipApi?> Api = new(HipApi.Load);

    private readonly HipApi _api;
    private readonly int _device;
    private readonly IntPtr _module;
    private readonly IntPtr[] _functions;

    private HipEngine(HipApi api, int device, IntPtr module, IntPtr[] functions)
    {
        _api = api;
        _device = device;
        _module = module;
        _functions = functions;
    }

    /// <summary>Why HIP cannot be used, or null.</summary>
    public static string? Unavailable => Api.Value is null ? HipApi.LoadFailure : null;

    public static List<GpuDeviceInfo> Describe()
    {
        var list = new List<GpuDeviceInfo>();
        var api = Api.Value;
        if (api is null) return list;
        if (api.GetDeviceCount(out int count) != 0) return list;
        for (int i = 0; i < count; i++)
        {
            var name = new byte[256];
            fixed (byte* p = name) api.DeviceGetName(p, name.Length, i);
            api.DeviceTotalMem(out nuint mem, i);
            api.DeviceGetAttribute(out int cus, HipApi.AttrMultiprocessorCount, i);
            string n = Encoding.ASCII.GetString(name).TrimEnd('\0');
            list.Add(new GpuDeviceInfo(n, GpuBackend.Rocm, (long)mem, 1024, cus, true, true, "native HIP (hiprtc kernels)") { ContextIndex = i });
        }
        return list;
    }

    public static HipEngine Open(int device)
    {
        var api = Api.Value ?? throw new NotSupportedException(HipApi.LoadFailure);
        api.Check(api.SetDevice(device), "hipSetDevice");
        // Without --offload-arch, hiprtc targets the architecture of the current device (set just above).
        using var stream = typeof(HipEngine).Assembly.GetManifestResourceStream("Finance.GPU.Kernels.finance.hip")
            ?? throw new InvalidOperationException("finance.hip resource missing");
        var source = Encoding.UTF8.GetBytes(new StreamReader(stream).ReadToEnd() + "\0");
        var name = Encoding.UTF8.GetBytes("finance.hip\0");
        IntPtr prog;
        fixed (byte* s = source, n = name) api.Check(api.RtcCreateProgram(out prog, s, n, 0, null, null), "hiprtcCreateProgram");
        try
        {
            var opt = Encoding.UTF8.GetBytes("-O3\0");
            fixed (byte* o = opt)
            {
                byte* opts = o;
                int status = api.RtcCompileProgram(prog, 1, &opts);
                if (status != 0)
                {
                    api.RtcGetProgramLogSize(prog, out nuint logSize);
                    var log = new byte[(int)logSize];
                    fixed (byte* l = log) api.RtcGetProgramLog(prog, l);
                    throw new InvalidOperationException("hiprtc compile failed: " + Encoding.UTF8.GetString(log).TrimEnd('\0'));
                }
            }
            api.RtcGetCodeSize(prog, out nuint size);
            var code = new byte[(int)size];
            fixed (byte* c = code) api.RtcGetCode(prog, c);
            IntPtr module;
            fixed (byte* c = code) api.Check(api.ModuleLoadData(out module, c), "hipModuleLoadData");
            var functions = EntryPoints.Select(e =>
            {
                var en = Encoding.UTF8.GetBytes(e + "\0");
                IntPtr f;
                fixed (byte* p = en) api.Check(api.ModuleGetFunction(out f, module, p), "hipModuleGetFunction " + e);
                return f;
            }).ToArray();
            return new HipEngine(api, device, module, functions);
        }
        finally
        {
            api.RtcDestroyProgram(ref prog);
        }
    }

    private IntPtr Alloc<T>(T[] data) where T : unmanaged
    {
        nuint bytes = (nuint)Math.Max(16, data.Length * sizeof(T));
        _api.Check(_api.Malloc(out IntPtr p, bytes), "hipMalloc");
        if (data.Length > 0)
            fixed (T* src = data) _api.Check(_api.Memcpy(p, (IntPtr)src, (nuint)(data.Length * sizeof(T)), HipApi.HostToDevice), "hipMemcpy");
        return p;
    }

    private void Run(int kernel, double[] fp, uint[] ip, double[] input, double[] output, int threads)
    {
        _api.Check(_api.SetDevice(_device), "hipSetDevice");
        var ptrs = new List<IntPtr>();
        try
        {
            IntPtr dFp = Alloc(fp), dIp = Alloc(ip), dIn = Alloc(input), dOut = Alloc(new double[output.Length]);
            ptrs.AddRange(new[] { dFp, dIp, dIn, dOut });
            uint groups = (uint)Math.Max(1, (threads + 255) / 256);
            uint gx = Math.Min(groups, 65535u), gy = (groups + gx - 1) / gx;
            IntPtr* args = stackalloc IntPtr[4];
            args[0] = (IntPtr)(&dFp);
            args[1] = (IntPtr)(&dIp);
            args[2] = (IntPtr)(&dIn);
            args[3] = (IntPtr)(&dOut);
            _api.Check(_api.ModuleLaunchKernel(_functions[kernel], gx, gy, 1, 256, 1, 1, 0, IntPtr.Zero, args, null), "hipModuleLaunchKernel");
            _api.Check(_api.DeviceSynchronize(), "hipDeviceSynchronize");
            fixed (double* o = output) _api.Check(_api.Memcpy((IntPtr)o, dOut, (nuint)(output.Length * 8), HipApi.DeviceToHost), "hipMemcpy");
        }
        finally
        {
            foreach (var p in ptrs) _api.Free(p);
        }
    }

    public void MonteCarlo(bool asian, McArgs args, int threads, double[] sums, double[] sumsq)
    {
        var output = new double[2 * threads];
        Run(asian ? 1 : 0, KernelParams.Mc(args), KernelParams.McInts(args, threads), Array.Empty<double>(), output, threads);
        Array.Copy(output, 0, sums, 0, threads);
        Array.Copy(output, threads, sumsq, 0, threads);
    }

    public void BlackScholes(double[] packed, int n, double[] output)
        => Run(2, Array.Empty<double>(), new[] { (uint)n }, packed, output, n);

    public void PortfolioReturns(double[] chol, double[] weights, double[] drifts, VarArgs args, int scenarios, double[] returns)
        => Run(3, new[] { args.SqrtHorizon }, KernelParams.VarInts(args, scenarios), KernelParams.VarInput(chol, weights, drifts), returns, scenarios);

    public void Dispose() => _api.ModuleUnload(_module);
}

/// <summary>Dynamically bound subset of the HIP runtime and hiprtc C APIs.</summary>
internal sealed unsafe class HipApi
{
    public const int HostToDevice = 1, DeviceToHost = 2;
    public const int AttrMultiprocessorCount = 63;   // hipDeviceAttributeMultiprocessorCount

    public static string LoadFailure { get; private set; } = "HIP runtime not loaded";

    public delegate* unmanaged<int> Init;
    public delegate* unmanaged<out int, int> GetDeviceCount;
    public delegate* unmanaged<byte*, int, int, int> DeviceGetName;
    public delegate* unmanaged<out nuint, int, int> DeviceTotalMem;
    public delegate* unmanaged<out int, int, int, int> DeviceGetAttribute;
    public delegate* unmanaged<int, int> SetDevice;
    public delegate* unmanaged<out IntPtr, nuint, int> Malloc;
    public delegate* unmanaged<IntPtr, int> Free;
    public delegate* unmanaged<IntPtr, IntPtr, nuint, int, int> Memcpy;
    public delegate* unmanaged<int> DeviceSynchronize;
    public delegate* unmanaged<out IntPtr, byte*, int> ModuleLoadData;
    public delegate* unmanaged<IntPtr, int> ModuleUnload;
    public delegate* unmanaged<out IntPtr, IntPtr, byte*, int> ModuleGetFunction;
    public delegate* unmanaged<IntPtr, uint, uint, uint, uint, uint, uint, uint, IntPtr, IntPtr*, IntPtr*, int> ModuleLaunchKernel;
    public delegate* unmanaged<out IntPtr, byte*, byte*, int, byte**, byte**, int> RtcCreateProgram;
    public delegate* unmanaged<IntPtr, int, byte**, int> RtcCompileProgram;
    public delegate* unmanaged<IntPtr, out nuint, int> RtcGetProgramLogSize;
    public delegate* unmanaged<IntPtr, byte*, int> RtcGetProgramLog;
    public delegate* unmanaged<IntPtr, out nuint, int> RtcGetCodeSize;
    public delegate* unmanaged<IntPtr, byte*, int> RtcGetCode;
    public delegate* unmanaged<ref IntPtr, int> RtcDestroyProgram;

    private static readonly string[] RuntimeNames = OperatingSystem.IsWindows()
        ? new[] { "amdhip64_7.dll", "amdhip64_6.dll", "amdhip64.dll" }
        : new[] { "libamdhip64.so.7", "libamdhip64.so.6", "libamdhip64.so", "/opt/rocm/lib/libamdhip64.so" };

    private static readonly string[] RtcNames = OperatingSystem.IsWindows()
        ? new[] { "hiprtc0702.dll", "hiprtc0701.dll", "hiprtc0700.dll", "hiprtc0604.dll", "hiprtc0603.dll", "hiprtc0602.dll", "hiprtc0601.dll", "hiprtc0600.dll", "hiprtc0507.dll", "hiprtc.dll" }
        : new[] { "libhiprtc.so.7", "libhiprtc.so.6", "libhiprtc.so", "/opt/rocm/lib/libhiprtc.so" };

    public static HipApi? Load()
    {
        if (OperatingSystem.IsMacOS()) { LoadFailure = "ROCm/HIP is not available on macOS"; return null; }
        var hip = TryLoadAny(RuntimeNames);
        if (hip == IntPtr.Zero) { LoadFailure = "HIP runtime (amdhip64) not found - install the AMD HIP SDK (Windows) or ROCm (Linux)"; return null; }
        var rtc = TryLoadAny(RtcNames);
        if (rtc == IntPtr.Zero) { LoadFailure = "hiprtc (HIP runtime compiler) not found"; return null; }
        try
        {
            var api = new HipApi
            {
                Init = (delegate* unmanaged<int>)NativeLibrary.GetExport(hip, "hipInit"),
                GetDeviceCount = (delegate* unmanaged<out int, int>)NativeLibrary.GetExport(hip, "hipGetDeviceCount"),
                DeviceGetName = (delegate* unmanaged<byte*, int, int, int>)NativeLibrary.GetExport(hip, "hipDeviceGetName"),
                DeviceTotalMem = (delegate* unmanaged<out nuint, int, int>)NativeLibrary.GetExport(hip, "hipDeviceTotalMem"),
                DeviceGetAttribute = (delegate* unmanaged<out int, int, int, int>)NativeLibrary.GetExport(hip, "hipDeviceGetAttribute"),
                SetDevice = (delegate* unmanaged<int, int>)NativeLibrary.GetExport(hip, "hipSetDevice"),
                Malloc = (delegate* unmanaged<out IntPtr, nuint, int>)NativeLibrary.GetExport(hip, "hipMalloc"),
                Free = (delegate* unmanaged<IntPtr, int>)NativeLibrary.GetExport(hip, "hipFree"),
                Memcpy = (delegate* unmanaged<IntPtr, IntPtr, nuint, int, int>)NativeLibrary.GetExport(hip, "hipMemcpy"),
                DeviceSynchronize = (delegate* unmanaged<int>)NativeLibrary.GetExport(hip, "hipDeviceSynchronize"),
                ModuleLoadData = (delegate* unmanaged<out IntPtr, byte*, int>)NativeLibrary.GetExport(hip, "hipModuleLoadData"),
                ModuleUnload = (delegate* unmanaged<IntPtr, int>)NativeLibrary.GetExport(hip, "hipModuleUnload"),
                ModuleGetFunction = (delegate* unmanaged<out IntPtr, IntPtr, byte*, int>)NativeLibrary.GetExport(hip, "hipModuleGetFunction"),
                ModuleLaunchKernel = (delegate* unmanaged<IntPtr, uint, uint, uint, uint, uint, uint, uint, IntPtr, IntPtr*, IntPtr*, int>)NativeLibrary.GetExport(hip, "hipModuleLaunchKernel"),
                RtcCreateProgram = (delegate* unmanaged<out IntPtr, byte*, byte*, int, byte**, byte**, int>)NativeLibrary.GetExport(rtc, "hiprtcCreateProgram"),
                RtcCompileProgram = (delegate* unmanaged<IntPtr, int, byte**, int>)NativeLibrary.GetExport(rtc, "hiprtcCompileProgram"),
                RtcGetProgramLogSize = (delegate* unmanaged<IntPtr, out nuint, int>)NativeLibrary.GetExport(rtc, "hiprtcGetProgramLogSize"),
                RtcGetProgramLog = (delegate* unmanaged<IntPtr, byte*, int>)NativeLibrary.GetExport(rtc, "hiprtcGetProgramLog"),
                RtcGetCodeSize = (delegate* unmanaged<IntPtr, out nuint, int>)NativeLibrary.GetExport(rtc, "hiprtcGetCodeSize"),
                RtcGetCode = (delegate* unmanaged<IntPtr, byte*, int>)NativeLibrary.GetExport(rtc, "hiprtcGetCode"),
                RtcDestroyProgram = (delegate* unmanaged<ref IntPtr, int>)NativeLibrary.GetExport(rtc, "hiprtcDestroyProgram"),
            };
            int init = api.Init();
            if (init != 0) { LoadFailure = $"hipInit failed ({init}) - no AMD GPU or driver"; return null; }
            return api;
        }
        catch (EntryPointNotFoundException e)
        {
            LoadFailure = "HIP runtime is missing an entry point: " + e.Message;
            return null;
        }
    }

    private static IntPtr TryLoadAny(IEnumerable<string> names)
    {
        foreach (var n in names)
            if (NativeLibrary.TryLoad(n, out var h)) return h;
        return IntPtr.Zero;
    }

    public void Check(int status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"{what} failed with HIP error {status}");
    }
}
