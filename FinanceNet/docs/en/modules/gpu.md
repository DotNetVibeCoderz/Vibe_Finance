# Finance.GPU

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/gpu.md) · Package: `Gravicode.Finance.GPU`

GPU-accelerated Monte Carlo option pricing, batch Black-Scholes with Greeks, and Monte Carlo
portfolio VaR, written as **ILGPU** kernels. The same code runs on CUDA, OpenCL or ILGPU's CPU
accelerator, with automatic device selection and fallback.

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

using GpuContext ctx = GpuContext.CreateBest(allowCpu: true);   // CUDA → OpenCL (FP64) → CPU accelerator
Console.WriteLine($"{ctx.Device.Name} via {ctx.Backend}{(ctx.FallbackReason is { } why ? $" (fallback: {why})" : "")}");

using GpuContext cuda = GpuContext.Create(GpuBackend.Cuda);     // throws if not available
```

| `GpuBackend` | Status |
|---|---|
| `Cuda` | Supported (NVIDIA, via ILGPU) |
| `OpenCL` | Supported for devices with double precision (Intel, AMD, NVIDIA) |
| `Cpu` | Supported (ILGPU CPU accelerator; always available) |
| `Rocm` | Mapped to an AMD **OpenCL** device when one exists; otherwise throws with an explanation |
| `DirectML` | **Not available** — ILGPU has no DirectML backend; `Create` throws `NotSupportedException` |
| `Vulkan` | **Not available** — ILGPU has no Vulkan compute backend; `Create` throws `NotSupportedException` |

`GpuBackendSupport.UnsupportedReason(backend)` returns the explanation text.

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
- DirectML and Vulkan are on the roadmap and would need a separate kernel path.

---

Made by Gravicode Studios, led by Kang Fadhil
