using Finance.LLM;
using Finance.LLM.Local;

namespace Finance.AI.Tests;

public class LocalFinBertTests
{
    [Fact]
    public void FromScoresMapsFinBertLabels()
    {
        var r = FinBertSentiment.FromScores(new[] { new LabelScore("negative", 0.7), new LabelScore("neutral", 0.2), new LabelScore("positive", 0.1) }, "FinBERT (local)");
        Assert.Equal("negative", r.Label);
        Assert.Equal(0.1 - 0.7, r.Score, 12);
        Assert.Equal(0.7, r.Confidence, 12);
        Assert.Equal("FinBERT (local)", r.Source);
    }

    /// <summary>
    /// Downloads ProsusAI/finbert (~440 MB, once, into the Hugging Face cache) and runs it in-process.
    /// Enable with FINANCENET_FINBERT_LOCAL=1. The sentences are in the style of Financial PhraseBank, the
    /// data FinBERT was fine-tuned on, where its labels are unambiguous.
    /// </summary>
    [Fact]
    public void LocalFinBertClassifiesPhraseBankStyleSentences()
    {
        if (Environment.GetEnvironmentVariable("FINANCENET_FINBERT_LOCAL") != "1") Assert.Skip("FINANCENET_FINBERT_LOCAL=1 not set.");
        using var fb = LocalFinBertSentiment.Load();
        Assert.Equal(new[] { "negative", "neutral", "positive" }, fb.Labels.Select(l => l.ToLowerInvariant()).Order());

        var pos = fb.Analyze("Operating profit rose to EUR 13.1 mn from EUR 8.7 mn in the corresponding period in 2007.");
        var neg = fb.Analyze("Quarterly profit fell sharply and the company cut its full-year outlook.");
        var neu = fb.Analyze("The annual general meeting will be held in Helsinki on 3 April.");
        Assert.Equal("positive", pos.Label);
        Assert.Equal("negative", neg.Label);
        Assert.Equal("neutral", neu.Label);
        Assert.True(pos.Score > 0.5 && neg.Score < -0.5, $"pos {pos.Score:F3}, neg {neg.Score:F3}");

        var all = fb.Classify("Net sales increased by 18.5 % to EUR 167.8 mn.");
        Assert.Equal(1.0, all.Sum(s => s.Score), 6);
        Assert.Equal("FinBERT (local)", pos.Source);
    }
}

public class LocalEmbeddingTests
{
    private static double Cos(float[] a, float[] b)
    {
        double d = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { d += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return d / Math.Sqrt(na * nb);
    }

    /// <summary>Downloads all-MiniLM-L6-v2 (~90 MB) once; enable with FINANCENET_FINBERT_LOCAL=1 (local HF models).</summary>
    [Fact]
    public async Task MiniLmEmbeddingsRankBySemantics()
    {
        if (Environment.GetEnvironmentVariable("FINANCENET_FINBERT_LOCAL") != "1") Assert.Skip("FINANCENET_FINBERT_LOCAL=1 not set.");
        using var model = LocalEmbeddingModel.Load();
        var v = await model.EmbedAsync(new[]
        {
            "Revenue grew strongly compared with last year.",
            "Net sales increased year over year.",
            "The weather in Jakarta was sunny and humid.",
        }, TestContext.Current.CancellationToken);
        Assert.True(Cos(v[0], v[1]) > Cos(v[0], v[2]) + 0.2, $"{Cos(v[0], v[1]):F3} vs {Cos(v[0], v[2]):F3}");

        // Dense retrieval finds a paraphrase that shares no keywords with the query.
        var index = new Finance.LLM.Rag.FilingIndex(model, new Finance.LLM.Rag.DocumentChunker { MaxCharacters = 60, OverlapCharacters = 0 });
        await index.AddAsync(Finance.LLM.Rag.SourceDocument.FromText("A", "Doc", "Turnover rose markedly against the previous period.\n\nThe board appointed a new auditor."));
        var hit = (await index.SearchAsync("revenue growth", 1, TestContext.Current.CancellationToken))[0];
        Assert.Equal(2, index.Chunks.Count);                // one sentence per passage, so only the embeddings decide
        Assert.Contains("Turnover", hit.Chunk.Text);
    }
}
