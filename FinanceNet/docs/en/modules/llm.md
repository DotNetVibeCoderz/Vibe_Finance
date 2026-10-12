# Finance.LLM

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/llm.md) · Package: `Gravicode.Finance.LLM`

Large-language-model integration for finance: one `IChatModel` abstraction over OpenAI, Azure
OpenAI, Anthropic, Ollama, Hugging Face and any OpenAI-compatible server; streaming; tool
(function) calling; a `FinancialAssistant` agent loop whose tools are your own C# functions;
FinGPT-style task helpers with structured JSON outputs; and FinBERT sentiment through Hugging Face —
hosted here, or in-process through the companion package `Gravicode.Finance.LLM.Local`.

Namespace: `Finance.LLM`. It depends only on `Finance.Core` — the host application decides which
Finance.Net functions to expose as tools.

## Key concepts

- **Tool calling** — the model does not compute numbers itself; it asks the host to call a named
  function with JSON arguments (described by a JSON schema), reads the result, and answers. This
  keeps option prices, VaR figures and quotes exact.
- **Grounding** — task helpers send the model only the text you provide (headlines, a transcript,
  context passages) and require JSON back, so outputs are checkable.
- **FinBERT** — a BERT model fine-tuned on financial text that classifies sentences as positive,
  negative or neutral.

## Creating a model

```csharp
using Finance.LLM;

// From the environment / a key file (see Getting started → Configure an LLM)
LlmSettings? settings = LlmSettings.FromEnvironment();
IChatModel model = settings!.CreateChatModel();
Console.WriteLine(settings);                    // AzureOpenAI model=... endpoint=host key=***

// Or explicitly
var http = new HttpClient();
IChatModel openai = OpenAiCompatibleChatModel.OpenAI(http, apiKey, model: "gpt-4o-mini");
IChatModel azure = OpenAiCompatibleChatModel.Azure(http, endpoint, apiKey, deployment: "my-deployment");
IChatModel claude = new AnthropicChatModel(http, anthropicKey);                 // default model claude-sonnet-5-5
IChatModel local = new OllamaChatModel(http, model: "llama3.1");                // http://localhost:11434
IChatModel hf = OpenAiCompatibleChatModel.HuggingFace(http, hfToken);
```

## Chat and streaming

```csharp
string answer = await model.AskAsync("Explain convexity in two sentences.", system: "You are a fixed-income tutor.");

var messages = new[] { ChatMessage.System("Be concise."), ChatMessage.User("What drives a bond's DV01?") };
ChatResponse r = await model.ChatAsync(messages, new ChatOptions { MaxTokens = 300, Temperature = 0.2 });
Console.WriteLine($"{r.Content}  [{r.Usage.PromptTokens} in / {r.Usage.CompletionTokens} out]");

await foreach (string token in model.StreamAsync(messages))
    Console.Write(token);
```

`ChatOptions` also accepts `Tools`, `JsonMode` and a per-call `Model`. The OpenAI-compatible client
retries a rejected request with `max_completion_tokens` instead of `max_tokens` and/or without
`temperature`, which newer Azure deployments require.

## The financial assistant

```csharp
using System.Text.Json;
using Finance.Pricing.Options;

var assistant = new FinancialAssistant(model)
    .RegisterTool("price_option",
        "Black-Scholes price and Greeks of a European option.",
        """{"type":"object","properties":{"type":{"type":"string","enum":["call","put"]},"spot":{"type":"number"},"strike":{"type":"number"},"years":{"type":"number"},"volatility":{"type":"number"},"rate":{"type":"number"}},"required":["type","spot","strike","years","volatility","rate"]}""",
        args =>
        {
            var type = args.GetProperty("type").GetString() == "put" ? OptionType.Put : OptionType.Call;
            var g = BlackScholes.Greeks(type, args.GetProperty("spot").GetDouble(), args.GetProperty("strike").GetDouble(),
                args.GetProperty("years").GetDouble(), args.GetProperty("rate").GetDouble(), 0, args.GetProperty("volatility").GetDouble());
            return JsonSerializer.Serialize(new { price = g.Price, delta = g.Delta, vegaPerPoint = g.VegaPerPoint });
        });

AssistantReply reply = await assistant.AskAsync("Price a 6-month 110 call on a 100 stock with 30% vol and 4% rates.");
Console.WriteLine(reply.Text);
foreach (ToolInvocation call in reply.ToolCalls) Console.WriteLine($"  ƒ {call.Name}({call.ArgumentsJson}) → {call.Result}");
```

The assistant keeps the conversation (`History`), runs up to `MaxToolRounds` tool rounds per
question, and `Reset()` clears it. An async overload of `RegisterTool` takes
`Func<JsonElement, CancellationToken, Task<string>>` for tools that call the network.

## Financial task helpers

```csharp
var tasks = new FinancialTasks(model);

SentimentResult s = await tasks.ClassifySentimentAsync("Company X cuts guidance amid weak demand");
HeadlineSummary h = await tasks.SummarizeHeadlinesAsync(headlines);              // Summary, OverallSentiment, KeyThemes, Tickers, Risks
EarningsCallAnalysis e = await tasks.AnalyzeEarningsCallAsync(transcript);       // Tone, ToneScore, Guidance, KeyMetrics, Risks, ...
FinancialAnswer a = await tasks.AnswerAsync("What was Q3 gross margin?", contextPassages);   // Answer, Citations, Confidence
```

## FinBERT via Hugging Face

```csharp
string token = LlmSettings.ReadHuggingFaceToken(@"path\to\HFToken.txt");
var hfClient = new HuggingFaceInference(new HttpClient(), token);
var finbert = new FinBertSentiment(hfClient);                    // ProsusAI/finbert
SentimentResult fs = await finbert.AnalyzeAsync("Revenue grew 25% year over year, beating estimates.");
IReadOnlyList<LabelScore> raw = await hfClient.ClassifyAsync("ProsusAI/finbert", "Shares plunged after the downgrade.");
```

## Local FinBERT (no API, no credits)

Package `Gravicode.Finance.LLM.Local` runs FinBERT in-process through HF.Net
(`Gravicode.HFNet.GraviTransformers`). It is a separate package so that `Gravicode.Finance.LLM` keeps
zero heavy dependencies.

```csharp
using Finance.LLM.Local;

using var finbert = LocalFinBertSentiment.Load();               // ProsusAI/finbert, ~440 MB, downloaded once
SentimentResult s = finbert.Analyze("Operating profit rose to EUR 13.1 mn from EUR 8.7 mn.");
// s.Label = "positive", s.Score = P(positive) − P(negative) = 0.92, s.Source = "FinBERT (local)"
IReadOnlyList<LabelScore> all = finbert.Classify("Quarterly profit fell sharply.");

// Plug it into the news pipeline:
var model = new DelegateSentimentModel("FinBERT", async (text, ct) =>
{
    var r = await finbert.AnalyzeAsync(text, ct);
    return (r.Label, r.Score);
});
```

The checkpoint goes into the shared Hugging Face cache (`HF_HUB_CACHE` / `HF_HOME`), so Python and
.NET share one copy. `LocalFinBertSentiment.Open(dir)` reads a local folder instead. Measured on a
4-core laptop: about **100 ms per sentence**. ProsusAI/finbert ships only a legacy (pre-torch-1.6)
`pytorch_model.bin`; HF.Net (0.8.1+) reads it directly. `Load(repoId, revision)` takes any branch, tag
or commit.

## Filings Q&A with citations (v0.4)

Retrieval-augmented question answering over 10-K/10-Q filings and annual reports (including Indonesian IDX
reports), with every answer sentence checked against the passages it cites.

```csharp
using Finance.LLM.Rag;
using Finance.LLM.Local;
using Finance.MarketData.Fundamentals;

// 1. Documents: an EDGAR 10-K (HTML), or a PDF annual report (one page per PDF page, cited as "p. N").
var sec = new SecEdgarProvider("Your Name you@example.com");
EdgarFiling tenK = (await sec.GetFilingsAsync("AAPL", "10-K", count: 1))[0];
string html = await sec.GetFilingDocumentAsync(tenK);
var filing = SourceDocument.FromHtml(tenK.AccessionNumber, $"Apple 10-K {tenK.ReportDate:yyyy}", html);
SourceDocument report = PdfReports.Load(@"BBCA-annual-report-2024.pdf");

// 2. Index: section-aware chunks, BM25 (English + Indonesian stopwords) fused with dense embeddings (optional).
using var embedder = LocalEmbeddingModel.Load();               // all-MiniLM-L6-v2, ~90 MB, in-process
var index = new FilingIndex(embedder);                          // new FilingIndex() = BM25 only
await index.AddAsync(filing);
await index.AddAsync(report);

// 3. Ask: the model must cite passages as [n]; every sentence is checked against what it cites.
var qa = new FilingsQa(chatModel, index) { TopK = 6 };
GroundedAnswer a = await qa.AskAsync("What were total net sales and how did they change?");
Console.WriteLine(a.Answer);
foreach (var p in a.Passages) Console.WriteLine($"  {p.Chunk.Reference}");      // "Apple 10-K 2025, Item 7. ..."
if (!a.IsGrounded) foreach (var c in a.UnsupportedClaims) Console.WriteLine($"  ⚠ {c.Sentence}: {string.Join("; ", c.Issues)}");
```

`CitationChecker` flags a sentence when it has no citation, cites a passage that was not retrieved, states a
number that does not appear in the cited passages (it understands "million/billion", "juta/miliar/triliun",
percent and Indonesian `1.234.567` thousands separators), or shares too few content words with them
(`MinimumOverlap`, 0.5). When the passages do not contain the answer the model replies `NOT_FOUND` and
`GroundedAnswer.NotFound` is set. The check is deterministic, so it also works on answers from any other
source. Verified live: Apple's latest 10-K from EDGAR answered through Azure OpenAI, fully grounded.

`LocalEmbeddingModel` (package `Gravicode.Finance.LLM.Local`) runs any sentence-transformers checkpoint
through HF.Net with mean pooling and L2 normalisation; implement `IEmbeddingModel` to use another embedder.
PDF reading uses Gravicode.OfficeNet.PdfNet; scanned (image-only) PDFs need OCR first.

## Notes and limitations

- **Live status**: the Azure OpenAI path (chat, tool-calling loop, JSON sentiment) was verified
  against a real deployment. The **Hugging Face FinBERT** call authenticated but the hosted API
  answered **HTTP 402 — no inference credits** on the test account; it needs prepaid credits or HF
  PRO. **Local FinBERT** (`Finance.LLM.Local`, above) needs neither and was verified on the real
  model.
- When a key file lists several deployments in `model:`, the first is used.
- Never commit key files; `LlmSettings.ToString()` redacts keys for logging.
- The assistant does not give personalised investment advice (it says so in its default system
  prompt, `FinancialAssistant.DefaultSystemPrompt`).

---

Made by Gravicode Studios, led by Kang Fadhil
