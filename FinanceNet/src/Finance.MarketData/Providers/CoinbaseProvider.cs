using System.Text.Json;
using Finance.Core.Market;
using Finance.MarketData.Internal;

namespace Finance.MarketData.Providers;

/// <summary>
/// Coinbase Exchange public REST API (no key). Candles come back newest first as
/// <c>[time, low, high, open, close, volume]</c> and at most 300 per request, so ranges are paged.
/// Coinbase has no 30-minute, 4-hour, weekly or monthly granularity; those are resampled.
/// </summary>
public sealed class CoinbaseProvider : IMarketDataProvider, IOrderBookProvider
{
    private const int MaxCandles = 300;
    private readonly HttpClient _http;

    public CoinbaseProvider(HttpClient? http = null, string baseUrl = "https://api.exchange.coinbase.com")
    {
        _http = http ?? Http.Default;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string Name => "Coinbase";

    public string BaseUrl { get; }

    /// <summary>"BTCUSD", "btc/usd" become "BTC-USD" (assumes a 3-letter quote currency when there is no separator).</summary>
    public static string NormalizeSymbol(string symbol)
    {
        var s = symbol.Replace("/", "-").Replace("_", "-").ToUpperInvariant();
        if (!s.Contains('-') && s.Length > 3) s = s[..^3] + "-" + s[^3..];
        return s;
    }

    public async Task<Bar[]> GetBarsAsync(string symbol, BarInterval interval, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        var (granularity, resample) = Granularity(interval);
        var product = NormalizeSymbol(symbol);
        var all = new List<Bar>();
        var step = TimeSpan.FromSeconds((double)granularity * MaxCandles);
        for (var from = start; from < end; from += step)
        {
            var to = from + step < end ? from + step : end;
            var url = $"{BaseUrl}/products/{product}/candles?granularity={granularity}&start={from.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}&end={to.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}";
            using var doc = await Http.GetJsonAsync(_http, url, Name, cancellationToken).ConfigureAwait(false);
            all.AddRange(ParseCandles(doc.RootElement));
        }
        var bars = all.GroupBy(b => b.Time).Select(g => g.First()).OrderBy(b => b.Time).ToArray();
        return resample is { } p ? BarAggregator.Resample(bars, p) : bars;
    }

    public async Task<MarketSnapshot> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        using var doc = await Http.GetJsonAsync(_http, $"{BaseUrl}/products/{NormalizeSymbol(symbol)}/ticker", Name, cancellationToken).ConfigureAwait(false);
        return ParseTicker(doc.RootElement, symbol);
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        using var doc = await Http.GetJsonAsync(_http, $"{BaseUrl}/products/{NormalizeSymbol(symbol)}/book?level=2", Name, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        CheckError(root);
        var book = new OrderBook(symbol, DateTime.UtcNow);
        book.SetBids(BinanceProvider.Levels(root.GetProperty("bids"), depth));
        book.SetAsks(BinanceProvider.Levels(root.GetProperty("asks"), depth));
        return book;
    }

    /// <summary>Parses a candles payload (any order) into bars sorted oldest first.</summary>
    public static Bar[] ParseCandles(JsonElement root)
    {
        CheckError(root);
        return root.EnumerateArray()
            .Select(c => new Bar(Http.FromUnixSeconds(c[0].GetInt64()), Http.Num(c[3]), Http.Num(c[2]), Http.Num(c[1]), Http.Num(c[4]), Http.Num(c[5])))
            .OrderBy(b => b.Time)
            .ToArray();
    }

    /// <summary>Parses <c>/products/{id}/ticker</c>.</summary>
    public static MarketSnapshot ParseTicker(JsonElement t, string symbol)
    {
        CheckError(t);
        var time = DateTime.TryParse(Http.Str(t, "time"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTime.UtcNow;
        return new MarketSnapshot(symbol, time, Http.Num(t, "price"), Http.Num(t, "bid"), Http.Num(t, "ask"), Http.Num(t, "volume"));
    }

    private static void CheckError(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var m) && !root.TryGetProperty("bids", out _) && !root.TryGetProperty("price", out _))
            throw new MarketDataException($"Coinbase error: {m}");
    }

    private static (int Seconds, TimeSpan? Resample) Granularity(BarInterval interval) => interval switch
    {
        BarInterval.Minute1 => (60, null),
        BarInterval.Minute5 => (300, null),
        BarInterval.Minute15 => (900, null),
        BarInterval.Minute30 => (900, TimeSpan.FromMinutes(30)),
        BarInterval.Hour1 => (3600, null),
        BarInterval.Hour4 => (3600, TimeSpan.FromHours(4)),
        BarInterval.Day1 => (86400, null),
        BarInterval.Week1 => (86400, TimeSpan.FromDays(7)),
        BarInterval.Month1 => (86400, TimeSpan.FromDays(30)),
        _ => throw new ArgumentOutOfRangeException(nameof(interval)),
    };
}
