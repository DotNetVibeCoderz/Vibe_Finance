using Avalonia;
using Avalonia.Media;
using Finance.Core.Market;

namespace Finance.Gallery.Controls;

/// <summary>Price ladder: asks above, bids below, with depth bars and cumulative size.</summary>
public sealed class OrderBookLadder : ChartBase
{
    public OrderBook? Book { get; set; }
    public int Levels { get; set; } = 14;
    public Func<double, string> PriceFormat { get; set; } = v => v.ToString("0.00");

    public OrderBookLadder() => PlotPadding = new Thickness(10, 26, 10, 8);

    protected override void RenderChart(DrawingContext ctx, Rect bounds)
    {
        if (Book is null) return;
        var r = PlotArea(bounds);
        int n = Math.Min(Levels, Math.Min(Book.Asks.Count, Book.Bids.Count));
        if (n == 0) return;
        double rowH = Math.Min(22, (r.Height - 30) / (2 * n));
        double maxSize = 0;
        for (int i = 0; i < n; i++) maxSize = Math.Max(maxSize, Math.Max(Book.Asks[i].Size, Book.Bids[i].Size));

        double cPrice = r.Left + r.Width * 0.30, cSize = r.Left + r.Width * 0.62, cTotal = r.Right - 6;
        DrawText(ctx, "PRICE", new Point(cPrice, r.Top - 18), 10, Palette.InkFaint, FontWeight.SemiBold, TextAnchor.Right);
        DrawText(ctx, "SIZE", new Point(cSize, r.Top - 18), 10, Palette.InkFaint, FontWeight.SemiBold, TextAnchor.Right);
        DrawText(ctx, "TOTAL", new Point(cTotal, r.Top - 18), 10, Palette.InkFaint, FontWeight.SemiBold, TextAnchor.Right);

        double cum = 0;
        var askCum = new double[n];
        for (int i = 0; i < n; i++) { cum += Book.Asks[i].Size; askCum[i] = cum; }
        for (int i = n - 1; i >= 0; i--)
        {
            double y = r.Top + (n - 1 - i) * rowH;
            Row(ctx, r, y, rowH, Book.Asks[i], askCum[i], maxSize, Palette.DownColor, cPrice, cSize, cTotal);
        }
        double midY = r.Top + n * rowH;
        ctx.DrawRectangle(Palette.Raised, null, new Rect(r.Left, midY + 2, r.Width, 26), 6, 6);
        DrawText(ctx, PriceFormat(Book.Mid), new Point(r.Left + 10, midY + 7), 13, Palette.Ink, FontWeight.Bold);
        DrawText(ctx, $"spread {PriceFormat(Book.Spread)}  ·  imbalance {Book.Imbalance(5):+0.00;-0.00}", new Point(r.Right - 8, midY + 9), 10.5, Palette.InkSoft, anchor: TextAnchor.Right);
        cum = 0;
        for (int i = 0; i < n; i++)
        {
            cum += Book.Bids[i].Size;
            double y = midY + 30 + i * rowH;
            Row(ctx, r, y, rowH, Book.Bids[i], cum, maxSize, Palette.UpColor, cPrice, cSize, cTotal);
        }
    }

    private void Row(DrawingContext ctx, Rect r, double y, double h, BookLevel level, double total, double maxSize, Color color, double cPrice, double cSize, double cTotal)
    {
        double w = level.Size / maxSize * r.Width;
        ctx.DrawRectangle(Palette.Brush(color, 38), null, new Rect(r.Right - w, y + 1, w, h - 2), 2, 2);
        DrawText(ctx, PriceFormat(level.Price), new Point(cPrice, y + h / 2 - 7), 11.5, Palette.Brush(color), FontWeight.SemiBold, TextAnchor.Right);
        DrawText(ctx, Compact(level.Size), new Point(cSize, y + h / 2 - 7), 11.5, Palette.Ink, anchor: TextAnchor.Right);
        DrawText(ctx, Compact(total), new Point(cTotal, y + h / 2 - 7), 11.5, Palette.InkSoft, anchor: TextAnchor.Right);
    }
}
