using Finance.Core;
using Finance.Core.Market;

namespace Finance.Backtesting;

/// <summary>Kind of a recorded market-data event.</summary>
public enum BookEventKind { Snapshot, Update, Trade }

/// <summary>
/// One recorded L2 event. <see cref="BookEventKind.Snapshot"/> carries full sides in <see cref="Bids"/>/<see cref="Asks"/>;
/// <see cref="BookEventKind.Update"/> sets the size of one level (0 removes it); <see cref="BookEventKind.Trade"/> is an
/// executed trade of <see cref="Size"/> at <see cref="Price"/>, <see cref="Side"/> being the aggressor.
/// </summary>
public sealed record BookEvent(DateTime Time, BookEventKind Kind, Side Side = Side.Buy, double Price = 0, double Size = 0,
    IReadOnlyList<BookLevel>? Bids = null, IReadOnlyList<BookLevel>? Asks = null);

/// <summary>Delay between a strategy decision and the exchange acting on it.</summary>
public interface ILatencyModel
{
    TimeSpan Next();
}

/// <summary>Fixed latency.</summary>
public sealed class ConstantLatency(TimeSpan latency) : ILatencyModel
{
    public TimeSpan Next() => latency;
}

/// <summary>Lognormal latency with the given median and log-standard-deviation (heavy right tail, as on real links).</summary>
public sealed class LognormalLatency(TimeSpan median, double sigma, ulong seed = 1) : ILatencyModel
{
    private readonly FastRandom _rng = new(seed);
    public TimeSpan Next() => TimeSpan.FromTicks((long)(median.Ticks * Math.Exp(sigma * _rng.NextGaussian())));
}

/// <summary>A resting or in-flight order of the replay engine.</summary>
public sealed class ReplayOrder
{
    internal ReplayOrder(long id, Side side, double quantity, double? limit, DateTime submitted, DateTime arrives)
    {
        Id = id; Side = side; Quantity = quantity; LimitPrice = limit; SubmittedAt = submitted; ArrivesAt = arrives;
    }

    public long Id { get; }
    public Side Side { get; }
    public double Quantity { get; }
    public double? LimitPrice { get; }
    public DateTime SubmittedAt { get; }
    /// <summary>When the order reaches the exchange (submit time + latency).</summary>
    public DateTime ArrivesAt { get; }
    public double Filled { get; internal set; }
    public double Remaining => Quantity - Filled;
    public bool IsLive { get; internal set; }
    public bool IsDone { get; internal set; }
    /// <summary>Displayed size ahead of this order at its price level (FIFO queue position).</summary>
    public double QueueAhead { get; internal set; }
    internal DateTime? CancelArrivesAt { get; set; }
}

/// <summary>A fill in the replay; <see cref="Maker"/> distinguishes passive (limit) from aggressive executions.</summary>
public readonly record struct ReplayFill(long OrderId, DateTime Time, Side Side, double Quantity, double Price, bool Maker, double Fee);

/// <summary>What a strategy can see and do during an order-book replay.</summary>
public sealed class BookReplayContext
{
    private readonly OrderBookReplay _engine;
    internal BookReplayContext(OrderBookReplay engine, OrderBook book) { _engine = engine; Book = book; }

    /// <summary>The replayed book as of <see cref="Time"/>.</summary>
    public OrderBook Book { get; }
    public DateTime Time { get; internal set; }
    public double Position { get; internal set; }
    public double Cash { get; internal set; }
    /// <summary>Cash + position marked at mid.</summary>
    public double Equity => Cash + Position * (double.IsNaN(Book.Mid) ? 0 : Book.Mid);
    public IReadOnlyList<ReplayOrder> OpenOrders => _engine.Orders.Where(o => !o.IsDone).ToList();

    /// <summary>Market order: executes against the book as it is when the order arrives, after the latency.</summary>
    public ReplayOrder SubmitMarket(Side side, double quantity) => _engine.Submit(side, quantity, null);

    /// <summary>Limit order: executes immediately if marketable on arrival, otherwise joins the back of its price level's queue.</summary>
    public ReplayOrder SubmitLimit(Side side, double quantity, double price) => _engine.Submit(side, quantity, price);

    /// <summary>Requests cancellation; it takes effect after the latency, so the order can still fill meanwhile.</summary>
    public void Cancel(ReplayOrder order) => _engine.RequestCancel(order);
}

/// <summary>Strategy driven by order-book events.</summary>
public abstract class BookStrategy
{
    /// <summary>Called after every market-data event has been applied to the book.</summary>
    public abstract void OnEvent(BookReplayContext context, BookEvent e);

    /// <summary>Called when one of the strategy's orders fills (fully or partly).</summary>
    public virtual void OnFill(BookReplayContext context, ReplayFill fill) { }
}

/// <summary>Result of an order-book replay.</summary>
public sealed record BookReplayResult(IReadOnlyList<ReplayFill> Fills, IReadOnlyList<ReplayOrder> Orders, DateTime[] Times, double[] Equity,
    double FinalPosition, double FinalCash, double FinalEquity, double FeesPaid);

/// <summary>
/// Event-driven replay of recorded L2 data, for strategies whose edge lives in the order book. It models
/// latency (orders and cancels reach the exchange late, against the book as it is then), marketable orders
/// walking the visible depth, and FIFO queue position for passive orders: a resting order fills only after the
/// displayed size ahead of it at its price has traded, or when the market trades through its price.
/// </summary>
/// <remarks>
/// Our own orders do not change the replayed book (no market impact on later events). That is the usual
/// assumption of replay backtests and is reasonable when our size is small relative to displayed depth.
/// Fees are charged per fill in basis points: <see cref="TakerFeeBps"/> for aggressive and
/// <see cref="MakerFeeBps"/> for passive fills (negative = rebate).
/// </remarks>
public sealed class OrderBookReplay
{
    private readonly List<ReplayOrder> _orders = new();
    private readonly List<ReplayFill> _fills = new();
    private BookStrategy _strategy = null!;
    private BookReplayContext _ctx = null!;
    private long _nextId = 1;

    public OrderBookReplay(string symbol, ILatencyModel? latency = null)
    {
        Symbol = symbol;
        Latency = latency ?? new ConstantLatency(TimeSpan.Zero);
    }

    public string Symbol { get; }
    public ILatencyModel Latency { get; }
    public double InitialCash { get; init; } = 1_000_000;
    public double TakerFeeBps { get; init; } = 1.0;
    public double MakerFeeBps { get; init; } = -0.2;

    /// <summary>
    /// How displayed size that disappears from our price level (cancellations) affects our queue position.
    /// True (default, optimistic): it is assumed to have been ahead of us. False (pessimistic): it is assumed to
    /// have been behind us, so only trades advance the queue. The truth lies between and is not observable in L2 data.
    /// </summary>
    public bool CancelsAdvanceQueue { get; init; } = true;

    internal IReadOnlyList<ReplayOrder> Orders => _orders;

    public BookReplayResult Run(BookStrategy strategy, IEnumerable<BookEvent> events)
    {
        _strategy = strategy;
        var book = new OrderBook(Symbol);
        _ctx = new BookReplayContext(this, book) { Cash = InitialCash };
        var times = new List<DateTime>();
        var equity = new List<double>();
        DateTime last = DateTime.MinValue;

        foreach (var e in events)
        {
            if (e.Time < last) throw new ArgumentException("Events must be in time order.");
            last = e.Time;
            // Orders and cancels that reach the exchange before this event act on the book as it stood.
            ProcessArrivals(e.Time);
            _ctx.Time = e.Time;
            switch (e.Kind)
            {
                case BookEventKind.Snapshot:
                    book.SetBids(e.Bids ?? Array.Empty<BookLevel>());
                    book.SetAsks(e.Asks ?? Array.Empty<BookLevel>());
                    break;
                case BookEventKind.Update:
                    UpdateQueues(e);
                    book.Update(e.Side, e.Price, e.Size);
                    break;
                case BookEventKind.Trade:
                    MatchTrade(e);
                    break;
            }
            book.Time = e.Time;
            CrossedThrough(book);
            strategy.OnEvent(_ctx, e);
            times.Add(e.Time);
            equity.Add(_ctx.Equity);
        }
        ProcessArrivals(DateTime.MaxValue);
        return new BookReplayResult(_fills.ToList(), _orders.ToList(), times.ToArray(), equity.ToArray(), _ctx.Position, _ctx.Cash, _ctx.Equity,
            _fills.Sum(f => f.Fee));
    }

    internal ReplayOrder Submit(Side side, double quantity, double? limit)
    {
        Guard.Positive(quantity);
        var o = new ReplayOrder(_nextId++, side, quantity, limit, _ctx.Time, _ctx.Time + Latency.Next());
        _orders.Add(o);
        return o;
    }

    internal void RequestCancel(ReplayOrder order)
    {
        if (!order.IsDone && order.CancelArrivesAt is null) order.CancelArrivesAt = _ctx.Time + Latency.Next();
    }

    private void ProcessArrivals(DateTime upTo)
    {
        foreach (var o in _orders.Where(o => !o.IsDone).OrderBy(o => o.ArrivesAt).ToList())
        {
            if (o.CancelArrivesAt is { } c && c <= upTo && c <= o.ArrivesAt) { o.IsDone = true; continue; } // cancelled in flight
            if (!o.IsLive && o.ArrivesAt <= upTo)
            {
                o.IsLive = true;
                ExecuteAggressive(o, o.ArrivesAt);
                if (!o.IsDone && o.LimitPrice is null) o.IsDone = true;          // unfilled market remainder is dropped
                else if (!o.IsDone)
                {
                    var same = o.Side == Side.Buy ? _ctx.Book.Bids : _ctx.Book.Asks;
                    o.QueueAhead = same.Where(l => l.Price == o.LimitPrice).Sum(l => l.Size);
                }
            }
            if (o.IsLive && !o.IsDone && o.CancelArrivesAt is { } cc && cc <= upTo) o.IsDone = true;
        }
    }

    /// <summary>Walks the opposite side up to the limit (if any), taking liquidity.</summary>
    private void ExecuteAggressive(ReplayOrder o, DateTime time)
    {
        var levels = o.Side == Side.Buy ? _ctx.Book.Asks : _ctx.Book.Bids;
        foreach (var level in levels.ToList())
        {
            if (o.Remaining <= 1e-12) break;
            if (o.LimitPrice is { } lim && (o.Side == Side.Buy ? level.Price > lim : level.Price < lim)) break;
            Fill(o, time, Math.Min(o.Remaining, level.Size), level.Price, maker: false);
        }
    }

    /// <summary>Applies size removed from our price level to the queue ahead, per <see cref="CancelsAdvanceQueue"/>.</summary>
    private void UpdateQueues(BookEvent e)
    {
        if (!CancelsAdvanceQueue) return;
        var levels = e.Side == Side.Buy ? _ctx.Book.Bids : _ctx.Book.Asks;
        double before = levels.Where(l => l.Price == e.Price).Sum(l => l.Size);
        if (e.Size >= before) return; // additions join behind us
        double removed = before - e.Size;
        foreach (var o in _orders.Where(o => o.IsLive && !o.IsDone && o.Side == e.Side && o.LimitPrice == e.Price))
            o.QueueAhead = Math.Max(0, o.QueueAhead - removed);
    }

    /// <summary>A print at our price consumes the queue ahead first, then fills us.</summary>
    private void MatchTrade(BookEvent e)
    {
        var restingSide = e.Side == Side.Buy ? Side.Sell : Side.Buy;   // aggressor buys hit resting sells
        double volume = e.Size;
        foreach (var o in _orders.Where(o => o.IsLive && !o.IsDone && o.Side == restingSide && o.LimitPrice is not null)
                     .OrderBy(o => o.ArrivesAt))
        {
            double lim = o.LimitPrice!.Value;
            bool atOrThrough = restingSide == Side.Sell ? e.Price >= lim : e.Price <= lim;
            if (!atOrThrough) continue;
            if (e.Price == lim)
            {
                double eaten = Math.Min(o.QueueAhead, volume);
                o.QueueAhead -= eaten;
                volume -= eaten;
                if (volume <= 0) break;
                double q = Math.Min(o.Remaining, volume);
                Fill(o, e.Time, q, lim, maker: true);
                volume -= q;
            }
            else Fill(o, e.Time, o.Remaining, lim, maker: true);           // traded through our price: we were filled first
        }
    }

    /// <summary>When the opposite best moves through a resting order's price, it has been filled at its limit.</summary>
    private void CrossedThrough(OrderBook book)
    {
        foreach (var o in _orders.Where(o => o.IsLive && !o.IsDone && o.LimitPrice is not null))
        {
            double lim = o.LimitPrice!.Value;
            bool crossed = o.Side == Side.Buy ? book.BestAsk < lim : book.BestBid > lim;
            if (crossed) Fill(o, book.Time, o.Remaining, lim, maker: true);
        }
    }

    private void Fill(ReplayOrder o, DateTime time, double qty, double price, bool maker)
    {
        if (qty <= 1e-12) return;
        double fee = qty * price * (maker ? MakerFeeBps : TakerFeeBps) / 10_000;
        o.Filled += qty;
        if (o.Remaining <= 1e-12) o.IsDone = true;
        double signed = o.Side == Side.Buy ? qty : -qty;
        _ctx.Position += signed;
        _ctx.Cash -= signed * price + fee;
        var f = new ReplayFill(o.Id, time, o.Side, qty, price, maker, fee);
        _fills.Add(f);
        _strategy.OnFill(_ctx, f);
    }
}
