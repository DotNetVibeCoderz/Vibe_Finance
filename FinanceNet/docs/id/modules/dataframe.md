# Finance.DataFrame

[← Indeks](../index.md) · [English](../../en/modules/dataframe.md) · Paket: `Gravicode.Finance.DataFrame`

Wadah kolumnar untuk data pasar. `OhlcvFrame` menyimpan candle satu instrumen sebagai enam array
paralel (ramah cache untuk indikator); `PricePanel` menyimpan banyak instrumen yang diselaraskan pada
tanggal yang sama (bentuk input untuk optimasi portofolio). Keduanya bisa dikonversi dari dan ke data
frame **GraviFrame**.

Namespace: `Finance.DataFrame`.

## Konsep utama

- **Penyimpanan kolumnar** — indikator membaca satu kolom setiap kali; menyimpan `Close` sebagai
  `double[]` yang bersebelahan berarti akses `Span<double>` langsung tanpa melompati struct bar.
- **Resampling** — mengubah bar harian menjadi mingguan/bulanan: open pertama, high tertinggi, low
  terendah, close terakhir, volume dijumlahkan.
- **Penyelarasan (inner join)** — aset diperdagangkan pada hari yang berbeda (libur, tanggal listing).
  Panel hanya menyimpan tanggal yang dimiliki semua aset, sehingga setiap baris adalah potongan lintas
  aset yang konsisten.

## Tur API

```csharp
using Finance.Core.Market;
using Finance.DataFrame;

// Dari bar, CSV, atau data sintetis
OhlcvFrame frame = OhlcvFrame.FromBars(SyntheticMarket.GeometricBrownianBars(750), symbol: "DEMO");
OhlcvFrame csv = OhlcvFrame.ReadCsv("AAPL.csv", symbol: "AAPL");   // header gaya Yahoo, culture invariant
OhlcvFrame syn = OhlcvFrame.Synthetic(500, symbol: "SYN", seed: 1);

Console.WriteLine($"{frame.Symbol}: {frame.Count} bar, close terakhir {frame.Close[^1]:F2}");
Bar first = frame[0];
Bar[] bars = frame.ToBars();

// Pemotongan dan resampling
OhlcvFrame year = frame.Slice(new DateTime(2021, 1, 1), new DateTime(2021, 12, 31));   // inklusif
OhlcvFrame weekly = frame.Resample(ResamplePeriod.Week);
OhlcvFrame recent = frame.Tail(60);
frame.WriteCsv("demo.csv");
```

### Panel multi-aset

```csharp
var a = OhlcvFrame.Synthetic(500, "AAA", seed: 1);
var b = OhlcvFrame.Synthetic(480, "BBB", seed: 2);
PricePanel panel = PricePanel.FromFrames(new[] { a, b });      // inner join pada timestamp

double[] aaa = panel["AAA"];
var prices = panel.ToMatrix();                                 // baris = tanggal, kolom = simbol
var rets = panel.ToReturnsMatrix(logReturns: true);            // input untuk Finance.Portfolio / Finance.Risk
PricePanel subset = panel.Select("AAA");
```

`PricePanel.Align(IReadOnlyList<(string Symbol, DateTime[] Time, double[] Values)>)` menyelaraskan deret
mentah secara langsung.

### Interop GraviFrame

```csharp
var gf = frame.ToGraviFrame();                  // Gravicode.Science.GraviFrame.DataFrame
OhlcvFrame round = gf.ToOhlcvFrame("DEMO");
var panelFrame = panel.ToGraviFrame();
```

## Catatan dan batasan

- `PricePanel.Align` hanya melakukan **inner join dengan timestamp yang persis sama**; belum ada as-of
  join di tingkat panel (gunakan `SeriesAlignment.AsOf` di `Finance.TimeSeries` untuk itu).
- Parser CSV melewati baris yang berisi `null` (seperti ekspor Yahoo untuk hari yang kosong).

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
