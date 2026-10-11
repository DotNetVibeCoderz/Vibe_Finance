# Benchmarks

[← Index](index.md) · [Bahasa Indonesia](../id/benchmarks.md)

Measured timings of the same workloads on three paths: managed .NET, the Rust kernels
(`Finance.RustBridge`) and the GPU (`Finance.GPU`, ILGPU).

## Machine and method

- **Machine:** a 4-core, 15 W laptop (8 logical threads) that **throttles** under sustained load.
  The GPU is an integrated **Intel UHD 620, used through OpenCL with double precision**.
- **Build:** Release, .NET 10.
- **Method:** `dotnet run -c Release --project benchmarks/Finance.Benchmarks -- --quick`. Every
  variant is warmed up with 35 calls (past tiered-JIT recompilation and GPU kernel compilation),
  then the variants of a case run **alternately in one process** and the **best of 7 rounds** is
  reported, so throttling penalises all variants alike.

Timings from separate runs are not comparable on this machine; only compare numbers within one run.

## Results

| Case | Managed | Rust | GPU |
|---|---:|---:|---:|
| SMA(20), 1M prices | 3.04 ms | 2.92 ms | – |
| EMA(20), 1M prices | 4.03 ms | 4.05 ms | – |
| RSI(14), 1M prices | 11.3 ms | 8.9 ms | – |
| Rolling std(20), 1M prices | 46.8 ms | 39.9 ms | – |
| Black-Scholes + Greeks, 1M options | 100.6 ms | 103.1 ms | 85.0 ms |
| Monte Carlo European, 4M paths | 54.3 ms | 41.7 ms (1 thread: 184 ms) | 19.6 ms |
| Historical VaR 99%, 1M returns | 14.1 ms | 5.4 ms | – |
| Monte Carlo portfolio VaR, 10 assets, 200k scenarios | 131 ms (host threads) | – | 54.5 ms |

"–" means that path is not implemented for the case.

## Reading the numbers

- **Rust clearly wins only for historical VaR (~2.6×) and multi-threaded Monte Carlo (~1.3×).**
  Simple indicators and Black-Scholes are essentially even: modern .NET with SIMD and tiered PGO is
  already close to native code for streaming arithmetic.
- **The integrated GPU wins on large Monte Carlo** (≈2.8× over managed for 4M paths, ≈2.4× for
  portfolio VaR) but only slightly on batch Black-Scholes, where transfer cost dominates.
- On a discrete NVIDIA GPU (CUDA) the GPU columns would be expected to improve substantially, but
  that has **not been measured** here.

## Full BenchmarkDotNet suites

```bash
dotnet run -c Release --project benchmarks/Finance.Benchmarks -- --filter *
```

Measured 2026-10-11 with BenchmarkDotNet 0.15.8 (default job) on the same laptop: Intel Core
i7-8650U (4 cores / 8 threads, 15 W, Kaby Lake R), Windows 11, .NET 10.0.12 x64 RyuJIT, integrated
Intel UHD 620 for the GPU rows. Each suite ran in one process; the laptop throttles, so the error
columns are wide and only ratios within one table are meaningful.

**Indicators** (managed vs Rust, one array of N prices, zero allocations on both sides):

| Method | N = 100,000 | N = 1,000,000 |
|---|---:|---:|
| SMA(20) managed | 294 µs | 3.42 ms |
| SMA(20) Rust | 269 µs | 3.32 ms |
| EMA(20) managed | 367 µs | 4.79 ms |
| EMA(20) Rust | 383 µs | 4.41 ms |
| RSI(14) managed | 988 µs | 12.05 ms |
| RSI(14) Rust | 918 µs | 11.39 ms |

**Pricing** (1,000,000 Black-Scholes prices + deltas; Monte Carlo European call, 2,000,000 paths):

| Method | Mean | Allocated |
|---|---:|---:|
| Black-Scholes batch, managed | 121.4 ms | 0 |
| Black-Scholes batch, Rust | 115.2 ms | 0 |
| Black-Scholes batch, GPU | 85.9 ms | 152 MB (result arrays) |
| Monte Carlo, managed (parallel) | 28.9 ms | 8 KB |
| Monte Carlo, Rust (parallel) | 31.6 ms | 0 |
| Monte Carlo, GPU | 12.9 ms | 1 MB |

**Risk** (historical VaR/ES at 99% over 1,000,000 returns):

| Method | Mean | Allocated |
|---|---:|---:|
| Historical VaR, managed | 15.05 ms | 8 MB (sort buffer) |
| Historical VaR, Rust | 6.23 ms | 0 |

What the full suites confirm: Rust is **2.4× faster for historical VaR** (selection instead of a
full sort, no allocation); simple indicators and Black-Scholes are within noise of the managed
code, which is already vectorised by RyuJIT. The integrated GPU is **2.2× faster for Monte Carlo**
and 1.4× for batch Black-Scholes (where copying the inputs over dominates). In this run, unlike
the `--quick` table, managed and Rust Monte Carlo are tied: both use the same 64 fixed RNG streams
and the difference is within the throttling noise.

---

Made by Gravicode Studios, led by Kang Fadhil
