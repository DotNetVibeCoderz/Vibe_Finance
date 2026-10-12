using Finance.LLM.Rag;
using Gravicode.HFNet.GraviTransformers;

namespace Finance.LLM.Local;

/// <summary>
/// Sentence embeddings computed in-process with HF.Net (mean-pooled encoder states), for dense retrieval in
/// <see cref="FilingIndex"/>. The default checkpoint, sentence-transformers/all-MiniLM-L6-v2, is a 22M-parameter BERT
/// that is quick on a CPU; any BERT-family encoder on the Hub works.
/// </summary>
public sealed class LocalEmbeddingModel : IEmbeddingModel, IDisposable
{
    public const string DefaultModel = "sentence-transformers/all-MiniLM-L6-v2";
    private readonly TransformerModel _model;
    private readonly Lock _gate = new();

    private LocalEmbeddingModel(TransformerModel model) => _model = model;

    /// <summary>Downloads (first time only) and loads an encoder from the Hub.</summary>
    public static LocalEmbeddingModel Load(string repoId = DefaultModel, string revision = "main") => new(TransformerModel.Load(repoId, revision));

    /// <summary>Loads an encoder from a local folder.</summary>
    public static LocalEmbeddingModel Open(string directory) => new(TransformerModel.Open(directory));

    /// <summary>Truncation limit in tokens.</summary>
    public int MaxTokens { get; init; } = 256;

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            lock (_gate)
            {
                var m = _model.EmbedBatch(texts, MaxTokens);
                int dim = m.Shape[1];
                var data = m.ToArray();
                var result = new float[texts.Count][];
                for (int i = 0; i < texts.Count; i++)
                {
                    result[i] = new float[dim];
                    for (int j = 0; j < dim; j++) result[i][j] = (float)data[i * dim + j];
                }
                return result;
            }
        }, cancellationToken);

    public void Dispose() => _model.Dispose();
}

/// <summary>
/// Loads PDF reports (IDX annual reports, prospectuses, PDF 10-Ks) as <see cref="SourceDocument"/>s, one entry per page,
/// with Gravicode.OfficeNet.PdfNet, so citations can point at page numbers.
/// </summary>
public static class PdfReports
{
    public static SourceDocument Load(string path, string? id = null, string? title = null, string? password = null)
    {
        var pdf = PdfNet.Document.PdfDocument.Open(path, password!);
        var pages = new List<string>();
        foreach (var page in pdf.Pages) pages.Add(page.ExtractText());
        var name = Path.GetFileNameWithoutExtension(path);
        return new SourceDocument(id ?? name, title ?? name, pages);
    }

    public static SourceDocument Load(byte[] bytes, string id, string title, string? password = null)
    {
        var pdf = PdfNet.Document.PdfDocument.Open(bytes, password!);
        var pages = new List<string>();
        foreach (var page in pdf.Pages) pages.Add(page.ExtractText());
        return new SourceDocument(id, title, pages);
    }
}
