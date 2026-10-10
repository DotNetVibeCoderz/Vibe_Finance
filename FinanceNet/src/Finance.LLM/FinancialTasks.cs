using System.Text.Json;
using System.Text.Json.Serialization;

namespace Finance.LLM;

/// <summary>Sentiment of a financial text. <see cref="Score"/> is in [-1, 1].</summary>
public sealed record SentimentResult(string Label, double Score, double Confidence, string Source, string? Rationale = null);

/// <summary>Summary of a batch of headlines.</summary>
public sealed record HeadlineSummary(string Summary, string OverallSentiment, string[] KeyThemes, string[] Tickers, string[] Risks);

/// <summary>Structured reading of an earnings call.</summary>
public sealed record EarningsCallAnalysis(
    string Tone,
    double ToneScore,
    string Guidance,
    string[] KeyMetrics,
    string[] Risks,
    string[] Opportunities,
    string[] ForwardLookingStatements,
    string Summary);

/// <summary>Answer to a question grounded in supplied context.</summary>
public sealed record FinancialAnswer(string Answer, string[] Citations, double Confidence);

/// <summary>
/// FinGPT-style financial NLP tasks on any <see cref="IChatModel"/>, returning typed results parsed
/// from JSON. Prompts ask for JSON only; <see cref="JsonExtract"/> tolerates code fences and prose around it.
/// </summary>
public sealed class FinancialTasks(IChatModel model)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };

    private async Task<T> JsonTaskAsync<T>(string system, string user, CancellationToken ct)
    {
        var resp = await model.ChatAsync(new[] { ChatMessage.System(system + " Reply with one JSON object only, no markdown."), ChatMessage.User(user) },
            new ChatOptions { JsonMode = true, MaxTokens = 1200 }, ct).ConfigureAwait(false);
        return JsonExtract.Deserialize<T>(resp.Content, Json) ?? throw new LlmException("Model returned no parseable JSON.");
    }

    /// <summary>Classifies the sentiment of a headline or paragraph for investors.</summary>
    public async Task<SentimentResult> ClassifySentimentAsync(string text, CancellationToken cancellationToken = default)
    {
        var r = await JsonTaskAsync<SentimentDto>(
            "You are a financial sentiment classifier. Judge the text's likely impact on the mentioned company's stock price.",
            $"Text: \"{text}\"\nReturn JSON: {{\"label\": \"positive|negative|neutral\", \"score\": number between -1 and 1, \"confidence\": number between 0 and 1, \"rationale\": short string}}",
            cancellationToken).ConfigureAwait(false);
        return new SentimentResult((r.Label ?? "neutral").ToLowerInvariant(), Math.Clamp(r.Score, -1, 1), Math.Clamp(r.Confidence, 0, 1), model.Name, r.Rationale);
    }

    /// <summary>Summarises a list of headlines into themes, sentiment and risks.</summary>
    public Task<HeadlineSummary> SummarizeHeadlinesAsync(IEnumerable<string> headlines, CancellationToken cancellationToken = default)
        => JsonTaskAsync<HeadlineSummary>(
            "You are a markets news editor.",
            "Headlines:\n" + string.Join("\n", headlines.Select((h, i) => $"{i + 1}. {h}")) +
            "\nReturn JSON: {\"summary\": string, \"overallSentiment\": \"positive|negative|neutral|mixed\", \"keyThemes\": [string], \"tickers\": [string], \"risks\": [string]}",
            cancellationToken);

    /// <summary>Analyses an earnings-call transcript (tone, guidance, metrics, risks).</summary>
    public Task<EarningsCallAnalysis> AnalyzeEarningsCallAsync(string transcript, CancellationToken cancellationToken = default)
        => JsonTaskAsync<EarningsCallAnalysis>(
            "You are an equity research analyst reading an earnings call transcript.",
            "Transcript:\n" + Clip(transcript, 24_000) +
            "\nReturn JSON: {\"tone\": \"bullish|bearish|neutral|mixed\", \"toneScore\": number -1..1, \"guidance\": \"raised|lowered|maintained|none\" plus detail, " +
            "\"keyMetrics\": [string], \"risks\": [string], \"opportunities\": [string], \"forwardLookingStatements\": [string], \"summary\": string}",
            cancellationToken);

    /// <summary>Answers a question using only the given context documents.</summary>
    public Task<FinancialAnswer> AnswerAsync(string question, IEnumerable<string> context, CancellationToken cancellationToken = default)
        => JsonTaskAsync<FinancialAnswer>(
            "You answer financial questions strictly from the provided context. If the context is insufficient, say so.",
            "Context:\n" + string.Join("\n---\n", context.Select((c, i) => $"[{i + 1}] {Clip(c, 4000)}")) + $"\n\nQuestion: {question}" +
            "\nReturn JSON: {\"answer\": string, \"citations\": [\"[n]\" references used], \"confidence\": number 0..1}",
            cancellationToken);

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n];

    private sealed record SentimentDto(string? Label, double Score, double Confidence, string? Rationale);
}

/// <summary>Extracts and deserialises the first JSON object in a model reply.</summary>
public static class JsonExtract
{
    /// <summary>The first balanced {...} block in <paramref name="text"/> (string-aware), or null.</summary>
    public static string? FirstObject(string text)
    {
        int start = text.IndexOf('{');
        while (start >= 0)
        {
            int depth = 0;
            bool inString = false, escape = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return text[start..(i + 1)];
            }
            start = text.IndexOf('{', start + 1);
        }
        return null;
    }

    public static T? Deserialize<T>(string text, JsonSerializerOptions? options = null)
    {
        var obj = FirstObject(text);
        if (obj is null) return default;
        try { return JsonSerializer.Deserialize<T>(obj, options ?? new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return default; }
    }
}
