using Finance.Core.Market;

namespace Finance.TimeSeries.Indicators;

/// <summary>Candlestick pattern recognisers.</summary>
public enum CandlePattern
{
    Doji, DragonflyDoji, GravestoneDoji, LongLeggedDoji, Hammer, InvertedHammer, HangingMan, ShootingStar,
    BullishMarubozu, BearishMarubozu, SpinningTop, BullishEngulfing, BearishEngulfing, BullishHarami, BearishHarami,
    PiercingLine, DarkCloudCover, MorningStar, EveningStar, ThreeWhiteSoldiers, ThreeBlackCrows, TweezerTop,
    TweezerBottom, ThreeInsideUp, ThreeInsideDown, BullishBeltHold, BearishBeltHold, InsideBar, OutsideBar,
    BullishKicker, BearishKicker, RisingThreeMethods, FallingThreeMethods,
    // v0.2 additions (appended so existing values keep their numbers)
    HighWave, Takuri, BullishDojiStar, BearishDojiStar, OnNeck, InNeck, Thrusting, MatchingLow, HomingPigeon,
    BullishSeparatingLines, BearishSeparatingLines, BullishCounterattack, BearishCounterattack,
    ThreeOutsideUp, ThreeOutsideDown, BullishAbandonedBaby, BearishAbandonedBaby, MorningDojiStar, EveningDojiStar,
    TwoCrows, UpsideGapTwoCrows, IdenticalThreeCrows, AdvanceBlock, UpsideTasukiGap, DownsideTasukiGap, StickSandwich,
    ThreeLineStrike,
}

/// <summary>
/// Candlestick pattern detection. Results follow TA-Lib's convention: +100 bullish, -100 bearish,
/// 0 no pattern, marked on the bar that completes the pattern. Trend context (needed by hammer,
/// hanging man, stars) is the sign of close versus its SMA over the 5 preceding bars.
/// </summary>
public static class Candlesticks
{
    private const double DojiBody = 0.1;   // body ≤ 10% of range
    private const double LongShadow = 2.0; // shadow ≥ 2× body

    /// <summary>All recognised patterns.</summary>
    public static IReadOnlyList<CandlePattern> All { get; } = Enum.GetValues<CandlePattern>();

    /// <summary>Scans one pattern over the bars.</summary>
    public static int[] Detect(ReadOnlySpan<Bar> bars, CandlePattern pattern)
    {
        var r = new int[bars.Length];
        for (int i = 0; i < bars.Length; i++) r[i] = At(bars, i, pattern);
        return r;
    }

    /// <summary>Every pattern found, as (index, pattern, signal) tuples in time order.</summary>
    public static List<(int Index, CandlePattern Pattern, int Signal)> Scan(ReadOnlySpan<Bar> bars)
    {
        var hits = new List<(int, CandlePattern, int)>();
        for (int i = 0; i < bars.Length; i++)
            foreach (var p in All)
            {
                int s = At(bars, i, p);
                if (s != 0) hits.Add((i, p, s));
            }
        return hits;
    }

    private static double Body(Bar b) => Math.Abs(b.Close - b.Open);
    private static double Upper(Bar b) => b.High - Math.Max(b.Open, b.Close);
    private static double Lower(Bar b) => Math.Min(b.Open, b.Close) - b.Low;
    private static bool Bull(Bar b) => b.Close > b.Open;
    private static bool Bear(Bar b) => b.Close < b.Open;
    private static double Mid(Bar b) => (b.Open + b.Close) / 2;

    private static double AvgBody(ReadOnlySpan<Bar> bars, int i, int lookback = 10)
    {
        int start = Math.Max(0, i - lookback);
        if (start >= i) return Body(bars[i]);
        double s = 0;
        for (int k = start; k < i; k++) s += Body(bars[k]);
        return s / (i - start);
    }

    /// <summary>+1 up-trend, -1 down-trend, 0 unknown, from the 5 bars before <paramref name="i"/>.</summary>
    private static int Trend(ReadOnlySpan<Bar> bars, int i)
    {
        if (i < 5) return 0;
        double s = 0;
        for (int k = i - 5; k < i; k++) s += bars[k].Close;
        double sma = s / 5, c = bars[i - 1].Close;
        return c > sma ? 1 : c < sma ? -1 : 0;
    }

    private static bool IsDoji(Bar b) => b.Range > 0 && Body(b) <= DojiBody * b.Range;
    private static bool IsLong(ReadOnlySpan<Bar> bars, int i) => Body(bars[i]) > AvgBody(bars, i);

    /// <summary>Prices within 5% of the larger of the two bars' ranges count as "equal".</summary>
    private static bool Near(double a, double b, Bar x, Bar y) => Math.Abs(a - b) <= 0.05 * Math.Max(x.Range, y.Range);
    private static double BodyTop(Bar b) => Math.Max(b.Open, b.Close);
    private static double BodyBottom(Bar b) => Math.Min(b.Open, b.Close);

    /// <summary>Signal of <paramref name="pattern"/> completing at bar <paramref name="i"/>.</summary>
    public static int At(ReadOnlySpan<Bar> bars, int i, CandlePattern pattern)
    {
        var b = bars[i];
        double range = b.Range, body = Body(b);
        switch (pattern)
        {
            case CandlePattern.Doji:
                return IsDoji(b) ? 100 : 0;
            case CandlePattern.DragonflyDoji:
                return IsDoji(b) && Upper(b) <= 0.1 * range && Lower(b) >= 0.6 * range ? 100 : 0;
            case CandlePattern.GravestoneDoji:
                return IsDoji(b) && Lower(b) <= 0.1 * range && Upper(b) >= 0.6 * range ? -100 : 0;
            case CandlePattern.LongLeggedDoji:
                return IsDoji(b) && Upper(b) >= 0.3 * range && Lower(b) >= 0.3 * range ? 100 : 0;
            case CandlePattern.Hammer:
                return body > 0 && Lower(b) >= LongShadow * body && Upper(b) <= 0.3 * body + 0.05 * range && Trend(bars, i) < 0 ? 100 : 0;
            case CandlePattern.HangingMan:
                return body > 0 && Lower(b) >= LongShadow * body && Upper(b) <= 0.3 * body + 0.05 * range && Trend(bars, i) > 0 ? -100 : 0;
            case CandlePattern.InvertedHammer:
                return body > 0 && Upper(b) >= LongShadow * body && Lower(b) <= 0.3 * body + 0.05 * range && Trend(bars, i) < 0 ? 100 : 0;
            case CandlePattern.ShootingStar:
                return body > 0 && Upper(b) >= LongShadow * body && Lower(b) <= 0.3 * body + 0.05 * range && Trend(bars, i) > 0 ? -100 : 0;
            case CandlePattern.BullishMarubozu:
                return Bull(b) && range > 0 && body >= 0.95 * range && IsLong(bars, i) ? 100 : 0;
            case CandlePattern.BearishMarubozu:
                return Bear(b) && range > 0 && body >= 0.95 * range && IsLong(bars, i) ? -100 : 0;
            case CandlePattern.SpinningTop:
                return range > 0 && body > DojiBody * range && body <= 0.3 * range && Upper(b) > body && Lower(b) > body ? (Bull(b) ? 100 : -100) : 0;
            case CandlePattern.HighWave:
                return body > 0 && body <= 0.3 * range && Upper(b) >= 3 * body && Lower(b) >= 3 * body ? (Bull(b) ? 100 : -100) : 0;
            case CandlePattern.Takuri:
                return IsDoji(b) && Upper(b) <= 0.1 * range && Lower(b) >= 0.75 * range && Lower(b) >= 3 * body ? 100 : 0;
        }

        if (i < 1) return 0;
        var p = bars[i - 1];
        switch (pattern)
        {
            case CandlePattern.BullishEngulfing:
                return Bear(p) && Bull(b) && b.Open <= p.Close && b.Close >= p.Open && body > Body(p) ? 100 : 0;
            case CandlePattern.BearishEngulfing:
                return Bull(p) && Bear(b) && b.Open >= p.Close && b.Close <= p.Open && body > Body(p) ? -100 : 0;
            case CandlePattern.BullishHarami:
                return Bear(p) && IsLong(bars, i - 1) && Bull(b) && b.Open > p.Close && b.Close < p.Open ? 100 : 0;
            case CandlePattern.BearishHarami:
                return Bull(p) && IsLong(bars, i - 1) && Bear(b) && b.Open < p.Close && b.Close > p.Open ? -100 : 0;
            case CandlePattern.PiercingLine:
                return Bear(p) && IsLong(bars, i - 1) && Bull(b) && b.Open < p.Low && b.Close > Mid(p) && b.Close < p.Open ? 100 : 0;
            case CandlePattern.DarkCloudCover:
                return Bull(p) && IsLong(bars, i - 1) && Bear(b) && b.Open > p.High && b.Close < Mid(p) && b.Close > p.Open ? -100 : 0;
            case CandlePattern.TweezerTop:
                return Trend(bars, i - 1) > 0 && Bull(p) && Bear(b) && Math.Abs(b.High - p.High) <= 0.05 * Math.Max(range, p.Range) ? -100 : 0;
            case CandlePattern.TweezerBottom:
                return Trend(bars, i - 1) < 0 && Bear(p) && Bull(b) && Math.Abs(b.Low - p.Low) <= 0.05 * Math.Max(range, p.Range) ? 100 : 0;
            case CandlePattern.BullishBeltHold:
                return Trend(bars, i) < 0 && Bull(b) && IsLong(bars, i) && Lower(b) <= 0.05 * range && b.Open < p.Low ? 100 : 0;
            case CandlePattern.BearishBeltHold:
                return Trend(bars, i) > 0 && Bear(b) && IsLong(bars, i) && Upper(b) <= 0.05 * range && b.Open > p.High ? -100 : 0;
            case CandlePattern.InsideBar:
                return b.High < p.High && b.Low > p.Low ? (Bull(b) ? 100 : -100) : 0;
            case CandlePattern.OutsideBar:
                return b.High > p.High && b.Low < p.Low ? (Bull(b) ? 100 : -100) : 0;
            case CandlePattern.BullishKicker:
                return Bear(p) && Bull(b) && b.Open > p.Open && IsLong(bars, i) && IsLong(bars, i - 1) ? 100 : 0;
            case CandlePattern.BearishKicker:
                return Bull(p) && Bear(b) && b.Open < p.Open && IsLong(bars, i) && IsLong(bars, i - 1) ? -100 : 0;
            case CandlePattern.BullishDojiStar:
                return Bear(p) && IsLong(bars, i - 1) && IsDoji(b) && BodyTop(b) < p.Close ? 100 : 0;
            case CandlePattern.BearishDojiStar:
                return Bull(p) && IsLong(bars, i - 1) && IsDoji(b) && BodyBottom(b) > p.Close ? -100 : 0;
            case CandlePattern.OnNeck:
                return Bear(p) && IsLong(bars, i - 1) && Bull(b) && b.Open < p.Low && Near(b.Close, p.Low, b, p) ? -100 : 0;
            case CandlePattern.InNeck:
                return Bear(p) && IsLong(bars, i - 1) && Bull(b) && b.Open < p.Low && b.Close >= p.Close && b.Close <= p.Close + 0.1 * Body(p) ? -100 : 0;
            case CandlePattern.Thrusting:
                return Bear(p) && IsLong(bars, i - 1) && Bull(b) && b.Open < p.Low && b.Close > p.Close + 0.1 * Body(p) && b.Close < Mid(p) ? -100 : 0;
            case CandlePattern.MatchingLow:
                return Trend(bars, i - 1) < 0 && Bear(p) && IsLong(bars, i - 1) && Bear(b) && Near(b.Close, p.Close, b, p) ? 100 : 0;
            case CandlePattern.HomingPigeon:
                return Trend(bars, i - 1) < 0 && Bear(p) && IsLong(bars, i - 1) && Bear(b) && b.Open < p.Open && b.Close > p.Close ? 100 : 0;
            case CandlePattern.BullishSeparatingLines:
                return Trend(bars, i - 1) > 0 && Bear(p) && Bull(b) && IsLong(bars, i) && Near(b.Open, p.Open, b, p) && Lower(b) <= 0.05 * range ? 100 : 0;
            case CandlePattern.BearishSeparatingLines:
                return Trend(bars, i - 1) < 0 && Bull(p) && Bear(b) && IsLong(bars, i) && Near(b.Open, p.Open, b, p) && Upper(b) <= 0.05 * range ? -100 : 0;
            case CandlePattern.BullishCounterattack:
                return Bear(p) && IsLong(bars, i - 1) && Bull(b) && IsLong(bars, i) && b.Open < p.Close && Near(b.Close, p.Close, b, p) ? 100 : 0;
            case CandlePattern.BearishCounterattack:
                return Bull(p) && IsLong(bars, i - 1) && Bear(b) && IsLong(bars, i) && b.Open > p.Close && Near(b.Close, p.Close, b, p) ? -100 : 0;
        }

        if (i < 2) return 0;
        var q = bars[i - 2];
        switch (pattern)
        {
            case CandlePattern.MorningStar:
                return Bear(q) && IsLong(bars, i - 2) && Body(p) <= 0.3 * Body(q) && Math.Max(p.Open, p.Close) < q.Close
                    && Bull(b) && b.Close > Mid(q) ? 100 : 0;
            case CandlePattern.EveningStar:
                return Bull(q) && IsLong(bars, i - 2) && Body(p) <= 0.3 * Body(q) && Math.Min(p.Open, p.Close) > q.Close
                    && Bear(b) && b.Close < Mid(q) ? -100 : 0;
            case CandlePattern.ThreeWhiteSoldiers:
                return Bull(q) && Bull(p) && Bull(b) && p.Close > q.Close && b.Close > p.Close
                    && p.Open > q.Open && p.Open < q.Close && b.Open > p.Open && b.Open < p.Close
                    && Upper(b) <= 0.3 * body && Upper(p) <= 0.3 * Body(p) ? 100 : 0;
            case CandlePattern.ThreeBlackCrows:
                return Bear(q) && Bear(p) && Bear(b) && p.Close < q.Close && b.Close < p.Close
                    && p.Open < q.Open && p.Open > q.Close && b.Open < p.Open && b.Open > p.Close
                    && Lower(b) <= 0.3 * body && Lower(p) <= 0.3 * Body(p) ? -100 : 0;
            case CandlePattern.ThreeInsideUp:
                return Bear(q) && IsLong(bars, i - 2) && Bull(p) && p.Open > q.Close && p.Close < q.Open && Bull(b) && b.Close > q.Open ? 100 : 0;
            case CandlePattern.ThreeInsideDown:
                return Bull(q) && IsLong(bars, i - 2) && Bear(p) && p.Open < q.Close && p.Close > q.Open && Bear(b) && b.Close < q.Open ? -100 : 0;
            case CandlePattern.ThreeOutsideUp:
                return Bear(q) && Bull(p) && p.Open <= q.Close && p.Close >= q.Open && Body(p) > Body(q) && b.Close > p.Close ? 100 : 0;
            case CandlePattern.ThreeOutsideDown:
                return Bull(q) && Bear(p) && p.Open >= q.Close && p.Close <= q.Open && Body(p) > Body(q) && b.Close < p.Close ? -100 : 0;
            case CandlePattern.BullishAbandonedBaby:
                return Bear(q) && IsLong(bars, i - 2) && IsDoji(p) && p.High < q.Low && Bull(b) && b.Low > p.High && b.Close > Mid(q) ? 100 : 0;
            case CandlePattern.BearishAbandonedBaby:
                return Bull(q) && IsLong(bars, i - 2) && IsDoji(p) && p.Low > q.High && Bear(b) && b.High < p.Low && b.Close < Mid(q) ? -100 : 0;
            case CandlePattern.MorningDojiStar:
                return Bear(q) && IsLong(bars, i - 2) && IsDoji(p) && BodyTop(p) < q.Close && Bull(b) && b.Close > Mid(q) ? 100 : 0;
            case CandlePattern.EveningDojiStar:
                return Bull(q) && IsLong(bars, i - 2) && IsDoji(p) && BodyBottom(p) > q.Close && Bear(b) && b.Close < Mid(q) ? -100 : 0;
            case CandlePattern.TwoCrows:
                return Trend(bars, i - 2) > 0 && Bull(q) && IsLong(bars, i - 2) && Bear(p) && BodyBottom(p) > q.Close
                    && Bear(b) && b.Open < p.Open && b.Open > p.Close && b.Close > q.Open && b.Close < q.Close ? -100 : 0;
            case CandlePattern.UpsideGapTwoCrows:
                return Trend(bars, i - 2) > 0 && Bull(q) && IsLong(bars, i - 2) && Bear(p) && BodyBottom(p) > q.Close
                    && Bear(b) && b.Open > p.Open && b.Close < p.Close && b.Close > q.Close ? -100 : 0;
            case CandlePattern.IdenticalThreeCrows:
                return Bear(q) && Bear(p) && Bear(b) && IsLong(bars, i - 2) && p.Close < q.Close && b.Close < p.Close
                    && Near(p.Open, q.Close, p, q) && Near(b.Open, p.Close, b, p) ? -100 : 0;
            case CandlePattern.AdvanceBlock:
                return Trend(bars, i - 2) > 0 && Bull(q) && Bull(p) && Bull(b) && p.Close > q.Close && b.Close > p.Close
                    && p.Open > q.Open && p.Open <= q.Close && b.Open > p.Open && b.Open <= p.Close
                    && Body(p) < Body(q) && body < Body(p) && Upper(b) > Upper(q) ? -100 : 0;
            case CandlePattern.UpsideTasukiGap:
                return Bull(q) && Bull(p) && p.Open > q.High && Bear(b) && b.Open > p.Open && b.Open < p.Close
                    && b.Close < p.Open && b.Close > q.High ? 100 : 0;
            case CandlePattern.DownsideTasukiGap:
                return Bear(q) && Bear(p) && p.Open < q.Low && Bull(b) && b.Open < p.Open && b.Open > p.Close
                    && b.Close > p.Open && b.Close < q.Low ? -100 : 0;
            case CandlePattern.StickSandwich:
                return Bear(q) && Bull(p) && p.Low > q.Close && Bear(b) && Near(b.Close, q.Close, b, q) ? 100 : 0;
        }

        if (i < 3) return 0;
        if (pattern == CandlePattern.ThreeLineStrike)
        {
            // Three same-coloured candles stepping one way, then one opposite candle that wipes them out.
            // TA-Lib convention: the sign is the colour of the first three.
            Bar a = bars[i - 3];
            if (Bull(a) && Bull(q) && Bull(p) && q.Close > a.Close && p.Close > q.Close && Bear(b) && b.Open > p.Close && b.Close < a.Open) return 100;
            if (Bear(a) && Bear(q) && Bear(p) && q.Close < a.Close && p.Close < q.Close && Bull(b) && b.Open < p.Close && b.Close > a.Open) return -100;
            return 0;
        }

        if (i < 4) return 0;
        var first = bars[i - 4];
        switch (pattern)
        {
            case CandlePattern.RisingThreeMethods:
            {
                if (!(Bull(first) && IsLong(bars, i - 4) && Bull(b) && IsLong(bars, i) && b.Close > first.Close)) return 0;
                for (int k = i - 3; k < i; k++)
                    if (bars[k].High > first.High || bars[k].Low < first.Low || Body(bars[k]) >= Body(first)) return 0;
                return 100;
            }
            case CandlePattern.FallingThreeMethods:
            {
                if (!(Bear(first) && IsLong(bars, i - 4) && Bear(b) && IsLong(bars, i) && b.Close < first.Close)) return 0;
                for (int k = i - 3; k < i; k++)
                    if (bars[k].High > first.High || bars[k].Low < first.Low || Body(bars[k]) >= Body(first)) return 0;
                return -100;
            }
        }
        return 0;
    }
}
