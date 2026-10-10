using System.Globalization;
using System.Text.Json;

namespace Finance.MarketData.Internal;

internal static class Http
{
    public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private static readonly Lazy<HttpClient> Shared = new(() =>
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return c;
    });

    /// <summary>The process-wide client used when callers do not inject one.</summary>
    public static HttpClient Default => Shared.Value;

    public static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url, string provider, CancellationToken ct, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (request.Headers.UserAgent.Count == 0 && client.DefaultRequestHeaders.UserAgent.Count == 0)
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        configure?.Invoke(request);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new MarketDataException($"{provider} returned {(int)response.StatusCode}: {Truncate(body)}");
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new MarketDataException($"{provider} returned invalid JSON: {Truncate(body)}", ex);
        }
    }

    public static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";

    /// <summary>Reads a number that may be encoded as a JSON number or a numeric string.</summary>
    public static double Num(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.GetDouble(),
            JsonValueKind.String => double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN,
            _ => double.NaN,
        };
    }

    public static double Num(JsonElement obj, string property)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var p) ? Num(p) : double.NaN;

    public static string? Str(JsonElement obj, string property)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : null;

    public static long UnixSeconds(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, t.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : t.Kind)).ToUnixTimeSeconds();

    public static long UnixMillis(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, t.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : t.Kind)).ToUnixTimeMilliseconds();

    public static DateTime FromUnixSeconds(long s) => DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;

    public static DateTime FromUnixMillis(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    public static string Esc(string s) => Uri.EscapeDataString(s);

    public static string Inv(double v) => v.ToString(CultureInfo.InvariantCulture);
}
