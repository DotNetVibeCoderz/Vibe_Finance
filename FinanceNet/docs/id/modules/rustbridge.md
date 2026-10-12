# Finance.RustBridge

[← Indeks](../index.md) · [English](../../en/modules/rustbridge.md) · Paket: `Gravicode.Finance.RustBridge`

Kernel **Rust** native untuk loop yang paling berat — indikator, statistik rolling, VaR/CVaR historis,
kovarians, Black-Scholes batch, pricing Monte Carlo, dan simulasi jalur GBM — yang dibuka lewat API C#
berbasis span yang aman. Jika library native tidak ada, API yang sama menjalankan **implementasi managed
dengan algoritma dan aliran bilangan acak yang identik**, sehingga hasil tidak pernah bergantung pada
ada atau tidaknya Rust.

Namespace: `Finance.RustBridge`. Crate-nya ada di `native/finance-native`.

## Cara kerjanya

```text
FastIndicators / FastRisk / FastPricing      (API C# publik, span masuk dan keluar)
        │
        ├── RustAccelerator.IsAvailable ? NativeKernels  →  finance_native.dll / .so / .dylib  (C ABI, fnat_*)
        └──                              : ManagedKernels (algoritma sama, aliran RNG sama)
```

- Crate-nya berupa `cdylib` **tanpa crate eksternal** (bisa dibangun offline). Isinya katalog indikator 248 entri
  (`src/ta`), HRP (`src/hrp.rs`), dan dispatch SIMD (`src/simd.rs`). Setiap fungsi ekspor
  menerima pointer + panjang, mengembalikan kode status (0 sukses, −1 pointer null, −2 argumen salah,
  −3 panic), dan menangkap panic agar tidak pernah menyeberang ke .NET.
- P/Invoke memakai `LibraryImport` yang di-generate oleh source generator.
- Monte Carlo selalu membagi pekerjaan menjadi **64 aliran acak tetap** dengan generator xoshiro yang
  sama seperti `Finance.Core.FastRandom`, sehingga native, managed, dan berapa pun jumlah thread
  memberikan harga yang sama.

## Status dan pemuatan

```csharp
using Finance.RustBridge;

Console.WriteLine(RustAccelerator.Describe());       // mis. "Rust accelerator 0.9.0 (path, SIMD avx2)" atau "... unavailable - managed kernels in use"
bool native = RustAccelerator.IsAvailable;           // berhasil dimuat dan tidak dimatikan paksa
string? version = RustAccelerator.NativeVersion;
string? why = RustAccelerator.LoadError;
RustAccelerator.ForceManaged = true;                 // atau env FINANCE_NATIVE_DISABLE=1
```

Urutan pencarian: variabel lingkungan `FINANCE_NATIVE_PATH`, di samping assembly, lalu
`runtimes/{rid}/native`. Paket NuGet membawa ketiga binary, dibangun CI di masing-masing OS:
`runtimes/win-x64/native/finance_native.dll`, `runtimes/linux-x64/native/libfinance_native.so` dan
`runtimes/osx-arm64/native/libfinance_native.dylib`.

## API

```csharp
double[] close = /* ... */;
double[] sma = FastIndicators.Sma(close, 20);
double[] ema = FastIndicators.Ema(close, 20);
double[] rsi = FastIndicators.Rsi(close, 14);
double[] sd = FastIndicators.RollingStd(close, 20);
var (mid, upper, lower) = FastIndicators.Bollinger(close, 20, 2.0);
double[] lr = FastIndicators.LogReturns(close);
DrawdownInfo dd = FastIndicators.MaxDrawdown(close);          // Depth, PeakIndex, TroughIndex

// Overload tanpa alokasi menulis ke buffer Anda
Span<double> buffer = new double[close.Length];
FastIndicators.Sma(close, 20, buffer);

TailRisk tail = FastRisk.HistoricalVaR(returns, confidence: 0.99);    // VaR, CVaR (kerugian positif)
double[] cov = FastRisk.Covariance(rowMajorData, rows, cols);

var batch = new OptionBatch(spot, strike, time, rate, dividend, volatility, isCall);   // span dengan panjang sama
BlackScholesBatchResult bs = FastPricing.BlackScholesBatch(batch);
MonteCarloEstimate mc = FastPricing.MonteCarloEuropean(
    new EuropeanOptionSpec(Spot: 100, Strike: 100, Time: 1, Rate: 0.05, Dividend: 0, Volatility: 0.2, IsCall: true),
    paths: 4_000_000, seed: 42, antithetic: true);
double[] paths = FastPricing.SimulateGbm(s0: 100, mu: 0.08, sigma: 0.2, dt: 1.0 / 252, steps: 252, paths: 1000);
```

`ManagedKernels` dan `NativeKernels` juga publik, untuk membandingkan satu jalur dengan yang lain dalam
benchmark.

## Katalog indikator, HRP, dan SIMD (v0.5)

```csharp
using Finance.RustBridge;
using Finance.TimeSeries.Indicators;

// Setiap entri IndicatorCatalog (248: 188 indikator + 60 pola candlestick) berjalan native.
IndicatorSeries[] macd = FastCatalog.Compute("MACD", frame, new Dictionary<string, double> { ["fast"] = 8 });
bool native = FastCatalog.IsNative("HT_SINE");                  // false jika library tidak ada atau dimatikan
IReadOnlyCollection<string> ids = FastCatalog.NativeIds;

HrpResult hrp = FastPortfolio.Hrp(covariance, assets, LinkageMethod.Single);   // hasil sama dengan HierarchicalRiskParity.Optimize

Console.WriteLine(RustAccelerator.SimdLevel);                   // Scalar (SSE2/NEON), Avx2 atau Avx512, dari CPUID
RustAccelerator.SimdLevel = NativeSimdLevel.Scalar;             // atau env FINANCE_NATIVE_SIMD=scalar|avx2|avx512
```

- **Paritas, bukan aproksimasi.** Katalog Rust adalah port baris demi baris: sebuah tes menjalankan setiap entri
  dengan parameter default dan acak, dan mensyaratkan nama output yang sama, NaN di posisi yang sama, dan nilai sama
  hingga 1e-9 relatif (dalam praktiknya cocok hingga beberapa bit terakhir). HRP harus menghasilkan urutan daun,
  dendrogram, dan bobot yang sama hingga 14 digit untuk ketiga linkage.
- **SIMD.** Loop element-wise yang berat (kovarians) dikompilasi tiga kali — baseline, AVX2+FMA, AVX-512F — dan
  dipilih saat runtime. Urutan penjumlahan tidak pernah diubah, sehingga setiap level memberi hasil identik bit demi
  bit; sebuah tes memeriksa setiap level yang didukung CPU. Jalur AVX-512 belum dieksekusi di laptop pengembangan
  (tidak ada AVX-512).
- Terukur di i7-8650U 4 core (AVX2, warm, terbaik dari 3): seluruh katalog pada 20.000 bar 620 ms managed vs 492 ms
  native (1,3×; entri terberat — Hurst, profil volume/TPO, transformasi Hilbert — setara, .NET sudah cepat di sana);
  HRP 400 aset 40,7 → 15,5 ms (2,6×); kovarians 2000 × 400 154 ms managed, 81 ms native baseline, 60 ms native AVX2.

## Build

```bash
cd native/finance-native
cargo build --release      # target/release/finance_native.dll (atau .so / .dylib)
cargo test                 # unit test Rust
```

Membangun `Finance.RustBridge` otomatis menjalankan `cargo build --release` jika `cargo` ada di
`PATH`; `-p:SkipRustBuild=true` melewatinya. DLL disalin ke output setiap proyek yang mereferensikan
RustBridge.

## Catatan dan batasan

- Rust jelas unggul hanya untuk pekerjaan yang berat per elemen atau paralel: VaR historis (~2,6×) dan
  Monte Carlo multi-thread. Indikator sederhana dan Black-Scholes berjalan kurang lebih sama cepatnya di
  .NET managed — lihat [Benchmark](../benchmarks.md).
- Platform lain (linux-arm64, osx-x64, win-arm64) belum punya binary native dan memakai fallback
  managed, yang hasilnya identik.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
