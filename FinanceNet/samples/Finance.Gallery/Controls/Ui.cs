using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Finance.Gallery.Controls;

/// <summary>Small factory for the Gallery's recurring building blocks (cards, stat tiles, form rows).</summary>
public static class Ui
{
    public static Border Card(Control content, string? title = null, string? subtitle = null, Control? headerRight = null, double padding = 16)
    {
        var stack = new DockPanel { LastChildFill = true };
        if (title is not null)
        {
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            if (headerRight is not null)
            {
                DockPanel.SetDock(headerRight, Dock.Right);
                head.Children.Add(headerRight);
            }
            var titles = new StackPanel { Spacing = 2 };
            titles.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Ink });
            if (subtitle is not null)
                titles.Children.Add(new TextBlock { Text = subtitle, FontSize = 11.5, Foreground = Palette.InkFaint, TextWrapping = TextWrapping.Wrap });
            head.Children.Add(titles);
            DockPanel.SetDock(head, Dock.Top);
            stack.Children.Add(head);
        }
        stack.Children.Add(content);
        return new Border
        {
            Background = Palette.Panel,
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(padding),
            Child = stack,
        };
    }

    /// <summary>A KPI tile: caption, big value, optional delta line and sparkline.</summary>
    public static Border Stat(string caption, string value, string? delta = null, bool? positive = null, double[]? spark = null)
    {
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(new TextBlock { Text = caption.ToUpperInvariant(), FontSize = 10.5, LetterSpacing = 0.8, Foreground = Palette.InkFaint, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(new TextBlock { Text = value, FontSize = 21, FontWeight = FontWeight.SemiBold, Foreground = Palette.Ink });
        if (delta is not null)
            stack.Children.Add(new TextBlock
            {
                Text = delta,
                FontSize = 11.5,
                Foreground = positive switch { true => Palette.Up, false => Palette.Down, _ => Palette.InkSoft },
            });
        if (spark is not null) stack.Children.Add(new Sparkline { Values = spark, Height = 30, Margin = new Thickness(0, 4, 0, 0) });
        return new Border
        {
            Background = Palette.Panel,
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12),
            Child = stack,
        };
    }

    /// <summary>Updates the value/delta of a tile created by <see cref="Stat"/>.</summary>
    public static void SetStat(Border tile, string value, string? delta = null, bool? positive = null)
    {
        if (tile.Child is not StackPanel s) return;
        ((TextBlock)s.Children[1]).Text = value;
        if (delta is not null && s.Children.Count > 2 && s.Children[2] is TextBlock d)
        {
            d.Text = delta;
            d.Foreground = positive switch { true => Palette.Up, false => Palette.Down, _ => Palette.InkSoft };
        }
    }

    public static Grid Columns(double spacing, params Control[] children) => Grid(spacing, children.Select(_ => "*").ToArray(), children);

    /// <summary>Lays out children in columns with explicit widths ("*", "2*", "320").</summary>
    public static Grid Grid(double spacing, string[] widths, Control[] children)
    {
        var g = new Grid();
        for (int i = 0; i < children.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Parse(widths[i])));
            if (i < children.Length - 1) g.ColumnDefinitions.Add(new ColumnDefinition(spacing, GridUnitType.Pixel));
            Avalonia.Controls.Grid.SetColumn(children[i], i * 2);
            g.Children.Add(children[i]);
        }
        return g;
    }

    public static StackPanel Stack(double spacing, params Control[] children)
    {
        var s = new StackPanel { Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var s = new StackPanel { Spacing = spacing, Orientation = Orientation.Horizontal };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static TextBlock Label(string text, double size = 12, IBrush? brush = null, FontWeight weight = FontWeight.Normal)
        => new() { Text = text, FontSize = size, Foreground = brush ?? Palette.InkSoft, FontWeight = weight, TextWrapping = TextWrapping.Wrap };

    /// <summary>A labelled slider whose value label updates live.</summary>
    public static (Control Row, Slider Slider) Slider(string caption, double min, double max, double value, Func<double, string> format, double step = 0)
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, MinWidth = 150 };
        if (step > 0) { slider.TickFrequency = step; slider.IsSnapToTickEnabled = true; }
        var valueLabel = new TextBlock { Text = format(value), Foreground = Palette.Ink, FontWeight = FontWeight.SemiBold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) valueLabel.Text = format(slider.Value);
        };
        var head = new DockPanel();
        DockPanel.SetDock(valueLabel, Dock.Right);
        head.Children.Add(valueLabel);
        head.Children.Add(new TextBlock { Text = caption, Foreground = Palette.InkSoft, FontSize = 12 });
        return (Stack(0, head, slider), slider);
    }

    public static (Control Row, ComboBox Box) Combo(string caption, IEnumerable<string> items, int selected = 0)
    {
        var box = new ComboBox { ItemsSource = items.ToList(), SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
        return (Stack(4, Label(caption), box), box);
    }

    public static (Control Row, TextBox Box) Field(string caption, string text, string? watermark = null)
    {
        var box = new TextBox { Text = text, PlaceholderText = watermark };
        return (Stack(4, Label(caption), box), box);
    }

    public static Button Button(string text, Action onClick, bool primary = true)
    {
        var b = new Button { Content = text };
        b.Classes.Add(primary ? "primary" : "ghost");
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>A pill-shaped tag.</summary>
    public static Border Pill(string text, Color color) => new()
    {
        Background = Palette.Brush(color, 40),
        BorderBrush = Palette.Brush(color, 120),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(8, 2),
        Child = new TextBlock { Text = text, FontSize = 10.5, Foreground = Palette.Brush(color), FontWeight = FontWeight.SemiBold },
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A simple key/value table.</summary>
    public static Grid Table(IEnumerable<(string Key, string Value)> rows, IBrush? valueBrush = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        int r = 0;
        foreach (var (k, v) in rows)
        {
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var kt = new TextBlock { Text = k, Foreground = Palette.InkSoft, FontSize = 12, Margin = new Thickness(0, 4) };
            var vt = new TextBlock { Text = v, Foreground = valueBrush ?? Palette.Ink, FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(12, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Right };
            Avalonia.Controls.Grid.SetRow(kt, r);
            Avalonia.Controls.Grid.SetRow(vt, r);
            Avalonia.Controls.Grid.SetColumn(vt, 1);
            g.Children.Add(kt);
            g.Children.Add(vt);
            r++;
        }
        return g;
    }

    /// <summary>A grid table with a header row (used for trades, order books, holdings).</summary>
    public static Grid DataTable(string[] headers, IEnumerable<string[]> rows, Func<int, int, IBrush?>? cellBrush = null, int maxRows = 200)
    {
        var g = new Grid();
        for (int c = 0; c < headers.Length; c++) g.ColumnDefinitions.Add(new ColumnDefinition(c == 0 ? 2.2 : 1, GridUnitType.Star));
        g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < headers.Length; c++)
        {
            var t = new TextBlock { Text = headers[c], FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.InkFaint, Margin = new Thickness(4, 0, 4, 6), TextAlignment = c == 0 ? TextAlignment.Left : TextAlignment.Right };
            Avalonia.Controls.Grid.SetColumn(t, c);
            g.Children.Add(t);
        }
        int r = 1;
        foreach (var row in rows.Take(maxRows))
        {
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < row.Length && c < headers.Length; c++)
            {
                var t = new TextBlock
                {
                    Text = row[c],
                    FontSize = 11.5,
                    Foreground = cellBrush?.Invoke(r - 1, c) ?? (c == 0 ? Palette.Ink : Palette.InkSoft),
                    Margin = new Thickness(4, 3),
                    TextAlignment = c == 0 ? TextAlignment.Left : TextAlignment.Right,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Avalonia.Controls.Grid.SetRow(t, r);
                Avalonia.Controls.Grid.SetColumn(t, c);
                g.Children.Add(t);
            }
            r++;
        }
        return g;
    }
}
