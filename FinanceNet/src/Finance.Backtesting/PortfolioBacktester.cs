using Finance.Core.Market;
using Finance.Mathematics;

namespace Finance.Backtesting;

/// <summary>How often a portfolio is rebalanced.</summary>
public enum RebalanceFrequency { Daily, Weekly, Monthly, Quarterly, EveryNBars }

/// <summary>What a target-weights provider sees at a rebalance.</summary>
/// <param name="History">Lookback x N matrix of closes, oldest row first, columns ordered as <paramref name="Assets"/>.</param>
/// <param name="CurrentWeights">Current position values divided by equity.</param>
public sealed record RebalanceRequest(DateTime Time, IReadOnlyList<string> Assets, Matrix History, IReadOnlyDictionary<string, double> CurrentWeights)
{
    /// <summary>Simple returns of <see cref="History"/> ((lookback − 1) x N).</summary>
    public Matrix Returns()
    {
        var r = new Matrix(History.Rows - 1, History.Columns);
        for (int t = 1; t < History.Rows; t++)
            for (int j = 0; j < History.Columns; j++) r[t - 1, j] = History[t, j] / History[t - 1, j] - 1;
        return r;
    }
}

/// <summary>
/// Periodically rebalances to target weights returned by a delegate (plug in any optimiser, e.g. one from
/// Finance.Portfolio). Sells are submitted before buys so freed cash funds purchases.
/// </summary>
public sealed class RebalancingStrategy : Strategy
{
    private readonly Func<RebalanceRequest, IReadOnlyDictionary<string, double>> _provider;
    private DateTime _lastTime;
    private int _barsSinceRebalance;
    private bool _hasRebalanced;

    public RebalancingStrategy(Func<RebalanceRequest, IReadOnlyDictionary<string, double>> targetWeights, int lookback = 60,
        RebalanceFrequency frequency = RebalanceFrequency.Monthly, int everyNBars = 21, string? name = null)
    {
        _provider = targetWeights ?? throw new ArgumentNullException(nameof(targetWeights));
        if (lookback < 2) throw new ArgumentException("Lookback must be at least two bars.");
        Lookback = lookback;
        Frequency = frequency;
        EveryNBars = everyNBars;
        _name = name ?? $"Rebalance ({frequency})";
    }

    private readonly string _name;
    public override string Name => _name;
    public int Lookback { get; }
    public RebalanceFrequency Frequency { get; }
    public int EveryNBars { get; }

    /// <summary>Number of rebalances performed.</summary>
    public int RebalanceCount { get; private set; }

    public override void OnBar(StrategyContext context, BarSlice slice)
    {
        bool due = IsDue(slice.Time);
        _lastTime = slice.Time;
        _barsSinceRebalance++;
        if (!context.IsTrading) return;
        if (_hasRebalanced && !due) return;

        var assets = context.Symbols;
        if (assets.Any(s => context.BarCount(s) < Lookback)) return;

        var history = new Matrix(Lookback, assets.Count);
        for (int j = 0; j < assets.Count; j++)
        {
            var c = context.Closes(assets[j], Lookback);
            for (int t = 0; t < Lookback; t++) history[t, j] = c[t];
        }
        double equity = context.Equity;
        var current = assets.ToDictionary(s => s, s => context.PositionQuantity(s) * context.LastPrice(s) / equity, StringComparer.Ordinal);
        var targets = _provider(new RebalanceRequest(slice.Time, assets, history, current));

        context.CancelAll();
        var orders = assets
            .Select(s => (Symbol: s, Delta: (targets.TryGetValue(s, out var w) ? w : 0) - current[s]))
            .OrderBy(x => x.Delta);
        foreach (var (symbol, _) in orders)
            context.OrderTargetPercent(symbol, targets.TryGetValue(symbol, out var w) ? w : 0);

        _hasRebalanced = true;
        _barsSinceRebalance = 0;
        RebalanceCount++;
    }

    private bool IsDue(DateTime now)
    {
        if (_lastTime == default) return true;
        return Frequency switch
        {
            RebalanceFrequency.Daily => true,
            RebalanceFrequency.Weekly => System.Globalization.ISOWeek.GetWeekOfYear(now) != System.Globalization.ISOWeek.GetWeekOfYear(_lastTime) || (now - _lastTime).TotalDays >= 7,
            RebalanceFrequency.Monthly => now.Month != _lastTime.Month || now.Year != _lastTime.Year,
            RebalanceFrequency.Quarterly => (now.Month - 1) / 3 != (_lastTime.Month - 1) / 3 || now.Year != _lastTime.Year,
            RebalanceFrequency.EveryNBars => _barsSinceRebalance >= EveryNBars,
            _ => false,
        };
    }
}

/// <summary>Convenience entry point for target-weight portfolio backtests.</summary>
public static class PortfolioBacktester
{
    /// <summary>Backtests periodic rebalancing to the weights produced by <paramref name="targetWeights"/>.</summary>
    public static BacktestResult Run(IReadOnlyDictionary<string, Bar[]> data, Func<RebalanceRequest, IReadOnlyDictionary<string, double>> targetWeights,
        int lookback = 60, RebalanceFrequency frequency = RebalanceFrequency.Monthly, BacktestSettings? settings = null, int everyNBars = 21, string? name = null)
        => new BacktestEngine(settings).Run(new RebalancingStrategy(targetWeights, lookback, frequency, everyNBars, name), data);

    /// <summary>Equal-weight provider (scaled by <paramref name="investedFraction"/> to leave room for costs).</summary>
    public static Func<RebalanceRequest, IReadOnlyDictionary<string, double>> EqualWeight(double investedFraction = 0.995)
        => req => req.Assets.ToDictionary(a => a, _ => investedFraction / req.Assets.Count, StringComparer.Ordinal);
}
