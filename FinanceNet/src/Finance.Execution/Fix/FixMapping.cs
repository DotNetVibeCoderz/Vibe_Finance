using Finance.Core.Market;

namespace Finance.Execution.Fix;

/// <summary>A decoded ExecutionReport (35=8).</summary>
public sealed record ExecutionReportInfo(
    string ClOrdId,
    string OrderId,
    string ExecId,
    char ExecType,
    char OrdStatus,
    string Symbol,
    Side Side,
    double LastQty,
    double LastPx,
    double CumQty,
    double LeavesQty,
    double AvgPx,
    DateTime TransactTime,
    string? Text)
{
    /// <summary>Core status for the FIX OrdStatus (39).</summary>
    public OrderStatus Status => OrdStatus switch
    {
        '0' or 'A' or 'E' => OrderStatus.New,
        '1' => OrderStatus.PartiallyFilled,
        '2' => OrderStatus.Filled,
        '4' or 'C' or '3' => OrderStatus.Cancelled,
        '8' => OrderStatus.Rejected,
        _ => OrderStatus.New,
    };

    /// <summary>True for a trade (ExecType F) carrying a fill.</summary>
    public bool IsTrade => ExecType is 'F' or '1' or '2' && LastQty > 0;

    /// <summary>The fill carried by a trade report, attributed to <paramref name="orderId"/>.</summary>
    public Fill? ToFill(long orderId, double commission = 0)
        => IsTrade ? new Fill(orderId, Symbol, Side, LastQty, LastPx, TransactTime, commission) : null;
}

/// <summary>Maps Core orders to and from FIX 4.4 order messages.</summary>
public static class FixMapping
{
    public static string SideCode(Side side) => side == Side.Buy ? "1" : "2";

    public static Side ParseSide(string? code) => code == "1" ? Side.Buy : code is "2" or "5" or "6" ? Side.Sell : throw new FixParseException($"Unsupported side {code}.");

    public static string OrdTypeCode(OrderType t) => t switch
    {
        OrderType.Market => "1",
        OrderType.Limit => "2",
        OrderType.Stop => "3",
        OrderType.StopLimit => "4",
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public static OrderType ParseOrdType(string? code) => code switch
    {
        "1" => OrderType.Market,
        "2" => OrderType.Limit,
        "3" => OrderType.Stop,
        "4" => OrderType.StopLimit,
        _ => throw new FixParseException($"Unsupported OrdType {code}."),
    };

    public static string TifCode(TimeInForce tif) => tif switch
    {
        TimeInForce.Day => "0",
        TimeInForce.Gtc => "1",
        TimeInForce.Ioc => "3",
        TimeInForce.Fok => "4",
        _ => "0",
    };

    public static TimeInForce ParseTif(string? code) => code switch
    {
        "1" => TimeInForce.Gtc,
        "3" => TimeInForce.Ioc,
        "4" => TimeInForce.Fok,
        _ => TimeInForce.Day,
    };

    /// <summary>NewOrderSingle (35=D) for a Core order.</summary>
    public static FixMessage NewOrderSingle(Order order, string clOrdId, string? account = null, DateTime? transactTime = null)
    {
        var m = new FixMessage(FixMsgType.NewOrderSingle)
            .Set(FixTags.ClOrdID, clOrdId)
            .Set(FixTags.Symbol, order.Symbol)
            .Set(FixTags.Side, SideCode(order.Side))
            .Set(FixTags.TransactTime, transactTime ?? DateTime.UtcNow)
            .Set(FixTags.OrderQty, order.Quantity)
            .Set(FixTags.OrdType, OrdTypeCode(order.Type))
            .Set(FixTags.TimeInForce, TifCode(order.TimeInForce));
        if (account is not null) m.Set(FixTags.Account, account);
        if (order.LimitPrice is { } p) m.Set(FixTags.Price, p);
        if (order.StopPrice is { } s) m.Set(FixTags.StopPx, s);
        return m;
    }

    /// <summary>Decodes a NewOrderSingle into a Core order (for acceptors and simulators).</summary>
    public static (Order Order, string ClOrdId) ParseNewOrderSingle(FixMessage m)
    {
        if (m.MsgType != FixMsgType.NewOrderSingle) throw new FixParseException("Not a NewOrderSingle.");
        var order = new Order(m[FixTags.Symbol], ParseSide(m.Get(FixTags.Side)), m.GetDouble(FixTags.OrderQty) ?? 0,
            ParseOrdType(m.Get(FixTags.OrdType)), m.GetDouble(FixTags.Price), m.GetDouble(FixTags.StopPx))
        {
            TimeInForce = ParseTif(m.Get(FixTags.TimeInForce)),
            Tag = m.Get(FixTags.ClOrdID),
        };
        return (order, m[FixTags.ClOrdID]);
    }

    /// <summary>OrderCancelRequest (35=F).</summary>
    public static FixMessage OrderCancelRequest(Order order, string clOrdId, string origClOrdId, DateTime? transactTime = null)
        => new FixMessage(FixMsgType.OrderCancelRequest)
            .Set(FixTags.OrigClOrdID, origClOrdId)
            .Set(FixTags.ClOrdID, clOrdId)
            .Set(FixTags.Symbol, order.Symbol)
            .Set(FixTags.Side, SideCode(order.Side))
            .Set(FixTags.TransactTime, transactTime ?? DateTime.UtcNow)
            .Set(FixTags.OrderQty, order.Quantity);

    /// <summary>Builds an ExecutionReport for an order's current state (acceptor side).</summary>
    public static FixMessage ExecutionReport(Order order, string clOrdId, string orderId, string execId, char execType,
        double lastQty = 0, double lastPx = 0, string? text = null, DateTime? transactTime = null)
    {
        char status = order.Status switch
        {
            OrderStatus.New => '0',
            OrderStatus.PartiallyFilled => '1',
            OrderStatus.Filled => '2',
            OrderStatus.Cancelled => '4',
            OrderStatus.Rejected => '8',
            _ => '0',
        };
        var m = new FixMessage(FixMsgType.ExecutionReport)
            .Set(FixTags.OrderID, orderId)
            .Set(FixTags.ClOrdID, clOrdId)
            .Set(FixTags.ExecID, execId)
            .Set(FixTags.ExecType, execType.ToString())
            .Set(FixTags.OrdStatus, status.ToString())
            .Set(FixTags.Symbol, order.Symbol)
            .Set(FixTags.Side, SideCode(order.Side))
            .Set(FixTags.OrderQty, order.Quantity)
            .Set(FixTags.LastQty, lastQty)
            .Set(FixTags.LastPx, lastPx)
            .Set(FixTags.LeavesQty, order.IsActive ? order.RemainingQuantity : 0)
            .Set(FixTags.CumQty, order.FilledQuantity)
            .Set(FixTags.AvgPx, order.AverageFillPrice)
            .Set(FixTags.TransactTime, transactTime ?? DateTime.UtcNow);
        if (text is not null) m.Set(FixTags.Text, text);
        return m;
    }

    /// <summary>Decodes an ExecutionReport.</summary>
    public static ExecutionReportInfo ParseExecutionReport(FixMessage m)
    {
        if (m.MsgType != FixMsgType.ExecutionReport) throw new FixParseException("Not an ExecutionReport.");
        var tt = m.Get(FixTags.TransactTime);
        return new ExecutionReportInfo(
            m.Get(FixTags.ClOrdID) ?? "",
            m.Get(FixTags.OrderID) ?? "",
            m.Get(FixTags.ExecID) ?? "",
            (m.Get(FixTags.ExecType) ?? "0")[0],
            (m.Get(FixTags.OrdStatus) ?? "0")[0],
            m.Get(FixTags.Symbol) ?? "",
            ParseSide(m.Get(FixTags.Side)),
            m.GetDouble(FixTags.LastQty) ?? 0,
            m.GetDouble(FixTags.LastPx) ?? 0,
            m.GetDouble(FixTags.CumQty) ?? 0,
            m.GetDouble(FixTags.LeavesQty) ?? 0,
            m.GetDouble(FixTags.AvgPx) ?? 0,
            tt is null ? DateTime.UtcNow : FixMessage.ParseTime(tt),
            m.Get(FixTags.Text));
    }
}
