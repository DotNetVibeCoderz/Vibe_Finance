using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;

namespace Finance.GPU;

/// <summary>
/// Direct3D 12 compute: the <see cref="GpuBackend.DirectML"/> backend. The HLSL kernels (Kernels/finance.hlsl) are
/// precompiled with DXC to DXIL, shader model 6.0 (Kernels/compile-dxil.ps1), and run on any DirectX 12 GPU whose
/// driver reports double-precision shader support, or on WARP, the software rasteriser that ships with Windows.
/// (Shader model 5.1 bytecode from FXC was tried first: its double-precision selects and stores executed wrongly on
/// both an Intel driver and WARP.)
/// </summary>
internal sealed class D3D12Engine : IGpuEngine
{
    private static readonly string[] EntryPoints = { "mc_european", "mc_asian", "black_scholes", "portfolio_return" };
    private static readonly Lazy<byte[][]> Bytecode = new(CompileKernels);

    private readonly ID3D12Device _device;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12CommandAllocator _allocator;
    private readonly ID3D12GraphicsCommandList _list;
    private readonly ID3D12Fence _fence;
    private readonly ID3D12RootSignature _root;
    private readonly ID3D12PipelineState[] _pipelines;
    private readonly AutoResetEvent _event = new(false);
    private ulong _fenceValue;

    private D3D12Engine(ID3D12Device device, byte[][]? bytecode = null)
    {
        _device = device;
        _queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Compute));
        _allocator = device.CreateCommandAllocator(CommandListType.Compute);
        _list = device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Compute, _allocator, null);
        _list.Close();
        _fence = device.CreateFence(0);
        // b0: grid width constant; t0..t2: parameter and input buffers; u0: output buffer (all root descriptors).
        var parameters = new[]
        {
            new RootParameter1(new RootConstants(0, 0, 1), ShaderVisibility.All),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All),
        };
        _root = device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None, parameters));
        _pipelines = (bytecode ?? Bytecode.Value).Select(code => device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = _root,
            ComputeShader = code,
        })).ToArray();
    }

    /// <summary>DXGI adapters with Direct3D 12 support, hardware first; WARP last.</summary>
    public static List<GpuDeviceInfo> Describe()
    {
        var list = new List<GpuDeviceInfo>();
        if (!OperatingSystem.IsWindows()) return list;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                using (adapter)
                {
                    var d = adapter.Description1;
                    bool software = (d.Flags & AdapterFlags.Software) != 0;
                    if (D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? dev).Failure || dev is null) continue;
                    bool fp64, sm6;
                    using (dev)
                    {
                        fp64 = dev.Options.DoublePrecisionFloatShaderOps;
                        sm6 = dev.CheckHighestShaderModel(ShaderModel.Model6_0) >= ShaderModel.Model6_0;
                    }
                    string name = software ? $"{d.Description} (WARP, software)" : d.Description;
                    string? note = !sm6 ? "driver does not support shader model 6.0"
                        : !fp64 ? "driver reports no double-precision shader support"
                        : software ? "Direct3D 12 software rasteriser (CPU)" : "Direct3D 12 compute shaders";
                    list.Add(new GpuDeviceInfo(name, GpuBackend.DirectML, (long)(ulong)d.DedicatedVideoMemory, 1024, 0, fp64, fp64 && sm6, note) { ContextIndex = (int)i });
                }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException or SharpGen.Runtime.SharpGenException)
        {
            // No DXGI/D3D12 on this system.
        }
        return list.OrderBy(x => x.Name.Contains("(WARP") ? 1 : 0).ToList();
    }

    /// <summary>Opens DXGI adapter <paramref name="index"/>.</summary>
    public static D3D12Engine Open(uint index)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1(index, out var adapter).CheckError();
        using (adapter)
        {
            D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? device).CheckError();
            return new D3D12Engine(device!);
        }
    }

    /// <summary>The kernel source the embedded DXIL was compiled from.</summary>
    internal static string KernelSource() => Resource("Finance.GPU.Kernels.finance.hlsl", r => new StreamReader(r).ReadToEnd());

    /// <summary>SHA-256 of <see cref="KernelSource"/> (LF line endings) recorded when the DXIL was compiled.</summary>
    internal static string DxilSourceHash() => Resource("Finance.GPU.Kernels.dxil.finance.hlsl.sha256", r => new StreamReader(r).ReadToEnd().Trim());

    private static T Resource<T>(string name, Func<Stream, T> read)
    {
        using var stream = typeof(D3D12Engine).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded resource {name} missing");
        return read(stream);
    }

    private static byte[][] CompileKernels() => EntryPoints.Select(e => Resource($"Finance.GPU.Kernels.dxil.{e}.dxil", r =>
    {
        using var ms = new MemoryStream();
        r.CopyTo(ms);
        return ms.ToArray();
    })).ToArray();

    private ID3D12Resource Buffer(HeapType heap, ulong bytes, ResourceStates state, ResourceFlags flags = ResourceFlags.None)
        => _device.CreateCommittedResource(new HeapProperties(heap), HeapFlags.None,
            ResourceDescription.Buffer(Math.Max(bytes, 16), flags), state);

    private unsafe ID3D12Resource Upload<T>(T[] data) where T : unmanaged
    {
        ulong bytes = (ulong)(data.Length * sizeof(T));
        var buffer = Buffer(HeapType.Upload, bytes, ResourceStates.GenericRead);
        if (data.Length > 0)
        {
            void* ptr;
            buffer.Map(0, null, &ptr).CheckError();
            fixed (T* src = data) System.Buffer.MemoryCopy(src, ptr, (long)bytes, (long)bytes);
            buffer.Unmap(0);
        }
        return buffer;
    }

    private unsafe void Run(int kernel, double[] fp, uint[] ip, double[] input, double[] output, int threads)
    {
        int groups = Math.Max(1, (threads + 63) / 64);
        int gx = Math.Min(groups, 65535), gy = (groups + gx - 1) / gx;
        ulong outBytes = (ulong)output.Length * 8;
        using var bFp = Upload(fp.Length > 0 ? fp : new double[1]);
        using var bIp = Upload(ip.Length > 0 ? ip : new uint[1]);
        using var bInUpload = Upload(input.Length > 0 ? input : new double[1]);
        using var bIn = Buffer(HeapType.Default, (ulong)Math.Max(1, input.Length) * 8, ResourceStates.CopyDest);
        using var bOut = Buffer(HeapType.Default, outBytes, ResourceStates.UnorderedAccess, ResourceFlags.AllowUnorderedAccess);
        using var bRead = Buffer(HeapType.Readback, outBytes, ResourceStates.CopyDest);

        _allocator.Reset();
        _list.Reset(_allocator, _pipelines[kernel]);
        _list.CopyBufferRegion(bIn, 0, bInUpload, 0, (ulong)Math.Max(1, input.Length) * 8);
        _list.ResourceBarrierTransition(bIn, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
        _list.SetComputeRootSignature(_root);
        _list.SetComputeRoot32BitConstant(0, (uint)(gx * 64), 0);
        _list.SetComputeRootShaderResourceView(1, bFp.GPUVirtualAddress);
        _list.SetComputeRootShaderResourceView(2, bIp.GPUVirtualAddress);
        _list.SetComputeRootShaderResourceView(3, bIn.GPUVirtualAddress);
        _list.SetComputeRootUnorderedAccessView(4, bOut.GPUVirtualAddress);
        _list.Dispatch((uint)gx, (uint)gy, 1);
        _list.ResourceBarrierTransition(bOut, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        _list.CopyBufferRegion(bRead, 0, bOut, 0, outBytes);
        _list.Close();
        _queue.ExecuteCommandList(_list);
        _queue.Signal(_fence, ++_fenceValue).CheckError();
        if (_fence.CompletedValue < _fenceValue)
        {
            _fence.SetEventOnCompletion(_fenceValue, _event.SafeWaitHandle.DangerousGetHandle()).CheckError();
            _event.WaitOne();
        }
        _device.DeviceRemovedReason.CheckError();

        void* ptr;
        bRead.Map(0, null, &ptr).CheckError();
        fixed (double* dst = output) System.Buffer.MemoryCopy(ptr, dst, (long)outBytes, (long)outBytes);
        bRead.Unmap(0);
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

    public void Dispose()
    {
        foreach (var p in _pipelines) p.Dispose();
        _root.Dispose();
        _fence.Dispose();
        _list.Dispose();
        _allocator.Dispose();
        _queue.Dispose();
        _device.Dispose();
        _event.Dispose();
    }
}
