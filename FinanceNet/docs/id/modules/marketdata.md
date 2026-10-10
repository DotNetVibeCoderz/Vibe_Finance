# Finance.MarketData

[← Indeks](../index.md) · [English](../../en/modules/marketdata.md) · Paket: `Gravicode.Finance.MarketData`

Satu antarmuka untuk bar historis, kuotasi, dan order book dari Yahoo Finance, Alpha Vantage,
Polygon.io, Binance, Coinbase, file CSV, dan provider sintetis yang deterministik — ditambah stream
real-time, caching, pembatasan laju (rate limiting), kalender ekonomi, dan data fundamental.

Namespace: `Finance.MarketData`, `.Providers`, `.Streaming`, `.Economic`, `.Fundamentals`.

## Antarmuka provider

```csharp
public interface IMarketDataProvider
{
    Task<Bar[]> GetBarsAsync(string symbol, BarInterval interval, DateTime start, DateTime end, CancellationToken ct = default);
    Task<MarketSnapshot> GetQuoteAsync(string symbol, CancellationToken ct = default);
}

public interface IOrderBookProvider
{
    Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken ct = default);
}
```

`MarketSnapshot(Symbol, Time, Last, Bid, Ask, Volume, ChangePercent)` menyediakan `Mid` dan
`ToQuote()`. Galat dilaporkan sebagai `MarketDataException`.

| Provider | Kunci | Bar | Kuotasi | Book | Catatan |
|---|---|---|---|---|---|
| `YahooFinanceProvider` | tidak perlu | ✓ | ✓ | – | API chart v8; opsi `UseAdjustedClose` |
| `BinanceProvider` | tidak perlu | ✓ | ✓ | ✓ | bawaan memakai mirror data pasar `data-api.binance.vision` |
| `CoinbaseProvider` | tidak perlu | ✓ | ✓ | ✓ | API publik Exchange |
| `AlphaVantageProvider` | API key | ✓ | ✓ | – | juga indikator ekonomi dan fundamental |
| `PolygonProvider` | API key | ✓ | ✓ | – | endpoint aggregates |
| `CsvProvider` | – | ✓ | ✓ | – | satu `SIMBOL.csv` per instrumen dalam satu folder |
| `SyntheticProvider` | – | ✓ | ✓ | ✓ | deterministik per simbol; bisa offline |

## Contoh

```csharp
using Finance.Core.Market;
using Finance.MarketData;
using Finance.MarketData.Providers;

var http = new HttpClient();
IMarketDataProvider yahoo = new YahooFinanceProvider(http);
Bar[] spy = await yahoo.GetBarsAsync("SPY", BarInterval.Day1, new DateTime(2020, 1, 1), DateTime.UtcNow);
Bar[] lastWeek = await yahoo.GetRecentBarsAsync("AAPL", BarInterval.Hour1, TimeSpan.FromDays(7));
double[] closes = spy.Closes();

var binance = new BinanceProvider(http);
MarketSnapshot btc = await binance.GetQuoteAsync("BTC/USDT");      // simbol dinormalisasi: BTCUSDT
OrderBook book = await binance.GetOrderBookAsync("ETHUSDT", depth: 50);

// Data offline yang dapat direproduksi, dengan antarmuka yang sama
IMarketDataProvider synthetic = new SyntheticProvider();
Bar[] demo = await synthetic.GetBarsAsync("DEMO", BarInterval.Day1, new DateTime(2022, 1, 1), new DateTime(2024, 1, 1));
```

### Caching dan rate limiting

```csharp
IMarketDataProvider av = new AlphaVantageProvider(apiKey: Environment.GetEnvironmentVariable("ALPHAVANTAGE_KEY")!, http);
IMarketDataProvider safe = new CachingProvider(RateLimitedProvider.AlphaVantageFree(av), diskDirectory: ".cache/marketdata");
```

`RateLimitedProvider.AlphaVantageFree` menerapkan batas paket gratis (5 panggilan per menit) dengan
token bucket (`RateLimiter`). `CachingProvider` menyimpan cache di memori dan, jika diminta, di disk.

### Utilitas

```csharp
Bar[] weekly = BarAggregator.Resample(dailyBars, TimeSpan.FromDays(7));
Bar[] minuteBars = BarAggregator.FromTicks(ticks, TimeSpan.FromMinutes(1));
string csv = CsvProvider.Format(bars);
Bar[] parsed = CsvProvider.Parse(csv);
```

## Streaming

```csharp
using Finance.MarketData.Streaming;

IMarketDataStream stream = new BinanceStream();          // wss://data-stream.binance.vision/ws, reconnect otomatis
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));

await foreach (Tick t in stream.TradesAsync("BTCUSDT", cts.Token))
    Console.WriteLine($"{t.Time:HH:mm:ss.fff} {t.Price} x {t.Size}");

await foreach (BarUpdate u in stream.BarsAsync("ETHUSDT", BarInterval.Minute1, cts.Token))
    if (u.IsFinal) Console.WriteLine($"tutup {u.Bar.Time:t} {u.Bar.Close}");

// Stream random-walk sintetis untuk demo dan pengujian
var sim = new SyntheticStream(startPrice: 100, annualVolatility: 0.4) { TickInterval = TimeSpan.FromMilliseconds(100) };
await foreach (OrderBook ob in sim.OrderBooksAsync("DEMO", depth: 10, cts.Token)) { /* ... */ }
```

## Kalender ekonomi dan fundamental

```csharp
using Finance.MarketData.Economic;
using Finance.MarketData.Fundamentals;

var calendar = new SampleEconomicCalendar();
var events = await calendar.GetEventsAsync(DateTime.Today, DateTime.Today.AddDays(30));

var avProvider = new AlphaVantageProvider(apiKey, http);
EconomicSeries cpi = await avProvider.GetIndicatorAsync(EconomicIndicator.Cpi);
double[] inflation = cpi.PercentChanges();

CompanyOverview overview = await avProvider.GetOverviewAsync("MSFT");
IncomeStatement[] income = await avProvider.GetIncomeStatementsAsync("MSFT");
BalanceSheet[] balance = await avProvider.GetBalanceSheetsAsync("MSFT");
FundamentalRatios ratios = FundamentalRatios.Compute(income[0], balance[0], previousIncome: income[1]);
```

## Catatan dan batasan

- Dari sebagian jaringan, `api.binance.com` gagal validasi TLS; karena itu provider secara bawaan memakai
  **mirror data pasar** resmi Binance, `https://data-api.binance.vision` (REST) dan
  `wss://data-stream.binance.vision/ws` (WebSocket). Isi `baseUrl` untuk memakai endpoint lain.
- Parsing REST Yahoo Finance dan Binance sudah diverifikasi secara live (`FINANCENET_LIVE=1`). Parsing
  Coinbase mengikuti format payload yang terdokumentasi, tetapi belum bisa diverifikasi live dari mesin
  pengembangan; parser WebSocket Binance diuji dengan pesan rekaman.
- `SampleEconomicCalendar` adalah kalender statis untuk ilustrasi, bukan feed live.
- API Yahoo tidak resmi dan bisa berubah atau dibatasi tanpa pemberitahuan.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
