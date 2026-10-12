using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Finance.RustBridge;

/// <summary>
/// Status of the Rust accelerator. The native library is located once, on first use; when it is
/// missing every Fast* API silently uses <see cref="ManagedKernels"/>, which has identical semantics.
/// </summary>
public static class RustAccelerator
{
    private static readonly Lazy<(bool Ok, string? Version, string? Error, string? Path)> State = new(Probe);
    private static string? _resolvedPath;

    /// <summary>True when <c>finance_native</c> was found and answered its version call.</summary>
    public static bool IsAvailable => State.Value.Ok && !ForceManaged;

    /// <summary>True when the library loaded, regardless of <see cref="ForceManaged"/>.</summary>
    public static bool IsLoaded => State.Value.Ok;

    /// <summary>Native library version (e.g. "0.2.0"), or null when unavailable.</summary>
    public static string? NativeVersion => State.Value.Version;

    /// <summary>Why loading failed, or null.</summary>
    public static string? LoadError => State.Value.Error;

    /// <summary>Full path of the loaded library, when known.</summary>
    public static string? LibraryPath => State.Value.Path;

    /// <summary>
    /// Forces the managed kernels even when the native library is available
    /// (benchmarks, parity checks, troubleshooting). Can also be set with the
    /// <c>FINANCE_NATIVE_DISABLE=1</c> environment variable.
    /// </summary>
    public static bool ForceManaged { get; set; } = Environment.GetEnvironmentVariable("FINANCE_NATIVE_DISABLE") == "1";

    /// <summary>
    /// SIMD level the native kernels use: <see cref="NativeSimdLevel.Scalar"/> (SSE2/NEON baseline),
    /// <see cref="NativeSimdLevel.Avx2"/> or <see cref="NativeSimdLevel.Avx512"/>. Detected from CPUID; can be lowered for
    /// comparisons (every level returns bit-identical results) or with <c>FINANCE_NATIVE_SIMD=scalar|avx2|avx512</c>.
    /// Setting a level the CPU lacks selects the highest supported one.
    /// </summary>
    public static NativeSimdLevel SimdLevel
    {
        get => IsLoaded ? (NativeSimdLevel)SafeSimd(NativeMethods.SimdLevel) : NativeSimdLevel.Scalar;
        set { if (IsLoaded) SafeSimd(() => NativeMethods.SetSimdLevel((int)value)); }
    }

    /// <summary>Highest SIMD level this CPU supports (as seen by the native library).</summary>
    public static NativeSimdLevel SupportedSimdLevel => IsLoaded ? (NativeSimdLevel)SafeSimd(NativeMethods.SimdSupported) : NativeSimdLevel.Scalar;

    private static int SafeSimd(Func<int> f)
    {
        try { return f(); }
        catch (EntryPointNotFoundException) { return 0; }   // native library older than 0.9
    }

    /// <summary>One-line description for logs and UIs.</summary>
    public static string Describe() => IsLoaded
        ? $"Rust accelerator {NativeVersion} ({LibraryPath ?? "default search path"}, SIMD {SimdLevel.ToString().ToLowerInvariant()}){(ForceManaged ? " - disabled, using managed kernels" : "")}"
        : $"Rust accelerator unavailable - managed kernels in use ({LoadError})";

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "Resolver must be installed before the first P/Invoke.")]
    internal static void InstallResolver()
        => NativeLibrary.SetDllImportResolver(typeof(RustAccelerator).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != NativeMethods.Library) return IntPtr.Zero;
        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                _resolvedPath = candidate;
                return handle;
            }
        }
        return NativeLibrary.TryLoad(name, assembly, searchPath, out var h) ? h : IntPtr.Zero;
    }

    /// <summary>Paths probed for the native library, in order.</summary>
    public static IEnumerable<string> Candidates()
    {
        string file = OperatingSystem.IsWindows() ? "finance_native.dll"
            : OperatingSystem.IsMacOS() ? "libfinance_native.dylib"
            : "libfinance_native.so";
        var env = Environment.GetEnvironmentVariable("FINANCE_NATIVE_PATH");
        if (!string.IsNullOrEmpty(env))
            yield return Directory.Exists(env) ? Path.Combine(env, file) : env;
        var dirs = new[] { Path.GetDirectoryName(typeof(RustAccelerator).Assembly.Location), AppContext.BaseDirectory }
            .Where(d => !string.IsNullOrEmpty(d)).Distinct();
        foreach (var dir in dirs)
        {
            yield return Path.Combine(dir!, file);
            yield return Path.Combine(dir!, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", file);
            yield return Path.Combine(dir!, "runtimes", PortableRid(), "native", file);
        }
    }

    private static string PortableRid()
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return $"{os}-{arch}";
    }

    private static (bool, string?, string?, string?) Probe()
    {
        try
        {
            uint v = NativeMethods.Version();
            return (true, $"{v / 10000}.{v / 100 % 100}.{v % 100}", null, _resolvedPath);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return (false, null, e.Message, null);
        }
    }
}

/// <summary>SIMD instruction sets used by the native kernels.</summary>
public enum NativeSimdLevel
{
    /// <summary>Baseline vector width of the target (SSE2 on x86-64, NEON on arm64).</summary>
    Scalar = 0,
    /// <summary>AVX2 + FMA (256-bit).</summary>
    Avx2 = 1,
    /// <summary>AVX-512F (512-bit).</summary>
    Avx512 = 2,
}
