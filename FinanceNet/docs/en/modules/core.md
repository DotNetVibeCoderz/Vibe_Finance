# Finance.Core

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/core.md) · Package: `Gravicode.Finance.Core`

The vocabulary every other module speaks: market data types, trading types, calendars, day-count
conventions, a reproducible random number generator and synthetic market generators. It has no
dependencies.

Namespaces: `Finance.Core`, `Finance.Core.Market`, `Finance.Core.Time`.

## Key concepts

- **Bar (OHLCV candle)** — open, high, low, close and volume over an interval. `Bar` is a
  `readonly record struct`, so a `Bar[]` is one contiguous block of memory.
- **Quote / microprice** — best bid and ask. The microprice weights each side by the *opposite*
  side's size, a better short-term fair value than the mid when the book is lopsided.
- **Order book (level 2)** — every resting price level. *Imbalance* compares bid and ask depth;
  *sweep price* is the average price you would pay to fill a size by walking the book.
- **Position accounting** — average-cost: buys raise the average price, sells realise PnL on the
  reduced part. A sell larger than the long position flips you short at the fill price.
- **Day count** — how two dates become a year fraction for interest: Actual/360, Actual/365F,
  Actual/Actual ISDA, 30/360 US and 30E/360.
- **Business calendar** — weekends plus holidays; date rolling conventions such as *Modified
  Following* (roll forward unless that crosses into the next month).

## API tour

### Market types

```csharp
using Finance.Core.Market;

var bar = new Bar(new DateTime(2024, 3, 1), Open: 100, High: 104, Low: 99, Close: 103, Volume: 1_200_000);
Console.WriteLine($"{bar.Typical:F2} {bar.Range} {bar.IsBullish}");

var q = new Quote(DateTime.UtcNow, Bid: 99.98, BidSize: 500, Ask: 100.02, AskSize: 100);
Console.WriteLine($"mid {q.Mid}, spread {q.Spread}, microprice {q.MicroPrice:F4}");

double perYear = BarInterval.Hour1.PeriodsPerYear();   // used to annualise statistics
var aapl = new Instrument("AAPL", AssetClass.Equity, "USD", "NASDAQ") { TickSize = 0.01 };
var cash = new Money(1_000m, Currency.USD) + new Money(250m, Currency.USD); // mixing currencies throws
```

### Order book

```csharp
var book = new OrderBook("BTCUSDT");
book.SetBids(new[] { new BookLevel(64_990, 1.2), new BookLevel(64_995, 0.4) });
book.SetAsks(new[] { new BookLevel(65_005, 0.7), new BookLevel(65_010, 2.0) });
book.Update(Side.Buy, 64_998, 0.5);           // insert/replace a level; size 0 removes it

Console.WriteLine($"{book.BestBid} / {book.BestAsk}, spread {book.Spread}");
Console.WriteLine($"imbalance {book.Imbalance(levels: 5):+0.00}");
Console.WriteLine($"cost to buy 1.5 BTC: {book.SweepPrice(Side.Buy, 1.5):F2}"); // NaN if the book is too thin
```

### Orders, fills and positions

```csharp
var order = Order.LimitBuy("AAPL", 100, 190.50);
order.ApplyFill(60, 190.45);                   // Status = PartiallyFilled
order.ApplyFill(40, 190.50);                   // Status = Filled, AverageFillPrice updated

var pos = new Position("AAPL");
pos.Apply(new Fill(order.Id, "AAPL", Side.Buy, 100, 190.47, DateTime.UtcNow, Commission: 1.0));
pos.Apply(new Fill(2, "AAPL", Side.Sell, 40, 195.00, DateTime.UtcNow));
Console.WriteLine($"qty {pos.Quantity}, avg {pos.AveragePrice:F2}, realised {pos.RealizedPnl:F2}, unrealised {pos.UnrealizedPnl(196):F2}");
```

`Order` ids are process-wide unique. `Fill.SignedQuantity` is positive for buys.

### Time

```csharp
using Finance.Core.Time;

double yf = DayCount.YearFraction(new DateOnly(2024, 1, 15), new DateOnly(2024, 7, 15), DayCountConvention.Actual360);
Tenor t = Tenor.Parse("3M");                    // also "10Y", "2W", "ON"
DateOnly end = t.AddTo(new DateOnly(2024, 1, 31));

var nyse = BusinessCalendar.NewYorkStockExchange;
bool open = nyse.IsBusinessDay(new DateOnly(2024, 7, 4));                       // false
DateOnly settle = nyse.AddBusinessDays(new DateOnly(2024, 3, 8), 2);
DateOnly rolled = BusinessCalendar.WeekendsOnly.Adjust(new DateOnly(2024, 8, 31), BusinessDayConvention.ModifiedFollowing);
```

### Random numbers and synthetic data

```csharp
using Finance.Core;

var rng = new FastRandom(seed: 42);             // xoshiro256**, identical on every platform
double z = rng.NextGaussian();
FastRandom perThread = rng.Split();             // independent stream for another thread

Bar[] bars = SyntheticMarket.GeometricBrownianBars(500, startPrice: 100, annualDrift: 0.08, annualVolatility: 0.25, seed: 7);
double[][] prices = SyntheticMarket.CorrelatedPrices(252, new[] { 0.06, 0.09 }, new[] { 0.15, 0.30 },
    new double[,] { { 1, 0.6 }, { 0.6, 1 } });
OrderBook demoBook = SyntheticMarket.OrderBook("DEMO", mid: 100, levels: 15);
```

`FinanceInfo.Version`, `FinanceInfo.CreditEnglish` and `FinanceInfo.CreditIndonesian` carry the
library identity.

## Notes and limitations

- `BusinessCalendar.NewYorkStockExchange` and `IndonesiaStockExchange` contain **fixed-rule
  holidays only**. Moveable holidays (Good Friday, Eid, Nyepi, Vesak…) must be added with
  `AddHoliday`.
- `FastRandom` is not thread-safe by design; give each thread its own instance via `Split()`.
- Synthetic bars skip weekends but not holidays.

---

Made by Gravicode Studios, led by Kang Fadhil
