using Avalonia;
using Avalonia.Controls;
using Finance.Backtesting;
using Finance.Backtesting.Strategies;
using Finance.Core.Market;
using Finance.Gallery.Controls;
using Finance.Gallery.Shell;
using Finance.MarketData;
using Finance.MarketData.Providers;

namespace Finance.Gallery.Pages;

/// <summary>Event-driven backtests of the built-in strategies, costs, trade log and walk-forward analysis.</summary>
public sealed class BacktestingStudioPage : ModulePage
{
    public override string Title => "Backtesting Studio";
    public override string Subtitle => "Event-driven engine (Finance.Backtesting) with next-bar fills, commissions and slippage: equity vs buy-and-hold, drawdowns, round-trip log and walk-forward optimisation of an SMA crossover.";
    public override string Glyph => "▶";
    public override string Group => "Trading";

    private static readonly string[] StrategyNames = { "SMA crossover", "RSI mean reversion", "Bollinger breakout", "Momentum rotation (multi-asset)", "Buy & hold" };
    private static readonly string[] Universe = { "AAPL", "MSFT", "NVDA", "AMZN", "META", "GOOGL", "JPM", "XOM" };

    private ComboBox _strategy = null!, _data = null!;
    private Slider _p1 = null!, _p2 = null!, _commission = null!, _slippage = null!;
    private Control _p1Row = null!, _p2Row = null!;
    private readonly LineChart _equity = new() { Height = 300, Title = "Equity curve" };
    private readonly LineChart _drawdown = new() { Height = 170, Title = "Drawdown", YFormat = ChartBase.Percent };
    private readonly CandleChart _candles = new() { Height = 300, VisibleBars = 180, ShowVolume = false, Title = "Trades" };
    private readonly ContentControl _trades = new();
    private readonly ContentControl _walkForward = new();
    private readonly LineChart _wfChart = new() { Height = 220, Title = "Walk-forward: stitched out-of-sample equity" };
    private readonly Avalonia.Controls.Primitives.UniformGrid _stats = new() { Columns = 6 };
    private Dictionary<string, Bar[]> _bars = new();
    private Task _ready = Task.CompletedTask;

    protected override Control Build()
    {
        (var sRow, _strategy) = Ui.Combo("Strategy", StrategyNames);
        (var dRow, _data) = Ui.Combo("Data", new[] { "Synthetic (offline)", "Yahoo Finance daily (live)" });
        (_p1Row, _p1) = Ui.Slider("Fast SMA", 5, 60, 20, v => v.ToString("0"), 1);
        (_p2Row, _p2) = Ui.Slider("Slow SMA", 20, 250, 100, v => v.ToString("0"), 1);
        (var cRow, _commission) = Ui.Slider("Commission", 0, 0.002, 0.0005, v => $"{v * 1e4:0.0} bps");
        (var slRow, _slippage) = Ui.Slider("Slippage", 0, 20, 2, v => $"{v:0.0} bps");
        _strategy.SelectionChanged += (_, _) => { Relabel(); Run(); };
        _data.SelectionChanged += (_, _) => _ready = LoadAsync();
        foreach (var s in new[] { _p1, _p2, _commission, _slippage })
            s.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) Run(); };

        var controls = Ui.Card(Ui.Stack(14, sRow, dRow, _p1Row, _p2Row, cRow, slRow,
            Ui.Label("Orders fill at the next bar's open. Statistics are annualised with 252 bars per year; Sharpe uses a 0% risk-free rate.", 11.5, Palette.InkFaint)),
            "Backtest", "$100,000 starting cash");

        var right = Ui.Stack(14,
            _stats,
            Ui.Grid(14, new[] { "3*", "2*" }, new Control[] { Ui.Stack(14, Ui.Card(_equity), Ui.Card(_drawdown)), Ui.Card(_trades, "Round trips", "Most recent first") }),
            Ui.Card(_candles),
            Ui.Grid(14, new[] { "3*", "2*" }, new Control[] { Ui.Card(_wfChart), Ui.Card(_walkForward, "Walk-forward windows", "SMA grid, 500-bar in-sample → 125-bar out-of-sample, objective = Sharpe") }));
        Relabel();
        _ready = LoadAsync();
        return Ui.Grid(18, new[] { "300", "*" }, new Control[] { controls, right });
    }

    public override Task WhenReadyAsync() => _ready;

    private void Relabel()
    {
        int s = _strategy.SelectedIndex;
        _p1Row.IsVisible = _p2Row.IsVisible = s != 4;
        SetCaption(_p1Row, s switch { 1 => "RSI period", 2 => "Bollinger period", 3 => "Lookback (days)", _ => "Fast SMA" });
        SetCaption(_p2Row, s switch { 1 => "Oversold level", 2 => "Width (σ × 10)", 3 => "Hold top N", _ => "Slow SMA" });
        (double a, double b, double c, double d, double e, double f) = s switch
        {
            1 => (2, 30, 14, 5, 45, 30),
            2 => (5, 60, 20, 10, 40, 20),
            3 => (20, 252, 126, 1, 6, 3),
            _ => (5, 60, 20, 20, 250, 100),
        };
        _p1.Minimum = a; _p1.Maximum = b; _p1.Value = c;
        _p2.Minimum = d; _p2.Maximum = e; _p2.Value = f;
    }

    private static void SetCaption(Control row, string caption)
    {
        if (row is StackPanel { Children: [DockPanel head, ..] } && head.Children.OfType<TextBlock>().LastOrDefault() is { } t) t.Text = caption;
    }

    private async Task LoadAsync()
    {
        var bars = new Dictionary<string, Bar[]>();
        if (_data.SelectedIndex == 1)
        {
            try
            {
                var yahoo = new YahooFinanceProvider();
                foreach (var s in Universe)
                    bars[s] = await yahoo.GetBarsAsync(s, BarInterval.Day1, DateTime.UtcNow.AddYears(-6), DateTime.UtcNow);
            }
            catch (Exception)
            {
                bars.Clear();
                _data.SelectedIndex = 0;
            }
        }
        if (bars.Count == 0)
        {
            var synth = new SyntheticProvider();
            foreach (var s in Universe)
                bars[s] = await synth.GetBarsAsync(s, BarInterval.Day1, new DateTime(2019, 1, 1), new DateTime(2025, 6, 30));
        }
        _bars = bars;
        Run();
        RunWalkForward();
    }

    private BacktestSettings Settings() => new()
    {
        InitialCash = 100_000,
        Commission = new PercentageCommission(_commission.Value),
        Slippage = new FixedBpsSlippage(_slippage.Value),
    };

    private void Run()
    {
        if (_bars.Count == 0) return;
        string sym = Universe[0];
        int a = (int)_p1.Value, b = (int)_p2.Value;
        Strategy strategy = _strategy.SelectedIndex switch
        {
            1 => new RsiMeanReversionStrategy(a, b, 100 - b, sym),
            2 => new BollingerBreakoutStrategy(a, b / 10.0, sym),
            3 => new MomentumRotationStrategy(a, Math.Min(b, Universe.Length - 1), 21),
            4 => new BuyAndHoldStrategy(sym),
            _ => new SmaCrossoverStrategy(Math.Min(a, b - 1), b, sym),
        };
        var engine = new BacktestEngine(Settings());
        bool multi = _strategy.SelectedIndex == 3;
        var result = multi ? engine.Run(strategy, _bars) : engine.Run(strategy, sym, _bars[sym]);
        var bench = multi
            ? new BacktestEngine(Settings()).Run(new BuyAndHoldStrategy(Universe), _bars)
            : new BacktestEngine(Settings()).Run(new BuyAndHoldStrategy(sym), sym, _bars[sym]);

        var st = result.Statistics;
        _stats.Children.Clear();
        foreach (var tile in new[]
                 {
                     Ui.Stat("Total return", ChartBase.Percent(st.TotalReturn), $"B&H {ChartBase.Percent(bench.Statistics.TotalReturn)}", st.TotalReturn >= bench.Statistics.TotalReturn),
                     Ui.Stat("CAGR", ChartBase.Percent2(st.Cagr), $"vol {ChartBase.Percent(st.AnnualVolatility)}", st.Cagr > 0),
                     Ui.Stat("Sharpe", st.SharpeRatio.ToString("0.00"), $"Sortino {st.SortinoRatio:0.00}", st.SharpeRatio > 0.5),
                     Ui.Stat("Max drawdown", ChartBase.Percent(st.MaxDrawdown), $"{st.MaxDrawdownDuration} bars", st.MaxDrawdown < bench.Statistics.MaxDrawdown),
                     Ui.Stat("Trades", st.NumberOfTrades.ToString(), $"win rate {ChartBase.Percent(st.WinRate)}", st.WinRate >= 0.5),
                     Ui.Stat("Profit factor", double.IsInfinity(st.ProfitFactor) ? "∞" : st.ProfitFactor.ToString("0.00"), $"exposure {ChartBase.Percent(st.Exposure)}", st.ProfitFactor > 1),
                 })
        {
            tile.Margin = new Thickness(0, 0, 12, 0);
            _stats.Children.Add(tile);
        }

        _equity.Dates = result.Times;
        _equity.SetSeries(
            new LineSeries(result.StrategyName, result.Equity, Palette.TealColor) { Fill = true, Thickness = 2.2 },
            new LineSeries("Buy & hold", Align(bench, result), Palette.InkSoftColor) { Dashed = true, Thickness = 1.5 });
        _drawdown.Dates = result.Times;
        _drawdown.SetSeries(new LineSeries("Drawdown", result.Drawdowns().Select(d => -Math.Abs(d)).ToArray(), Palette.DownColor) { Fill = true });

        var bars = _bars[sym];
        var index = new Dictionary<DateTime, int>();
        for (int i = 0; i < bars.Length; i++) index[bars[i].Time] = i;
        _candles.Bars = bars;
        _candles.Title = $"{sym} with fills";
        _candles.Markers.Clear();
        foreach (var f in result.Fills.Where(f => f.Symbol == sym))
            if (index.TryGetValue(f.Time, out int i)) _candles.Markers.Add((i, f.Side == Side.Buy));
        _candles.Refresh();

        _trades.Content = result.Trades.Count == 0
            ? Ui.Label("No completed round trips.", 12, Palette.InkFaint)
            : Ui.DataTable(new[] { "Entry → exit", "Return", "PnL" },
                result.Trades.Reverse().Take(16).Select(t => new[]
                {
                    $"{t.Symbol}  {t.EntryTime:dd MMM yy} → {t.ExitTime:dd MMM yy}", ChartBase.Percent(t.ReturnPct), t.Pnl.ToString("N0"),
                }),
                (r, c) => c >= 1 ? (result.Trades.Reverse().ElementAt(r).Pnl >= 0 ? Palette.Up : Palette.Down) : null);
    }

    private static double[] Align(BacktestResult bench, BacktestResult result)
    {
        var map = new Dictionary<DateTime, double>();
        for (int i = 0; i < bench.Times.Length; i++) map[bench.Times[i]] = bench.Equity[i];
        return result.Times.Select(t => map.TryGetValue(t, out var v) ? v : double.NaN).ToArray();
    }

    private void RunWalkForward()
    {
        string sym = Universe[0];
        var grid = from f in new[] { 10, 20, 30, 50 } from s in new[] { 60, 100, 150, 200 } where f < s select (Fast: f, Slow: s);
        var wf = new WalkForwardOptimizer<(int Fast, int Slow)>(grid, p => new SmaCrossoverStrategy(p.Fast, p.Slow, sym), Settings());
        var res = wf.Run(new Dictionary<string, Bar[]> { [sym] = _bars[sym] }, 500, 125);
        _wfChart.Dates = res.Times;
        _wfChart.SetSeries(new LineSeries("Out-of-sample equity", res.Equity, Palette.VioletColor) { Fill = true });
        _walkForward.Content = Ui.Stack(8,
            Ui.DataTable(new[] { "Window", "Out-of-sample from", "Best params" },
                res.Windows.Select(w => new[] { $"#{w.Index + 1}", w.OutOfSampleStart.ToString("yyyy-MM-dd"), $"SMA {w.BestParameters.Fast}/{w.BestParameters.Slow}" })),
            Ui.Label($"Stitched OOS: return {ChartBase.Percent(res.Statistics.TotalReturn)}, Sharpe {res.Statistics.SharpeRatio:0.00}, max DD {ChartBase.Percent(res.Statistics.MaxDrawdown)}", 12, Palette.InkSoft));
    }
}
