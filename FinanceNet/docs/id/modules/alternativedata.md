# Finance.AlternativeData

[← Indeks](../index.md) · [English](../../en/modules/alternativedata.md) · Paket: `Gravicode.Finance.AlternativeData`

Sinyal dari luar data harga: obrolan investor ritel di media sosial, skor ESG, jaringan rantai pasok,
dan cuaca. Setiap area berada di namespace tersendiri.

Namespace: `Finance.AlternativeData.Social`, `.Esg`, `.SupplyChain`, `.Weather`.

## Media sosial

**Konsep.** *Cashtag* (`$TSLA`) dan sebutan ticker diekstrak dari postingan; setiap postingan diberi
skor sentimen; skor diagregasi per simbol dan periode, opsional dibobot dengan **engagement** —
`log(1 + likes + 2·comments + 3·shares)`, sub-linear supaya satu postingan viral tidak mendominasi.
**Buzz** adalah z-score jumlah sebutan terhadap riwayat rolling-nya; lonjakan di atas 3σ menandakan
perhatian yang tidak biasa.

```csharp
using Finance.AlternativeData.Social;

var reddit = new RedditClient(new HttpClient());
IReadOnlyList<SocialPost> posts = await reddit.GetPostsAsync("wallstreetbets", sort: "new", limit: 100);

var known = new HashSet<string> { "AAPL", "NVDA", "TSLA", "GME" };
IReadOnlyList<string> tickers = MentionExtractor.Extract("Loading $NVDA calls, TSLA looks weak", known);

var aggregator = new SocialSentimentAggregator { KnownTickers = known };      // bawaan memakai scorer leksikon
IReadOnlyList<SymbolSentiment> daily = aggregator.Aggregate(posts, TimeSpan.FromDays(1));
// SymbolSentiment(Symbol, PeriodStart, Mentions, AverageSentiment, EngagementWeightedSentiment, BullishRatio, TotalEngagement)

double[] z = BuzzDetector.ZScores(hourlyMentionCounts, window: 24);
int[] spikes = BuzzDetector.Spikes(hourlyMentionCounts, window: 24, threshold: 3);
```

Model apa pun bisa dipasang sebagai scorer — misalnya FinBERT atau LLM dari `Finance.LLM`:

```csharp
var finbertScorer = new DelegateSentimentScorer(text => MyFinBertScore(text));   // mengembalikan [-1, 1]
var withFinBert = new SocialSentimentAggregator(finbertScorer);
```

## ESG

**Konsep.** Skor pilar Environmental, Social, dan Governance; tingkat *kontroversi*; *intensitas
karbon* (tCO₂e per $ juta pendapatan). **WACI** — weighted-average carbon intensity — adalah metrik
karbon portofolio standar (TCFD).

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

## Rantai pasok

**Konsep.** Perusahaan adalah simpul; tautan *pemasok → pelanggan* membawa bobot *ketergantungan*
(porsi input pelanggan). **PageRank** menemukan perusahaan yang sentral secara sistemik; **eksposur**
mengukur seberapa besar sebuah perusahaan bergantung, langsung maupun tidak langsung, pada seorang
pemasok; **simulasi guncangan** merambatkan gangguan ke hilir.

```csharp
using Finance.AlternativeData.SupplyChain;

var graph = new SupplyChainGraph()
    .AddLink("TSMC", "Apple", 0.9)
    .AddLink("TSMC", "Nvidia", 0.95)
    .AddLink("Nvidia", "Microsoft", 0.3)
    .AddLink("Apple", "Retailer", 0.2);

Dictionary<string, double> centrality = graph.PageRank();
Dictionary<string, double> exposure = graph.ExposureTo("TSMC");
ShockResult shock = graph.SimulateShock("TSMC", severity: 0.5);   // Impact per perusahaan, Rounds
```

## Cuaca

**Konsep.** *Heating degree days* (HDD) dan *cooling degree days* (CDD) mengukur seberapa jauh suhu
rata-rata harian berada di bawah / di atas suhu dasar (bawaan 18 °C). Keduanya memengaruhi permintaan
energi dan menjadi underlying derivatif cuaca.

```csharp
using Finance.AlternativeData.Weather;

var meteo = new OpenMeteoClient(new HttpClient());               // API Open-Meteo gratis, tanpa kunci
WeatherObservation[] history = await meteo.GetHistoryAsync(40.71, -74.01, new DateOnly(2023, 1, 1), new DateOnly(2023, 12, 31));
var (hdd, cdd) = WeatherAnalytics.Accumulate(history);

Dictionary<DateOnly, double> degreeDays = WeatherAnalytics.DegreeDaySeries(history);
var lagged = WeatherAnalytics.Correlate(degreeDays, natGasReturns, maxLag: 5);   // (Lag, Correlation, Count)
Dictionary<DateOnly, double> anomalies = WeatherAnalytics.Anomalies(history);
```

## Catatan dan batasan

- Reddit makin sering memblokir listing `.json` tanpa autentikasi. Dari banyak jaringan Anda akan
  mendapat HTTP 403/429; halaman Social Sentiment di Gallery lalu beralih ke postingan sintetis yang
  diberi label jelas.
- `SocialLexiconScorer` bawaan adalah leksikon ringkas untuk keuangan/ritel; tidak memahami sarkasme.
  Gunakan `DelegateSentimentScorer` untuk memasang model yang lebih kuat.
- Data ESG dan rantai pasok disediakan pengguna — modul ini menyediakan analitik, bukan vendor data.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
