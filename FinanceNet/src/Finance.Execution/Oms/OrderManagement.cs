using System.Collections.Concurrent;
using Finance.Core.Market;

namespace Finance.Execution.Oms;

/// <summary>What happened to an order.</summary>
public enum OrderEventKind { Submitted, Accepted, PartiallyFilled, Filled, CancelRequested, Cancelled, Rejected }

/// <summary>An order lifecycle event.</summary>
public sealed record OrderEvent(OrderEventKind Kind, Order Order, DateTime Time, Fill? Fill = null, string? Message = null);

/// <summary>Valid order status transitions.</summary>
public static class OrderStateMachine
{
    /// <summary>True when <paramref name="from"/> may move to <paramref name="to"/>.</summary>
    public static bool CanTransition(OrderStatus from, OrderStatus to) => from switch
    {
        OrderStatus.New => to is OrderStatus.PartiallyFilled or OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected,
        OrderStatus.PartiallyFilled => to is OrderStatus.PartiallyFilled or OrderStatus.Filled or OrderStatus.Cancelled,
        _ => false,
    };

    /// <summary>Terminal states never change again.</summary>
    public static bool IsTerminal(OrderStatus s) => s is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected;

    /// <summary>Sets the status, throwing on an illegal transition.</summary>
    public static void Transition(Order order, OrderStatus to)
    {
        if (order.Status == to && to != OrderStatus.PartiallyFilled) return;
        if (!CanTransition(order.Status, to))
            throw new InvalidOperationException($"Order {order.Id}: illegal transition {order.Status} -> {to}.");
        order.Status = to;
    }
}

/// <summary>
/// Thread-safe store of orders and their fills, raising <see cref="OrderEvent"/>s.
/// Fills go through <see cref="RecordFill"/> so status, average price and history stay consistent.
/// </summary>
public sealed class OrderStore
{
    private readonly ConcurrentDictionary<long, Order> _orders = new();
    private readonly ConcurrentDictionary<long, List<Fill>> _fills = new();
    private readonly List<OrderEvent> _history = new();
    private readonly object _gate = new();

    /// <summary>Raised after every state change.</summary>
    public event Action<OrderEvent>? OrderEvent;

    public IReadOnlyList<Order> Orders => _orders.Values.OrderBy(o => o.Id).ToList();

    public IEnumerable<Order> ActiveOrders => _orders.Values.Where(o => o.IsActive);

    public IReadOnlyList<OrderEvent> History
    {
        get { lock (_gate) return _history.ToList(); }
    }

    public Order? Get(long id) => _orders.TryGetValue(id, out var o) ? o : null;

    public IReadOnlyList<Fill> FillsOf(long id) => _fills.TryGetValue(id, out var f) ? f : (IReadOnlyList<Fill>)Array.Empty<Fill>();

    public IEnumerable<Fill> AllFills => _fills.Values.SelectMany(f => f).OrderBy(f => f.Time);

    public void Add(Order order, DateTime time)
    {
        if (!_orders.TryAdd(order.Id, order)) throw new InvalidOperationException($"Order {order.Id} already exists.");
        _fills[order.Id] = new List<Fill>();
        Raise(new OrderEvent(OrderEventKind.Submitted, order, time));
    }

    public void Accept(Order order, DateTime time) => Raise(new OrderEvent(OrderEventKind.Accepted, order, time));

    public void RecordFill(Fill fill)
    {
        var order = Get(fill.OrderId) ?? throw new InvalidOperationException($"Unknown order {fill.OrderId}.");
        if (!order.IsActive) throw new InvalidOperationException($"Order {order.Id} is {order.Status}.");
        if (fill.Quantity > order.RemainingQuantity + 1e-9) throw new InvalidOperationException("Fill exceeds remaining quantity.");
        lock (_gate)
        {
            order.ApplyFill(fill.Quantity, fill.Price);
            _fills[order.Id].Add(fill);
        }
        Raise(new OrderEvent(order.Status == OrderStatus.Filled ? OrderEventKind.Filled : OrderEventKind.PartiallyFilled, order, fill.Time, fill));
    }

    public bool Cancel(long id, DateTime time, string? reason = null)
    {
        var order = Get(id);
        if (order is null || !order.IsActive) return false;
        OrderStateMachine.Transition(order, OrderStatus.Cancelled);
        Raise(new OrderEvent(OrderEventKind.Cancelled, order, time, Message: reason));
        return true;
    }

    public void Reject(Order order, DateTime time, string reason)
    {
        if (!_orders.ContainsKey(order.Id)) Add(order, time);
        OrderStateMachine.Transition(order, OrderStatus.Rejected);
        Raise(new OrderEvent(OrderEventKind.Rejected, order, time, Message: reason));
    }

    private void Raise(OrderEvent e)
    {
        lock (_gate) _history.Add(e);
        OrderEvent?.Invoke(e);
    }
}
