# Finance.Backtesting

[← Indeks](../index.md) · [English](../../en/modules/backtesting.md) · Paket: `Gravicode.Finance.Backtesting`

Simulasi strategi ala Zipline dan Backtrader: engine event-driven untuk satu atau banyak instrumen,
backtester vektor yang cepat, parameter sweep, optimasi walk-forward, dan rebalancing portofolio
berkala.

Namespace: `Finance.Backtesting` dan `Finance.Backtesting.Strategies` (strategi bawaan).

## Konsep utama

- **Event-driven** — engine memutar ulang bar sesuai urutan waktu dan memanggil strategi Anda sekali
  per bar (`OnBar`). Order disimulasikan oleh model broker lengkap dengan komisi dan slippage.
- **Tanpa look-ahead** — secara bawaan, order yang dibuat saat memproses bar *t* terisi pada **open
  bar t+1** (`FillTiming.NextBarOpen`). Pengisian di close bar yang sama ("cheat-on-close") harus
  diaktifkan secara eksplisit.
- **Slippage** — basis poin tetap, atau model volume-share yang membatasi pengisian pada sebagian volume
  bar dan menggeser harga sesuai partisipasi.
- **Walk-forward** — optimasi parameter di jendela in-sample, perdagangkan set terbaik di jendela
  out-of-sample berikutnya, geser ke depan, lalu sambungkan ekuitas out-of-sample. Ini perkiraan
  realistis bagaimana strategi yang terus disetel ulang akan berkinerja.
- **Round trip** — satu entry beserta exit pasangannya; win rate, profit factor, dan rata-rata trade
  dihitung dari round trip.

## Menulis strategi

```csharp
using Finance.Backtesting;
using Finance.Core.Market;

public sealed class Breakout : Strategy
{
    private readonly int _lookback;
    public Breakout(int lookback = 55) => _lookback = lookback;
    public override string Name => $"Breakout {_lookback}";

    public override void OnBar(StrategyContext ctx, BarSlice slice)
    {
        foreach (var symbol in ctx.Symbols)
        {
            if (!slice.TryGet(symbol, out var bar) || ctx.BarCount(symbol) <= _lookback) continue;
            var history = ctx.History(symbol, _lookback + 1);           // termasuk bar saat ini
            double high = double.MinValue, low = double.MaxValue;
            for (int i = 0; i < history.Length - 1; i++) { high = Math.Max(high, history[i].High); low = Math.Min(low, history[i].Low); }

            if (bar.Close > high && ctx.PositionQuantity(symbol) <= 0) ctx.OrderTargetPercent(symbol, 0.95);
            else if (bar.Close < low && ctx.PositionQuantity(symbol) > 0) ctx.ClosePosition(symbol);
        }
    }
}
```

`StrategyContext` menyediakan `Time`, `Cash`, `Equity`, `Positions`, `Closes(symbol)`, `History`,
`LastPrice`, `Log`, serta metode order: `Buy`, `Sell`, `BuyLimit`, `SellLimit`, `BuyStop`, `SellStop`,
`Submit(Order)`, `Cancel`, `CancelAll`, `OrderTargetQuantity`, `OrderTargetValue`,
`OrderTargetPercent`, `ClosePosition`. Override `Initialize`, `OnFill`, dan `OnEnd` sesuai kebutuhan.

`Name` secara bawaan adalah nama kelas; `BacktestResult.StrategyName` melaporkannya.

## Menjalankan backtest

```csharp
using Finance.Backtesting.Strategies;

var settings = new BacktestSettings
{
    InitialCash = 100_000,
    FillTiming = FillTiming.NextBarOpen,
    Commission = CommissionModels.Percentage(0.0005, minimum: 1),
    Slippage = SlippageModels.FixedBps(2),
    AllowShort = false,
    FractionalQuantities = false,
    RiskFreeRate = 0.03,
};

Bar[] bars = SyntheticMarket.GeometricBrownianBars(2000, seed: 3);
BacktestResult result = new BacktestEngine(settings).Run(new SmaCrossoverStrategy(fast: 20, slow: 50), "DEMO", bars);

PerformanceStatistics s = result.Statistics;
Console.WriteLine($"{result.StrategyName}: total {s.TotalReturn:P1}, CAGR {s.Cagr:P1}, Sharpe {s.SharpeRatio:F2}, " +
                  $"max DD {s.MaxDrawdown:P1}, trade {s.NumberOfTrades}, menang {s.WinRate:P0}, PF {s.ProfitFactor:F2}");
foreach (RoundTrip t in result.Trades.Take(5))
    Console.WriteLine($"{t.EntryTime:d} → {t.ExitTime:d} {t.Pnl,10:N2} ({t.ReturnPct:P1})");
// result.Times / Equity / Cash / GrossExposure / Fills / Orders / FinalPositions
```

Untuk beberapa instrumen, berikan `IReadOnlyDictionary<string, Bar[]>`; bar disinkronkan berdasarkan
timestamp.

Strategi bawaan (namespace `Finance.Backtesting.Strategies`): `BuyAndHoldStrategy`,
`SmaCrossoverStrategy`, `RsiMeanReversionStrategy`, `BollingerBreakoutStrategy`,
`MomentumRotationStrategy(lookback, topN, rebalanceEvery)`.

## Backtest vektor

Jika strategi hanya fungsi dari harga, hitung posisinya sebagai array dan lewati loop event:

```csharp
double[] close = bars.Select(b => b.Close).ToArray();
double[] pos = VectorizedBacktester.CrossoverPositions(close, fast: 20, slow: 50);
VectorizedResult v = VectorizedBacktester.Run(close, pos, costBps: 5);
Console.WriteLine(v.Statistics);
// Multi-aset: VectorizedBacktester.Run(matriksHarga, matriksBobot, costBps: 5)
```

## Optimasi

```csharp
var data = new Dictionary<string, Bar[]> { ["DEMO"] = bars };
var grid = ParameterSweep.Grid(new[] { 10, 20, 30 }, new[] { 50, 100, 150 });

var sweep = ParameterSweep.Run(grid, p => new SmaCrossoverStrategy(p.Item1, p.Item2), data, settings,
    objective: st => st.SharpeRatio);
Console.WriteLine($"terbaik {sweep[0].Parameters}: Sharpe {sweep[0].Score:F2}");

var wf = new WalkForwardOptimizer<(int, int)>(grid, p => new SmaCrossoverStrategy(p.Item1, p.Item2), settings);
WalkForwardResult<(int, int)> oos = wf.Run(data, inSampleBars: 504, outOfSampleBars: 126);
Console.WriteLine($"out-of-sample tersambung: {oos.Statistics}");
```

## Rebalancing portofolio

Pasang fungsi bobot apa saja — misalnya optimizer dari `Finance.Portfolio` — ke rebalancer berkala.
`Finance.Backtesting` tidak bergantung pada `Finance.Portfolio`; delegate-lah yang menghubungkan
keduanya.

```csharp
BacktestResult rebal = PortfolioBacktester.Run(universe,       // IReadOnlyDictionary<string, Bar[]>
    req =>
    {
        var hrp = Finance.Portfolio.HierarchicalRiskParity.OptimizeFromReturns(req.Returns(), req.Assets);
        return hrp.Weights.ToDictionary();
    },
    lookback: 120, frequency: RebalanceFrequency.Monthly, settings: settings);

BacktestResult ew = PortfolioBacktester.Run(universe, PortfolioBacktester.EqualWeight());
```

## Aksi korporasi dan survivorship (v0.2)

```csharp
var settings = new BacktestSettings
{
    CorporateActions = new[]
    {
        CorporateAction.Split("NVDA", new DateTime(2024, 6, 10), 10),          // 10-untuk-1
        CorporateAction.Dividend("KO", new DateTime(2024, 6, 14), 0.485),
    },
    Delistings = new[] { new Delisting("SIVB", new DateTime(2023, 3, 28), DelistingReturn: -1.0) },
    Universe = pointInTimeUniverse,          // strategi memanggil context.IsInUniverse(symbol)
};
Bar[] research = CorporateActionAdjuster.Adjust(rawBars, actions);   // seri total return yang disesuaikan mundur
```

Berikan bar **mentah** ke mesin bersama daftar aksinya: split menskalakan posisi dan biayanya serta
membatalkan order terbuka simbol itu; dividen menambah (atau, untuk posisi short, mengurangi) kas pada ex-date;
kepemilikan yang delisting ditutup pada harga terakhir × (1 + delisting return). Gunakan bar yang disesuaikan
untuk riset dan indikator. `PointInTimeUniverse` mencatat siapa yang layak investasi kapan, sehingga backtest
memilih dari semesta sebagaimana adanya saat itu, bukan dari para penyintas hari ini.

## Replay order book (v0.2)

```csharp
var replay = new OrderBookReplay("BTCUSDT", new LognormalLatency(TimeSpan.FromMilliseconds(3), sigma: 0.5))
{
    TakerFeeBps = 1.0, MakerFeeBps = -0.2, CancelsAdvanceQueue = true,
};
BookReplayResult r = replay.Run(new MyMarketMaker(), recordedEvents);   // snapshot, update level, trade
```

`BookStrategy` melihat setiap event L2 dan mengirim order market atau limit lewat `BookReplayContext`. Replay
memodelkan latensi (order dan pembatalan bertindak pada book saat tiba), order marketable yang menelusuri
kedalaman terlihat, serta posisi antrean FIFO: order yang menunggu baru terisi setelah ukuran di depannya
tertransaksi, atau saat harga diperdagangkan menembus harganya. `CancelsAdvanceQueue` memilih apakah ukuran
yang ditarik di level kita dihitung di depan kita (optimis) atau di belakang (pesimis).

## Catatan dan batasan

- Replay order book mengasumsikan order kita tidak mengubah event berikutnya (tanpa dampak pasar yang
  menetap), asumsi umum replay; wajar selama ukuran kita kecil dibanding kedalaman yang terlihat.
- Saat split, order terbuka simbol tersebut dibatalkan alih-alih disesuaikan, karena order bersifat immutable.

- Statistik harian memakai `BacktestSettings.PeriodsPerYear` (bawaan 252); sesuaikan untuk bar
  intraday.
- `BacktestSettings.StartTradingAt` memberi bar-bar awal untuk pemanasan indikator tanpa trading —
  optimizer walk-forward memakainya.
- Setiap jendela out-of-sample walk-forward **dimulai tanpa posisi** (tidak ada posisi yang dibawa dari
  jendela sebelumnya), sehingga tren yang melewati batas jendela baru dimasuki lagi pada sinyal
  berikutnya.
- Belum ada engine replay tingkat tick atau order book (ada di roadmap).

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
