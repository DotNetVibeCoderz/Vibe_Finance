using Finance.Core.Market;

namespace Finance.Execution.Routing;

/// <summary>A trading venue with its current book and taker fee.</summary>
public sealed record Venue(string Name, OrderBook Book, double TakerFeeBps = 0);

/// <summary>Quantity sent to one venue.</summary>
public sealed record VenueAllocation(string Venue, double Quantity, double AveragePrice, double Fees)
{
    /// <summary>Price including fees per unit.</summary>
    public double AllInPrice(Side side) => AveragePrice + side.Sign() * Fees / Quantity;
}

/// <summary>A routing decision.</summary>
public sealed record RoutingPlan(Side Side, double RequestedQuantity, IReadOnlyList<VenueAllocation> Allocations)
{
    public double FilledQuantity => Allocations.Sum(a => a.Quantity);
    public double TotalFees => Allocations.Sum(a => a.Fees);
    public double AveragePrice => FilledQuantity > 0 ? Allocations.Sum(a => a.AveragePrice * a.Quantity) / FilledQuantity : double.NaN;

    /// <summary>Average all-in price (fees included) — what the router minimises for buys / maximises for sells.</summary>
    public double AllInPrice => FilledQuantity > 0 ? (Allocations.Sum(a => a.AveragePrice * a.Quantity) + Side.Sign() * TotalFees) / FilledQuantity : double.NaN;

    public bool IsComplete => FilledQuantity >= RequestedQuantity - 1e-9;
}

/// <summary>
/// Smart order router: merges the books of several venues, ranks every level by its fee-adjusted
/// price and takes liquidity greedily from the best, which is optimal for a taker order when each
/// level's cost is linear.
/// </summary>
public static class SmartOrderRouter
{
    public static RoutingPlan Route(Side side, double quantity, IReadOnlyList<Venue> venues)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        var levels = venues.SelectMany(v =>
            (side == Side.Buy ? v.Book.Asks : v.Book.Bids).Select(l => (Venue: v, l.Price, l.Size, AllIn: l.Price * (1 + side.Sign() * v.TakerFeeBps * 1e-4))));
        var ordered = side == Side.Buy ? levels.OrderBy(l => l.AllIn) : levels.OrderByDescending(l => l.AllIn);

        var acc = new Dictionary<string, (double Qty, double Notional, double Fees)>();
        double remaining = quantity;
        foreach (var l in ordered)
        {
            if (remaining <= 1e-12) break;
            double take = Math.Min(remaining, l.Size);
            var cur = acc.GetValueOrDefault(l.Venue.Name);
            acc[l.Venue.Name] = (cur.Qty + take, cur.Notional + take * l.Price, cur.Fees + take * l.Price * l.Venue.TakerFeeBps * 1e-4);
            remaining -= take;
        }
        var allocations = acc.Select(kv => new VenueAllocation(kv.Key, kv.Value.Qty, kv.Value.Notional / kv.Value.Qty, kv.Value.Fees))
            .OrderByDescending(a => a.Quantity).ToList();
        return new RoutingPlan(side, quantity, allocations);
    }

    /// <summary>Cost of sending everything to a single venue (for comparison with <see cref="Route"/>).</summary>
    public static RoutingPlan SingleVenue(Side side, double quantity, Venue venue) => Route(side, quantity, new[] { venue });
}
