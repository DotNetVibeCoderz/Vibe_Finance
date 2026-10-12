# Finance.GPU

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/gpu.md) · Package: `Gravicode.Finance.GPU`

GPU-accelerated Monte Carlo option pricing, batch Black-Scholes with Greeks, and Monte Carlo
portfolio VaR. The kernels run on four engines with identical semantics and the same random numbers:
**ILGPU** (CUDA, OpenCL, CPU accelerator), **native HIP** (AMD ROCm), **Vulkan** compute and
**Direct3D 12** compute (the `DirectML` backend), with automatic device selection and fallback.

Namespace: `Finance.GPU`.

## Key concepts

- **Embarrassingly parallel** — every Monte Carlo path or option in a batch is independent, so one
  GPU thread handles one (or a few) of them and partial sums are reduced at the end.
- **Counter-based random numbers** — each thread derives its normals from a hash of
  `(seed, counter)` with no per-thread state. Results are reproducible and identical on GPU and host
  (`...Reference` methods compute the same numbers on CPU threads).
- **Double precision** — finance needs `double`; OpenCL devices are used only if they support
  64-bit floats.

## Devices and backends

```csharp
using Finance.GPU;

foreach (GpuDeviceInfo d in GpuContext.ListDevices())
    Console.WriteLine(d);                       // name, backend, memory, FP64 support, usable?

using GpuContext ctx = GpuContext.CreateBest(allowCpu: true);   // CUDA → ROCm → OpenCL (FP64) → Vulkan → Direct3D 12 → CPU
Console.WriteLine($"{ctx.Device.Name} via {ctx.Backend}{(ctx.FallbackReason is { } why ? $" (fallback: {why})" : "")}");

using GpuContext cuda = GpuContext.Create(GpuBackend.Cuda);     // throws NotSupportedException (with the reason) if not available
using GpuContext warp = GpuContext.Create(GpuContext.ListDevices().First(d => d.Name.Contains("WARP")));   // one listed device
```

| `GpuBackend` | Status |
|---|---|
| `GpuBackend` | Engine | Status |
|---|---|---|
| `Cuda` | ILGPU (PTX) | Supported (NVIDIA) |
| `OpenCL` | ILGPU | Supported for devices with double precision (Intel, AMD, NVIDIA) |
| `Cpu` | ILGPU CPU accelerator | Always available |
| `Rocm` | Native HIP: C++ kernels compiled at run time with hiprtc | Needs the AMD HIP SDK (Windows) or ROCm (Linux); without it an AMD OpenCL device is used. **Not verified on AMD hardware** (none available); the kernel source was checked on the host against closed forms and the reference RNG |
| `Vulkan` | `finance_gpu` native library (Rust, wgpu 24, WGSL with f64/i64) | Devices with `shaderFloat64` + `shaderInt64`; verified on Intel UHD 620 |
| `DirectML` | Direct3D 12 compute shaders (HLSL → DXIL, shader model 6.0, precompiled) | Any DirectX 12 GPU with double-precision shaders, or WARP (Windows' software device); Windows only; verified on Intel UHD 620 and WARP |

`DirectML` itself is an FP16/FP32 tensor-operator library with no double-precision transcendental operators;
the backend runs Finance.GPU's FP64 kernels on the Direct3D 12 compute layer DirectML is built on.
`GpuBackendSupport.UnsupportedReason(backend)` explains why a backend has no device on this machine.

How the non-ILGPU engines keep full double precision: SPIR-V and DXIL have no 64-bit `exp`/`log`/`cos`, so the
kernels implement them (range reduction + polynomials, relative error ~1e-16). The Direct3D 12 kernels also avoid
optional "extended double" instructions (division, int↔double conversion) and 64-bit integers, so they run on any
device that reports double support. A parity test compares every engine with the host reference: Monte Carlo sums
agree to 1e-9 relative, batch Black-Scholes to 1e-10, VaR to 1e-12.

## Pricing

```csharp
var pricer = new GpuOptionPricer(ctx);

GpuMonteCarloResult euro = pricer.MonteCarloEuropean(spot: 100, strike: 100, time: 1, rate: 0.05, dividend: 0,
    volatility: 0.2, isCall: true, paths: 4_000_000, seed: 42);
Console.WriteLine($"{euro.Price:F4} ± {euro.StdError:F4} on {euro.Device} in {euro.Elapsed.TotalMilliseconds:F0} ms");

GpuMonteCarloResult asian = pricer.MonteCarloAsian(100, 100, 1, 0.05, 0, 0.2, isCall: true, steps: 252, paths: 200_000);

int n = 1_000_000;
double[] s = new double[n], k = new double[n], t = new double[n], r = new double[n], q = new double[n], v = new double[n];
bool[] calls = new bool[n];
// ... fill the contract arrays ...
GpuGreeksBatch greeks = pricer.BlackScholesBatch(s, k, t, r, q, v, calls);   // Price, Delta, Gamma, Vega, Theta, Rho
```

## Risk

```csharp
var risk = new GpuRiskEngine(ctx);
GpuVaRResult var99 = risk.MonteCarloVaR(weights, annualDrift, annualCovariance, horizonYears: 10.0 / 252,
    scenarios: 1_000_000, confidence: 0.99);
Console.WriteLine($"VaR {var99.VaR:P2}  CVaR {var99.CVaR:P2}  ({var99.Scenarios:N0} scenarios on {var99.Device})");

GpuVaRResult check = GpuRiskEngine.MonteCarloVaRReference(weights, annualDrift, annualCovariance, 10.0 / 252);
```

Correlated normals are produced through the Cholesky factor of the covariance matrix.

## Notes and limitations

- **Measure before you switch.** On the development laptop (Intel UHD 620 over OpenCL) the GPU won
  for 4M-path Monte Carlo and 200k-scenario VaR but only modestly for batch Black-Scholes — see
  [Benchmarks](../benchmarks.md). Small workloads are dominated by kernel launch and transfer cost.
- The first call on a context compiles the kernels (JIT); warm up before timing.
- There is no GPU historical VaR (sorting is cheaper on the CPU or in Rust for typical sizes).
- Every engine keeps grow-only device buffers per context (0.10), so repeated calls allocate nothing once the sizes
  have been seen; the Vulkan host also passes arrays as zero-copy byte views.
- Measured on the same Intel UHD 620 (0.10, warm, best of 2):

  | Engine | MC European 4M paths | Batch BS 1M | Batch BS 1k (per call) | MC VaR 1M |
  |---|---|---|---|---|
  | OpenCL (ILGPU) | 18 ms | 59 ms | 1.02 ms | 133 ms |
  | Direct3D 12 | 24 ms | 80 ms | 0.31 ms | 162 ms |
  | Vulkan | 27 ms | 83 ms | 0.29 ms | 161 ms |
  | WARP (software) | 180 ms | 199 ms | 0.39 ms | 358 ms |
  | ILGPU CPU | 190 ms | 308 ms | 0.68 ms | 508 ms |

  OpenCL is fastest for large batches on this GPU (native `exp`/`log` versus the polynomial versions the other
  engines need); Vulkan and Direct3D 12 have the lowest per-call overhead and matter where OpenCL is missing
  (Windows-on-ARM, many cloud and AMD consumer setups). In 0.9, before buffer reuse, Vulkan took 168 ms for 1M options.
- wgpu 24 is used on purpose: wgpu 30 crashed inside `vkCreateDevice` on the (2022) Intel Vulkan driver.
- The Direct3D 12 shaders are precompiled: after editing `Kernels/finance.hlsl` run `Kernels/compile-dxil.ps1`
  (a test fails while the embedded DXIL is stale). Shader model 5.1 bytecode from FXC was tried first and
  executed double-precision selects and stores wrongly on both the Intel driver and WARP.

---

Made by Gravicode Studios, led by Kang Fadhil
