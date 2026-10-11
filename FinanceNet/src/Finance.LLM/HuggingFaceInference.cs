using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Finance.LLM;

/// <summary>A label with its probability.</summary>
public readonly record struct LabelScore(string Label, double Score);

/// <summary>
/// Hugging Face Inference API client (via the <c>router.huggingface.co/hf-inference</c> endpoint) for
/// hosted task models such as text classification. Cold models answer 503 with an estimated load time;
/// the client waits and retries a few times.
/// </summary>
public sealed class HuggingFaceInference(HttpClient http, string token, string baseUrl = "https://router.huggingface.co/hf-inference/models")
{
    /// <summary>Classifies one text and returns all labels sorted by score (descending).</summary>
    public async Task<IReadOnlyList<LabelScore>> ClassifyAsync(string model, string text, CancellationToken cancellationToken = default)
    {
        var all = await ClassifyBatchAsync(model, new[] { text }, cancellationToken).ConfigureAwait(false);
        return all[0];
    }

    /// <summary>Classifies several texts in one request.</summary>
    public async Task<IReadOnlyList<IReadOnlyList<LabelScore>>> ClassifyBatchAsync(string model, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var inputs = new JsonArray();
        foreach (var t in texts) inputs.Add(t);
        var body = new JsonObject { ["inputs"] = inputs, ["parameters"] = new JsonObject { ["top_k"] = null } };
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/{model}")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if ((int)resp.StatusCode == 503 && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(20, 3 * (attempt + 1))), cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (!resp.IsSuccessStatusCode) throw new LlmException($"Hugging Face {model} returned {(int)resp.StatusCode}: {OpenAiCompatibleChatModel.Truncate(json, 300)}", (int)resp.StatusCode);
            return ParseClassification(json, texts.Count);
        }
    }

    /// <summary>Parses either [[{label,score}...]...] or a flat [{label,score}...] (single input).</summary>
    internal static IReadOnlyList<IReadOnlyList<LabelScore>> ParseClassification(string json, int expected)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = new List<IReadOnlyList<LabelScore>>();
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return result;
        if (root[0].ValueKind == JsonValueKind.Object) result.Add(ParseOne(root));
        else foreach (var item in root.EnumerateArray()) result.Add(ParseOne(item));
        return result;

        static IReadOnlyList<LabelScore> ParseOne(JsonElement arr) =>
            arr.EnumerateArray().Select(e => new LabelScore(e.GetProperty("label").GetString() ?? "", e.GetProperty("score").GetDouble()))
               .OrderByDescending(l => l.Score).ToList();
    }
}

/// <summary>Financial sentiment from FinBERT (ProsusAI/finbert) on Hugging Face.</summary>
public sealed class FinBertSentiment(HuggingFaceInference client, string model = "ProsusAI/finbert")
{
    /// <summary>Sentiment of one text; score = P(positive) − P(negative) in [-1, 1].</summary>
    public async Task<SentimentResult> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
        => FromScores(await client.ClassifyAsync(model, text, cancellationToken).ConfigureAwait(false));

    /// <summary>Sentiment of several texts in one request.</summary>
    public async Task<IReadOnlyList<SentimentResult>> AnalyzeBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        => (await client.ClassifyBatchAsync(model, texts, cancellationToken).ConfigureAwait(false)).Select(s => FromScores(s)).ToList();

    /// <summary>Maps FinBERT class probabilities to a <see cref="SentimentResult"/> (shared with the local runner in Finance.LLM.Local).</summary>
    public static SentimentResult FromScores(IReadOnlyList<LabelScore> scores, string source = "FinBERT")
    {
        double P(string l) => scores.FirstOrDefault(s => s.Label.Equals(l, StringComparison.OrdinalIgnoreCase)).Score;
        var top = scores.Count > 0 ? scores[0] : new LabelScore("neutral", 1);
        return new SentimentResult(top.Label.ToLowerInvariant(), P("positive") - P("negative"), top.Score, source);
    }
}
