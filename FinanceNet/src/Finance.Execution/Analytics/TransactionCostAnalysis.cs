using Finance.Core.Market;

namespace Finance.Execution.Analytics;

/// <summary>
/// Implementation shortfall decomposition (Perold 1988). All costs are positive when they hurt
/// and expressed in currency; <c>*Bps</c> are relative to the paper portfolio value
/// (decision price × parent quantity).
/// </summary>
public sealed record ShortfallReport(
    double ParentQuantity,
    double FilledQuantity,
    double DecisionPrice,
    double AverageFillPrice,
    double ExecutionCost,
    double OpportunityCost,
    double Fees,
    double TotalShortfall,
    double TotalBps,
    double ExecutionBps,
    double OpportunityBps,
    double FeesBps)
{
    public double FillRate => ParentQuantity > 0 ? FilledQuantity / ParentQuantity : 0;
}

/// <summary>Slippage of an execution against standard benchmarks, in basis points (positive = worse than benchmark).</summary>
public sealed record SlippageReport(double AverageFillPrice, double ArrivalPrice, double IntervalVwap, double VsArrivalBps, double VsVwapBps);

/// <summary>Transaction cost analysis.</summary>
public static class TransactionCostAnalysis
{
    /// <summary>
    /// Implementation shortfall of a parent order.
    /// </summary>
    /// <param name="side">Parent side.</param>
    /// <param name="parentQuantity">Intended quantity at decision time.</param>
    /// <param name="decisionPrice">Price when the decision was made.</param>
    /// <param name="fills">Executions.</param>
    /// <param name="closePrice">Price at the end of the horizon, used to mark the unfilled remainder.</param>
    public static ShortfallReport ImplementationShortfall(Side side, double parentQuantity, double decisionPrice, IReadOnlyList<Fill> fills, double closePrice)
    {
        if (parentQuantity <= 0 || decisionPrice <= 0) throw new ArgumentOutOfRangeException(nameof(parentQuantity));
        double filled = fills.Sum(f => f.Quantity);
        double avg = filled > 0 ? fills.Sum(f => f.Quantity * f.Price) / filled : double.NaN;
        double fees = fills.Sum(f => f.Commission);
        int s = side.Sign();
        double exec = filled > 0 ? s * (avg - decisionPrice) * filled : 0;
        double opp = s * (closePrice - decisionPrice) * Math.Max(0, parentQuantity - filled);
        double paper = decisionPrice * parentQuantity;
        double total = exec + opp + fees;
        return new ShortfallReport(parentQuantity, filled, decisionPrice, avg, exec, opp, fees, total,
            total / paper * 1e4, exec / paper * 1e4, opp / paper * 1e4, fees / paper * 1e4);
    }

    /// <summary>
    /// Slippage versus the arrival price and versus the market VWAP of <paramref name="intervalBars"/>
    /// (the bars covering the execution window; typical price × volume).
    /// </summary>
    public static SlippageReport Slippage(Side side, IReadOnlyList<Fill> fills, double arrivalPrice, IReadOnlyList<Bar> intervalBars)
    {
        double filled = fills.Sum(f => f.Quantity);
        if (filled <= 0) throw new ArgumentException("No fills.");
        double avg = fills.Sum(f => f.Quantity * f.Price) / filled;
        double vol = intervalBars.Sum(b => b.Volume);
        double vwap = vol > 0 ? intervalBars.Sum(b => b.Typical * b.Volume) / vol : double.NaN;
        int s = side.Sign();
        return new SlippageReport(avg, arrivalPrice, vwap, s * (avg / arrivalPrice - 1) * 1e4, double.IsNaN(vwap) ? double.NaN : s * (avg / vwap - 1) * 1e4);
    }

    /// <summary>Square-root market impact estimate: σ · η · √(Q / ADV), as a fraction of price.</summary>
    public static double SquareRootImpact(double quantity, double averageDailyVolume, double dailyVolatility, double eta = 1.0)
        => eta * dailyVolatility * Math.Sqrt(quantity / averageDailyVolume);
}
