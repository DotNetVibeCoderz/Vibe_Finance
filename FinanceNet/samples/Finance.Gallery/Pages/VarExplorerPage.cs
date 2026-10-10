using Avalonia.Controls;
using Finance.Core.Market;
using Finance.Gallery.Controls;
using Finance.Gallery.Shell;
using Finance.Mathematics;
using Finance.Risk;

namespace Finance.Gallery.Pages;

/// <summary>Value-at-Risk and Expected Shortfall across methods, backtesting, stress tests and Monte Carlo.</summary>
public sealed class VarExplorerPage : ModulePage
{
    public override string Title => "VaR Explorer";
    public override string Subtitle => "Historical, parametric, Cornish-Fisher and Monte Carlo VaR/ES, Kupiec & Christoffersen backtests, historical stress scenarios and simulation fans (Finance.Risk).";
    public override string Glyph => "σ";
    public override string Group => "Quant";

    private static readonly string[] Assets = { "Equities", "Tech", "Bonds", "Gold" };
    private static readonly double[] Drift = { 0.08, 0.14, 0.03, 0.05 };
    private static readonly double[] Vol = { 0.18, 0.32, 0.06, 0.15 };
    private static readonly double[,] Corr =
    {
        { 1.0, 0.8, -0.2, 0.1 },
        { 0.8, 1.0, -0.25, 0.05 },
        { -0.2, -0.25, 1.0, 0.25 },
        { 0.1, 0.05, 0.25, 1.0 },
    };

    private Slider _confidence = null!, _wEq = null!, _wTech = null!, _wBond = null!;
    private readonly HistogramChart _hist = new() { Height = 300, Title = "Daily portfolio returns" };
    private readonly ContentControl _methods = new();
    private readonly ContentControl _backtest = new();
    private readonly BarChart _stress = new() { Height = 260, Horizontal = true, Title = "Stress scenarios — P&L on $1M" };
    private readonly LineChart _fan = new() { Height = 260, Title = "1-year Monte Carlo fan (5/25/50/75/95%)" };
    private double[][] _returns = Array.Empty<double[]>();

    protected override Control Build()
    {
        var prices = SyntheticMarket.CorrelatedPrices(1500, Drift, Vol, Corr, seed: 99);
        _returns = new double[prices.Length - 1][];
        for (int t = 1; t < prices.Length; t++)
        {
            var r = new double[Assets.Length];
            for (int i = 0; i < r.Length; i++) r[i] = prices[t][i] / prices[t - 1][i] - 1;
            _returns[t - 1] = r;
        }

        (var confRow, _confidence) = Ui.Slider("Confidence", 0.90, 0.995, 0.99, v => (v * 100).ToString("0.0") + "%");
        (var eqRow, _wEq) = Ui.Slider("Equities weight", 0, 1, 0.35, ChartBase.Percent);
        (var techRow, _wTech) = Ui.Slider("Tech weight", 0, 1, 0.25, ChartBase.Percent);
        (var bondRow, _wBond) = Ui.Slider("Bonds weight", 0, 1, 0.30, ChartBase.Percent);
        foreach (var s in new[] { _confidence, _wEq, _wTech, _wBond })
            s.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) Recalculate(); };

        var controls = Ui.Card(Ui.Stack(14, confRow, eqRow, techRow, bondRow,
            Ui.Label("Gold takes the remainder. Weights are normalised to 100%. Returns: 1,500 synthetic trading days from correlated GBM.", 11.5, Palette.InkFaint)),
            "Portfolio", "Four-asset book");

        var right = Ui.Stack(14,
            Ui.Columns(14, Ui.Card(_hist), Ui.Card(_methods, "VaR / ES by method", "1-day, as a fraction of portfolio value")),
            Ui.Columns(14, Ui.Card(_stress), Ui.Card(_fan)),
            Ui.Card(_backtest, "Backtest of historical VaR (rolling 250-day window)", "Kupiec POF and Christoffersen independence tests, Basel traffic light"));
        Recalculate();
        return Ui.Grid(18, new[] { "300", "*" }, new Control[] { controls, right });
    }

    private double[] Weights()
    {
        double a = _wEq.Value, b = _wTech.Value, c = _wBond.Value;
        double gold = Math.Max(0, 1 - a - b - c);
        double sum = a + b + c + gold;
        return new[] { a / sum, b / sum, c / sum, gold / sum };
    }

    private void Recalculate()
    {
        if (_wBond is null) return;
        double conf = _confidence.Value;
        var w = Weights();
        var port = _returns.Select(r => r.Select((x, i) => x * w[i]).Sum()).ToArray();

        var estimates = new (string Name, VarEstimate E)[]
        {
            ("Historical", ValueAtRisk.Historical(port, conf)),
            ("Parametric normal", ValueAtRisk.ParametricNormal(port, conf)),
            ("Student-t (ν=5)", ValueAtRisk.ParametricStudentT(port, 5, conf)),
            ("Cornish-Fisher", ValueAtRisk.CornishFisher(port, conf)),
            ("Monte Carlo (100k)", MonteCarloVar(w, conf)),
        };
        _methods.Content = Ui.DataTable(new[] { "Method", "VaR", "ES", "VaR on $1M" },
            estimates.Select(e => new[] { e.Name, ChartBase.Percent2(e.E.ValueAtRisk), ChartBase.Percent2(e.E.ExpectedShortfall), "$" + (e.E.ValueAtRisk * 1_000_000).ToString("N0") }),
            (r, c) => c == 1 ? Palette.Down : c == 2 ? Palette.Gold : null);

        var hist = estimates[0].E;
        _hist.Values = port;
        _hist.TailThreshold = -hist.ValueAtRisk;
        _hist.Markers.Clear();
        _hist.Markers.Add((-hist.ValueAtRisk, "VaR", Palette.DownColor));
        _hist.Markers.Add((-hist.ExpectedShortfall, "ES", Palette.GoldColor));
        _hist.XFormat = ChartBase.Percent2;
        double m = Stats.Mean(port), sd = Stats.StdDev(port);
        _hist.Density = x => Normal.Pdf(x, m, sd);
        _hist.Refresh();

        // Rolling backtest.
        const int window = 250;
        var forecasts = new double[port.Length - window];
        var realised = new double[port.Length - window];
        for (int t = window; t < port.Length; t++)
        {
            forecasts[t - window] = ValueAtRisk.Historical(port.AsSpan(t - window, window), conf).ValueAtRisk;
            realised[t - window] = port[t];
        }
        var bt = VarBacktest.Run(realised, forecasts, conf);
        _backtest.Content = Ui.Columns(14,
            Ui.Stat("Exceptions", $"{bt.Exceptions} / {bt.Observations}", $"expected {bt.ExpectedExceptions:0.0}", bt.Exceptions <= bt.ExpectedExceptions * 1.5),
            Ui.Stat("Kupiec p-value", bt.KupiecPValue.ToString("0.000"), bt.KupiecPValue > 0.05 ? "coverage not rejected" : "coverage rejected", bt.KupiecPValue > 0.05),
            Ui.Stat("Christoffersen p", bt.ChristoffersenPValue.ToString("0.000"), bt.ChristoffersenPValue > 0.05 ? "independent exceptions" : "clustered exceptions", bt.ChristoffersenPValue > 0.05),
            Ui.Stat("Basel zone", bt.Zone.ToString(), "traffic-light test", bt.Zone.ToString() == "Green"));

        // Stress testing on $1M.
        var positions = new List<StressPosition>
        {
            StressPosition.Equity("Equities", 1_000_000 * w[0], 1.0),
            StressPosition.Equity("Tech", 1_000_000 * w[1], 1.35),
            StressPosition.Bond("Bonds", 1_000_000 * w[2], 7.0, 2.0, 60),
            StressPosition.Single("Gold", 1_000_000 * w[3], RiskFactors.Commodity, 0.6),
        };
        var stress = StressTester.RunAll(positions);
        _stress.Labels = stress.Select(s => s.Scenario).ToArray();
        _stress.Values = stress.Select(s => s.TotalPnl).ToArray();
        _stress.Format = v => (v / 1000).ToString("0") + "K";
        _stress.Refresh();

        // Monte Carlo fan.
        var cov = AnnualCovariance();
        var sim = PortfolioSimulation.Run(1_000_000, w, Drift, cov, years: 1, stepsPerYear: 52, paths: 5000, seed: 5, samplePaths: 0);
        var x = sim.Times.Select(t => t * 12).ToArray();
        _fan.XFormat = v => $"{v:0}m";
        _fan.YFormat = ChartBase.Compact;
        var colors = new[] { Palette.DownColor, Palette.PinkColor, Palette.AccentColor, Palette.TealColor, Palette.UpColor };
        _fan.SetSeries(sim.Fan.Select((f, k) => new LineSeries($"P{sim.FanPercentiles[k] * 100:0}", f, colors[k % colors.Length]) { X = x, Fill = k == 2, Thickness = k == 2 ? 2.6 : 1.5 }).ToArray());
    }

    private static Matrix AnnualCovariance()
    {
        int n = Vol.Length;
        var cov = new Matrix(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) cov[i, j] = Corr[i, j] * Vol[i] * Vol[j];
        return cov;
    }

    private static VarEstimate MonteCarloVar(double[] w, double conf)
    {
        var daily = AnnualCovariance();
        var mu = Drift.Select(d => d / 252).ToArray();
        return ValueAtRisk.MonteCarlo(w, mu, (1.0 / 252) * daily, conf, 100_000, 11);
    }
}
