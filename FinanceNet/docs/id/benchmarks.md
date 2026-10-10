# Benchmark

[← Indeks](index.md) · [English](../en/benchmarks.md)

Hasil pengukuran waktu untuk beban kerja yang sama di tiga jalur: .NET managed, kernel Rust
(`Finance.RustBridge`), dan GPU (`Finance.GPU`, ILGPU).

## Mesin dan metode

- **Mesin:** laptop 4 core 15 W (8 thread logis) yang **mengalami throttling** saat beban berkelanjutan.
  GPU-nya adalah **Intel UHD 620 terintegrasi, dipakai lewat OpenCL dengan presisi ganda**.
- **Build:** Release, .NET 10.
- **Metode:** `dotnet run -c Release --project benchmarks/Finance.Benchmarks -- --quick`. Setiap
  varian dipanaskan dengan 35 panggilan (melewati rekompilasi tiered JIT dan kompilasi kernel GPU),
  lalu varian-varian satu kasus dijalankan **bergantian dalam satu proses** dan dilaporkan **yang
  terbaik dari 7 putaran**, sehingga throttling merugikan semua varian secara setara.

Waktu dari run yang berbeda tidak bisa dibandingkan di mesin ini; bandingkan angka hanya dalam satu run.

## Hasil

| Kasus | Managed | Rust | GPU |
|---|---:|---:|---:|
| SMA(20), 1 juta harga | 3,04 ms | 2,92 ms | – |
| EMA(20), 1 juta harga | 4,03 ms | 4,05 ms | – |
| RSI(14), 1 juta harga | 11,3 ms | 8,9 ms | – |
| Rolling std(20), 1 juta harga | 46,8 ms | 39,9 ms | – |
| Black-Scholes + Greeks, 1 juta opsi | 100,6 ms | 103,1 ms | 85,0 ms |
| Monte Carlo Eropa, 4 juta jalur | 54,3 ms | 41,7 ms (1 thread: 184 ms) | 19,6 ms |
| VaR historis 99%, 1 juta return | 14,1 ms | 5,4 ms | – |
| VaR portofolio Monte Carlo, 10 aset, 200 ribu skenario | 131 ms (thread host) | – | 54,5 ms |

"–" berarti jalur tersebut belum diimplementasikan untuk kasus itu.

## Membaca angkanya

- **Rust jelas unggul hanya untuk VaR historis (~2,6×) dan Monte Carlo multi-thread (~1,3×).**
  Indikator sederhana dan Black-Scholes pada dasarnya seimbang: .NET modern dengan SIMD dan tiered PGO
  sudah mendekati kode native untuk aritmetika berurutan.
- **GPU terintegrasi unggul untuk Monte Carlo besar** (≈2,8× dibanding managed untuk 4 juta jalur,
  ≈2,4× untuk VaR portofolio) tetapi hanya sedikit untuk Black-Scholes batch, karena biaya transfer
  data mendominasi.
- Di GPU NVIDIA diskrit (CUDA), kolom GPU diperkirakan membaik secara signifikan, tetapi hal itu **belum
  diukur** di sini.

## Suite BenchmarkDotNet lengkap

```bash
dotnet run -c Release --project benchmarks/Finance.Benchmarks -- --filter *
```

Suite BenchmarkDotNet sudah bisa dikompilasi, tetapi hasilnya belum dipublikasikan — tabel di atas
berasal dari `--quick`.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
