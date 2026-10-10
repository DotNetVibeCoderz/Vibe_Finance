using Finance.Core.Market;

namespace Finance.MarketData;

/// <summary>A snapshot of the latest market state for one symbol.</summary>
public sealed record MarketSnapshot(
    string Symbol,
    DateTime Time,
    double Last,
    double Bid = double.NaN,
    double Ask = double.NaN,
    double Volume = double.NaN,
    double ChangePercent = double.NaN)
{
    /// <summary>Mid price when both sides are known, otherwise the last price.</summary>
    public double Mid => double.IsNaN(Bid) || double.IsNaN(Ask) ? Last : (Bid + Ask) * 0.5;

    /// <summary>Converts to a Core top-of-book quote (sizes unknown are 0).</summary>
    public Quote ToQuote() => new(Time, double.IsNaN(Bid) ? Last : Bid, 0, double.IsNaN(Ask) ? Last : Ask, 0);
}

/// <summary>Historical bars and latest quotes from one data vendor.</summary>
public interface IMarketDataProvider
{
    /// <summary>Provider name, e.g. "Yahoo".</summary>
    string Name { get; }

    /// <summary>Bars in [<paramref name="start"/>, <paramref name="end"/>], oldest first, UTC timestamps.</summary>
    Task<Bar[]> GetBarsAsync(string symbol, BarInterval interval, DateTime start, DateTime end, CancellationToken cancellationToken = default);

    /// <summary>The latest quote / last trade.</summary>
    Task<MarketSnapshot> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default);
}

/// <summary>A provider that can also return level-2 depth.</summary>
public interface IOrderBookProvider
{
    /// <summary>Order book snapshot with up to <paramref name="depth"/> levels per side.</summary>
    Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default);
}

/// <summary>Raised when a vendor returns an error or an unexpected payload.</summary>
public sealed class MarketDataException : Exception
{
    public MarketDataException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Convenience overloads.</summary>
public static class MarketDataProviderExtensions
{
    /// <summary>Bars for the last <paramref name="lookback"/> up to now.</summary>
    public static Task<Bar[]> GetRecentBarsAsync(this IMarketDataProvider provider, string symbol, BarInterval interval, TimeSpan lookback, CancellationToken cancellationToken = default)
    {
        var end = DateTime.UtcNow;
        return provider.GetBarsAsync(symbol, interval, end - lookback, end, cancellationToken);
    }

    /// <summary>Close prices of the bars.</summary>
    public static double[] Closes(this Bar[] bars)
    {
        var c = new double[bars.Length];
        for (int i = 0; i < bars.Length; i++) c[i] = bars[i].Close;
        return c;
    }
}
