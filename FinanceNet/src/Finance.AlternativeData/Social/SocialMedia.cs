using System.Text.RegularExpressions;

namespace Finance.AlternativeData.Social;

/// <summary>A social media post (Reddit, X/Twitter, StockTwits, forum...).</summary>
public sealed record SocialPost(
    string Id,
    string Source,
    string Author,
    string Text,
    DateTime Time,
    int Likes = 0,
    int Comments = 0,
    int Shares = 0)
{
    /// <summary>log(1 + likes + 2·comments + 3·shares): sub-linear so one viral post cannot dominate.</summary>
    public double Engagement => Math.Log(1 + Likes + 2.0 * Comments + 3.0 * Shares);
}

/// <summary>Scores text sentiment in [-1, 1].</summary>
public interface ISentimentScorer
{
    double Score(string text);
}

/// <summary>Wraps any function (e.g. a FinBERT or LLM scorer from another module) as an <see cref="ISentimentScorer"/>.</summary>
public sealed class DelegateSentimentScorer : ISentimentScorer
{
    private readonly Func<string, double> _score;

    public DelegateSentimentScorer(Func<string, double> score) => _score = score;

    public double Score(string text) => Math.Clamp(_score(text), -1, 1);
}

/// <summary>
/// A small trading-slang lexicon scorer ("moon", "calls", "bagholder"...) with negation handling.
/// Good enough for buzz dashboards; plug a model-based scorer in for research.
/// </summary>
public sealed class SocialLexiconScorer : ISentimentScorer
{
    private static readonly Dictionary<string, double> Lexicon = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bull"] = 1, ["bullish"] = 1, ["moon"] = 1, ["mooning"] = 1, ["rocket"] = 0.8, ["calls"] = 0.6, ["long"] = 0.4,
        ["buy"] = 0.6, ["buying"] = 0.6, ["undervalued"] = 0.8, ["breakout"] = 0.7, ["rally"] = 0.7, ["beat"] = 0.6,
        ["strong"] = 0.5, ["growth"] = 0.4, ["upgrade"] = 0.7, ["squeeze"] = 0.5, ["hodl"] = 0.5, ["gain"] = 0.5, ["gains"] = 0.5,
        ["profit"] = 0.5, ["record"] = 0.4, ["surge"] = 0.7, ["soar"] = 0.8, ["win"] = 0.4,
        ["bear"] = -1, ["bearish"] = -1, ["puts"] = -0.6, ["short"] = -0.4, ["sell"] = -0.6, ["selling"] = -0.6, ["dump"] = -0.9,
        ["crash"] = -1, ["overvalued"] = -0.8, ["bagholder"] = -0.8, ["bagholding"] = -0.8, ["miss"] = -0.6, ["weak"] = -0.5,
        ["downgrade"] = -0.7, ["bankrupt"] = -1, ["bankruptcy"] = -1, ["fraud"] = -1, ["loss"] = -0.5, ["losses"] = -0.5,
        ["plunge"] = -0.8, ["tank"] = -0.7, ["tanking"] = -0.8, ["rug"] = -0.9, ["scam"] = -1, ["recession"] = -0.6, ["lawsuit"] = -0.6,
    };

    private static readonly HashSet<string> Negations = new(StringComparer.OrdinalIgnoreCase) { "not", "no", "never", "isn't", "dont", "don't", "won't", "aint", "ain't" };

    private static readonly Regex Word = new(@"[A-Za-z']+", RegexOptions.Compiled);

    public double Score(string text)
    {
        double sum = 0;
        int hits = 0, negateWindow = 0;
        foreach (Match m in Word.Matches(text))
        {
            var w = m.Value;
            if (Negations.Contains(w))
            {
                negateWindow = 3;
                continue;
            }
            if (Lexicon.TryGetValue(w, out var v))
            {
                sum += negateWindow > 0 ? -v : v;
                hits++;
            }
            if (negateWindow > 0) negateWindow--;
        }
        if (text.Contains("🚀")) { sum += 0.8; hits++; }
        if (text.Contains("📉")) { sum -= 0.8; hits++; }
        return hits == 0 ? 0 : Math.Clamp(sum / Math.Sqrt(hits + 1), -1, 1);
    }
}

/// <summary>Finds ticker mentions in text.</summary>
public static class MentionExtractor
{
    private static readonly Regex Cashtag = new(@"(?<![A-Za-z0-9])\$([A-Za-z]{1,6}(?:\.[A-Za-z]{1,2})?)\b", RegexOptions.Compiled);
    private static readonly Regex Upper = new(@"\b[A-Z]{2,5}\b", RegexOptions.Compiled);

    private static readonly HashSet<string> CommonWords = new() { "CEO", "USA", "IPO", "ETF", "GDP", "CPI", "FED", "THE", "AND", "FOR", "YOLO", "IMO", "DD", "ATH", "EPS", "SEC", "LOL", "WSB", "USD", "AI" };

    /// <summary>
    /// Distinct tickers mentioned: every cashtag ($AAPL), plus bare upper-case words that are in
    /// <paramref name="knownTickers"/> (bare words are ignored when no universe is given).
    /// </summary>
    public static IReadOnlyList<string> Extract(string text, ISet<string>? knownTickers = null)
    {
        var found = new List<string>();
        foreach (Match m in Cashtag.Matches(text))
        {
            var t = m.Groups[1].Value.ToUpperInvariant();
            if (!found.Contains(t)) found.Add(t);
        }
        if (knownTickers is not null)
        {
            foreach (Match m in Upper.Matches(text))
            {
                var t = m.Value;
                if (!CommonWords.Contains(t) && knownTickers.Contains(t) && !found.Contains(t)) found.Add(t);
            }
        }
        return found;
    }
}

/// <summary>Sentiment and attention for one symbol over one period.</summary>
public sealed record SymbolSentiment(
    string Symbol,
    DateTime PeriodStart,
    int Mentions,
    double AverageSentiment,
    double EngagementWeightedSentiment,
    double BullishRatio,
    double TotalEngagement);

/// <summary>Aggregates posts into per-symbol, per-period sentiment.</summary>
public sealed class SocialSentimentAggregator
{
    private readonly ISentimentScorer _scorer;

    public SocialSentimentAggregator(ISentimentScorer? scorer = null) => _scorer = scorer ?? new SocialLexiconScorer();

    /// <summary>Ticker universe for bare-word matching (optional).</summary>
    public ISet<string>? KnownTickers { get; init; }

    /// <summary>Posts scoring above this count as bullish (below the negative as bearish).</summary>
    public double NeutralBand { get; init; } = 0.05;

    /// <summary>Groups posts by symbol and period (e.g. one day) and aggregates them, oldest first.</summary>
    public IReadOnlyList<SymbolSentiment> Aggregate(IEnumerable<SocialPost> posts, TimeSpan period)
    {
        var rows = new List<(string Sym, long Bucket, double Score, double Weight)>();
        foreach (var p in posts)
        {
            var symbols = MentionExtractor.Extract(p.Text, KnownTickers);
            if (symbols.Count == 0) continue;
            double s = _scorer.Score(p.Text);
            long bucket = p.Time.Ticks / period.Ticks;
            foreach (var sym in symbols) rows.Add((sym, bucket, s, 1 + p.Engagement));
        }
        return rows.GroupBy(r => (r.Sym, r.Bucket))
            .Select(g =>
            {
                double wsum = g.Sum(r => r.Weight);
                int bull = g.Count(r => r.Score > NeutralBand), bear = g.Count(r => r.Score < -NeutralBand);
                return new SymbolSentiment(g.Key.Sym, new DateTime(g.Key.Bucket * period.Ticks, DateTimeKind.Utc), g.Count(),
                    g.Average(r => r.Score), g.Sum(r => r.Score * r.Weight) / wsum,
                    bull + bear == 0 ? 0.5 : bull / (double)(bull + bear), wsum - g.Count());
            })
            .OrderBy(s => s.PeriodStart).ThenBy(s => s.Symbol)
            .ToList();
    }
}

/// <summary>Flags abnormal attention: mention counts far above their trailing mean.</summary>
public static class BuzzDetector
{
    /// <summary>
    /// Rolling z-score of each count against the previous <paramref name="window"/> counts
    /// (NaN until the window is full). The current value is excluded from its own baseline.
    /// </summary>
    public static double[] ZScores(IReadOnlyList<double> counts, int window = 20)
    {
        var z = new double[counts.Count];
        for (int i = 0; i < counts.Count; i++)
        {
            if (i < window)
            {
                z[i] = double.NaN;
                continue;
            }
            double mean = 0;
            for (int k = i - window; k < i; k++) mean += counts[k];
            mean /= window;
            double v = 0;
            for (int k = i - window; k < i; k++) v += (counts[k] - mean) * (counts[k] - mean);
            double sd = Math.Sqrt(v / (window - 1));
            z[i] = sd > 0 ? (counts[i] - mean) / sd : (counts[i] > mean ? double.PositiveInfinity : 0);
        }
        return z;
    }

    /// <summary>Indices where the z-score exceeds <paramref name="threshold"/>.</summary>
    public static int[] Spikes(IReadOnlyList<double> counts, int window = 20, double threshold = 3)
        => ZScores(counts, window).Select((v, i) => (v, i)).Where(x => x.v > threshold).Select(x => x.i).ToArray();
}
