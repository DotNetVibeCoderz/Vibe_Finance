# Finance.GPU

[← Indeks](../index.md) · [English](../../en/modules/gpu.md) · Paket: `Gravicode.Finance.GPU`

Pricing opsi Monte Carlo, Black-Scholes batch beserta Greeks, dan VaR portofolio Monte Carlo yang
dipercepat GPU, ditulis sebagai kernel **ILGPU**. Kode yang sama berjalan di CUDA, OpenCL, atau CPU
accelerator milik ILGPU, dengan pemilihan perangkat dan fallback otomatis.

Namespace: `Finance.GPU`.

## Konsep utama

- **Sangat paralel (embarrassingly parallel)** — setiap jalur Monte Carlo atau setiap opsi dalam batch
  saling independen, sehingga satu thread GPU menangani satu (atau beberapa) di antaranya, lalu jumlah
  parsialnya direduksi di akhir.
- **Bilangan acak berbasis counter** — setiap thread menurunkan bilangan normalnya dari hash
  `(seed, counter)` tanpa state per thread. Hasilnya dapat direproduksi dan identik antara GPU dan host
  (metode `...Reference` menghitung angka yang sama di thread CPU).
- **Presisi ganda** — keuangan butuh `double`; perangkat OpenCL hanya dipakai jika mendukung floating
  point 64-bit.

## Perangkat dan backend

```csharp
using Finance.GPU;

foreach (GpuDeviceInfo d in GpuContext.ListDevices())
    Console.WriteLine(d);                       // nama, backend, memori, dukungan FP64, bisa dipakai?

using GpuContext ctx = GpuContext.CreateBest(allowCpu: true);   // CUDA → OpenCL (FP64) → CPU accelerator
Console.WriteLine($"{ctx.Device.Name} via {ctx.Backend}{(ctx.FallbackReason is { } why ? $" (fallback: {why})" : "")}");

using GpuContext cuda = GpuContext.Create(GpuBackend.Cuda);     // melempar exception jika tidak tersedia
```

| `GpuBackend` | Status |
|---|---|
| `Cuda` | Didukung (NVIDIA, lewat ILGPU) |
| `OpenCL` | Didukung untuk perangkat dengan presisi ganda (Intel, AMD, NVIDIA) |
| `Cpu` | Didukung (CPU accelerator ILGPU; selalu tersedia) |
| `Rocm` | Dipetakan ke perangkat **OpenCL** AMD jika ada; jika tidak, melempar exception beserta penjelasannya |
| `DirectML` | **Tidak tersedia** — ILGPU tidak punya backend DirectML; `Create` melempar `NotSupportedException` |
| `Vulkan` | **Tidak tersedia** — ILGPU tidak punya backend compute Vulkan; `Create` melempar `NotSupportedException` |

`GpuBackendSupport.UnsupportedReason(backend)` mengembalikan teks penjelasannya.

## Pricing

```csharp
var pricer = new GpuOptionPricer(ctx);

GpuMonteCarloResult euro = pricer.MonteCarloEuropean(spot: 100, strike: 100, time: 1, rate: 0.05, dividend: 0,
    volatility: 0.2, isCall: true, paths: 4_000_000, seed: 42);
Console.WriteLine($"{euro.Price:F4} ± {euro.StdError:F4} di {euro.Device} dalam {euro.Elapsed.TotalMilliseconds:F0} ms");

GpuMonteCarloResult asian = pricer.MonteCarloAsian(100, 100, 1, 0.05, 0, 0.2, isCall: true, steps: 252, paths: 200_000);

int n = 1_000_000;
double[] s = new double[n], k = new double[n], t = new double[n], r = new double[n], q = new double[n], v = new double[n];
bool[] calls = new bool[n];
// ... isi array kontrak ...
GpuGreeksBatch greeks = pricer.BlackScholesBatch(s, k, t, r, q, v, calls);   // Price, Delta, Gamma, Vega, Theta, Rho
```

## Risiko

```csharp
var risk = new GpuRiskEngine(ctx);
GpuVaRResult var99 = risk.MonteCarloVaR(weights, annualDrift, annualCovariance, horizonYears: 10.0 / 252,
    scenarios: 1_000_000, confidence: 0.99);
Console.WriteLine($"VaR {var99.VaR:P2}  CVaR {var99.CVaR:P2}  ({var99.Scenarios:N0} skenario di {var99.Device})");

GpuVaRResult check = GpuRiskEngine.MonteCarloVaRReference(weights, annualDrift, annualCovariance, 10.0 / 252);
```

Bilangan normal berkorelasi dibuat melalui faktor Cholesky dari matriks kovarians.

## Catatan dan batasan

- **Ukur dulu sebelum beralih.** Di laptop pengembangan (Intel UHD 620 lewat OpenCL), GPU unggul untuk
  Monte Carlo 4 juta jalur dan VaR 200 ribu skenario, tetapi hanya sedikit lebih cepat untuk Black-Scholes
  batch — lihat [Benchmark](../benchmarks.md). Beban kerja kecil didominasi biaya peluncuran kernel dan
  transfer data.
- Panggilan pertama pada sebuah context mengompilasi kernel (JIT); lakukan pemanasan sebelum mengukur
  waktu.
- Belum ada VaR historis di GPU (untuk ukuran umum, pengurutan lebih murah di CPU atau Rust).
- DirectML dan Vulkan ada di roadmap dan membutuhkan jalur kernel tersendiri.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
