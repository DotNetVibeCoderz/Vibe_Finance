using Finance.Core.Market;

namespace Finance.MarketData.Providers;

/// <summary>
/// Indonesia Stock Exchange (IDX / BEI) market data. IDX publishes no free public API, so prices come from a
/// vendor that carries IDX listings, by default Yahoo Finance ("BBCA" → "BBCA.JK", "IHSG" → "^JKSE",
/// "LQ45" → "^JKLQ45"). Any <see cref="IMarketDataProvider"/> that understands the ".JK" convention can be passed
/// instead. Exchange rules for order sizing and validation are in <see cref="IdxRules"/>.
/// </summary>
public sealed class IdxProvider : IMarketDataProvider
{
    private readonly IMarketDataProvider _vendor;

    public IdxProvider(IMarketDataProvider? vendor = null) => _vendor = vendor ?? new YahooFinanceProvider();

    public string Name => $"IDX ({_vendor.Name})";

    /// <summary>Maps an IDX code or index name to the vendor symbol.</summary>
    public static string VendorSymbol(string symbol)
    {
        var s = symbol.Trim().ToUpperInvariant();
        return s switch
        {
            "IHSG" or "JCI" or "COMPOSITE" or "COMPOSITE INDEX" => "^JKSE",
            "LQ45" => "^JKLQ45",
            _ when s.StartsWith('^') || s.EndsWith(".JK", StringComparison.Ordinal) => s,
            _ => s + ".JK",
        };
    }

    public Task<Bar[]> GetBarsAsync(string symbol, BarInterval interval, DateTime start, DateTime end, CancellationToken cancellationToken = default)
        => _vendor.GetBarsAsync(VendorSymbol(symbol), interval, start, end, cancellationToken);

    public async Task<MarketSnapshot> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
        => (await _vendor.GetQuoteAsync(VendorSymbol(symbol), cancellationToken).ConfigureAwait(false)) with { Symbol = symbol.ToUpperInvariant() };
}

/// <summary>
/// IDX equity trading rules: board lot, price fractions (tick sizes) and auto-rejection limits. The tick table is
/// the one in force since 2016; auto-rejection percentages have been changed several times by IDX (notably the
/// asymmetric lower limits of 2020-2025), so they are parameters with the symmetric regular-board bands as
/// defaults. Check the current IDX circular before relying on them for order validation.
/// </summary>
public static class IdxRules
{
    /// <summary>Shares per lot on the regular market.</summary>
    public const int LotSize = 100;

    /// <summary>Price fraction (tick size) in rupiah for a price level.</summary>
    public static int TickSize(double price) => price switch
    {
        < 200 => 1,
        < 500 => 2,
        < 2000 => 5,
        < 5000 => 10,
        _ => 25,
    };

    /// <summary>Rounds a price to the nearest valid tick (down for bids, up for offers when <paramref name="side"/> is given).</summary>
    public static double RoundToTick(double price, Side? side = null)
    {
        int tick = TickSize(price);
        double steps = price / tick;
        double rounded = side switch
        {
            Side.Buy => Math.Floor(steps + 1e-9),
            Side.Sell => Math.Ceiling(steps - 1e-9),
            _ => Math.Round(steps, MidpointRounding.AwayFromZero),
        } * tick;
        // A price that crosses a band boundary must use the tick of its new band.
        return TickSize(rounded) == tick ? rounded : RoundToTick(rounded, side);
    }

    /// <summary>Whether <paramref name="price"/> is on the tick grid.</summary>
    public static bool IsValidPrice(double price) => price > 0 && Math.Abs(price / TickSize(price) - Math.Round(price / TickSize(price))) < 1e-9;

    /// <summary>Whole lots for a share quantity (rounded down).</summary>
    public static long Lots(double shares) => (long)Math.Floor(shares / LotSize + 1e-9);

    /// <summary>
    /// Upper (ARA) and lower (ARB) auto-rejection prices around the reference (previous close), on the tick grid.
    /// Default upper bands: 35% for prices 50-200, 25% for 200-5,000, 20% above 5,000. <paramref name="lowerPercent"/>
    /// overrides the lower band (e.g. 0.15 for a flat 15% ARB); by default it mirrors the upper band.
    /// </summary>
    public static (double Lower, double Upper) AutoRejection(double referencePrice, double? lowerPercent = null)
    {
        double up = referencePrice switch
        {
            <= 200 => 0.35,
            <= 5000 => 0.25,
            _ => 0.20,
        };
        double lo = lowerPercent ?? up;
        double upper = RoundToTick(referencePrice * (1 + up), Side.Buy);
        double lower = Math.Max(50, RoundToTick(referencePrice * (1 - lo), Side.Sell));   // regular-board floor of Rp50
        return (lower, upper);
    }
}
