using Finance.Pricing.Credit;
using Finance.Pricing.Rates;
using Xunit;

namespace Finance.Quant.Tests;

/// <summary>
/// Inflation swaps, cross-currency swaps and the ISDA-style CDS. References: bootstrap round trips, the par
/// identity of a floating leg with notional exchange, closed-form integrals for flat hazard and flat rates, and
/// fine-grid numerical integration written independently in the test.
/// </summary>
public class RatesCreditV2Tests
{
    private static readonly YieldCurve Usd = new(new[] { 1.0, 2, 5, 10 }, new[] { 0.040, 0.039, 0.037, 0.038 });

    // The ISDA model assumes flat forwards between curve nodes, which is exactly a log-linear discount curve.
    private static readonly YieldCurve UsdIsda = new(new[] { 1.0, 2, 5, 10 }, new[] { 0.040, 0.039, 0.037, 0.038 }, CurveInterpolation.LogLinearDiscount);

    // ---------------- Inflation ----------------

    [Fact]
    public void ZeroCouponInflationBootstrapRoundTrips()
    {
        double[] t = { 1, 2, 5, 10, 30 }, k = { 0.031, 0.028, 0.025, 0.024, 0.023 };
        var infl = InflationCurve.FromZeroCouponSwaps(310.0, t, k);
        for (int i = 0; i < t.Length; i++)
        {
            var swap = new ZeroCouponInflationSwap(1_000_000, k[i], t[i]);
            Assert.Equal(k[i], swap.ParRate(infl), 12);
            Assert.Equal(0, swap.Npv(Usd, infl), 6);
        }
        Assert.Equal(310.0 * Math.Pow(1.025, 5), infl.ForwardIndex(5), 9);
    }

    [Fact]
    public void YearOnYearParEqualsFlatZeroInflationWithoutConvexity()
    {
        // With a flat 2.5% zero-coupon curve, every forward YoY ratio is 1.025, so the YoY par rate is 2.5%.
        var infl = InflationCurve.FromZeroCouponSwaps(100, new[] { 1.0, 30 }, new[] { 0.025, 0.025 });
        var yoy = new YearOnYearInflationSwap(1_000_000, 0.02, 10);
        Assert.Equal(0.025, yoy.ParRate(Usd, infl), 12);
        Assert.True(yoy.Npv(Usd, infl) > 0); // paying 2% fixed against 2.5% inflation
    }

    // ---------------- Cross-currency ----------------

    private static readonly YieldCurve Eur = new(new[] { 1.0, 5, 10 }, new[] { 0.025, 0.026, 0.028 });

    [Fact]
    public void FloatFloatWithNotionalExchangeIsWorthZeroOnConsistentCurves()
    {
        // A floating leg forecast and discounted on one curve, with notional exchanges, is worth par: zero net.
        var swap = new CrossCurrencySwap(
            new CrossCurrencyLeg(1_080_000, Usd, Usd, 0), new CrossCurrencyLeg(1_000_000, Eur, Eur, 0), Maturity: 7);
        Assert.Equal(0, swap.Npv(1.08), 6);
        Assert.Equal(0, swap.ParForeignSpread(1.08), 12);
    }

    [Fact]
    public void BasisSpreadReflectsADifferentDiscountCurve()
    {
        // Discount the EUR leg 20bp lower (USD-collateral basis): the par spread on the EUR leg is then negative,
        // and setting it zeroes the NPV.
        var eurDisc = Eur.Shifted(-0.0020);
        var leg = new CrossCurrencyLeg(1_000_000, eurDisc, Eur, 0);
        var swap = new CrossCurrencySwap(new CrossCurrencyLeg(1_080_000, Usd, Usd, 0), leg, Maturity: 5);
        double basis = swap.ParForeignSpread(1.08);
        Assert.True(basis < 0, $"basis {basis * 1e4:F2}bp");
        var atPar = swap with { Foreign = leg with { Rate = basis } };
        Assert.Equal(0, atPar.Npv(1.08), 6);
    }

    [Fact]
    public void FixedFixedLegMatchesBondFormula()
    {
        // A fixed leg with exchanges is a bond minus its notional: N·(c·Σ τP + P(T) − 1).
        var leg = new CrossCurrencyLeg(1_000_000, YieldCurve.Flat(0.03), null, 0.04, Frequency: 1);
        double expected = 0;
        for (int i = 1; i <= 5; i++) expected += 0.04 * Math.Exp(-0.03 * i);
        expected += Math.Exp(-0.15) - 1;
        Assert.Equal(1_000_000 * expected, leg.Value(0, 5), 6);
    }

    // ---------------- ISDA-style CDS ----------------

    [Fact]
    public void ProtectionLegMatchesFlatClosedForm()
    {
        const double lambda = 0.02, r = 0.03, R = 0.4, T = 5;
        var cds = new IsdaCds(1, T, Recovery: R);
        double expected = (1 - R) * lambda / (lambda + r) * (1 - Math.Exp(-(lambda + r) * T));
        Assert.Equal(expected, cds.ProtectionLegValue(YieldCurve.Flat(r), HazardCurve.Flat(lambda)), 12);
    }

    [Fact]
    public void RiskyPv01MatchesFineGridIntegration()
    {
        var disc = UsdIsda;
        var hz = new HazardCurve(new[] { 1.0, 3, 5 }, new[] { 0.010, 0.018, 0.025 });
        var cds = new IsdaCds(1, 5, Frequency: 4);
        // Independent: coupons at quarter ends + accrual on default by a fine midpoint rule.
        double pv = 0, tau = 0.25, f = 365.0 / 360;
        for (int i = 1; i <= 20; i++)
        {
            double t0 = (i - 1) * tau, t1 = i * tau;
            pv += tau * f * disc.DiscountFactor(t1) * hz.SurvivalProbability(t1);
            const int steps = 4000;
            double h = tau / steps;
            for (int s = 0; s < steps; s++)
            {
                double u0 = t0 + s * h, u1 = u0 + h, um = u0 + h / 2;
                pv += f * (um - t0) * disc.DiscountFactor(um) * (hz.SurvivalProbability(u0) - hz.SurvivalProbability(u1));
            }
        }
        Assert.Equal(pv, cds.RiskyPv01(disc, hz), 9);
    }

    [Fact]
    public void UpfrontAndQuotedSpreadConvertBothWays()
    {
        var cds = new IsdaCds(10_000_000, 5, IsdaCds.InvestmentGradeCoupon);
        double upfront = cds.UpfrontFromQuotedSpread(Usd, 0.0300);
        Assert.True(upfront > 0.08 && upfront < 0.10, $"upfront {upfront:P2}");   // ~ (300 − 100)bp × ~4.4y RPV01
        Assert.Equal(0.0300, cds.QuotedSpreadFromUpfront(Usd, upfront), 10);
        Assert.Equal(0, cds.UpfrontFromQuotedSpread(Usd, 0.01), 10);              // quoted at the coupon: no upfront
        Assert.True(cds.Cs01(Usd, 0.03) > 0);                                     // buyer gains when spreads widen
    }

    [Fact]
    public void AgreesWithTheExistingTimeSteppedCds()
    {
        // Same hazard and curve, Act/365 accrual: the closed-form and weekly-grid valuations agree closely.
        var hz = new HazardCurve(new[] { 2.0, 5 }, new[] { 0.015, 0.022 });
        var isda = new IsdaCds(1, 5, Coupon: 0.01, AccrualFactor: 1.0);
        var old = new CreditDefaultSwap(1, 0.01, 5);
        Assert.Equal(old.ProtectionLegValue(UsdIsda, hz), isda.ProtectionLegValue(UsdIsda, hz), 6);
        Assert.Equal(old.ParSpread(UsdIsda, hz), isda.ParSpread(UsdIsda, hz), 5);
    }
}
