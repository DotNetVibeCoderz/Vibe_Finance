# Finance.LLM

[← Indeks](../index.md) · [English](../../en/modules/llm.md) · Paket: `Gravicode.Finance.LLM`

Integrasi large language model untuk keuangan: satu abstraksi `IChatModel` untuk OpenAI, Azure OpenAI,
Anthropic, Ollama, Hugging Face, dan server apa pun yang kompatibel OpenAI; streaming; tool (function)
calling; agen `FinancialAssistant` yang tool-nya adalah fungsi C# Anda sendiri; helper tugas ala
FinGPT dengan keluaran JSON terstruktur; dan sentimen FinBERT lewat Hugging Face.

Namespace: `Finance.LLM`. Modul ini hanya bergantung pada `Finance.Core` — aplikasi host yang
menentukan fungsi Finance.Net mana yang dibuka sebagai tool.

## Konsep utama

- **Tool calling** — model tidak menghitung angka sendiri; ia meminta host memanggil fungsi bernama
  dengan argumen JSON (dijelaskan oleh JSON schema), membaca hasilnya, lalu menjawab. Dengan begitu
  harga opsi, angka VaR, dan kuotasi tetap tepat.
- **Grounding** — helper tugas hanya mengirim teks yang Anda berikan (judul berita, transkrip, kutipan
  konteks) dan mewajibkan balasan JSON, sehingga keluarannya bisa diperiksa.
- **FinBERT** — model BERT yang di-fine-tune pada teks keuangan untuk mengklasifikasikan kalimat menjadi
  positif, negatif, atau netral.

## Membuat model

```csharp
using Finance.LLM;

// Dari environment / key file (lihat Memulai → Mengonfigurasi LLM)
LlmSettings? settings = LlmSettings.FromEnvironment();
IChatModel model = settings!.CreateChatModel();
Console.WriteLine(settings);                    // AzureOpenAI model=... endpoint=host key=***

// Atau secara eksplisit
var http = new HttpClient();
IChatModel openai = OpenAiCompatibleChatModel.OpenAI(http, apiKey, model: "gpt-4o-mini");
IChatModel azure = OpenAiCompatibleChatModel.Azure(http, endpoint, apiKey, deployment: "my-deployment");
IChatModel claude = new AnthropicChatModel(http, anthropicKey);                 // model bawaan claude-sonnet-5-5
IChatModel local = new OllamaChatModel(http, model: "llama3.1");                // http://localhost:11434
IChatModel hf = OpenAiCompatibleChatModel.HuggingFace(http, hfToken);
```

## Chat dan streaming

```csharp
string answer = await model.AskAsync("Jelaskan convexity dalam dua kalimat.", system: "Anda tutor fixed income.");

var messages = new[] { ChatMessage.System("Jawab ringkas."), ChatMessage.User("Apa yang memengaruhi DV01 obligasi?") };
ChatResponse r = await model.ChatAsync(messages, new ChatOptions { MaxTokens = 300, Temperature = 0.2 });
Console.WriteLine($"{r.Content}  [{r.Usage.PromptTokens} masuk / {r.Usage.CompletionTokens} keluar]");

await foreach (string token in model.StreamAsync(messages))
    Console.Write(token);
```

`ChatOptions` juga menerima `Tools`, `JsonMode`, dan `Model` per panggilan. Klien yang kompatibel OpenAI
akan mengulang permintaan yang ditolak dengan `max_completion_tokens` alih-alih `max_tokens` dan/atau
tanpa `temperature`, seperti yang diwajibkan deployment Azure yang lebih baru.

## Asisten keuangan

```csharp
using System.Text.Json;
using Finance.Pricing.Options;

var assistant = new FinancialAssistant(model)
    .RegisterTool("price_option",
        "Harga Black-Scholes dan Greeks opsi Eropa.",
        """{"type":"object","properties":{"type":{"type":"string","enum":["call","put"]},"spot":{"type":"number"},"strike":{"type":"number"},"years":{"type":"number"},"volatility":{"type":"number"},"rate":{"type":"number"}},"required":["type","spot","strike","years","volatility","rate"]}""",
        args =>
        {
            var type = args.GetProperty("type").GetString() == "put" ? OptionType.Put : OptionType.Call;
            var g = BlackScholes.Greeks(type, args.GetProperty("spot").GetDouble(), args.GetProperty("strike").GetDouble(),
                args.GetProperty("years").GetDouble(), args.GetProperty("rate").GetDouble(), 0, args.GetProperty("volatility").GetDouble());
            return JsonSerializer.Serialize(new { price = g.Price, delta = g.Delta, vegaPerPoint = g.VegaPerPoint });
        });

AssistantReply reply = await assistant.AskAsync("Berapa harga call strike 110 jatuh tempo 6 bulan untuk saham 100, vol 30%, bunga 4%?");
Console.WriteLine(reply.Text);
foreach (ToolInvocation call in reply.ToolCalls) Console.WriteLine($"  ƒ {call.Name}({call.ArgumentsJson}) → {call.Result}");
```

Asisten menyimpan percakapan (`History`), menjalankan maksimal `MaxToolRounds` putaran tool per
pertanyaan, dan `Reset()` menghapusnya. Overload async `RegisterTool` menerima
`Func<JsonElement, CancellationToken, Task<string>>` untuk tool yang memanggil jaringan.

## Helper tugas keuangan

```csharp
var tasks = new FinancialTasks(model);

SentimentResult s = await tasks.ClassifySentimentAsync("Company X cuts guidance amid weak demand");
HeadlineSummary h = await tasks.SummarizeHeadlinesAsync(headlines);              // Summary, OverallSentiment, KeyThemes, Tickers, Risks
EarningsCallAnalysis e = await tasks.AnalyzeEarningsCallAsync(transcript);       // Tone, ToneScore, Guidance, KeyMetrics, Risks, ...
FinancialAnswer a = await tasks.AnswerAsync("Berapa gross margin Q3?", contextPassages);   // Answer, Citations, Confidence
```

## FinBERT lewat Hugging Face

```csharp
string token = LlmSettings.ReadHuggingFaceToken(@"path\ke\HFToken.txt");
var hfClient = new HuggingFaceInference(new HttpClient(), token);
var finbert = new FinBertSentiment(hfClient);                    // ProsusAI/finbert
SentimentResult fs = await finbert.AnalyzeAsync("Revenue grew 25% year over year, beating estimates.");
IReadOnlyList<LabelScore> raw = await hfClient.ClassifyAsync("ProsusAI/finbert", "Shares plunged after the downgrade.");
```

## FinBERT lokal (tanpa API, tanpa kredit)

Paket `Gravicode.Finance.LLM.Local` menjalankan FinBERT di dalam proses lewat HF.Net
(`Gravicode.HFNet.GraviTransformers`). Paketnya dipisah agar `Gravicode.Finance.LLM` tetap tanpa
dependensi berat.

```csharp
using Finance.LLM.Local;

using var finbert = LocalFinBertSentiment.Load();               // ProsusAI/finbert, ~440 MB, diunduh sekali
SentimentResult s = finbert.Analyze("Operating profit rose to EUR 13.1 mn from EUR 8.7 mn.");
// s.Label = "positive", s.Score = P(positive) − P(negative) = 0.92, s.Source = "FinBERT (local)"
IReadOnlyList<LabelScore> all = finbert.Classify("Quarterly profit fell sharply.");

// Pasang ke pipeline berita:
var model = new DelegateSentimentModel("FinBERT", async (text, ct) =>
{
    var r = await finbert.AnalyzeAsync(text, ct);
    return (r.Label, r.Score);
});
```

Checkpoint disimpan di cache Hugging Face bersama (`HF_HUB_CACHE` / `HF_HOME`), sehingga Python dan
.NET berbagi satu salinan. `LocalFinBertSentiment.Open(dir)` membaca folder lokal. Hasil ukur di laptop
4 core: sekitar **100 ms per kalimat**. ProsusAI/finbert hanya menyediakan `pytorch_model.bin` legacy
(pra-torch-1.6); HF.Net 0.8.1 membacanya langsung. `Load(repoId, revision)` menerima branch, tag atau
commit apa pun.

## Catatan dan batasan

- **Status live**: jalur Azure OpenAI (chat, loop tool-calling, sentimen JSON) sudah diverifikasi pada
  deployment sungguhan. Panggilan **FinBERT Hugging Face** berhasil diautentikasi, tetapi API hosted
  menjawab **HTTP 402 — kredit inferensi habis** pada akun uji; perlu kredit prabayar atau HF PRO.
  **FinBERT lokal** (`Finance.LLM.Local`, di atas) tidak memerlukan keduanya dan sudah diverifikasi
  dengan model aslinya.
- Jika key file mencantumkan beberapa deployment di `model:`, yang pertama dipakai.
- Jangan pernah meng-commit key file; `LlmSettings.ToString()` menyamarkan kunci untuk log.
- Asisten tidak memberikan saran investasi personal (dinyatakan di system prompt bawaannya,
  `FinancialAssistant.DefaultSystemPrompt`).

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
