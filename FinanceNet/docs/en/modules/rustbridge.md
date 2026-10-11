# Finance.RustBridge

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/rustbridge.md) · Package: `Gravicode.Finance.RustBridge`

Native **Rust** kernels for hot loops — indicators, rolling statistics, historical VaR/CVaR,
covariance, batch Black-Scholes, Monte Carlo pricing and GBM path simulation — exposed through safe,
span-based C# APIs. When the native library is not present, the same API runs **managed
implementations with identical algorithms and random streams**, so results never depend on whether
Rust is installed.

Namespace: `Finance.RustBridge`. The crate lives in `native/finance-native`.

## How it fits together

```text
FastIndicators / FastRisk / FastPricing      (public C# API, spans in and out)
        │
        ├── RustAccelerator.IsAvailable ? NativeKernels  →  finance_native.dll / .so / .dylib  (C ABI, fnat_*)
        └──                              : ManagedKernels (same algorithms, same RNG streams)
```

- The crate is a `cdylib` with **no external crates** (builds offline). Every export takes pointer +
  length, returns a status code (0 ok, −1 null pointer, −2 bad argument, −3 panic) and catches panics
  so they never cross into .NET.
- P/Invoke uses source-generated `LibraryImport`.
- Monte Carlo always splits work into **64 fixed random streams** using the same xoshiro generator
  as `Finance.Core.FastRandom`, so native, managed and any thread count give the same price.

## Status and loading

```csharp
using Finance.RustBridge;

Console.WriteLine(RustAccelerator.Describe());       // e.g. "Rust accelerator 0.1.0 (path)" or "... unavailable - managed kernels in use"
bool native = RustAccelerator.IsAvailable;           // loaded and not forced off
string? version = RustAccelerator.NativeVersion;
string? why = RustAccelerator.LoadError;
RustAccelerator.ForceManaged = true;                 // or env FINANCE_NATIVE_DISABLE=1
```

Search order: the `FINANCE_NATIVE_PATH` environment variable, next to the assembly, then
`runtimes/{rid}/native`. The NuGet package carries all three binaries, built by CI on each OS:
`runtimes/win-x64/native/finance_native.dll`, `runtimes/linux-x64/native/libfinance_native.so` and
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

// Allocation-free overloads write into your buffer
Span<double> buffer = new double[close.Length];
FastIndicators.Sma(close, 20, buffer);

TailRisk tail = FastRisk.HistoricalVaR(returns, confidence: 0.99);    // VaR, CVaR (positive losses)
double[] cov = FastRisk.Covariance(rowMajorData, rows, cols);

var batch = new OptionBatch(spot, strike, time, rate, dividend, volatility, isCall);   // spans of equal length
BlackScholesBatchResult bs = FastPricing.BlackScholesBatch(batch);
MonteCarloEstimate mc = FastPricing.MonteCarloEuropean(
    new EuropeanOptionSpec(Spot: 100, Strike: 100, Time: 1, Rate: 0.05, Dividend: 0, Volatility: 0.2, IsCall: true),
    paths: 4_000_000, seed: 42, antithetic: true);
double[] paths = FastPricing.SimulateGbm(s0: 100, mu: 0.08, sigma: 0.2, dt: 1.0 / 252, steps: 252, paths: 1000);
```

`ManagedKernels` and `NativeKernels` are public too, for benchmarking one path against the other.

## Building

```bash
cd native/finance-native
cargo build --release      # target/release/finance_native.dll (or .so / .dylib)
cargo test                 # Rust unit tests
```

Building `Finance.RustBridge` runs `cargo build --release` automatically when `cargo` is on the
`PATH`; `-p:SkipRustBuild=true` skips it. The DLL is copied to the output of every project that
references RustBridge.

## Notes and limitations

- Rust is a clear win only where the work is heavy per element or parallel: historical VaR (~2.6×)
  and multi-threaded Monte Carlo. Simple indicators and Black-Scholes run about as fast in managed
  .NET — see [Benchmarks](../benchmarks.md).
- Other platforms (linux-arm64, osx-x64, win-arm64) have no native binary yet and use the managed
  fallback, which gives identical results.

---

Made by Gravicode Studios, led by Kang Fadhil
