using Finance.AlternativeData.Esg;
using Finance.AlternativeData.Social;
using Finance.AlternativeData.SupplyChain;
using Finance.AlternativeData.Weather;

namespace Finance.Data.Tests;

public class AlternativeDataTests
{
    [Fact]
    public void Mentions_cashtags_and_known_tickers()
    {
        var m = MentionExtractor.Extract("Loading $aapl and $BRK.B, also NVDA but not CEO or AI", new HashSet<string> { "NVDA", "AI" });
        Assert.Equal(new[] { "AAPL", "BRK.B", "NVDA" }, m);
        Assert.Empty(MentionExtractor.Extract("price is $100"));
    }

    [Fact]
    public void Lexicon_scorer_handles_negation()
    {
        var s = new SocialLexiconScorer();
        Assert.True(s.Score("Super bullish, buying calls 🚀") > 0.5);
        Assert.True(s.Score("this will crash, total scam") < -0.5);
        Assert.True(s.Score("not bullish at all") < 0);
        Assert.Equal(0, s.Score("the weather is nice"));
    }

    [Fact]
    public void Reddit_listing_parses_and_aggregates()
    {
        var posts = RedditClient.ParseListing(Payloads.Reddit);
        Assert.Equal(2, posts.Count);
        Assert.Equal("reddit/stocks", posts[0].Source);
        Assert.Contains("Expecting a dump", posts[1].Text);
        var agg = new SocialSentimentAggregator { KnownTickers = new HashSet<string> { "TSLA" } }.Aggregate(posts, TimeSpan.FromDays(1));
        var nvda = Assert.Single(agg, a => a.Symbol == "NVDA");
        var tsla = Assert.Single(agg, a => a.Symbol == "TSLA");
        Assert.True(nvda.EngagementWeightedSentiment > 0);
        Assert.True(tsla.AverageSentiment < 0);
        Assert.Equal(1.0, nvda.BullishRatio);
    }

    [Fact]
    public void Delegate_scorer_is_pluggable()
    {
        var agg = new SocialSentimentAggregator(new DelegateSentimentScorer(_ => 5));
        var r = agg.Aggregate(new[] { new SocialPost("1", "x", "a", "$AMD", DateTime.UtcNow) }, TimeSpan.FromHours(1));
        Assert.Equal(1.0, r[0].AverageSentiment);
    }

    [Fact]
    public void Buzz_detector_flags_spike()
    {
        var counts = Enumerable.Range(0, 30).Select(i => 10.0 + (i % 3)).ToList();
        counts.Add(60);
        var spikes = BuzzDetector.Spikes(counts, 20, 3);
        Assert.Equal(new[] { 30 }, spikes);
        Assert.True(double.IsNaN(BuzzDetector.ZScores(counts, 20)[5]));
    }

    [Fact]
    public void Esg_portfolio_waci_flags_and_tilt()
    {
        var scores = new Dictionary<string, EsgScore>
        {
            ["A"] = new("A", 80, 70, 90, 0, 50, "Tech"),
            ["B"] = new("B", 20, 30, 40, 4, 900, "Energy"),
            ["C"] = new("C", 60, 60, 60, 1, 100, "Tech"),
        };
        var w = new Dictionary<string, double> { ["A"] = 0.5, ["B"] = 0.3, ["D"] = 0.2 };
        var p = EsgAnalytics.Portfolio(w, scores);
        Assert.Equal(0.8, p.Coverage, 9);
        Assert.Equal((0.5 * 80 + 0.3 * 20) / 0.8, p.Environmental, 9);
        Assert.Equal((0.5 * 50 + 0.3 * 900) / 0.8, p.Waci, 9);
        var flags = EsgAnalytics.Flags(w, scores);
        Assert.Contains(flags, f => f.Symbol == "B" && f.Reason.Contains("Controversy"));
        Assert.Contains(flags, f => f.Symbol == "B" && f.Reason.Contains("Carbon"));
        Assert.Contains(flags, f => f.Symbol == "D");
        var best = EsgAnalytics.BestInClass(scores.Values, 0.5, 3);
        Assert.Equal(new[] { "A" }, best);
        var tilted = EsgAnalytics.Tilt(new Dictionary<string, double> { ["A"] = 0.5, ["B"] = 0.5 }, scores);
        Assert.True(tilted["A"] > tilted["B"]);
        Assert.Equal(1.0, tilted.Values.Sum(), 9);
    }

    private static SupplyChainGraph Chain() => new SupplyChainGraph()
        .AddLink("TSMC", "Apple", 0.8)
        .AddLink("TSMC", "Nvidia", 0.9)
        .AddLink("Foxconn", "Apple", 0.5)
        .AddLink("Apple", "Retailer", 0.4)
        .AddLink("Nvidia", "Cloud", 0.6);

    [Fact]
    public void Supply_chain_pagerank_ranks_critical_supplier_first()
    {
        var pr = Chain().PageRank();
        Assert.Equal("TSMC", pr.OrderByDescending(kv => kv.Value).First().Key);
        Assert.Equal(1.0, pr.Values.Sum(), 6);
    }

    [Fact]
    public void Supply_chain_exposure_and_contagion()
    {
        var g = Chain();
        var exp = g.ExposureTo("TSMC");
        Assert.Equal(0.8, exp["Apple"], 9);
        Assert.Equal(0.8 * 0.4, exp["Retailer"], 9);
        Assert.Equal(0.9 * 0.6, exp["Cloud"], 9);
        Assert.False(exp.ContainsKey("Foxconn"));

        var shock = g.SimulateShock("TSMC", 1.0);
        Assert.Equal(1.0, shock.Impact["TSMC"]);
        Assert.Equal(0.8, shock.Impact["Apple"], 9);
        Assert.Equal(0.32, shock.Impact["Retailer"], 9);
        Assert.Equal(0, shock.Impact["Foxconn"]);
        Assert.Equal("TSMC", shock.Ranked.First().Key);
        Assert.Throws<ArgumentOutOfRangeException>(() => g.AddLink("X", "Y", 1.5));
    }

    [Fact]
    public void Open_meteo_parse_and_degree_days()
    {
        var obs = OpenMeteoClient.Parse(Payloads.OpenMeteo);
        Assert.Equal(3, obs.Length);
        Assert.Equal(17.7, obs[0].TempMean);
        Assert.Equal((21.7 + 9.8) / 2, obs[2].TempMean, 9); // mean missing → (min+max)/2
        Assert.True(double.IsNaN(obs[2].Precipitation));
        Assert.Equal(0.3, WeatherAnalytics.HeatingDegreeDays(17.7), 9);
        Assert.Equal(2, WeatherAnalytics.CoolingDegreeDays(20), 9);
        var (hdd, cdd) = WeatherAnalytics.Accumulate(obs);
        Assert.Equal(0.3 + 1.1 + (18 - 15.75), hdd, 9);
        Assert.Equal(0, cdd);
    }

    [Fact]
    public void Weather_correlation_finds_lagged_relationship()
    {
        var rng = new Finance.Core.FastRandom(5);
        var start = new DateOnly(2024, 1, 1);
        var weather = new Dictionary<DateOnly, double>();
        var temps = new double[200];
        for (int i = 0; i < 200; i++)
        {
            temps[i] = rng.NextGaussian();
            weather[start.AddDays(i)] = temps[i];
        }
        // Return on day i is driven by the temperature one day earlier.
        var returns = Enumerable.Range(0, 200).Select(i => (start.AddDays(i), i == 0 ? 0 : 0.8 * temps[i - 1] + 0.2 * rng.NextGaussian())).ToList();
        var c = WeatherAnalytics.Correlate(weather, returns, 2);
        Assert.True(c[1].Correlation > 0.9);
        Assert.True(Math.Abs(c[0].Correlation) < 0.3);
    }
}
