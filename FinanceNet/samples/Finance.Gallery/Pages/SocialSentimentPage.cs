using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Finance.AlternativeData.Social;
using Finance.Core;
using Finance.Gallery.Controls;
using Finance.Gallery.Shell;

namespace Finance.Gallery.Pages;

/// <summary>Reddit posts → cashtag mentions → engagement-weighted sentiment → buzz z-scores.</summary>
public sealed class SocialSentimentPage : ModulePage
{
    public override string Title => "Social Sentiment";
    public override string Subtitle => "Retail chatter from Reddit (r/wallstreetbets, r/stocks, r/investing): ticker mentions, engagement-weighted sentiment and abnormal-buzz detection (Finance.AlternativeData).";
    public override string Glyph => "☍";
    public override string Group => "Intelligence";

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "AAPL", "MSFT", "NVDA", "TSLA", "AMZN", "META", "GOOGL", "GOOG", "AMD", "INTC", "SPY", "QQQ", "PLTR", "GME", "AMC",
        "NFLX", "COIN", "MSTR", "SMCI", "AVGO", "BABA", "DIS", "JPM", "BAC", "SOFI", "RIVN", "LCID", "NIO", "UBER", "ARM",
    };

    private readonly BarChart _mentions = new() { Height = 300, Horizontal = true, Title = "Most mentioned", BarColor = Palette.SkyColor };
    private readonly ScatterChart _map = new() { Height = 300, Title = "Mentions vs engagement-weighted sentiment", XLabel = "mentions", YLabel = "sentiment" };
    private readonly LineChart _buzz = new() { Height = 250, Title = "Hourly mentions and buzz z-score (top ticker)" };
    private readonly StackPanel _posts = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { FontSize = 11.5, Foreground = Palette.InkFaint };
    private Task _ready = Task.CompletedTask;

    protected override Control Build()
    {
        _ready = LoadAsync();
        return Ui.Stack(14,
            Ui.Card(Ui.Row(12, Ui.Button("Refresh", () => _ready = LoadAsync()), _status)),
            Ui.Columns(14, Ui.Card(_mentions), Ui.Card(_map)),
            Ui.Grid(14, new[] { "*", "*" }, new Control[] { Ui.Card(_buzz), Ui.Card(new ScrollViewer { Content = _posts, Height = 300 }, "Posts", "Highest engagement first") }));
    }

    public override Task WhenReadyAsync() => _ready;

    private async Task LoadAsync()
    {
        _status.Text = "Fetching Reddit…";
        var posts = new List<SocialPost>();
        string source = "Reddit public JSON";
        try
        {
            var reddit = new RedditClient(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
            var batches = await Task.WhenAll(new[] { "wallstreetbets", "stocks", "investing" }.Select(async s =>
            {
                try { return await reddit.GetPostsAsync(s, "new", 100); }
                catch (Exception) { return Array.Empty<SocialPost>(); }
            }));
            foreach (var b in batches) posts.AddRange(b);
        }
        catch (Exception)
        {
        }
        if (posts.Count(p => MentionExtractor.Extract(p.Text, Known).Count > 0) < 10)
        {
            posts = SyntheticPosts().ToList();
            source = "synthetic posts (Reddit unavailable or rate-limited)";
        }

        var agg = new SocialSentimentAggregator { KnownTickers = Known };
        var period = TimeSpan.FromDays(30);
        var stats = agg.Aggregate(posts, period)
            .GroupBy(s => s.Symbol)
            .Select(g => new SymbolSentiment(g.Key, g.Min(x => x.PeriodStart), g.Sum(x => x.Mentions),
                g.Average(x => x.AverageSentiment), g.Average(x => x.EngagementWeightedSentiment), g.Average(x => x.BullishRatio), g.Sum(x => x.TotalEngagement)))
            .OrderByDescending(s => s.Mentions).Take(12).ToList();
        _status.Text = $"{posts.Count} posts from {source} · {stats.Sum(s => s.Mentions)} ticker mentions · {DateTime.Now:HH:mm:ss}";

        _mentions.Labels = stats.Select(s => s.Symbol).ToArray();
        _mentions.Values = stats.Select(s => (double)s.Mentions).ToArray();
        _mentions.Format = v => v.ToString("0");
        _mentions.Refresh();

        _map.Points.Clear();
        _map.Highlights.Clear();
        _map.XFormat = v => v.ToString("0");
        _map.YFormat = v => v.ToString("+0.00;-0.00;0");
        foreach (var s in stats)
            _map.Highlights.Add(new ScatterPoint(s.Mentions, s.EngagementWeightedSentiment, s.Symbol,
                s.EngagementWeightedSentiment >= 0 ? Palette.UpColor : Palette.DownColor, 3 + Math.Min(6, Math.Log(1 + s.TotalEngagement))));
        _map.Refresh();

        if (stats.Count > 0)
        {
            string top = stats[0].Symbol;
            var times = posts.Where(p => MentionExtractor.Extract(p.Text, Known).Contains(top)).Select(p => p.Time).ToList();
            var start = posts.Min(p => p.Time);
            int hours = Math.Max(2, (int)Math.Ceiling((posts.Max(p => p.Time) - start).TotalHours) + 1);
            var counts = new double[hours];
            foreach (var t in times) counts[Math.Clamp((int)(t - start).TotalHours, 0, hours - 1)]++;
            var z = BuzzDetector.ZScores(counts, Math.Min(12, Math.Max(3, hours / 3)));
            _buzz.YFormat = v => v.ToString("0.#");
            _buzz.XFormat = v => $"{v:0}h";
            _buzz.ReferenceLines.Clear();
            _buzz.ReferenceLines.Add((3, "spike threshold (z = 3)", Palette.DownColor));
            _buzz.SetSeries(new LineSeries($"{top} mentions/h", counts, Palette.SkyColor) { Fill = true }, new LineSeries("buzz z", z, Palette.GoldColor));
        }

        var scorer = new SocialLexiconScorer();
        _posts.Children.Clear();
        foreach (var p in posts.OrderByDescending(p => p.Engagement).Take(30))
        {
            double sc = scorer.Score(p.Text);
            var color = sc > 0.05 ? Palette.UpColor : sc < -0.05 ? Palette.DownColor : Palette.InkSoftColor;
            var tickers = MentionExtractor.Extract(p.Text, Known);
            _posts.Children.Add(new Border
            {
                Background = Palette.Raised,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8),
                Child = Ui.Stack(3,
                    new TextBlock { Text = p.Text.Length > 220 ? p.Text[..220] + "…" : p.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Palette.Ink },
                    new TextBlock
                    {
                        Text = $"{p.Source} · ▲{p.Likes} · {p.Comments} comments · {string.Join(" ", tickers.Select(t => "$" + t))} · sentiment {sc:+0.00;-0.00;0}",
                        FontSize = 11, Foreground = Palette.Brush(color),
                    }),
            });
        }
    }

    private static IEnumerable<SocialPost> SyntheticPosts()
    {
        var rng = new FastRandom(77);
        string[] tickers = { "NVDA", "TSLA", "AAPL", "PLTR", "AMD", "GME", "MSFT", "SPY", "COIN", "META" };
        string[] bull = { "{0} to the moon, loading more calls", "Earnings beat incoming for ${0}, strong guidance", "${0} breakout confirmed, bullish", "Bought the dip on {0}, great growth story" };
        string[] bear = { "${0} looks overvalued, buying puts", "{0} guidance weak, expecting a drop", "Sold all my ${0}, downgrade risk", "{0} lawsuit news is bad, bearish" };
        var now = DateTime.UtcNow;
        for (int i = 0; i < 400; i++)
        {
            int k = (int)Math.Min(tickers.Length - 1, Math.Floor(Math.Pow(rng.NextDouble(), 1.8) * tickers.Length));
            bool isBull = rng.NextDouble() < (k % 3 == 0 ? 0.7 : 0.45);
            var tpl = isBull ? bull[rng.NextInt(bull.Length)] : bear[rng.NextInt(bear.Length)];
            double hoursAgo = k == 0 && rng.NextDouble() < 0.3 ? rng.NextDouble() * 3 : rng.NextDouble() * 48;
            yield return new SocialPost($"s{i}", "r/wallstreetbets", "user" + rng.NextInt(999), string.Format(tpl, tickers[k]), now.AddHours(-hoursAgo),
                Likes: (int)(Math.Exp(rng.NextGaussian() * 1.5) * 20), Comments: rng.NextInt(120));
        }
    }
}
