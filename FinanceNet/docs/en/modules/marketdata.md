# Finance.MarketData

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/marketdata.md) · Package: `Gravicode.Finance.MarketData`

One interface for historical bars, quotes and order books across Yahoo Finance, Alpha Vantage,
Polygon.io, Binance, Coinbase, CSV files and a deterministic synthetic provider — plus real-time
streams, caching, rate limiting, an economic calendar and fundamental data.

Namespaces: `Finance.MarketData`, `.Providers`, `.Streaming`, `.Economic`, `.Fundamentals`.

## The provider interface

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

`MarketSnapshot(Symbol, Time, Last, Bid, Ask, Volume, ChangePercent)` exposes `Mid` and
`ToQuote()`. Errors surface as `MarketDataException`.

| Provider | Key | Bars | Quote | Book | Notes |
|---|---|---|---|---|---|
| `YahooFinanceProvider` | none | ✓ | ✓ | – | v8 chart API; `UseAdjustedClose` option |
| `BinanceProvider` | none | ✓ | ✓ | ✓ | defaults to the `data-api.binance.vision` market-data mirror |
| `CoinbaseProvider` | none | ✓ | ✓ | ✓ | Exchange public API |
| `AlphaVantageProvider` | API key | ✓ | ✓ | – | also economic indicators and fundamentals |
| `PolygonProvider` | API key | ✓ | ✓ | – | aggregates endpoint |
| `CsvProvider` | – | ✓ | ✓ | – | one `SYMBOL.csv` per instrument in a directory |
| `SyntheticProvider` | – | ✓ | ✓ | ✓ | deterministic per symbol; works offline |

## Examples

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
MarketSnapshot btc = await binance.GetQuoteAsync("BTC/USDT");      // symbols are normalised: BTCUSDT
OrderBook book = await binance.GetOrderBookAsync("ETHUSDT", depth: 50);

// Offline, reproducible data with the same interface
IMarketDataProvider synthetic = new SyntheticProvider();
Bar[] demo = await synthetic.GetBarsAsync("DEMO", BarInterval.Day1, new DateTime(2022, 1, 1), new DateTime(2024, 1, 1));
```

### Caching and rate limiting

```csharp
IMarketDataProvider av = new AlphaVantageProvider(apiKey: Environment.GetEnvironmentVariable("ALPHAVANTAGE_KEY")!, http);
IMarketDataProvider safe = new CachingProvider(RateLimitedProvider.AlphaVantageFree(av), diskDirectory: ".cache/marketdata");
```

`RateLimitedProvider.AlphaVantageFree` enforces the free tier (5 calls per minute) with a token
bucket (`RateLimiter`). `CachingProvider` caches in memory and, optionally, on disk.

### Utilities

```csharp
Bar[] weekly = BarAggregator.Resample(dailyBars, TimeSpan.FromDays(7));
Bar[] minuteBars = BarAggregator.FromTicks(ticks, TimeSpan.FromMinutes(1));
string csv = CsvProvider.Format(bars);
Bar[] parsed = CsvProvider.Parse(csv);
```

## Streaming

```csharp
using Finance.MarketData.Streaming;

IMarketDataStream stream = new BinanceStream();          // wss://data-stream.binance.vision/ws, auto-reconnect
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));

await foreach (Tick t in stream.TradesAsync("BTCUSDT", cts.Token))
    Console.WriteLine($"{t.Time:HH:mm:ss.fff} {t.Price} x {t.Size}");

await foreach (BarUpdate u in stream.BarsAsync("ETHUSDT", BarInterval.Minute1, cts.Token))
    if (u.IsFinal) Console.WriteLine($"closed {u.Bar.Time:t} {u.Bar.Close}");

// Synthetic random-walk stream for demos and tests
var sim = new SyntheticStream(startPrice: 100, annualVolatility: 0.4) { TickInterval = TimeSpan.FromMilliseconds(100) };
await foreach (OrderBook ob in sim.OrderBooksAsync("DEMO", depth: 10, cts.Token)) { /* ... */ }
```

## Economic calendar and fundamentals

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

## More providers (v0.3)

```csharp
var alpaca = new AlpacaProvider(keyId, secret);                 // US stocks, IEX feed (free) or SIP
var oanda  = new OandaProvider(token, accountId);               // FX/CFD mid candles, "EUR/USD" accepted
var bybit  = new BybitProvider(category: "spot");               // public, no key; also IOrderBookProvider
var okx    = new OkxProvider();                                 // public, no key; "BTCUSDT" → "BTC-USDT"
var idx    = new IdxProvider();                                 // IDX via Yahoo: "BBCA" → "BBCA.JK", "IHSG" → "^JKSE"

int tick = IdxRules.TickSize(4_530);                            // Rp10
var (arb, ara) = IdxRules.AutoRejection(4_530);                 // auto-rejection band, on the tick grid

var fred = new FredProvider(fredApiKey);
EconomicSeries curve = await fred.GetSeriesAsync("T10Y2Y");      // any of FRED's series
var releases = await fred.GetEventsAsync(DateTime.UtcNow, DateTime.UtcNow.AddDays(14));   // release calendar

var sec = new SecEdgarProvider("Your Name you@example.com");    // SEC requires a contact User-Agent
IncomeStatement[] apple = await sec.GetIncomeStatementsAsync("AAPL");
IReadOnlyList<XbrlFact> rd = await sec.GetFactsAsync("AAPL", "us-gaap", "ResearchAndDevelopmentExpense");
```

EDGAR statements merge synonym concepts per period (filers switch from `Revenues` to
`RevenueFromContractWithCustomerExcludingAssessedTax`, ...), the latest filing winning, so restatements are
picked up. EDGAR was verified live on Apple's filings (revenue, margins, assets = liabilities + equity).

### Filing documents (v0.4)

```csharp
IReadOnlyList<EdgarFiling> filings = await sec.GetFilingsAsync("AAPL", form: "10-Q", count: 4);   // Form, AccessionNumber, FilingDate, ReportDate, Url
string html = await sec.GetFilingDocumentAsync(filings[0]);      // primary document, ready for Finance.LLM.Rag
```

See [Finance.LLM](llm.md) for question answering over the filing text.

## Notes and limitations

- IDX has no free official API; `IdxProvider` relies on a vendor carrying ".JK" listings. `IdxRules` encodes the
  2016 tick table and lot size; auto-rejection percentages have changed several times, so they are parameters.
- Bybit and OKX could not be reached from the development network; their parsers follow the documented formats.
  Alpaca, OANDA and FRED need keys and were not exercised live.

- From some networks `api.binance.com` fails TLS validation; the providers therefore default to
  Binance's official **market-data mirrors** `https://data-api.binance.vision` (REST) and
  `wss://data-stream.binance.vision/ws` (WebSocket). Pass `baseUrl` to use another endpoint.
- Yahoo Finance and Binance REST parsing is verified live (`FINANCENET_LIVE=1`). Coinbase parsing
  follows the documented payload format but could not be verified live from the development
  machine; the Binance WebSocket parser is unit-tested on recorded messages.
- `SampleEconomicCalendar` is a static illustrative calendar, not a live feed.
- Yahoo's API is unofficial and may change or throttle without notice.

---

Made by Gravicode Studios, led by Kang Fadhil
