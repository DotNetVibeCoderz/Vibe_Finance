# Finance.NewsAnalytics

[← Indeks](../index.md) · [English](../../en/modules/newsanalytics.md) · Paket: `Gravicode.Finance.NewsAnalytics`

Pipeline intelijen berita: membaca feed RSS/Atom, menandai ticker, membuang berita yang nyaris
duplikat, memberi skor sentimen dengan leksikon khusus keuangan (atau model apa pun yang Anda pasang),
mengklasifikasikan event pasar, dan merangkum per ticker — termasuk deret sentimen harian berbobot
peluruhan dan analisis transkrip earnings call.

Namespace: `Finance.NewsAnalytics`.

## Konsep utama

- **Sentimen khusus keuangan** — leksikon sentimen umum salah membaca konteks keuangan ("liability",
  "tax", "crude" tidak bernada negatif di sini). Leksikon bawaan mengikuti pendekatan
  Loughran-McDonald: daftar kata *positif*, *negatif*, *ketidakpastian*, *litigasi*, dan *pembatasan*
  yang dikurasi, dengan penanganan negasi ("not profitable") dan penguat (intensifier).
- **Deteksi nyaris-duplikat** — satu berita disebarluaskan berkali-kali. *Shingle* kata yang di-hash
  menjadi signature **MinHash** mengestimasi kemiripan Jaccard dengan murah; item yang mirip
  dikelompokkan.
- **Klasifikasi event** — penanda berbasis aturan untuk laporan laba, M&A, guidance, kenaikan/penurunan
  rekomendasi analis, hukum/regulasi, pergantian manajemen, dividen, buyback, peluncuran produk, dan
  berita makro.
- **Peluruhan sentimen** — berita lama makin kurang penting; skor harian digabung dengan half-life
  eksponensial.

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

var engine = new NewsIntelligence();                              // model leksikon + entity extractor bawaan
IReadOnlyList<ScoredNews> scored = await engine.ProcessAsync(items, deduplicate: true);

TickerIntelligence nvda = NewsIntelligence.Summarize(scored, "NVDA", halfLifeDays: 3);
Console.WriteLine($"{nvda.ArticleCount} artikel, rata-rata {nvda.AverageSentiment:+0.00}, terkini {nvda.RecentSentiment:+0.00}, tren {nvda.SentimentTrend:+0.00}");
foreach (var top in nvda.TopNegative) Console.WriteLine($"  ▼ {top.Item.Title}");
foreach (DailySentiment d in nvda.Daily) Console.WriteLine($"{d.Date}: {d.Count} item, berbobot peluruhan {d.DecayedScore:+0.00}");
```

## Komponen dasar

```csharp
var lexicon = new LexiconSentimentModel();
NewsSentiment s = lexicon.Score("Profit warning: the company expects significantly lower margins amid litigation.");
// Score dalam [-1, 1], Label, Positive, Negative, Uncertainty, Litigious, Constraining, WordCount, UncertaintyRatio

var extractor = new EntityExtractor();
extractor.AddCompany("Bank Central Asia", "BBCA.JK");
IReadOnlyList<string> tickers = extractor.ExtractTickers("Bank Central Asia and $AAPL rallied; NASDAQ: MSFT slipped.");

NewsEventType ev = EventClassifier.Classify("Analysts upgrade Tesla to buy after record deliveries");
foreach (var flag in EventClassifier.Flags(ev)) Console.WriteLine(flag);   // AnalystUpgrade, ...

var dedup = new DuplicateDetector(shingleSize: 3, permutations: 128, threshold: 0.6);
IReadOnlyList<NewsItem> unique = dedup.Deduplicate(items);
double jac = dedup.Jaccard("Apple beats estimates", "Apple beats Wall Street estimates");

TranscriptAnalysis call = EarningsCallAnalyzer.Analyze(transcriptText);
Console.WriteLine($"pemaparan {call.PreparedRemarks.Score:+0.00} vs tanya-jawab {call.QuestionAndAnswer.Score:+0.00}, pergeseran {call.ToneShift:+0.00}, ketidakpastian {call.UncertaintyRatio:P2}");
```

### Memasang FinBERT atau LLM

```csharp
var finbertModel = new DelegateSentimentModel("FinBERT", async (text, ct) =>
{
    var r = await finbert.AnalyzeAsync(text, ct);                 // Finance.LLM.FinBertSentiment
    return (r.Label, r.Score);
});
var engineWithFinBert = new NewsIntelligence(finbertModel);
```

## Catatan dan batasan

- Leksikon bawaan berbahasa Inggris, sesuai bahasa sumber berita keuangan global yang menjadi sasarannya.
- Feed diambil lewat internet publik; Yahoo dan Google bisa membatasi laju permintaan. Kirim
  `User-Agent` yang menyerupai browser.
- Skor leksikon transparan dan cepat tetapi harfiah — tidak menangkap sarkasme dan konteks. Gunakan
  `DelegateSentimentModel` untuk penilaian berbasis model.
- Klasifikasi event berbasis aturan (pola kata kunci), sehingga lebih mengutamakan presisi pada judul
  yang tersurat jelas.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
