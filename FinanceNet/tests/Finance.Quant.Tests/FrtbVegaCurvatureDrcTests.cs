using Finance.Mathematics;
using Finance.Risk;
using Xunit;

namespace Finance.Quant.Tests;

/// <summary>
/// FRTB-SA vega, curvature and default risk charge. Every expected value is worked out by hand from the MAR21 /
/// MAR22 formulas in the test itself, step by step, never taken from the implementation.
/// </summary>
public class FrtbVegaCurvatureDrcTests
{
    private const FrtbCorrelationScenario Low = FrtbCorrelationScenario.Low;
    private const FrtbCorrelationScenario Medium = FrtbCorrelationScenario.Medium;
    private const FrtbCorrelationScenario High = FrtbCorrelationScenario.High;

    private static double Two(double a, double b, double rho) => Math.Sqrt(Math.Max(0, a * a + b * b + 2 * rho * a * b));
    private static double Hi(double rho) => Math.Min(1.25 * rho, 1);
    private static double Lo(double rho) => Math.Max(2 * rho - 1, 0.75 * rho);

    // ---------------- Vega ----------------

    [Fact]
    public void VegaRiskWeightsFromLiquidityHorizons()
    {
        Assert.True(0.55 * Math.Sqrt(60 / 10.0) > 1);                                     // 1.347 → capped
        Assert.Equal(1.0, FrtbSa.VegaRiskWeight(FrtbVegaClass.Girr), 15);
        Assert.Equal(0.55 * Math.Sqrt(2), FrtbSa.VegaRiskWeight(FrtbVegaClass.EquityLargeCap), 15);   // LH 20: 77.78%
        Assert.Equal(1.0, FrtbSa.VegaRiskWeight(FrtbVegaClass.Fx), 15);                    // 0.55·2 = 1.1 → 100%
        Assert.Equal(1.0, FrtbSa.VegaRiskWeight(FrtbVegaClass.Commodity), 15);
        Assert.Equal(60, FrtbSa.VegaLiquidityHorizon(FrtbVegaClass.Girr));
        Assert.Equal(40, FrtbSa.VegaLiquidityHorizon(FrtbVegaClass.Fx));
    }

    [Fact]
    public void GirrVegaCorrelationIsProductOfMaturityFactors()
    {
        // Option 1y vs 3y: |1−3|/1 = 2 → e^(−0.02); underlying 5y vs 10y: 5/5 = 1 → e^(−0.01).
        Assert.Equal(Math.Exp(-0.02) * Math.Exp(-0.01), FrtbSa.GirrVegaCorrelation(1, 3, 5, 10), 15);
        Assert.Equal(1.0, FrtbSa.GirrVegaCorrelation(5, 5, 1, 1), 15);
        Assert.Equal(0.6 * Math.Exp(-0.01 * 9 / 1), FrtbSa.VegaCorrelation(0.6, 1, 10), 15);   // other classes: ρ(delta)·ρ(T)
    }

    [Fact]
    public void GirrVegaSingleCurrencyHandComputed()
    {
        var s = new[] { new GirrVegaSensitivity("USD", 1, 5, 200_000), new GirrVegaSensitivity("USD", 3, 10, -150_000) };
        double ws1 = 1.0 * 200_000, ws2 = 1.0 * -150_000;          // RW = 100%
        double rho = Math.Exp(-0.01 * 2 / 1) * Math.Exp(-0.01 * 5 / 5);   // e^(−0.03) = 0.970446
        var r = FrtbSa.GirrVega(s);
        Assert.Equal(Two(ws1, ws2, rho), r.ByScenario[Medium], 6);
        Assert.Equal(Two(ws1, ws2, Hi(rho)), r.ByScenario[High], 6);          // min(1.213, 1) = 1 → 50 000
        Assert.Equal(50_000, r.ByScenario[High], 6);
        Assert.Equal(Two(ws1, ws2, Lo(rho)), r.ByScenario[Low], 6);
        Assert.Equal(Low, r.Binding);                                        // offsetting: lower correlation hurts
    }

    [Fact]
    public void GirrVegaAcrossCurrencies()
    {
        var s = new[] { new GirrVegaSensitivity("EUR", 0.5, 1, 80_000), new GirrVegaSensitivity("JPY", 5, 10, 60_000) };
        var r = FrtbSa.GirrVega(s);
        Assert.Equal(Two(80_000, 60_000, 0.5), r.ByScenario[Medium], 6);
        Assert.Equal(Two(80_000, 60_000, 0.625), r.ByScenario[High], 6);
        Assert.Equal(Two(80_000, 60_000, 0.375), r.ByScenario[Low], 6);
        Assert.Equal(High, r.Binding);                                       // same sign: higher correlation hurts
        Assert.Equal(80_000, r.BucketCapital["EUR"], 6);
    }

    [Fact]
    public void GirrVegaLimitingCases()
    {
        // A single sensitivity gives |RW·s| = |s| under every scenario.
        var one = FrtbSa.GirrVega(new[] { new GirrVegaSensitivity("GBP", 1, 1, -12_345) });
        foreach (var sc in Enum.GetValues<FrtbCorrelationScenario>()) Assert.Equal(12_345, one.ByScenario[sc], 9);
        // Two opposite sensitivities on the same vertex net to zero.
        var flat = FrtbSa.GirrVega(new[] { new GirrVegaSensitivity("GBP", 3, 5, 7_000), new GirrVegaSensitivity("gbp", 3, 5, -7_000) });
        Assert.Equal(0.0, flat.Charge, 12);
    }

    // ---------------- Curvature ----------------

    [Fact]
    public void CurvatureExposureDefinition()
    {
        // CVR⁺ = −(ΔV⁺ − RW·s), CVR⁻ = −(ΔV⁻ + RW·s). A linear instrument (ΔV± = ±RW·s) has zero curvature.
        var (up, down) = FrtbSa.CurvatureExposure(0.017 * 1000, -0.017 * 1000, 0.017, 1000);
        Assert.Equal(0.0, up, 12);
        Assert.Equal(0.0, down, 12);
        var (u2, d2) = FrtbSa.CurvatureExposure(-30_000, 50_000, 0.017, -1_000_000);
        Assert.Equal(13_000, u2, 9);      // −(−30 000 + 17 000)
        Assert.Equal(-33_000, d2, 9);     // −(50 000 − 17 000)
    }

    [Fact]
    public void GirrCurvatureTwoCurrenciesHandComputed()
    {
        var s = new[]
        {
            new GirrCurvatureSensitivity("USD", -30_000, 50_000, -1_000_000),   // CVR⁺ = 13 000, CVR⁻ = −33 000
            new GirrCurvatureSensitivity("EUR", 10_000, -2_000, 400_000),       // RW·s = 6 800: CVR⁺ = −3 200, CVR⁻ = −4 800
        };
        // USD: K⁺ = 13 000, K⁻ = max(−33 000, 0) = 0 → up, S = 13 000.
        // EUR: both negative → K⁺ = K⁻ = 0; tie broken by the larger ΣCVR (−3 200 > −4 800) → up, S = −3 200.
        double kUsd = 13_000, sUsd = 13_000, sEur = -3_200;
        double Total(double g) => Math.Sqrt(kUsd * kUsd + 0 + 2 * g * sUsd * sEur);   // ψ(+,−) = 1
        double gamma = 0.5 * 0.5;                                                       // squared delta γ
        var r = FrtbSa.GirrCurvature(s);
        Assert.Equal(Total(gamma), r.ByScenario[Medium], 6);                  // √148.2e6 = 12 173.74
        Assert.Equal(Total(Hi(gamma)), r.ByScenario[High], 6);                // γ = 0.3125
        Assert.Equal(Total(Lo(gamma)), r.ByScenario[Low], 6);                 // γ = max(−0.5, 0.1875)
        Assert.Equal(Math.Sqrt(153.4e6), r.Charge, 6);
        Assert.Equal(Low, r.Binding);
        Assert.Equal(FrtbCurvatureShock.Up, r.Selected["USD"]);
        Assert.Equal(FrtbCurvatureShock.Up, r.Selected["EUR"]);
        Assert.Equal(0.0, r.BucketCapital["EUR"], 12);
    }

    [Fact]
    public void GirrCurvatureSingleFactorLimits()
    {
        // One currency: charge = max(CVR⁺, CVR⁻, 0). CVR⁺ = −(−5 000 − 0.017·100 000) = 6 700, CVR⁻ = −(−3 000 + 1 700) = 1 300.
        var r = FrtbSa.GirrCurvature(new[] { new GirrCurvatureSensitivity("USD", -5_000, -3_000, 100_000) });
        Assert.Equal(6_700, r.Charge, 9);
        Assert.Equal(FrtbCurvatureShock.Up, r.Selected["USD"]);
        // Both CVRs negative (long convexity): ψ = 0 path and the floor give zero.
        var neg = FrtbSa.GirrCurvature(new[] { new GirrCurvatureSensitivity("USD", 4_000, 1_000, 100_000) });   // CVR⁺ = −2 300, CVR⁻ = −2 700
        Assert.Equal(0.0, neg.Charge, 12);
        Assert.Equal(FrtbCurvatureShock.Up, neg.Selected["USD"]);              // tie at 0, −2 300 > −2 700
    }

    [Fact]
    public void CurvatureIntraBucketPsiAndFloors()
    {
        // One bucket, three risk factors, curvature correlation 0.5 between all of them.
        var rho = new Matrix(3, 3);
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) rho[i, j] = i == j ? 1 : 0.5;
        double[] up = { 6, -2, -3 }, down = { 1, 2, -4 };
        // Up, ρ: 6² + 2ρ[6·(−2) + 6·(−3)] + ψ(−2,−3)=0 → 36 − 60ρ.   Down: 1 + 4 + 2ρ[1·2 + 1·(−4) + 2·(−4)] = 5 − 20ρ.
        double KUp(double p) => Math.Sqrt(Math.Max(0, 36 - 60 * p));
        double KDown(double p) => Math.Sqrt(Math.Max(0, 5 - 20 * p));
        // Medium ρ = 0.5: K⁺ = √6, K⁻ = 0 (5 − 10 < 0) → √6.  High ρ = 0.625: both floored at 0, charge 0.
        // Low ρ = max(0, 0.375) = 0.375: K⁺ = √13.5, K⁻ = 0 → binding.
        var r = FrtbSa.AggregateCurvature(new[] { ("B", up, down, rho) }, (_, _) => 0);
        Assert.Equal(Math.Max(KUp(0.5), KDown(0.5)), r.ByScenario[Medium], 12);
        Assert.Equal(Math.Sqrt(6), r.ByScenario[Medium], 12);
        Assert.Equal(0.0, r.ByScenario[High], 12);
        Assert.Equal(Math.Sqrt(13.5), r.ByScenario[Low], 12);
        Assert.Equal(Math.Max(KUp(0.375), KDown(0.375)), r.Charge, 12);
        Assert.Equal(Low, r.Binding);
        // Without ψ the (−2, −3) pair would add 2·0.5·6 = 6: K⁺ would be √12, not √6.
        Assert.NotEqual(Math.Sqrt(12), r.ByScenario[Medium], 6);
    }

    [Fact]
    public void CurvatureAcrossBucketsNegativeSumIsCapped()
    {
        // A: both CVRs negative → K = 0, S = −100 (up wins the tie). B: K = S = 1.
        // 0 + 1 + 2·0.25·(−100)·1 = −49 < 0 → S_A capped to [−0, 0] → 1.
        var id = Matrix.Identity(1);
        var buckets = new[] { ("A", new[] { -100.0 }, new[] { -120.0 }, id), ("B", new[] { 1.0 }, new[] { -1.0 }, id) };
        var r = FrtbSa.AggregateCurvature(buckets, (_, _) => 0.25);
        foreach (var sc in Enum.GetValues<FrtbCorrelationScenario>()) Assert.Equal(1.0, r.ByScenario[sc], 12);
        // Two buckets with both S negative: ψ = 0 removes the cross term entirely.
        var both = new[] { ("A", new[] { -5.0 }, new[] { -6.0 }, id), ("B", new[] { -7.0 }, new[] { -8.0 }, id) };
        Assert.Equal(0.0, FrtbSa.AggregateCurvature(both, (_, _) => 0.25).Charge, 12);
    }

    [Fact]
    public void SensitivitiesBasedChargeTakesMaxOfScenarioSums()
    {
        var delta = new Dictionary<FrtbCorrelationScenario, double> { [Low] = 10, [Medium] = 12, [High] = 11 };
        var vega = new Dictionary<FrtbCorrelationScenario, double> { [Low] = 5, [Medium] = 2, [High] = 6 };
        var curv = new Dictionary<FrtbCorrelationScenario, double> { [Low] = 0, [Medium] = 1, [High] = 0 };
        // Low 15, Medium 15, High 17 → 17 (High), although Medium is the largest delta on its own.
        var r = FrtbSa.SensitivitiesBasedCharge(delta, vega, curv);
        Assert.Equal(17, r.Charge, 12);
        Assert.Equal(High, r.Binding);
        Assert.Equal(15, r.ByScenario[Medium], 12);
    }

    // ---------------- Default risk charge ----------------

    [Fact]
    public void DrcParameters()
    {
        Assert.Equal(1.0, FrtbDrc.Lgd(FrtbSeniority.Equity));
        Assert.Equal(1.0, FrtbDrc.Lgd(FrtbSeniority.NonSeniorDebt));
        Assert.Equal(0.75, FrtbDrc.Lgd(FrtbSeniority.SeniorDebt));
        Assert.Equal(0.25, FrtbDrc.Lgd(FrtbSeniority.CoveredBond));
        Assert.Equal(0.25, FrtbDrc.MaturityScale(0.1));
        Assert.Equal(0.5, FrtbDrc.MaturityScale(0.5));
        Assert.Equal(1.0, FrtbDrc.MaturityScale(7));
        Assert.Equal(0.005, FrtbDrc.RiskWeight(FrtbCreditQuality.AAA));
        Assert.Equal(0.06, FrtbDrc.RiskWeight(FrtbCreditQuality.BBB));
        Assert.Equal(0.15, FrtbDrc.RiskWeight(FrtbCreditQuality.Unrated));
        Assert.Equal(1.0, FrtbDrc.RiskWeight(FrtbCreditQuality.Defaulted));
        // Long senior bond 100 at 98: 0.75·100 + (98 − 100) = 73. Short of the same: 0.75·(−100) + 2 = −73.
        Assert.Equal(73, FrtbDrc.JumpToDefault(100, 98, 0.75), 12);
        Assert.Equal(-73, FrtbDrc.JumpToDefault(-100, -98, 0.75), 12);
        // A long whose market value is far below the recovery value cannot have negative JTD.
        Assert.Equal(0, FrtbDrc.JumpToDefault(100, 20, 0.75), 12);
    }

    [Fact]
    public void DrcHandComputedWithHbrAndSeniorityNetting()
    {
        var p = new[]
        {
            // X (A, 3%): long senior 1 000 000 at 1 020 000, 3y → 0.75·1e6 + 20 000 = 770 000.
            new DrcPosition("X", FrtbDrcBucket.Corporate, FrtbCreditQuality.A, FrtbSeniority.SeniorDebt, 1_000_000, 1_020_000, 3),
            // X: short equity 200 000, 3 months → −200 000·0.25 = −50 000; junior short offsets senior long.
            new DrcPosition("X", FrtbDrcBucket.Corporate, FrtbCreditQuality.A, FrtbSeniority.Equity, -200_000, -200_000, 0.25),
            // Y (BB, 15%): short senior −500 000 at −490 000, 6m → (−375 000 + 10 000)·0.5 = −182 500.
            new DrcPosition("Y", FrtbDrcBucket.Corporate, FrtbCreditQuality.BB, FrtbSeniority.SeniorDebt, -500_000, -490_000, 0.5),
            // Y: long covered bond 300 000, 2y → 0.25·300 000 = 75 000; more senior than the short → offsets.
            new DrcPosition("Y", FrtbDrcBucket.Corporate, FrtbCreditQuality.BB, FrtbSeniority.CoveredBond, 300_000, 300_000, 2),
            // Z (BBB, 6%): long non-senior 100 000 at 90 000, 0.1y → 90 000·0.25 = 22 500.
            new DrcPosition("Z", FrtbDrcBucket.Corporate, FrtbCreditQuality.BBB, FrtbSeniority.NonSeniorDebt, 100_000, 90_000, 0.1),
            // Z: short senior 50 000, 1y → −37 500. A senior short may NOT offset a junior long: both stay.
            new DrcPosition("Z", FrtbDrcBucket.Corporate, FrtbCreditQuality.BBB, FrtbSeniority.SeniorDebt, -50_000, -50_000, 1),
            // S (AA, 2%) sovereign: long senior 400 000, 5y → 300 000.
            new DrcPosition("S", FrtbDrcBucket.Sovereign, FrtbCreditQuality.AA, FrtbSeniority.SeniorDebt, 400_000, 400_000, 5),
        };
        double longX = 770_000 - 50_000, shortY = -182_500 + 75_000, longZ = 22_500, shortZ = -37_500;
        double sumLong = longX + longZ, sumShort = -shortY - shortZ;                 // 742 500 and 145 000
        double hbr = sumLong / (sumLong + sumShort);                                 // 0.836620
        double wLong = 0.03 * longX + 0.06 * longZ;                                  // 21 600 + 1 350 = 22 950
        double wShort = 0.15 * -shortY + 0.06 * -shortZ;                             // 16 125 + 2 250 = 18 375
        double corporate = Math.Max(wLong - hbr * wShort, 0);                        // 7 577.11
        double sovereign = 0.02 * 300_000;                                           // 6 000

        var r = FrtbDrc.Charge(p);
        Assert.Equal((longX, 0.0), r.NetJtd["X"]);
        Assert.Equal((0.0, shortY), r.NetJtd["Y"]);
        Assert.Equal((longZ, shortZ), r.NetJtd["Z"]);
        var c = r.Buckets[FrtbDrcBucket.Corporate];
        Assert.Equal(hbr, c.Hbr, 12);
        Assert.Equal(wLong, c.WeightedLong, 6);
        Assert.Equal(wShort, c.WeightedShort, 6);
        Assert.Equal(corporate, c.Charge, 6);
        Assert.Equal(7_577.112676, c.Charge, 5);
        Assert.Equal(sovereign, r.Buckets[FrtbDrcBucket.Sovereign].Charge, 9);
        Assert.Equal(corporate + sovereign, r.Charge, 6);
    }

    [Fact]
    public void DrcLimitingCases()
    {
        // Single long: RW·JTD. Unrated senior 1 000 000 at par, 1y: 0.15·750 000.
        var single = FrtbDrc.Charge(new[] { new DrcPosition("U", FrtbDrcBucket.Corporate, FrtbCreditQuality.Unrated, FrtbSeniority.SeniorDebt, 1_000_000, 1_000_000, 1) });
        Assert.Equal(112_500, single.Charge, 9);
        // Perfectly offsetting long and short of one obligor and seniority: zero.
        var flat = FrtbDrc.Charge(new[]
        {
            new DrcPosition("O", FrtbDrcBucket.Corporate, FrtbCreditQuality.B, FrtbSeniority.SeniorDebt, 500_000, 495_000, 2),
            new DrcPosition("O", FrtbDrcBucket.Corporate, FrtbCreditQuality.B, FrtbSeniority.SeniorDebt, -500_000, -495_000, 2),
        });
        Assert.Equal(0.0, flat.Charge, 9);
        // Shorts only: HBR = 0 and the floor gives zero.
        var shorts = FrtbDrc.Charge(new[] { new DrcPosition("Q", FrtbDrcBucket.Corporate, FrtbCreditQuality.CCC, FrtbSeniority.SeniorDebt, -1_000_000, -1_000_000, 1) });
        Assert.Equal(0.0, shorts.Buckets[FrtbDrcBucket.Corporate].Hbr, 12);
        Assert.Equal(0.0, shorts.Charge, 12);
        // Inconsistent credit quality for one obligor is rejected.
        Assert.Throws<ArgumentException>(() => FrtbDrc.Charge(new[]
        {
            new DrcPosition("W", FrtbDrcBucket.Corporate, FrtbCreditQuality.A, FrtbSeniority.SeniorDebt, 1, 1, 1),
            new DrcPosition("W", FrtbDrcBucket.Corporate, FrtbCreditQuality.BB, FrtbSeniority.SeniorDebt, 1, 1, 1),
        }));
    }
}
