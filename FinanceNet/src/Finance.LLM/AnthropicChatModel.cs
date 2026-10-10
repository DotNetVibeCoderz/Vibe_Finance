using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Finance.LLM;

/// <summary>Client for the Anthropic Messages API (<c>/v1/messages</c>), including tool use and streaming.</summary>
public sealed class AnthropicChatModel(HttpClient http, string apiKey, string model = "claude-sonnet-5-5", string baseUrl = "https://api.anthropic.com") : IChatModel
{
    private const string Version = "2023-06-01";

    public string Name => $"anthropic:{model}";

    public async Task<ChatResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var resp = await SendAsync(messages, options, false, cancellationToken).ConfigureAwait(false);
        return ParseResponse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var resp = await SendAsync(messages, options, true, cancellationToken).ConfigureAwait(false);
        await using var s = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var data in Sse.ReadDataAsync(s, cancellationToken).ConfigureAwait(false))
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var t) && t.GetString() == "content_block_delta"
                && root.GetProperty("delta").TryGetProperty("text", out var text))
            {
                var v = text.GetString();
                if (!string.IsNullOrEmpty(v)) yield return v;
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options, bool stream, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/v1/messages")
        {
            Content = new StringContent(BuildBody(messages, options, stream).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", Version);
        var resp = await http.SendAsync(req, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode) return resp;
        var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        int status = (int)resp.StatusCode;
        resp.Dispose();
        throw new LlmException($"{Name} returned {status}: {OpenAiCompatibleChatModel.Truncate(err, 500)}", status);
    }

    internal JsonObject BuildBody(IReadOnlyList<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = options?.Model ?? model,
            ["max_tokens"] = options?.MaxTokens ?? 1024,
        };
        var system = string.Join("\n\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Content));
        if (options?.JsonMode == true) system = (system + "\n\nRespond with a single JSON object only.").Trim();
        if (system.Length > 0) body["system"] = system;
        if (options?.Temperature is { } t) body["temperature"] = t;

        var arr = new JsonArray();
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case ChatRole.System:
                    continue;
                case ChatRole.Tool:
                    arr.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m.ToolCallId, ["content"] = m.Content }),
                    });
                    break;
                case ChatRole.Assistant when m.ToolCalls is { Count: > 0 }:
                {
                    var blocks = new JsonArray();
                    if (!string.IsNullOrEmpty(m.Content)) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
                    foreach (var c in m.ToolCalls)
                        blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = c.Id, ["name"] = c.Name, ["input"] = JsonNode.Parse(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson) });
                    arr.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
                    break;
                }
                default:
                    arr.Add(new JsonObject { ["role"] = m.Role == ChatRole.User ? "user" : "assistant", ["content"] = m.Content });
                    break;
            }
        }
        body["messages"] = arr;
        if (options?.Tools is { Count: > 0 } tools)
        {
            var ta = new JsonArray();
            foreach (var tool in tools) ta.Add(new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["input_schema"] = tool.SchemaNode });
            body["tools"] = ta;
        }
        if (stream) body["stream"] = true;
        return body;
    }

    internal static ChatResponse ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        foreach (var block in root.GetProperty("content").EnumerateArray())
        {
            var type = block.GetProperty("type").GetString();
            if (type == "text") text.Append(block.GetProperty("text").GetString());
            else if (type == "tool_use")
                calls.Add(new ToolCall(block.GetProperty("id").GetString() ?? "", block.GetProperty("name").GetString() ?? "", block.GetProperty("input").GetRawText()));
        }
        var usage = default(TokenUsage);
        if (root.TryGetProperty("usage", out var u))
            usage = new TokenUsage(u.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0, u.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0);
        return new ChatResponse(text.ToString(), calls, root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null, usage);
    }
}
