using Finance.Core.Market;

namespace Finance.Backtesting.Strategies;

/// <summary>Buys an equal-weighted basket on the first bar where every symbol has a price, then holds.</summary>
public sealed class BuyAndHoldStrategy : Strategy
{
    private readonly string[]? _symbols;
    private bool _invested;

    public BuyAndHoldStrategy(params string[] symbols) => _symbols = symbols.Length > 0 ? symbols : null;

    public override string Name => "Buy & Hold";

    public override void OnBar(StrategyContext context, BarSlice slice)
    {
        if (_invested || !context.IsTrading) return;
        var symbols = _symbols ?? context.Symbols.ToArray();
        if (symbols.Any(s => double.IsNaN(context.LastPrice(s)))) return;
        foreach (var s in symbols) context.OrderTargetPercent(s, 1.0 / symbols.Length);
        _invested = true;
    }
}

/// <summary>
/// Moving-average crossover: long when the fast SMA is above the slow SMA, flat (or short) otherwise.
/// </summary>
public sealed class SmaCrossoverStrategy : Strategy
{
    public SmaCrossoverStrategy(int fast = 20, int slow = 50, string? symbol = null, bool allowShort = false)
    {
        if (fast <= 0 || slow <= fast) throw new ArgumentException("Need 0 < fast < slow.");
        Fast = fast;
        Slow = slow;
        Symbol = symbol;
        AllowShort = allowShort;
    }

    public int Fast { get; }
    public int Slow { get; }
    public string? Symbol { get; private set; }
    public bool AllowShort { get; }
    public override string Name => $"SMA Crossover ({Fast}/{Slow})";

    public override void Initialize(StrategyContext context) => Symbol ??= context.Symbols[0];

    public override void OnBar(StrategyContext context, BarSlice slice)
    {
        if (!slice.Contains(Symbol!)) return;
        var closes = context.Closes(Symbol!);
        if (closes.Length < Slow) return;
        double fast = Mean(closes[^Fast..]), slow = Mean(closes[^Slow..]);
        double target = fast > slow ? 0.999 : (AllowShort ? -0.999 : 0);
        double current = context.PositionQuantity(Symbol!);
        if (Math.Sign(target) != Math.Sign(current) || (target == 0 && current != 0))
            context.OrderTargetPercent(Symbol!, target);
    }

    private static double Mean(ReadOnlySpan<double> x)
    {
        double s = 0;
        foreach (var v in x) s += v;
        return s / x.Length;
    }
}

/// <summary>RSI mean reversion: buy when RSI drops below <see cref="Oversold"/>, exit when it rises above <see cref="Overbought"/>.</summary>
public sealed class RsiMeanReversionStrategy : Strategy
{
    private WilderRsi? _rsi;

    public RsiMeanReversionStrategy(int period = 14, double oversold = 30, double overbought = 70, string? symbol = null)
    {
        Period = period;
        Oversold = oversold;
        Overbought = overbought;
        Symbol = symbol;
    }

    public int Period { get; }
    public double Oversold { get; }
    public double Overbought { get; }
    public string? Symbol { get; private set; }
    public override string Name => $"RSI Mean Reversion ({Period}, {Oversold}/{Overbought})";

    public override void Initialize(StrategyContext context)
    {
        Symbol ??= context.Symbols[0];
        _rsi = new WilderRsi(Period);
    }

    public override void OnBar(StrategyContext context, BarSlice slice)
    {
        if (!slice.TryGet(Symbol!, out var bar)) return;
        double rsi = _rsi!.Update(bar.Close);
        if (!_rsi.IsReady) return;
        double pos = context.PositionQuantity(Symbol!);
        if (pos == 0 && rsi < Oversold && context.OpenOrders.Count == 0) context.OrderTargetPercent(Symbol!, 0.999);
        else if (pos > 0 && rsi > Overbought) context.ClosePosition(Symbol!);
    }
}

/// <summary>Bollinger breakout: buy a close above the upper band, exit on a close below the middle band.</summary>
public sealed class BollingerBreakoutStrategy : Strategy
{
    public BollingerBreakoutStrategy(int period = 20, double width = 2, string? symbol = null)
    {
        Period = period;
        Width = width;
        Symbol = symbol;
    }

    public int Period { get; }
    public double Width { get; }
    public string? Symbol { get; private set; }
    public override string Name => $"Bollinger Breakout ({Period}, {Width})";

    public override void Initialize(StrategyContext context) => Symbol ??= context.Symbols[0];

    public override void OnBar(StrategyContext context, BarSlice slice)
    {
        if (!slice.TryGet(Symbol!, out var bar)) return;
        var closes = context.Closes(Symbol!);
        if (closes.Length < Period) return;
        var (mean, std) = Indicators.MeanStd(closes[^Period..]);
        double pos = context.PositionQuantity(Symbol!);
        if (pos == 0 && bar.Close > mean + Width * std && context.OpenOrders.Count == 0) context.OrderTargetPercent(Symbol!, 0.999);
        else if (pos > 0 && bar.Close < mean) context.ClosePosition(Symbol!);
    }
}

/// <summary>
/// Cross-sectional momentum rotation: every <see cref="RebalanceEvery"/> bars, hold the <see cref="TopN"/> symbols with
/// the highest trailing <see cref="Lookback"/>-bar return in equal weight (only those with positive momentum
/// when <see cref="AbsoluteFilter"/> is set).
/// </summary>
public sealed class MomentumRotationStrategy : Strategy
{
    private int _bars;
    private int _lastRebalance = -1;

    public MomentumRotationStrategy(int lookback = 126, int topN = 3, int rebalanceEvery = 21, bool absoluteFilter = false)
    {
        Lookback = lookback;
        TopN = topN;
        RebalanceEvery = rebalanceEvery;
        AbsoluteFilter = absoluteFilter;
    }

    public int Lookback { get; }
    public int TopN { get; }
    public int RebalanceEvery { get; }
    public bool AbsoluteFilter { get; }
    public override string Name => $"Momentum Rotation (top {TopN}, {Lookback}d)";

    /// <summary>Symbols selected at the last rebalance.</summary>
    public IReadOnlyList<string> Holdings { get; private set; } = Array.Empty<string>();

    public override void OnBar(StrategyContext context, BarSlice slice)
    {
        _bars++;
        if (!context.IsTrading) return;
        if (_lastRebalance >= 0 && _bars - _lastRebalance < RebalanceEvery) return;
        var eligible = context.Symbols.Where(s => context.BarCount(s) > Lookback).ToArray();
        if (eligible.Length == 0) return;
        var ranked = eligible
            .Select(s =>
            {
                var c = context.Closes(s);
                return (Symbol: s, Momentum: c[^1] / c[^(Lookback + 1)] - 1);
            })
            .Where(x => !AbsoluteFilter || x.Momentum > 0)
            .OrderByDescending(x => x.Momentum)
            .Take(TopN)
            .Select(x => x.Symbol)
            .ToArray();
        _lastRebalance = _bars;

        var keep = new HashSet<string>(ranked, StringComparer.Ordinal);
        foreach (var s in context.Symbols)
            if (!keep.Contains(s) && context.PositionQuantity(s) != 0) context.ClosePosition(s);
        double weight = ranked.Length > 0 ? 0.995 / ranked.Length : 0;
        foreach (var s in ranked) context.OrderTargetPercent(s, weight);
        Holdings = ranked;
    }
}
