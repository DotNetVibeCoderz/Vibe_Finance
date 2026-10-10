# Finance.NewsAnalytics

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/newsanalytics.md) · Package: `Gravicode.Finance.NewsAnalytics`

A news-intelligence pipeline: read RSS/Atom feeds, tag tickers, remove near-duplicate stories,
score sentiment with a finance-specific lexicon (or any model you plug in), classify market events,
and summarise per ticker — including a decay-weighted daily sentiment series and earnings-call
transcript analysis.

Namespace: `Finance.NewsAnalytics`.

## Key concepts

- **Finance-specific sentiment** — general sentiment lexicons misread finance ("liability",
  "tax", "crude" are not negative here). The built-in lexicon follows the Loughran-McDonald
  approach: curated *positive*, *negative*, *uncertainty*, *litigious* and *constraining* word lists,
  with negation handling ("not profitable") and intensifiers.
- **Near-duplicate detection** — the same story is syndicated many times. Word *shingles* hashed
  into **MinHash** signatures estimate Jaccard similarity cheaply; similar items are clustered.
- **Event classification** — rule-based flags for earnings, M&A, guidance, analyst up/downgrades,
  legal/regulatory, management change, dividends, buybacks, product launches and macro news.
- **Sentiment decay** — older news matters less; daily scores are combined with an exponential
  half-life.

## Pipeline

```csharp
using Finance.NewsAnalytics;

var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 Finance.Net");
var reader = new FeedReader(http);

IReadOnlyList<NewsItem> items = await reader.ReadManyAsync(new[]
{
    FeedReader.YahooFinanceUrl("AAPL,MSFT"),
    FeedReader.GoogleNewsUrl("NVDA stock"),
});

var engine = new NewsIntelligence();                              // lexicon model + default entity extractor
IReadOnlyList<ScoredNews> scored = await engine.ProcessAsync(items, deduplicate: true);

TickerIntelligence nvda = NewsIntelligence.Summarize(scored, "NVDA", halfLifeDays: 3);
Console.WriteLine($"{nvda.ArticleCount} articles, avg {nvda.AverageSentiment:+0.00}, recent {nvda.RecentSentiment:+0.00}, trend {nvda.SentimentTrend:+0.00}");
foreach (var top in nvda.TopNegative) Console.WriteLine($"  ▼ {top.Item.Title}");
foreach (DailySentiment d in nvda.Daily) Console.WriteLine($"{d.Date}: {d.Count} items, decayed {d.DecayedScore:+0.00}");
```

## Building blocks

```csharp
var lexicon = new LexiconSentimentModel();
NewsSentiment s = lexicon.Score("Profit warning: the company expects significantly lower margins amid litigation.");
// Score in [-1, 1], Label, Positive, Negative, Uncertainty, Litigious, Constraining, WordCount, UncertaintyRatio

var extractor = new EntityExtractor();
extractor.AddCompany("Bank Central Asia", "BBCA.JK");
IReadOnlyList<string> tickers = extractor.ExtractTickers("Bank Central Asia and $AAPL rallied; NASDAQ: MSFT slipped.");

NewsEventType ev = EventClassifier.Classify("Analysts upgrade Tesla to buy after record deliveries");
foreach (var flag in EventClassifier.Flags(ev)) Console.WriteLine(flag);   // AnalystUpgrade, ...

var dedup = new DuplicateDetector(shingleSize: 3, permutations: 128, threshold: 0.6);
IReadOnlyList<NewsItem> unique = dedup.Deduplicate(items);
double jac = dedup.Jaccard("Apple beats estimates", "Apple beats Wall Street estimates");

TranscriptAnalysis call = EarningsCallAnalyzer.Analyze(transcriptText);
Console.WriteLine($"remarks {call.PreparedRemarks.Score:+0.00} vs Q&A {call.QuestionAndAnswer.Score:+0.00}, shift {call.ToneShift:+0.00}, uncertainty {call.UncertaintyRatio:P2}");
```

### Plugging in FinBERT or an LLM

```csharp
var finbertModel = new DelegateSentimentModel("FinBERT", async (text, ct) =>
{
    var r = await finbert.AnalyzeAsync(text, ct);                 // Finance.LLM.FinBertSentiment
    return (r.Label, r.Score);
});
var engineWithFinBert = new NewsIntelligence(finbertModel);
```

## Notes and limitations

- The built-in lexicon is English, matching the global financial news sources it targets.
- Feeds are fetched over the public internet; Yahoo and Google may rate-limit. Send a browser-like
  `User-Agent`.
- Lexicon scores are transparent and fast but literal — they miss sarcasm and context. Use a
  `DelegateSentimentModel` for model-based scoring.
- Event classification is rule-based (keyword patterns), so it favours precision on clearly worded
  headlines.

---

Made by Gravicode Studios, led by Kang Fadhil
