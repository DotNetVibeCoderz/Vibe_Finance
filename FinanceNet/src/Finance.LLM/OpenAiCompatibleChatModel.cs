using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Finance.LLM;

/// <summary>Connection settings for an OpenAI-compatible chat completions endpoint.</summary>
public sealed record OpenAiCompatibleOptions
{
    /// <summary>Full URL of the chat completions endpoint (including any query string).</summary>
    public required string Endpoint { get; init; }
    public string? ApiKey { get; init; }
    public required string Model { get; init; }
    /// <summary>"Authorization" (Bearer) for OpenAI-style services, "api-key" for Azure OpenAI.</summary>
    public string AuthHeader { get; init; } = "Authorization";
    /// <summary>Send <c>max_completion_tokens</c> instead of <c>max_tokens</c> (newer reasoning models need it).</summary>
    public bool UseMaxCompletionTokens { get; init; }
    /// <summary>Whether to include the model name in the body (Azure takes it from the URL).</summary>
    public bool SendModel { get; init; } = true;
    public string ProviderName { get; init; } = "openai";
}

/// <summary>
/// Client for OpenAI-style <c>/chat/completions</c> APIs: OpenAI, Azure OpenAI, DeepSeek,
/// the Hugging Face router, vLLM, LM Studio and others. Newer deployments reject
/// <c>max_tokens</c> or a non-default <c>temperature</c>; the client detects those 400s, adapts and retries once.
/// </summary>
public sealed class OpenAiCompatibleChatModel : IChatModel
{
    private readonly HttpClient _http;
    private OpenAiCompatibleOptions _options;
    private bool _dropTemperature;

    public OpenAiCompatibleChatModel(HttpClient http, OpenAiCompatibleOptions options)
    {
        _http = http;
        _options = options;
    }

    public string Name => $"{_options.ProviderName}:{_options.Model}";

    /// <summary>OpenAI platform.</summary>
    public static OpenAiCompatibleChatModel OpenAI(HttpClient http, string apiKey, string model = "gpt-4o-mini", string baseUrl = "https://api.openai.com/v1") =>
        new(http, new OpenAiCompatibleOptions { Endpoint = baseUrl.TrimEnd('/') + "/chat/completions", ApiKey = apiKey, Model = model });

    /// <summary>Azure OpenAI deployment.</summary>
    public static OpenAiCompatibleChatModel Azure(HttpClient http, string endpoint, string apiKey, string deployment, string apiVersion = "2024-12-01-preview") =>
        new(http, new OpenAiCompatibleOptions
        {
            Endpoint = $"{endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions?api-version={apiVersion}",
            ApiKey = apiKey,
            Model = deployment,
            AuthHeader = "api-key",
            SendModel = false,
            ProviderName = "azure-openai",
        });

    /// <summary>Hugging Face router (OpenAI-compatible chat over hosted inference providers).</summary>
    public static OpenAiCompatibleChatModel HuggingFace(HttpClient http, string token, string model = "meta-llama/Llama-3.1-8B-Instruct") =>
        new(http, new OpenAiCompatibleOptions { Endpoint = "https://router.huggingface.co/v1/chat/completions", ApiKey = token, Model = model, ProviderName = "huggingface" });

    /// <summary>DeepSeek platform.</summary>
    public static OpenAiCompatibleChatModel DeepSeek(HttpClient http, string apiKey, string model = "deepseek-chat") =>
        new(http, new OpenAiCompatibleOptions { Endpoint = "https://api.deepseek.com/chat/completions", ApiKey = apiKey, Model = model, ProviderName = "deepseek" });

    public async Task<ChatResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var resp = await SendAsync(messages, options, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseResponse(json);
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var resp = await SendAsync(messages, options, stream: true, cancellationToken).ConfigureAwait(false);
        await using var s = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var data in Sse.ReadDataAsync(s, cancellationToken).ConfigureAwait(false))
        {
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            if (choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var text = c.GetString();
                if (!string.IsNullOrEmpty(text)) yield return text;
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options, bool stream, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var body = BuildBody(messages, options, stream);
            using var req = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(_options.ApiKey))
            {
                if (_options.AuthHeader == "Authorization") req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                else req.Headers.TryAddWithoutValidation(_options.AuthHeader, _options.ApiKey);
            }
            var resp = await _http.SendAsync(req, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return resp;

            var error = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            int status = (int)resp.StatusCode;
            resp.Dispose();
            if (status == 400 && attempt < 2 && Adapt(error)) continue;
            throw new LlmException($"{Name} returned {status}: {Truncate(error, 500)}", status);
        }
    }

    /// <summary>Adjusts the request shape after a parameter-related 400. Returns true when a retry makes sense.</summary>
    private bool Adapt(string error)
    {
        bool changed = false;
        if (error.Contains("max_tokens", StringComparison.OrdinalIgnoreCase) && !_options.UseMaxCompletionTokens)
        {
            _options = _options with { UseMaxCompletionTokens = true };
            changed = true;
        }
        if (error.Contains("temperature", StringComparison.OrdinalIgnoreCase) && !_dropTemperature)
        {
            _dropTemperature = true;
            changed = true;
        }
        return changed;
    }

    internal JsonObject BuildBody(IReadOnlyList<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var body = new JsonObject();
        if (_options.SendModel) body["model"] = options?.Model ?? _options.Model;
        var arr = new JsonArray();
        foreach (var m in messages) arr.Add(ToJson(m));
        body["messages"] = arr;
        if (options?.Temperature is { } t && !_dropTemperature) body["temperature"] = t;
        if (options?.MaxTokens is { } mt) body[_options.UseMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = mt;
        if (options?.JsonMode == true) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        if (options?.Tools is { Count: > 0 } tools)
        {
            var ta = new JsonArray();
            foreach (var tool in tools)
                ta.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["parameters"] = tool.SchemaNode },
                });
            body["tools"] = ta;
        }
        if (stream) body["stream"] = true;
        return body;
    }

    private static JsonObject ToJson(ChatMessage m)
    {
        var o = new JsonObject { ["role"] = m.Role.ToString().ToLowerInvariant() };
        if (m.Role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 } calls)
        {
            o["content"] = string.IsNullOrEmpty(m.Content) ? null : m.Content;
            var tc = new JsonArray();
            foreach (var c in calls)
                tc.Add(new JsonObject
                {
                    ["id"] = c.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
                });
            o["tool_calls"] = tc;
        }
        else o["content"] = m.Content;
        if (m.Role == ChatRole.Tool) o["tool_call_id"] = m.ToolCallId;
        return o;
    }

    internal static ChatResponse ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var choice = root.GetProperty("choices")[0];
        var msg = choice.GetProperty("message");
        string content = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
        var calls = new List<ToolCall>();
        if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            foreach (var tc in tcs.EnumerateArray())
            {
                var fn = tc.GetProperty("function");
                calls.Add(new ToolCall(tc.TryGetProperty("id", out var id) ? id.GetString() ?? "" : Guid.NewGuid().ToString("N"),
                    fn.GetProperty("name").GetString() ?? "", fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}"));
            }
        string? finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        var usage = default(TokenUsage);
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            usage = new TokenUsage(u.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0, u.TryGetProperty("completion_tokens", out var q) ? q.GetInt32() : 0);
        return new ChatResponse(content, calls, finish, usage);
    }

    internal static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
