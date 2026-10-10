using System.Text.RegularExpressions;
using Finance.Mathematics;

namespace Finance.NewsAnalytics;

/// <summary>A news item with its sentiment and detected events.</summary>
public sealed record ScoredNews(NewsItem Item, NewsSentiment Sentiment, NewsEventType Events);

/// <summary>One day of aggregated sentiment for a ticker.</summary>
public readonly record struct DailySentiment(DateOnly Date, int Count, double MeanScore, double DecayedScore);

/// <summary>
/// Builds a daily sentiment series per ticker: raw daily means plus an exponentially decayed
/// index S_d = Σ score_i · 2^{-(d - d_i)/halfLife} / Σ 2^{-(d - d_i)/halfLife} over all news up to day d.
/// </summary>
public static class SentimentTimeSeries
{
    public static IReadOnlyList<DailySentiment> Build(IEnumerable<ScoredNews> news, string ticker, double halfLifeDays = 3, DateOnly? from = null, DateOnly? to = null)
    {
        var relevant = news.Where(n => n.Item.Tickers.Contains(ticker, StringComparer.OrdinalIgnoreCase))
            .Select(n => (Date: DateOnly.FromDateTime(n.Item.Published.UtcDateTime), n.Sentiment.Score)).OrderBy(n => n.Date).ToList();
        if (relevant.Count == 0) return Array.Empty<DailySentiment>();
        var start = from ?? relevant[0].Date;
        var end = to ?? relevant[^1].Date;
        var byDay = relevant.GroupBy(r => r.Date).ToDictionary(g => g.Key, g => g.Select(x => x.Score).ToArray());
        var series = new List<DailySentiment>();
        double decay = Math.Pow(0.5, 1 / halfLifeDays), num = 0, den = 0;
        foreach (var r in relevant.Where(r => r.Date < start)) { double w = Math.Pow(decay, start.DayNumber - r.Date.DayNumber); num += w * r.Score; den += w; }
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (d != start) { num *= decay; den *= decay; }
            byDay.TryGetValue(d, out var scores);
            if (scores != null) foreach (var s in scores) { num += s; den += 1; }
            series.Add(new DailySentiment(d, scores?.Length ?? 0, scores is { Length: > 0 } ? scores.Average() : double.NaN, den > 0 ? num / den : 0));
        }
        return series;
    }
}

/// <summary>
/// Near-duplicate detection: word 3-gram shingles, 128-permutation MinHash signatures and LSH-free
/// pairwise signature comparison (fine for the few thousand headlines a day a desk sees).
/// </summary>
public sealed class DuplicateDetector(int shingleSize = 3, int permutations = 128, double threshold = 0.6)
{
    private readonly ulong[] _a = Seeds(permutations, 0x51ED2701UL);
    private readonly ulong[] _b = Seeds(permutations, 0xA5A5F00DUL);

    /// <summary>Exact Jaccard similarity of two texts' shingle sets.</summary>
    public double Jaccard(string x, string y)
    {
        var a = Shingles(x);
        var b = Shingles(y);
        if (a.Count == 0 && b.Count == 0) return 1;
        int inter = a.Count(b.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }

    /// <summary>MinHash signature of a text.</summary>
    public ulong[] Signature(string text)
    {
        var sig = Enumerable.Repeat(ulong.MaxValue, permutations).ToArray();
        foreach (var s in Shingles(text))
        {
            ulong h = Fnv(s);
            for (int i = 0; i < permutations; i++)
            {
                ulong v = _a[i] * h + _b[i];
                v ^= v >> 29;
                if (v < sig[i]) sig[i] = v;
            }
        }
        return sig;
    }

    /// <summary>Estimated Jaccard similarity from two signatures.</summary>
    public static double EstimateSimilarity(ulong[] a, ulong[] b)
    {
        int eq = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] == b[i]) eq++;
        return (double)eq / a.Length;
    }

    /// <summary>Groups near-duplicate items; each group's first element is the earliest item.</summary>
    public IReadOnlyList<IReadOnlyList<NewsItem>> Cluster(IReadOnlyList<NewsItem> items)
    {
        var ordered = items.OrderBy(i => i.Published).ToList();
        var sigs = ordered.Select(i => Signature(i.Title)).ToList();
        var groups = new List<List<int>>();
        for (int i = 0; i < ordered.Count; i++)
        {
            // MinHash is only an estimate (σ ≈ 0.04 at 128 permutations), so near the threshold confirm with the exact Jaccard.
            var g = groups.FirstOrDefault(gr =>
            {
                double est = EstimateSimilarity(sigs[gr[0]], sigs[i]);
                if (est >= threshold + 0.15) return true;
                return est >= threshold - 0.15 && Jaccard(ordered[gr[0]].Title, ordered[i].Title) >= threshold;
            });
            if (g is null) groups.Add(new List<int> { i });
            else g.Add(i);
        }
        return groups.Select(g => (IReadOnlyList<NewsItem>)g.Select(i => ordered[i]).ToList()).ToList();
    }

    /// <summary>One representative (earliest) per near-duplicate cluster.</summary>
    public IReadOnlyList<NewsItem> Deduplicate(IReadOnlyList<NewsItem> items) => Cluster(items).Select(g => g[0]).ToList();

    private HashSet<string> Shingles(string text)
    {
        var t = FinancialLexicon.Tokenize(text);
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (t.Count < shingleSize) { if (t.Count > 0) set.Add(string.Join(' ', t)); return set; }
        for (int i = 0; i + shingleSize <= t.Count; i++) set.Add(string.Join(' ', t.Skip(i).Take(shingleSize)));
        return set;
    }

    private static ulong Fnv(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    private static ulong[] Seeds(int n, ulong seed)
    {
        var r = new Finance.Core.FastRandom(seed);
        return Enumerable.Range(0, n).Select(_ => r.NextUInt64() | 1).ToArray();
    }
}

/// <summary>Result of an earnings-call transcript analysis.</summary>
public sealed record TranscriptAnalysis(
    NewsSentiment Overall,
    NewsSentiment PreparedRemarks,
    NewsSentiment QuestionAndAnswer,
    double UncertaintyRatio,
    IReadOnlyList<string> ForwardLookingStatements,
    IReadOnlyList<(string Term, int Count)> TopTopics,
    double ToneShift);

/// <summary>
/// Lexicon-based earnings call analysis: splits prepared remarks from Q&amp;A at the operator's
/// hand-off, scores tone of each, measures uncertainty and extracts forward-looking sentences.
/// </summary>
public static partial class EarningsCallAnalyzer
{
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "that", "with", "this", "are", "was", "our", "we", "you", "have", "from", "will", "be", "in", "of", "to", "a", "on",
        "is", "it", "as", "at", "by", "an", "or", "so", "but", "not", "they", "their", "has", "had", "were", "been", "its", "i", "us", "about",
        "more", "than", "also", "which", "there", "what", "would", "can", "very", "just", "think", "know", "really", "like", "quarter", "year",
        "thank", "thanks", "question", "yes", "well", "going", "get", "do", "that's", "we're", "it's", "i'm", "into", "over", "these", "those",
        "some", "all", "any", "if", "when", "how", "your", "operator", "call", "up", "out", "one", "now", "see", "said",
    };

    public static TranscriptAnalysis Analyze(string transcript)
    {
        var model = new LexiconSentimentModel();
        var split = QaMarker().Match(transcript);
        string prepared = split.Success ? transcript[..split.Index] : transcript;
        string qa = split.Success ? transcript[split.Index..] : "";
        var overall = model.Score(transcript);
        var p = model.Score(prepared);
        var q = qa.Length > 0 ? model.Score(qa) : NewsSentiment.Neutral();

        var sentences = SentenceSplit().Split(transcript).Select(s => s.Trim()).Where(s => s.Length > 0);
        var fls = sentences.Where(s => ForwardLooking().IsMatch(s)).Take(25).ToList();

        var topics = FinancialLexicon.Tokenize(transcript).Where(t => t.Length > 3 && !Stop.Contains(t))
            .GroupBy(t => t).Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).Take(15).ToList();
        return new TranscriptAnalysis(overall, p, q, overall.UncertaintyRatio, fls, topics, q.Score - p.Score);
    }

    [GeneratedRegex(@"(question[- ]and[- ]answer|q\s*&\s*a session|we will now (begin|open)|open (it|the (call|line)) (up )?(for|to) questions|first question)", RegexOptions.IgnoreCase)]
    private static partial Regex QaMarker();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"\b(expect|anticipate|outlook|guidance|forecast|project(ed|ion)?|plan to|intend|going forward|next (quarter|year)|fiscal (20\d\d|year)|we see|will (be|grow|continue|deliver))\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForwardLooking();
}

/// <summary>Per-ticker news intelligence summary.</summary>
public sealed record TickerIntelligence(
    string Ticker,
    int ArticleCount,
    double AverageSentiment,
    double RecentSentiment,
    double SentimentTrend,
    int Positive,
    int Negative,
    int Neutral,
    IReadOnlyDictionary<NewsEventType, int> EventCounts,
    IReadOnlyList<ScoredNews> TopPositive,
    IReadOnlyList<ScoredNews> TopNegative,
    IReadOnlyList<DailySentiment> Daily);

/// <summary>
/// End-to-end news pipeline: tag tickers, de-duplicate, score sentiment, classify events and
/// summarise per ticker.
/// </summary>
public sealed class NewsIntelligence(ISentimentModel? sentimentModel = null, EntityExtractor? extractor = null)
{
    private readonly ISentimentModel _model = sentimentModel ?? new LexiconSentimentModel();
    private readonly EntityExtractor _extractor = extractor ?? new EntityExtractor();

    /// <summary>Tags, de-duplicates (optional) and scores the items.</summary>
    public async Task<IReadOnlyList<ScoredNews>> ProcessAsync(IEnumerable<NewsItem> items, bool deduplicate = true, CancellationToken cancellationToken = default)
    {
        var list = items.Select(_extractor.Tag).ToList();
        if (deduplicate) list = new DuplicateDetector().Deduplicate(list).ToList();
        var scored = new List<ScoredNews>(list.Count);
        foreach (var item in list)
        {
            var s = await _model.ScoreAsync(item.Text, cancellationToken).ConfigureAwait(false);
            scored.Add(new ScoredNews(item, s, EventClassifier.Classify(item.Text)));
        }
        return scored;
    }

    /// <summary>Summary for one ticker. Trend = mean score of the newer half minus the older half.</summary>
    public static TickerIntelligence Summarize(IReadOnlyList<ScoredNews> scored, string ticker, double halfLifeDays = 3)
    {
        var mine = scored.Where(s => s.Item.Tickers.Contains(ticker, StringComparer.OrdinalIgnoreCase)).OrderBy(s => s.Item.Published).ToList();
        var scores = mine.Select(m => m.Sentiment.Score).ToArray();
        double avg = scores.Length > 0 ? Stats.Mean(scores) : 0;
        int half = scores.Length / 2;
        double trend = scores.Length >= 2 ? Stats.Mean(scores.AsSpan(half)) - Stats.Mean(scores.AsSpan(0, Math.Max(1, half))) : 0;
        var events = new Dictionary<NewsEventType, int>();
        foreach (var m in mine)
            foreach (var f in EventClassifier.Flags(m.Events)) events[f] = events.GetValueOrDefault(f) + 1;
        var daily = SentimentTimeSeries.Build(mine, ticker, halfLifeDays);
        return new TickerIntelligence(ticker, mine.Count, avg, daily.Count > 0 ? daily[^1].DecayedScore : 0, trend,
            mine.Count(m => m.Sentiment.Label == "positive"), mine.Count(m => m.Sentiment.Label == "negative"), mine.Count(m => m.Sentiment.Label == "neutral"),
            events, mine.OrderByDescending(m => m.Sentiment.Score).Where(m => m.Sentiment.Score > 0).Take(5).ToList(),
            mine.OrderBy(m => m.Sentiment.Score).Where(m => m.Sentiment.Score < 0).Take(5).ToList(), daily);
    }
}
