using Finance.LLM.Rag;
using Gravicode.HFNet.GraviTransformers.Vision;
using PdfNet.Document;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Finance.LLM.Ocr;

/// <summary>One recognised line of a page.</summary>
public sealed record OcrLine(int Line, string Text, IReadOnlyList<TextRegion> Regions);

/// <summary>
/// OCR for scanned pages: <see cref="TextLineSegmenter"/> finds the text lines, and TrOCR (HF.Net
/// <c>GraviTransformers.Vision</c>, in-process, no API) reads each one. The default checkpoint is
/// <c>microsoft/trocr-base-printed</c>; <c>microsoft/trocr-small-printed</c> is faster but misreads more. Both printed
/// checkpoints were fine-tuned on receipts (SROIE) and answer in UPPER CASE.
/// </summary>
public sealed class ScannedPageOcr : IDisposable
{
    /// <summary>Default checkpoint.</summary>
    public const string DefaultModel = "microsoft/trocr-base-printed";

    /// <summary>
    /// Decoders used for page images: PNG, JPEG, BMP, GIF and WebP with metadata skipped. ImageSharp 3.1.12's open
    /// advisories are in its TIFF decoder, ICC profile parser and TIFF encoders; none of them is on this path.
    /// </summary>
    internal static readonly DecoderOptions Decoding = new()
    {
        Configuration = new Configuration(
            new PngConfigurationModule(),
            new JpegConfigurationModule(),
            new BmpConfigurationModule(),
            new GifConfigurationModule(),
            new WebpConfigurationModule()),
        SkipMetadata = true,
    };

    private readonly TrOcrModel _model;

    private ScannedPageOcr(TrOcrModel model) => _model = model;

    /// <summary>Downloads (once, into the Hugging Face cache) and loads a TrOCR checkpoint.</summary>
    public static ScannedPageOcr Load(string repoId = DefaultModel, string revision = "main") => new(TrOcrModel.Load(repoId, revision));

    /// <summary>Loads a TrOCR checkpoint from a local folder.</summary>
    public static ScannedPageOcr Open(string directory) => new(TrOcrModel.Open(directory));

    /// <summary>Line finder used before recognition.</summary>
    public TextLineSegmenter Segmenter { get; init; } = new();

    /// <summary>Generation settings (beam search etc.); null uses the checkpoint's own.</summary>
    public OcrSettings? Settings { get; init; }

    /// <summary>Reads an encoded page image (PNG, JPEG, BMP, GIF, WebP).</summary>
    public IReadOnlyList<OcrLine> ReadPage(byte[] encodedImage)
    {
        ArgumentNullException.ThrowIfNull(encodedImage);
        using var image = Image.Load<Rgb24>(Decoding, encodedImage);
        return ReadPage(image);
    }

    /// <summary>Reads a decoded page image.</summary>
    public IReadOnlyList<OcrLine> ReadPage(Image<Rgb24> page)
    {
        var regions = Segmenter.Find(page);
        var pixels = regions.Select(r =>
        {
            using var crop = page.Clone(ctx => ctx.Crop(r.Bounds));
            return _model.Processor.Convert(crop);
        }).ToList();
        var texts = pixels.Count == 0 ? Array.Empty<string>() : _model.RecognizeBatch(pixels, Settings);
        return regions.Zip(texts, (r, t) => (r, t))
            .GroupBy(x => x.r.Line)
            .Select(g => new OcrLine(g.Key, string.Join("  ", g.Select(x => x.t.Trim()).Where(t => t.Length > 0)), g.Select(x => x.r).ToList()))
            .ToList();
    }

    /// <summary>Plain text of a page, one line per row.</summary>
    public string ReadPageText(byte[] encodedImage) => string.Join("\n", ReadPage(encodedImage).Select(l => l.Text));

    public void Dispose() => _model.Dispose();
}

/// <summary>
/// Loads PDF reports for <see cref="FilingIndex"/>, OCR-ing scanned pages: a page whose text layer has fewer than
/// <c>minTextCharacters</c> characters is read from its embedded images instead (largest first is not assumed:
/// images are read in content order). Pages that already have text are taken as they are.
/// </summary>
public static class OcrPdfReports
{
    /// <summary>Loads a PDF from disk; one <see cref="SourceDocument"/> page per PDF page.</summary>
    public static SourceDocument Load(string path, ScannedPageOcr ocr, string? id = null, string? title = null, string? password = null,
        int minTextCharacters = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Load(File.ReadAllBytes(path), id ?? Path.GetFileNameWithoutExtension(path), title ?? Path.GetFileNameWithoutExtension(path), ocr, password,
            minTextCharacters);
    }

    /// <summary>Loads a PDF from bytes; one <see cref="SourceDocument"/> page per PDF page.</summary>
    public static SourceDocument Load(byte[] bytes, string id, string title, ScannedPageOcr ocr, string? password = null, int minTextCharacters = 20)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        using var doc = PdfDocument.Open(bytes, password ?? "");
        var pages = new List<string>();
        foreach (var page in doc.Pages)
        {
            string text = page.ExtractText() ?? "";
            if (text.Count(c => !char.IsWhiteSpace(c)) >= minTextCharacters)
            {
                pages.Add(text);
                continue;
            }
            var ocrText = new List<string>();
            foreach (var image in page.ExtractImages())
            {
                if (!IsDecodable(image.Extension)) continue;
                ocrText.Add(ocr.ReadPageText(image.Data));
            }
            pages.Add(ocrText.Count > 0 ? string.Join("\n", ocrText) : text);
        }
        return new SourceDocument(id, title, pages);
    }

    private static bool IsDecodable(string? extension) => (extension ?? "").TrimStart('.').ToLowerInvariant() is "png" or "jpg" or "jpeg" or "bmp" or "gif" or "webp";
}
