namespace Finance.Core.Market;

public enum Side { Buy, Sell }

public enum OrderType { Market, Limit, Stop, StopLimit }

public enum TimeInForce { Day, Gtc, Ioc, Fok }

public enum OrderStatus { New, PartiallyFilled, Filled, Cancelled, Rejected }

public static class SideExtensions
{
    /// <summary>+1 for buy, -1 for sell.</summary>
    public static int Sign(this Side side) => side == Side.Buy ? 1 : -1;

    public static Side Opposite(this Side side) => side == Side.Buy ? Side.Sell : Side.Buy;
}

/// <summary>An order. Status fields are updated by brokers and simulators.</summary>
public sealed class Order
{
    private static long _nextId;

    public Order(string symbol, Side side, double quantity, OrderType type = OrderType.Market, double? limitPrice = null, double? stopPrice = null)
    {
        if (!(quantity > 0)) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (type is OrderType.Limit or OrderType.StopLimit && limitPrice is null)
            throw new ArgumentException("Limit orders need a limit price.");
        if (type is OrderType.Stop or OrderType.StopLimit && stopPrice is null)
            throw new ArgumentException("Stop orders need a stop price.");
        Id = Interlocked.Increment(ref _nextId);
        Symbol = symbol;
        Side = side;
        Quantity = quantity;
        Type = type;
        LimitPrice = limitPrice;
        StopPrice = stopPrice;
    }

    public long Id { get; }
    public string Symbol { get; }
    public Side Side { get; }
    public double Quantity { get; }
    public OrderType Type { get; }
    public double? LimitPrice { get; }
    public double? StopPrice { get; }
    public TimeInForce TimeInForce { get; init; } = TimeInForce.Gtc;
    public string? Tag { get; init; }
    public DateTime CreatedAt { get; init; }

    public OrderStatus Status { get; set; } = OrderStatus.New;
    public double FilledQuantity { get; private set; }
    public double AverageFillPrice { get; private set; }
    public double RemainingQuantity => Quantity - FilledQuantity;
    public bool IsActive => Status is OrderStatus.New or OrderStatus.PartiallyFilled;

    /// <summary>Records a (partial) fill and updates the running average price.</summary>
    public void ApplyFill(double quantity, double price)
    {
        double total = FilledQuantity + quantity;
        AverageFillPrice = (AverageFillPrice * FilledQuantity + price * quantity) / total;
        FilledQuantity = total;
        Status = FilledQuantity >= Quantity - 1e-12 ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
    }

    public static Order MarketBuy(string symbol, double qty) => new(symbol, Side.Buy, qty);
    public static Order MarketSell(string symbol, double qty) => new(symbol, Side.Sell, qty);
    public static Order LimitBuy(string symbol, double qty, double price) => new(symbol, Side.Buy, qty, OrderType.Limit, price);
    public static Order LimitSell(string symbol, double qty, double price) => new(symbol, Side.Sell, qty, OrderType.Limit, price);

    public override string ToString() => $"#{Id} {Side} {Quantity} {Symbol} {Type}{(LimitPrice is { } p ? $" @{p}" : "")} [{Status}]";
}

/// <summary>An execution report.</summary>
public readonly record struct Fill(long OrderId, string Symbol, Side Side, double Quantity, double Price, DateTime Time, double Commission = 0)
{
    /// <summary>Signed quantity: positive for buys.</summary>
    public double SignedQuantity => Side == Side.Buy ? Quantity : -Quantity;

    public double Notional => Quantity * Price;
}

/// <summary>A net position with average-cost accounting.</summary>
public sealed class Position
{
    public Position(string symbol) => Symbol = symbol;

    public string Symbol { get; }

    /// <summary>Signed quantity (negative = short).</summary>
    public double Quantity { get; private set; }
    public double AveragePrice { get; private set; }

    /// <summary>Realised PnL net of commissions.</summary>
    public double RealizedPnl { get; private set; }

    public bool IsFlat => System.Math.Abs(Quantity) < 1e-12;

    public double MarketValue(double price) => Quantity * price;
    public double UnrealizedPnl(double price) => (price - AveragePrice) * Quantity;

    /// <summary>Applies a fill; PnL is realised on the part that reduces the position.</summary>
    public void Apply(Fill fill)
    {
        double q = fill.SignedQuantity;
        if (IsFlat || System.Math.Sign(Quantity) == System.Math.Sign(q))
        {
            double total = Quantity + q;
            AveragePrice = (AveragePrice * Quantity + fill.Price * q) / total;
            Quantity = total;
        }
        else
        {
            double closed = System.Math.Min(System.Math.Abs(q), System.Math.Abs(Quantity));
            RealizedPnl += closed * (fill.Price - AveragePrice) * System.Math.Sign(Quantity);
            double before = Quantity;
            Quantity += q;
            if (System.Math.Abs(Quantity) < 1e-12) { Quantity = 0; AveragePrice = 0; }
            else if (System.Math.Sign(Quantity) != System.Math.Sign(before)) AveragePrice = fill.Price; // flipped through flat
        }
        RealizedPnl -= fill.Commission;
    }
}
