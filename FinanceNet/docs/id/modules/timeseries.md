# Finance.TimeSeries

[← Indeks](../index.md) · [English](../../en/modules/timeseries.md) · Paket: `Gravicode.Finance.TimeSeries`

Return, drawdown, statistik rolling, dan analisis teknikal. Setiap fungsi batch menerima span,
mengembalikan array yang sejajar dengan inputnya, dan mengisi `NaN` selama periode pemanasan
(warm-up), sehingga hasilnya bisa langsung diplot terhadap tanggal asli tanpa mengatur ulang indeks.

Namespace: `Finance.TimeSeries` (return, rolling, penyelarasan) dan `Finance.TimeSeries.Indicators`
(`Ta`, `Candlesticks`, indikator streaming, `IndicatorCatalog`).

## Konsep utama

- **Return sederhana vs log** — return sederhana `P₁/P₀ − 1` bisa dijumlahkan antar aset; return log
  `ln(P₁/P₀)` bisa dijumlahkan antar waktu.
- **Drawdown** — penurunan dari puncak berjalan sebuah kurva ekuitas; *max drawdown* adalah yang
  terburuk.
- **Jendela rolling O(n)** — rata-rata/variansi rolling memakai jumlah berjalan; min/max rolling memakai
  deque monoton, sehingga jendela 252 hari sama mahalnya dengan jendela 5 hari.
- **Keluarga indikator** — *tren* (moving average, ADX, SAR), *momentum* (RSI, MACD, stokastik),
  *volatilitas* (ATR, Bollinger, Keltner), *volume* (OBV, MFI, VWAP), *statistik* (z-score, regresi),
  dan *pola candlestick* (engulfing, doji, morning star, dan lain-lain).
- **Wilder smoothing** — RSI, ATR, dan ADX memakai moving average Wilder (alpha = 1/periode) sesuai
  definisi aslinya; hasilnya mengikuti konvensi TA-Lib.

## Tur API

### Return dan drawdown

```csharp
using Finance.TimeSeries;

double[] close = frame.Close;                  // misalnya dari OhlcvFrame
double[] r = Returns.Simple(close);            // panjang n-1
double[] equity = Returns.EquityCurve(r, initial: 10_000);
var (maxDd, peak, trough) = Returns.MaxDrawdown(equity);
double sharpe = Returns.Sharpe(r, annualRiskFree: 0.03);
var (months, monthly) = Returns.Monthly(dates[1..], r);
```

### Statistik rolling

```csharp
double[] vol20 = Rolling.StdDev(r, 20);
double[] beta60 = Rolling.Beta(stockReturns, marketReturns, 60);
double[] corr = Rolling.Correlation(a, b, 60);
double[] ewma = Rolling.Ewma(r, alpha: 0.06);
```

`Rolling` juga menyediakan `Sum`, `Mean`, `Variance`, `Min`, `Max`, `ArgMin`, `ArgMax`, `Covariance`,
`ZScore`, `EwmaVolatility`, `PercentRank`, `Apply`, `Shift`, dan `Diff`. `NaN` di awal dilewati,
sehingga fungsi rolling bisa dirangkai.

### Indikator teknikal (`Ta`)

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

Indikator dengan beberapa keluaran mengembalikan tuple bernama. Tersedia juga, antara lain: `Wma`,
`Dema`, `Tema`, `Hma`, `Kama`, `T3`, `Trima`, `Zlema`, `Alma`, `Vwma`, `Ichimoku`, `Supertrend`,
`ParabolicSar`, `Aroon`, `Keltner`, `Donchian`, `Cci`, `Roc`, `Trix`, `Tsi`, `Kst`, `Ppo`, `Apo`,
`Cmo`, `Dpo`, `UltimateOscillator`, `AwesomeOscillator`, `WilliamsR`, `StochRsi`, `Obv`, `Vwap`,
`Mfi`, `Cmf`, `ChaikinOscillator`, `ForceIndex`, `EaseOfMovement`, `Vpt`, `Nvi`, `Pvi`,
`PivotPoints`, `HeikinAshi`, `LinearRegression`, `ZScore`, `Hurst`, `UlcerIndex`, serta beberapa
estimator volatilitas berbasis rentang (Parkinson, Garman-Klass, Rogers-Satchell, Yang-Zhang).

Tambahan v0.2 mencakup:

- **Siklus (Ehlers)**: `HilbertTransform` (periode dan fase siklus dominan, phasor, sine / lead sine,
  trendline instan, mode tren-vs-siklus), `Mama` (MAMA/FAMA), `SuperSmoother`, `HighPass`,
  `RoofingFilter`, `Decycler`, `LaguerreRsi`, `InstantaneousTrendline`, `CenterOfGravity`.
- **Tren**: `Frama`, `SineWma`, `GeometricMa`, `Ehma`, `Alligator`, `Gator` dan `Fractals` dari
  Bill Williams, `ZigZag`, `Vhf`, `EfficiencyRatio`, `TrendIntensity`, `Pfe`, `RandomWalkIndex`.
- **Momentum**: `DirectionalMovement` (+DM/−DM), `RocFraction`, `Pmo`, `Rmi`, `Smi`, `Inertia`,
  `PsychologicalLine`, `IntradayMomentum`, `PrettyGoodOscillator`, `ForecastOscillator`, `Bias`,
  `DoubleSmoothedStochastic`, `WaveTrend`, `Kdj`, `WilliamsAd`, `Squeeze`.
- **Volatilitas**: `YangZhangVolatility`, `EwmaVolatility`, `RelativeVolatilityIndex`, `Starc`,
  `AccelerationBands`, `StandardErrorBands`, `AverageRange`.
- **Volume**: `TwiggsMoneyFlow`, `MarketFacilitation`, `VolumeZoneOscillator`, `RelativeVolume`,
  `VwMacd`, `Vpci`, serta `VolumeProfile` / `MarketProfile` (TPO) bergulir dengan point of control
  dan value area 70%.
- **Statistik**: `RSquared`, `StandardError`, `MeanAbsoluteDeviation`, `BarsSinceExtreme`,
  `Entropy`, `Autocorrelation`, `CoefficientOfVariation`, `RollingQuantile`, `RollingSharpe`,
  `Drawdown`, `LogReturn`.

```csharp
var ht = Ta.HilbertTransform(close);                // DominantPeriod, DominantPhase, Sine, LeadSine, Trendline, TrendMode, ...
var (mama, fama) = Ta.Mama(close);
var vp = Ta.VolumeProfile(frame.High, frame.Low, frame.Volume, period: 50);   // PointOfControl, ValueAreaHigh, ValueAreaLow
```

### Pola candlestick

```csharp
int[] engulfing = Candlesticks.Detect(bars, CandlePattern.BullishEngulfing);  // +100 / -100 / 0 per bar
var hits = Candlesticks.Scan(bars);             // semua (Index, Pattern, Signal) yang ditemukan
```

Tersedia 60 pola, dari `Doji` dan `Hammer` hingga `ThreeWhiteSoldiers`, `RisingThreeMethods`,
`BullishAbandonedBaby`, `ThreeLineStrike`, `UpsideTasukiGap` dan `AdvanceBlock`. Sinyal mengikuti
TA-Lib: +100 bullish, −100 bearish, ditandai pada bar yang melengkapi pola.

### Indikator streaming

Untuk data live, kelas streaming memperbarui nilai dalam O(1) per bar dan memberikan hasil yang persis
sama dengan versi batch:

```csharp
var rsiLive = new StreamingRsi(14);
foreach (var b in liveBars)
{
    double v = rsiLive.Update(b);                // atau Update(harga)
    if (rsiLive.IsReady && v < 30) Console.WriteLine($"{b.Time}: oversold");
}
```

Tersedia: `StreamingSma`, `StreamingEma`, `StreamingRsi`, `StreamingAtr`, `StreamingBollinger`
(dengan `Upper` / `Lower`), dan `StreamingMacd`, semuanya mengimplementasikan `IStreamingIndicator`.

### Katalog indikator

`IndicatorCatalog` mendeskripsikan setiap indikator beserta metadata dan delegate perhitungan yang
seragam — inilah yang ditampilkan oleh Indicator Explorer di Gallery.

```csharp
Console.WriteLine(IndicatorCatalog.Count);                       // 248
foreach (var d in IndicatorCatalog.ByCategory(IndicatorCategory.Momentum))
    Console.WriteLine($"{d.Id,-12} {d.Name} ({string.Join(", ", d.Parameters.Select(p => $"{p.Name}={p.Default}"))})");

IndicatorDefinition bbDef = IndicatorCatalog.Get("BBANDS");
IndicatorSeries[] outputs = bbDef.Compute(bars, new Dictionary<string, double> { ["period"] = 30 });
foreach (var s in outputs) Console.WriteLine($"{s.Name}: {s.Values[^1]:F2}");
```

Setiap `IndicatorDefinition` memiliki `Id`, `Name`, `Category`, `Description`, `Overlay` (digambar di
grafik harga atau di panel tersendiri), dan `Parameters` (`Name`, `Default`, `Min`, `Max`,
`IsInteger`). Parameter yang tidak diberikan memakai nilai default. Id pola berbentuk
`CDL_BULLISHENGULFING`.

## Catatan dan batasan

- Katalog berisi **248 entri: 188 indikator ditambah 60 pola candlestick**, dalam delapan kategori
  (Trend, Momentum, Volatility, Volume, Statistics, PriceTransform, Pattern, Cycle).
- Keluarga Hilbert transform mengikuti formulasi TA-Lib; nilainya berbeda dari TA-Lib hanya pada masa
  pemanasan (50 bar pertama NaN di sini, TA-Lib membuang lookback tetap 63 bar).
- `ZigZag` digambar dengan hindsight (kaki terakhir bisa berubah) — alat bantu grafik, bukan sinyal.
  `Fractals` dan `Alligator` dilaporkan pada bar yang mengonfirmasinya, sehingga tidak melihat ke depan.
- `Returns.Sharpe` mengembalikan 0 jika simpangan baku di bawah 1e-12 (deret konstan), alih-alih rasio
  raksasa akibat sisa floating-point.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
