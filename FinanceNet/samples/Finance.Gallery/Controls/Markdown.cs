using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Finance.Gallery.Controls;

/// <summary>Minimal markdown for chat replies: headings, bullets, **bold** and `code`.</summary>
public static class Markdown
{
    public static Control Render(string text)
    {
        var stack = new StackPanel { Spacing = 4 };
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0) { stack.Children.Add(new Border { Height = 4 }); continue; }
            int indent = line.Length - line.TrimStart().Length;
            line = line.TrimStart();
            double size = 13;
            var weight = FontWeight.Normal;
            string prefix = "";
            if (line.StartsWith('#'))
            {
                int level = line.TakeWhile(c => c == '#').Count();
                line = line[level..].Trim();
                size = level <= 2 ? 15 : 14;
                weight = FontWeight.SemiBold;
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                prefix = "•  ";
                line = line[2..];
            }
            var tb = new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = size,
                FontWeight = weight,
                Foreground = Palette.Ink,
                Margin = new Thickness(indent * 6 + (prefix.Length > 0 ? 4 : 0), 0, 0, 0),
                Inlines = new InlineCollection(),
            };
            if (prefix.Length > 0) tb.Inlines.Add(new Run(prefix) { Foreground = Palette.Teal });
            foreach (var inline in Parse(line)) tb.Inlines.Add(inline);
            stack.Children.Add(tb);
        }
        return stack;
    }

    private static IEnumerable<Inline> Parse(string line)
    {
        int i = 0;
        while (i < line.Length)
        {
            int bold = line.IndexOf("**", i, StringComparison.Ordinal);
            int code = line.IndexOf('`', i);
            int next = bold < 0 ? code : code < 0 ? bold : Math.Min(bold, code);
            if (next < 0) { yield return new Run(line[i..]); yield break; }
            if (next > i) yield return new Run(line[i..next]);
            if (next == bold)
            {
                int end = line.IndexOf("**", bold + 2, StringComparison.Ordinal);
                if (end < 0) { yield return new Run(line[bold..]); yield break; }
                yield return new Run(line[(bold + 2)..end]) { FontWeight = FontWeight.SemiBold, Foreground = Brushes.White };
                i = end + 2;
            }
            else
            {
                int end = line.IndexOf('`', code + 1);
                if (end < 0) { yield return new Run(line[code..]); yield break; }
                yield return new Run(line[(code + 1)..end]) { FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"), Foreground = Palette.Gold };
                i = end + 1;
            }
        }
    }
}
