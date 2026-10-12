namespace Finance.Core.Market;

public enum Side { Buy, Sell }

public enum OrderType { Market, Limit, Stop, StopLimit }

public enum TimeInForce { Day, Gtc, Ioc, Fok }

public enum OrderStatus { New, PartiallyFilled, Filled, Cancelled, Rejected }

/// <summary>Which price a stop / conditional order (or an attached take-profit / stop-loss) is triggered by.</summary>
public enum TriggerPriceType { Last, Mark, Index }

/// <summary>Direction the trigger price must cross: rising to it (buy stops) or falling to it (sell stops).</summary>
public enum TriggerDirection { Rising, Falling }

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

    /// <summary>Price that triggers a <see cref="OrderType.Stop"/> / <see cref="OrderType.StopLimit"/> order (its stop price).</summary>
    public double? TriggerPrice => Type is OrderType.Stop or OrderType.StopLimit ? StopPrice : null;

    /// <summary>Trigger reference for stop orders and attached TP/SL; null leaves the venue default (usually last price).</summary>
    public TriggerPriceType? TriggerBy { get; init; }

    /// <summary>Explicit trigger direction; null derives it from the side (see <see cref="EffectiveTriggerDirection"/>).</summary>
    public TriggerDirection? TriggerDirection { get; init; }

    /// <summary>Rising for buy stops and falling for sell stops unless <see cref="TriggerDirection"/> is set.</summary>
    public TriggerDirection EffectiveTriggerDirection
        => TriggerDirection ?? (Side == Side.Buy ? Market.TriggerDirection.Rising : Market.TriggerDirection.Falling);

    /// <summary>Take-profit trigger attached to this order (venues that support attached TP/SL).</summary>
    public double? TakeProfitPrice { get; init; }

    /// <summary>Stop-loss trigger attached to this order (venues that support attached TP/SL).</summary>
    public double? StopLossPrice { get; init; }

    /// <summary>True when the order is a stop / conditional order.</summary>
    public bool IsConditional => Type is OrderType.Stop or OrderType.StopLimit;

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
    public static Order StopMarket(string symbol, Side side, double qty, double stopPrice) => new(symbol, side, qty, OrderType.Stop, stopPrice: stopPrice);
    public static Order StopLimit(string symbol, Side side, double qty, double stopPrice, double limitPrice) => new(symbol, side, qty, OrderType.StopLimit, limitPrice, stopPrice);

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

    /// <summary>
    /// Applies a stock split of <paramref name="ratio"/> new shares per old share (2 for a 2-for-1, 0.1 for a 1-for-10
    /// reverse split): quantity is multiplied and the average price divided, so cost basis and value are unchanged.
    /// </summary>
    public void ApplySplit(double ratio)
    {
        if (!(ratio > 0)) throw new ArgumentOutOfRangeException(nameof(ratio), "Split ratio must be positive.");
        Quantity *= ratio;
        AveragePrice /= ratio;
    }
}
