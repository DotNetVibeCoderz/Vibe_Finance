using Finance.Core.Market;

namespace Finance.Execution.Risk;

/// <summary>Pre-trade limits. Leave a limit at its default (infinity) to disable it.</summary>
public sealed record RiskLimits
{
    /// <summary>Maximum notional (quantity × price) of a single order.</summary>
    public double MaxOrderNotional { get; init; } = double.PositiveInfinity;

    /// <summary>Maximum quantity of a single order.</summary>
    public double MaxOrderQuantity { get; init; } = double.PositiveInfinity;

    /// <summary>Maximum absolute position per symbol after the order fills.</summary>
    public double MaxPosition { get; init; } = double.PositiveInfinity;

    /// <summary>Fat-finger band: max |limit − reference| / reference (e.g. 0.05 = 5%).</summary>
    public double MaxPriceDeviation { get; init; } = double.PositiveInfinity;

    /// <summary>Symbols that may not be traded.</summary>
    public IReadOnlySet<string> Restricted { get; init; } = new HashSet<string>();
}

/// <summary>Outcome of a pre-trade check.</summary>
public sealed record RiskCheckResult(bool Passed, IReadOnlyList<string> Violations)
{
    public static RiskCheckResult Ok { get; } = new(true, Array.Empty<string>());

    public override string ToString() => Passed ? "OK" : string.Join("; ", Violations);
}

/// <summary>Validates orders against <see cref="RiskLimits"/> before they leave the building.</summary>
public sealed class PreTradeRiskChecker
{
    public PreTradeRiskChecker(RiskLimits limits) => Limits = limits;

    public RiskLimits Limits { get; }

    /// <param name="order">The order to check.</param>
    /// <param name="referencePrice">Last/mid price used for notional and the price band.</param>
    /// <param name="currentPosition">Signed current position in the symbol.</param>
    public RiskCheckResult Check(Order order, double referencePrice, double currentPosition = 0)
    {
        var v = new List<string>();
        if (Limits.Restricted.Contains(order.Symbol)) v.Add($"{order.Symbol} is restricted");
        double px = order.LimitPrice ?? referencePrice;
        double notional = order.Quantity * px;
        if (notional > Limits.MaxOrderNotional) v.Add($"Notional {notional:N0} exceeds {Limits.MaxOrderNotional:N0}");
        if (order.Quantity > Limits.MaxOrderQuantity) v.Add($"Quantity {order.Quantity} exceeds {Limits.MaxOrderQuantity}");
        double after = currentPosition + order.Side.Sign() * order.Quantity;
        if (Math.Abs(after) > Limits.MaxPosition) v.Add($"Position {after} would exceed {Limits.MaxPosition}");
        if (order.LimitPrice is { } lp && referencePrice > 0)
        {
            double dev = Math.Abs(lp - referencePrice) / referencePrice;
            if (dev > Limits.MaxPriceDeviation) v.Add($"Limit {lp} is {dev:P1} away from reference {referencePrice}");
        }
        return v.Count == 0 ? RiskCheckResult.Ok : new RiskCheckResult(false, v);
    }
}
