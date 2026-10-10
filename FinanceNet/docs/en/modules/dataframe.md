# Finance.DataFrame

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/dataframe.md) · Package: `Gravicode.Finance.DataFrame`

Columnar containers for market data. `OhlcvFrame` holds one instrument's candles as six parallel
arrays (cache-friendly for indicators); `PricePanel` holds many instruments aligned on common dates
(the input shape for portfolio optimisation). Both convert to and from **GraviFrame** data frames.

Namespace: `Finance.DataFrame`.

## Key concepts

- **Columnar storage** — indicators read one column at a time; storing `Close` as a contiguous
  `double[]` means no struct-of-bars stride and direct `Span<double>` access.
- **Resampling** — turning daily bars into weekly/monthly ones: first open, max high, min low,
  last close, summed volume.
- **Alignment (inner join)** — assets trade on different days (holidays, listings). A panel keeps
  only the dates every asset has, so each row is a consistent cross-section.

## API tour

```csharp
using Finance.Core.Market;
using Finance.DataFrame;

// From bars, CSV or synthetic data
OhlcvFrame frame = OhlcvFrame.FromBars(SyntheticMarket.GeometricBrownianBars(750), symbol: "DEMO");
OhlcvFrame csv = OhlcvFrame.ReadCsv("AAPL.csv", symbol: "AAPL");   // Yahoo-style headers, invariant culture
OhlcvFrame syn = OhlcvFrame.Synthetic(500, symbol: "SYN", seed: 1);

Console.WriteLine($"{frame.Symbol}: {frame.Count} bars, last close {frame.Close[^1]:F2}");
Bar first = frame[0];
Bar[] bars = frame.ToBars();

// Slicing and resampling
OhlcvFrame year = frame.Slice(new DateTime(2021, 1, 1), new DateTime(2021, 12, 31));   // inclusive
OhlcvFrame weekly = frame.Resample(ResamplePeriod.Week);
OhlcvFrame recent = frame.Tail(60);
frame.WriteCsv("demo.csv");
```

### Multi-asset panels

```csharp
var a = OhlcvFrame.Synthetic(500, "AAA", seed: 1);
var b = OhlcvFrame.Synthetic(480, "BBB", seed: 2);
PricePanel panel = PricePanel.FromFrames(new[] { a, b });      // inner join on timestamps

double[] aaa = panel["AAA"];
var prices = panel.ToMatrix();                                 // rows = dates, columns = symbols
var rets = panel.ToReturnsMatrix(logReturns: true);            // feeds Finance.Portfolio / Finance.Risk
PricePanel subset = panel.Select("AAA");
```

`PricePanel.Align(IReadOnlyList<(string Symbol, DateTime[] Time, double[] Values)>)` aligns raw
series directly.

### GraviFrame interop

```csharp
var gf = frame.ToGraviFrame();                  // Gravicode.Science.GraviFrame.DataFrame
OhlcvFrame round = gf.ToOhlcvFrame("DEMO");
var panelFrame = panel.ToGraviFrame();
```

## Notes and limitations

- `PricePanel.Align` performs **exact-timestamp inner joins** only; there is no as-of join at the
  panel level (use `SeriesAlignment.AsOf` in `Finance.TimeSeries` for that).
- CSV parsing skips rows containing `null` (as Yahoo exports for missing days).

---

Made by Gravicode Studios, led by Kang Fadhil
