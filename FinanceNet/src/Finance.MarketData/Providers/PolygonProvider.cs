using System.Text.Json;
using Finance.Core.Market;
using Finance.MarketData.Internal;

namespace Finance.MarketData.Providers;

/// <summary>
/// Polygon.io REST API: aggregates (<c>/v2/aggs</c>) and previous-day / last-trade quotes.
/// Paging follows the <c>next_url</c> field.
/// </summary>
public sealed class PolygonProvider : IMarketDataProvider
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public PolygonProvider(string apiKey, HttpClient? http = null, string baseUrl = "https://api.polygon.io")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey;
        _http = http ?? Http.Default;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string Name => "Polygon";

    public string BaseUrl { get; }

    public async Task<Bar[]> GetBarsAsync(string symbol, BarInterval interval, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        var (mult, span) = Timespan(interval);
        string? url = $"{BaseUrl}/v2/aggs/ticker/{Http.Esc(symbol.ToUpperInvariant())}/range/{mult}/{span}/{Http.UnixMillis(start)}/{Http.UnixMillis(end)}?adjusted=true&sort=asc&limit=50000";
        var all = new List<Bar>();
        for (int page = 0; url is not null && page < 100; page++)
        {
            using var doc = await Http.GetJsonAsync(_http, WithKey(url), Name, cancellationToken).ConfigureAwait(false);
            all.AddRange(ParseAggregates(doc.RootElement));
            url = Http.Str(doc.RootElement, "next_url");
        }
        return all.ToArray();
    }

    public async Task<MarketSnapshot> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        using var doc = await Http.GetJsonAsync(_http, WithKey($"{BaseUrl}/v2/aggs/ticker/{Http.Esc(symbol.ToUpperInvariant())}/prev?adjusted=true"), Name, cancellationToken).ConfigureAwait(false);
        var bars = ParseAggregates(doc.RootElement);
        if (bars.Length == 0) throw new MarketDataException($"Polygon returned no data for {symbol}.");
        var b = bars[^1];
        return new MarketSnapshot(symbol, b.Time, b.Close, Volume: b.Volume, ChangePercent: (b.Close / b.Open - 1) * 100);
    }

    /// <summary>Parses an aggregates payload ({status, results:[{t,o,h,l,c,v}]}).</summary>
    public static Bar[] ParseAggregates(JsonElement root)
    {
        var status = Http.Str(root, "status");
        if (status is "ERROR" or "NOT_AUTHORIZED")
            throw new MarketDataException($"Polygon: {Http.Str(root, "error") ?? Http.Str(root, "message") ?? status}");
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return Array.Empty<Bar>();
        return results.EnumerateArray()
            .Select(r => new Bar(Http.FromUnixMillis((long)Http.Num(r, "t")), Http.Num(r, "o"), Http.Num(r, "h"), Http.Num(r, "l"), Http.Num(r, "c"), Http.Num(r, "v")))
            .ToArray();
    }

    private string WithKey(string url) => url + (url.Contains('?') ? "&" : "?") + "apiKey=" + Http.Esc(_apiKey);

    private static (int, string) Timespan(BarInterval interval) => interval switch
    {
        BarInterval.Minute1 => (1, "minute"),
        BarInterval.Minute5 => (5, "minute"),
        BarInterval.Minute15 => (15, "minute"),
        BarInterval.Minute30 => (30, "minute"),
        BarInterval.Hour1 => (1, "hour"),
        BarInterval.Hour4 => (4, "hour"),
        BarInterval.Day1 => (1, "day"),
        BarInterval.Week1 => (1, "week"),
        BarInterval.Month1 => (1, "month"),
        _ => throw new ArgumentOutOfRangeException(nameof(interval)),
    };
}
