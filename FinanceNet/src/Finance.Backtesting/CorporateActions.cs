using Finance.Core.Market;

namespace Finance.Backtesting;

/// <summary>Kind of corporate action.</summary>
public enum CorporateActionKind { Split, CashDividend }

/// <summary>
/// A corporate action effective at the start of <paramref name="ExDate"/>.
/// </summary>
/// <param name="Symbol">Affected symbol.</param>
/// <param name="ExDate">Ex-date: the first bar that trades without the dividend / at the split-adjusted price.</param>
/// <param name="Kind">Split or cash dividend.</param>
/// <param name="Value">Split ratio (new shares per old; 2 = 2-for-1, 0.1 = 1-for-10) or dividend per share.</param>
public sealed record CorporateAction(string Symbol, DateTime ExDate, CorporateActionKind Kind, double Value)
{
    public static CorporateAction Split(string symbol, DateTime exDate, double ratio) => new(symbol, exDate, CorporateActionKind.Split, ratio);
    public static CorporateAction Dividend(string symbol, DateTime exDate, double perShare) => new(symbol, exDate, CorporateActionKind.CashDividend, perShare);
}

/// <summary>A symbol leaving the investable universe; holdings are closed at the last price times (1 + return).</summary>
/// <param name="Symbol">Delisted symbol.</param>
/// <param name="Date">Delisting date: positions are closed at the first engine step on or after it.</param>
/// <param name="DelistingReturn">Return from the last traded price to the delisting payout (−1 = worthless, as in a bankruptcy).</param>
public sealed record Delisting(string Symbol, DateTime Date, double DelistingReturn = 0);

/// <summary>Membership of one symbol in a universe over [From, To].</summary>
public sealed record UniverseMembership(string Symbol, DateTime From, DateTime? To = null);

/// <summary>
/// Point-in-time universe: which symbols were investable on each date. Backtesting only on today's
/// constituents (the survivors) overstates returns; selecting from the universe as it was avoids that bias.
/// </summary>
public sealed class PointInTimeUniverse
{
    private readonly List<UniverseMembership> _members;

    public PointInTimeUniverse(IEnumerable<UniverseMembership> memberships) => _members = memberships.ToList();

    public IReadOnlyList<UniverseMembership> Memberships => _members;

    /// <summary>Whether <paramref name="symbol"/> was in the universe on <paramref name="date"/>.</summary>
    public bool IsMember(string symbol, DateTime date)
        => _members.Any(m => string.Equals(m.Symbol, symbol, StringComparison.Ordinal) && m.From <= date && (m.To is null || date <= m.To));

    /// <summary>All members on <paramref name="date"/>, sorted.</summary>
    public IReadOnlyList<string> Members(DateTime date)
        => _members.Where(m => m.From <= date && (m.To is null || date <= m.To)).Select(m => m.Symbol).Distinct().Order(StringComparer.Ordinal).ToList();
}

/// <summary>Price adjustment for research series.</summary>
public static class CorporateActionAdjuster
{
    /// <summary>
    /// Back-adjusts raw bars so that returns across ex-dates are continuous (CRSP-style): every bar before an
    /// ex-date is multiplied by the action's factor (1/ratio for a split; 1 − D/C₋₁ for a dividend D, C₋₁ being the
    /// close before the ex-date). Volumes are scaled inversely for splits. The latest bars keep their raw prices.
    /// </summary>
    /// <remarks>
    /// Use adjusted bars for indicators and research. In the event-driven engine, feed raw bars and pass the actions
    /// through <see cref="BacktestSettings.CorporateActions"/>, so positions and cash change exactly as at a broker.
    /// </remarks>
    public static Bar[] Adjust(ReadOnlySpan<Bar> raw, IEnumerable<CorporateAction> actions)
    {
        var bars = raw.ToArray();
        var price = Enumerable.Repeat(1.0, bars.Length).ToArray();
        var volume = Enumerable.Repeat(1.0, bars.Length).ToArray();
        foreach (var a in actions)
        {
            int ex = Array.FindIndex(bars, b => b.Time >= a.ExDate);
            if (ex <= 0) continue;
            // Each factor is computed from raw prices, then all factors compound on the earlier bars.
            double pf = a.Kind == CorporateActionKind.Split ? 1 / a.Value : 1 - a.Value / raw[ex - 1].Close;
            double vf = a.Kind == CorporateActionKind.Split ? a.Value : 1;
            for (int i = 0; i < ex; i++) { price[i] *= pf; volume[i] *= vf; }
        }
        for (int i = 0; i < bars.Length; i++)
        {
            var b = bars[i];
            double f = price[i];
            bars[i] = new Bar(b.Time, b.Open * f, b.High * f, b.Low * f, b.Close * f, b.Volume * volume[i]);
        }
        return bars;
    }
}
