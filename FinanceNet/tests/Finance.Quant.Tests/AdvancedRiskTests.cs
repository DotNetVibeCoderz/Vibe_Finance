using Finance.Core;
using Finance.Mathematics;
using Finance.Risk;
using Xunit;

namespace Finance.Quant.Tests;

/// <summary>
/// v0.2 risk models. Each check uses a property that holds by theory: GPD threshold stability, the exact
/// tail of an exponential, the homogeneity of VaR, liquidation-schedule algebra, or FRTB values worked out
/// by hand from the MAR21 formulas.
/// </summary>
public class AdvancedRiskTests
{
    private static double[] GpdLosses(int n, double xi, double beta, ulong seed)
    {
        var rng = new FastRandom(seed);
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = beta / xi * (Math.Pow(1 - rng.NextDouble(), -xi) - 1); // inverse CDF
        return x;
    }

    [Fact]
    public void GpdFitRecoversShapeAndThresholdStableScale()
    {
        // Excesses of a GPD(ξ, β) over u are GPD(ξ, β + ξu): same shape, a known larger scale.
        const double xi = 0.3, beta = 0.01;
        var losses = GpdLosses(40_000, xi, beta, seed: 5);
        var fit = AdvancedVar.FitTail(losses.Select(l => -l).ToArray(), thresholdQuantile: 0.5);
        Assert.InRange(fit.Xi, xi - 0.05, xi + 0.05);
        double expectedBeta = beta + xi * fit.Threshold;
        Assert.InRange(fit.Beta, expectedBeta * 0.92, expectedBeta * 1.08);
        Assert.Equal(20_000, fit.Exceedances, 2.0);
    }

    [Fact]
    public void ExtremeValueVarMatchesTheExponentialTail()
    {
        // Exponential losses with mean 1%: VaR_p = −ln(1 − p)·1%, ES_p = VaR_p + 1% (memorylessness).
        var rng = new FastRandom(9);
        var returns = new double[100_000];
        for (int i = 0; i < returns.Length; i++) returns[i] = 0.01 * Math.Log(1 - rng.NextDouble());
        var v = AdvancedVar.ExtremeValue(returns, 0.999, thresholdQuantile: 0.95);
        double exact = -Math.Log(0.001) * 0.01;
        Assert.InRange(v.ValueAtRisk, exact * 0.97, exact * 1.03);
        Assert.InRange(v.ExpectedShortfall, (exact + 0.01) * 0.96, (exact + 0.01) * 1.04);
        Assert.Equal(VarMethod.ExtremeValue, v.Method);
    }

    [Fact]
    public void GpdTailFormulasHandValues()
    {
        // u = 2%, ξ = 0.25, β = 1%, 50 of 1,000 above u, p = 99%: tail ratio (1000/50)·0.01 = 0.2.
        var fit = new GpdTailFit(0.02, 0.25, 0.01, 50, 1000);
        Assert.Equal(0.02 + 0.04 * (1.4953487812212205 - 1), fit.ValueAtRisk(0.99), 12);
        Assert.Equal(0.039813951248849 / 0.75 + 0.005 / 0.75, fit.ExpectedShortfall(0.99), 10);
    }

    [Fact]
    public void FilteredHistoricalEwmaMatchesIndependentFilter()
    {
        var r = VolatilityModels.SimulateGarch11(600, 0, 2e-6, 0.08, 0.9, seed: 3);
        var fhs = AdvancedVar.FilteredHistorical(r, 0.99, VolatilityFilter.Ewma, 0.94);

        double v = r[0] * r[0];
        var scen = new double[r.Length];
        var z = new double[r.Length];
        for (int t = 0; t < r.Length; t++)
        {
            if (t > 0) v = 0.94 * v + 0.06 * r[t - 1] * r[t - 1];
            z[t] = r[t] / Math.Sqrt(v);
        }
        double next = Math.Sqrt(0.94 * v + 0.06 * r[^1] * r[^1]);
        for (int t = 0; t < r.Length; t++) scen[t] = next * z[t];
        var expected = ValueAtRisk.Historical(scen, 0.99);

        Assert.Equal(next, fhs.CurrentVolatility, 12);
        Assert.Equal(expected.ValueAtRisk, fhs.Estimate.ValueAtRisk, 12);
        Assert.Equal(expected.ExpectedShortfall, fhs.Estimate.ExpectedShortfall, 12);
        Assert.Equal(VarMethod.FilteredHistorical, fhs.Estimate.Method);
    }

    [Fact]
    public void FilteredHistoricalIsPositivelyHomogeneous()
    {
        var r = VolatilityModels.SimulateGarch11(500, 0, 2e-6, 0.08, 0.9, seed: 4);
        var scaled = r.Select(x => 3 * x).ToArray();
        var a = AdvancedVar.FilteredHistorical(r, 0.99, VolatilityFilter.Ewma).Estimate;
        var b = AdvancedVar.FilteredHistorical(scaled, 0.99, VolatilityFilter.Ewma).Estimate;
        Assert.Equal(3 * a.ValueAtRisk, b.ValueAtRisk, 12);
    }

    [Fact]
    public void FilteredHistoricalGarchReactsToTheCurrentVolatility()
    {
        // A calm history ending in a burst: plain HS barely moves, FHS scales to today's volatility.
        var calm = VolatilityModels.SimulateGarch11(1500, 0, 1e-6, 0.08, 0.9, seed: 7).ToList();
        var rng = new FastRandom(8);
        for (int i = 0; i < 10; i++) calm.Add(0.04 * rng.NextGaussian());
        var r = calm.ToArray();
        var hs = ValueAtRisk.Historical(r, 0.99);
        var fhs = AdvancedVar.FilteredHistorical(r, 0.99);
        Assert.True(fhs.Estimate.ValueAtRisk > 1.5 * hs.ValueAtRisk, $"FHS {fhs.Estimate.ValueAtRisk:F4} vs HS {hs.ValueAtRisk:F4}");
    }

    [Fact]
    public void MultiPeriodFhsScalesRoughlyWithSquareRootOfTime()
    {
        var r = VolatilityModels.SimulateGarch11(2000, 0, 4e-6, 0.03, 0.95, seed: 12);
        var one = AdvancedVar.FilteredHistoricalMultiPeriod(r, 1, 0.99, 40_000);
        var single = AdvancedVar.FilteredHistorical(r, 0.99).Estimate;
        Assert.InRange(one.ValueAtRisk, single.ValueAtRisk * 0.9, single.ValueAtRisk * 1.1);
        var ten = AdvancedVar.FilteredHistoricalMultiPeriod(r, 10, 0.99, 40_000);
        Assert.InRange(ten.ValueAtRisk / one.ValueAtRisk, Math.Sqrt(10) * 0.75, Math.Sqrt(10) * 1.25);
    }

    [Fact]
    public void LiquidityAdjustmentsHandValues()
    {
        Assert.Equal(0.02 + 0.5 * (0.004 + 3 * 0.001), AdvancedVar.LiquidityAdjusted(0.02, 0.004, 0.001), 15);
        Assert.Equal(1.0, AdvancedVar.OrderlyLiquidation(1.0, 1), 15);                   // one day: no change
        Assert.Equal(Math.Sqrt(11.0 * 21 / 60), AdvancedVar.OrderlyLiquidation(1.0, 10), 15);
        Assert.True(AdvancedVar.OrderlyLiquidation(1.0, 10) < Math.Sqrt(10));             // less than holding it all
    }

    private static Matrix Scenarios(int n, ulong seed)
    {
        var rng = new FastRandom(seed);
        var m = new Matrix(n, 3);
        for (int s = 0; s < n; s++)
        {
            double f = rng.NextGaussian();
            m[s, 0] = 0.010 * f + 0.004 * rng.NextGaussian();
            m[s, 1] = 0.015 * f + 0.010 * rng.NextGaussian();
            m[s, 2] = 0.020 * rng.NextGaussian();
        }
        return m;
    }

    [Fact]
    public void HistoricalAttributionIsConsistent()
    {
        var sc = Scenarios(5000, 21);
        double[] w = { 0.5, 0.3, 0.2 };
        var total = AdvancedVar.PortfolioHistorical(w, sc, 0.99).ValueAtRisk;

        // Doubling every position doubles VaR, so the incremental VaR of adding w is exactly VaR(w).
        Assert.Equal(total, AdvancedVar.IncrementalHistorical(w, w, sc, 0.99), 12);

        // Components add up to the loss of the scenarios around the quantile, which is close to VaR.
        var comp = AdvancedVar.ComponentHistorical(w, sc, 0.99, window: 1);
        Assert.InRange(comp.Sum(), total * 0.97, total * 1.03);
    }

    [Fact]
    public void MarginalIncrementalMatchesFullRevaluationForSmallTrades()
    {
        var cov = Matrix.FromRows(new[] { new[] { 0.04, 0.006, 0.0 }, new[] { 0.006, 0.09, 0.01 }, new[] { 0.0, 0.01, 0.0625 } });
        double[] w = { 0.5, 0.3, 0.2 }, d = { 0.01, -0.02, 0.005 };
        double Error(double scale)
        {
            var ds = d.Select(x => x * scale).ToArray();
            return Math.Abs(VarAttribution.Incremental(w, ds, cov, 0.99) - AdvancedVar.IncrementalMarginal(w, ds, cov, 0.99));
        }
        // A first-order (Euler) approximation is off by a second-order term: a 10x smaller trade, ~100x smaller error.
        double e1 = Error(1), e2 = Error(0.1);
        Assert.True(e1 > 0 && e2 < e1 / 50, $"errors {e1:E2} and {e2:E2} do not shrink quadratically");
        Assert.True(Error(0.01) < 1e-3 * Math.Abs(VarAttribution.Incremental(w, d.Select(x => x * 0.01).ToArray(), cov, 0.99)));
    }

    // ---------------- FRTB-SA ----------------

    private static double Charge(double ws1, double ws2, double rho)
        => Math.Sqrt(Math.Max(0, ws1 * ws1 + ws2 * ws2 + 2 * rho * ws1 * ws2));

    [Fact]
    public void GirrSingleCurrencyHandComputed()
    {
        // USD is a specified currency: risk weights divided by √2. 1y: 1.6%, 5y: 1.1%.
        var s = new[] { new GirrSensitivity("USD", 1, 1_000_000), new GirrSensitivity("USD", 5, -400_000) };
        double ws1 = 0.016 / Math.Sqrt(2) * 1_000_000, ws2 = 0.011 / Math.Sqrt(2) * -400_000;
        double rho = Math.Exp(-0.03 * 4 / 1);                                      // 0.8869, above the 40% floor
        double medium = Charge(ws1, ws2, rho);
        double high = Charge(ws1, ws2, Math.Min(1.25 * rho, 1));
        double low = Charge(ws1, ws2, Math.Max(2 * rho - 1, 0.75 * rho));

        var r = FrtbSa.GirrDelta(s);
        Assert.Equal(medium, r.ByScenario[FrtbCorrelationScenario.Medium], 6);
        Assert.Equal(high, r.ByScenario[FrtbCorrelationScenario.High], 6);
        Assert.Equal(low, r.ByScenario[FrtbCorrelationScenario.Low], 6);
        Assert.Equal(Math.Max(medium, Math.Max(high, low)), r.Charge, 6);
        Assert.Equal(FrtbCorrelationScenario.Low, r.Binding);   // offsetting positions: lower correlation hurts
    }

    [Fact]
    public void GirrCorrelationFloorAndRiskWeights()
    {
        Assert.Equal(0.40, FrtbSa.GirrCorrelation(0.25, 30), 15);               // exp(−0.03·119) is far below 40%
        Assert.Equal(Math.Exp(-0.03 * 1 / 1), FrtbSa.GirrCorrelation(1, 2), 15);           // |1 − 2| / min(1, 2) = 1
        Assert.Equal(0.999 * Math.Exp(-0.03), FrtbSa.GirrCorrelation(1, 2, sameCurve: false), 15);
        Assert.Equal(Math.Exp(-0.03 * 5 / 5), FrtbSa.GirrCorrelation(10, 5), 15);
        Assert.Equal(0.017, FrtbSa.GirrRiskWeight(0.25));
        Assert.Equal(0.013, FrtbSa.GirrRiskWeight(2));
        Assert.Equal(0.011, FrtbSa.GirrRiskWeight(30));
    }

    [Fact]
    public void GirrAcrossCurrenciesAndNetting()
    {
        // BRL is not specified (full risk weight); two BRL lines on one vertex net to a single sensitivity.
        var s = new[]
        {
            new GirrSensitivity("EUR", 2, 500_000),
            new GirrSensitivity("BRL", 10, -300_000), new GirrSensitivity("brl", 10, 100_000),
        };
        double eur = 0.013 / Math.Sqrt(2) * 500_000, brl = 0.011 * -200_000;
        double Total(double g) => Math.Sqrt(eur * eur + brl * brl + 2 * g * eur * brl);
        var r = FrtbSa.GirrDelta(s);
        Assert.Equal(Total(0.5), r.ByScenario[FrtbCorrelationScenario.Medium], 6);
        Assert.Equal(Total(0.625), r.ByScenario[FrtbCorrelationScenario.High], 6);
        Assert.Equal(Total(0.375), r.ByScenario[FrtbCorrelationScenario.Low], 6);
        Assert.Equal(Math.Abs(brl), r.BucketCapital["BRL"], 6);
    }

    [Fact]
    public void AlternativeSpecificationCapsBucketSums()
    {
        // Within each bucket two same-sign sensitivities at ρ = 0 give K = √2·|ws| but S = 2·ws, so with γ = 1
        // and opposite-signed buckets the across-bucket sum goes negative; Sᵦ is then capped to ±Kᵦ.
        var rho = Matrix.Identity(2);
        var buckets = new List<(string, double[], Matrix)> { ("A", new[] { 1.0, 1.0 }, rho), ("B", new[] { -1.0, -1.0 }, rho) };
        var r = FrtbSa.Aggregate(buckets, (_, _) => 1.0);
        double k = Math.Sqrt(2);
        // Medium: 2 + 2 + 2·1·(2)(−2) = −4 < 0 → capped S = ±√2: 2 + 2 − 2·2 = 0.
        Assert.Equal(0.0, r.ByScenario[FrtbCorrelationScenario.Medium], 9);
        // Low: γ = max(2·1 − 1, 0.75) = 1 → same as medium.
        Assert.Equal(0.0, r.ByScenario[FrtbCorrelationScenario.Low], 9);
        Assert.Equal(k, r.BucketCapital["A"], 12);
    }
}
