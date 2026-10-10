using System.Text.Json;

namespace Finance.AlternativeData.Social;

/// <summary>
/// Reads public subreddit listings through Reddit's <c>.json</c> endpoints (no OAuth). Reddit
/// requires a descriptive User-Agent and throttles anonymous clients to roughly 10 requests a minute.
/// </summary>
public sealed class RedditClient
{
    private readonly HttpClient _http;

    public RedditClient(HttpClient? http = null, string baseUrl = "https://www.reddit.com", string userAgent = "FinanceNet/0.1 (by Gravicode Studios)")
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        BaseUrl = baseUrl.TrimEnd('/');
        UserAgent = userAgent;
    }

    public string BaseUrl { get; }

    public string UserAgent { get; }

    /// <summary>Latest posts of a subreddit (sort: new, hot, top, rising).</summary>
    public async Task<IReadOnlyList<SocialPost>> GetPostsAsync(string subreddit, string sort = "new", int limit = 50, CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/r/{Uri.EscapeDataString(subreddit)}/{sort}.json?limit={Math.Clamp(limit, 1, 100)}&raw_json=1");
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Reddit returned {(int)resp.StatusCode}.");
        return ParseListing(body);
    }

    /// <summary>Parses a Reddit listing JSON document.</summary>
    public static IReadOnlyList<SocialPost> ParseListing(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<SocialPost>();
        foreach (var child in doc.RootElement.GetProperty("data").GetProperty("children").EnumerateArray())
        {
            var d = child.GetProperty("data");
            string title = d.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string body = d.TryGetProperty("selftext", out var s) ? s.GetString() ?? "" : "";
            double created = d.TryGetProperty("created_utc", out var c) ? c.GetDouble() : 0;
            list.Add(new SocialPost(
                d.GetProperty("id").GetString() ?? "",
                "reddit/" + (d.TryGetProperty("subreddit", out var sr) ? sr.GetString() : ""),
                d.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "",
                string.IsNullOrEmpty(body) ? title : title + "\n" + body,
                DateTimeOffset.FromUnixTimeSeconds((long)created).UtcDateTime,
                d.TryGetProperty("score", out var sc) ? Math.Max(0, sc.GetInt32()) : 0,
                d.TryGetProperty("num_comments", out var nc) ? nc.GetInt32() : 0));
        }
        return list;
    }
}
