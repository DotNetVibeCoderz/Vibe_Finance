using System.Globalization;
using System.Text.Json;
using Finance.MarketData.Internal;

namespace Finance.MarketData.Economic;

/// <summary>
/// Federal Reserve Economic Data (FRED, St. Louis Fed) — free API key from fred.stlouisfed.org. Serves any of its
/// ~800,000 series by id, the standard indicators of <see cref="EconomicIndicator"/>, and (as an
/// <see cref="IEconomicCalendar"/>) the official schedule of upcoming data releases.
/// </summary>
public sealed class FredProvider : IEconomicDataProvider, IEconomicCalendar
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public FredProvider(string apiKey, HttpClient? http = null, string baseUrl = "https://api.stlouisfed.org/fred")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey;
        _http = http ?? Http.Default;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string Name => "FRED";
    public string BaseUrl { get; }

    /// <summary>FRED series id used for each standard indicator.</summary>
    public static string SeriesId(EconomicIndicator indicator) => indicator switch
    {
        EconomicIndicator.RealGdp => "GDPC1",
        EconomicIndicator.RealGdpPerCapita => "A939RX0Q048SBEA",
        EconomicIndicator.TreasuryYield => "DGS10",
        EconomicIndicator.FederalFundsRate => "FEDFUNDS",
        EconomicIndicator.Cpi => "CPIAUCSL",
        EconomicIndicator.Inflation => "FPCPITOTLZGUSA",
        EconomicIndicator.RetailSales => "RSAFS",
        EconomicIndicator.DurableGoods => "DGORDER",
        EconomicIndicator.Unemployment => "UNRATE",
        EconomicIndicator.NonfarmPayroll => "PAYEMS",
        _ => throw new ArgumentOutOfRangeException(nameof(indicator)),
    };

    public Task<EconomicSeries> GetIndicatorAsync(EconomicIndicator indicator, CancellationToken cancellationToken = default)
        => GetSeriesAsync(SeriesId(indicator), cancellationToken: cancellationToken);

    /// <summary>A series by FRED id (e.g. "DGS10", "T10Y2Y"), oldest first; missing values (".") are skipped.</summary>
    public async Task<EconomicSeries> GetSeriesAsync(string seriesId, DateOnly? start = null, DateOnly? end = null, CancellationToken cancellationToken = default)
    {
        var id = Http.Esc(seriesId);
        using var meta = await Http.GetJsonAsync(_http, $"{BaseUrl}/series?series_id={id}&api_key={_apiKey}&file_type=json", Name, cancellationToken).ConfigureAwait(false);
        var range = (start is { } s ? $"&observation_start={s:yyyy-MM-dd}" : "") + (end is { } e ? $"&observation_end={e:yyyy-MM-dd}" : "");
        using var obs = await Http.GetJsonAsync(_http, $"{BaseUrl}/series/observations?series_id={id}&api_key={_apiKey}&file_type=json{range}", Name, cancellationToken)
            .ConfigureAwait(false);
        return ParseSeries(meta.RootElement, obs.RootElement);
    }

    public static EconomicSeries ParseSeries(JsonElement meta, JsonElement observations)
    {
        if (Http.Str(observations, "error_message") is { } err) throw new MarketDataException($"FRED: {err}");
        var m = meta.GetProperty("seriess")[0];
        var points = observations.GetProperty("observations").EnumerateArray()
            .Select(o => (Date: Http.Str(o, "date"), Value: Http.Str(o, "value")))
            .Where(o => o.Date is not null && o.Value is not null and not ".")
            .Select(o => new EconomicObservation(DateOnly.ParseExact(o.Date!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                double.Parse(o.Value!, CultureInfo.InvariantCulture)))
            .ToArray();
        return new EconomicSeries(Http.Str(m, "title") ?? Http.Str(m, "id") ?? "", Http.Str(m, "frequency") ?? "", Http.Str(m, "units") ?? "", points);
    }

    /// <summary>Scheduled release dates (FRED's release calendar), including future dates.</summary>
    public async Task<IReadOnlyList<EconomicEvent>> GetEventsAsync(DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl}/releases/dates?api_key={_apiKey}&file_type=json&realtime_start={start:yyyy-MM-dd}&realtime_end={end:yyyy-MM-dd}" +
                  "&include_release_dates_with_no_data=true&limit=1000&sort_order=asc";
        using var doc = await Http.GetJsonAsync(_http, url, Name, cancellationToken).ConfigureAwait(false);
        return ParseReleaseDates(doc.RootElement);
    }

    public static IReadOnlyList<EconomicEvent> ParseReleaseDates(JsonElement root)
        => root.GetProperty("release_dates").EnumerateArray()
            .Select(r =>
            {
                var name = Http.Str(r, "release_name") ?? "";
                var date = DateTime.SpecifyKind(DateTime.ParseExact(Http.Str(r, "date")!, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc);
                return new EconomicEvent(date, "US", name, Importance(name));
            })
            .OrderBy(e => e.Time).ToList();

    /// <summary>Market-moving releases are High; the rest of the headline set Medium; everything else Low.</summary>
    public static EventImportance Importance(string releaseName)
    {
        string n = releaseName.ToLowerInvariant();
        if (n.Contains("employment situation") || n.Contains("consumer price index") || n.Contains("gross domestic product")
            || n.Contains("fomc") || n.Contains("personal income and outlays")) return EventImportance.High;
        if (n.Contains("retail") || n.Contains("producer price") || n.Contains("industrial production") || n.Contains("durable")
            || n.Contains("housing") || n.Contains("jolts") || n.Contains("unemployment insurance")) return EventImportance.Medium;
        return EventImportance.Low;
    }
}
