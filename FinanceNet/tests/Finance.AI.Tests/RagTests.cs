using Finance.LLM;
using Finance.LLM.Local;
using Finance.LLM.Rag;
using Finance.MarketData.Fundamentals;

namespace Finance.AI.Tests;

/// <summary>Filings RAG: chunking, retrieval, the deterministic citation checker, and the Q&amp;A loop.</summary>
public class RagTests
{
    private const string Filing = """
        <html><head><style>p{color:red}</style><script>var x = 1;</script></head><body>
        <p>PART II</p>
        <p>Item 7. Management&#8217;s Discussion and Analysis of Financial Condition and Results of Operations</p>
        <p>Total net sales increased 6% or $24.6 billion during 2025 compared to 2024, driven by higher net sales of iPhone and Services.</p>
        <table><tr><td>Net sales</td><td>416,161</td><td>391,035</td></tr><tr><td>Gross margin percentage</td><td>46.9%</td><td>46.2%</td></tr></table>
        <p>Item 1A. Risk Factors</p>
        <p>The Company&#8217;s operations are subject to risks from global and regional economic conditions &amp; supply-chain disruption.</p>
        </body></html>
        """;

    [Fact]
    public void HtmlBecomesSectionedPassages()
    {
        var doc = SourceDocument.FromHtml("AAPL-10K-2025", "Apple 10-K 2025", Filing);
        Assert.DoesNotContain("color:red", doc.Pages[0]);
        Assert.Contains("economic conditions & supply-chain", doc.Pages[0]);
        Assert.Contains("Net sales | 416,161 | 391,035", doc.Pages[0]);

        var chunks = new DocumentChunker().Chunk(doc);
        Assert.Contains(chunks, c => c.Section.StartsWith("Item 7") && c.Text.Contains("416,161"));
        Assert.Contains(chunks, c => c.Section.StartsWith("Item 1A") && c.Text.Contains("supply-chain"));
    }

    [Fact]
    public async Task Bm25RanksThePassageThatAnswers()
    {
        var index = new FilingIndex();
        await index.AddAsync(SourceDocument.FromHtml("A", "Apple 10-K", Filing));
        await index.AddAsync(SourceDocument.FromText("B", "Bank laporan tahunan", "Laba bersih perseroan naik menjadi Rp 54,8 triliun.\nDana pihak ketiga tumbuh 7%."));
        var hits = await index.SearchAsync("What were total net sales and gross margin?");
        Assert.Equal("A", hits[0].Chunk.DocumentId);
        var id = await index.SearchAsync("berapa laba bersih perseroan");
        Assert.Equal("B", id[0].Chunk.DocumentId);
    }

    private static readonly string[] Passages =
    {
        "Net sales | 416,161 | 391,035\nGross margin percentage | 46.9% | 46.2%",            // a table in millions
        "Total net sales increased 6% or $24.6 billion during 2025 compared to 2024.",
    };

    [Fact]
    public void CitationCheckerAcceptsFaithfulClaimsAcrossUnits()
    {
        // $416.2 billion in prose is 416,161 million in the table; 46.9% is quoted exactly.
        var claims = CitationChecker.Check(
            "Total net sales were $416.2 billion in 2025 [1, 2]. Gross margin percentage rose to 46.9% [1].", Passages);
        Assert.All(claims, c => Assert.True(c.Supported, string.Join("; ", c.Issues)));
    }

    [Fact]
    public void CitationCheckerFlagsWrongNumbersMissingAndPhantomCitations()
    {
        var claims = CitationChecker.Check(
            "Total net sales were $430.0 billion in 2025 [1]. Net sales grew strongly. Gross margin percentage was 46.9% [7].", Passages);
        Assert.Equal(3, claims.Count);
        Assert.Contains(claims[0].Issues, i => i.Contains("430.0"));
        Assert.Contains("no citation", claims[1].Issues);
        Assert.Contains(claims[2].Issues, i => i.Contains("do not exist"));
        Assert.All(claims, c => Assert.False(c.Supported));
    }

    [Fact]
    public void IndonesianThousandsSeparatorsAndScaleWords()
    {
        var claims = CitationChecker.Check("Laba bersih perseroan mencapai Rp 1.250 miliar [1].",
            new[] { "Laba bersih perseroan tahun 2024 sebesar Rp 1.250.000.000.000." });
        Assert.True(claims[0].Supported, string.Join("; ", claims[0].Issues));
    }

    /// <summary>A scripted chat model: returns a fixed reply and records the prompt.</summary>
    private sealed class Scripted(string reply) : IChatModel
    {
        public string Name => "scripted";
        public string? LastPrompt { get; private set; }

        public Task<ChatResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastPrompt = messages[^1].Content;
            return Task.FromResult(new ChatResponse(reply, Array.Empty<ToolCall>(), "stop", new TokenUsage(0, 0)));
        }

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return reply;
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task QaReturnsCheckedAnswersAndHonoursNotFound()
    {
        var index = new FilingIndex();
        await index.AddAsync(SourceDocument.FromHtml("A", "Apple 10-K", Filing));

        var model = new Scripted("Total net sales increased 6% or $24.6 billion during 2025 [1].");
        var qa = new FilingsQa(model, index) { TopK = 1 };
        var answer = await qa.AskAsync("How much did total net sales increase in 2025?");
        Assert.Contains("[1] (Apple 10-K, Item 7", model.LastPrompt);
        Assert.True(answer.IsGrounded, string.Join("; ", answer.Claims.SelectMany(c => c.Issues)));

        var none = await new FilingsQa(new Scripted("NOT_FOUND"), index).AskAsync("What is the CEO's favourite colour?");
        Assert.True(none.NotFound);
        Assert.False(none.IsGrounded);
    }

    [Fact]
    public void PdfReportsKeepPageNumbers()
    {
        var pdf = PdfNet.Document.PdfDocument.Create();
        foreach (var text in new[] { "Laporan Tahunan 2024", "Laba bersih tahun berjalan Rp 54,8 triliun" })
        {
            var page = pdf.Pages.Add();
            using var canvas = page.OpenCanvas();
            canvas.SetFont(PdfNet.Content.StandardFont.Helvetica, 12);
            canvas.DrawText(text, 72, 720);
        }
        using var ms = new MemoryStream();
        pdf.Save(ms);
        var bytes = ms.ToArray();
        var doc = PdfReports.Load(bytes, "BBCA-AR-2024", "BCA Annual Report 2024");
        Assert.Equal(2, doc.Pages.Count);
        Assert.Contains("54,8 triliun", doc.Pages[1]);
        var chunk = new DocumentChunker().Chunk(doc).Single(c => c.Text.Contains("Laba bersih"));
        Assert.Equal(2, chunk.Page);
        Assert.StartsWith("BCA Annual Report 2024, p. 2", chunk.Reference);
    }

    /// <summary>End to end on Apple's latest 10-K with a real model: FINANCENET_LIVE=1, FINANCENET_SEC_USER_AGENT, FINANCENET_LLM_KEYFILE.</summary>
    [Fact]
    public async Task LiveTenKQuestionIsAnsweredWithCheckedCitations()
    {
        var ua = Environment.GetEnvironmentVariable("FINANCENET_SEC_USER_AGENT");
        var keyFile = Environment.GetEnvironmentVariable("FINANCENET_LLM_KEYFILE");
        if (Environment.GetEnvironmentVariable("FINANCENET_LIVE") != "1" || string.IsNullOrWhiteSpace(ua) || string.IsNullOrWhiteSpace(keyFile) || !File.Exists(keyFile))
            Assert.Skip("FINANCENET_LIVE, FINANCENET_SEC_USER_AGENT and FINANCENET_LLM_KEYFILE are needed.");
        var ct = TestContext.Current.CancellationToken;
        var sec = new SecEdgarProvider(ua);
        var filing = (await sec.GetFilingsAsync("AAPL", "10-K", 1, ct))[0];
        var html = await sec.GetFilingDocumentAsync(filing, ct);
        var index = new FilingIndex();
        await index.AddAsync(SourceDocument.FromHtml($"AAPL-{filing.FilingDate:yyyy}", $"Apple 10-K {filing.FilingDate:yyyy}", html), ct);
        Assert.True(index.Chunks.Count > 100);

        var model = LlmSettings.FromKeyFile(keyFile).CreateChatModel();
        var answer = await new FilingsQa(model, index).AskAsync("What were Apple's total net sales for the most recent fiscal year, and how did they change from the prior year?", ct);
        Assert.False(answer.NotFound, answer.Answer);
        Assert.True(answer.Claims.Count(c => c.Supported) >= answer.Claims.Count - 1,
            answer.Answer + "\n" + string.Join("\n", answer.UnsupportedClaims.Select(c => c.Sentence + " :: " + string.Join("; ", c.Issues))));
    }
}
