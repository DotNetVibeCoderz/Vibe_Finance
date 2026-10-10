using System.Text.Json;

namespace Finance.LLM;

/// <summary>A host-provided tool: definition plus the delegate that executes it.</summary>
public sealed record AssistantTool(ToolDefinition Definition, Func<JsonElement, CancellationToken, Task<string>> Handler);

/// <summary>One executed tool call, for display and auditing.</summary>
public sealed record ToolInvocation(string Name, string ArgumentsJson, string Result, bool Failed);

/// <summary>Final answer of an assistant turn together with the tools it used.</summary>
public sealed record AssistantReply(string Text, IReadOnlyList<ToolInvocation> ToolCalls, TokenUsage Usage);

/// <summary>
/// A tool-using financial assistant: sends the conversation, executes requested tools through
/// host delegates (quotes, option pricing, VaR …), feeds results back and repeats until the
/// model answers in text or <see cref="MaxToolRounds"/> is reached. Conversation history is kept between turns.
/// </summary>
public sealed class FinancialAssistant
{
    public const string DefaultSystemPrompt =
        "You are Finance.Net Assistant, a precise quantitative finance analyst. Use the provided tools for any number you report " +
        "(prices, option values, risk metrics) instead of guessing. Show key figures with units, state assumptions, and keep answers concise. " +
        "You do not give personalised investment advice. Built by Gravicode Studios, led by Kang Fadhil.";

    private readonly IChatModel _model;
    private readonly Dictionary<string, AssistantTool> _tools = new(StringComparer.Ordinal);
    private readonly List<ChatMessage> _history = new();

    public FinancialAssistant(IChatModel model, string? systemPrompt = null)
    {
        _model = model;
        _history.Add(ChatMessage.System(systemPrompt ?? DefaultSystemPrompt));
    }

    public int MaxToolRounds { get; set; } = 6;
    public ChatOptions Options { get; set; } = new() { MaxTokens = 1500 };
    public IReadOnlyList<ChatMessage> History => _history;
    public IEnumerable<ToolDefinition> Tools => _tools.Values.Select(t => t.Definition);

    /// <summary>Registers a tool with a JSON-schema parameter description and an async handler.</summary>
    public FinancialAssistant RegisterTool(string name, string description, string parametersSchemaJson, Func<JsonElement, CancellationToken, Task<string>> handler)
    {
        _tools[name] = new AssistantTool(new ToolDefinition(name, description, parametersSchemaJson), handler);
        return this;
    }

    /// <summary>Registers a synchronous tool.</summary>
    public FinancialAssistant RegisterTool(string name, string description, string parametersSchemaJson, Func<JsonElement, string> handler)
        => RegisterTool(name, description, parametersSchemaJson, (args, _) => Task.FromResult(handler(args)));

    /// <summary>Clears the conversation but keeps the system prompt and tools.</summary>
    public void Reset() => _history.RemoveRange(1, _history.Count - 1);

    /// <summary>Sends a user message and runs the tool loop to a final answer.</summary>
    public async Task<AssistantReply> AskAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        _history.Add(ChatMessage.User(userMessage));
        var invocations = new List<ToolInvocation>();
        int prompt = 0, completion = 0;
        var options = Options with { Tools = _tools.Count > 0 ? _tools.Values.Select(t => t.Definition).ToList() : null };
        for (int round = 0; ; round++)
        {
            var resp = await _model.ChatAsync(_history, round >= MaxToolRounds ? options with { Tools = null } : options, cancellationToken).ConfigureAwait(false);
            prompt += resp.Usage.PromptTokens;
            completion += resp.Usage.CompletionTokens;
            if (!resp.HasToolCalls || round >= MaxToolRounds)
            {
                _history.Add(ChatMessage.Assistant(resp.Content));
                return new AssistantReply(resp.Content, invocations, new TokenUsage(prompt, completion));
            }
            _history.Add(new ChatMessage(ChatRole.Assistant, resp.Content) { ToolCalls = resp.ToolCalls });
            foreach (var call in resp.ToolCalls)
            {
                string result;
                bool failed = false;
                if (_tools.TryGetValue(call.Name, out var tool))
                {
                    try { result = await tool.Handler(call.Arguments, cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { result = JsonSerializer.Serialize(new { error = ex.Message }); failed = true; }
                }
                else
                {
                    result = JsonSerializer.Serialize(new { error = $"Unknown tool '{call.Name}'." });
                    failed = true;
                }
                invocations.Add(new ToolInvocation(call.Name, call.ArgumentsJson, result, failed));
                _history.Add(ChatMessage.ToolResult(call.Id, call.Name, result));
            }
        }
    }
}
