using Finance.LLM.Ocr;
using Finance.LLM.Rag;
using PdfNet.Content;
using PdfNet.Document;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Finance.AI.Tests;

/// <summary>
/// OCR of scanned pages. The fixtures are three printed lines rendered by PIL (the same images HF.Net checks TrOCR
/// against transformers with); pages are composed from them so every line's true position and text are known.
/// </summary>
public class OcrTests
{
    internal static readonly string[] Texts = { "Net revenue rose 12% to $4.3 billion", "Laba bersih Rp 1.234 miliar", "Operating margin: 18.5% (Q3 2025)" };

    internal static Image<Rgb24> Line(int i) => Image.Load<Rgb24>(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ocr", $"line{i}.png"));

    /// <summary>An A4-ish page (1240×1754 at 150 dpi) with the three lines at known places.</summary>
    internal static (Image<Rgb24> Page, Rectangle[] Truth) Page(int gap = 60)
    {
        var page = new Image<Rgb24>(1240, 1754, Color.White);
        var truth = new Rectangle[3];
        int y = 150;
        for (int i = 0; i < 3; i++)
        {
            using var line = Line(i);
            var at = new Point(120, y);
            page.Mutate(ctx => ctx.DrawImage(line, at, 1f));
            truth[i] = new Rectangle(at.X, at.Y, line.Width, line.Height);
            y += line.Height + gap;
        }
        return (page, truth);
    }

    [Fact]
    public void SegmenterFindsEachPrintedLineInOrder()
    {
        var (page, truth) = Page();
        using (page)
        {
            var regions = new TextLineSegmenter().Find(page);
            Assert.Equal(3, regions.Count);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(i, regions[i].Line);
                // Each crop contains its line's ink and does not reach into the next line.
                Assert.True(regions[i].Bounds.Top <= truth[i].Bottom && regions[i].Bounds.Bottom >= truth[i].Top);
                if (i < 2) Assert.True(regions[i].Bounds.Bottom < truth[i + 1].Top + truth[i + 1].Height / 2);
            }
        }
    }

    [Fact]
    public void WideGapsSplitALineIntoSegments()
    {
        // Two columns: line 0 at the left and line 2 at the right on the same row, separated by a gutter.
        using var page = new Image<Rgb24>(1300, 200, Color.White);
        using var a = Line(0);
        using var b = Line(2);
        page.Mutate(ctx => ctx.DrawImage(a, new Point(20, 60), 1f).DrawImage(b, new Point(20 + a.Width + 160, 60), 1f));
        var regions = new TextLineSegmenter().Find(page);
        Assert.Equal(2, regions.Count);
        Assert.All(regions, r => Assert.Equal(0, r.Line));
        Assert.True(regions[0].Bounds.Right < regions[1].Bounds.Left);
        // Word gaps (a fraction of the line height) must not split: one region for a single line.
        using var single = new Image<Rgb24>(700, 120, Color.White);
        single.Mutate(ctx => ctx.DrawImage(a, new Point(30, 30), 1f));
        Assert.Single(new TextLineSegmenter().Find(single));
    }

    [Fact]
    public void OtsuSeparatesInkFromPaper()
    {
        var gray = new byte[1000];
        for (int i = 0; i < gray.Length; i++) gray[i] = (byte)(i % 10 == 0 ? 30 + i % 7 : 230 + i % 9);
        int t = TextLineSegmenter.Otsu(gray);
        Assert.InRange(t, 36, 229);
    }

    /// <summary>
    /// Downloads microsoft/trocr-base-printed (~1.3 GB) once; enable with FINANCENET_FINBERT_LOCAL=1 (local HF models).
    /// A scanned page reads back as the three lines, and a PDF mixing a scanned page with a text page loads both.
    /// </summary>
    [Fact]
    public async Task ScannedPdfPagesAreReadAndIndexed()
    {
        if (Environment.GetEnvironmentVariable("FINANCENET_FINBERT_LOCAL") != "1") Assert.Skip("FINANCENET_FINBERT_LOCAL=1 not set.");
        using var ocr = ScannedPageOcr.Load();
        var (page, _) = Page();
        byte[] png;
        using (page)
        using (var ms = new MemoryStream())
        {
            page.SaveAsPng(ms);
            png = ms.ToArray();
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lines = ocr.ReadPage(png);
        TestContext.Current.SendDiagnosticMessage($"OCR of 3 lines: {sw.ElapsedMilliseconds} ms");
        // The printed checkpoints answer in upper case (fine-tuned on receipts), so compare ignoring case.
        Assert.Equal(Texts.Select(t => t.ToUpperInvariant()), lines.Select(l => l.Text.ToUpperInvariant()));

        // A two-page PDF: page 1 is the scanned image only, page 2 has a text layer.
        using var doc = PdfDocument.Create();
        var scanned = doc.Pages.Add();
        using (var canvas = scanned.OpenCanvas()) canvas.DrawImage(png, 0, 0, 595, 842);
        var typed = doc.Pages.Add();
        using (var canvas = typed.OpenCanvas())
        {
            canvas.SetFont(StandardFont.Helvetica, 12);
            canvas.DrawText("Item 7. Management discussion: operating cash flow increased to $1.1 billion.", 50, 750);
        }
        using var pdf = new MemoryStream();
        doc.Save(pdf);

        var report = OcrPdfReports.Load(pdf.ToArray(), "scan", "Scanned report", ocr);
        Assert.Equal(2, report.Pages.Count);
        Assert.Contains("LABA BERSIH RP 1.234 MILIAR", report.Pages[0].ToUpperInvariant());
        Assert.Contains("operating cash flow", report.Pages[1]);

        var index = new FilingIndex();
        await index.AddAsync(report, TestContext.Current.CancellationToken);
        var hit = (await index.SearchAsync("laba bersih", 1, TestContext.Current.CancellationToken))[0];
        Assert.Equal(1, hit.Chunk.Page);
    }
}
