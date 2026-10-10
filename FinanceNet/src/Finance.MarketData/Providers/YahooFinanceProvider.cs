using System.Text.Json;
using Finance.Core.Market;
using Finance.MarketData.Internal;

namespace Finance.MarketData.Providers;

/// <summary>
/// Yahoo Finance through the public v8 chart API (no key). Yahoo rejects requests without a
/// browser-like User-Agent, which the default client sets. 4-hour bars are built from hourly bars.
/// </summary>
public sealed class YahooFinanceProvider : IMarketDataProvider
{
    private readonly HttpClient _http;

    public YahooFinanceProvider(HttpClient? http = null, string baseUrl = "https://query1.finance.yahoo.com")
    {
        _http = http ?? Http.Default;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string Name => "Yahoo";

    public string BaseUrl { get; }

    /// <summary>When true, OHLC are scaled by adjclose/close so splits and dividends do not create jumps.</summary>
    public bool UseAdjustedClose { get; init; }

    public async Task<Bar[]> GetBarsAsync(string symbol, BarInterval interval, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        var fetchInterval = interval == BarInterval.Hour4 ? BarInterval.Hour1 : interval;
        var url = $"{BaseUrl}/v8/finance/chart/{Http.Esc(symbol)}?period1={Http.UnixSeconds(start)}&period2={Http.UnixSeconds(end)}&interval={IntervalCode(fetchInterval)}&includePrePost=false&events=div%2Csplits";
        using var doc = await Http.GetJsonAsync(_http, url, Name, cancellationToken).ConfigureAwait(false);
        var bars = ParseChart(doc.RootElement, UseAdjustedClose);
        return interval == BarInterval.Hour4 ? BarAggregator.Resample(bars, TimeSpan.FromHours(4)) : bars;
    }

    public async Task<MarketSnapshot> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl}/v8/finance/chart/{Http.Esc(symbol)}?range=1d&interval=1d";
        using var doc = await Http.GetJsonAsync(_http, url, Name, cancellationToken).ConfigureAwait(false);
        var result = Result(doc.RootElement);
        var meta = result.GetProperty("meta");
        double last = Http.Num(meta, "regularMarketPrice");
        double prev = Http.Num(meta, "chartPreviousClose");
        if (double.IsNaN(prev)) prev = Http.Num(meta, "previousClose");
        double vol = Http.Num(meta, "regularMarketVolume");
        long t = meta.TryGetProperty("regularMarketTime", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetInt64() : Http.UnixSeconds(DateTime.UtcNow);
        double change = prev > 0 ? (last / prev - 1) * 100 : double.NaN;
        return new MarketSnapshot(symbol, Http.FromUnixSeconds(t), last, Volume: vol, ChangePercent: change);
    }

    /// <summary>Parses a v8 chart response. Rows with a null close (halted / partial sessions) are skipped.</summary>
    public static Bar[] ParseChart(JsonElement root, bool adjusted = false)
    {
        var result = Result(root);
        if (!result.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.Array) return Array.Empty<Bar>();
        var quote = result.GetProperty("indicators").GetProperty("quote")[0];
        JsonElement adj = default;
        bool hasAdj = adjusted && result.GetProperty("indicators").TryGetProperty("adjclose", out var adjArr)
            && adjArr.GetArrayLength() > 0 && adjArr[0].TryGetProperty("adjclose", out adj);
        var open = quote.GetProperty("open");
        var high = quote.GetProperty("high");
        var low = quote.GetProperty("low");
        var close = quote.GetProperty("close");
        var volume = quote.GetProperty("volume");
        int n = ts.GetArrayLength();
        var bars = new List<Bar>(n);
        for (int i = 0; i < n; i++)
        {
            double c = Http.Num(close[i]);
            if (double.IsNaN(c)) continue;
            double o = Http.Num(open[i]), h = Http.Num(high[i]), l = Http.Num(low[i]), v = Http.Num(volume[i]);
            if (double.IsNaN(o)) o = c;
            if (double.IsNaN(h)) h = Math.Max(o, c);
            if (double.IsNaN(l)) l = Math.Min(o, c);
            if (hasAdj)
            {
                double a = Http.Num(adj[i]);
                if (!double.IsNaN(a) && c != 0)
                {
                    double k = a / c;
                    o *= k; h *= k; l *= k; c = a;
                }
            }
            bars.Add(new Bar(Http.FromUnixSeconds(ts[i].GetInt64()), o, h, l, c, double.IsNaN(v) ? 0 : v));
        }
        return bars.ToArray();
    }

    private static JsonElement Result(JsonElement root)
    {
        var chart = root.GetProperty("chart");
        if (chart.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            throw new MarketDataException($"Yahoo error: {Http.Str(err, "code")} {Http.Str(err, "description")}");
        var results = chart.GetProperty("result");
        if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            throw new MarketDataException("Yahoo returned no result.");
        return results[0];
    }

    internal static string IntervalCode(BarInterval interval) => interval switch
    {
        BarInterval.Minute1 => "1m",
        BarInterval.Minute5 => "5m",
        BarInterval.Minute15 => "15m",
        BarInterval.Minute30 => "30m",
        BarInterval.Hour1 => "60m",
        BarInterval.Day1 => "1d",
        BarInterval.Week1 => "1wk",
        BarInterval.Month1 => "1mo",
        _ => throw new ArgumentOutOfRangeException(nameof(interval)),
    };
}
