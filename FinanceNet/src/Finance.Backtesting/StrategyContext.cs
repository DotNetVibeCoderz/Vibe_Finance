using System.Runtime.InteropServices;
using Finance.Core.Market;

namespace Finance.Backtesting;

/// <summary>
/// The strategy's view of the simulation: clock, cash, positions, bar history and the order API.
/// It also owns the simulated broker that fills orders against bars.
/// </summary>
public sealed class StrategyContext
{
    private readonly BacktestSettings _settings;
    private readonly Strategy _strategy;
    private readonly Dictionary<string, Position> _positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Bar>> _history = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<double>> _closes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bar> _lastBar = new(StringComparer.Ordinal);
    private readonly List<Order> _pending = new();
    private readonly List<Order> _closeQueue = new();
    private readonly HashSet<long> _triggeredStops = new();
    private readonly List<Fill> _fills = new();
    private readonly List<Order> _orders = new();
    private readonly RoundTripTracker _trips = new();
    private readonly List<string> _log = new();

    internal StrategyContext(BacktestSettings settings, Strategy strategy, IEnumerable<string> symbols)
    {
        _settings = settings;
        _strategy = strategy;
        Cash = settings.InitialCash;
        Symbols = symbols.ToArray();
        foreach (var s in Symbols)
        {
            _history[s] = new List<Bar>();
            _closes[s] = new List<double>();
        }
    }

    /// <summary>All symbols in the feed.</summary>
    public IReadOnlyList<string> Symbols { get; }

    /// <summary>Current simulation time.</summary>
    public DateTime Time { get; private set; }

    /// <summary>Number of timestamps processed so far (including the current one).</summary>
    public int BarIndex { get; private set; } = -1;

    public double Cash { get; private set; }

    public BacktestSettings Settings => _settings;

    /// <summary>True once <see cref="BacktestSettings.StartTradingAt"/> has been reached.</summary>
    public bool IsTrading => _settings.StartTradingAt is not { } start || Time >= start;

    /// <summary>Cash plus the market value of all positions at the latest closes.</summary>
    public double Equity
    {
        get
        {
            double e = Cash;
            foreach (var (s, p) in _positions)
                if (!p.IsFlat) e += p.Quantity * LastPrice(s);
            return e;
        }
    }

    /// <summary>Sum of |position value| at the latest closes.</summary>
    public double GrossExposure
    {
        get
        {
            double g = 0;
            foreach (var (s, p) in _positions)
                if (!p.IsFlat) g += Math.Abs(p.Quantity * LastPrice(s));
            return g;
        }
    }

    public IReadOnlyDictionary<string, Position> Positions => _positions;

    /// <summary>Signed position quantity in <paramref name="symbol"/> (0 when flat).</summary>
    public double PositionQuantity(string symbol) => _positions.TryGetValue(symbol, out var p) ? p.Quantity : 0;

    /// <summary>Active (unfilled) orders.</summary>
    public IReadOnlyList<Order> OpenOrders => _pending.Concat(_closeQueue).Where(o => o.IsActive).ToList();

    /// <summary>Messages written with <see cref="Log"/>.</summary>
    public IReadOnlyList<string> Messages => _log;

    /// <summary>Latest close of <paramref name="symbol"/> (NaN before its first bar).</summary>
    public double LastPrice(string symbol) => _lastBar.TryGetValue(symbol, out var b) ? b.Close : double.NaN;

    /// <summary>Latest bar of <paramref name="symbol"/>.</summary>
    public bool TryGetLastBar(string symbol, out Bar bar) => _lastBar.TryGetValue(symbol, out bar);

    /// <summary>Number of bars seen for <paramref name="symbol"/>.</summary>
    public int BarCount(string symbol) => _history.TryGetValue(symbol, out var h) ? h.Count : 0;

    /// <summary>All bars seen so far for <paramref name="symbol"/>, oldest first (a view; do not keep it across bars).</summary>
    public ReadOnlySpan<Bar> Bars(string symbol) => CollectionsMarshal.AsSpan(_history[symbol]);

    /// <summary>The last <paramref name="count"/> bars (fewer if not enough history).</summary>
    public ReadOnlySpan<Bar> History(string symbol, int count)
    {
        var all = Bars(symbol);
        return all[Math.Max(0, all.Length - count)..];
    }

    /// <summary>All closes seen so far for <paramref name="symbol"/>, oldest first (a view).</summary>
    public ReadOnlySpan<double> Closes(string symbol) => CollectionsMarshal.AsSpan(_closes[symbol]);

    /// <summary>The last <paramref name="count"/> closes.</summary>
    public ReadOnlySpan<double> Closes(string symbol, int count)
    {
        var all = Closes(symbol);
        return all[Math.Max(0, all.Length - count)..];
    }

    public void Log(string message) => _log.Add($"{Time:yyyy-MM-dd HH:mm} {message}");

    // ---- Order API -------------------------------------------------------------------------------------

    /// <summary>Submits an order. Before trading starts orders are cancelled immediately.</summary>
    public Order Submit(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        _orders.Add(order);
        if (!IsTrading || !_history.ContainsKey(order.Symbol))
        {
            order.Status = _history.ContainsKey(order.Symbol) ? OrderStatus.Cancelled : OrderStatus.Rejected;
            return order;
        }
        if (_settings.FillTiming == FillTiming.CurrentBarClose && order.Type == OrderType.Market) _closeQueue.Add(order);
        else _pending.Add(order);
        return order;
    }

    public Order Buy(string symbol, double quantity) => Submit(new Order(symbol, Side.Buy, quantity) { CreatedAt = Time });
    public Order Sell(string symbol, double quantity) => Submit(new Order(symbol, Side.Sell, quantity) { CreatedAt = Time });

    public Order BuyLimit(string symbol, double quantity, double limit)
        => Submit(new Order(symbol, Side.Buy, quantity, OrderType.Limit, limit) { CreatedAt = Time });

    public Order SellLimit(string symbol, double quantity, double limit)
        => Submit(new Order(symbol, Side.Sell, quantity, OrderType.Limit, limit) { CreatedAt = Time });

    public Order BuyStop(string symbol, double quantity, double stop)
        => Submit(new Order(symbol, Side.Buy, quantity, OrderType.Stop, stopPrice: stop) { CreatedAt = Time });

    public Order SellStop(string symbol, double quantity, double stop)
        => Submit(new Order(symbol, Side.Sell, quantity, OrderType.Stop, stopPrice: stop) { CreatedAt = Time });

    /// <summary>Cancels an active order.</summary>
    public bool Cancel(Order order)
    {
        if (!order.IsActive) return false;
        order.Status = OrderStatus.Cancelled;
        _pending.Remove(order);
        _closeQueue.Remove(order);
        return true;
    }

    /// <summary>Cancels all active orders, optionally only for one symbol.</summary>
    public void CancelAll(string? symbol = null)
    {
        foreach (var o in OpenOrders)
            if (symbol is null || o.Symbol == symbol) Cancel(o);
    }

    /// <summary>
    /// Orders the difference between <paramref name="target"/> and the current position plus quantities
    /// already working in open orders. Returns null when nothing needs to trade.
    /// </summary>
    public Order? OrderTargetQuantity(string symbol, double target)
    {
        double working = 0;
        foreach (var o in OpenOrders)
            if (o.Symbol == symbol) working += o.Side.Sign() * o.RemainingQuantity;
        double delta = target - PositionQuantity(symbol) - working;
        if (!_settings.FractionalQuantities) delta = Math.Truncate(delta);
        if (Math.Abs(delta) < 1e-12) return null;
        return delta > 0 ? Buy(symbol, delta) : Sell(symbol, -delta);
    }

    /// <summary>Targets a position worth <paramref name="value"/> at the latest close.</summary>
    public Order? OrderTargetValue(string symbol, double value)
    {
        double price = LastPrice(symbol);
        if (!(price > 0)) return null;
        return OrderTargetQuantity(symbol, value / price);
    }

    /// <summary>Targets a position worth <paramref name="percent"/> of current equity (1 = 100%).</summary>
    public Order? OrderTargetPercent(string symbol, double percent) => OrderTargetValue(symbol, percent * Equity);

    /// <summary>Closes the position in <paramref name="symbol"/>.</summary>
    public Order? ClosePosition(string symbol) => OrderTargetQuantity(symbol, 0);

    // ---- Engine plumbing --------------------------------------------------------------------------------

    /// <summary>Whether <paramref name="symbol"/> is in the point-in-time universe now (true when no universe is set).</summary>
    public bool IsInUniverse(string symbol) => _settings.Universe?.IsMember(symbol, Time) ?? true;

    private readonly HashSet<CorporateAction> _appliedActions = new();
    private readonly HashSet<Delisting> _appliedDelistings = new();

    /// <summary>Applies splits, dividends and delistings whose dates fall in (previous, now].</summary>
    private void ApplyCorporateEvents(DateTime previous, DateTime now)
    {
        if (_settings.CorporateActions is { } actions)
            foreach (var a in actions.Where(a => a.ExDate > previous && a.ExDate <= now && !_appliedActions.Contains(a)).OrderBy(a => a.ExDate))
            {
                _appliedActions.Add(a);
                if (!_positions.TryGetValue(a.Symbol, out var p) || p.IsFlat) continue;
                if (a.Kind == CorporateActionKind.Split)
                {
                    p.ApplySplit(a.Value);
                    if (_lastBar.TryGetValue(a.Symbol, out var lb))
                        _lastBar[a.Symbol] = new Bar(lb.Time, lb.Open / a.Value, lb.High / a.Value, lb.Low / a.Value, lb.Close / a.Value, lb.Volume * a.Value);
                    foreach (var o in _pending.Concat(_closeQueue).Where(o => o.Symbol == a.Symbol && o.IsActive)) o.Status = OrderStatus.Cancelled;
                    Log($"Split {a.Value}:1 on {a.Symbol}: position now {p.Quantity}, open orders cancelled.");
                }
                else
                {
                    double amount = p.Quantity * a.Value;
                    Cash += amount;
                    Log($"Dividend {a.Value} on {a.Symbol}: {(amount >= 0 ? "received" : "paid")} {Math.Abs(amount):F2}.");
                }
            }

        if (_settings.Delistings is { } delistings)
            foreach (var d in delistings.Where(d => d.Date <= now && !_appliedDelistings.Contains(d)))
            {
                _appliedDelistings.Add(d);
                foreach (var o in _pending.Concat(_closeQueue).Where(o => o.Symbol == d.Symbol && o.IsActive)) o.Status = OrderStatus.Cancelled;
                if (!_positions.TryGetValue(d.Symbol, out var p) || p.IsFlat || !_lastBar.TryGetValue(d.Symbol, out var last)) continue;
                double price = last.Close * (1 + d.DelistingReturn);
                var side = p.Quantity > 0 ? Side.Sell : Side.Buy;
                var fill = new Fill(-1, d.Symbol, side, Math.Abs(p.Quantity), price, d.Date);
                Cash -= fill.SignedQuantity * price;
                _trips.Record(fill, p.Quantity);
                p.Apply(fill);
                _fills.Add(fill);
                Log($"Delisted {d.Symbol}: closed {fill.Quantity} at {price:F4} (delisting return {d.DelistingReturn:P1}).");
            }
    }

    internal IReadOnlyList<Fill> Fills => _fills;
    internal IReadOnlyList<Order> AllOrders => _orders;
    internal IReadOnlyList<RoundTrip> RoundTrips => _trips.Completed;

    internal void Advance(DateTime time, Dictionary<string, Bar> bars)
    {
        ApplyCorporateEvents(Time, time);
        Time = time;
        BarIndex++;
        foreach (var (s, b) in bars)
        {
            _history[s].Add(b);
            _closes[s].Add(b.Close);
        }
    }

    /// <summary>Evaluates working orders against the new bars (before the strategy sees them).</summary>
    internal void ProcessPending(Dictionary<string, Bar> bars)
    {
        if (_pending.Count == 0) return;
        // Copy: OnFill handlers may submit new orders, which must wait for the next bar.
        var working = _pending.ToArray();
        foreach (var order in working)
        {
            if (!order.IsActive) { _pending.Remove(order); continue; }
            if (!bars.TryGetValue(order.Symbol, out var bar)) continue;
            if (TryGetTriggerPrice(order, bar, out double price)) Execute(order, bar, price, isLimit: order.Type is OrderType.Limit or OrderType.StopLimit);
            if (!order.IsActive) _pending.Remove(order);
        }
    }

    /// <summary>Fills market orders queued for the close of the current bar.</summary>
    internal void ProcessCloseQueue(Dictionary<string, Bar> bars)
    {
        if (_closeQueue.Count == 0) return;
        var queue = _closeQueue.ToArray();
        _closeQueue.Clear();
        foreach (var order in queue)
        {
            if (!order.IsActive) continue;
            if (!bars.TryGetValue(order.Symbol, out var bar))
            {
                _pending.Add(order); // no bar now; fill at the symbol's next open
                continue;
            }
            Execute(order, bar, bar.Close, isLimit: false);
            if (order.IsActive) _pending.Add(order); // remainder (volume-capped) continues next bar
        }
    }

    internal void UpdateLastBars(Dictionary<string, Bar> bars)
    {
        foreach (var (s, b) in bars) _lastBar[s] = b;
    }

    internal void NotifyBar(BarSlice slice) => _strategy.OnBar(this, slice);

    private bool TryGetTriggerPrice(Order order, in Bar bar, out double price)
    {
        bool buy = order.Side == Side.Buy;
        price = double.NaN;
        switch (order.Type)
        {
            case OrderType.Market:
                price = bar.Open;
                return true;
            case OrderType.Limit:
            {
                double limit = order.LimitPrice!.Value;
                if (buy && bar.Low <= limit) { price = Math.Min(bar.Open, limit); return true; }
                if (!buy && bar.High >= limit) { price = Math.Max(bar.Open, limit); return true; }
                return false;
            }
            case OrderType.Stop:
            {
                double stop = order.StopPrice!.Value;
                if (buy && bar.High >= stop) { price = Math.Max(bar.Open, stop); return true; }
                if (!buy && bar.Low <= stop) { price = Math.Min(bar.Open, stop); return true; }
                return false;
            }
            case OrderType.StopLimit:
            {
                double stop = order.StopPrice!.Value, limit = order.LimitPrice!.Value;
                bool triggeredBefore = _triggeredStops.Contains(order.Id);
                bool triggered = triggeredBefore || (buy ? bar.High >= stop : bar.Low <= stop);
                if (!triggered) return false;
                _triggeredStops.Add(order.Id);
                // After triggering it rests as a limit order; on the trigger bar it can only fill from the stop level.
                double reference = triggeredBefore ? bar.Open : (buy ? Math.Max(bar.Open, stop) : Math.Min(bar.Open, stop));
                if (buy && bar.Low <= limit) { price = Math.Min(reference, limit); return true; }
                if (!buy && bar.High >= limit) { price = Math.Max(reference, limit); return true; }
                return false;
            }
            default:
                return false;
        }
    }

    private void Execute(Order order, in Bar bar, double basePrice, bool isLimit)
    {
        double qty = _settings.Slippage.MaxFillQuantity(bar, order.RemainingQuantity);
        if (!_settings.FractionalQuantities) qty = Math.Floor(qty + 1e-9);
        if (qty <= 0) return; // volume-capped to nothing: keep working

        double price = _settings.Slippage.AdjustPrice(order.Side, basePrice, qty, bar);
        if (isLimit && order.LimitPrice is { } limit)
            price = order.Side == Side.Buy ? Math.Min(price, limit) : Math.Max(price, limit);

        double held = PositionQuantity(order.Symbol);
        double uncapped = qty;
        if (order.Side == Side.Sell && !_settings.AllowShort)
            qty = Math.Min(qty, Math.Max(0, held));
        if (order.Side == Side.Buy && !_settings.AllowLeverage)
            qty = AffordableQuantity(qty, price);
        bool capped = qty < uncapped - 1e-12;

        if (qty <= 1e-12)
        {
            order.Status = order.FilledQuantity == 0 ? OrderStatus.Rejected : OrderStatus.Cancelled;
            Log($"Order {order.Id} {order.Side} {order.Symbol} {(order.FilledQuantity == 0 ? "rejected" : "cancelled")}: insufficient {(order.Side == Side.Buy ? "cash" : "position")}.");
            return;
        }

        double commission = _settings.Commission.Calculate(qty, price);
        var fill = new Fill(order.Id, order.Symbol, order.Side, qty, price, Time, commission);
        Cash -= fill.SignedQuantity * price + commission;
        if (!_positions.TryGetValue(order.Symbol, out var position))
            _positions[order.Symbol] = position = new Position(order.Symbol);
        _trips.Record(fill, position.Quantity);
        position.Apply(fill);
        order.ApplyFill(qty, price);
        // A fill cut short by cash or position limits will not complete later either.
        if (capped && order.IsActive) order.Status = OrderStatus.Cancelled;
        _fills.Add(fill);
        _strategy.OnFill(this, fill);
    }

    /// <summary>Largest quantity ≤ <paramref name="qty"/> whose cost plus commission fits in the cash balance.</summary>
    private double AffordableQuantity(double qty, double price)
    {
        double cash = Cash;
        double Cost(double q) => q * price + _settings.Commission.Calculate(q, price);
        if (Cost(qty) <= cash + 1e-9) return qty;
        double q = qty;
        for (int i = 0; i < 60; i++)
        {
            double next = (cash - _settings.Commission.Calculate(q, price)) / price;
            if (!_settings.FractionalQuantities) next = Math.Floor(next + 1e-9);
            next = Math.Min(next, qty);
            if (next <= 0) return 0;
            if (Cost(next) <= cash + 1e-9) return next;
            q = _settings.FractionalQuantities ? next * (1 - 1e-9) : next - 1;
            if (q <= 0) return 0;
            if (Cost(q) <= cash + 1e-9) return q;
        }
        return 0;
    }
}

/// <summary>Builds round trips from the fill stream.</summary>
internal sealed class RoundTripTracker
{
    private sealed class Open
    {
        public int Direction;
        public DateTime EntryTime;
        public double EntryQty, EntryValue, ExitQty, ExitValue, Commission;
    }

    private readonly Dictionary<string, Open> _open = new(StringComparer.Ordinal);
    public List<RoundTrip> Completed { get; } = new();

    public void Record(Fill fill, double positionBefore)
    {
        double q = fill.SignedQuantity;
        double qtyLeft = fill.Quantity;
        double commissionPerUnit = fill.Quantity > 0 ? fill.Commission / fill.Quantity : 0;
        _open.TryGetValue(fill.Symbol, out var trip);

        if (trip is not null && Math.Sign(q) != trip.Direction)
        {
            double closing = Math.Min(qtyLeft, Math.Abs(positionBefore));
            trip.ExitQty += closing;
            trip.ExitValue += closing * fill.Price;
            trip.Commission += closing * commissionPerUnit;
            qtyLeft -= closing;
            if (Math.Abs(positionBefore) - closing <= 1e-12)
            {
                Close(fill.Symbol, trip, fill.Time);
                trip = null;
            }
        }
        if (qtyLeft > 1e-12)
        {
            if (trip is null)
            {
                trip = new Open { Direction = Math.Sign(q), EntryTime = fill.Time };
                _open[fill.Symbol] = trip;
            }
            trip.EntryQty += qtyLeft;
            trip.EntryValue += qtyLeft * fill.Price;
            trip.Commission += qtyLeft * commissionPerUnit;
        }
    }

    private void Close(string symbol, Open t, DateTime exitTime)
    {
        double entryAvg = t.EntryValue / t.EntryQty;
        double exitAvg = t.ExitValue / t.ExitQty;
        double pnl = t.Direction * (t.ExitValue - entryAvg * t.ExitQty) - t.Commission;
        Completed.Add(new RoundTrip(symbol, t.Direction, t.EntryTime, exitTime, t.EntryQty, entryAvg, exitAvg, pnl, pnl / t.EntryValue, t.Commission));
        _open.Remove(symbol);
    }
}
