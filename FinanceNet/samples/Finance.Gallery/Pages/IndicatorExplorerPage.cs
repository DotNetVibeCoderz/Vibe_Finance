using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Finance.Core.Market;
using Finance.Gallery.Controls;
using Finance.Gallery.Shell;
using Finance.TimeSeries.Indicators;

namespace Finance.Gallery.Pages;

/// <summary>Browse every entry of <see cref="IndicatorCatalog"/>, tweak its parameters and see it on a chart.</summary>
public sealed class IndicatorExplorerPage : ModulePage
{
    public override string Title => "Indicator Explorer";
    public override string Subtitle => $"All {IndicatorCatalog.Count} entries of the Finance.TimeSeries catalogue — trend, momentum, volatility, volume, statistics, Ehlers cycles and candlestick patterns — computed live.";
    public override string Glyph => "∿";
    public override string Group => "Markets";

    private readonly Bar[] _bars = SyntheticMarket.GeometricBrownianBars(420, 120, 0.10, 0.30, seed: 314);
    private readonly CandleChart _chart = new() { Height = 560, VisibleBars = 150 };
    private readonly StackPanel _params = new() { Spacing = 12 };
    private readonly TextBlock _name = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Palette.Ink };
    private readonly TextBlock _desc = new() { FontSize = 12, Foreground = Palette.InkSoft, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _tags = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly ContentControl _latest = new();
    private ListBox _list = null!;
    private ComboBox _category = null!;
    private TextBox _search = null!;
    private IndicatorDefinition? _current;
    private readonly Dictionary<string, double> _values = new();

    protected override Control Build()
    {
        var categories = new[] { "All" }.Concat(Enum.GetNames<IndicatorCategory>()).ToArray();
        (var catRow, _category) = Ui.Combo("Category", categories);
        (var searchRow, _search) = Ui.Field("Search", "", "e.g. RSI, Ichimoku, engulfing");
        _list = new ListBox { Height = 430, Background = Brushes.Transparent };
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem { Tag: IndicatorDefinition d }) Select(d);
        };
        _category.SelectionChanged += (_, _) => Filter();
        _search.TextChanged += (_, _) => Filter();

        var counts = Enum.GetValues<IndicatorCategory>()
            .Select(c => Ui.Pill($"{c} {IndicatorCatalog.ByCategory(c).Count()}", Color(c))).ToArray();

        var left = Ui.Card(Ui.Stack(12, catRow, searchRow, _list), "Catalogue", $"{IndicatorCatalog.Count} indicators and patterns");
        var info = Ui.Card(Ui.Stack(10, _name, _tags, _desc, new Border { Height = 1, Background = Palette.Line, Margin = new Thickness(0, 4) }, _params, _latest), "Parameters");
        var pills = new WrapPanel();
        foreach (var c in counts) { c.Margin = new Thickness(0, 0, 6, 6); pills.Children.Add(c); }
        var center = Ui.Stack(8, pills, Ui.Card(_chart));

        Filter();
        Select(IndicatorCatalog.TryGet("BBANDS", out var bb) && bb is not null ? bb : IndicatorCatalog.All[0]);
        return Ui.Grid(16, new[] { "270", "*", "300" }, new Control[] { left, center, info });
    }

    private static Color Color(IndicatorCategory c) => c switch
    {
        IndicatorCategory.Trend => Palette.AccentColor,
        IndicatorCategory.Momentum => Palette.TealColor,
        IndicatorCategory.Volatility => Palette.GoldColor,
        IndicatorCategory.Volume => Palette.SkyColor,
        IndicatorCategory.Statistics => Palette.VioletColor,
        IndicatorCategory.PriceTransform => Palette.OrangeColor,
        IndicatorCategory.Cycle => Palette.UpColor,
        _ => Palette.PinkColor,
    };

    private void Filter()
    {
        string cat = _category.SelectedItem as string ?? "All";
        string q = (_search.Text ?? "").Trim();
        var items = IndicatorCatalog.All
            .Where(d => cat == "All" || d.Category.ToString() == cat)
            .Where(d => q.Length == 0 || d.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || d.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(d => new ListBoxItem
            {
                Tag = d,
                Padding = new Thickness(8, 6),
                Content = Ui.Stack(1,
                    new TextBlock { Text = d.Name, FontSize = 12.5, Foreground = Palette.Ink, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = $"{d.Id} · {d.Category}", FontSize = 10.5, Foreground = Palette.InkFaint }),
            }).ToList();
        _list.ItemsSource = items;
    }

    private void Select(IndicatorDefinition d)
    {
        _current = d;
        _values.Clear();
        _name.Text = d.Name;
        _desc.Text = d.Description;
        _tags.Children.Clear();
        _tags.Children.Add(Ui.Pill(d.Category.ToString(), Color(d.Category)));
        _tags.Children.Add(Ui.Pill(d.Overlay ? "Price overlay" : "Separate pane", Palette.InkSoftColor));
        _tags.Children.Add(Ui.Pill(d.Id, Palette.InkFaintColor));
        _params.Children.Clear();
        foreach (var p in d.Parameters)
        {
            _values[p.Name] = p.Default;
            var name = p.Name;
            (var row, var slider) = Ui.Slider(p.Name, p.Min, p.Max, p.Default, v => p.IsInteger ? v.ToString("0") : v.ToString("0.###"), p.IsInteger ? 1 : 0);
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
                _values[name] = slider.Value;
                Compute();
            };
            _params.Children.Add(row);
        }
        if (d.Parameters.Count == 0) _params.Children.Add(Ui.Label("No parameters.", 12, Palette.InkFaint));
        Compute();
    }

    private void Compute()
    {
        if (_current is null) return;
        IndicatorSeries[] output;
        try
        {
            output = _current.Compute(_bars, _values);
        }
        catch (Exception ex)
        {
            _latest.Content = Ui.Label(ex.Message, 12, Palette.Down);
            return;
        }

        _chart.Bars = _bars;
        _chart.Overlays.Clear();
        _chart.LowerPane.Clear();
        _chart.LowerPaneLevels.Clear();
        bool pattern = _current.Category == IndicatorCategory.Pattern;
        for (int i = 0; i < output.Length; i++)
        {
            var s = new LineSeries(output[i].Name, output[i].Values, Palette.SeriesColor(i + 1)) { Thickness = 1.8 };
            if (_current.Overlay && !pattern) _chart.Overlays.Add(s);
            else _chart.LowerPane.Add(s);
        }
        _chart.LowerPaneTitle = _current.Overlay ? "" : _current.Name;
        if (_current.Id.StartsWith("RSI", StringComparison.Ordinal) || _current.Id is "MFI" or "STOCH" or "STOCHRSI" or "WILLR")
        {
            _chart.LowerPaneLevels.Add((_current.Id == "WILLR" ? -20 : 70, Palette.DownColor));
            _chart.LowerPaneLevels.Add((_current.Id == "WILLR" ? -80 : 30, Palette.UpColor));
        }
        _chart.Refresh();

        var rows = output.Select(o =>
        {
            double last = o.Values.LastOrDefault(double.IsFinite);
            int hits = pattern ? o.Values.Count(v => v != 0 && double.IsFinite(v)) : 0;
            return (o.Name, pattern ? $"{hits} signals" : last.ToString(Math.Abs(last) < 10 ? "0.0000" : "0.00"));
        });
        _latest.Content = Ui.Stack(6, Ui.Label("Latest values", 12, Palette.InkFaint, FontWeight.SemiBold), Ui.Table(rows));
    }
}
