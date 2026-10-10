using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Finance.LLM;

/// <summary>Client for a local Ollama server (<c>/api/chat</c>, NDJSON streaming, tool calls).</summary>
public sealed class OllamaChatModel(HttpClient http, string model = "llama3.1", string baseUrl = "http://localhost:11434") : IChatModel
{
    public string Name => $"ollama:{model}";

    public async Task<ChatResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var resp = await SendAsync(messages, options, false, cancellationToken).ConfigureAwait(false);
        return ParseResponse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var resp = await SendAsync(messages, options, true, cancellationToken).ConfigureAwait(false);
        await using var s = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(s);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c))
            {
                var t = c.GetString();
                if (!string.IsNullOrEmpty(t)) yield return t;
            }
            if (root.TryGetProperty("done", out var d) && d.GetBoolean()) yield break;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options, bool stream, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = options?.Model ?? model, ["stream"] = stream };
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            var o = new JsonObject { ["role"] = m.Role.ToString().ToLowerInvariant(), ["content"] = m.Content };
            if (m.ToolCalls is { Count: > 0 } calls)
            {
                var tc = new JsonArray();
                foreach (var c in calls)
                    tc.Add(new JsonObject { ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = JsonNode.Parse(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson) } });
                o["tool_calls"] = tc;
            }
            arr.Add(o);
        }
        body["messages"] = arr;
        var opts = new JsonObject();
        if (options?.Temperature is { } t) opts["temperature"] = t;
        if (options?.MaxTokens is { } n) opts["num_predict"] = n;
        if (opts.Count > 0) body["options"] = opts;
        if (options?.JsonMode == true) body["format"] = "json";
        if (options?.Tools is { Count: > 0 } tools)
        {
            var ta = new JsonArray();
            foreach (var tool in tools)
                ta.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["parameters"] = tool.SchemaNode } });
            body["tools"] = ta;
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/api/chat")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var resp = await http.SendAsync(req, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode) return resp;
        var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        int status = (int)resp.StatusCode;
        resp.Dispose();
        throw new LlmException($"{Name} returned {status}: {OpenAiCompatibleChatModel.Truncate(err, 500)}", status);
    }

    internal static ChatResponse ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var msg = root.GetProperty("message");
        var calls = new List<ToolCall>();
        if (msg.TryGetProperty("tool_calls", out var tcs))
        {
            int i = 0;
            foreach (var tc in tcs.EnumerateArray())
            {
                var fn = tc.GetProperty("function");
                calls.Add(new ToolCall($"call_{i++}", fn.GetProperty("name").GetString() ?? "", fn.GetProperty("arguments").GetRawText()));
            }
        }
        var usage = new TokenUsage(root.TryGetProperty("prompt_eval_count", out var p) ? p.GetInt32() : 0, root.TryGetProperty("eval_count", out var e) ? e.GetInt32() : 0);
        return new ChatResponse(msg.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "", calls, root.TryGetProperty("done_reason", out var d) ? d.GetString() : null, usage);
    }
}
