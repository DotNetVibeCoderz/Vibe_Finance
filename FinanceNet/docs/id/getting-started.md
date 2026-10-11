# Memulai

[← Indeks](index.md) · [English](../en/getting-started.md)

## Kebutuhan

- **.NET 10 SDK** (10.0.100 atau lebih baru; `global.json` otomatis memakai feature band terbaru).
- **Rust** (opsional, toolchain stable dengan `cargo`) untuk membangun akselerator native. Tanpa Rust
  semuanya tetap berjalan: `Finance.RustBridge` beralih ke kode managed dengan hasil yang identik.
- **Windows, Linux, atau macOS.** Gallery memakai Avalonia dan berjalan di ketiganya.

## Instalasi dari NuGet

```bash
dotnet new console -n MyQuant && cd MyQuant
dotnet add package Gravicode.Finance.Pricing
dotnet add package Gravicode.Finance.Risk
```

## Program pertama Anda

Program berikut menghitung harga opsi, membandingkannya dengan binomial tree, lalu mengukur
Value-at-Risk dari riwayat harga sintetis. Tidak perlu akses jaringan.

```csharp
using Finance.Core.Market;
using Finance.Pricing.Options;
using Finance.Risk;

// 1. Harga dan Greeks Black-Scholes-Merton untuk call at-the-money 6 bulan.
var g = BlackScholes.Greeks(OptionType.Call, spot: 100, strike: 100, time: 0.5,
                            rate: 0.04, dividendYield: 0.01, volatility: 0.25);
Console.WriteLine($"Harga {g.Price:F4}  Delta {g.Delta:F4}  Vega/poin {g.VegaPerPoint:F4}  Theta/hari {g.ThetaPerDay:F4}");

// 2. Kontrak yang sama di CRR tree 500 langkah, gaya Eropa dan Amerika.
double euro = LatticePricer.Binomial(OptionType.Call, ExerciseStyle.European, 100, 100, 0.5, 0.04, 0.01, 0.25);
double amer = LatticePricer.Binomial(OptionType.Put, ExerciseStyle.American, 100, 100, 0.5, 0.04, 0.01, 0.25);
Console.WriteLine($"Tree (call Eropa) {euro:F4}, put Amerika {amer:F4}");

// 3. VaR satu hari 99% dan Expected Shortfall saham sintetis.
Bar[] bars = SyntheticMarket.GeometricBrownianBars(1000, startPrice: 100, annualDrift: 0.08, annualVolatility: 0.30);
var returns = new double[bars.Length - 1];
for (int i = 1; i < bars.Length; i++) returns[i - 1] = bars[i].Close / bars[i - 1].Close - 1;

VarEstimate hist = ValueAtRisk.Historical(returns, 0.99);
VarEstimate cf = ValueAtRisk.CornishFisher(returns, 0.99);
Console.WriteLine($"VaR historis {hist.ValueAtRisk:P2}, ES {hist.ExpectedShortfall:P2}; VaR Cornish-Fisher {cf.ValueAtRisk:P2}");
```

VaR dan ES dilaporkan sebagai **fraksi kerugian positif**: `0.025` berarti "rugi 2,5% dari nilai
portofolio". Gunakan `ValueAtRiskAmount(nilaiPortofolio)` untuk nilai dalam mata uang.

## Build dari source

```bash
git clone <repository> FinanceNet && cd FinanceNet
dotnet build FinanceNet.slnx
```

File solusinya adalah `FinanceNet.slnx` (format solusi XML di .NET 10).

### Akselerator Rust

Saat dibangun, `src/Finance.RustBridge` menjalankan `cargo build --release` pada
`native/finance-native` jika `cargo` ada di `PATH`, lalu menyalin `finance_native.dll` (`.so` /
`.dylib` di Linux/macOS) ke folder output. Untuk membangun crate-nya sendiri:

```bash
cd native/finance-native
cargo build --release
cargo test
```

Untuk melewati langkah Rust (misalnya di mesin tanpa Rust), tambahkan:

```bash
dotnet build FinanceNet.slnx -p:SkipRustBuild=true
```

Dengan properti itu, binary yang sudah pernah dibangun di `native/finance-native/target/release`
tetap disalin jika ada. Jika `cargo` tidak ditemukan, build hanya menampilkan pesan dan tidak gagal.
Saat runtime, `RustAccelerator.IsAvailable` memberi tahu apakah library native berhasil dimuat.
Setel `FINANCE_NATIVE_DISABLE=1` untuk memaksa jalur managed, atau `FINANCE_NATIVE_PATH` untuk
menunjuk ke file library tertentu.

## Menjalankan pengujian

```bash
dotnet test FinanceNet.slnx
```

Pengujian dipisah per lapisan:

| Proyek | Cakupan |
|---|---|
| `tests/Finance.Foundation.Tests` | Core, Math |
| `tests/Finance.Analytics.Tests` | DataFrame, TimeSeries, Visualization |
| `tests/Finance.Quant.Tests` | Pricing, Risk |
| `tests/Finance.Portfolio.Tests` | Portfolio, Backtesting |
| `tests/Finance.Data.Tests` | MarketData, AlternativeData, Execution |
| `tests/Finance.AI.Tests` | ML, LLM, NewsAnalytics |
| `tests/Finance.Native.Tests` | GPU, RustBridge |

Menjalankan satu proyek, kelas, atau tes dengan filter:

```bash
dotnet test tests/Finance.Quant.Tests
dotnet test tests/Finance.Quant.Tests --filter "FullyQualifiedName~BlackScholes"
```

### Tes live

Unit test tidak pernah menyentuh jaringan. Beberapa tes *live* dilewati kecuali Anda mengaktifkannya:

| Variabel | Mengaktifkan |
|---|---|
| `FINANCENET_LIVE=1` | Permintaan nyata ke Yahoo Finance dan Binance (`Finance.Data.Tests`) |
| `FINANCENET_LLM_KEYFILE=<path>` | Panggilan chat dan tool-calling sungguhan (`Finance.AI.Tests`) |
| `FINANCENET_HF_TOKENFILE=<path>` | Inferensi FinBERT di Hugging Face (`Finance.AI.Tests`) |
| `FINANCENET_FINBERT_LOCAL=1` | FinBERT lokal lewat HF.Net; mengunduh ProsusAI/finbert (~440 MB) sekali (`Finance.AI.Tests`) |

Inferensi hosted Hugging Face mengembalikan HTTP 402 jika akun tidak punya kredit inferensi; tes
live FinBERT akan melaporkannya lalu dilewati.

## Mengonfigurasi LLM

`LlmSettings.FromEnvironment()` (dipakai Gallery dan pengujian) mencari secara berurutan:

1. `FINANCENET_LLM_KEYFILE` — sebuah key file (lihat di bawah).
2. `FINANCENET_LLM_PROVIDER` beserta `FINANCENET_LLM_API_KEY`, `FINANCENET_LLM_ENDPOINT`,
   `FINANCENET_LLM_MODEL`. Provider-nya salah satu dari `OpenAI`, `AzureOpenAI`, `Anthropic`,
   `Ollama`, `HuggingFace`, `DeepSeek`, `OpenAICompatible`.
3. `AZURE_OPENAI_API_KEY` + `AZURE_OPENAI_ENDPOINT` (+ `AZURE_OPENAI_DEPLOYMENT`).
4. `OPENAI_API_KEY` (+ `OPENAI_MODEL`).
5. `ANTHROPIC_API_KEY` (+ `ANTHROPIC_MODEL`).

Key file adalah teks biasa yang terdiri dari beberapa bagian: satu baris judul provider diikuti
baris `kunci: nilai`.

```text
Azure OpenAI
apikey: <kunci Anda>
endpoint: https://<resource>.openai.azure.com/
model: <nama-deployment>
```

`LlmSettings.ToString()` menyamarkan kunci, jadi aman untuk dicatat di log. Simpan key file **di luar**
repository.

## Menjalankan aplikasi

```bash
# Gallery desktop Avalonia (12 halaman)
dotnet run --project samples/Finance.Gallery

# Render setiap halaman Gallery ke PNG secara headless (1440×900), lalu keluar
dotnet run --project samples/Finance.Gallery -- --screenshot docs/screenshots

# Tur library di konsol
dotnet run --project samples/Finance.Quickstart

# Benchmark: tabel stopwatch cepat, atau suite BenchmarkDotNet lengkap
dotnet run -c Release --project benchmarks/Finance.Benchmarks -- --quick
dotnet run -c Release --project benchmarks/Finance.Benchmarks -- --filter *
```

## Notebook

Folder `notebooks/` berisi 15 notebook `.dib` untuk **Polyglot Notebooks** (.NET Interactive) di
VS Code. Notebook memulihkan paket `Gravicode.Finance.*` dari `artifacts/packages` (buat dengan
`dotnet pack -c Release`) atau dari nuget.org. Lihat [Notebook](notebooks.md).

## Langkah berikutnya

- Jelajahi [panduan per modul](index.md#panduan-per-modul).
- Buka [Gallery](gallery.md) untuk melihat setiap modul bekerja secara interaktif.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
