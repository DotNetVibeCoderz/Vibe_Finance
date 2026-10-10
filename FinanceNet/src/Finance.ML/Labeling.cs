using Finance.Core.Market;
using Finance.Mathematics;

namespace Finance.ML;

/// <summary>Outcome of a triple-barrier label.</summary>
public readonly record struct BarrierLabel(int StartIndex, int EndIndex, int Label, double Return, BarrierHit Hit);

/// <summary>Which barrier ended the event.</summary>
public enum BarrierHit { Upper, Lower, Vertical }

/// <summary>Labelling methods for supervised learning on prices.</summary>
public static class Labeling
{
    /// <summary>
    /// Fixed-horizon labels: +1 if the forward return over <paramref name="horizon"/> bars exceeds
    /// <paramref name="threshold"/>, -1 if below -threshold, otherwise 0. The last bars get 0 and are excluded via <c>valid</c>.
    /// </summary>
    public static (int[] Labels, bool[] Valid) FixedHorizon(ReadOnlySpan<double> close, int horizon, double threshold = 0)
    {
        var labels = new int[close.Length];
        var valid = new bool[close.Length];
        for (int i = 0; i + horizon < close.Length; i++)
        {
            double r = close[i + horizon] / close[i] - 1;
            labels[i] = r > threshold ? 1 : r < -threshold ? -1 : 0;
            valid[i] = true;
        }
        return (labels, valid);
    }

    /// <summary>
    /// Triple-barrier method (AFML ch. 3). For each event start, the upper/lower barriers sit at
    /// ±multiplier × rolling volatility of log returns; the vertical barrier is <paramref name="maxHolding"/> bars later.
    /// The label is the sign of the return at the first barrier touched (0 on a vertical hit when <paramref name="zeroOnVertical"/>).
    /// </summary>
    public static List<BarrierLabel> TripleBarrier(ReadOnlySpan<double> close, int maxHolding = 10, double upperMultiplier = 2,
        double lowerMultiplier = 2, int volatilityWindow = 20, bool zeroOnVertical = false, IReadOnlyList<int>? eventStarts = null)
    {
        int n = close.Length;
        var logRet = new double[n];
        for (int i = 1; i < n; i++) logRet[i] = Math.Log(close[i] / close[i - 1]);
        var result = new List<BarrierLabel>();
        IEnumerable<int> starts = eventStarts ?? Enumerable.Range(volatilityWindow, Math.Max(0, n - volatilityWindow - 1));
        foreach (int s in starts)
        {
            if (s < volatilityWindow || s >= n - 1) continue;
            double vol = Stats.StdDev(logRet.AsSpan(s - volatilityWindow + 1, volatilityWindow));
            if (!(vol > 0)) continue;
            double up = upperMultiplier * vol, down = -lowerMultiplier * vol;
            int end = Math.Min(n - 1, s + maxHolding);
            var hit = BarrierHit.Vertical;
            int endIdx = end;
            for (int t = s + 1; t <= end; t++)
            {
                double r = Math.Log(close[t] / close[s]);
                if (upperMultiplier > 0 && r >= up) { hit = BarrierHit.Upper; endIdx = t; break; }
                if (lowerMultiplier > 0 && r <= down) { hit = BarrierHit.Lower; endIdx = t; break; }
            }
            double ret = close[endIdx] / close[s] - 1;
            int label = hit switch
            {
                BarrierHit.Upper => 1,
                BarrierHit.Lower => -1,
                _ => zeroOnVertical ? 0 : Math.Sign(ret),
            };
            result.Add(new BarrierLabel(s, endIdx, label, ret, hit));
        }
        return result;
    }

    /// <summary>
    /// Meta-labels: given a primary model's side (+1/-1, 0 = no bet) per event, returns 1 when
    /// following that side was profitable, else 0. A secondary classifier then learns bet sizing.
    /// </summary>
    public static int[] MetaLabels(IReadOnlyList<BarrierLabel> events, IReadOnlyList<int> primarySide)
    {
        if (events.Count != primarySide.Count) throw new ArgumentException("One primary side per event is required.");
        var m = new int[events.Count];
        for (int i = 0; i < events.Count; i++) m[i] = primarySide[i] != 0 && primarySide[i] * events[i].Return > 0 ? 1 : 0;
        return m;
    }

    /// <summary>Convenience overload over bars.</summary>
    public static List<BarrierLabel> TripleBarrier(ReadOnlySpan<Bar> bars, int maxHolding = 10, double multiplier = 2, int volatilityWindow = 20)
    {
        var c = new double[bars.Length];
        for (int i = 0; i < c.Length; i++) c[i] = bars[i].Close;
        return TripleBarrier(c, maxHolding, multiplier, multiplier, volatilityWindow);
    }
}
