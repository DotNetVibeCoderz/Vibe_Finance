# Finance.TimeSeries

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/timeseries.md) · Package: `Gravicode.Finance.TimeSeries`

Returns, drawdowns, rolling statistics and technical analysis. Every batch function takes spans,
returns an array aligned with its input and writes `NaN` during the warm-up period, so outputs can
be plotted against the original dates without re-indexing.

Namespaces: `Finance.TimeSeries` (returns, rolling, alignment) and `Finance.TimeSeries.Indicators`
(`Ta`, `Candlesticks`, streaming indicators, `IndicatorCatalog`).

## Key concepts

- **Simple vs log returns** — simple returns `P₁/P₀ − 1` aggregate across assets; log returns
  `ln(P₁/P₀)` aggregate across time.
- **Drawdown** — the fall from the running peak of an equity curve; *max drawdown* is the worst.
- **Rolling windows in O(n)** — rolling mean/variance use running sums; rolling min/max use a
  monotonic deque, so a 252-day window costs the same as a 5-day one.
- **Indicator families** — *trend* (moving averages, ADX, SAR), *momentum* (RSI, MACD, stochastics),
  *volatility* (ATR, Bollinger, Keltner), *volume* (OBV, MFI, VWAP), *statistics* (z-score,
  regression) and *candlestick patterns* (engulfing, doji, morning star…).
- **Wilder smoothing** — RSI, ATR and ADX use Wilder's moving average (alpha = 1/period), as in the
  original definitions; results match TA-Lib conventions.

## API tour

### Returns and drawdowns

```csharp
using Finance.TimeSeries;

double[] close = frame.Close;                  // e.g. from OhlcvFrame
double[] r = Returns.Simple(close);            // length n-1
double[] equity = Returns.EquityCurve(r, initial: 10_000);
var (maxDd, peak, trough) = Returns.MaxDrawdown(equity);
double sharpe = Returns.Sharpe(r, annualRiskFree: 0.03);
var (months, monthly) = Returns.Monthly(dates[1..], r);
```

### Rolling statistics

```csharp
double[] vol20 = Rolling.StdDev(r, 20);
double[] beta60 = Rolling.Beta(stockReturns, marketReturns, 60);
double[] corr = Rolling.Correlation(a, b, 60);
double[] ewma = Rolling.Ewma(r, alpha: 0.06);
```

`Rolling` also offers `Sum`, `Mean`, `Variance`, `Min`, `Max`, `ArgMin`, `ArgMax`, `Covariance`,
`ZScore`, `EwmaVolatility`, `PercentRank`, `Apply`, `Shift` and `Diff`. Leading `NaN`s are skipped,
so rolling functions can be chained.

### Technical indicators (`Ta`)

```csharp
using Finance.TimeSeries.Indicators;

double[] sma = Ta.Sma(close, 50);
double[] ema = Ta.Ema(close, 20);
double[] rsi = Ta.Rsi(close, 14);
var macd = Ta.Macd(close);                          // (Macd, Signal, Histogram)
var bb = Ta.Bollinger(close, 20, 2);                // (Upper, Middle, Lower, PercentB, Bandwidth)
double[] atr = Ta.Atr(frame.High, frame.Low, close, 14);
var dmi = Ta.Dmi(frame.High, frame.Low, close);     // (PlusDi, MinusDi, Dx, Adx, Adxr)
var stoch = Ta.Stochastic(frame.High, frame.Low, close);   // (K, D)
```

Multi-output indicators return named tuples. Also available, among others: `Wma`, `Dema`, `Tema`,
`Hma`, `Kama`, `T3`, `Trima`, `Zlema`, `Alma`, `Vwma`, `Ichimoku`, `Supertrend`, `ParabolicSar`,
`Aroon`, `Keltner`, `Donchian`, `Cci`, `Roc`, `Trix`, `Tsi`, `Kst`, `Ppo`, `Apo`, `Cmo`, `Dpo`,
`UltimateOscillator`, `AwesomeOscillator`, `WilliamsR`, `StochRsi`, `Obv`, `Vwap`, `Mfi`, `Cmf`,
`ChaikinOscillator`, `ForceIndex`, `EaseOfMovement`, `Vpt`, `Nvi`, `Pvi`, `PivotPoints`,
`HeikinAshi`, `LinearRegression`, `ZScore`, `Hurst`, `UlcerIndex` and several range-based
volatility estimators (Parkinson, Garman-Klass, Rogers-Satchell, Yang-Zhang).

The v0.2 additions cover:

- **Cycles (Ehlers)**: `HilbertTransform` (dominant period and phase, phasor, sine / lead sine,
  instantaneous trendline, trend-vs-cycle mode), `Mama` (MAMA/FAMA), `SuperSmoother`, `HighPass`,
  `RoofingFilter`, `Decycler`, `LaguerreRsi`, `InstantaneousTrendline`, `CenterOfGravity`.
- **Trend**: `Frama`, `SineWma`, `GeometricMa`, `Ehma`, Bill Williams' `Alligator`, `Gator` and
  `Fractals`, `ZigZag`, `Vhf`, `EfficiencyRatio`, `TrendIntensity`, `Pfe`, `RandomWalkIndex`.
- **Momentum**: `DirectionalMovement` (+DM/−DM), `RocFraction`, `Pmo`, `Rmi`, `Smi`, `Inertia`,
  `PsychologicalLine`, `IntradayMomentum`, `PrettyGoodOscillator`, `ForecastOscillator`, `Bias`,
  `DoubleSmoothedStochastic`, `WaveTrend`, `Kdj`, `WilliamsAd`, `Squeeze`.
- **Volatility**: `YangZhangVolatility`, `EwmaVolatility`, `RelativeVolatilityIndex`, `Starc`,
  `AccelerationBands`, `StandardErrorBands`, `AverageRange`.
- **Volume**: `TwiggsMoneyFlow`, `MarketFacilitation`, `VolumeZoneOscillator`, `RelativeVolume`,
  `VwMacd`, `Vpci`, and rolling `VolumeProfile` / `MarketProfile` (TPO) with point of control and
  70% value area.
- **Statistics**: `RSquared`, `StandardError`, `MeanAbsoluteDeviation`, `BarsSinceExtreme`,
  `Entropy`, `Autocorrelation`, `CoefficientOfVariation`, `RollingQuantile`, `RollingSharpe`,
  `Drawdown`, `LogReturn`.

```csharp
var ht = Ta.HilbertTransform(close);                // DominantPeriod, DominantPhase, Sine, LeadSine, Trendline, TrendMode, ...
var (mama, fama) = Ta.Mama(close);
var vp = Ta.VolumeProfile(frame.High, frame.Low, frame.Volume, period: 50);   // PointOfControl, ValueAreaHigh, ValueAreaLow
```

### Candlestick patterns

```csharp
int[] engulfing = Candlesticks.Detect(bars, CandlePattern.BullishEngulfing);  // +100 / -100 / 0 per bar
var hits = Candlesticks.Scan(bars);             // every (Index, Pattern, Signal) found
```

60 patterns are implemented, from `Doji` and `Hammer` to `ThreeWhiteSoldiers`, `RisingThreeMethods`,
`BullishAbandonedBaby`, `ThreeLineStrike`, `UpsideTasukiGap` and `AdvanceBlock`. Signals follow TA-Lib:
+100 bullish, −100 bearish, marked on the bar that completes the pattern.

### Streaming indicators

For live feeds, streaming classes update in O(1) per bar and give exactly the batch result:

```csharp
var rsiLive = new StreamingRsi(14);
foreach (var b in liveBars)
{
    double v = rsiLive.Update(b);                // or Update(price)
    if (rsiLive.IsReady && v < 30) Console.WriteLine($"{b.Time}: oversold");
}
```

Available: `StreamingSma`, `StreamingEma`, `StreamingRsi`, `StreamingAtr`, `StreamingBollinger`
(with `Upper` / `Lower`) and `StreamingMacd`, all implementing `IStreamingIndicator`.

### The indicator catalogue

`IndicatorCatalog` describes every indicator with metadata and a uniform compute delegate — this is
what the Gallery's Indicator Explorer enumerates.

```csharp
Console.WriteLine(IndicatorCatalog.Count);                       // 248
foreach (var d in IndicatorCatalog.ByCategory(IndicatorCategory.Momentum))
    Console.WriteLine($"{d.Id,-12} {d.Name} ({string.Join(", ", d.Parameters.Select(p => $"{p.Name}={p.Default}"))})");

IndicatorDefinition bbDef = IndicatorCatalog.Get("BBANDS");
IndicatorSeries[] outputs = bbDef.Compute(bars, new Dictionary<string, double> { ["period"] = 30 });
foreach (var s in outputs) Console.WriteLine($"{s.Name}: {s.Values[^1]:F2}");
```

Each `IndicatorDefinition` has `Id`, `Name`, `Category`, `Description`, `Overlay` (drawn on the
price chart or in its own pane) and `Parameters` (`Name`, `Default`, `Min`, `Max`, `IsInteger`).
Pattern ids look like `CDL_BULLISHENGULFING`.

## Notes and limitations

- The catalogue holds **248 entries: 188 indicators plus 60 candlestick patterns**, in eight
  categories (Trend, Momentum, Volatility, Volume, Statistics, PriceTransform, Pattern, Cycle).
- The Hilbert-transform family follows TA-Lib's formulation; values differ from TA-Lib only during
  warm-up (the first 50 bars are NaN here, TA-Lib discards a fixed 63-bar lookback).
- `ZigZag` is drawn with hindsight (the last leg repaints) — a chart aid, not a signal. `Fractals`
  and the `Alligator` are reported on the bar that confirms them, so they never look ahead.
- `Returns.Sharpe` returns 0 when the standard deviation is below 1e-12 (a constant series), rather
  than an enormous ratio from floating-point residue.

---

Made by Gravicode Studios, led by Kang Fadhil
