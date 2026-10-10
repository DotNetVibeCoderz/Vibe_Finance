using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Finance.Core.Market;
using Finance.Gallery.Controls;
using Finance.Gallery.Shell;
using Finance.MarketData;
using Finance.MarketData.Providers;
using Finance.TimeSeries.Indicators;

namespace Finance.Gallery.Pages;

/// <summary>Live crypto watchlist and chart from Binance public market data, with a synthetic fallback offline.</summary>
public sealed class LiveMarketPage : ModulePage
{
    public override string Title => "Live Market Dashboard";
    public override string Subtitle => "Real-time Binance market data (Finance.MarketData) — watchlist, 1-minute candles with EMA overlays and RSI, refreshed every few seconds. Falls back to the synthetic provider offline.";
    public override string Glyph => "◉";
    public override string Group => "Markets";

    private static readonly string[] Symbols = { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT" };

    private IMarketDataProvider _provider = new BinanceProvider();
    private readonly Dictionary<string, Border> _tiles = new();
    private readonly Dictionary<string, Sparkline> _sparks = new();
    private readonly CandleChart _chart = new() { Height = 520, VisibleBars = 120, LowerPaneTitle = "RSI(14)" };
    private readonly TextBlock _source = new() { FontSize = 11.5, Foreground = Palette.InkFaint };
    private readonly TextBlock _headline = new() { FontSize = 24, FontWeight = FontWeight.Bold, Foreground = Palette.Ink };
    private readonly TextBlock _change = new() { FontSize = 13, FontWeight = FontWeight.SemiBold };
    private readonly ContentControl _stats = new();
    private DispatcherTimer? _timer;
    private string _selected = Symbols[0];
    private bool _refreshing, _online = true;
    private Task _ready = Task.CompletedTask;

    protected override Control Build()
    {
        var watch = new Avalonia.Controls.Primitives.UniformGrid { Columns = 6 };
        foreach (var s in Symbols)
        {
            var spark = new Sparkline { Height = 34, Margin = new Thickness(0, 6, 0, 0) };
            var tile = Ui.Stat(s.Replace("USDT", "/USDT"), "…", "…", null);
            ((StackPanel)tile.Child!).Children.Add(spark);
            tile.Margin = new Thickness(0, 0, 12, 0);
            tile.Cursor = new Cursor(StandardCursorType.Hand);
            var sym = s;
            tile.PointerPressed += (_, _) => { _selected = sym; _ = RefreshAsync(); };
            _tiles[s] = tile;
            _sparks[s] = spark;
            watch.Children.Add(tile);
        }
        _chart.LowerPaneLevels.Add((70, Palette.DownColor));
        _chart.LowerPaneLevels.Add((30, Palette.UpColor));

        var header = Ui.Row(14, _headline, _change);
        header.VerticalAlignment = VerticalAlignment.Center;
        _ready = RefreshAsync();
        return Ui.Stack(14, watch,
            Ui.Grid(14, new[] { "*", "280" }, new Control[]
            {
                Ui.Card(Ui.Stack(6, header, _source, _chart)),
                Ui.Card(_stats, "Session statistics", "Last 120 one-minute bars"),
            }));
    }

    public override Task WhenReadyAsync() => _ready;

    public override void OnShown()
    {
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) => _ = RefreshAsync());
        _timer.Start();
    }

    public override void OnHidden() => _timer?.Stop();

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            Bar[] bars;
            try
            {
                var quotes = await Task.WhenAll(Symbols.Select(s => _provider.GetQuoteAsync(s)));
                foreach (var q in quotes) UpdateTile(q);
                bars = await _provider.GetRecentBarsAsync(_selected, BarInterval.Minute1, TimeSpan.FromHours(3));
                if (_sparks[Symbols[0]].Values.Length == 0)
                    foreach (var s in Symbols)
                    {
                        var hourly = await _provider.GetRecentBarsAsync(s, BarInterval.Hour1, TimeSpan.FromDays(3));
                        _sparks[s].Values = hourly.Select(b => b.Close).ToArray();
                        _sparks[s].Refresh();
                    }
                _source.Text = $"Source: {(_online ? "Binance public market data (data-api.binance.vision)" : "synthetic provider (offline)")} · updated {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception) when (_online)
            {
                _online = false;
                _provider = new SyntheticProvider();
                _refreshing = false;
                await RefreshAsync();
                return;
            }
            if (bars.Length == 0) return;
            UpdateChart(bars);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void UpdateTile(MarketSnapshot q)
    {
        if (!_tiles.TryGetValue(q.Symbol, out var tile)) return;
        string price = q.Last >= 100 ? q.Last.ToString("N2") : q.Last.ToString("0.0000");
        bool up = !(q.ChangePercent < 0);
        Ui.SetStat(tile, price, double.IsNaN(q.ChangePercent) ? "" : $"{(up ? "▲" : "▼")} {Math.Abs(q.ChangePercent):0.00}% 24h", up);
        tile.BorderBrush = q.Symbol == _selected ? Palette.Accent : Palette.Line;
    }

    private void UpdateChart(Bar[] bars)
    {
        var close = bars.Select(b => b.Close).ToArray();
        _chart.Bars = bars;
        _chart.Overlays.Clear();
        _chart.Overlays.Add(new LineSeries("EMA 20", Ta.Ema(close, 20), Palette.GoldColor) { Thickness = 1.6 });
        _chart.Overlays.Add(new LineSeries("EMA 50", Ta.Ema(close, 50), Palette.SkyColor) { Thickness = 1.6 });
        _chart.LowerPane.Clear();
        _chart.LowerPane.Add(new LineSeries("RSI", Ta.Rsi(close, 14), Palette.VioletColor) { Thickness = 1.6 });
        _chart.PriceFormat = v => v >= 100 ? v.ToString("N1") : v.ToString("0.0000");
        _chart.Refresh();

        var window = bars.TakeLast(120).ToArray();
        double first = window[0].Open, last = window[^1].Close;
        double ret = last / first - 1;
        _headline.Text = $"{_selected.Replace("USDT", "/USDT")}  {_chart.PriceFormat(last)}";
        _change.Text = $"{(ret >= 0 ? "▲" : "▼")} {Math.Abs(ret) * 100:0.00}% over 2h";
        _change.Foreground = ret >= 0 ? Palette.Up : Palette.Down;
        var lr = new double[window.Length - 1];
        for (int i = 1; i < window.Length; i++) lr[i - 1] = Math.Log(window[i].Close / window[i - 1].Close);
        double vol = Finance.Mathematics.Stats.StdDev(lr) * Math.Sqrt(525_600);
        var vwap = window.Sum(b => b.Typical * b.Volume) / Math.Max(1e-12, window.Sum(b => b.Volume));
        var rsi = Ta.Rsi(close, 14)[^1];
        _stats.Content = Ui.Table(new[]
        {
            ("High", _chart.PriceFormat(window.Max(b => b.High))),
            ("Low", _chart.PriceFormat(window.Min(b => b.Low))),
            ("VWAP", _chart.PriceFormat(vwap)),
            ("Volume", ChartBase.Compact(window.Sum(b => b.Volume))),
            ("Realised vol (ann.)", ChartBase.Percent(vol)),
            ("RSI(14)", rsi.ToString("0.0")),
            ("Signal", rsi > 70 ? "Overbought" : rsi < 30 ? "Oversold" : close[^1] > Ta.Ema(close, 50)[^1] ? "Above EMA50" : "Below EMA50"),
            ("Bars", window.Length.ToString()),
        });
        foreach (var (s, t) in _tiles) t.BorderBrush = s == _selected ? Palette.Accent : Palette.Line;
    }
}
