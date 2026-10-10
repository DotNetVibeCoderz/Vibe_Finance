using System.Text.Json;
using Finance.Core.Market;
using Finance.MarketData.Internal;

namespace Finance.MarketData.Providers;

/// <summary>
/// Binance spot public market data (no key). The default base URL is the official
/// market-data-only mirror <c>data-api.binance.vision</c>, which serves the same payloads as
/// <c>api.binance.com</c> and is reachable from more networks.
/// </summary>
public sealed class BinanceProvider : IMarketDataProvider, IOrderBookProvider
{
    private const int MaxKlines = 1000;
    private readonly HttpClient _http;

    public BinanceProvider(HttpClient? http = null, string baseUrl = "https://data-api.binance.vision")
    {
        _http = http ?? Http.Default;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string Name => "Binance";

    public string BaseUrl { get; }

    /// <summary>"BTC-USDT", "btc/usdt" and "BTCUSDT" all become "BTCUSDT".</summary>
    public static string NormalizeSymbol(string symbol) => symbol.Replace("-", "").Replace("/", "").Replace("_", "").ToUpperInvariant();

    public async Task<Bar[]> GetBarsAsync(string symbol, BarInterval interval, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        var sym = NormalizeSymbol(symbol);
        var all = new List<Bar>();
        long from = Http.UnixMillis(start), to = Http.UnixMillis(end);
        while (from <= to)
        {
            var url = $"{BaseUrl}/api/v3/klines?symbol={sym}&interval={IntervalCode(interval)}&startTime={from}&endTime={to}&limit={MaxKlines}";
            using var doc = await Http.GetJsonAsync(_http, url, Name, cancellationToken).ConfigureAwait(false);
            var page = ParseKlines(doc.RootElement);
            if (page.Length == 0) break;
            all.AddRange(page);
            if (page.Length < MaxKlines) break;
            from = Http.UnixMillis(page[^1].Time) + 1;
        }
        return all.ToArray();
    }

    public async Task<MarketSnapshot> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var sym = NormalizeSymbol(symbol);
        using var doc = await Http.GetJsonAsync(_http, $"{BaseUrl}/api/v3/ticker/24hr?symbol={sym}", Name, cancellationToken).ConfigureAwait(false);
        return ParseTicker(doc.RootElement, symbol);
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        var sym = NormalizeSymbol(symbol);
        int limit = new[] { 5, 10, 20, 50, 100, 500, 1000, 5000 }.First(l => l >= Math.Min(depth, 5000));
        using var doc = await Http.GetJsonAsync(_http, $"{BaseUrl}/api/v3/depth?symbol={sym}&limit={limit}", Name, cancellationToken).ConfigureAwait(false);
        return ParseDepth(doc.RootElement, symbol, depth);
    }

    /// <summary>Parses the <c>/api/v3/klines</c> array-of-arrays payload.</summary>
    public static Bar[] ParseKlines(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new MarketDataException($"Binance error: {root}");
        var bars = new Bar[root.GetArrayLength()];
        int i = 0;
        foreach (var k in root.EnumerateArray())
        {
            bars[i++] = new Bar(Http.FromUnixMillis(k[0].GetInt64()), Http.Num(k[1]), Http.Num(k[2]), Http.Num(k[3]), Http.Num(k[4]), Http.Num(k[5]));
        }
        return bars;
    }

    /// <summary>Parses <c>/api/v3/ticker/24hr</c>.</summary>
    public static MarketSnapshot ParseTicker(JsonElement t, string symbol)
    {
        if (t.TryGetProperty("code", out _)) throw new MarketDataException($"Binance error: {Http.Str(t, "msg")}");
        long close = t.TryGetProperty("closeTime", out var ct) ? ct.GetInt64() : Http.UnixMillis(DateTime.UtcNow);
        return new MarketSnapshot(symbol, Http.FromUnixMillis(close), Http.Num(t, "lastPrice"), Http.Num(t, "bidPrice"), Http.Num(t, "askPrice"),
            Http.Num(t, "volume"), Http.Num(t, "priceChangePercent"));
    }

    /// <summary>Parses <c>/api/v3/depth</c> into a Core order book.</summary>
    public static OrderBook ParseDepth(JsonElement d, string symbol, int depth = int.MaxValue)
    {
        if (d.TryGetProperty("code", out _)) throw new MarketDataException($"Binance error: {Http.Str(d, "msg")}");
        var book = new OrderBook(symbol, DateTime.UtcNow);
        book.SetBids(Levels(d.GetProperty("bids"), depth));
        book.SetAsks(Levels(d.GetProperty("asks"), depth));
        return book;
    }

    internal static IEnumerable<BookLevel> Levels(JsonElement arr, int depth)
        => arr.EnumerateArray().Take(depth).Select(l => new BookLevel(Http.Num(l[0]), Http.Num(l[1]))).ToList();

    internal static string IntervalCode(BarInterval interval) => interval switch
    {
        BarInterval.Minute1 => "1m",
        BarInterval.Minute5 => "5m",
        BarInterval.Minute15 => "15m",
        BarInterval.Minute30 => "30m",
        BarInterval.Hour1 => "1h",
        BarInterval.Hour4 => "4h",
        BarInterval.Day1 => "1d",
        BarInterval.Week1 => "1w",
        BarInterval.Month1 => "1M",
        _ => throw new ArgumentOutOfRangeException(nameof(interval)),
    };
}
