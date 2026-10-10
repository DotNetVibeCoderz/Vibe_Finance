using System.Text.Json;
using System.Text.Json.Nodes;

namespace Finance.LLM;

/// <summary>Role of a chat message.</summary>
public enum ChatRole { System, User, Assistant, Tool }

/// <summary>A tool invocation requested by the model.</summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson)
{
    /// <summary>Parsed arguments (an empty object when the model sent nothing parseable).</summary>
    public JsonElement Arguments
    {
        get
        {
            try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(ArgumentsJson) ? "{}" : ArgumentsJson).RootElement.Clone(); }
            catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
        }
    }
}

/// <summary>A chat message. Assistant messages may carry tool calls; tool messages carry a <see cref="ToolCallId"/>.</summary>
public sealed record ChatMessage(ChatRole Role, string Content)
{
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    /// <summary>Tool name for tool-result messages (needed by some providers).</summary>
    public string? Name { get; init; }

    public static ChatMessage System(string text) => new(ChatRole.System, text);
    public static ChatMessage User(string text) => new(ChatRole.User, text);
    public static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);
    public static ChatMessage ToolResult(string toolCallId, string name, string content) => new(ChatRole.Tool, content) { ToolCallId = toolCallId, Name = name };
}

/// <summary>A tool the model may call, described by a JSON schema of its parameters.</summary>
public sealed record ToolDefinition(string Name, string Description, string ParametersSchemaJson)
{
    internal JsonNode SchemaNode => JsonNode.Parse(ParametersSchemaJson) ?? new JsonObject();
}

/// <summary>Per-request options.</summary>
public sealed record ChatOptions
{
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }
    /// <summary>Ask for a JSON object response where the provider supports it.</summary>
    public bool JsonMode { get; init; }
    /// <summary>Overrides the client's default model.</summary>
    public string? Model { get; init; }
}

/// <summary>Token usage reported by the provider.</summary>
public readonly record struct TokenUsage(int PromptTokens, int CompletionTokens)
{
    public int Total => PromptTokens + CompletionTokens;
}

/// <summary>A model reply.</summary>
public sealed record ChatResponse(string Content, IReadOnlyList<ToolCall> ToolCalls, string? FinishReason, TokenUsage Usage)
{
    public bool HasToolCalls => ToolCalls.Count > 0;
}

/// <summary>Provider-neutral chat completion interface.</summary>
public interface IChatModel
{
    /// <summary>Provider and model, e.g. "azure-openai:gpt-4o".</summary>
    string Name { get; }

    Task<ChatResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Streams the text of the reply as it is generated.</summary>
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Raised when a provider returns an error status.</summary>
public sealed class LlmException(string message, int statusCode = 0) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class ChatModelExtensions
{
    /// <summary>Single-turn convenience: optional system prompt plus one user message.</summary>
    public static async Task<string> AskAsync(this IChatModel model, string prompt, string? system = null, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var msgs = new List<ChatMessage>();
        if (system != null) msgs.Add(ChatMessage.System(system));
        msgs.Add(ChatMessage.User(prompt));
        return (await model.ChatAsync(msgs, options, cancellationToken).ConfigureAwait(false)).Content;
    }
}

/// <summary>Reads server-sent-event "data:" payloads from a stream.</summary>
internal static class Sse
{
    public static async IAsyncEnumerable<string> ReadDataAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) yield break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            if (data.Length > 0) yield return data;
        }
    }
}
