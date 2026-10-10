using System.Globalization;
using System.Text.Json;
using Finance.Mathematics;

namespace Finance.AlternativeData.Weather;

/// <summary>Daily weather at one location (°C, mm).</summary>
public readonly record struct WeatherObservation(DateOnly Date, double TempMean, double TempMin, double TempMax, double Precipitation);

/// <summary>
/// Open-Meteo client (free, no key). Recent and forecast days come from the forecast API,
/// history from the archive (ERA5) API.
/// </summary>
public sealed class OpenMeteoClient
{
    private const string Daily = "temperature_2m_mean,temperature_2m_min,temperature_2m_max,precipitation_sum";
    private readonly HttpClient _http;

    public OpenMeteoClient(HttpClient? http = null, string forecastUrl = "https://api.open-meteo.com", string archiveUrl = "https://archive-api.open-meteo.com")
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        ForecastUrl = forecastUrl.TrimEnd('/');
        ArchiveUrl = archiveUrl.TrimEnd('/');
    }

    public string ForecastUrl { get; }

    public string ArchiveUrl { get; }

    /// <summary>Historical daily observations in [start, end].</summary>
    public Task<WeatherObservation[]> GetHistoryAsync(double latitude, double longitude, DateOnly start, DateOnly end, CancellationToken cancellationToken = default)
        => Fetch($"{ArchiveUrl}/v1/archive?latitude={Inv(latitude)}&longitude={Inv(longitude)}&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}&daily={Daily}&timezone=UTC", cancellationToken);

    /// <summary>Past <paramref name="pastDays"/> plus the next <paramref name="forecastDays"/> days.</summary>
    public Task<WeatherObservation[]> GetForecastAsync(double latitude, double longitude, int pastDays = 7, int forecastDays = 7, CancellationToken cancellationToken = default)
        => Fetch($"{ForecastUrl}/v1/forecast?latitude={Inv(latitude)}&longitude={Inv(longitude)}&daily={Daily}&past_days={pastDays}&forecast_days={forecastDays}&timezone=UTC", cancellationToken);

    /// <summary>Parses the <c>daily</c> block of an Open-Meteo response. Missing values become NaN.</summary>
    public static WeatherObservation[] Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
            throw new HttpRequestException("Open-Meteo: " + (root.TryGetProperty("reason", out var r) ? r.GetString() : "error"));
        var d = root.GetProperty("daily");
        var time = d.GetProperty("time");
        double At(string key, int i) => d.TryGetProperty(key, out var arr) && arr[i].ValueKind == JsonValueKind.Number ? arr[i].GetDouble() : double.NaN;
        var result = new WeatherObservation[time.GetArrayLength()];
        for (int i = 0; i < result.Length; i++)
        {
            double min = At("temperature_2m_min", i), max = At("temperature_2m_max", i), mean = At("temperature_2m_mean", i);
            if (double.IsNaN(mean)) mean = (min + max) / 2;
            result[i] = new WeatherObservation(DateOnly.Parse(time[i].GetString()!, CultureInfo.InvariantCulture), mean, min, max, At("precipitation_sum", i));
        }
        return result;
    }

    private async Task<WeatherObservation[]> Fetch(string url, CancellationToken ct)
    {
        var body = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        return Parse(body);
    }

    private static string Inv(double v) => v.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Degree days and weather/market correlation.</summary>
public static class WeatherAnalytics
{
    /// <summary>Base temperature used by energy markets: 18 °C (≈ 65 °F).</summary>
    public const double DefaultBaseCelsius = 18.0;

    /// <summary>Heating degree days: max(0, base − mean temperature).</summary>
    public static double HeatingDegreeDays(double tempMean, double baseTemp = DefaultBaseCelsius) => Math.Max(0, baseTemp - tempMean);

    /// <summary>Cooling degree days: max(0, mean temperature − base).</summary>
    public static double CoolingDegreeDays(double tempMean, double baseTemp = DefaultBaseCelsius) => Math.Max(0, tempMean - baseTemp);

    /// <summary>Accumulated HDD and CDD over the observations (what weather derivatives settle on).</summary>
    public static (double Hdd, double Cdd) Accumulate(IEnumerable<WeatherObservation> obs, double baseTemp = DefaultBaseCelsius)
    {
        double h = 0, c = 0;
        foreach (var o in obs)
        {
            if (double.IsNaN(o.TempMean)) continue;
            h += HeatingDegreeDays(o.TempMean, baseTemp);
            c += CoolingDegreeDays(o.TempMean, baseTemp);
        }
        return (h, c);
    }

    /// <summary>
    /// Pearson correlation between a weather variable and asset returns joined on date, for lags
    /// 0..<paramref name="maxLag"/> (lag k pairs weather on day t with the return on the k-th
    /// following trading date). Returns (lag, correlation, observations).
    /// </summary>
    public static IReadOnlyList<(int Lag, double Correlation, int Count)> Correlate(
        IReadOnlyDictionary<DateOnly, double> weather,
        IReadOnlyList<(DateOnly Date, double Return)> returns,
        int maxLag = 5)
    {
        var result = new List<(int, double, int)>();
        for (int lag = 0; lag <= maxLag; lag++)
        {
            var xs = new List<double>();
            var ys = new List<double>();
            for (int i = 0; i + lag < returns.Count; i++)
            {
                if (!weather.TryGetValue(returns[i].Date, out var w) || double.IsNaN(w)) continue;
                double r = returns[i + lag].Return;
                if (double.IsNaN(r)) continue;
                xs.Add(w);
                ys.Add(r);
            }
            result.Add((lag, xs.Count > 2 ? Stats.Correlation(xs.ToArray(), ys.ToArray()) : double.NaN, xs.Count));
        }
        return result;
    }

    /// <summary>Daily HDD+CDD keyed by date (a common "energy demand" signal).</summary>
    public static Dictionary<DateOnly, double> DegreeDaySeries(IEnumerable<WeatherObservation> obs, double baseTemp = DefaultBaseCelsius)
        => obs.Where(o => !double.IsNaN(o.TempMean)).ToDictionary(o => o.Date, o => HeatingDegreeDays(o.TempMean, baseTemp) + CoolingDegreeDays(o.TempMean, baseTemp));

    /// <summary>
    /// Temperature anomaly vs the same calendar day's mean across the history (seasonality removed).
    /// Needs several years of observations: with a single year each day is its own climatology, so every anomaly is 0.
    /// </summary>
    public static Dictionary<DateOnly, double> Anomalies(IReadOnlyList<WeatherObservation> obs)
    {
        var climatology = obs.Where(o => !double.IsNaN(o.TempMean)).GroupBy(o => o.Date.DayOfYear).ToDictionary(g => g.Key, g => g.Average(o => o.TempMean));
        return obs.Where(o => !double.IsNaN(o.TempMean)).ToDictionary(o => o.Date, o => o.TempMean - climatology[o.Date.DayOfYear]);
    }
}
