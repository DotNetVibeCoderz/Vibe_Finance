# Finance.AlternativeData

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/alternativedata.md) · Package: `Gravicode.Finance.AlternativeData`

Signals from outside price data: retail social-media chatter, ESG scores, supply-chain networks
and weather. Each area is a self-contained namespace.

Namespaces: `Finance.AlternativeData.Social`, `.Esg`, `.SupplyChain`, `.Weather`.

## Social media

**Concepts.** *Cashtags* (`$TSLA`) and ticker mentions are extracted from posts; each post is
scored for sentiment; scores are aggregated per symbol and period, optionally weighted by
**engagement** — `log(1 + likes + 2·comments + 3·shares)`, sub-linear so one viral post cannot
dominate. **Buzz** is the z-score of mention counts against their rolling history; a spike above 3σ
flags abnormal attention.

```csharp
using Finance.AlternativeData.Social;

var reddit = new RedditClient(new HttpClient());
IReadOnlyList<SocialPost> posts = await reddit.GetPostsAsync("wallstreetbets", sort: "new", limit: 100);

var known = new HashSet<string> { "AAPL", "NVDA", "TSLA", "GME" };
IReadOnlyList<string> tickers = MentionExtractor.Extract("Loading $NVDA calls, TSLA looks weak", known);

var aggregator = new SocialSentimentAggregator { KnownTickers = known };      // lexicon scorer by default
IReadOnlyList<SymbolSentiment> daily = aggregator.Aggregate(posts, TimeSpan.FromDays(1));
// SymbolSentiment(Symbol, PeriodStart, Mentions, AverageSentiment, EngagementWeightedSentiment, BullishRatio, TotalEngagement)

double[] z = BuzzDetector.ZScores(hourlyMentionCounts, window: 24);
int[] spikes = BuzzDetector.Spikes(hourlyMentionCounts, window: 24, threshold: 3);
```

Plug any model in as the scorer — for example FinBERT or an LLM from `Finance.LLM`:

```csharp
var finbertScorer = new DelegateSentimentScorer(text => MyFinBertScore(text));   // returns [-1, 1]
var withFinBert = new SocialSentimentAggregator(finbertScorer);
```

## ESG

**Concepts.** Environmental, Social and Governance pillar scores; *controversy* level; *carbon
intensity* (tCO₂e per $M revenue). **WACI** — weighted-average carbon intensity — is the standard
portfolio carbon metric (TCFD).

```csharp
using Finance.AlternativeData.Esg;

var scores = new Dictionary<string, EsgScore>
{
    ["AAA"] = new("AAA", Environmental: 72, Social: 65, Governance: 80, Controversy: 1, CarbonIntensity: 120, Sector: "Tech"),
    ["BBB"] = new("BBB", 35, 50, 55, Controversy: 4, CarbonIntensity: 820, Sector: "Energy"),
};
var weights = new Dictionary<string, double> { ["AAA"] = 0.6, ["BBB"] = 0.4 };

PortfolioEsg esg = EsgAnalytics.Portfolio(weights, scores);    // Environmental, Social, Governance, Total, Waci, Coverage
IReadOnlyList<EsgFlag> flags = EsgAnalytics.Flags(weights, scores, maxControversy: 3, maxCarbonIntensity: 500);
IReadOnlyList<string> leaders = EsgAnalytics.BestInClass(scores.Values, keepFraction: 0.5);
Dictionary<string, double> tilted = EsgAnalytics.Tilt(weights, scores, strength: 0.5);
```

## Supply chain

**Concepts.** Companies are nodes; a link *supplier → customer* carries a *dependency* weight (the
share of the customer's inputs). **PageRank** finds systemically central firms; **exposure**
measures how much a company depends, directly and indirectly, on a supplier; a **shock
simulation** propagates a disruption downstream.

```csharp
using Finance.AlternativeData.SupplyChain;

var graph = new SupplyChainGraph()
    .AddLink("TSMC", "Apple", 0.9)
    .AddLink("TSMC", "Nvidia", 0.95)
    .AddLink("Nvidia", "Microsoft", 0.3)
    .AddLink("Apple", "Retailer", 0.2);

Dictionary<string, double> centrality = graph.PageRank();
Dictionary<string, double> exposure = graph.ExposureTo("TSMC");
ShockResult shock = graph.SimulateShock("TSMC", severity: 0.5);   // Impact per company, Rounds
```

## Weather

**Concepts.** *Heating degree days* (HDD) and *cooling degree days* (CDD) measure how far the
daily mean temperature falls below / rises above a base (18 °C by default). They drive energy
demand and are the underlying of weather derivatives.

```csharp
using Finance.AlternativeData.Weather;

var meteo = new OpenMeteoClient(new HttpClient());               // free Open-Meteo API, no key
WeatherObservation[] history = await meteo.GetHistoryAsync(40.71, -74.01, new DateOnly(2023, 1, 1), new DateOnly(2023, 12, 31));
var (hdd, cdd) = WeatherAnalytics.Accumulate(history);

Dictionary<DateOnly, double> degreeDays = WeatherAnalytics.DegreeDaySeries(history);
var lagged = WeatherAnalytics.Correlate(degreeDays, natGasReturns, maxLag: 5);   // (Lag, Correlation, Count)
Dictionary<DateOnly, double> anomalies = WeatherAnalytics.Anomalies(history);
```

## Notes and limitations

- Reddit increasingly blocks unauthenticated `.json` listings. Expect HTTP 403/429 from many
  networks; the Gallery's Social Sentiment page then falls back to clearly labelled synthetic posts.
- The built-in `SocialLexiconScorer` is a compact finance/retail lexicon; it does not understand
  sarcasm. Use `DelegateSentimentScorer` to plug in a stronger model.
- ESG and supply-chain inputs are user-supplied — the module provides analytics, not data vendors.

---

Made by Gravicode Studios, led by Kang Fadhil
