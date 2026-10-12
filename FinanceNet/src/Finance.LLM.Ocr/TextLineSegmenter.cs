using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Finance.LLM.Ocr;

/// <summary>A text region on a page image, in pixels, with its reading-order position.</summary>
/// <param name="Bounds">Crop rectangle (padding included).</param>
/// <param name="Line">Zero-based text line (top to bottom).</param>
/// <param name="Segment">Zero-based segment within the line (left to right).</param>
public sealed record TextRegion(Rectangle Bounds, int Line, int Segment);

/// <summary>
/// Finds printed text lines on a scanned page with projection profiles: Otsu threshold, rows with ink grouped into
/// line bands, each band split into segments at gaps wider than <see cref="SegmentGapFactor"/> × line height (column
/// gutters, tab stops), so that each crop is one line of text as TrOCR expects. Works for clean, deskewed printed
/// pages; skewed scans, tables with ruling lines and handwriting are out of scope.
/// </summary>
public sealed class TextLineSegmenter
{
    /// <summary>A row is ink-bearing when at least this fraction of its pixels is dark.</summary>
    public double MinRowInk { get; init; } = 0.002;

    /// <summary>Bands thinner than this (pixels) are noise.</summary>
    public int MinLineHeight { get; init; } = 6;

    /// <summary>Blank rows tolerated inside one line band (descenders, dots of i/j).</summary>
    public int MaxRowGap { get; init; } = 1;

    /// <summary>A horizontal gap wider than this × the band height splits the line into segments.</summary>
    public double SegmentGapFactor { get; init; } = 2.0;

    /// <summary>Padding added around each region, as a fraction of its height.</summary>
    public double Padding { get; init; } = 0.25;

    /// <summary>Regions in reading order (line by line, left to right).</summary>
    public IReadOnlyList<TextRegion> Find(Image<Rgb24> page)
    {
        ArgumentNullException.ThrowIfNull(page);
        int w = page.Width, h = page.Height;
        var gray = new byte[w * h];
        page.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                    gray[y * w + x] = (byte)((row[x].R * 299 + row[x].G * 587 + row[x].B * 114 + 500) / 1000);
            }
        });
        int threshold = Otsu(gray);
        var ink = new bool[w * h];
        for (int i = 0; i < ink.Length; i++) ink[i] = gray[i] <= threshold;

        var regions = new List<TextRegion>();
        int line = 0;
        foreach (var (top, bottom) in Bands(ink, w, h))
        {
            int bandHeight = bottom - top + 1;
            int segment = 0;
            foreach (var (left, right) in Segments(ink, w, top, bottom, bandHeight))
            {
                int pad = (int)Math.Ceiling(Padding * bandHeight);
                int x0 = Math.Max(0, left - pad), y0 = Math.Max(0, top - pad);
                int x1 = Math.Min(w - 1, right + pad), y1 = Math.Min(h - 1, bottom + pad);
                regions.Add(new TextRegion(new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1), line, segment++));
            }
            if (segment > 0) line++;
        }
        return regions;
    }

    private IEnumerable<(int Top, int Bottom)> Bands(bool[] ink, int w, int h)
    {
        int minInk = Math.Max(1, (int)Math.Ceiling(MinRowInk * w));
        int start = -1, lastInk = -1;
        for (int y = 0; y <= h; y++)
        {
            bool hasInk = false;
            if (y < h)
            {
                int count = 0;
                for (int x = 0; x < w && count < minInk; x++) if (ink[y * w + x]) count++;
                hasInk = count >= minInk;
            }
            if (hasInk)
            {
                if (start < 0) start = y;
                lastInk = y;
            }
            else if (start >= 0 && y - lastInk > MaxRowGap)
            {
                if (lastInk - start + 1 >= MinLineHeight) yield return (start, lastInk);
                start = -1;
            }
        }
    }

    private IEnumerable<(int Left, int Right)> Segments(bool[] ink, int w, int top, int bottom, int bandHeight)
    {
        int maxGap = Math.Max(1, (int)Math.Round(SegmentGapFactor * bandHeight));
        int start = -1, lastInk = -1;
        for (int x = 0; x <= w; x++)
        {
            bool hasInk = false;
            if (x < w)
                for (int y = top; y <= bottom && !hasInk; y++) hasInk = ink[y * w + x];
            if (hasInk)
            {
                if (start < 0) start = x;
                lastInk = x;
            }
            else if (start >= 0 && (x == w || x - lastInk > maxGap))
            {
                yield return (start, lastInk);
                start = -1;
            }
        }
    }

    /// <summary>Otsu's threshold on an 8-bit grey image (pixels ≤ threshold are ink).</summary>
    internal static int Otsu(byte[] gray)
    {
        var hist = new long[256];
        foreach (var g in gray) hist[g]++;
        long total = gray.Length;
        double sumAll = 0;
        for (int i = 0; i < 256; i++) sumAll += i * (double)hist[i];
        double sumB = 0, best = -1;
        long wB = 0;
        int threshold = 127;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            long wF = total - wB;
            if (wF == 0) break;
            sumB += t * (double)hist[t];
            double mB = sumB / wB, mF = (sumAll - sumB) / wF;
            double between = wB * (double)wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; threshold = t; }
        }
        return threshold;
    }
}
