using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Finance.LLM.Rag;

/// <summary>A document to index: a filing, an annual report, a transcript.</summary>
/// <param name="Id">Stable identifier, e.g. "AAPL-10K-2025".</param>
/// <param name="Title">Human-readable title shown in citations.</param>
/// <param name="Pages">Text per page (one element for documents without pages, such as HTML filings).</param>
public sealed record SourceDocument(string Id, string Title, IReadOnlyList<string> Pages)
{
    /// <summary>A single-page document from plain text.</summary>
    public static SourceDocument FromText(string id, string title, string text) => new(id, title, new[] { text });

    /// <summary>A document from HTML (an EDGAR filing): scripts/styles removed, block elements become line breaks, entities decoded.</summary>
    public static SourceDocument FromHtml(string id, string title, string html) => new(id, title, new[] { HtmlToText(html) });

    /// <summary>Plain text from HTML, keeping paragraph and table-row boundaries as line breaks.</summary>
    public static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, @"<(script|style|head)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<ix:header>.*?</ix:header>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);   // inline-XBRL hidden facts
        s = Regex.Replace(s, @"<(br|/p|/div|/tr|/li|/h\d|/table)[^>]*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</t[dh]>", " | ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s).Replace(' ', ' ');
        s = Regex.Replace(s, @"[ \t\f\v]+", " ");
        s = Regex.Replace(s, @" *\n[ \n]*", "\n");
        return s.Trim();
    }
}

/// <summary>A retrievable passage with where it came from.</summary>
public sealed record DocumentChunk(string DocumentId, string Title, int Index, int Page, string Section, string Text)
{
    /// <summary>"Title, p. 12, Item 7" style reference.</summary>
    public string Reference => $"{Title}{(Page > 0 ? $", p. {Page}" : "")}{(Section.Length > 0 ? $", {Section}" : "")}";
}

/// <summary>
/// Splits documents into overlapping passages of roughly <see cref="MaxCharacters"/>, on paragraph boundaries, carrying
/// the current section heading (10-K "Item 7.", numbered or upper-case headings of annual reports) and page number.
/// </summary>
public sealed class DocumentChunker
{
    // Filing structure: "Item 7. Management's Discussion and Analysis ...", "PART II", "BAB III" (long titles allowed).
    private static readonly Regex FilingHeading = new(@"^(item\s+\d+[a-z]?\.|part\s+[ivx]+\b|bab\s+[ivx\d]+\b)[^|]{0,150}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Generic short headings: "3.2 Liquidity and Capital Resources", "RISK FACTORS" (case-sensitive upper-case test).
    private static readonly Regex ShortHeading = new(@"^(\d+(\.\d+)*\.?\s+[A-Z][^.!?|]{2,80}|[A-Z][A-Z0-9 ,&'\-]{5,80})$", RegexOptions.Compiled);

    private static bool IsHeading(string line) => FilingHeading.IsMatch(line) || line.Length <= 90 && ShortHeading.IsMatch(line);

    public int MaxCharacters { get; init; } = 1200;
    public int OverlapCharacters { get; init; } = 200;

    public IReadOnlyList<DocumentChunk> Chunk(SourceDocument doc)
    {
        var chunks = new List<DocumentChunk>();
        string section = "";
        for (int p = 0; p < doc.Pages.Count; p++)
        {
            int page = doc.Pages.Count > 1 ? p + 1 : 0;
            var current = new StringBuilder();
            string currentSection = section;
            foreach (var raw in doc.Pages[p].Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (IsHeading(line))
                {
                    if (current.Length > 0) { Emit(); current.Clear(); }
                    section = currentSection = line.TrimEnd('.');
                    continue;
                }
                if (current.Length + line.Length + 1 > MaxCharacters && current.Length > 0)
                {
                    Emit();
                    var tail = current.ToString();
                    current.Clear();
                    if (OverlapCharacters > 0 && tail.Length > OverlapCharacters)
                    {
                        int cut = tail.IndexOf(' ', tail.Length - OverlapCharacters);
                        current.Append(tail[(cut < 0 ? tail.Length - OverlapCharacters : cut + 1)..]).Append(' ');
                    }
                }
                // Very long single lines (tables flattened to one line) are split hard.
                while (line.Length > MaxCharacters)
                {
                    current.Append(line[..MaxCharacters]);
                    Emit();
                    current.Clear();
                    line = line[(MaxCharacters - OverlapCharacters)..];
                }
                current.Append(line).Append('\n');
            }
            if (current.Length > 0) Emit();

            void Emit()
            {
                var text = current.ToString().Trim();
                if (text.Length > 0) chunks.Add(new DocumentChunk(doc.Id, doc.Title, chunks.Count, page, currentSection, text));
            }
        }
        return chunks;
    }
}

/// <summary>Turns texts into dense vectors (e.g. a local sentence-transformer, or a hosted embeddings API).</summary>
public interface IEmbeddingModel
{
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}

/// <summary>Okapi BM25 over chunk text (k1 = 1.5, b = 0.75), with English and Indonesian stop words removed.</summary>
public sealed class Bm25Index
{
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "by", "with", "as", "at", "is", "are", "was", "were", "be", "been",
        "this", "that", "these", "those", "it", "its", "from", "which", "what", "how", "our", "we", "us", "did", "does", "do",
        "yang", "dan", "di", "ke", "dari", "untuk", "pada", "dengan", "dalam", "adalah", "ini", "itu", "atau", "oleh", "sebagai", "berapa", "apa",
    };
    private readonly List<Dictionary<string, int>> _tf = new();
    private readonly Dictionary<string, int> _df = new(StringComparer.Ordinal);
    private readonly List<int> _len = new();

    public int Count => _tf.Count;

    /// <summary>Lower-cased words and numbers (keeping 3.5 and 1,234 together), stop words removed.</summary>
    public static IEnumerable<string> Tokenize(string text)
        => Regex.Matches(text.ToLowerInvariant(), @"\d+(?:[.,]\d+)*|[\p{L}]+").Select(m => m.Value.Replace(",", "")).Where(t => !Stop.Contains(t) && t.Length > 1 || char.IsDigit(t[0]));

    public void Add(string text)
    {
        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        int n = 0;
        foreach (var t in Tokenize(text)) { tf[t] = tf.GetValueOrDefault(t) + 1; n++; }
        foreach (var t in tf.Keys) _df[t] = _df.GetValueOrDefault(t) + 1;
        _tf.Add(tf);
        _len.Add(n);
    }

    /// <summary>(document index, score) for the best <paramref name="k"/> matches.</summary>
    public IReadOnlyList<(int Index, double Score)> Search(string query, int k = 10)
    {
        if (_tf.Count == 0) return Array.Empty<(int, double)>();
        const double k1 = 1.5, b = 0.75;
        double avg = _len.Average();
        var terms = Tokenize(query).Distinct().ToList();
        var scores = new double[_tf.Count];
        foreach (var t in terms)
        {
            if (!_df.TryGetValue(t, out int df)) continue;
            double idf = Math.Log(1 + (_tf.Count - df + 0.5) / (df + 0.5));
            for (int d = 0; d < _tf.Count; d++)
                if (_tf[d].TryGetValue(t, out int f))
                    scores[d] += idf * f * (k1 + 1) / (f + k1 * (1 - b + b * _len[d] / avg));
        }
        return scores.Select((s, i) => (i, s)).Where(p => p.s > 0).OrderByDescending(p => p.s).Take(k).ToList();
    }
}

/// <summary>A retrieved passage and its fused rank score.</summary>
public sealed record RetrievedPassage(DocumentChunk Chunk, double Score);

/// <summary>
/// Passage index over filings: BM25 always, plus dense retrieval when an <see cref="IEmbeddingModel"/> is given, fused
/// with reciprocal-rank fusion (each list contributes 1 / (60 + rank)).
/// </summary>
public sealed class FilingIndex
{
    private readonly List<DocumentChunk> _chunks = new();
    private readonly List<float[]> _vectors = new();
    private readonly Bm25Index _bm25 = new();
    private readonly IEmbeddingModel? _embedder;

    public FilingIndex(IEmbeddingModel? embedder = null, DocumentChunker? chunker = null)
    {
        _embedder = embedder;
        Chunker = chunker ?? new DocumentChunker();
    }

    public DocumentChunker Chunker { get; }
    public IReadOnlyList<DocumentChunk> Chunks => _chunks;

    public async Task AddAsync(SourceDocument document, CancellationToken cancellationToken = default)
    {
        var chunks = Chunker.Chunk(document);
        foreach (var c in chunks) _bm25.Add(c.Title + " " + c.Section + " " + c.Text);
        _chunks.AddRange(chunks);
        if (_embedder is not null)
            for (int i = 0; i < chunks.Count; i += 32)
                _vectors.AddRange(await _embedder.EmbedAsync(chunks.Skip(i).Take(32).Select(c => c.Text).ToList(), cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<RetrievedPassage>> SearchAsync(string query, int k = 6, CancellationToken cancellationToken = default)
    {
        var fused = new Dictionary<int, double>();
        var lexical = _bm25.Search(query, 50);
        for (int r = 0; r < lexical.Count; r++) fused[lexical[r].Index] = 1.0 / (60 + r + 1);
        if (_embedder is not null && _vectors.Count == _chunks.Count && _chunks.Count > 0)
        {
            var q = (await _embedder.EmbedAsync(new[] { query }, cancellationToken).ConfigureAwait(false))[0];
            var dense = _vectors.Select((v, i) => (i, Cosine(q, v))).OrderByDescending(p => p.Item2).Take(50).ToList();
            for (int r = 0; r < dense.Count; r++) fused[dense[r].i] = fused.GetValueOrDefault(dense[r].i) + 1.0 / (60 + r + 1);
        }
        return fused.OrderByDescending(p => p.Value).Take(k).Select(p => new RetrievedPassage(_chunks[p.Key], p.Value)).ToList();
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 ? dot / Math.Sqrt(na * nb) : 0;
    }
}

/// <summary>One sentence of an answer and whether its citations back it up.</summary>
/// <param name="Sentence">The sentence as written (citation markers included).</param>
/// <param name="Citations">Passage numbers it cites (1-based, as shown to the model).</param>
/// <param name="Supported">True when every cited passage exists, every number in the sentence appears in a cited passage,
/// and enough of its content words do.</param>
/// <param name="Issues">Why it is not supported.</param>
public sealed record CheckedClaim(string Sentence, int[] Citations, bool Supported, IReadOnlyList<string> Issues);

/// <summary>An answer with its evidence and the result of checking every citation.</summary>
public sealed record GroundedAnswer(string Question, string Answer, IReadOnlyList<RetrievedPassage> Passages, IReadOnlyList<CheckedClaim> Claims, bool NotFound)
{
    /// <summary>True when the model answered and every sentence is supported by what it cites.</summary>
    public bool IsGrounded => !NotFound && Claims.Count > 0 && Claims.All(c => c.Supported);

    public IEnumerable<CheckedClaim> UnsupportedClaims => Claims.Where(c => !c.Supported);
}

/// <summary>
/// Checks an answer's citations against the passages it was given, without another model call: each sentence must cite
/// at least one existing passage; every number in it (amounts with million/billion/juta/miliar/triliun scaling,
/// percentages, years) must occur in a cited passage within rounding; and at least <see cref="MinimumOverlap"/> of its
/// content words must appear there. Deterministic and cheap, so it can run on every answer.
/// </summary>
public static class CitationChecker
{
    /// <summary>Share of a sentence's content words that must occur in its cited passages.</summary>
    public static double MinimumOverlap { get; set; } = 0.5;

    private static readonly Regex CitationMarker = new(@"\[(\d+(?:\s*,\s*\d+)*)\]", RegexOptions.Compiled);
    private static readonly Regex NumberToken = new(
        @"(?<![\w.])(?:[$€£]|rp\.?\s?|us\$|idr\s?)?(\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)(\s?%|\s?(?:percent|persen))?(\s?(?:million|billion|trillion|thousand|juta|miliar|milyar|triliun|ribu|mn|bn|m\b|b\b))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<CheckedClaim> Check(string answer, IReadOnlyList<string> passages)
    {
        var claims = new List<CheckedClaim>();
        foreach (var sentence in SplitSentences(answer))
        {
            var cites = CitationMarker.Matches(sentence).SelectMany(m => m.Groups[1].Value.Split(',')).Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture))
                .Distinct().ToArray();
            var issues = new List<string>();
            string bare = CitationMarker.Replace(sentence, " ");
            if (cites.Length == 0)
            {
                issues.Add("no citation");
                claims.Add(new CheckedClaim(sentence, cites, false, issues));
                continue;
            }
            var missing = cites.Where(c => c < 1 || c > passages.Count).ToArray();
            if (missing.Length > 0) issues.Add($"cites passage(s) that do not exist: {string.Join(", ", missing)}");
            var evidence = string.Join("\n", cites.Where(c => c >= 1 && c <= passages.Count).Select(c => passages[c - 1]));

            var evidenceNumbers = Numbers(evidence).ToList();
            foreach (var (text, value) in Numbers(bare))
                if (!evidenceNumbers.Any(e => Matches(value, e.Value)))
                    issues.Add($"number {text.Trim()} not found in cited passage(s)");

            var words = ContentWords(bare);
            if (words.Count > 0)
            {
                var ev = new HashSet<string>(ContentWords(evidence));
                double overlap = words.Count(w => ev.Contains(w) || ev.Any(e => Stem(e) == Stem(w))) / (double)words.Count;
                if (overlap < MinimumOverlap) issues.Add($"only {overlap:P0} of its words appear in the cited passage(s)");
            }
            claims.Add(new CheckedClaim(sentence, cites, issues.Count == 0, issues));
        }
        return claims;
    }

    internal static IEnumerable<string> SplitSentences(string text)
        => Regex.Split(text.Trim(), @"(?<=[.!?](?:\s*\[[\d,\s]+\])*)\s+(?=[A-Z0-9\[\(""])")
            .Select(s => s.Trim()).Where(s => s.Length > 0 && Regex.IsMatch(s, @"\w"));

    /// <summary>Numbers with their scaled values: "$391.0 billion" → 3.91e11, "12.5%" → 12.5, "Rp 1.250 miliar" → 1.25e12.</summary>
    internal static IEnumerable<(string Text, double Value)> Numbers(string text)
    {
        foreach (Match m in NumberToken.Matches(text))
        {
            string digits = m.Groups[1].Value;
            // Indonesian thousands separators ("1.250.000") use dots: more than one dot means grouping.
            if (digits.Count(c => c == '.') > 1) digits = digits.Replace(".", "");
            if (!double.TryParse(digits.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
            string scale = m.Groups[3].Value.Trim().ToLowerInvariant();
            v *= scale switch
            {
                "thousand" or "ribu" => 1e3,
                "million" or "juta" or "mn" or "m" => 1e6,
                "billion" or "miliar" or "milyar" or "bn" or "b" => 1e9,
                "trillion" or "triliun" => 1e12,
                _ => 1,
            };
            yield return (m.Value, v);
        }
    }

    /// <summary>
    /// Equal up to the rounding a writer would apply: within 0.5% relative, or, for amounts, equal when the evidence
    /// is quoted in a different unit (a table in millions vs prose in billions is the same 0.5% test after scaling).
    /// </summary>
    private static bool Matches(double claimed, double evidence)
    {
        if (claimed == evidence) return true;
        foreach (var unit in new[] { 1.0, 1e3, 1e6, 1e9 })
        {
            double e = evidence * unit;
            if (Math.Abs(claimed - e) <= 0.005 * Math.Max(Math.Abs(claimed), Math.Abs(e)) + 1e-9) return true;
        }
        return false;
    }

    private static List<string> ContentWords(string text)
        => Bm25Index.Tokenize(text).Where(t => !char.IsDigit(t[0]) && t.Length > 2).Distinct().ToList();

    private static string Stem(string w) => w.Length > 5 ? w[..5] : w;
}

/// <summary>
/// Question answering over indexed filings: retrieves passages, asks the model to answer only from them with a
/// [n] citation on every sentence, then checks each citation with <see cref="CitationChecker"/>. A question the
/// passages cannot answer comes back with <see cref="GroundedAnswer.NotFound"/> set instead of a guess.
/// </summary>
public sealed class FilingsQa(IChatModel model, FilingIndex index)
{
    public const string NotFoundMarker = "NOT_FOUND";

    public int TopK { get; init; } = 6;

    public static string SystemPrompt =>
        "You answer questions about company filings using ONLY the numbered passages provided. " +
        "End every sentence with the passage number(s) supporting it, like [2] or [1, 3]. " +
        "Quote figures exactly as they appear, with their units. Never use outside knowledge. " +
        $"If the passages do not contain the answer, reply with exactly {NotFoundMarker}.";

    public async Task<GroundedAnswer> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        var passages = await index.SearchAsync(question, TopK, cancellationToken).ConfigureAwait(false);
        if (passages.Count == 0) return new GroundedAnswer(question, NotFoundMarker, passages, Array.Empty<CheckedClaim>(), true);
        var context = string.Join("\n\n", passages.Select((p, i) => $"[{i + 1}] ({p.Chunk.Reference})\n{p.Chunk.Text}"));
        var reply = await model.ChatAsync(new[]
        {
            ChatMessage.System(SystemPrompt),
            ChatMessage.User($"Passages:\n{context}\n\nQuestion: {question}"),
        }, new ChatOptions { Temperature = 0, MaxTokens = 800 }, cancellationToken).ConfigureAwait(false);
        var answer = reply.Content.Trim();
        if (answer.Contains(NotFoundMarker, StringComparison.Ordinal))
            return new GroundedAnswer(question, answer, passages, Array.Empty<CheckedClaim>(), true);
        var claims = CitationChecker.Check(answer, passages.Select(p => p.Chunk.Text).ToList());
        return new GroundedAnswer(question, answer, passages, claims, false);
    }
}
