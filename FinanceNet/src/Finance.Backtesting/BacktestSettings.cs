using Finance.Core.Market;

namespace Finance.Backtesting;

/// <summary>When market orders are filled.</summary>
public enum FillTiming
{
    /// <summary>Orders placed while handling bar t fill at the open of bar t+1 (no look-ahead; the default).</summary>
    NextBarOpen,
    /// <summary>Market orders placed while handling bar t fill at that bar's close ("cheat-on-close").</summary>
    CurrentBarClose,
}

/// <summary>Engine configuration.</summary>
public sealed record BacktestSettings
{
    /// <summary>Starting cash.</summary>
    public double InitialCash { get; init; } = 100_000;

    public FillTiming FillTiming { get; init; } = FillTiming.NextBarOpen;

    public ICommissionModel Commission { get; init; } = CommissionModels.None;

    public ISlippageModel Slippage { get; init; } = SlippageModels.None;

    /// <summary>Allow short positions. When false, sells are capped at the long position.</summary>
    public bool AllowShort { get; init; }

    /// <summary>Allow buying with borrowed cash. When false, buys are capped at available cash.</summary>
    public bool AllowLeverage { get; init; }

    /// <summary>Allow fractional quantities (crypto); otherwise quantities are whole units.</summary>
    public bool FractionalQuantities { get; init; }

    /// <summary>Bars per year, used to annualise statistics.</summary>
    public double PeriodsPerYear { get; init; } = 252;

    /// <summary>Annual risk-free rate used by the Sharpe and Sortino ratios.</summary>
    public double RiskFreeRate { get; init; }

    /// <summary>
    /// Bars before this time are fed to the strategy (so indicators warm up) but orders are ignored and no
    /// equity is recorded. Used by walk-forward analysis.
    /// </summary>
    public DateTime? StartTradingAt { get; init; }
}

/// <summary>Computes the commission of a fill.</summary>
public interface ICommissionModel
{
    double Calculate(double quantity, double price);
}

/// <summary>Adjusts fill prices and caps fill sizes.</summary>
public interface ISlippageModel
{
    /// <summary>Maximum quantity that can fill on this bar (∞ when unconstrained).</summary>
    double MaxFillQuantity(in Bar bar, double requested);

    /// <summary>Execution price after slippage for a fill of <paramref name="quantity"/> at <paramref name="price"/>.</summary>
    double AdjustPrice(Side side, double price, double quantity, in Bar bar);
}

/// <summary>Built-in commission models.</summary>
public static class CommissionModels
{
    public static ICommissionModel None { get; } = new FixedCommission(0);
    public static ICommissionModel PerShare(double perShare, double minimum = 0) => new PerShareCommission(perShare, minimum);
    public static ICommissionModel Percentage(double rate, double minimum = 0) => new PercentageCommission(rate, minimum);
    public static ICommissionModel Fixed(double amount) => new FixedCommission(amount);
}

/// <summary>A fixed amount per fill.</summary>
public sealed record FixedCommission(double Amount) : ICommissionModel
{
    public double Calculate(double quantity, double price) => quantity > 0 ? Amount : 0;
}

/// <summary>A rate per share/unit with a per-fill minimum.</summary>
public sealed record PerShareCommission(double PerShare, double Minimum = 0) : ICommissionModel
{
    public double Calculate(double quantity, double price) => quantity > 0 ? Math.Max(Minimum, PerShare * quantity) : 0;
}

/// <summary>A fraction of traded notional with a per-fill minimum (e.g. 0.001 = 10 bps).</summary>
public sealed record PercentageCommission(double Rate, double Minimum = 0) : ICommissionModel
{
    public double Calculate(double quantity, double price) => quantity > 0 ? Math.Max(Minimum, Rate * quantity * price) : 0;
}

/// <summary>Built-in slippage models.</summary>
public static class SlippageModels
{
    public static ISlippageModel None { get; } = new FixedBpsSlippage(0);
    public static ISlippageModel FixedBps(double bps) => new FixedBpsSlippage(bps);
    public static ISlippageModel VolumeShare(double volumeLimit = 0.025, double priceImpact = 0.1) => new VolumeShareSlippage(volumeLimit, priceImpact);
}

/// <summary>Moves every fill adversely by a fixed number of basis points.</summary>
public sealed record FixedBpsSlippage(double Bps) : ISlippageModel
{
    public double MaxFillQuantity(in Bar bar, double requested) => requested;

    public double AdjustPrice(Side side, double price, double quantity, in Bar bar)
        => price * (1 + side.Sign() * Bps / 10_000);
}

/// <summary>
/// Zipline's volume-share model: a fill may take at most <see cref="VolumeLimit"/> of the bar's volume, and the
/// price moves by <see cref="PriceImpact"/> × (fill / bar volume)². Bars with zero volume are unconstrained.
/// </summary>
public sealed record VolumeShareSlippage(double VolumeLimit = 0.025, double PriceImpact = 0.1) : ISlippageModel
{
    public double MaxFillQuantity(in Bar bar, double requested)
        => bar.Volume > 0 ? Math.Min(requested, VolumeLimit * bar.Volume) : requested;

    public double AdjustPrice(Side side, double price, double quantity, in Bar bar)
    {
        if (bar.Volume <= 0) return price;
        double share = quantity / bar.Volume;
        return price * (1 + side.Sign() * PriceImpact * share * share);
    }
}
