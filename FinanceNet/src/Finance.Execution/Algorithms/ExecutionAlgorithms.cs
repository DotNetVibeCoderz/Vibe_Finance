using Finance.Core.Market;

namespace Finance.Execution.Algorithms;

/// <summary>One scheduled child order of a parent execution.</summary>
public readonly record struct ChildOrder(DateTime Time, double Quantity, double? LimitPrice = null)
{
    /// <summary>Creates the Core order for this slice.</summary>
    public Order ToOrder(string symbol, Side side)
        => LimitPrice is { } p ? new Order(symbol, side, Quantity, OrderType.Limit, p) { CreatedAt = Time } : new Order(symbol, side, Quantity) { CreatedAt = Time };
}

/// <summary>
/// Schedulers that split a parent order into child orders. Quantities are rounded to
/// <c>lotSize</c> while the total is preserved exactly (rounding error is carried forward).
/// </summary>
public static class ExecutionAlgorithms
{
    /// <summary>Time-weighted: equal slices at equal intervals over [start, end).</summary>
    public static IReadOnlyList<ChildOrder> Twap(double totalQuantity, DateTime start, DateTime end, int slices, double lotSize = 1, double? limitPrice = null)
    {
        if (slices <= 0) throw new ArgumentOutOfRangeException(nameof(slices));
        var weights = Enumerable.Repeat(1.0, slices).ToArray();
        return Schedule(totalQuantity, start, end, weights, lotSize, limitPrice);
    }

    /// <summary>
    /// Volume-weighted: slice sizes follow the expected intraday volume profile (one weight per bucket,
    /// e.g. the classic U-shape from <see cref="UShapedProfile"/>).
    /// </summary>
    public static IReadOnlyList<ChildOrder> Vwap(double totalQuantity, DateTime start, DateTime end, IReadOnlyList<double> volumeProfile, double lotSize = 1, double? limitPrice = null)
    {
        if (volumeProfile.Count == 0 || volumeProfile.Any(v => v < 0) || volumeProfile.Sum() <= 0)
            throw new ArgumentException("Volume profile must be non-negative with a positive sum.");
        return Schedule(totalQuantity, start, end, volumeProfile, lotSize, limitPrice);
    }

    /// <summary>
    /// Percentage of volume: each bucket trades <paramref name="participation"/> × forecast volume until the
    /// parent is complete. The schedule may finish early or leave a remainder if volume is insufficient
    /// (check the sum).
    /// </summary>
    public static IReadOnlyList<ChildOrder> Pov(double totalQuantity, double participation, DateTime start, TimeSpan bucket, IReadOnlyList<double> forecastVolume, double lotSize = 1)
    {
        if (participation <= 0 || participation > 1) throw new ArgumentOutOfRangeException(nameof(participation));
        var result = new List<ChildOrder>();
        double remaining = totalQuantity, carry = 0;
        for (int i = 0; i < forecastVolume.Count && remaining > 1e-12; i++)
        {
            double target = Math.Min(remaining, participation * forecastVolume[i] + carry);
            double q = RoundLot(target, lotSize);
            carry = target - q;
            if (q <= 0) continue;
            result.Add(new ChildOrder(start + bucket * i, q));
            remaining -= q;
        }
        return result;
    }

    /// <summary>Iceberg: shows only <paramref name="displayQuantity"/> at a time; each refill is a new child at the same limit.</summary>
    public static IReadOnlyList<ChildOrder> Iceberg(double totalQuantity, double displayQuantity, double limitPrice, DateTime start, TimeSpan? refillDelay = null)
    {
        if (displayQuantity <= 0) throw new ArgumentOutOfRangeException(nameof(displayQuantity));
        var result = new List<ChildOrder>();
        double remaining = totalQuantity;
        var t = start;
        while (remaining > 1e-12)
        {
            double q = Math.Min(displayQuantity, remaining);
            result.Add(new ChildOrder(t, q, limitPrice));
            remaining -= q;
            t += refillDelay ?? TimeSpan.Zero;
        }
        return result;
    }

    /// <summary>A U-shaped intraday volume profile (heavy open and close) with <paramref name="buckets"/> buckets, summing to 1.</summary>
    public static double[] UShapedProfile(int buckets, double curvature = 2.5)
    {
        var w = new double[buckets];
        for (int i = 0; i < buckets; i++)
        {
            double x = buckets == 1 ? 0 : 2.0 * i / (buckets - 1) - 1; // -1..1
            w[i] = 1 + curvature * x * x;
        }
        double s = w.Sum();
        for (int i = 0; i < buckets; i++) w[i] /= s;
        return w;
    }

    /// <summary>Volume profile estimated from historical intraday bars: average share of daily volume per time-of-day bucket.</summary>
    public static double[] ProfileFromBars(IEnumerable<Bar> intradayBars, TimeSpan bucket)
    {
        var byDay = intradayBars.GroupBy(b => b.Time.Date).ToList();
        var shares = new Dictionary<long, List<double>>();
        foreach (var day in byDay)
        {
            double total = day.Sum(b => b.Volume);
            if (total <= 0) continue;
            foreach (var g in day.GroupBy(b => b.Time.TimeOfDay.Ticks / bucket.Ticks))
            {
                if (!shares.TryGetValue(g.Key, out var l)) shares[g.Key] = l = new List<double>();
                l.Add(g.Sum(b => b.Volume) / total);
            }
        }
        var prof = shares.OrderBy(kv => kv.Key).Select(kv => kv.Value.Average()).ToArray();
        double s = prof.Sum();
        return s > 0 ? prof.Select(v => v / s).ToArray() : prof;
    }

    private static IReadOnlyList<ChildOrder> Schedule(double total, DateTime start, DateTime end, IReadOnlyList<double> weights, double lotSize, double? limit)
    {
        if (total <= 0) throw new ArgumentOutOfRangeException(nameof(total));
        if (end <= start) throw new ArgumentException("end must be after start.");
        double wsum = weights.Sum();
        var step = TimeSpan.FromTicks((end - start).Ticks / weights.Count);
        var result = new List<ChildOrder>(weights.Count);
        double allocated = 0, carry = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            double target = i == weights.Count - 1 ? total - allocated : total * weights[i] / wsum + carry;
            double q = i == weights.Count - 1 ? Math.Round(target, 10) : RoundLot(target, lotSize);
            carry = target - q;
            if (q <= 0) continue;
            result.Add(new ChildOrder(start + step * i, q, limit));
            allocated += q;
        }
        return result;
    }

    private static double RoundLot(double q, double lot) => lot > 0 ? Math.Floor(q / lot + 1e-9) * lot : q;
}
