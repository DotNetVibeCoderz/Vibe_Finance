using Avalonia;
using Avalonia.Controls;
using Finance.Core;
using Finance.Core.Market;
using Finance.Gallery.Controls;
using Finance.Gallery.Shell;
using Finance.Mathematics;
using Finance.Portfolio;
using Matrix = Finance.Mathematics.Matrix;

namespace Finance.Gallery.Pages;

/// <summary>PyPortfolioOpt-style optimisation: ten methods, three risk models, frontier, risk contributions, discrete allocation.</summary>
public sealed class PortfolioOptimizerPage : ModulePage
{
    public override string Title => "Portfolio Optimizer";
    public override string Subtitle => "Mean-variance, Black-Litterman, risk parity, hierarchical risk parity, CVaR and heuristic portfolios over an eight-asset universe, with risk models and discrete allocation (Finance.Portfolio).";
    public override string Glyph => "◔";
    public override string Group => "Quant";

    private static readonly string[] Assets = { "US Large", "US Tech", "Europe", "Emerging", "Gov Bonds", "Corp Bonds", "Gold", "REITs" };
    private static readonly double[] Drift = { 0.08, 0.13, 0.06, 0.09, 0.03, 0.045, 0.05, 0.07 };
    private static readonly double[] Vol = { 0.16, 0.27, 0.18, 0.23, 0.06, 0.08, 0.15, 0.21 };

    private static readonly string[] Methods =
    {
        "Max Sharpe", "Min Volatility", "Efficient Return", "Black-Litterman", "Risk Parity (ERC)",
        "Hierarchical Risk Parity", "Min CVaR (95%)", "Max Diversification", "Inverse Volatility", "Equal Weight",
    };

    private ComboBox _method = null!, _risk = null!;
    private Slider _maxWeight = null!, _target = null!, _view = null!;
    private readonly ScatterChart _frontier = new() { Height = 330, Title = "Efficient frontier", XLabel = "volatility", YLabel = "return" };
    private readonly BarChart _weights = new() { Height = 300, Horizontal = true, Title = "Weights", Format = ChartBase.Percent, BarColor = Palette.AccentColor };
    private readonly BarChart _contrib = new() { Height = 300, Horizontal = true, Title = "Risk contribution", Format = ChartBase.Percent, BarColor = Palette.GoldColor };
    private readonly HeatmapChart _corr = new() { Height = 330, Title = "Correlation", ShowValues = true, PlotPadding = new Thickness(84, 28, 12, 34) };
    private readonly ContentControl _allocation = new();
    private Border _ret = null!, _vol = null!, _sharpe = null!, _div = null!;
    private Matrix _returns = null!;
    private double[] _lastPrices = null!;

    protected override Control Build()
    {
        var corr = new double[8, 8];
        for (int i = 0; i < 8; i++)
            for (int j = 0; j < 8; j++)
                corr[i, j] = i == j ? 1 : Block(i) == Block(j) ? 0.75 : (Block(i) == 1 || Block(j) == 1) ? (Block(i) == 2 || Block(j) == 2 ? 0.05 : -0.15) : 0.25;
        var prices = SyntheticMarket.CorrelatedPrices(1260, Drift, Vol, corr, seed: 2024);
        _lastPrices = prices[^1].Select((p, i) => p * (i + 1) * 0.6).ToArray();
        _returns = ExpectedReturns.ReturnsFromPrices(Matrix.FromRows(prices));

        (var methodRow, _method) = Ui.Combo("Method", Methods);
        (var riskRow, _risk) = Ui.Combo("Risk model", new[] { "Ledoit-Wolf shrinkage", "Sample covariance", "Exponential (span 180)" });
        (var maxRow, _maxWeight) = Ui.Slider("Max weight per asset", 0.15, 1, 0.40, ChartBase.Percent);
        (var targetRow, _target) = Ui.Slider("Target return (Efficient Return)", 0.04, 0.11, 0.07, ChartBase.Percent);
        (var viewRow, _view) = Ui.Slider("BL view: Tech beats US Large by", -0.05, 0.10, 0.03, ChartBase.Percent);
        foreach (var s in new[] { _maxWeight, _target, _view })
            s.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) Recalculate(); };
        _method.SelectionChanged += (_, _) => Recalculate();
        _risk.SelectionChanged += (_, _) => Recalculate();

        _ret = Ui.Stat("Expected return", "-");
        _vol = Ui.Stat("Volatility", "-");
        _sharpe = Ui.Stat("Sharpe (rf 2%)", "-");
        _div = Ui.Stat("Diversification ratio", "-");

        var controls = Ui.Card(Ui.Stack(14, methodRow, riskRow, maxRow, targetRow, viewRow,
            Ui.Label("Universe: five years of daily synthetic prices for eight asset classes with block correlations (equities, bonds, real assets).", 11.5, Palette.InkFaint)),
            "Optimiser", "PyPortfolioOpt-style API");
        var right = Ui.Stack(14,
            Ui.Columns(12, _ret, _vol, _sharpe, _div),
            Ui.Grid(14, new[] { "3*", "2*" }, new Control[] { Ui.Card(_frontier), Ui.Card(_corr) }),
            Ui.Columns(14, Ui.Card(_weights), Ui.Card(_contrib), Ui.Card(_allocation, "Discrete allocation", "$100,000 budget, greedy integer shares")));
        Recalculate();
        return Ui.Grid(18, new[] { "300", "*" }, new Control[] { controls, right });
    }

    private static int Block(int i) => i switch { < 4 => 0, < 6 => 1, _ => 2 };

    private void Recalculate()
    {
        if (_div is null) return;
        var mu = ExpectedReturns.MeanHistorical(_returns);
        var cov = _risk.SelectedIndex switch
        {
            1 => RiskModels.SampleCovariance(_returns),
            2 => RiskModels.ExponentialCovariance(_returns),
            _ => RiskModels.LedoitWolf(_returns),
        };
        double ub = Math.Max(_maxWeight.Value, 1.0 / Assets.Length + 1e-6);
        var mvo = new MeanVarianceOptimizer(mu, cov, 0, ub, Assets);
        const double rf = 0.02;

        PortfolioWeights w;
        string method = _method.SelectedItem as string ?? Methods[0];
        double[] usedMu = mu;
        try
        {
            w = method switch
            {
                "Min Volatility" => mvo.MinVolatility(),
                "Efficient Return" => mvo.EfficientReturn(Math.Min(_target.Value, mvo.MaxAttainableReturn - 1e-4)),
                "Black-Litterman" => BlackLitterman(cov, ub, out usedMu),
                "Risk Parity (ERC)" => RiskParity.EqualRiskContribution(cov, Assets),
                "Hierarchical Risk Parity" => HierarchicalRiskParity.Optimize(cov, Assets).Weights,
                "Min CVaR (95%)" => new CVaROptimizer(_returns, 0.95, 0, ub, Assets).MinCVaR(),
                "Max Diversification" => HeuristicPortfolios.MaxDiversification(cov, 0, ub, Assets),
                "Inverse Volatility" => HeuristicPortfolios.InverseVolatility(cov, Assets),
                "Equal Weight" => HeuristicPortfolios.EqualWeight(Assets.Length, Assets),
                _ => mvo.MaxSharpe(rf),
            };
        }
        catch (Exception ex)
        {
            _allocation.Content = Ui.Label(ex.Message, 12, Palette.Down);
            return;
        }

        var perf = PortfolioPerformance.Compute(w.AsSpan(), usedMu, cov, rf);
        Ui.SetStat(_ret, ChartBase.Percent2(perf.ExpectedReturn), method == "Black-Litterman" ? "posterior returns" : "historical mean");
        Ui.SetStat(_vol, ChartBase.Percent2(perf.Volatility));
        Ui.SetStat(_sharpe, perf.SharpeRatio.ToString("0.000"));
        Ui.SetStat(_div, HeuristicPortfolios.DiversificationRatio(w.AsSpan(), cov).ToString("0.00"), "Σwσ / σp");

        // Frontier, random portfolios and assets.
        var frontier = mvo.EfficientFrontier(40, rf);
        _frontier.Points.Clear();
        _frontier.Curves.Clear();
        _frontier.Highlights.Clear();
        var rng = new FastRandom(3);
        for (int k = 0; k < 900; k++)
        {
            var rw = new double[Assets.Length];
            double sum = 0;
            for (int i = 0; i < rw.Length; i++) { rw[i] = -Math.Log(1 - rng.NextDouble()); sum += rw[i]; }
            for (int i = 0; i < rw.Length; i++) rw[i] /= sum;
            var p = PortfolioPerformance.Compute(rw, mu, cov, rf);
            _frontier.Points.Add(new ScatterPoint(p.Volatility, p.ExpectedReturn, null, Palette.Diverging(Math.Clamp(p.SharpeRatio - 0.3, -1, 1)), 2.2));
        }
        _frontier.Curves.Add(new LineSeries("Frontier", frontier.Select(f => f.ExpectedReturn).ToArray(), Palette.GoldColor) { X = frontier.Select(f => f.Volatility).ToArray(), Thickness = 2.6 });
        for (int i = 0; i < Assets.Length; i++)
            _frontier.Highlights.Add(new ScatterPoint(Math.Sqrt(cov[i, i]), mu[i], Assets[i], Palette.InkSoftColor, 2));
        var chosen = PortfolioPerformance.Compute(w.AsSpan(), mu, cov, rf);
        _frontier.Highlights.Add(new ScatterPoint(chosen.Volatility, chosen.ExpectedReturn, method, Palette.TealColor, 5));
        _frontier.Refresh();

        _weights.Labels = Assets;
        _weights.Values = w.ToArray();
        _weights.Refresh();
        var rc = RiskParity.RiskContributions(w.AsSpan(), cov);
        double total = rc.Sum();
        _contrib.Labels = Assets;
        _contrib.Values = rc.Select(v => total > 0 ? v / total : 0).ToArray();
        _contrib.Refresh();

        var corrM = Stats.CovarianceToCorrelation(cov);
        _corr.Values = corrM.ToArray();
        _corr.RowLabels = Assets;
        _corr.ColumnLabels = new[] { "USL", "Tech", "EU", "EM", "Gov", "Corp", "Gold", "REIT" };
        _corr.Refresh();

        var alloc = DiscreteAllocation.Greedy(w, _lastPrices, 100_000);
        _allocation.Content = Ui.Stack(8,
            Ui.DataTable(new[] { "Asset", "Price", "Shares", "Value" },
                Assets.Select((a, i) =>
                {
                    int sh = alloc.Shares.TryGetValue(a, out var s) ? s : 0;
                    return new[] { a, _lastPrices[i].ToString("0.00"), sh.ToString(), (sh * _lastPrices[i]).ToString("N0") };
                })),
            Ui.Label($"Leftover cash: ${alloc.Leftover:N2}", 12, Palette.InkSoft));
    }

    private PortfolioWeights BlackLitterman(Matrix cov, double ub, out double[] posterior)
    {
        var caps = new[] { 40.0, 25, 15, 10, 30, 20, 8, 6 };
        var prior = BlackLittermanModel.MarketImpliedPriorReturns(caps, cov, riskAversion: 2.5, riskFreeRate: 0.02);
        var views = new BlackLittermanViews(Assets)
            .Relative("US Tech", "US Large", _view.Value, 0.6)
            .Absolute("Gold", 0.06, 0.4);
        var bl = BlackLittermanModel.Create(cov, prior, views);
        posterior = bl.Posterior;
        return new MeanVarianceOptimizer(bl.Posterior, bl.PosteriorCovariance, 0, ub, Assets).MaxSharpe(0.02);
    }
}
