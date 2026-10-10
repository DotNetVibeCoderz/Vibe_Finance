using Avalonia.Media;

namespace Finance.Gallery;

/// <summary>The Gallery's palette: a dark trading-terminal look with indigo and teal accents.</summary>
public static class Palette
{
    public static readonly Color GroundColor = Color.Parse("#0B0F17");
    public static readonly Color PanelColor = Color.Parse("#111827");
    public static readonly Color RaisedColor = Color.Parse("#172033");
    public static readonly Color LineColor = Color.Parse("#243047");
    public static readonly Color InkColor = Color.Parse("#E6EDF7");
    public static readonly Color InkSoftColor = Color.Parse("#9AA8BD");
    public static readonly Color InkFaintColor = Color.Parse("#64748B");
    public static readonly Color AccentColor = Color.Parse("#6366F1");
    public static readonly Color TealColor = Color.Parse("#2DD4BF");
    public static readonly Color UpColor = Color.Parse("#22C55E");
    public static readonly Color DownColor = Color.Parse("#F43F5E");
    public static readonly Color GoldColor = Color.Parse("#FBBF24");
    public static readonly Color SkyColor = Color.Parse("#38BDF8");
    public static readonly Color PinkColor = Color.Parse("#F472B6");
    public static readonly Color OrangeColor = Color.Parse("#FB923C");
    public static readonly Color VioletColor = Color.Parse("#A78BFA");

    public static readonly IBrush Ground = new SolidColorBrush(GroundColor);
    public static readonly IBrush Panel = new SolidColorBrush(PanelColor);
    public static readonly IBrush Raised = new SolidColorBrush(RaisedColor);
    public static readonly IBrush Line = new SolidColorBrush(LineColor);
    public static readonly IBrush Ink = new SolidColorBrush(InkColor);
    public static readonly IBrush InkSoft = new SolidColorBrush(InkSoftColor);
    public static readonly IBrush InkFaint = new SolidColorBrush(InkFaintColor);
    public static readonly IBrush Accent = new SolidColorBrush(AccentColor);
    public static readonly IBrush Teal = new SolidColorBrush(TealColor);
    public static readonly IBrush Up = new SolidColorBrush(UpColor);
    public static readonly IBrush Down = new SolidColorBrush(DownColor);
    public static readonly IBrush Gold = new SolidColorBrush(GoldColor);

    /// <summary>Categorical series colours, in the order charts assign them.</summary>
    public static readonly Color[] Series =
    {
        AccentColor, TealColor, GoldColor, PinkColor, SkyColor, OrangeColor, VioletColor, UpColor, DownColor,
    };

    public static Color SeriesColor(int i) => Series[i % Series.Length];

    public static IBrush Brush(Color c, byte alpha = 255) => new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));

    /// <summary>Diverging colour for a value in [-1, 1] (correlation heatmaps): rose ← slate → indigo/teal.</summary>
    public static Color Diverging(double v)
    {
        v = Math.Clamp(v, -1, 1);
        var mid = Color.Parse("#1E293B");
        var hi = Color.Parse("#2DD4BF");
        var lo = Color.Parse("#F43F5E");
        var target = v >= 0 ? hi : lo;
        double t = Math.Abs(v);
        return Color.FromRgb(
            (byte)(mid.R + (target.R - mid.R) * t),
            (byte)(mid.G + (target.G - mid.G) * t),
            (byte)(mid.B + (target.B - mid.B) * t));
    }
}
