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

- Crate-nya berupa `cdylib` **tanpa crate eksternal** (bisa dibangun offline). Setiap fungsi ekspor
  menerima pointer + panjang, mengembalikan kode status (0 sukses, −1 pointer null, −2 argumen salah,
  −3 panic), dan menangkap panic agar tidak pernah menyeberang ke .NET.
- P/Invoke memakai `LibraryImport` yang di-generate oleh source generator.
- Monte Carlo selalu membagi pekerjaan menjadi **64 aliran acak tetap** dengan generator xoshiro yang
  sama seperti `Finance.Core.FastRandom`, sehingga native, managed, dan berapa pun jumlah thread
  memberikan harga yang sama.

## Status dan pemuatan

```csharp
using Finance.RustBridge;

Console.WriteLine(RustAccelerator.Describe());       // mis. "Rust accelerator 0.2.0 (path)" atau "... unavailable - managed kernels in use"
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
