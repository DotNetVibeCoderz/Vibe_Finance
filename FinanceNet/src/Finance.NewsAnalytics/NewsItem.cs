using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Finance.NewsAnalytics;

/// <summary>A news article or headline.</summary>
public sealed record NewsItem(string Title, DateTimeOffset Published, string? Summary = null, string? Url = null, string? Source = null)
{
    /// <summary>Tickers attached by the feed or by <see cref="EntityExtractor"/>.</summary>
    public IReadOnlyList<string> Tickers { get; init; } = Array.Empty<string>();

    /// <summary>Title and summary joined, the text analysed by default.</summary>
    public string Text => string.IsNullOrWhiteSpace(Summary) ? Title : $"{Title}. {Summary}";

    /// <summary>Stable identifier (URL, or a hash of the title).</summary>
    public string Id => Url ?? Title.GetHashCode(StringComparison.Ordinal).ToString("x8", CultureInfo.InvariantCulture);
}

/// <summary>
/// RSS 2.0 / Atom reader with helpers for Yahoo Finance and Google News feeds. Network-free
/// parsing is available through <see cref="Parse"/>.
/// </summary>
public sealed partial class FeedReader(HttpClient http)
{
    /// <summary>Yahoo Finance headline feed for one or more comma-separated tickers.</summary>
    public static string YahooFinanceUrl(string tickers, string region = "US", string lang = "en-US")
        => $"https://feeds.finance.yahoo.com/rss/2.0/headline?s={Uri.EscapeDataString(tickers)}&region={region}&lang={lang}";

    /// <summary>Google News search feed.</summary>
    public static string GoogleNewsUrl(string query, string language = "en-US", string country = "US")
        => $"https://news.google.com/rss/search?q={Uri.EscapeDataString(query)}&hl={language}&gl={country}&ceid={country}:{language.Split('-')[0]}";

    /// <summary>Downloads and parses a feed.</summary>
    public async Task<IReadOnlyList<NewsItem>> ReadAsync(string url, CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Finance.Net FeedReader)");
        using var resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var xml = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(xml);
    }

    /// <summary>Reads several feeds concurrently and merges them, newest first, without duplicate URLs.</summary>
    public async Task<IReadOnlyList<NewsItem>> ReadManyAsync(IEnumerable<string> urls, CancellationToken cancellationToken = default)
    {
        var results = await Task.WhenAll(urls.Select(async u =>
        {
            try { return await ReadAsync(u, cancellationToken).ConfigureAwait(false); }
            catch (HttpRequestException) { return Array.Empty<NewsItem>(); }
        })).ConfigureAwait(false);
        return results.SelectMany(r => r).DistinctBy(n => n.Id).OrderByDescending(n => n.Published).ToList();
    }

    /// <summary>Parses RSS 2.0 or Atom XML.</summary>
    public static IReadOnlyList<NewsItem> Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new FormatException("Empty feed.");
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var items = new List<NewsItem>();
        if (root.Name.LocalName == "feed")
        {
            string? feedTitle = root.Element(atom + "title")?.Value;
            foreach (var e in root.Elements(atom + "entry"))
            {
                var link = e.Elements(atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value;
                var date = ParseDate(e.Element(atom + "published")?.Value ?? e.Element(atom + "updated")?.Value);
                items.Add(new NewsItem(Clean(e.Element(atom + "title")?.Value), date, Clean(e.Element(atom + "summary")?.Value ?? e.Element(atom + "content")?.Value), link, feedTitle));
            }
            return items;
        }
        var channel = root.Element("channel") ?? root;
        string? source = channel.Element("title")?.Value;
        foreach (var e in channel.Elements("item"))
        {
            var src = e.Element("source")?.Value ?? source;
            items.Add(new NewsItem(Clean(e.Element("title")?.Value), ParseDate(e.Element("pubDate")?.Value), Clean(e.Element("description")?.Value), e.Element("link")?.Value?.Trim(), src));
        }
        return items;
    }

    internal static DateTimeOffset ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return DateTimeOffset.MinValue;
        s = s.Trim();
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return d;
        // RFC-822 with a zone abbreviation such as "GMT"/"EST".
        var m = Rfc822().Match(s);
        if (m.Success && DateTimeOffset.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out d)) return d;
        return DateTimeOffset.MinValue;
    }

    internal static string Clean(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = Tags().Replace(html, " ");
        return Spaces().Replace(WebUtility.HtmlDecode(text), " ").Trim();
    }

    [GeneratedRegex(@"^(.*\d{2}:\d{2}(:\d{2})?)\s*[A-Z]{1,4}$")]
    private static partial Regex Rfc822();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
