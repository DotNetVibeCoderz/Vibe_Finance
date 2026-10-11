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
