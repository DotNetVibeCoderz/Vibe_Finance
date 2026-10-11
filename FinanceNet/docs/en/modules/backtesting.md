# Finance.Backtesting

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/backtesting.md) · Package: `Gravicode.Finance.Backtesting`

Strategy simulation in the spirit of Zipline and Backtrader: an event-driven engine over one or
many instruments, a fast vectorised backtester, parameter sweeps, walk-forward optimisation and
periodic portfolio rebalancing.

Namespaces: `Finance.Backtesting` and `Finance.Backtesting.Strategies` (built-in strategies).

## Key concepts

- **Event-driven** — the engine replays bars in time order and calls your strategy once per bar
  (`OnBar`). Orders are simulated by a broker model with commissions and slippage.
- **No look-ahead** — by default an order placed while handling bar *t* fills at the **open of
  bar t+1** (`FillTiming.NextBarOpen`). Same-bar close fills ("cheat-on-close") are opt-in.
- **Slippage** — fixed basis points, or a volume-share model that caps fills at a fraction of bar
  volume and moves the price with participation.
- **Walk-forward** — optimise parameters on an in-sample window, trade the best set on the next
  out-of-sample window, roll forward and stitch the out-of-sample equity. A realistic estimate of
  how a re-tuned strategy would have done.
- **Round trip** — an entry and its matching exit; win rate, profit factor and average trade come
  from round trips.

## Writing a strategy

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
            var history = ctx.History(symbol, _lookback + 1);           // includes the current bar
            double high = double.MinValue, low = double.MaxValue;
            for (int i = 0; i < history.Length - 1; i++) { high = Math.Max(high, history[i].High); low = Math.Min(low, history[i].Low); }

            if (bar.Close > high && ctx.PositionQuantity(symbol) <= 0) ctx.OrderTargetPercent(symbol, 0.95);
            else if (bar.Close < low && ctx.PositionQuantity(symbol) > 0) ctx.ClosePosition(symbol);
        }
    }
}
```

`StrategyContext` exposes `Time`, `Cash`, `Equity`, `Positions`, `Closes(symbol)`, `History`,
`LastPrice`, `Log`, and order methods: `Buy`, `Sell`, `BuyLimit`, `SellLimit`, `BuyStop`,
`SellStop`, `Submit(Order)`, `Cancel`, `CancelAll`, `OrderTargetQuantity`, `OrderTargetValue`,
`OrderTargetPercent`, `ClosePosition`. Override `Initialize`, `OnFill` and `OnEnd` as needed.

`Name` defaults to the class name; `BacktestResult.StrategyName` reports it.

## Running a backtest

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
                  $"max DD {s.MaxDrawdown:P1}, trades {s.NumberOfTrades}, win {s.WinRate:P0}, PF {s.ProfitFactor:F2}");
foreach (RoundTrip t in result.Trades.Take(5))
    Console.WriteLine($"{t.EntryTime:d} → {t.ExitTime:d} {t.Pnl,10:N2} ({t.ReturnPct:P1})");
// result.Times / Equity / Cash / GrossExposure / Fills / Orders / FinalPositions
```

For several instruments pass an `IReadOnlyDictionary<string, Bar[]>`; bars are synchronised by
timestamp.

Built-in strategies (namespace `Finance.Backtesting.Strategies`): `BuyAndHoldStrategy`, `SmaCrossoverStrategy`, `RsiMeanReversionStrategy`,
`BollingerBreakoutStrategy`, `MomentumRotationStrategy(lookback, topN, rebalanceEvery)`.

## Vectorised backtests

When the strategy is a function of prices only, compute positions as an array and skip the event
loop:

```csharp
double[] close = bars.Select(b => b.Close).ToArray();
double[] pos = VectorizedBacktester.CrossoverPositions(close, fast: 20, slow: 50);
VectorizedResult v = VectorizedBacktester.Run(close, pos, costBps: 5);
Console.WriteLine(v.Statistics);
// Multi-asset: VectorizedBacktester.Run(priceMatrix, weightMatrix, costBps: 5)
```

## Optimisation

```csharp
var data = new Dictionary<string, Bar[]> { ["DEMO"] = bars };
var grid = ParameterSweep.Grid(new[] { 10, 20, 30 }, new[] { 50, 100, 150 });

var sweep = ParameterSweep.Run(grid, p => new SmaCrossoverStrategy(p.Item1, p.Item2), data, settings,
    objective: st => st.SharpeRatio);
Console.WriteLine($"best {sweep[0].Parameters}: Sharpe {sweep[0].Score:F2}");

var wf = new WalkForwardOptimizer<(int, int)>(grid, p => new SmaCrossoverStrategy(p.Item1, p.Item2), settings);
WalkForwardResult<(int, int)> oos = wf.Run(data, inSampleBars: 504, outOfSampleBars: 126);
Console.WriteLine($"stitched out-of-sample: {oos.Statistics}");
```

## Portfolio rebalancing

Plug any weight function — for example a `Finance.Portfolio` optimiser — into a periodic
rebalancer. `Finance.Backtesting` does not depend on `Finance.Portfolio`; the delegate connects them.

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

## Corporate actions and survivorship (v0.2)

```csharp
var settings = new BacktestSettings
{
    CorporateActions = new[]
    {
        CorporateAction.Split("NVDA", new DateTime(2024, 6, 10), 10),          // 10-for-1
        CorporateAction.Dividend("KO", new DateTime(2024, 6, 14), 0.485),
    },
    Delistings = new[] { new Delisting("SIVB", new DateTime(2023, 3, 28), DelistingReturn: -1.0) },
    Universe = pointInTimeUniverse,          // strategies call context.IsInUniverse(symbol)
};
Bar[] research = CorporateActionAdjuster.Adjust(rawBars, actions);   // back-adjusted total-return series
```

Feed the engine **raw** bars together with the actions: splits rescale the position and its cost, and cancel
the symbol's open orders; dividends credit (or debit, for shorts) cash on the ex-date; delisted holdings are
closed at the last price × (1 + delisting return). Use adjusted bars for research and indicators.
`PointInTimeUniverse` records who was investable when, so a backtest can select from the universe as it
was rather than from today's survivors.

## Order-book replay (v0.2)

```csharp
var replay = new OrderBookReplay("BTCUSDT", new LognormalLatency(TimeSpan.FromMilliseconds(3), sigma: 0.5))
{
    TakerFeeBps = 1.0, MakerFeeBps = -0.2, CancelsAdvanceQueue = true,
};
BookReplayResult r = replay.Run(new MyMarketMaker(), recordedEvents);   // snapshots, level updates, trades
```

A `BookStrategy` sees every L2 event and submits market or limit orders through `BookReplayContext`. The
replay models latency (orders and cancels act on the book as it is when they arrive), marketable orders
walking the visible depth, and FIFO queue position: a resting order fills only after the displayed size ahead
of it has traded, or when the market trades through its price. `CancelsAdvanceQueue` chooses whether pulled
size at our level counts as ahead of us (optimistic) or behind (pessimistic).

## Notes and limitations

- Order-book replay assumes our orders do not change later events (no persistent market impact), the usual
  replay assumption; it is reasonable while our size is small relative to displayed depth.
- On a split, open orders for the symbol are cancelled rather than adjusted, because orders are immutable.

- Daily statistics use `BacktestSettings.PeriodsPerYear` (252 by default); set it for intraday bars.
- `BacktestSettings.StartTradingAt` feeds earlier bars for indicator warm-up without trading — the
  walk-forward optimiser uses it.
- Each walk-forward out-of-sample window **starts flat** (no position carried over from the previous
  window), so a trend held across a window boundary is re-entered at the next signal.
- There is no tick-level or order-book replay engine yet (roadmap).

---

Made by Gravicode Studios, led by Kang Fadhil
