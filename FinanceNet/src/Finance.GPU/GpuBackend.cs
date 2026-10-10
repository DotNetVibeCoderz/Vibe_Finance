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
    /// <summary>AMD ROCm. ILGPU has no native ROCm/HIP backend; AMD GPUs are reached through their OpenCL driver.</summary>
    Rocm,
    /// <summary>DirectML. Not supported by ILGPU - reported as unavailable.</summary>
    DirectML,
    /// <summary>Vulkan compute. Not supported by ILGPU - reported as unavailable.</summary>
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
    /// <summary>Index into ILGPU's device list, or -1 for backends that have no device.</summary>
    internal int ContextIndex { get; init; } = -1;

    public override string ToString() => !IsUsable
        ? $"{Backend}: unavailable - {Note}"
        : Backend == GpuBackend.Cpu
            ? $"{Backend}: {Name} (host memory, {Environment.ProcessorCount} threads)"
            : $"{Backend}: {Name} ({MemoryBytes / (1024 * 1024)} MB, {Multiprocessors} CUs)";
}

/// <summary>Messages explaining what each backend can and cannot do.</summary>
public static class GpuBackendSupport
{
    /// <summary>Message for backends ILGPU cannot drive.</summary>
    public static string? UnsupportedReason(GpuBackend backend) => backend switch
    {
        GpuBackend.DirectML => "DirectML is not supported: ILGPU (the GPU compiler behind Finance.GPU) targets CUDA, OpenCL and CPU only. Use CUDA or OpenCL on the same GPU.",
        GpuBackend.Vulkan => "Vulkan compute is not supported: ILGPU has no SPIR-V/Vulkan backend. Use CUDA or OpenCL on the same GPU.",
        GpuBackend.Rocm => "ROCm has no native ILGPU backend; AMD GPUs are used through the OpenCL driver that ships with ROCm/Adrenalin. No AMD OpenCL device was found.",
        _ => null,
    };
}
