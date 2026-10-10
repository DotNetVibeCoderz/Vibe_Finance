using Finance.Core.Market;
using Finance.Execution.Oms;
using Finance.Execution.Risk;

namespace Finance.Execution.Brokers;

/// <summary>
/// A simulated broker. Orders are matched against the latest order book (walking levels, so large
/// orders fill partially and at worse prices) or, without a book, against the top-of-book quote.
/// Liquidity taken from a snapshot is remembered until the next book update so two orders cannot
/// consume the same size. Supports market, limit, stop and stop-limit orders with Day/GTC/IOC/FOK,
/// commissions, simulated latency and optional pre-trade risk checks.
/// </summary>
public sealed class PaperBroker : IBroker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Position> _positions = new();
    private readonly Dictionary<string, Quote> _quotes = new();
    private readonly Dictionary<string, OrderBook> _books = new();
    private readonly Dictionary<string, double> _last = new();
    private readonly Dictionary<(string, Side, double), double> _consumed = new();
    private readonly HashSet<long> _triggered = new();
    private readonly TimeProvider _time;

    public PaperBroker(double initialCash = 100_000, string currency = "USD", CommissionModel? commission = null,
        PreTradeRiskChecker? risk = null, TimeProvider? time = null)
    {
        Cash = initialCash;
        Currency = currency;
        Commission = commission ?? CommissionModel.Zero;
        Risk = risk;
        _time = time ?? TimeProvider.System;
        Store.OrderEvent += e => OrderUpdated?.Invoke(e);
    }

    public string Name => "Paper";

    public event Action<OrderEvent>? OrderUpdated;

    /// <summary>Order and fill records.</summary>
    public OrderStore Store { get; } = new();

    public string Currency { get; }

    public double Cash { get; private set; }

    public CommissionModel Commission { get; }

    public PreTradeRiskChecker? Risk { get; }

    /// <summary>Delay applied before an order reaches the simulated venue.</summary>
    public TimeSpan Latency { get; init; } = TimeSpan.Zero;

    /// <summary>Adverse slippage applied to fills against a quote (no book), in basis points.</summary>
    public double QuoteSlippageBps { get; init; }

    /// <summary>Allow short positions (otherwise sells beyond the position are rejected).</summary>
    public bool AllowShort { get; init; } = true;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ----- market data -----

    /// <summary>Updates the top of book for a symbol and matches resting orders.</summary>
    public void UpdateQuote(string symbol, Quote quote)
    {
        lock (_gate)
        {
            _quotes[symbol] = quote;
            _last[symbol] = quote.Mid;
            if (!_books.ContainsKey(symbol))
                foreach (var k in _consumed.Keys.Where(k => k.Item1 == symbol).ToList()) _consumed.Remove(k);
            MatchResting(symbol);
        }
    }

    /// <summary>Replaces the order book of a symbol (resetting consumed liquidity) and matches resting orders.</summary>
    public void UpdateOrderBook(OrderBook book)
    {
        lock (_gate)
        {
            _books[book.Symbol] = book;
            foreach (var k in _consumed.Keys.Where(k => k.Item1 == book.Symbol).ToList()) _consumed.Remove(k);
            if (!double.IsNaN(book.Mid)) _last[book.Symbol] = book.Mid;
            MatchResting(book.Symbol);
        }
    }

    /// <summary>Sets a last trade price with an infinitely deep zero-spread quote around it.</summary>
    public void UpdatePrice(string symbol, double price, DateTime? time = null)
        => UpdateQuote(symbol, new Quote(time ?? Now, price, 0, price, 0));

    /// <summary>Latest reference (mid/last) price, or NaN.</summary>
    public double LastPrice(string symbol)
    {
        lock (_gate) return _last.TryGetValue(symbol, out var p) ? p : double.NaN;
    }

    // ----- IBroker -----

    public async Task<Order> SubmitAsync(Order order, CancellationToken cancellationToken = default)
    {
        if (Latency > TimeSpan.Zero) await Task.Delay(Latency, _time, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            double reference = _last.TryGetValue(order.Symbol, out var p) ? p : order.LimitPrice ?? order.StopPrice ?? double.NaN;
            double pos = _positions.TryGetValue(order.Symbol, out var ps) ? ps.Quantity : 0;
            if (Risk is not null)
            {
                var check = Risk.Check(order, reference, pos);
                if (!check.Passed)
                {
                    Store.Reject(order, Now, check.ToString());
                    return order;
                }
            }
            if (!AllowShort && order.Side == Side.Sell && order.Quantity > pos + 1e-9)
            {
                Store.Reject(order, Now, "Short selling is disabled.");
                return order;
            }
            Store.Add(order, Now);
            Store.Accept(order, Now);

            if (order.TimeInForce == TimeInForce.Fok && Available(order) < order.Quantity - 1e-9)
            {
                Store.Cancel(order.Id, Now, "FOK: insufficient liquidity");
                return order;
            }
            TryMatch(order);
            if (order.IsActive && order.TimeInForce is TimeInForce.Ioc or TimeInForce.Fok)
                Store.Cancel(order.Id, Now, "IOC remainder cancelled");
        }
        return order;
    }

    public Task<bool> CancelAsync(long orderId, CancellationToken cancellationToken = default)
    {
        lock (_gate) return Task.FromResult(Store.Cancel(orderId, Now, "Cancelled by client"));
    }

    /// <summary>Cancels every open Day order (call at the session close).</summary>
    public int ExpireDayOrders()
    {
        lock (_gate)
        {
            int n = 0;
            foreach (var o in Store.ActiveOrders.Where(o => o.TimeInForce == TimeInForce.Day).ToList())
                if (Store.Cancel(o.Id, Now, "Day order expired")) n++;
            return n;
        }
    }

    public Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<Position>>(_positions.Values.Where(p => !p.IsFlat).ToList());
    }

    public Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            double equity = Cash;
            foreach (var p in _positions.Values)
                if (_last.TryGetValue(p.Symbol, out var px)) equity += p.MarketValue(px);
                else equity += p.MarketValue(p.AveragePrice);
            return Task.FromResult(new AccountInfo("PAPER", Currency, Cash, equity, Math.Max(0, equity)));
        }
    }

    // ----- matching -----

    private void MatchResting(string symbol)
    {
        foreach (var o in Store.ActiveOrders.Where(o => o.Symbol == symbol).OrderBy(o => o.Id).ToList()) TryMatch(o);
    }

    private bool Triggered(Order o)
    {
        if (o.Type is not (OrderType.Stop or OrderType.StopLimit)) return true;
        if (_triggered.Contains(o.Id)) return true;
        if (!_last.TryGetValue(o.Symbol, out var px)) return false;
        bool hit = o.Side == Side.Buy ? px >= o.StopPrice!.Value : px <= o.StopPrice!.Value;
        if (hit) _triggered.Add(o.Id);
        return hit;
    }

    private bool PriceOk(Order o, double price)
    {
        bool limit = o.Type is OrderType.Limit or OrderType.StopLimit;
        if (!limit) return true;
        return o.Side == Side.Buy ? price <= o.LimitPrice!.Value + 1e-12 : price >= o.LimitPrice!.Value - 1e-12;
    }

    /// <summary>Liquidity (price, size) on the side an order takes from, best first.</summary>
    private IEnumerable<(double Price, double Size)> Liquidity(Order o)
    {
        if (_books.TryGetValue(o.Symbol, out var book))
        {
            var levels = o.Side == Side.Buy ? book.Asks : book.Bids;
            foreach (var l in levels)
            {
                double used = _consumed.GetValueOrDefault((o.Symbol, o.Side, l.Price));
                if (l.Size - used > 1e-12) yield return (l.Price, l.Size - used);
            }
            yield break;
        }
        if (_quotes.TryGetValue(o.Symbol, out var q))
        {
            double px = o.Side == Side.Buy ? q.Ask : q.Bid;
            double size = o.Side == Side.Buy ? q.AskSize : q.BidSize;
            if (size <= 0) size = double.PositiveInfinity;
            double used = _consumed.GetValueOrDefault((o.Symbol, o.Side, px));
            if (size - used > 1e-12) yield return (px, size - used);
        }
    }

    private double Available(Order o)
        => Liquidity(o).Where(l => PriceOk(o, l.Price)).Sum(l => l.Size);

    private void TryMatch(Order o)
    {
        if (!o.IsActive || !Triggered(o)) return;
        bool fromBook = _books.ContainsKey(o.Symbol);
        foreach (var (price, size) in Liquidity(o).ToList())
        {
            if (!o.IsActive) break;
            if (!PriceOk(o, price)) break;
            double qty = Math.Min(o.RemainingQuantity, size);
            if (qty <= 1e-12) continue;
            double fillPx = price;
            if (!fromBook && QuoteSlippageBps > 0 && o.Type is OrderType.Market or OrderType.Stop)
                fillPx *= 1 + o.Side.Sign() * QuoteSlippageBps * 1e-4;
            var key = (o.Symbol, o.Side, price);
            if (!double.IsPositiveInfinity(size) || fromBook) _consumed[key] = _consumed.GetValueOrDefault(key) + qty;
            var fill = new Fill(o.Id, o.Symbol, o.Side, qty, fillPx, Now, Commission.Compute(qty, fillPx));
            Book(fill);
            Store.RecordFill(fill);
        }
    }

    private void Book(Fill fill)
    {
        if (!_positions.TryGetValue(fill.Symbol, out var p)) _positions[fill.Symbol] = p = new Position(fill.Symbol);
        p.Apply(fill);
        Cash -= fill.SignedQuantity * fill.Price + fill.Commission;
    }
}
