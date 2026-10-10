using Finance.Core.Market;

namespace Finance.Backtesting;

/// <summary>
/// Base class for event-driven strategies. Override <see cref="OnBar"/>; the other hooks are optional.
/// A strategy instance holds state, so use a fresh instance per backtest.
/// </summary>
public abstract class Strategy
{
    /// <summary>Display name (defaults to the type name).</summary>
    public virtual string Name => GetType().Name;

    /// <summary>Called once before the first bar.</summary>
    public virtual void Initialize(StrategyContext context) { }

    /// <summary>Called for every timestamp of the merged feed, after pending orders were processed.</summary>
    public abstract void OnBar(StrategyContext context, BarSlice slice);

    /// <summary>Called for every fill.</summary>
    public virtual void OnFill(StrategyContext context, Fill fill) { }

    /// <summary>Called once after the last bar.</summary>
    public virtual void OnEnd(StrategyContext context) { }
}

/// <summary>The bars that share one timestamp.</summary>
public sealed class BarSlice
{
    private readonly Dictionary<string, Bar> _bars;

    internal BarSlice(DateTime time, Dictionary<string, Bar> bars)
    {
        Time = time;
        _bars = bars;
    }

    public DateTime Time { get; }

    /// <summary>Symbols with a bar at this timestamp.</summary>
    public IEnumerable<string> Symbols => _bars.Keys;

    public IReadOnlyDictionary<string, Bar> Bars => _bars;

    public bool Contains(string symbol) => _bars.ContainsKey(symbol);

    public bool TryGet(string symbol, out Bar bar) => _bars.TryGetValue(symbol, out bar);

    public Bar this[string symbol] => _bars[symbol];
}

/// <summary>A completed round trip (flat → position → flat).</summary>
/// <param name="Direction">+1 long, −1 short.</param>
/// <param name="Quantity">Total quantity opened.</param>
/// <param name="EntryPrice">Average entry price.</param>
/// <param name="ExitPrice">Average exit price.</param>
/// <param name="Pnl">Profit net of commissions.</param>
/// <param name="ReturnPct">Pnl divided by entry notional.</param>
public sealed record RoundTrip(string Symbol, int Direction, DateTime EntryTime, DateTime ExitTime, double Quantity,
    double EntryPrice, double ExitPrice, double Pnl, double ReturnPct, double Commission)
{
    public TimeSpan Duration => ExitTime - EntryTime;
    public bool IsWin => Pnl > 0;
}
