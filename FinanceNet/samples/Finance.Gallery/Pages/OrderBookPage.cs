using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Finance.Core.Market;
using Finance.Gallery.Controls;
using Finance.Gallery.Shell;
using Finance.MarketData;
using Finance.MarketData.Providers;

namespace Finance.Gallery.Pages;

/// <summary>Level-2 order book: ladder, cumulative depth, microstructure stats and market-impact estimates.</summary>
public sealed class OrderBookPage : ModulePage
{
    public override string Title => "Order Book Viewer";
    public override string Subtitle => "Level-2 depth from Binance (Finance.MarketData → Core OrderBook): price ladder, cumulative depth, imbalance, microprice and sweep-cost estimates, refreshed every second.";
    public override string Glyph => "≣";
    public override string Group => "Markets";

    private IOrderBookProvider _provider = new BinanceProvider();
    private readonly OrderBookLadder _ladder = new() { Height = 640, Levels = 14 };
    private readonly DepthChart _depth = new() { Height = 330, Title = "Cumulative depth" };
    private readonly LineChart _spread = new() { Height = 250, Title = "Mid price and microprice (last 2 minutes)" };
    private readonly ContentControl _impact = new();
    private Border _midTile = null!, _spreadTile = null!, _imbTile = null!, _depthTile = null!;
    private ComboBox _symbol = null!;
    private DispatcherTimer? _timer;
    private readonly List<double> _mids = new(), _micro = new();
    private bool _busy, _online = true;
    private Task _ready = Task.CompletedTask;

    protected override Control Build()
    {
        (var symRow, _symbol) = Ui.Combo("Market", new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT" });
        _symbol.SelectionChanged += (_, _) => { _mids.Clear(); _micro.Clear(); _ = RefreshAsync(); };
        _midTile = Ui.Stat("Mid", "…");
        _spreadTile = Ui.Stat("Spread", "…");
        _imbTile = Ui.Stat("Imbalance (5 lvls)", "…");
        _depthTile = Ui.Stat("Depth bid / ask", "…");
        _ready = RefreshAsync();
        return Ui.Grid(14, new[] { "360", "*" }, new Control[]
        {
            Ui.Card(Ui.Stack(10, symRow, _ladder), "Price ladder"),
            Ui.Stack(14,
                Ui.Columns(12, _midTile, _spreadTile, _imbTile, _depthTile),
                Ui.Card(_depth),
                Ui.Columns(14, Ui.Card(_spread), Ui.Card(_impact, "Market impact", "Volume-weighted fill price when sweeping the book"))),
        });
    }

    public override Task WhenReadyAsync() => _ready.ContinueWith(_ => Task.Delay(3500)).Unwrap();

    public override void OnShown()
    {
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _ = RefreshAsync());
        _timer.Start();
    }

    public override void OnHidden() => _timer?.Stop();

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        string sym = _symbol.SelectedItem as string ?? "BTCUSDT";
        try
        {
            OrderBook book;
            try
            {
                book = await _provider.GetOrderBookAsync(sym, 50);
            }
            catch (Exception) when (_online)
            {
                _online = false;
                _provider = new SyntheticProvider();
                book = await _provider.GetOrderBookAsync(sym, 50);
            }
            Render(book);
        }
        catch (Exception)
        {
            // keep the last good frame
        }
        finally
        {
            _busy = false;
        }
    }

    private void Render(OrderBook book)
    {
        string F(double v) => v >= 1000 ? v.ToString("N2") : v.ToString("0.0000");
        _ladder.Book = book;
        _ladder.PriceFormat = F;
        _ladder.Refresh();
        _depth.Book = book;
        _depth.PriceFormat = v => v >= 1000 ? v.ToString("N1") : v.ToString("0.000");
        _depth.Title = _online ? "Cumulative depth · live Binance" : "Cumulative depth · synthetic (offline)";
        _depth.Refresh();

        double bps = book.Spread / book.Mid * 1e4;
        var micro = new Quote(book.Time, book.BestBid, book.Bids[0].Size, book.BestAsk, book.Asks[0].Size).MicroPrice;
        Ui.SetStat(_midTile, F(book.Mid), $"micro {F(micro)}", micro >= book.Mid);
        Ui.SetStat(_spreadTile, F(book.Spread), $"{bps:0.00} bps");
        double imb = book.Imbalance(5);
        Ui.SetStat(_imbTile, imb.ToString("+0.000;-0.000"), imb > 0 ? "bid-heavy" : "ask-heavy", imb > 0);
        Ui.SetStat(_depthTile, $"{ChartBase.Compact(book.Depth(Side.Buy))} / {ChartBase.Compact(book.Depth(Side.Sell))}", "units across shown levels");

        _mids.Add(book.Mid);
        _micro.Add(micro);
        if (_mids.Count > 120) { _mids.RemoveAt(0); _micro.RemoveAt(0); }
        _spread.YFormat = F;
        _spread.SetSeries(new LineSeries("Mid", _mids.ToArray(), Palette.AccentColor), new LineSeries("Microprice", _micro.ToArray(), Palette.GoldColor) { Dashed = true });

        double unit = Math.Max(book.Depth(Side.Buy), 1) / 40;
        double[] sizes = { unit, unit * 4, unit * 12, unit * 30 };
        _impact.Content = Ui.DataTable(new[] { "Size", "Buy cost", "Sell cost", "Notional" }, sizes.Select(q =>
        {
            double b = book.SweepPrice(Side.Buy, q), s = book.SweepPrice(Side.Sell, q);
            return new[]
            {
                ChartBase.Compact(q),
                double.IsNaN(b) ? "book too thin" : $"{(b / book.Mid - 1) * 1e4:0.00} bps",
                double.IsNaN(s) ? "book too thin" : $"{(1 - s / book.Mid) * 1e4:0.00} bps",
                ChartBase.Compact(q * book.Mid),
            };
        }), (r, c) => c is 1 or 2 ? Palette.Gold : null);
    }
}
