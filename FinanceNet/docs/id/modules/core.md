# Finance.Core

[← Indeks](../index.md) · [English](../../en/modules/core.md) · Paket: `Gravicode.Finance.Core`

"Kosakata" yang dipakai semua modul lain: tipe data pasar, tipe trading, kalender, konvensi day
count, generator bilangan acak yang dapat direproduksi, dan generator pasar sintetis. Modul ini
tidak punya dependensi.

Namespace: `Finance.Core`, `Finance.Core.Market`, `Finance.Core.Time`.

## Konsep utama

- **Bar (candle OHLCV)** — harga pembukaan, tertinggi, terendah, penutupan, dan volume dalam satu
  interval. `Bar` adalah `readonly record struct`, sehingga `Bar[]` tersimpan dalam satu blok memori
  yang bersebelahan.
- **Kuotasi / microprice** — bid dan ask terbaik. Microprice membobot setiap sisi dengan ukuran sisi
  *lawannya*; ini estimasi nilai wajar jangka pendek yang lebih baik daripada mid saat order book
  timpang.
- **Order book (level 2)** — semua level harga yang sedang antre. *Imbalance* membandingkan kedalaman
  bid dan ask; *sweep price* adalah harga rata-rata yang Anda bayar untuk mengisi sejumlah unit dengan
  "menyapu" book.
- **Akuntansi posisi** — metode biaya rata-rata: pembelian menaikkan harga rata-rata, penjualan
  merealisasikan PnL atas bagian yang dikurangi. Penjualan melebihi posisi long akan membalik posisi
  menjadi short di harga fill.
- **Day count** — cara mengubah dua tanggal menjadi fraksi tahun untuk perhitungan bunga:
  Actual/360, Actual/365F, Actual/Actual ISDA, 30/360 US, dan 30E/360.
- **Kalender bursa** — akhir pekan ditambah hari libur; konvensi penggeseran tanggal seperti
  *Modified Following* (geser maju kecuali melewati batas bulan).

## Tur API

### Tipe pasar

```csharp
using Finance.Core.Market;

var bar = new Bar(new DateTime(2024, 3, 1), Open: 100, High: 104, Low: 99, Close: 103, Volume: 1_200_000);
Console.WriteLine($"{bar.Typical:F2} {bar.Range} {bar.IsBullish}");

var q = new Quote(DateTime.UtcNow, Bid: 99.98, BidSize: 500, Ask: 100.02, AskSize: 100);
Console.WriteLine($"mid {q.Mid}, spread {q.Spread}, microprice {q.MicroPrice:F4}");

double perYear = BarInterval.Hour1.PeriodsPerYear();   // dipakai untuk menganualisasi statistik
var aapl = new Instrument("AAPL", AssetClass.Equity, "USD", "NASDAQ") { TickSize = 0.01 };
var cash = new Money(1_000m, Currency.USD) + new Money(250m, Currency.USD); // mencampur mata uang akan melempar exception
```

### Order book

```csharp
var book = new OrderBook("BTCUSDT");
book.SetBids(new[] { new BookLevel(64_990, 1.2), new BookLevel(64_995, 0.4) });
book.SetAsks(new[] { new BookLevel(65_005, 0.7), new BookLevel(65_010, 2.0) });
book.Update(Side.Buy, 64_998, 0.5);           // sisipkan/ganti level; ukuran 0 menghapusnya

Console.WriteLine($"{book.BestBid} / {book.BestAsk}, spread {book.Spread}");
Console.WriteLine($"imbalance {book.Imbalance(levels: 5):+0.00}");
Console.WriteLine($"biaya membeli 1,5 BTC: {book.SweepPrice(Side.Buy, 1.5):F2}"); // NaN jika book terlalu tipis
```

### Order, fill, dan posisi

```csharp
var order = Order.LimitBuy("AAPL", 100, 190.50);
order.ApplyFill(60, 190.45);                   // Status = PartiallyFilled
order.ApplyFill(40, 190.50);                   // Status = Filled, AverageFillPrice diperbarui

var pos = new Position("AAPL");
pos.Apply(new Fill(order.Id, "AAPL", Side.Buy, 100, 190.47, DateTime.UtcNow, Commission: 1.0));
pos.Apply(new Fill(2, "AAPL", Side.Sell, 40, 195.00, DateTime.UtcNow));
Console.WriteLine($"qty {pos.Quantity}, avg {pos.AveragePrice:F2}, realisasi {pos.RealizedPnl:F2}, belum terealisasi {pos.UnrealizedPnl(196):F2}");
```

Id `Order` unik di seluruh proses. `Fill.SignedQuantity` bernilai positif untuk pembelian.

### Waktu

```csharp
using Finance.Core.Time;

double yf = DayCount.YearFraction(new DateOnly(2024, 1, 15), new DateOnly(2024, 7, 15), DayCountConvention.Actual360);
Tenor t = Tenor.Parse("3M");                    // juga "10Y", "2W", "ON"
DateOnly end = t.AddTo(new DateOnly(2024, 1, 31));

var nyse = BusinessCalendar.NewYorkStockExchange;
bool open = nyse.IsBusinessDay(new DateOnly(2024, 7, 4));                       // false
DateOnly settle = nyse.AddBusinessDays(new DateOnly(2024, 3, 8), 2);
DateOnly rolled = BusinessCalendar.WeekendsOnly.Adjust(new DateOnly(2024, 8, 31), BusinessDayConvention.ModifiedFollowing);
```

### Bilangan acak dan data sintetis

```csharp
using Finance.Core;

var rng = new FastRandom(seed: 42);             // xoshiro256**, identik di semua platform
double z = rng.NextGaussian();
FastRandom perThread = rng.Split();             // aliran independen untuk thread lain

Bar[] bars = SyntheticMarket.GeometricBrownianBars(500, startPrice: 100, annualDrift: 0.08, annualVolatility: 0.25, seed: 7);
double[][] prices = SyntheticMarket.CorrelatedPrices(252, new[] { 0.06, 0.09 }, new[] { 0.15, 0.30 },
    new double[,] { { 1, 0.6 }, { 0.6, 1 } });
OrderBook demoBook = SyntheticMarket.OrderBook("DEMO", mid: 100, levels: 15);
```

`FinanceInfo.Version`, `FinanceInfo.CreditEnglish`, dan `FinanceInfo.CreditIndonesian` menyimpan
identitas library.

## Catatan dan batasan

- `BusinessCalendar.NewYorkStockExchange` dan `IndonesiaStockExchange` hanya memuat **hari libur
  bertanggal tetap**. Hari libur yang berpindah-pindah (Jumat Agung, Idulfitri, Nyepi, Waisak, dan
  lain-lain) harus ditambahkan dengan `AddHoliday`.
- `FastRandom` memang tidak thread-safe; berikan setiap thread instansnya sendiri lewat `Split()`.
- Bar sintetis melewati akhir pekan tetapi tidak melewati hari libur.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
