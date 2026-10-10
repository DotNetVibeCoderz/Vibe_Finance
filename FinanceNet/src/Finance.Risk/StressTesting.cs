namespace Finance.Risk;

/// <summary>Standard risk-factor names used by the built-in scenarios.</summary>
public static class RiskFactors
{
    /// <summary>Equity index return (−0.30 = −30%).</summary>
    public const string Equity = "Equity";
    /// <summary>Parallel change in interest rates (0.01 = +100bp).</summary>
    public const string Rates = "Rates";
    /// <summary>Change in credit spreads (0.01 = +100bp).</summary>
    public const string Credit = "Credit";
    /// <summary>Return of the US dollar against other currencies.</summary>
    public const string Fx = "FX";
    /// <summary>Broad commodity return.</summary>
    public const string Commodity = "Commodity";
    /// <summary>Change in implied volatility (0.10 = +10 vol points).</summary>
    public const string Volatility = "Volatility";
    /// <summary>Crypto asset return.</summary>
    public const string Crypto = "Crypto";
}

/// <summary>A set of simultaneous factor shocks.</summary>
public sealed record StressScenario(string Name, IReadOnlyDictionary<string, double> Shocks, string Description = "")
{
    /// <summary>Shock for a factor (0 when the scenario does not move it).</summary>
    public double Shock(string factor) => Shocks.TryGetValue(factor, out var v) ? v : 0;

    /// <summary>
    /// Stylised historical scenarios. Magnitudes are rounded peak-to-trough moves of representative
    /// market benchmarks (S&amp;P 500, US 10y Treasury, US IG/HY spreads, DXY, broad commodities, VIX),
    /// intended for illustrative stress testing rather than regulatory use.
    /// </summary>
    public static IReadOnlyList<StressScenario> Historical { get; } = new[]
    {
        new StressScenario("Black Monday 1987", new Dictionary<string, double>
            { [RiskFactors.Equity] = -0.226, [RiskFactors.Rates] = -0.005, [RiskFactors.Volatility] = 0.80 },
            "19 Oct 1987 one-day equity crash."),
        new StressScenario("Dot-com Bust 2000-02", new Dictionary<string, double>
            { [RiskFactors.Equity] = -0.49, [RiskFactors.Rates] = -0.02, [RiskFactors.Credit] = 0.02, [RiskFactors.Volatility] = 0.20 },
            "S&P 500 peak-to-trough Mar 2000 – Oct 2002."),
        new StressScenario("Global Financial Crisis 2008", new Dictionary<string, double>
            { [RiskFactors.Equity] = -0.50, [RiskFactors.Rates] = -0.02, [RiskFactors.Credit] = 0.045, [RiskFactors.Fx] = 0.15,
              [RiskFactors.Commodity] = -0.55, [RiskFactors.Volatility] = 0.55 },
            "Lehman collapse; equities halve, spreads blow out, flight to quality."),
        new StressScenario("Taper Tantrum 2013", new Dictionary<string, double>
            { [RiskFactors.Equity] = -0.06, [RiskFactors.Rates] = 0.01, [RiskFactors.Credit] = 0.005, [RiskFactors.Fx] = 0.03 },
            "May–Sep 2013 rates sell-off."),
        new StressScenario("COVID-19 Crash 2020", new Dictionary<string, double>
            { [RiskFactors.Equity] = -0.34, [RiskFactors.Rates] = -0.015, [RiskFactors.Credit] = 0.025, [RiskFactors.Fx] = 0.05,
              [RiskFactors.Commodity] = -0.45, [RiskFactors.Volatility] = 0.65, [RiskFactors.Crypto] = -0.50 },
            "Feb–Mar 2020 pandemic sell-off."),
        new StressScenario("Rates Shock 2022", new Dictionary<string, double>
            { [RiskFactors.Equity] = -0.25, [RiskFactors.Rates] = 0.025, [RiskFactors.Credit] = 0.012, [RiskFactors.Fx] = 0.15,
              [RiskFactors.Commodity] = 0.20, [RiskFactors.Volatility] = 0.15, [RiskFactors.Crypto] = -0.65 },
            "2022 inflation and central-bank tightening: stocks and bonds fall together."),
    };

    /// <summary>A custom scenario.</summary>
    public static StressScenario Custom(string name, params (string Factor, double Shock)[] shocks)
        => new(name, shocks.ToDictionary(s => s.Factor, s => s.Shock));
}

/// <summary>
/// A position described by its market value and factor sensitivities. P&amp;L for a factor shock Δf is
/// MV · (s₁ Δf + ½ s₂ Δf²), where s₁ is a first-order sensitivity per unit of market value (equity beta,
/// or −duration for rates/credit) and s₂ an optional second-order term (convexity, option gamma).
/// </summary>
public sealed record StressPosition(string Name, double MarketValue, IReadOnlyDictionary<string, double> Sensitivities,
    IReadOnlyDictionary<string, double>? Convexities = null)
{
    /// <summary>Equity position with a beta to the equity factor.</summary>
    public static StressPosition Equity(string name, double marketValue, double beta = 1.0)
        => new(name, marketValue, new Dictionary<string, double> { [RiskFactors.Equity] = beta });

    /// <summary>Bond position with rate and spread durations and convexity.</summary>
    public static StressPosition Bond(string name, double marketValue, double modifiedDuration, double spreadDuration = 0, double convexity = 0)
        => new(name, marketValue,
            new Dictionary<string, double> { [RiskFactors.Rates] = -modifiedDuration, [RiskFactors.Credit] = -spreadDuration },
            new Dictionary<string, double> { [RiskFactors.Rates] = convexity });

    /// <summary>Position with an arbitrary single-factor exposure.</summary>
    public static StressPosition Single(string name, double marketValue, string factor, double sensitivity = 1.0)
        => new(name, marketValue, new Dictionary<string, double> { [factor] = sensitivity });

    /// <summary>P&amp;L under a scenario.</summary>
    public double ProfitAndLoss(StressScenario scenario)
    {
        double pnl = 0;
        foreach (var (factor, s1) in Sensitivities)
        {
            double d = scenario.Shock(factor);
            pnl += s1 * d;
            if (Convexities is not null && Convexities.TryGetValue(factor, out var s2)) pnl += 0.5 * s2 * d * d;
        }
        return MarketValue * pnl;
    }
}

/// <summary>Stress result for one scenario.</summary>
public sealed record StressResult(string Scenario, double TotalPnl, double ReturnOnPortfolio, IReadOnlyDictionary<string, double> PositionPnl);

/// <summary>Applies scenarios to a portfolio of <see cref="StressPosition"/>s.</summary>
public static class StressTester
{
    /// <summary>Runs one scenario.</summary>
    public static StressResult Run(IReadOnlyList<StressPosition> positions, StressScenario scenario)
    {
        var byPos = new Dictionary<string, double>();
        double total = 0, value = 0;
        foreach (var p in positions)
        {
            double pnl = p.ProfitAndLoss(scenario);
            byPos[p.Name] = byPos.TryGetValue(p.Name, out var prior) ? prior + pnl : pnl;
            total += pnl;
            value += p.MarketValue;
        }
        return new StressResult(scenario.Name, total, value != 0 ? total / Math.Abs(value) : 0, byPos);
    }

    /// <summary>Runs every scenario (the built-in historical set by default), worst first.</summary>
    public static IReadOnlyList<StressResult> RunAll(IReadOnlyList<StressPosition> positions, IEnumerable<StressScenario>? scenarios = null)
        => (scenarios ?? StressScenario.Historical).Select(s => Run(positions, s)).OrderBy(r => r.TotalPnl).ToList();

    /// <summary>
    /// Reverse stress test: scales <paramref name="scenario"/> by the factor k so that the portfolio loses
    /// <paramref name="targetLoss"/> (positive currency amount). Linear positions only (convexity ignored).
    /// </summary>
    public static double ReverseStressMultiplier(IReadOnlyList<StressPosition> positions, StressScenario scenario, double targetLoss)
    {
        double basePnl = positions.Sum(p => p.MarketValue * p.Sensitivities.Sum(kv => kv.Value * scenario.Shock(kv.Key)));
        if (basePnl >= 0) return double.PositiveInfinity;
        return targetLoss / -basePnl;
    }
}
