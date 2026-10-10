using Finance.NewsAnalytics;

namespace Finance.AI.Tests;

public class NewsTests
{
    private const string Rss = """
        <?xml version="1.0"?>
        <rss version="2.0"><channel><title>Yahoo! Finance: AAPL News</title>
        <item><title>Apple beats estimates &amp; raises dividend</title><link>https://x/1</link><description>&lt;p&gt;Record &lt;b&gt;profit&lt;/b&gt;&lt;/p&gt;</description><pubDate>Thu, 09 Oct 2025 14:30:00 GMT</pubDate></item>
        <item><title>Tesla shares plunge after recall</title><link>https://x/2</link><pubDate>Fri, 10 Oct 2025 09:00:00 +0000</pubDate></item>
        </channel></rss>
        """;

    private const string Atom = """
        <feed xmlns="http://www.w3.org/2005/Atom"><title>Desk</title>
        <entry><title>Nvidia unveils new chip</title><link href="https://y/1"/><updated>2025-10-08T12:00:00Z</updated><summary>Launch event</summary></entry>
        </feed>
        """;

    [Fact]
    public void ParsesRssAndAtom()
    {
        var rss = FeedReader.Parse(Rss);
        Assert.Equal(2, rss.Count);
        Assert.Equal("Apple beats estimates & raises dividend", rss[0].Title);
        Assert.Equal("Record profit", rss[0].Summary);
        Assert.Equal(new DateTimeOffset(2025, 10, 9, 14, 30, 0, TimeSpan.Zero), rss[0].Published);
        Assert.Equal("Yahoo! Finance: AAPL News", rss[0].Source);
        var atom = FeedReader.Parse(Atom);
        Assert.Equal("https://y/1", atom[0].Url);
        Assert.Equal(2025, atom[0].Published.Year);
        Assert.Contains("s=AAPL", FeedReader.YahooFinanceUrl("AAPL"));
    }

    [Fact]
    public void LexiconScoresFinancialTone()
    {
        var m = new LexiconSentimentModel();
        Assert.Equal("positive", m.Score("Apple beats estimates with record profit and strong growth").Label);
        Assert.Equal("negative", m.Score("Shares plunge after the company misses forecasts and warns of losses").Label);
        Assert.True(m.Score("Results were not strong").Score < 0);
        Assert.True(m.Score("profit rose sharply").Score > m.Score("profit rose slightly").Score);
        var s = m.Score("The company may face a lawsuit; outcomes are uncertain");
        Assert.True(s.Uncertainty >= 2);
        Assert.True(s.Litigious >= 1);
    }

    [Fact]
    public void ExtractsTickersAndEvents()
    {
        var x = new EntityExtractor();
        var t = x.ExtractTickers("Microsoft to buy stake as $NVDA rallies; NASDAQ: AMD flat; Bank Central Asia steady");
        Assert.Equal(new[] { "MSFT", "NVDA", "AMD", "BBCA.JK" }, t);

        var e = EventClassifier.Classify("Goldman upgrades Apple to buy after Q3 earnings beat; CEO to step down");
        Assert.True(e.HasFlag(NewsEventType.AnalystUpgrade));
        Assert.True(e.HasFlag(NewsEventType.Earnings));
        Assert.True(e.HasFlag(NewsEventType.ManagementChange));
        Assert.False(e.HasFlag(NewsEventType.Dividend));
        Assert.True(EventClassifier.Classify("Board approves $10B share repurchase program").HasFlag(NewsEventType.Buyback));
    }

    [Fact]
    public void DetectsNearDuplicates()
    {
        var d = new DuplicateDetector();
        var a = "Apple reports record quarterly revenue driven by iPhone sales in China";
        var b = "Apple reports record quarterly revenue driven by iPhone sales in China, shares up";
        var c = "Oil prices slide as OPEC signals higher output next month";
        Assert.True(d.Jaccard(a, b) > 0.7);
        Assert.True(DuplicateDetector.EstimateSimilarity(d.Signature(a), d.Signature(b)) > 0.6);
        Assert.True(DuplicateDetector.EstimateSimilarity(d.Signature(a), d.Signature(c)) < 0.2);
        var now = DateTimeOffset.UtcNow;
        var items = new[] { new NewsItem(b, now), new NewsItem(a, now.AddMinutes(-5)), new NewsItem(c, now) };
        var dedup = d.Deduplicate(items);
        Assert.Equal(2, dedup.Count);
        Assert.Contains(dedup, n => n.Title == a);
    }

    [Fact]
    public void SentimentSeriesDecays()
    {
        var day0 = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var news = new[]
        {
            new ScoredNews(new NewsItem("a", day0) { Tickers = new[] { "AAPL" } }, new NewsSentiment(1, "positive", 1, 0, 0, 0, 0, 1), NewsEventType.None),
            new ScoredNews(new NewsItem("b", day0.AddDays(3)) { Tickers = new[] { "AAPL" } }, new NewsSentiment(-1, "negative", 0, 1, 0, 0, 0, 1), NewsEventType.None),
        };
        var s = SentimentTimeSeries.Build(news, "AAPL", halfLifeDays: 3);
        Assert.Equal(4, s.Count);
        Assert.Equal(1, s[0].DecayedScore, 12);
        Assert.Equal(1, s[2].DecayedScore, 12);
        Assert.Equal((0.5 - 1) / 1.5, s[3].DecayedScore, 12);
        Assert.True(double.IsNaN(s[1].MeanScore));
    }

    [Fact]
    public void AnalyzesEarningsCall()
    {
        var transcript =
            "Good morning. Revenue grew 20% to a record level and margins improved strongly. We expect continued growth next year and raise our guidance. " +
            "Operator: We will now begin the question-and-answer session. " +
            "Analyst: Are you worried about the slowdown? CEO: There is uncertainty and some risk of weaker demand, and costs may rise. Supply shortages could delay shipments.";
        var a = EarningsCallAnalyzer.Analyze(transcript);
        Assert.True(a.PreparedRemarks.Score > 0);
        Assert.True(a.QuestionAndAnswer.Score < a.PreparedRemarks.Score);
        Assert.True(a.ToneShift < 0);
        Assert.NotEmpty(a.ForwardLookingStatements);
        Assert.True(a.UncertaintyRatio > 0);
        Assert.NotEmpty(a.TopTopics);
    }

    [Fact]
    public async Task PipelineSummarisesPerTicker()
    {
        var now = DateTimeOffset.UtcNow;
        var items = new[]
        {
            new NewsItem("Apple beats estimates with record profit", now.AddDays(-2)),
            new NewsItem("Apple beats estimates with record profit!", now.AddDays(-2).AddMinutes(3)),
            new NewsItem("Apple faces antitrust lawsuit, shares fall", now.AddDays(-1)),
            new NewsItem("Tesla unveils new model", now),
        };
        var intel = new NewsIntelligence();
        var scored = await intel.ProcessAsync(items);
        Assert.Equal(3, scored.Count);
        var aapl = NewsIntelligence.Summarize(scored, "AAPL");
        Assert.Equal(2, aapl.ArticleCount);
        Assert.Equal(1, aapl.Positive);
        Assert.Equal(1, aapl.Negative);
        Assert.True(aapl.SentimentTrend < 0);
        Assert.True(aapl.EventCounts.ContainsKey(NewsEventType.LegalRegulatory));
        Assert.Single(aapl.TopNegative);

        var custom = new DelegateSentimentModel("fixed", (_, _) => Task.FromResult(("positive", 0.5)));
        var s2 = await new NewsIntelligence(custom).ProcessAsync(items, deduplicate: false);
        Assert.All(s2, s => Assert.Equal(0.5, s.Sentiment.Score));
    }
}
