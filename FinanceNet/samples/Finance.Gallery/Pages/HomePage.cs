using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Finance.Core;
using Finance.Core.Market;
using Finance.Gallery.Controls;
using Finance.Gallery.Shell;

namespace Finance.Gallery.Pages;

/// <summary>Landing page: what Finance.Net is, its modules and the attribution.</summary>
public sealed class HomePage : ModulePage
{
    public override string Title => "Overview";
    public override string Subtitle => "Quantitative finance, trading, risk, portfolio, market intelligence and financial AI — native .NET 10 with Rust and GPU acceleration.";
    public override string Glyph => "◆";
    public override string Group => "Finance.Net";

    private static readonly (string Name, string What, Color Color)[] Modules =
    {
        ("Finance.Core", "Bars, orders, positions, order books, calendars, day counts", Palette.AccentColor),
        ("Finance.Math", "Statistics, matrices, distributions, optimisers, regression", Palette.AccentColor),
        ("Finance.DataFrame", "Columnar OHLCV frames, panels, CSV, GraviFrame interop", Palette.SkyColor),
        ("Finance.TimeSeries", "Returns, rolling statistics, technical indicators", Palette.SkyColor),
        ("Finance.MarketData", "Yahoo, Alpha Vantage, Polygon, Binance, Coinbase", Palette.SkyColor),
        ("Finance.AlternativeData", "Social, ESG, supply chain and weather analytics", Palette.SkyColor),
        ("Finance.Pricing", "Options, bonds, curves, swaps, swaptions, CDS, structured", Palette.GoldColor),
        ("Finance.Risk", "VaR, CVaR, stress tests, Monte Carlo, performance metrics", Palette.GoldColor),
        ("Finance.Portfolio", "Mean-variance, Black-Litterman, risk parity, HRP, CVaR", Palette.GoldColor),
        ("Finance.Backtesting", "Event-driven and vectorised engines, walk-forward", Palette.TealColor),
        ("Finance.Execution", "OMS, FIX 4.4, brokers, TWAP/VWAP, smart order routing", Palette.TealColor),
        ("Finance.ML", "Features, labelling, forests, RL, factor research, SHAP", Palette.PinkColor),
        ("Finance.LLM", "OpenAI/Azure/Anthropic/HF clients, tool-calling assistant", Palette.PinkColor),
        ("Finance.NewsAnalytics", "RSS, lexicon sentiment, events, earnings calls", Palette.PinkColor),
        ("Finance.Visualization", "SVG charts and HTML tearsheets", Palette.VioletColor),
        ("Finance.GPU", "ILGPU Monte Carlo and batch pricing", Palette.OrangeColor),
        ("Finance.RustBridge", "Native Rust kernels with managed fallback", Palette.OrangeColor),
    };

    protected override Control Build()
    {
        var bars = SyntheticMarket.GeometricBrownianBars(500, 180, 0.12, 0.28, seed: 2026);
        var closes = bars.Select(b => b.Close).ToArray();
        double ret = closes[^1] / closes[0] - 1;

        var hero = new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(28, 24),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#1B2A6B"), 0), new GradientStop(Color.Parse("#3B1E7A"), 0.6), new GradientStop(Color.Parse("#0F3B47"), 1) },
            },
            Child = Ui.Grid(24, new[] { "*", "420" }, new Control[]
            {
                Ui.Stack(10,
                    new TextBlock { Text = "The quant stack, native to .NET", FontSize = 28, FontWeight = FontWeight.Bold, Foreground = Brushes.White },
                    new TextBlock
                    {
                        Text = "QuantLib-style pricing, PyPortfolioOpt optimisers, pyfolio analytics, Zipline/Backtrader backtesting, FinRL agents and FinGPT-style language tools — in one coherent, fast, memory-frugal library.",
                        FontSize = 13.5, Foreground = Palette.Brush(Colors.White, 210), TextWrapping = TextWrapping.Wrap, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left,
                    },
                    Ui.Row(8, Ui.Pill(".NET 10", Palette.SkyColor), Ui.Pill("Rust accelerators", Palette.OrangeColor), Ui.Pill("ILGPU", Palette.GoldColor), Ui.Pill("17 modules", Palette.TealColor), Ui.Pill("MIT", Palette.VioletColor)),
                    new TextBlock { Text = FinanceInfo.CreditIndonesian + "  ·  " + FinanceInfo.CreditEnglish, FontSize = 11.5, Foreground = Palette.Brush(Colors.White, 170), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap }),
                new LineChart
                {
                    Height = 170,
                    PlotPadding = new Thickness(44, 10, 6, 22),
                    Series = { new LineSeries("Synthetic equity", closes, Palette.TealColor) { Fill = true } },
                    Dates = bars.Select(b => b.Time).ToArray(),
                    ShowLegend = false,
                },
            }),
        };

        var stats = Ui.Columns(14,
            Ui.Stat("Modules", "17", "Core → AI, one dependency graph", null),
            Ui.Stat("Gallery apps", "11", "Live, interactive, offline-capable", null),
            Ui.Stat("Notebooks", "15", ".NET Interactive (.dib)", null),
            Ui.Stat("Demo series", ChartBase.Percent(ret), "500 synthetic sessions", ret >= 0, closes));

        var grid = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (name, what, color) in Modules)
        {
            grid.Children.Add(new Border
            {
                Width = 262,
                Margin = new Thickness(0, 0, 12, 12),
                Padding = new Thickness(14, 12),
                CornerRadius = new CornerRadius(10),
                Background = Palette.Panel,
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(1),
                Child = Ui.Stack(4,
                    Ui.Row(8, new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Palette.Brush(color), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, Foreground = Palette.Ink, FontSize = 13 }),
                    Ui.Label(what, 11.5, Palette.InkFaint)),
            });
        }

        return Ui.Stack(18, hero, stats,
            Ui.Card(grid, "Architecture", "Every module is a separate NuGet package (Gravicode.Finance.*). Pick only what you need."));
    }
}
