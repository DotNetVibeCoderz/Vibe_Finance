using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Finance.Backtesting;
using Finance.Backtesting.Strategies;
using Finance.Core.Market;
using Finance.Gallery.Controls;
using Finance.Gallery.Shell;
using Finance.MarketData.Providers;
using Finance.TimeSeries.Indicators;

namespace Finance.Gallery.Pages;

/// <summary>
/// No-code rule builder: entry/exit conditions on indicators, stops and sizing. Every change re-runs the
/// event-driven backtest and regenerates the equivalent C# <see cref="Strategy"/> class.
/// </summary>
public sealed class StrategyBuilderPage : ModulePage
{
    public override string Title => "Strategy Builder";
    public override string Subtitle => "Compose entry and exit rules from indicators, add stop-loss, take-profit and position sizing — the strategy is backtested on every change and exported as C# code for Finance.Backtesting.";
    public override string Glyph => "⚙";
    public override string Group => "Trading";

    internal static readonly string[] Operands = { "Close", "SMA", "EMA", "RSI", "Bollinger upper", "Bollinger lower", "Value" };
    internal static readonly string[] Comparisons = { "crosses above", "crosses below", "is above", "is below" };

    private RuleEditor _entry = null!, _exit = null!;
    private Slider _stop = null!, _take = null!, _size = null!;
    private ComboBox _symbol = null!;
    private readonly CandleChart _chart = new() { Height = 380, VisibleBars = 200, ShowVolume = false };
    private readonly LineChart _equity = new() { Height = 220, Title = "Equity vs buy & hold" };
    private readonly SelectableTextBlock _code = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"), FontSize = 11.5, Foreground = Palette.Ink, TextWrapping = TextWrapping.NoWrap };
    private readonly ContentControl _stats = new();
    private Bar[] _bars = Array.Empty<Bar>();

    protected override Control Build()
    {
        (var symRow, _symbol) = Ui.Combo("Symbol (synthetic daily)", new[] { "AAPL", "MSFT", "NVDA", "TSLA", "SPY" });
        _entry = new RuleEditor("Enter long when", 1, 20, 2, 50, 0, Changed);
        _exit = new RuleEditor("Exit when", 1, 20, 2, 50, 1, Changed);
        (var stopRow, _stop) = Ui.Slider("Stop-loss", 0, 0.25, 0.08, v => v == 0 ? "off" : ChartBase.Percent(v));
        (var takeRow, _take) = Ui.Slider("Take-profit", 0, 0.6, 0.25, v => v == 0 ? "off" : ChartBase.Percent(v));
        (var sizeRow, _size) = Ui.Slider("Position size", 0.1, 1, 0.95, ChartBase.Percent);
        foreach (var s in new[] { _stop, _take, _size })
            s.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) Changed(); };
        _symbol.SelectionChanged += (_, _) => { Load(); Changed(); };

        var rules = Ui.Card(Ui.Stack(16, symRow, _entry.View, _exit.View, Ui.Stack(12, stopRow, takeRow, sizeRow)), "Rules", "Golden-cross style defaults");
        var right = Ui.Stack(14,
            _stats,
            Ui.Card(_chart),
            Ui.Grid(14, new[] { "*", "*" }, new Control[]
            {
                Ui.Card(_equity),
                Ui.Card(new ScrollViewer { Content = _code, Height = 220, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }, "Generated C#", "Paste into your project — runs on Finance.Backtesting"),
            }));
        Load();
        Changed();
        return Ui.Grid(18, new[] { "330", "*" }, new Control[] { rules, right });
    }

    private void Load()
    {
        string sym = _symbol.SelectedItem as string ?? "AAPL";
        _bars = new SyntheticProvider().GetBarsAsync(sym, BarInterval.Day1, new DateTime(2020, 1, 1), new DateTime(2025, 6, 30)).GetAwaiter().GetResult();
    }

    private void Changed()
    {
        if (_size is null || _bars.Length == 0) return;
        string sym = _symbol.SelectedItem as string ?? "AAPL";
        var entry = _entry.Rule;
        var exit = _exit.Rule;
        var strategy = new RuleStrategy(sym, _bars, entry, exit, _stop.Value, _take.Value, _size.Value);
        var settings = new BacktestSettings { InitialCash = 100_000, Commission = new PercentageCommission(0.0005), Slippage = new FixedBpsSlippage(2) };
        var result = new BacktestEngine(settings).Run(strategy, sym, _bars);
        var bench = new BacktestEngine(settings).Run(new BuyAndHoldStrategy(sym), sym, _bars);

        _chart.Bars = _bars;
        _chart.Title = $"{sym} — signals";
        _chart.Overlays.Clear();
        _chart.LowerPane.Clear();
        _chart.LowerPaneLevels.Clear();
        foreach (var (rule, idx) in new[] { (entry, 0), (exit, 1) })
            foreach (var (op, period) in new[] { (rule.Left, rule.LeftPeriod), (rule.Right, rule.RightPeriod) })
            {
                if (op is "Close" or "Value") continue;
                var series = new LineSeries($"{op}({period})", RuleStrategy.Series(_bars, op, period), Palette.SeriesColor(_chart.Overlays.Count + _chart.LowerPane.Count + 2)) { Thickness = 1.6 };
                if (op == "RSI")
                {
                    if (_chart.LowerPane.All(s => s.Name != series.Name)) _chart.LowerPane.Add(series);
                }
                else if (_chart.Overlays.All(s => s.Name != series.Name)) _chart.Overlays.Add(series);
            }
        if (_chart.LowerPane.Count > 0)
        {
            _chart.LowerPaneTitle = "RSI";
            _chart.LowerPaneLevels.Add((70, Palette.DownColor));
            _chart.LowerPaneLevels.Add((30, Palette.UpColor));
        }
        var index = _bars.Select((b, i) => (b.Time, i)).ToDictionary(x => x.Time, x => x.i);
        _chart.Markers.Clear();
        foreach (var f in result.Fills)
            if (index.TryGetValue(f.Time, out int i)) _chart.Markers.Add((i, f.Side == Side.Buy));
        _chart.Refresh();

        _equity.Dates = result.Times;
        _equity.SetSeries(new LineSeries("Strategy", result.Equity, Palette.TealColor) { Fill = true }, new LineSeries("Buy & hold", bench.Equity, Palette.InkSoftColor) { Dashed = true });

        var st = result.Statistics;
        _stats.Content = Ui.Columns(12,
            Ui.Stat("Total return", ChartBase.Percent(st.TotalReturn), $"B&H {ChartBase.Percent(bench.Statistics.TotalReturn)}", st.TotalReturn >= bench.Statistics.TotalReturn),
            Ui.Stat("Sharpe", st.SharpeRatio.ToString("0.00"), $"B&H {bench.Statistics.SharpeRatio:0.00}", st.SharpeRatio >= bench.Statistics.SharpeRatio),
            Ui.Stat("Max drawdown", ChartBase.Percent(st.MaxDrawdown), $"B&H {ChartBase.Percent(bench.Statistics.MaxDrawdown)}", st.MaxDrawdown <= bench.Statistics.MaxDrawdown),
            Ui.Stat("Trades", st.NumberOfTrades.ToString(), $"win rate {ChartBase.Percent(st.WinRate)}", st.WinRate >= 0.5),
            Ui.Stat("Exposure", ChartBase.Percent(st.Exposure), "time in market"));

        _code.Text = RuleStrategy.GenerateCode(sym, entry, exit, _stop.Value, _take.Value, _size.Value);
    }

    /// <summary>One "A comparison B" condition editor.</summary>
    private sealed class RuleEditor
    {
        private readonly ComboBox _left, _cmp, _right;
        private readonly NumericUpDown _lp, _rp;

        public RuleEditor(string caption, int left, int leftPeriod, int right, int rightPeriod, int cmp, Action changed)
        {
            _left = new ComboBox { ItemsSource = Operands, SelectedIndex = left, MinWidth = 150 };
            _right = new ComboBox { ItemsSource = Operands, SelectedIndex = right, MinWidth = 150 };
            _cmp = new ComboBox { ItemsSource = Comparisons, SelectedIndex = cmp, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            _lp = new NumericUpDown { Value = leftPeriod, Minimum = 1, Maximum = 400, Increment = 1, FormatString = "0", Width = 120 };
            _rp = new NumericUpDown { Value = rightPeriod, Minimum = 1, Maximum = 400, Increment = 1, FormatString = "0", Width = 120 };
            foreach (var c in new[] { _left, _cmp, _right }) c.SelectionChanged += (_, _) => changed();
            _lp.ValueChanged += (_, _) => changed();
            _rp.ValueChanged += (_, _) => changed();
            View = new Border
            {
                Background = Palette.Raised,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Child = Ui.Stack(8,
                    Ui.Label(caption, 12, Palette.Teal, FontWeight.SemiBold),
                    Ui.Grid(8, new[] { "*", "Auto" }, new Control[] { _left, _lp }),
                    _cmp,
                    Ui.Grid(8, new[] { "*", "Auto" }, new Control[] { _right, _rp }),
                    Ui.Label("Period / value fields apply to the operand on their left.", 10.5, Palette.InkFaint)),
            };
        }

        public Control View { get; }

        public Rule Rule => new(
            Operands[Math.Max(0, _left.SelectedIndex)], (int)(_lp.Value ?? 20),
            Comparisons[Math.Max(0, _cmp.SelectedIndex)],
            Operands[Math.Max(0, _right.SelectedIndex)], (int)(_rp.Value ?? 50));
    }
}

/// <summary>A condition: left operand, comparison, right operand ("Value" uses the period field as a constant).</summary>
public sealed record Rule(string Left, int LeftPeriod, string Comparison, string Right, int RightPeriod)
{
    public override string ToString() => $"{Describe(Left, LeftPeriod)} {Comparison} {Describe(Right, RightPeriod)}";

    private static string Describe(string op, int p) => op switch { "Close" => "Close", "Value" => p.ToString(), _ => $"{op}({p})" };
}

/// <summary>Executes two <see cref="Rule"/>s with optional stop-loss / take-profit on a single symbol.</summary>
public sealed class RuleStrategy : Strategy
{
    private readonly string _symbol;
    private readonly Rule _entry, _exit;
    private readonly double _stop, _take, _size;
    private readonly double[] _el, _er, _xl, _xr;
    private double _entryPrice;

    public RuleStrategy(string symbol, Bar[] bars, Rule entry, Rule exit, double stopLoss, double takeProfit, double size)
    {
        _symbol = symbol;
        _entry = entry;
        _exit = exit;
        _stop = stopLoss;
        _take = takeProfit;
        _size = size;
        _el = Series(bars, entry.Left, entry.LeftPeriod);
        _er = Series(bars, entry.Right, entry.RightPeriod);
        _xl = Series(bars, exit.Left, exit.LeftPeriod);
        _xr = Series(bars, exit.Right, exit.RightPeriod);
    }

    public override string Name => "Rule strategy";

    public static double[] Series(Bar[] bars, string op, int period)
    {
        var close = bars.Select(b => b.Close).ToArray();
        return op switch
        {
            "SMA" => Ta.Sma(close, period),
            "EMA" => Ta.Ema(close, period),
            "RSI" => Ta.Rsi(close, period),
            "Bollinger upper" => Ta.Bollinger(close, period, 2).Upper,
            "Bollinger lower" => Ta.Bollinger(close, period, 2).Lower,
            "Value" => Enumerable.Repeat((double)period, bars.Length).ToArray(),
            _ => close,
        };
    }

    private static bool Test(string cmp, double[] l, double[] r, int i)
    {
        if (i < 1 || !double.IsFinite(l[i]) || !double.IsFinite(r[i]) || !double.IsFinite(l[i - 1]) || !double.IsFinite(r[i - 1])) return false;
        return cmp switch
        {
            "crosses above" => l[i - 1] <= r[i - 1] && l[i] > r[i],
            "crosses below" => l[i - 1] >= r[i - 1] && l[i] < r[i],
            "is above" => l[i] > r[i],
            _ => l[i] < r[i],
        };
    }

    public override void OnBar(StrategyContext context, BarSlice slice)
    {
        if (!slice.TryGet(_symbol, out var bar)) return;
        int i = context.BarCount(_symbol) - 1;
        double qty = context.PositionQuantity(_symbol);
        if (qty <= 0)
        {
            if (Test(_entry.Comparison, _el, _er, i))
            {
                context.OrderTargetPercent(_symbol, _size);
                _entryPrice = bar.Close;
            }
            return;
        }
        double change = bar.Close / _entryPrice - 1;
        if (Test(_exit.Comparison, _xl, _xr, i) || (_stop > 0 && change <= -_stop) || (_take > 0 && change >= _take))
            context.ClosePosition(_symbol);
    }

    /// <summary>Source code of an equivalent hand-written strategy.</summary>
    public static string GenerateCode(string symbol, Rule entry, Rule exit, double stop, double take, double size)
    {
        string Expr(string op, int p) => op switch
        {
            "SMA" => $"Ta.Sma(close, {p})",
            "EMA" => $"Ta.Ema(close, {p})",
            "RSI" => $"Ta.Rsi(close, {p})",
            "Bollinger upper" => $"Ta.Bollinger(close, {p}, 2).Upper",
            "Bollinger lower" => $"Ta.Bollinger(close, {p}, 2).Lower",
            "Value" => $"Enumerable.Repeat({p}.0, close.Length).ToArray()",
            _ => "close",
        };
        string Cond(Rule r, string l, string rr) => r.Comparison switch
        {
            "crosses above" => $"{l}[i - 1] <= {rr}[i - 1] && {l}[i] > {rr}[i]",
            "crosses below" => $"{l}[i - 1] >= {rr}[i - 1] && {l}[i] < {rr}[i]",
            "is above" => $"{l}[i] > {rr}[i]",
            _ => $"{l}[i] < {rr}[i]",
        };
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string risk = (stop > 0 ? $" || change <= -{stop.ToString("0.###", inv)}" : "") + (take > 0 ? $" || change >= {take.ToString("0.###", inv)}" : "");
        return $$"""
            // Generated by the Finance.Net Gallery Strategy Builder
            // Entry: {{entry}}
            // Exit:  {{exit}}
            using Finance.Backtesting;
            using Finance.TimeSeries.Indicators;

            public sealed class MyRuleStrategy : Strategy
            {
                private double _entryPrice;

                public override void OnBar(StrategyContext ctx, BarSlice slice)
                {
                    var close = ctx.Closes("{{symbol}}").ToArray();
                    int i = close.Length - 1;
                    if (i < 1) return;
                    var a = {{Expr(entry.Left, entry.LeftPeriod)}};
                    var b = {{Expr(entry.Right, entry.RightPeriod)}};
                    var c = {{Expr(exit.Left, exit.LeftPeriod)}};
                    var d = {{Expr(exit.Right, exit.RightPeriod)}};

                    if (ctx.PositionQuantity("{{symbol}}") <= 0)
                    {
                        if ({{Cond(entry, "a", "b")}})
                        {
                            ctx.OrderTargetPercent("{{symbol}}", {{size.ToString("0.##", inv)}});
                            _entryPrice = close[i];
                        }
                        return;
                    }
                    double change = close[i] / _entryPrice - 1;
                    if (({{Cond(exit, "c", "d")}}){{risk}})
                        ctx.ClosePosition("{{symbol}}");
                }
            }

            // var result = new BacktestEngine().Run(new MyRuleStrategy(), "{{symbol}}", bars);
            """;
    }
}
