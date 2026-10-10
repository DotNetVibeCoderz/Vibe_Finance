using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Finance.Gallery.Controls;
using Finance.Gallery.Shell;
using Finance.Pricing.Options;

namespace Finance.Gallery.Pages;

/// <summary>Black-Scholes-Merton price and Greeks, compared with lattice and Monte Carlo engines.</summary>
public sealed class GreeksExplorerPage : ModulePage
{
    public override string Title => "Greeks Explorer";
    public override string Subtitle => "Black-Scholes-Merton price and sensitivities, cross-checked against CRR, trinomial and Monte Carlo engines (Finance.Pricing).";
    public override string Glyph => "Δ";
    public override string Group => "Quant";

    private ComboBox _type = null!, _greek = null!;
    private Slider _spot = null!, _strike = null!, _days = null!, _vol = null!, _rate = null!, _div = null!;
    private Border _price = null!, _delta = null!, _gamma = null!, _vega = null!, _theta = null!, _rho = null!;
    private readonly LineChart _valueChart = new() { Height = 300, Title = "Value vs spot" };
    private readonly LineChart _greekChart = new() { Height = 300 };
    private readonly ContentControl _engines = new();
    private readonly LineChart _surface = new() { Height = 260, Title = "Value vs time to expiry (ATM, ±10%)" };

    protected override Control Build()
    {
        (var typeRow, _type) = Ui.Combo("Option", new[] { "Call", "Put" });
        (var greekRow, _greek) = Ui.Combo("Greek profile", new[] { "Delta", "Gamma", "Vega", "Theta", "Vanna", "Volga", "Charm" });
        (var spotRow, _spot) = Ui.Slider("Spot", 50, 150, 100, v => v.ToString("0.00"));
        (var strikeRow, _strike) = Ui.Slider("Strike", 50, 150, 100, v => v.ToString("0.00"));
        (var daysRow, _days) = Ui.Slider("Days to expiry", 1, 730, 182, v => $"{v:0} d", 1);
        (var volRow, _vol) = Ui.Slider("Volatility", 0.05, 1.0, 0.25, ChartBase.Percent);
        (var rateRow, _rate) = Ui.Slider("Risk-free rate", 0, 0.12, 0.04, v => (v * 100).ToString("0.00") + "%");
        (var divRow, _div) = Ui.Slider("Dividend yield", 0, 0.08, 0.01, v => (v * 100).ToString("0.00") + "%");

        foreach (var s in new[] { _spot, _strike, _days, _vol, _rate, _div })
            s.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) Recalculate(); };
        _type.SelectionChanged += (_, _) => Recalculate();
        _greek.SelectionChanged += (_, _) => Recalculate();

        _price = Ui.Stat("Price", "-");
        _delta = Ui.Stat("Delta", "-");
        _gamma = Ui.Stat("Gamma", "-");
        _vega = Ui.Stat("Vega / vol pt", "-");
        _theta = Ui.Stat("Theta / day", "-");
        _rho = Ui.Stat("Rho / 1%", "-");

        var controls = Ui.Card(Ui.Stack(14, typeRow, spotRow, strikeRow, daysRow, volRow, rateRow, divRow, greekRow), "Contract", "Drag a slider — everything re-prices instantly.");
        var right = Ui.Stack(14,
            Ui.Columns(12, _price, _delta, _gamma, _vega, _theta, _rho),
            Ui.Columns(14, Ui.Card(_valueChart), Ui.Card(_greekChart)),
            Ui.Columns(14, Ui.Card(_engines, "Engine comparison", "Same contract, four pricing methods"), Ui.Card(_surface)));

        Recalculate();
        return Ui.Grid(18, new[] { "300", "*" }, new Control[] { controls, right });
    }

    private void Recalculate()
    {
        if (_rho is null) return;
        var type = _type.SelectedIndex == 1 ? OptionType.Put : OptionType.Call;
        double s = _spot.Value, k = _strike.Value, t = _days.Value / 365.0, v = _vol.Value, r = _rate.Value, q = _div.Value;
        var g = BlackScholes.Greeks(type, s, k, t, r, q, v);

        Ui.SetStat(_price, g.Price.ToString("0.0000"));
        Ui.SetStat(_delta, g.Delta.ToString("0.0000"));
        Ui.SetStat(_gamma, g.Gamma.ToString("0.00000"));
        Ui.SetStat(_vega, g.VegaPerPoint.ToString("0.0000"));
        Ui.SetStat(_theta, g.ThetaPerDay.ToString("0.0000"));
        Ui.SetStat(_rho, (g.Rho / 100).ToString("0.0000"));

        // Value today vs payoff at expiry across spot.
        const int n = 121;
        var xs = new double[n];
        var today = new double[n];
        var payoff = new double[n];
        var greek = new double[n];
        string greekName = _greek.SelectedItem as string ?? "Delta";
        for (int i = 0; i < n; i++)
        {
            double x = k * (0.5 + i / (double)(n - 1));
            xs[i] = x;
            var gi = BlackScholes.Greeks(type, x, k, t, r, q, v);
            today[i] = gi.Price;
            payoff[i] = type == OptionType.Call ? Math.Max(0, x - k) : Math.Max(0, k - x);
            greek[i] = greekName switch
            {
                "Gamma" => gi.Gamma,
                "Vega" => gi.VegaPerPoint,
                "Theta" => gi.ThetaPerDay,
                "Vanna" => gi.Vanna,
                "Volga" => gi.Volga,
                "Charm" => gi.Charm,
                _ => gi.Delta,
            };
        }
        _valueChart.XFormat = x => x.ToString("0");
        _valueChart.SetSeries(
            new LineSeries("Value today", today, Palette.AccentColor) { X = xs, Fill = true, Thickness = 2.4 },
            new LineSeries("Payoff at expiry", payoff, Palette.GoldColor) { X = xs, Dashed = true });
        _greekChart.Title = $"{greekName} vs spot";
        _greekChart.XFormat = x => x.ToString("0");
        _greekChart.YFormat = y => y.ToString(Math.Abs(y) < 0.01 ? "0.0000" : "0.000");
        _greekChart.SetSeries(new LineSeries(greekName, greek, Palette.TealColor) { X = xs, Thickness = 2.4 });

        // Engines.
        var bin = LatticePricer.Binomial(type, ExerciseStyle.European, s, k, t, r, q, v, 800);
        var binAm = LatticePricer.Binomial(type, ExerciseStyle.American, s, k, t, r, q, v, 800);
        var tri = LatticePricer.Trinomial(type, ExerciseStyle.American, s, k, t, r, q, v, 400);
        var mc = new MonteCarloPricer(s, r, q, v, new MonteCarloSettings(Paths: 40_000, Steps: 1, Seed: 7)).European(type, k, t);
        _engines.Content = Ui.DataTable(new[] { "Engine", "Price", "vs BSM" }, new[]
        {
            new[] { "Black-Scholes-Merton (closed form)", g.Price.ToString("0.0000"), "—" },
            new[] { "CRR binomial, 800 steps (European)", bin.ToString("0.0000"), (bin - g.Price).ToString("+0.0000;-0.0000") },
            new[] { "Monte Carlo, 40k antithetic paths", $"{mc.Price:0.0000} ± {mc.StandardError:0.0000}", (mc.Price - g.Price).ToString("+0.0000;-0.0000") },
            new[] { "CRR binomial (American)", binAm.ToString("0.0000"), (binAm - g.Price).ToString("+0.0000;-0.0000") },
            new[] { "Trinomial, 400 steps (American)", tri.ToString("0.0000"), (tri - g.Price).ToString("+0.0000;-0.0000") },
        });

        // Time decay for three moneyness levels.
        int m = 80;
        var days = new double[m];
        var series = new[] { 0.9, 1.0, 1.1 }.Select(f => new double[m]).ToArray();
        for (int i = 0; i < m; i++)
        {
            double tt = Math.Max(1, 365 * (1 - i / (double)(m - 1))) / 365.0;
            days[i] = tt * 365;
            series[0][i] = BlackScholes.Price(type, k * 0.9, k, tt, r, q, v);
            series[1][i] = BlackScholes.Price(type, k, k, tt, r, q, v);
            series[2][i] = BlackScholes.Price(type, k * 1.1, k, tt, r, q, v);
        }
        _surface.XFormat = x => $"{x:0}d";
        _surface.SetSeries(
            new LineSeries("Spot −10%", series[0], Palette.PinkColor) { X = days },
            new LineSeries("ATM", series[1], Palette.AccentColor) { X = days },
            new LineSeries("Spot +10%", series[2], Palette.TealColor) { X = days });
    }
}
