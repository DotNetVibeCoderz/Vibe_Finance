using System.Text.RegularExpressions;

namespace Finance.NewsAnalytics;

/// <summary>Sentiment scores of a text.</summary>
public sealed record NewsSentiment(
    double Score,
    string Label,
    int Positive,
    int Negative,
    int Uncertainty,
    int Litigious,
    int Constraining,
    int WordCount,
    string Source = "Lexicon")
{
    /// <summary>Uncertainty words per word.</summary>
    public double UncertaintyRatio => WordCount > 0 ? (double)Uncertainty / WordCount : 0;

    public static NewsSentiment Neutral(string source = "Lexicon") => new(0, "neutral", 0, 0, 0, 0, 0, 0, source);
}

/// <summary>A pluggable sentiment model. Hosts can wrap FinBERT or an LLM via <see cref="DelegateSentimentModel"/>.</summary>
public interface ISentimentModel
{
    string Name { get; }
    Task<NewsSentiment> ScoreAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Adapts any async scorer (e.g. FinBERT, an LLM) returning (label, score in [-1, 1]).</summary>
public sealed class DelegateSentimentModel(string name, Func<string, CancellationToken, Task<(string Label, double Score)>> scorer) : ISentimentModel
{
    public string Name => name;

    public async Task<NewsSentiment> ScoreAsync(string text, CancellationToken cancellationToken = default)
    {
        var (label, score) = await scorer(text, cancellationToken).ConfigureAwait(false);
        return new NewsSentiment(score, label, 0, 0, 0, 0, 0, FinancialLexicon.Tokenize(text).Count, name);
    }
}

/// <summary>
/// Finance-specific word lists in the spirit of Loughran &amp; McDonald (2011): general-purpose
/// sentiment dictionaries misclassify words like "liability", "tax" or "cost" in a financial context.
/// The lists are curated stems (prefix matches) of the most frequent dictionary entries.
/// </summary>
public static partial class FinancialLexicon
{
    public static readonly string[] Positive =
    {
        "beat", "beats", "exceed", "outperform", "record", "strong", "strength", "growth", "grow", "gain", "surge", "soar", "jump", "rally",
        "rebound", "recover", "profit", "profitab", "upgrad", "raise", "raised", "boost", "improv", "expand", "accelerat", "robust", "solid",
        "optimis", "bullish", "momentum", "win", "won", "award", "breakthrough", "innovat", "success", "advantage", "efficien", "upside",
        "dividend increase", "buyback", "positive", "favorabl", "favourabl", "attractive", "resilien", "tops", "top estimates", "higher", "rise",
        "rises", "rising", "rose", "climb", "best", "lead", "leading", "opportunit", "approv", "achiev", "enhanc", "benefit", "stabl", "rewarding",
    };

    public static readonly string[] Negative =
    {
        "miss", "misses", "missed", "loss", "losses", "decline", "declin", "drop", "fall", "fell", "plunge", "plummet", "slump", "tumble", "crash",
        "weak", "downgrad", "cut", "cuts", "lower", "lowered", "warn", "warning", "bearish", "concern", "fear", "risk", "volatil", "default",
        "bankrupt", "insolven", "layoff", "lay off", "restructur", "impair", "writedown", "write-down", "writeoff", "deficit", "debt", "shortfall",
        "slowdown", "recession", "inflation", "downturn", "disappoint", "negative", "adverse", "unfavorabl", "unfavourabl", "delay", "recall",
        "fraud", "scandal", "probe", "investigat", "breach", "halt", "suspend", "worst", "sink", "sank", "slide", "slid", "pressure", "headwind",
        "dilut", "underperform", "fail", "failure", "closure", "terminat", "shortage", "costly", "doubt",
    };

    public static readonly string[] Uncertainty =
    {
        "may", "might", "could", "possibl", "uncertain", "uncertaint", "approximat", "assum", "believ", "depend", "fluctuat", "indefinite",
        "likely", "perhaps", "predict", "probab", "roughly", "seem", "sometime", "unclear", "unknown", "unpredictab", "variab", "volatil",
        "anticipat", "expect", "estimat", "contingen", "risk", "tentativ", "speculat",
    };

    public static readonly string[] Litigious =
    {
        "lawsuit", "litigat", "sue", "sued", "suing", "court", "plaintiff", "defendant", "settlement", "settle", "legal", "attorney", "allegat",
        "alleg", "indict", "subpoena", "regulator", "sec ", "doj", "antitrust", "class action", "verdict", "appeal", "injunction", "penalt",
        "fine", "fined", "violation", "complian", "prosecut",
    };

    public static readonly string[] Constraining =
    {
        "must", "require", "obligat", "covenant", "restrict", "limit", "prohibit", "mandator", "commit", "bound", "constrain", "compel",
        "impose", "necessit", "depend upon",
    };

    private static readonly HashSet<string> Negators = new(StringComparer.Ordinal)
    {
        "not", "no", "never", "neither", "nor", "without", "hardly", "barely", "isn't", "wasn't", "aren't", "don't", "doesn't", "didn't",
        "won't", "cannot", "can't", "fails", "failed", "lack", "lacks",
    };

    private static readonly Dictionary<string, double> Intensifiers = new(StringComparer.Ordinal)
    {
        ["very"] = 1.5, ["sharply"] = 1.7, ["significantly"] = 1.6, ["strongly"] = 1.5, ["massive"] = 1.8, ["huge"] = 1.7,
        ["record"] = 1.4, ["slightly"] = 0.6, ["modestly"] = 0.7, ["marginally"] = 0.6, ["somewhat"] = 0.7, ["dramatically"] = 1.8,
        ["deeply"] = 1.6, ["steep"] = 1.6, ["steeply"] = 1.6,
    };

    /// <summary>Lower-case word tokens (apostrophes and hyphens kept).</summary>
    public static List<string> Tokenize(string text) => Words().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();

    /// <summary>
    /// Direction verbs that are positive on their own ("revenue grows") but describe a worsening when their
    /// subject is negative ("uncertainty grows", "losses widen").
    /// </summary>
    public static readonly string[] Direction =
    {
        "grow", "rise", "rises", "rising", "rose", "increas", "surge", "soar", "jump", "climb", "mount", "spike", "widen", "deepen", "higher",
    };

    internal static bool Matches(string token, string[] stems)
    {
        foreach (var s in stems)
        {
            if (s.Contains(' ')) continue;
            if (token.StartsWith(s, StringComparison.Ordinal) && (token.Length - s.Length <= 4 || s.Length >= 6)) return true;
        }
        return false;
    }

    internal static int Phrases(string lower, string[] stems)
    {
        int n = 0;
        foreach (var s in stems)
            if (s.Contains(' ') && lower.Contains(s, StringComparison.Ordinal)) n++;
        return n;
    }

    internal static bool IsNegator(string t) => Negators.Contains(t);

    internal static double Intensity(string t) => Intensifiers.TryGetValue(t, out var v) ? v : 1;

    [GeneratedRegex(@"[a-z][a-z'\-]*")]
    private static partial Regex Words();
}

/// <summary>
/// Lexicon sentiment with negation (a negator within the previous three tokens flips polarity) and
/// intensifiers. Score = (pos − neg) / (pos + neg + 1) ∈ (−1, 1) after weighting; labels use ±0.1 thresholds.
/// </summary>
public sealed class LexiconSentimentModel : ISentimentModel
{
    public string Name => "Lexicon";

    public Task<NewsSentiment> ScoreAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(Score(text));

    /// <summary>Synchronous scoring.</summary>
    public NewsSentiment Score(string text)
    {
        var tokens = FinancialLexicon.Tokenize(text);
        var lower = text.ToLowerInvariant();
        double pos = 0, neg = 0;
        int p = 0, n = 0, u = 0, l = 0, c = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            bool isPos = FinancialLexicon.Matches(t, FinancialLexicon.Positive);
            bool isNeg = !isPos && FinancialLexicon.Matches(t, FinancialLexicon.Negative);
            bool isUnc = FinancialLexicon.Matches(t, FinancialLexicon.Uncertainty);
            bool isLit = FinancialLexicon.Matches(t, FinancialLexicon.Litigious);
            if (isUnc) u++;
            if (isLit) l++;
            if (FinancialLexicon.Matches(t, FinancialLexicon.Constraining)) c++;
            if (isPos && FinancialLexicon.Matches(t, FinancialLexicon.Direction) && i > 0 && PrecededByAdverse(tokens, i))
            {
                isPos = false;
                isNeg = true;
            }
            if (!isPos && !isNeg)
            {
                // Litigation and uncertainty language is not sentiment in the strict Loughran-McDonald sense, but in
                // headlines it reliably leans negative; count it at reduced weight.
                if (isLit) neg += 0.75;
                else if (isUnc) neg += 0.5;
                continue;
            }
            bool negated = false;
            double weight = 1;
            for (int k = Math.Max(0, i - 3); k < i; k++)
            {
                if (FinancialLexicon.IsNegator(tokens[k])) negated = !negated;
                weight *= FinancialLexicon.Intensity(tokens[k]);
            }
            for (int k = i + 1; k < Math.Min(tokens.Count, i + 3); k++) weight *= FinancialLexicon.Intensity(tokens[k]);
            if (isPos ^ negated) { pos += weight; p++; }
            else { neg += weight; n++; }
        }
        int pp = FinancialLexicon.Phrases(lower, FinancialLexicon.Positive), np = FinancialLexicon.Phrases(lower, FinancialLexicon.Negative);
        pos += pp; neg += np; p += pp; n += np;
        l += FinancialLexicon.Phrases(lower, FinancialLexicon.Litigious);
        double score = (pos - neg) / (pos + neg + 1);

        static bool PrecededByAdverse(List<string> toks, int at)
        {
            for (int k = Math.Max(0, at - 2); k < at; k++)
                if (FinancialLexicon.Matches(toks[k], FinancialLexicon.Negative) || FinancialLexicon.Matches(toks[k], FinancialLexicon.Uncertainty)
                    || FinancialLexicon.Matches(toks[k], FinancialLexicon.Litigious))
                    return true;
            return false;
        }
        string label = score > 0.1 ? "positive" : score < -0.1 ? "negative" : "neutral";
        return new NewsSentiment(score, label, p, n, u, l, c, tokens.Count);
    }
}
