using Finance.Core.Market;

namespace Finance.Backtesting;

/// <summary>Output of an event-driven backtest.</summary>
public sealed class BacktestResult
{
    internal BacktestResult(string strategyName, DateTime[] times, double[] equity, double[] cash, double[] grossExposure,
        IReadOnlyList<Fill> fills, IReadOnlyList<Order> orders, IReadOnlyList<RoundTrip> trades,
        IReadOnlyDictionary<string, Position> positions, IReadOnlyList<string> messages, PerformanceStatistics statistics, double initialCash)
    {
        StrategyName = strategyName;
        Times = times;
        Equity = equity;
        Cash = cash;
        GrossExposure = grossExposure;
        Fills = fills;
        Orders = orders;
        Trades = trades;
        FinalPositions = positions;
        Messages = messages;
        Statistics = statistics;
        InitialCash = initialCash;
    }

    public string StrategyName { get; }
    public double InitialCash { get; }

    /// <summary>Timestamps of the recorded equity curve.</summary>
    public DateTime[] Times { get; }

    /// <summary>Equity (cash + positions marked at the close) per timestamp.</summary>
    public double[] Equity { get; }
    public double[] Cash { get; }
    public double[] GrossExposure { get; }
    public IReadOnlyList<Fill> Fills { get; }
    public IReadOnlyList<Order> Orders { get; }

    /// <summary>Completed round trips.</summary>
    public IReadOnlyList<RoundTrip> Trades { get; }
    public IReadOnlyDictionary<string, Position> FinalPositions { get; }
    public IReadOnlyList<string> Messages { get; }
    public PerformanceStatistics Statistics { get; }

    public double FinalEquity => Equity.Length > 0 ? Equity[^1] : InitialCash;

    /// <summary>Per-period returns of the equity curve (length = Equity.Length − 1).</summary>
    public double[] Returns()
    {
        var r = new double[Math.Max(0, Equity.Length - 1)];
        for (int i = 1; i < Equity.Length; i++) r[i - 1] = Equity[i] / Equity[i - 1] - 1;
        return r;
    }

    /// <summary>Drawdown from the running peak per timestamp (≤ 0).</summary>
    public double[] Drawdowns()
    {
        var dd = new double[Equity.Length];
        double peak = double.NegativeInfinity;
        for (int i = 0; i < Equity.Length; i++)
        {
            peak = Math.Max(peak, Equity[i]);
            dd[i] = Equity[i] / peak - 1;
        }
        return dd;
    }

    public override string ToString() => $"{StrategyName}: {Statistics}";
}

/// <summary>
/// Event-driven backtester. Feeds are merged by timestamp; on each timestamp the engine
/// (1) fills working orders against the new bars, (2) updates prices, (3) calls <see cref="Strategy.OnBar"/>,
/// (4) fills close-timed market orders, and (5) records equity.
/// </summary>
public sealed class BacktestEngine
{
    public BacktestEngine(BacktestSettings? settings = null) => Settings = settings ?? new BacktestSettings();

    public BacktestSettings Settings { get; }

    /// <summary>Runs a single-symbol backtest.</summary>
    public BacktestResult Run(Strategy strategy, string symbol, Bar[] bars)
        => Run(strategy, new Dictionary<string, Bar[]> { [symbol] = bars });

    /// <summary>Runs a multi-asset backtest. Each symbol's bars must be in ascending time order.</summary>
    public BacktestResult Run(Strategy strategy, IReadOnlyDictionary<string, Bar[]> data)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(data);
        if (data.Count == 0) throw new ArgumentException("No data.");
        foreach (var (s, bars) in data)
            for (int i = 1; i < bars.Length; i++)
                if (bars[i].Time <= bars[i - 1].Time) throw new ArgumentException($"Bars for '{s}' are not strictly ascending at index {i}.");

        var symbols = data.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var ctx = new StrategyContext(Settings, strategy, symbols);
        var cursors = new int[symbols.Length];
        var feeds = symbols.Select(s => data[s]).ToArray();

        int capacity = feeds.Max(f => f.Length);
        var times = new List<DateTime>(capacity);
        var equity = new List<double>(capacity);
        var cash = new List<double>(capacity);
        var gross = new List<double>(capacity);

        strategy.Initialize(ctx);
        while (true)
        {
            DateTime next = DateTime.MaxValue;
            for (int k = 0; k < feeds.Length; k++)
                if (cursors[k] < feeds[k].Length && feeds[k][cursors[k]].Time < next) next = feeds[k][cursors[k]].Time;
            if (next == DateTime.MaxValue) break;

            var bars = new Dictionary<string, Bar>(symbols.Length, StringComparer.Ordinal);
            for (int k = 0; k < feeds.Length; k++)
                if (cursors[k] < feeds[k].Length && feeds[k][cursors[k]].Time == next)
                    bars[symbols[k]] = feeds[k][cursors[k]++];

            ctx.Advance(next, bars);
            ctx.ProcessPending(bars);
            ctx.UpdateLastBars(bars);
            ctx.NotifyBar(new BarSlice(next, bars));
            ctx.ProcessCloseQueue(bars);

            if (ctx.IsTrading)
            {
                times.Add(next);
                equity.Add(ctx.Equity);
                cash.Add(ctx.Cash);
                gross.Add(ctx.GrossExposure);
            }
        }
        strategy.OnEnd(ctx);

        var eq = equity.ToArray();
        var gx = gross.ToArray();
        var trades = ctx.RoundTrips.ToList();
        var stats = PerformanceStatistics.Compute(eq, Settings.PeriodsPerYear, Settings.RiskFreeRate, trades, gx);
        return new BacktestResult(strategy.Name, times.ToArray(), eq, cash.ToArray(), gx, ctx.Fills.ToList(), ctx.AllOrders.ToList(),
            trades, new Dictionary<string, Position>(ctx.Positions), ctx.Messages.ToList(), stats, Settings.InitialCash);
    }
}
