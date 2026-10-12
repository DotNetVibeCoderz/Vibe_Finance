namespace Finance.GPU;

/// <summary>Compute backends Finance.GPU knows about.</summary>
public enum GpuBackend
{
    /// <summary>ILGPU's multi-threaded CPU accelerator (always available; used as the fallback).</summary>
    Cpu,
    /// <summary>NVIDIA CUDA through ILGPU's PTX backend.</summary>
    Cuda,
    /// <summary>OpenCL 2.0+ devices (Intel, AMD, NVIDIA) through ILGPU.</summary>
    OpenCL,
    /// <summary>AMD ROCm: native HIP kernels compiled with hiprtc; without the HIP runtime, AMD GPUs are reached through OpenCL.</summary>
    Rocm,
    /// <summary>
    /// Direct3D 12 compute shaders on any DirectX 12 GPU with double-precision shader support (or WARP, the Windows
    /// software rasteriser). DirectML itself is an FP16/FP32 tensor-operator library; Finance.GPU's FP64 Monte Carlo
    /// kernels run on the Direct3D 12 layer that DirectML is built on. Windows only.
    /// </summary>
    DirectML,
    /// <summary>Vulkan compute through the finance_gpu native library (wgpu); needs shaderFloat64 and shaderInt64.</summary>
    Vulkan,
}

/// <summary>A device discovered on this machine (or a backend that cannot be used).</summary>
/// <param name="Name">Device name as reported by the driver.</param>
/// <param name="Backend">Backend that drives it.</param>
/// <param name="MemoryBytes">Device memory in bytes (0 when unknown).</param>
/// <param name="MaxThreadsPerGroup">Maximum threads per group.</param>
/// <param name="Multiprocessors">Number of multiprocessors / compute units.</param>
/// <param name="SupportsFloat64">Whether the device runs double-precision kernels.</param>
/// <param name="IsUsable">True when Finance.GPU can run its kernels on it.</param>
/// <param name="Note">Explanation when the device is not usable, or extra detail.</param>
public sealed record GpuDeviceInfo(
    string Name,
    GpuBackend Backend,
    long MemoryBytes,
    int MaxThreadsPerGroup,
    int Multiprocessors,
    bool SupportsFloat64,
    bool IsUsable,
    string? Note)
{
    /// <summary>Index into the owning engine's device list (ILGPU, HIP, Vulkan or DXGI adapter), or -1 when there is no device.</summary>
    internal int ContextIndex { get; init; } = -1;

    public override string ToString() => !IsUsable
        ? $"{Backend}: unavailable - {Note}"
        : Backend == GpuBackend.Cpu
            ? $"{Backend}: {Name} (host memory, {Environment.ProcessorCount} threads)"
            : MemoryBytes <= 0
                ? $"{Backend}: {Name} ({Note})"
                : $"{Backend}: {Name} ({MemoryBytes / (1024 * 1024)} MB{(Multiprocessors > 0 ? $", {Multiprocessors} CUs" : "")})";
}

/// <summary>Messages explaining what each backend can and cannot do.</summary>
public static class GpuBackendSupport
{
    /// <summary>Why a backend has no usable device on this machine (null for backends that are always present).</summary>
    public static string? UnsupportedReason(GpuBackend backend) => backend switch
    {
        GpuBackend.DirectML => OperatingSystem.IsWindows()
            ? "no Direct3D 12 adapter with double-precision shader support was found"
            : "Direct3D 12 (the DirectML backend) is available on Windows only",
        GpuBackend.Vulkan => VulkanEngine.Unavailable ?? "no Vulkan device with shaderFloat64/shaderInt64 was found",
        GpuBackend.Rocm => (HipEngine.Unavailable ?? "no HIP device") + "; no AMD OpenCL device was found either",
        _ => null,
    };
}
