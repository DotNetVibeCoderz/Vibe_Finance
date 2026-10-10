using Finance.Core.Market;
using Finance.Execution.Oms;

namespace Finance.Execution.Brokers;

/// <summary>Account balances.</summary>
public sealed record AccountInfo(string AccountId, string Currency, double Cash, double Equity, double BuyingPower);

/// <summary>A broker or exchange connection.</summary>
public interface IBroker
{
    string Name { get; }

    /// <summary>Raised for every order lifecycle event.</summary>
    event Action<OrderEvent>? OrderUpdated;

    /// <summary>Sends an order; returns it with its status after the venue's immediate response.</summary>
    Task<Order> SubmitAsync(Order order, CancellationToken cancellationToken = default);

    /// <summary>Cancels an open order; false when it was not open.</summary>
    Task<bool> CancelAsync(long orderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken cancellationToken = default);

    Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default);
}

/// <summary>Raised when a broker API returns an error.</summary>
public sealed class BrokerException : Exception
{
    public BrokerException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Commission schedule: per-share + basis points of notional, with a minimum per fill.</summary>
public sealed record CommissionModel(double PerShare = 0, double Bps = 0, double Minimum = 0)
{
    public static CommissionModel Zero { get; } = new();

    /// <summary>Interactive Brokers US tiered-like: $0.005/share, $1 minimum.</summary>
    public static CommissionModel InteractiveBrokers { get; } = new(0.005, 0, 1.0);

    /// <summary>Binance spot taker: 10 bps.</summary>
    public static CommissionModel BinanceSpot { get; } = new(0, 10, 0);

    public double Compute(double quantity, double price)
        => Math.Max(Minimum, PerShare * quantity + Bps * 1e-4 * quantity * price);
}
