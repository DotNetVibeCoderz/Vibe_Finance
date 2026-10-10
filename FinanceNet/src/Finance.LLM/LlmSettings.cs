namespace Finance.LLM;

/// <summary>Supported providers.</summary>
public enum LlmProvider { OpenAI, AzureOpenAI, Anthropic, Ollama, HuggingFace, DeepSeek, OpenAICompatible }

/// <summary>
/// Connection settings for a chat model, loadable from environment variables or from a
/// "key: value" key file. Use <see cref="CreateChatModel"/> to get an <see cref="IChatModel"/>.
/// </summary>
public sealed record LlmSettings
{
    public LlmProvider Provider { get; init; } = LlmProvider.OpenAI;
    public string? ApiKey { get; init; }
    /// <summary>Base endpoint (Azure resource URL, OpenAI-compatible base URL, Ollama URL).</summary>
    public string? Endpoint { get; init; }
    /// <summary>Model or Azure deployment name.</summary>
    public string Model { get; init; } = "gpt-4o-mini";
    public string AzureApiVersion { get; init; } = "2024-12-01-preview";

    /// <summary>Redacted description safe for logs.</summary>
    public override string ToString() => $"{Provider} model={Model} endpoint={(Endpoint is null ? "-" : new Uri(Endpoint).Host)} key={(string.IsNullOrEmpty(ApiKey) ? "none" : "***")}";

    /// <summary>
    /// Reads FINANCENET_LLM_PROVIDER, FINANCENET_LLM_API_KEY, FINANCENET_LLM_ENDPOINT, FINANCENET_LLM_MODEL,
    /// falling back to the conventional AZURE_OPENAI_*, OPENAI_API_KEY and ANTHROPIC_API_KEY variables. Returns null when nothing is configured.
    /// </summary>
    public static LlmSettings? FromEnvironment()
    {
        string? Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v : null;
        if (Env("FINANCENET_LLM_KEYFILE") is { } file && File.Exists(file)) return FromKeyFile(file);
        if (Env("FINANCENET_LLM_PROVIDER") is { } p && Enum.TryParse<LlmProvider>(p, true, out var provider))
            return new LlmSettings { Provider = provider, ApiKey = Env("FINANCENET_LLM_API_KEY"), Endpoint = Env("FINANCENET_LLM_ENDPOINT"), Model = Env("FINANCENET_LLM_MODEL") ?? "gpt-4o-mini" };
        if (Env("AZURE_OPENAI_API_KEY") is { } ak && Env("AZURE_OPENAI_ENDPOINT") is { } ae)
            return new LlmSettings { Provider = LlmProvider.AzureOpenAI, ApiKey = ak, Endpoint = ae, Model = Env("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o-mini" };
        if (Env("OPENAI_API_KEY") is { } ok) return new LlmSettings { Provider = LlmProvider.OpenAI, ApiKey = ok, Model = Env("OPENAI_MODEL") ?? "gpt-4o-mini" };
        if (Env("ANTHROPIC_API_KEY") is { } an) return new LlmSettings { Provider = LlmProvider.Anthropic, ApiKey = an, Model = Env("ANTHROPIC_MODEL") ?? "claude-sonnet-5-5" };
        return null;
    }

    /// <summary>
    /// Parses a key file made of sections: a provider title line (e.g. "Azure OpenAI") followed by
    /// "key: value" lines (apikey, endpoint, model). The first section matching <paramref name="section"/>
    /// (case-insensitive prefix, default "Azure OpenAI") is used; a comma-separated model list takes the first entry.
    /// </summary>
    public static LlmSettings FromKeyFile(string path, string section = "Azure OpenAI")
    {
        var sections = ParseKeyFile(File.ReadAllLines(path));
        var match = sections.FirstOrDefault(s => s.Title.Equals(section, StringComparison.OrdinalIgnoreCase))
            ?? sections.FirstOrDefault(s => s.Title.StartsWith(section, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Section '{section}' not found in key file.");
        string? Get(params string[] keys) => keys.Select(k => match.Values.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => v != null);
        var model = Get("model", "modelid", "model id", "deployment") ?? "gpt-4o-mini";
        model = model.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0];
        var provider = match.Title.Contains("azure", StringComparison.OrdinalIgnoreCase) ? LlmProvider.AzureOpenAI
            : match.Title.Contains("anthro", StringComparison.OrdinalIgnoreCase) ? LlmProvider.Anthropic
            : match.Title.Contains("deep", StringComparison.OrdinalIgnoreCase) ? LlmProvider.DeepSeek
            : match.Title.Contains("hugging", StringComparison.OrdinalIgnoreCase) ? LlmProvider.HuggingFace
            : match.Title.Contains("ollama", StringComparison.OrdinalIgnoreCase) ? LlmProvider.Ollama
            : LlmProvider.OpenAICompatible;
        return new LlmSettings
        {
            Provider = provider,
            ApiKey = Get("apikey", "api key", "key") ?? match.Values.FirstOrDefault(kv => kv.Key.EndsWith("api key") || kv.Key.EndsWith("apikey")).Value,
            Endpoint = Get("endpoint") ?? match.Values.FirstOrDefault(kv => kv.Key.EndsWith("endpoint")).Value,
            Model = model,
        };
    }

    /// <summary>Reads a Hugging Face token from a file containing either the bare token or "...: hf_xxx".</summary>
    public static string ReadHuggingFaceToken(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            int i = line.IndexOf("hf_", StringComparison.Ordinal);
            if (i >= 0) return line[i..].Split(' ', '\t')[0];
        }
        throw new InvalidOperationException("No Hugging Face token found in file.");
    }

    internal sealed record KeySection(string Title, Dictionary<string, string> Values);

    internal static List<KeySection> ParseKeyFile(IEnumerable<string> lines)
    {
        var result = new List<KeySection>();
        KeySection? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("export ", StringComparison.Ordinal)) continue;
            int colon = line.IndexOf(':');
            bool isUrlOnly = line.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            if (colon > 0 && !isUrlOnly)
            {
                current ??= new KeySection("", new(StringComparer.OrdinalIgnoreCase));
                if (!result.Contains(current)) result.Add(current);
                var key = line[..colon].Trim().ToLowerInvariant();
                current.Values.TryAdd(key, line[(colon + 1)..].Trim());
            }
            else
            {
                current = new KeySection(line, new(StringComparer.OrdinalIgnoreCase));
                result.Add(current);
            }
        }
        return result;
    }

    /// <summary>Creates the matching chat client.</summary>
    public IChatModel CreateChatModel(HttpClient? http = null)
    {
        http ??= new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        string Key() => ApiKey ?? throw new InvalidOperationException($"{Provider} needs an API key.");
        return Provider switch
        {
            LlmProvider.AzureOpenAI => OpenAiCompatibleChatModel.Azure(http, Endpoint ?? throw new InvalidOperationException("Azure needs an endpoint."), Key(), Model, AzureApiVersion),
            LlmProvider.OpenAI => OpenAiCompatibleChatModel.OpenAI(http, Key(), Model, Endpoint ?? "https://api.openai.com/v1"),
            LlmProvider.DeepSeek => OpenAiCompatibleChatModel.DeepSeek(http, Key(), Model),
            LlmProvider.HuggingFace => OpenAiCompatibleChatModel.HuggingFace(http, Key(), Model),
            LlmProvider.Anthropic => new AnthropicChatModel(http, Key(), Model, Endpoint ?? "https://api.anthropic.com"),
            LlmProvider.Ollama => new OllamaChatModel(http, Model, Endpoint ?? "http://localhost:11434"),
            _ => new OpenAiCompatibleChatModel(http, new OpenAiCompatibleOptions
            {
                Endpoint = (Endpoint ?? throw new InvalidOperationException("Endpoint required.")).TrimEnd('/') + "/chat/completions",
                ApiKey = ApiKey,
                Model = Model,
                ProviderName = "openai-compatible",
            }),
        };
    }
}
