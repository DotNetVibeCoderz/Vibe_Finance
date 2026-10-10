using System.Net;
using System.Text.Json;
using Finance.LLM;

namespace Finance.AI.Tests;

public class LlmTests
{
    private const string ToolCallReply = """
        {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"price_option","arguments":"{\"spot\":100,\"strike\":105}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":50,"completion_tokens":10}}
        """;

    private const string TextReply = """
        {"choices":[{"message":{"role":"assistant","content":"The call is worth 8.02."},"finish_reason":"stop"}],"usage":{"prompt_tokens":70,"completion_tokens":8}}
        """;

    [Fact]
    public async Task AzureClientUsesDeploymentUrlAndApiKeyHeader()
    {
        var h = new FakeHandler().Enqueue(TextReply);
        var m = OpenAiCompatibleChatModel.Azure(h.Client(), "https://res.openai.azure.com/", "secret", "gpt-x");
        var r = await m.ChatAsync(new[] { ChatMessage.User("hi") }, new ChatOptions { MaxTokens = 10 });
        Assert.Equal("The call is worth 8.02.", r.Content);
        Assert.Equal(78, r.Usage.Total);
        var req = h.Requests[0].Request;
        Assert.Equal("https://res.openai.azure.com/openai/deployments/gpt-x/chat/completions?api-version=2024-12-01-preview", req.RequestUri!.ToString());
        Assert.Equal("secret", req.Headers.GetValues("api-key").Single());
        Assert.DoesNotContain("\"model\"", h.Requests[0].Body);
    }

    [Fact]
    public async Task ClientAdaptsToMaxCompletionTokensAndTemperatureErrors()
    {
        var h = new FakeHandler()
            .Enqueue("""{"error":{"message":"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead."}}""", HttpStatusCode.BadRequest)
            .Enqueue("""{"error":{"message":"Unsupported value: 'temperature' does not support 0.2 with this model."}}""", HttpStatusCode.BadRequest)
            .Enqueue(TextReply);
        var m = OpenAiCompatibleChatModel.OpenAI(h.Client(), "k", "o-model");
        var r = await m.ChatAsync(new[] { ChatMessage.User("hi") }, new ChatOptions { MaxTokens = 10, Temperature = 0.2 });
        Assert.Equal("The call is worth 8.02.", r.Content);
        Assert.Equal(3, h.Requests.Count);
        Assert.Contains("max_completion_tokens", h.Requests[2].Body);
        Assert.DoesNotContain("temperature", h.Requests[2].Body);
        Assert.Equal("Bearer", h.Requests[0].Request.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task ToolsAreSerialisedAndToolCallsParsed()
    {
        var h = new FakeHandler().Enqueue(ToolCallReply);
        var m = OpenAiCompatibleChatModel.OpenAI(h.Client(), "k");
        var tool = new ToolDefinition("price_option", "Prices an option", """{"type":"object","properties":{"spot":{"type":"number"}}}""");
        var r = await m.ChatAsync(new[] { ChatMessage.User("price it") }, new ChatOptions { Tools = new[] { tool } });
        Assert.True(r.HasToolCalls);
        Assert.Equal("price_option", r.ToolCalls[0].Name);
        Assert.Equal(100, r.ToolCalls[0].Arguments.GetProperty("spot").GetDouble());
        using var body = JsonDocument.Parse(h.Requests[0].Body);
        Assert.Equal("function", body.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task StreamingParsesServerSentEvents()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\ndata: [DONE]\n\n";
        var h = new FakeHandler().Enqueue(sse, contentType: "text/event-stream");
        var m = OpenAiCompatibleChatModel.OpenAI(h.Client(), "k");
        var parts = new List<string>();
        await foreach (var p in m.StreamAsync(new[] { ChatMessage.User("hi") })) parts.Add(p);
        Assert.Equal("Hello", string.Concat(parts));
        Assert.Contains("\"stream\":true", h.Requests[0].Body);
    }

    [Fact]
    public async Task AnthropicMapsSystemToolsAndToolUse()
    {
        var reply = """{"content":[{"type":"text","text":"Checking."},{"type":"tool_use","id":"tu_1","name":"get_quote","input":{"symbol":"AAPL"}}],"stop_reason":"tool_use","usage":{"input_tokens":12,"output_tokens":7}}""";
        var h = new FakeHandler().Enqueue(reply);
        var m = new AnthropicChatModel(h.Client(), "key", "claude-test");
        var tool = new ToolDefinition("get_quote", "Quote", """{"type":"object","properties":{"symbol":{"type":"string"}}}""");
        var r = await m.ChatAsync(new[] { ChatMessage.System("sys"), ChatMessage.User("AAPL?") }, new ChatOptions { Tools = new[] { tool } });
        Assert.Equal("Checking.", r.Content);
        Assert.Equal("AAPL", r.ToolCalls[0].Arguments.GetProperty("symbol").GetString());
        using var body = JsonDocument.Parse(h.Requests[0].Body);
        Assert.Equal("sys", body.RootElement.GetProperty("system").GetString());
        Assert.Equal("get_quote", body.RootElement.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("key", h.Requests[0].Request.Headers.GetValues("x-api-key").Single());

        // Tool results become user tool_result blocks.
        var b2 = m.BuildBody(new[]
        {
            ChatMessage.User("q"),
            new ChatMessage(ChatRole.Assistant, "") { ToolCalls = r.ToolCalls },
            ChatMessage.ToolResult("tu_1", "get_quote", "{\"price\":1}"),
        }, null, false);
        Assert.Equal("tool_result", b2["messages"]![2]!["content"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task OllamaParsesReply()
    {
        var h = new FakeHandler().Enqueue("""{"message":{"role":"assistant","content":"ok","tool_calls":[{"function":{"name":"f","arguments":{"a":1}}}]},"done":true,"prompt_eval_count":3,"eval_count":2}""");
        var m = new OllamaChatModel(h.Client(), "llama");
        var r = await m.ChatAsync(new[] { ChatMessage.User("x") });
        Assert.Equal("ok", r.Content);
        Assert.Equal("f", r.ToolCalls[0].Name);
        Assert.Equal(5, r.Usage.Total);
    }

    [Fact]
    public async Task AssistantRunsToolLoop()
    {
        var h = new FakeHandler().Enqueue(ToolCallReply).Enqueue(TextReply);
        var assistant = new FinancialAssistant(OpenAiCompatibleChatModel.OpenAI(h.Client(), "k"));
        double seenSpot = 0;
        assistant.RegisterTool("price_option", "Black-Scholes price", """{"type":"object","properties":{"spot":{"type":"number"},"strike":{"type":"number"}}}""",
            args => { seenSpot = args.GetProperty("spot").GetDouble(); return "{\"price\":8.02}"; });
        var reply = await assistant.AskAsync("Price a call");
        Assert.Equal("The call is worth 8.02.", reply.Text);
        Assert.Equal(100, seenSpot);
        Assert.Single(reply.ToolCalls);
        Assert.False(reply.ToolCalls[0].Failed);
        Assert.Contains("\"tool_call_id\":\"call_1\"", h.Requests[1].Body);
        Assert.Equal(5, assistant.History.Count); // system, user, assistant(tool), tool, assistant
    }

    [Fact]
    public async Task FinancialTasksParseFencedJson()
    {
        var content = "Sure!\n```json\n{\"label\":\"Positive\",\"score\":0.8,\"confidence\":0.9,\"rationale\":\"beat {estimates}\"}\n```";
        var reply = JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });
        var h = new FakeHandler().Enqueue(reply);
        var tasks = new FinancialTasks(OpenAiCompatibleChatModel.OpenAI(h.Client(), "k"));
        var s = await tasks.ClassifySentimentAsync("Apple beats estimates");
        Assert.Equal("positive", s.Label);
        Assert.Equal(0.8, s.Score, 9);
        Assert.Equal("beat {estimates}", s.Rationale);
        Assert.Contains("json_object", h.Requests[0].Body);
    }

    [Fact]
    public void KeyFileParsingPicksSectionAndFirstModel()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, new[]
            {
                "Azure OpenAI ", "apikey: KEY1", "endpoint: https://x.openai.azure.com/", "model: dep-a, dep-b", "",
                "DeepSeek", "Deep Seek Api Key: KEY2", "Deep Seek Endpoint: https://api.deepseek.com", "Model Id: deepseek-chat", "",
                "export AZURE_OPENAI_API_KEY=\"ignored\"",
            });
            var s = LlmSettings.FromKeyFile(path);
            Assert.Equal(LlmProvider.AzureOpenAI, s.Provider);
            Assert.Equal("dep-a", s.Model);
            Assert.Equal("KEY1", s.ApiKey);
            Assert.DoesNotContain("KEY1", s.ToString());
            var d = LlmSettings.FromKeyFile(path, "DeepSeek");
            Assert.Equal(LlmProvider.DeepSeek, d.Provider);
            Assert.Equal("KEY2", d.ApiKey);
            Assert.Equal("deepseek-chat", d.Model);

            File.WriteAllText(path, "Hugging-Face Access Token: hf_abc123");
            Assert.Equal("hf_abc123", LlmSettings.ReadHuggingFaceToken(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FinBertParsesClassification()
    {
        var h = new FakeHandler().Enqueue("""[[{"label":"positive","score":0.9},{"label":"negative","score":0.04},{"label":"neutral","score":0.06}]]""");
        var fb = new FinBertSentiment(new HuggingFaceInference(h.Client(), "hf_x"));
        var r = await fb.AnalyzeAsync("Profit soared");
        Assert.Equal("positive", r.Label);
        Assert.Equal(0.86, r.Score, 9);
        Assert.EndsWith("/ProsusAI/finbert", h.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Single(HuggingFaceInference.ParseClassification("""[{"label":"a","score":1}]""", 1));
    }

    [Fact]
    public void JsonExtractHandlesNestingAndStrings()
    {
        Assert.Equal("{\"a\":{\"b\":\"}\"}}", JsonExtract.FirstObject("x {\"a\":{\"b\":\"}\"}} y"));
        Assert.Null(JsonExtract.FirstObject("no json"));
    }
}

/// <summary>Live tests against real providers; skipped unless the key-file env vars are set.</summary>
public class LiveLlmTests
{
    [Fact]
    public async Task AzureOpenAiAnswersAndCallsTools()
    {
        var file = Environment.GetEnvironmentVariable("FINANCENET_LLM_KEYFILE");
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) Assert.Skip("FINANCENET_LLM_KEYFILE not set.");
        var model = LlmSettings.FromKeyFile(file).CreateChatModel();
        var text = await model.AskAsync("Reply with exactly the word: pong", options: new ChatOptions { MaxTokens = 400 });
        Assert.Contains("pong", text, StringComparison.OrdinalIgnoreCase);

        var assistant = new FinancialAssistant(model) { Options = new ChatOptions { MaxTokens = 2000 } };
        bool called = false;
        assistant.RegisterTool("get_quote", "Latest price of a stock symbol", """{"type":"object","properties":{"symbol":{"type":"string"}},"required":["symbol"]}""",
            a => { called = true; return "{\"symbol\":\"" + a.GetProperty("symbol").GetString() + "\",\"price\":187.25}"; });
        var reply = await assistant.AskAsync("What is the latest price of AAPL? Use the tool.");
        Assert.True(called);
        Assert.Contains("187", reply.Text);

        var sentiment = await new FinancialTasks(model).ClassifySentimentAsync("Company X beats estimates and raises full-year guidance");
        Assert.Equal("positive", sentiment.Label);
    }

    [Fact]
    public async Task HuggingFaceFinBertClassifies()
    {
        var file = Environment.GetEnvironmentVariable("FINANCENET_HF_TOKENFILE");
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) Assert.Skip("FINANCENET_HF_TOKENFILE not set.");
        var fb = new FinBertSentiment(new HuggingFaceInference(new HttpClient(), LlmSettings.ReadHuggingFaceToken(file)));
        SentimentResult r;
        try { r = await fb.AnalyzeAsync("Quarterly profit fell sharply and the company cut its outlook."); }
        catch (LlmException ex) when (ex.StatusCode == 402) { Assert.Skip("Hugging Face account has no inference credits (HTTP 402)."); throw; }
        Assert.Equal("negative", r.Label);
        Assert.True(r.Score < 0);
    }
}
