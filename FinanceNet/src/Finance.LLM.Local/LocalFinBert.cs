using Gravicode.HFNet.GraviTransformers;

namespace Finance.LLM.Local;

/// <summary>
/// FinBERT run in-process through HF.Net (Gravicode.HFNet.GraviTransformers): no Inference API, no credits, no
/// network after the first download. <see cref="Load"/> fetches the checkpoint from the Hub into the shared Hugging
/// Face cache (<c>HF_HUB_CACHE</c> / <c>HF_HOME</c>, the same one Python uses); <see cref="Open"/> reads a local
/// directory. Results have the same shape as the hosted <see cref="FinBertSentiment"/>.
/// </summary>
/// <remarks>
/// A model instance is not thread-safe; calls are serialised internally, so one instance can be shared.
/// Inference is CPU-bound — about a few hundred milliseconds per sentence on a laptop for the 110M-parameter
/// ProsusAI/finbert.
/// </remarks>
public sealed class LocalFinBertSentiment : IDisposable
{
    /// <summary>The default checkpoint: ProsusAI/finbert (BERT-base fine-tuned on Financial PhraseBank).</summary>
    public const string DefaultModel = "ProsusAI/finbert";

    private readonly TransformerModel _model;
    private readonly Lock _gate = new();

    private LocalFinBertSentiment(TransformerModel model)
    {
        if (!model.HasClassificationHead)
        {
            model.Dispose();
            throw new ArgumentException("The checkpoint has no sequence classification head.", nameof(model));
        }
        _model = model;
    }

    /// <summary>
    /// Downloads (first time only) and loads a Hub checkpoint at <paramref name="revision"/> (a branch, tag or
    /// commit). Both safetensors and <c>pytorch_model.bin</c> work, including the legacy pre-torch-1.6 layout
    /// ProsusAI/finbert ships. Set <c>HF_TOKEN</c> for gated repositories.
    /// </summary>
    public static LocalFinBertSentiment Load(string repoId = DefaultModel, string revision = "main")
        => new(TransformerModel.Load(repoId, revision));

    /// <summary>Loads a checkpoint from a local folder (config.json, weights and tokenizer files).</summary>
    public static LocalFinBertSentiment Open(string directory) => new(TransformerModel.Open(directory));

    /// <summary>The Hub id the model came from.</summary>
    public string Model => _model.RepoId;

    /// <summary>The label names of the classification head, e.g. positive / negative / neutral.</summary>
    public IReadOnlyList<string> Labels => _model.Labels;

    /// <summary>All class probabilities for a text, best first.</summary>
    public IReadOnlyList<LabelScore> Classify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
            return _model.Predict(text, Labels.Count).Select(p => new LabelScore(p.Label, p.Score)).ToList();
    }

    /// <summary>Sentiment of one text; score = P(positive) − P(negative) in [-1, 1].</summary>
    public SentimentResult Analyze(string text) => FinBertSentiment.FromScores(Classify(text), "FinBERT (local)");

    /// <summary>Sentiment of several texts, in order.</summary>
    public IReadOnlyList<SentimentResult> AnalyzeBatch(IEnumerable<string> texts) => texts.Select(Analyze).ToList();

    /// <summary>Runs <see cref="Analyze"/> on the thread pool, so UI and request threads are not blocked.</summary>
    public Task<SentimentResult> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
        => Task.Run(() => Analyze(text), cancellationToken);

    public void Dispose() => _model.Dispose();
}
