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

The BenchmarkDotNet suites compile; their results have not been published yet — the table above
comes from `--quick`.

---

Made by Gravicode Studios, led by Kang Fadhil
