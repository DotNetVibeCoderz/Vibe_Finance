# Finance.GPU

[← Indeks](../index.md) · [English](../../en/modules/gpu.md) · Paket: `Gravicode.Finance.GPU`

Pricing opsi Monte Carlo, Black-Scholes batch beserta Greeks, dan VaR portofolio Monte Carlo yang
dipercepat GPU. Kernel berjalan di empat engine dengan semantik dan bilangan acak yang identik: **ILGPU**
(CUDA, OpenCL, CPU accelerator), **HIP native** (AMD ROCm), compute **Vulkan**, dan compute **Direct3D 12**
(backend `DirectML`), dengan pemilihan perangkat dan fallback otomatis.

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

using GpuContext ctx = GpuContext.CreateBest(allowCpu: true);   // CUDA → ROCm → OpenCL (FP64) → Vulkan → Direct3D 12 → CPU
Console.WriteLine($"{ctx.Device.Name} via {ctx.Backend}{(ctx.FallbackReason is { } why ? $" (fallback: {why})" : "")}");

using GpuContext cuda = GpuContext.Create(GpuBackend.Cuda);     // melempar NotSupportedException (beserta alasannya) jika tidak tersedia
using GpuContext warp = GpuContext.Create(GpuContext.ListDevices().First(d => d.Name.Contains("WARP")));   // satu perangkat dari daftar
```

| `GpuBackend` | Engine | Status |
|---|---|---|
| `Cuda` | ILGPU (PTX) | Didukung (NVIDIA) |
| `OpenCL` | ILGPU | Didukung untuk perangkat dengan presisi ganda (Intel, AMD, NVIDIA) |
| `Cpu` | CPU accelerator ILGPU | Selalu tersedia |
| `Rocm` | HIP native: kernel C++ dikompilasi saat runtime dengan hiprtc | Butuh AMD HIP SDK (Windows) atau ROCm (Linux); tanpa itu dipakai perangkat OpenCL AMD. **Belum diverifikasi di hardware AMD** (tidak tersedia); source kernel diperiksa di host terhadap rumus tertutup dan RNG referensi |
| `Vulkan` | Library native `finance_gpu` (Rust, wgpu 24, WGSL dengan f64/i64) | Perangkat dengan `shaderFloat64` + `shaderInt64`; terverifikasi di Intel UHD 620 |
| `DirectML` | Compute shader Direct3D 12 (HLSL → DXIL, shader model 6.0, dikompilasi sebelumnya) | GPU DirectX 12 apa pun dengan shader presisi ganda, atau WARP (perangkat software bawaan Windows); khusus Windows; terverifikasi di Intel UHD 620 dan WARP |

`DirectML` sendiri adalah library operator tensor FP16/FP32 tanpa operator transendental presisi ganda; backend ini
menjalankan kernel FP64 Finance.GPU di lapisan compute Direct3D 12 yang menjadi dasar DirectML.
`GpuBackendSupport.UnsupportedReason(backend)` menjelaskan mengapa sebuah backend tidak punya perangkat di mesin ini.

Cara engine non-ILGPU menjaga presisi ganda penuh: SPIR-V dan DXIL tidak punya `exp`/`log`/`cos` 64-bit, jadi
kernel mengimplementasikannya sendiri (reduksi rentang + polinomial, galat relatif ~1e-16). Kernel Direct3D 12
juga menghindari instruksi opsional "extended double" (pembagian, konversi int↔double) dan integer 64-bit, sehingga
berjalan di perangkat apa pun yang mendukung double. Tes paritas membandingkan setiap engine dengan referensi host:
jumlah Monte Carlo cocok hingga 1e-9 relatif, Black-Scholes batch 1e-10, VaR 1e-12.

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
- Setiap engine menyimpan buffer perangkat yang hanya bertambah besar per context (0.10), sehingga panggilan berulang
  tidak mengalokasikan apa pun setelah ukurannya pernah terlihat; host Vulkan juga mengirim array sebagai tampilan
  byte tanpa salinan.
- Terukur di Intel UHD 620 yang sama (0.10, warm, terbaik dari 2):

  | Engine | MC Eropa 4 juta jalur | BS batch 1 juta | BS batch 1 ribu (per panggilan) | VaR MC 1 juta |
  |---|---|---|---|---|
  | OpenCL (ILGPU) | 18 ms | 59 ms | 1,02 ms | 133 ms |
  | Direct3D 12 | 24 ms | 80 ms | 0,31 ms | 162 ms |
  | Vulkan | 27 ms | 83 ms | 0,29 ms | 161 ms |
  | WARP (software) | 180 ms | 199 ms | 0,39 ms | 358 ms |
  | CPU ILGPU | 190 ms | 308 ms | 0,68 ms | 508 ms |

  OpenCL tercepat untuk batch besar di GPU ini (`exp`/`log` native dibanding versi polinomial yang dibutuhkan engine
  lain); Vulkan dan Direct3D 12 punya overhead per panggilan terendah dan berguna ketika OpenCL tidak ada
  (Windows-on-ARM, banyak cloud dan AMD konsumen). Di 0.9, sebelum buffer dipakai ulang, Vulkan butuh 168 ms untuk 1 juta opsi.
- wgpu 24 dipakai dengan sengaja: wgpu 30 crash di dalam `vkCreateDevice` pada driver Vulkan Intel (2022).
- Shader Direct3D 12 dikompilasi sebelumnya: setelah mengubah `Kernels/finance.hlsl`, jalankan
  `Kernels/compile-dxil.ps1` (sebuah tes gagal selama DXIL yang tertanam basi). Bytecode shader model 5.1 dari FXC
  sempat dicoba dan mengeksekusi select dan store presisi ganda dengan salah, baik di driver Intel maupun WARP.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
