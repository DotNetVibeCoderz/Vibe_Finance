using System.Globalization;
using System.Text;

namespace Finance.Visualization;

/// <summary>Colours and typography for charts and reports.</summary>
public sealed record ChartTheme(
    string Name,
    string Background,
    string Panel,
    string Grid,
    string Axis,
    string Text,
    string MutedText,
    string Up,
    string Down,
    string Accent,
    string[] Palette,
    string FontFamily = "Inter, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif")
{
    /// <summary>Dark theme (default): deep navy panels, teal/coral candles.</summary>
    public static ChartTheme Dark { get; } = new(
        "dark", "#0B1220", "#111A2E", "#1E2A44", "#3A4A6B", "#E6EDF7", "#8A9BB8",
        "#22C55E", "#EF4444", "#38BDF8",
        new[] { "#38BDF8", "#F59E0B", "#A78BFA", "#34D399", "#F472B6", "#FB923C", "#60A5FA", "#FACC15", "#2DD4BF", "#F87171" });

    /// <summary>Light theme.</summary>
    public static ChartTheme Light { get; } = new(
        "light", "#FFFFFF", "#F8FAFC", "#E2E8F0", "#94A3B8", "#0F172A", "#64748B",
        "#16A34A", "#DC2626", "#0284C7",
        new[] { "#0284C7", "#D97706", "#7C3AED", "#059669", "#DB2777", "#EA580C", "#2563EB", "#CA8A04", "#0D9488", "#B91C1C" });

    /// <summary>Palette colour for series <paramref name="index"/> (cycles).</summary>
    public string Series(int index) => Palette[((index % Palette.Length) + Palette.Length) % Palette.Length];
}

/// <summary>Common chart options.</summary>
public sealed record ChartOptions
{
    public int Width { get; init; } = 960;
    public int Height { get; init; } = 480;
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? XLabel { get; init; }
    public string? YLabel { get; init; }
    public ChartTheme Theme { get; init; } = ChartTheme.Dark;
    public bool ShowLegend { get; init; } = true;
    public bool ShowGrid { get; init; } = true;

    /// <summary>.NET format string for y-axis labels (e.g. "P0" for percent, "N2").</summary>
    public string YFormat { get; init; } = "G4";

    /// <summary>.NET format string for x-axis labels of numeric axes.</summary>
    public string XFormat { get; init; } = "G4";

    /// <summary>Forces the y-range to include zero.</summary>
    public bool IncludeZero { get; init; }
}

/// <summary>Low-level SVG building with invariant number formatting.</summary>
internal sealed class SvgBuilder
{
    private readonly StringBuilder _sb = new(16 * 1024);

    public SvgBuilder(int width, int height, ChartTheme theme)
    {
        Width = width;
        Height = height;
        Theme = theme;
        _sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"{Esc(theme.FontFamily)}\">");
        _sb.Append($"<rect width=\"{width}\" height=\"{height}\" rx=\"10\" fill=\"{theme.Background}\"/>");
    }

    public int Width { get; }
    public int Height { get; }
    public ChartTheme Theme { get; }

    public static string F(double v) => double.IsFinite(v) ? Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture) : "0";

    public static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public SvgBuilder Raw(string s) { _sb.Append(s); return this; }

    public SvgBuilder Rect(double x, double y, double w, double h, string fill, string? stroke = null, double rx = 0, double opacity = 1)
    {
        _sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(Math.Max(0, w))}\" height=\"{F(Math.Max(0, h))}\" fill=\"{fill}\"");
        if (stroke is not null) _sb.Append($" stroke=\"{stroke}\"");
        if (rx > 0) _sb.Append($" rx=\"{F(rx)}\"");
        if (opacity < 1) _sb.Append($" fill-opacity=\"{F(opacity)}\"");
        _sb.Append("/>");
        return this;
    }

    public SvgBuilder Line(double x1, double y1, double x2, double y2, string stroke, double width = 1, string? dash = null, double opacity = 1)
    {
        _sb.Append($"<line x1=\"{F(x1)}\" y1=\"{F(y1)}\" x2=\"{F(x2)}\" y2=\"{F(y2)}\" stroke=\"{stroke}\" stroke-width=\"{F(width)}\"");
        if (dash is not null) _sb.Append($" stroke-dasharray=\"{dash}\"");
        if (opacity < 1) _sb.Append($" stroke-opacity=\"{F(opacity)}\"");
        _sb.Append("/>");
        return this;
    }

    public SvgBuilder Path(string d, string stroke, double width = 1.6, string fill = "none", double fillOpacity = 1, string? dash = null)
    {
        _sb.Append($"<path d=\"{d}\" stroke=\"{stroke}\" stroke-width=\"{F(width)}\" fill=\"{fill}\" stroke-linejoin=\"round\" stroke-linecap=\"round\"");
        if (fillOpacity < 1) _sb.Append($" fill-opacity=\"{F(fillOpacity)}\"");
        if (dash is not null) _sb.Append($" stroke-dasharray=\"{dash}\"");
        _sb.Append("/>");
        return this;
    }

    public SvgBuilder Circle(double cx, double cy, double r, string fill, string? stroke = null, double opacity = 1)
    {
        _sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"{fill}\"");
        if (stroke is not null) _sb.Append($" stroke=\"{stroke}\" stroke-width=\"1.5\"");
        if (opacity < 1) _sb.Append($" fill-opacity=\"{F(opacity)}\"");
        _sb.Append("/>");
        return this;
    }

    public SvgBuilder Text(double x, double y, string text, string fill, double size = 12, string anchor = "start", string weight = "normal", double rotate = 0)
    {
        _sb.Append($"<text x=\"{F(x)}\" y=\"{F(y)}\" fill=\"{fill}\" font-size=\"{F(size)}\" text-anchor=\"{anchor}\" font-weight=\"{weight}\"");
        if (rotate != 0) _sb.Append($" transform=\"rotate({F(rotate)} {F(x)} {F(y)})\"");
        _sb.Append('>').Append(Esc(text)).Append("</text>");
        return this;
    }

    public SvgBuilder Polygon(IEnumerable<(double X, double Y)> points, string fill, double opacity = 1)
    {
        _sb.Append("<polygon points=\"");
        foreach (var (x, y) in points) _sb.Append(F(x)).Append(',').Append(F(y)).Append(' ');
        _sb.Append($"\" fill=\"{fill}\"");
        if (opacity < 1) _sb.Append($" fill-opacity=\"{F(opacity)}\"");
        _sb.Append("/>");
        return this;
    }

    public override string ToString() => _sb.ToString() + "</svg>";
}
