using Finance.Core.Market;

namespace Finance.MarketData;

/// <summary>Builds bars from finer bars or ticks.</summary>
public static class BarAggregator
{
    /// <summary>
    /// Resamples bars into buckets of <paramref name="period"/> aligned to the Unix epoch (UTC).
    /// Input must be sorted by time.
    /// </summary>
    public static Bar[] Resample(IReadOnlyList<Bar> bars, TimeSpan period)
    {
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));
        var result = new List<Bar>();
        long ticks = period.Ticks;
        int i = 0;
        while (i < bars.Count)
        {
            long bucket = bars[i].Time.Ticks / ticks;
            var first = bars[i];
            double h = first.High, l = first.Low, v = first.Volume, c = first.Close;
            int j = i + 1;
            while (j < bars.Count && bars[j].Time.Ticks / ticks == bucket)
            {
                h = Math.Max(h, bars[j].High);
                l = Math.Min(l, bars[j].Low);
                v += bars[j].Volume;
                c = bars[j].Close;
                j++;
            }
            result.Add(new Bar(new DateTime(bucket * ticks, first.Time.Kind), first.Open, h, l, c, v));
            i = j;
        }
        return result.ToArray();
    }

    /// <summary>Builds time bars from trade ticks (sorted by time).</summary>
    public static Bar[] FromTicks(IReadOnlyList<Tick> ticks, TimeSpan period)
    {
        var result = new List<Bar>();
        long p = period.Ticks;
        int i = 0;
        while (i < ticks.Count)
        {
            long bucket = ticks[i].Time.Ticks / p;
            double o = ticks[i].Price, h = o, l = o, c = o, v = ticks[i].Size;
            int j = i + 1;
            while (j < ticks.Count && ticks[j].Time.Ticks / p == bucket)
            {
                double px = ticks[j].Price;
                if (px > h) h = px;
                if (px < l) l = px;
                c = px;
                v += ticks[j].Size;
                j++;
            }
            result.Add(new Bar(new DateTime(bucket * p, ticks[i].Time.Kind), o, h, l, c, v));
            i = j;
        }
        return result.ToArray();
    }

    /// <summary>Keeps bars whose time lies in [start, end].</summary>
    public static Bar[] Slice(IEnumerable<Bar> bars, DateTime start, DateTime end)
        => bars.Where(b => b.Time >= start && b.Time <= end).ToArray();
}
