using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Finance.Gallery.Controls;
using Finance.Gallery.Shell;
using Finance.NewsAnalytics;

namespace Finance.Gallery.Pages;

/// <summary>Live RSS headlines → de-duplication → lexicon sentiment → events → per-ticker intelligence.</summary>
public sealed class NewsIntelligencePage : ModulePage
{
    public override string Title => "News Intelligence";
    public override string Subtitle => "Live Yahoo Finance and Google News RSS, de-duplicated with MinHash, scored by a Loughran-McDonald-style lexicon and classified into market events (Finance.NewsAnalytics).";
    public override string Glyph => "▤";
    public override string Group => "Intelligence";

    private static readonly HttpClient Http = CreateHttp();
    private TextBox _tickers = null!;
    private readonly TextBlock _status = new() { FontSize = 11.5, Foreground = Palette.InkFaint };
    private readonly StackPanel _feed = new() { Spacing = 8 };
    private readonly WrapPanel _summaries = new();
    private readonly BarChart _events = new() { Height = 250, Horizontal = true, Title = "Event types", BarColor = Palette.VioletColor };
    private readonly LineChart _daily = new() { Height = 250, Title = "Decay-weighted daily sentiment" };
    private Task _ready = Task.CompletedTask;

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Finance.Net-Gallery/0.1");
        return h;
    }

    protected override Control Build()
    {
        (var row, _tickers) = Ui.Field("Tickers", "AAPL, MSFT, NVDA, TSLA");
        var refresh = Ui.Button("Fetch news", () => _ready = LoadAsync());
        var bar = Ui.Grid(12, new[] { "*", "Auto" }, new Control[] { row, refresh });
        refresh.VerticalAlignment = VerticalAlignment.Bottom;

        var feedScroll = new ScrollViewer { Content = _feed, Height = 520 };
        _ready = LoadAsync();
        return Ui.Stack(14,
            Ui.Card(Ui.Stack(8, bar, _status)),
            _summaries,
            Ui.Grid(14, new[] { "3*", "2*" }, new Control[]
            {
                Ui.Card(feedScroll, "Headlines", "Newest first · score in [-1, 1] · detected events"),
                Ui.Stack(14, Ui.Card(_events), Ui.Card(_daily)),
            }));
    }

    public override Task WhenReadyAsync() => _ready;

    private async Task LoadAsync()
    {
        var tickers = (_tickers.Text ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToUpperInvariant()).Distinct().Take(8).ToArray();
        if (tickers.Length == 0) return;
        _status.Text = "Fetching feeds…";
        var reader = new FeedReader(Http);
        var items = new List<NewsItem>();
        string source = "live RSS";
        try
        {
            var tasks = tickers.Select(t => SafeRead(reader, FeedReader.YahooFinanceUrl(t), t))
                .Concat(tickers.Select(t => SafeRead(reader, FeedReader.GoogleNewsUrl(t + " stock"), t)));
            foreach (var batch in await Task.WhenAll(tasks)) items.AddRange(batch);
        }
        catch (Exception)
        {
            // fall through to the offline sample
        }
        if (items.Count == 0)
        {
            items.AddRange(OfflineSample(tickers));
            source = "offline sample (network unavailable)";
        }

        var engine = new NewsIntelligence();
        var scored = await engine.ProcessAsync(items, deduplicate: true);
        int raw = items.Count;
        _status.Text = $"{raw} headlines from {source} · {scored.Count} after de-duplication · {DateTime.Now:HH:mm:ss}";
        Render(tickers, scored);
    }

    private static async Task<IReadOnlyList<NewsItem>> SafeRead(FeedReader reader, string url, string ticker)
    {
        try
        {
            var items = await reader.ReadAsync(url);
            return items.Select(i => i with { Tickers = i.Tickers.Contains(ticker) ? i.Tickers : i.Tickers.Append(ticker).ToArray() }).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<NewsItem>();
        }
    }

    private void Render(string[] tickers, IReadOnlyList<ScoredNews> scored)
    {
        _summaries.Children.Clear();
        var dailySeries = new List<LineSeries>();
        for (int ti = 0; ti < tickers.Length; ti++)
        {
            var intel = NewsIntelligence.Summarize(scored, tickers[ti]);
            var card = new Border
            {
                Width = 262,
                Margin = new Thickness(0, 0, 12, 0),
                Background = Palette.Panel,
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 12),
                Child = Ui.Stack(4,
                    Ui.Row(8, new TextBlock { Text = intel.Ticker, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Palette.Ink },
                        Ui.Pill($"{intel.ArticleCount} articles", Palette.InkSoftColor)),
                    new SentimentGauge { Value = intel.RecentSentiment, Caption = "Recent sentiment", Height = 58 },
                    Ui.Label($"▲ {intel.Positive}   ● {intel.Neutral}   ▼ {intel.Negative}   trend {intel.SentimentTrend:+0.00;-0.00}", 11.5, Palette.InkSoft)),
            };
            _summaries.Children.Add(card);
            if (intel.Daily.Count > 1)
                dailySeries.Add(new LineSeries(intel.Ticker, intel.Daily.Select(d => d.DecayedScore).ToArray(), Palette.SeriesColor(ti))
                {
                    X = intel.Daily.Select(d => (double)d.Date.DayNumber).ToArray(),
                });
        }
        _daily.XFormat = v => DateOnly.FromDayNumber((int)v).ToString("dd MMM");
        _daily.YFormat = v => v.ToString("0.00");
        _daily.SetSeries(dailySeries.ToArray());

        var eventCounts = Enum.GetValues<NewsEventType>().Where(e => e != NewsEventType.None)
            .Select(e => (e, scored.Count(s => s.Events.HasFlag(e)))).Where(x => x.Item2 > 0).OrderByDescending(x => x.Item2).ToList();
        _events.Labels = eventCounts.Select(x => x.e.ToString()).ToArray();
        _events.Values = eventCounts.Select(x => (double)x.Item2).ToArray();
        _events.Format = v => v.ToString("0");
        _events.Refresh();

        _feed.Children.Clear();
        foreach (var s in scored.OrderByDescending(s => s.Item.Published).Take(80))
        {
            var color = s.Sentiment.Score > 0.05 ? Palette.UpColor : s.Sentiment.Score < -0.05 ? Palette.DownColor : Palette.InkSoftColor;
            var tags = new WrapPanel();
            foreach (var t in s.Item.Tickers.Take(4)) tags.Children.Add(Spaced(Ui.Pill(t, Palette.AccentColor)));
            foreach (var e in Enum.GetValues<NewsEventType>().Where(e => e != NewsEventType.None && s.Events.HasFlag(e)))
                tags.Children.Add(Spaced(Ui.Pill(e.ToString(), Palette.VioletColor)));
            var score = new Border
            {
                Width = 58,
                CornerRadius = new CornerRadius(8),
                Background = Palette.Brush(color, 40),
                Padding = new Thickness(0, 6),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = s.Sentiment.Score.ToString("+0.00;-0.00;0.00"), Foreground = Palette.Brush(color), FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center },
            };
            var text = Ui.Stack(3,
                new TextBlock { Text = s.Item.Title, FontSize = 13, FontWeight = FontWeight.Medium, Foreground = Palette.Ink, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"{s.Item.Source ?? "news"} · {s.Item.Published.LocalDateTime:dd MMM HH:mm}", FontSize = 11, Foreground = Palette.InkFaint },
                tags);
            _feed.Children.Add(new Border
            {
                Padding = new Thickness(10, 8),
                CornerRadius = new CornerRadius(10),
                Background = Palette.Raised,
                Child = Ui.Grid(12, new[] { "58", "*" }, new Control[] { score, text }),
            });
        }
    }

    private static Control Spaced(Control c)
    {
        c.Margin = new Thickness(0, 2, 6, 0);
        return c;
    }

    private static IEnumerable<NewsItem> OfflineSample(string[] tickers)
    {
        var now = DateTimeOffset.UtcNow;
        string[] templates =
        {
            "{0} beats earnings expectations as revenue surges on strong demand",
            "{0} shares fall after analyst downgrade cites slowing growth",
            "{0} announces $10 billion share buyback and raises dividend",
            "Regulators open investigation into {0} over antitrust concerns",
            "{0} raises full-year guidance, citing record margins",
            "{0} names new chief financial officer amid restructuring",
            "{0} unveils new product line; analysts upgrade to buy",
            "Uncertainty grows as {0} warns of supply chain disruptions",
        };
        int k = 0;
        foreach (var t in tickers)
            foreach (var tpl in templates)
                yield return new NewsItem(string.Format(tpl, t), now.AddHours(-3 * k++), Source: "sample") { Tickers = new[] { t } };
    }
}
