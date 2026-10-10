using Finance.Pricing.Credit;
using Finance.Pricing.Options;
using Finance.Pricing.Rates;
using Finance.Pricing.Structured;
using Xunit;

namespace Finance.Quant.Tests;

public class FixedIncomeTests
{
    [Fact]
    public void Bond_PriceAtCouponDate_MatchesClosedForm()
    {
        var bond = new FixedRateBond(new DateOnly(2024, 1, 15), new DateOnly(2027, 1, 15), 0.10, 2);
        var settle = new DateOnly(2024, 1, 15);
        // 6 semiannual coupons of 5, yield 12% → 95.0827.
        Assert.Equal(95.0827, bond.DirtyPrice(0.12, settle), 4);
        Assert.Equal(0, bond.AccruedInterest(settle), 10);
        Assert.Equal(100, bond.CleanPrice(0.10, settle), 9);
    }

    [Fact]
    public void Bond_YieldRoundTrip_AndAccruedHalfPeriod()
    {
        var bond = new FixedRateBond(new DateOnly(2020, 3, 1), new DateOnly(2030, 3, 1), 0.05, 2, 100, Finance.Core.Time.DayCountConvention.Thirty360);
        var settle = new DateOnly(2025, 6, 1); // 3 of 6 months elapsed
        Assert.Equal(1.25, bond.AccruedInterest(settle), 10);
        double clean = bond.CleanPrice(0.0437, settle);
        Assert.Equal(0.0437, bond.YieldToMaturity(clean, settle), 10);
    }

    [Fact]
    public void Bond_DurationAndConvexity_MatchFiniteDifferences()
    {
        var bond = new FixedRateBond(new DateOnly(2021, 5, 15), new DateOnly(2035, 5, 15), 0.045, 2);
        var settle = new DateOnly(2025, 8, 20);
        double y = 0.051, h = 1e-5;
        var a = bond.Analytics(y, settle);
        double p0 = bond.DirtyPrice(y, settle), pu = bond.DirtyPrice(y + h, settle), pd = bond.DirtyPrice(y - h, settle);
        Assert.Equal(-(pu - pd) / (2 * h) / p0, a.ModifiedDuration, 6);
        Assert.Equal((pu - 2 * p0 + pd) / (h * h) / p0, a.Convexity, 3);
        Assert.Equal(a.ModifiedDuration * (1 + y / 2), a.MacaulayDuration, 10);
        Assert.Equal((pd - pu) / (2 * h) * 1e-4, a.Dv01, 6);
    }

    [Fact]
    public void ZeroCoupon_MacaulayDurationEqualsMaturity()
    {
        var bond = new FixedRateBond(new DateOnly(2025, 1, 1), new DateOnly(2030, 1, 1), 0.0, 1);
        var a = bond.Analytics(0.04, new DateOnly(2025, 1, 1));
        Assert.Equal(5, a.MacaulayDuration, 9);
        Assert.Equal(100 / Math.Pow(1.04, 5), ZeroCouponBond.Price(100, 0.04, 5), 10);
        Assert.Equal(0.04, ZeroCouponBond.Yield(100, ZeroCouponBond.Price(100, 0.04, 5), 5), 12);
    }

    [Fact]
    public void Bootstrap_RepricesEveryInstrument()
    {
        var inst = new ICurveInstrument[]
        {
            new DepositInstrument(0.25, 0.031), new DepositInstrument(0.5, 0.032),
            new FraInstrument(0.5, 1.0, 0.0335),
            new SwapInstrument(2, 0.034), new SwapInstrument(3, 0.0352), new SwapInstrument(5, 0.037),
            new SwapInstrument(7, 0.038), new SwapInstrument(10, 0.039),
        };
        foreach (var interp in Enum.GetValues<CurveInterpolation>())
        {
            var curve = CurveBootstrapper.Bootstrap(inst, interp);
            foreach (var i in inst) Assert.True(Math.Abs(i.PricingError(curve)) < 1e-10, $"{interp} {i}");
        }
    }

    [Fact]
    public void YieldCurve_ForwardsConsistentWithDiscountFactors()
    {
        var c = new YieldCurve(new[] { 1.0, 2, 5, 10 }, new[] { 0.02, 0.025, 0.03, 0.035 }, CurveInterpolation.CubicZero);
        double f = c.ForwardRate(2, 5);
        Assert.Equal(c.DiscountFactor(2) / (1 + 3 * f), c.DiscountFactor(5), 12);
        Assert.Equal(0.02, c.ZeroRate(0.5), 12); // flat before first pillar
        Assert.Equal(Math.Exp(-0.05 * 3), YieldCurve.Flat(0.05).DiscountFactor(3), 12);
        Assert.Equal(0.05, YieldCurve.Flat(0.05).InstantaneousForward(4), 8);
    }

    [Fact]
    public void NelsonSiegel_RecoversParameters_SvenssonFitsAtLeastAsWell()
    {
        var truth = new NelsonSiegelSvensson(0.045, -0.02, 0.015, 2.0);
        double[] t = { 0.25, 0.5, 1, 2, 3, 5, 7, 10, 20, 30 };
        var y = t.Select(truth.ZeroRate).ToArray();
        var ns = NelsonSiegelSvensson.FitNelsonSiegel(t, y);
        Assert.Equal(truth.Beta0, ns.Beta0, 6);
        Assert.Equal(truth.Tau1, ns.Tau1, 3);
        var sv = NelsonSiegelSvensson.FitSvensson(t, y);
        foreach (var ti in t) Assert.Equal(truth.ZeroRate(ti), sv.ZeroRate(ti), 6);
    }

    [Fact]
    public void FloatingRateNote_PricesAtParWithZeroMargin()
    {
        var curve = new YieldCurve(new[] { 0.5, 2, 5 }, new[] { 0.03, 0.035, 0.04 });
        Assert.Equal(100, new FloatingRateNote(5, 4).Price(curve), 9);
        Assert.True(new FloatingRateNote(5, 4, 0.01).Price(curve) > 100);
    }

    [Fact]
    public void Swap_AtParRateIsWorthZero_AndDv01Sign()
    {
        var curve = new YieldCurve(new[] { 1.0, 3, 5, 10 }, new[] { 0.03, 0.033, 0.036, 0.04 });
        var probe = new InterestRateSwap(1_000_000, 0.0, 10, true, 1, 4);
        double par = probe.ParRate(curve);
        var swap = probe with { FixedRate = par };
        Assert.Equal(0, swap.Npv(curve), 6);
        Assert.Equal(curve.ParRate(10, 1), par, 10); // single-curve identity
        Assert.True(swap.Dv01(curve) > 0); // payer gains when rates rise
        Assert.True((swap with { PayFixed = false }).Dv01(curve) < 0);
    }

    [Fact]
    public void Swaption_PutCallParityAndAtmBachelier()
    {
        var curve = YieldCurve.Flat(0.03);
        var payer = new Swaption(2, 5, 0.032, true, 1_000_000);
        var receiver = payer with { Payer = false };
        double fwd = payer.ForwardSwapRate(curve), ann = payer.Annuity(curve);
        double vol = 0.25;
        // Payer - receiver = forward-starting payer swap.
        Assert.Equal(1_000_000 * ann * (fwd - 0.032), payer.PriceBlack(curve, vol) - receiver.PriceBlack(curve, vol), 6);
        var atm = payer with { Strike = fwd };
        Assert.Equal(1_000_000 * ann * 0.008 * Math.Sqrt(2 / (2 * Math.PI)), atm.PriceBachelier(curve, 0.008), 6);
    }

    [Fact]
    public void Forwards_CostOfCarryAndCoveredInterestParity()
    {
        Assert.Equal(40 * Math.Exp(0.05 * 0.25), Forwards.Price(40, 0.05, 0.25), 12); // Hull 5.4
        Assert.Equal(1.6 * Math.Exp((0.05 - 0.07) * 2), Forwards.FxForward(1.6, 0.05, 0.07, 2), 12);
        double f = Forwards.Price(100, 0.04, 1, 0.01);
        Assert.Equal(0.04, Forwards.ImpliedRepoRate(100, f, 1, 0.01), 12);
    }

    [Fact]
    public void Cds_ParSpreadNearCreditTriangle_AndBootstrapReprices()
    {
        var disc = YieldCurve.Flat(0.03);
        var flat = HazardCurve.Flat(0.02);
        var cds = new CreditDefaultSwap(10_000_000, 0, 5, 4, 0.4);
        double par = cds.ParSpread(disc, flat);
        Assert.Equal(0.02 * 0.6, par, 4); // credit triangle s ≈ λ(1 − R)

        double[] mats = { 1, 3, 5, 7, 10 };
        double[] spreads = { 0.006, 0.009, 0.012, 0.0135, 0.015 };
        var hz = CreditDefaultSwap.BootstrapHazard(disc, mats, spreads);
        for (int i = 0; i < mats.Length; i++)
            Assert.Equal(spreads[i], new CreditDefaultSwap(1, 0, mats[i]).ParSpread(disc, hz), 10);
        Assert.True(hz.SurvivalProbability(10) < hz.SurvivalProbability(5));
    }

    [Fact]
    public void StructuredProducts_FairTermsPriceAtPar()
    {
        double s = 100, r = 0.04, q = 0.01, v = 0.25;
        double c = ReverseConvertible.FairCoupon(1000, 1, 100, s, r, q, v);
        Assert.Equal(1000, new ReverseConvertible(1000, c, 1, 100).Price(s, r, q, v), 6);
        Assert.True(c > r);

        double part = CapitalProtectedNote.FairParticipation(1000, 5, 1.0, s, r, q, v);
        Assert.Equal(1000, new CapitalProtectedNote(1000, 5, 1.0, part).Price(s, r, q, v), 8);
        Assert.InRange(part, 0.1, 1.5);
    }

    [Fact]
    public void Autocallable_BoundedAndConsistent()
    {
        var note = new AutocallableNote(1000, new[] { 0.5, 1, 1.5, 2, 2.5, 3 }, 0.04, 1.0, 0.6);
        var res = note.Price(100, 0.03, 0, 0.25, 60_000);
        Assert.InRange(res.Price, 800, 1000 * (1 + 0.24));
        double total = res.CallProbabilities.Sum() + (1 - res.CallProbabilities.Sum());
        Assert.Equal(1, total, 12);
        Assert.True(res.CallProbabilities[0] > 0.4); // ~P(S_0.5 ≥ S_0)
        Assert.InRange(res.ExpectedLife, 0.5, 3);
        // With zero vol and positive drift the note calls at the first date.
        var det = note.Price(100, 0.03, 0, 1e-9, 1000);
        Assert.Equal(1000 * 1.04 * Math.Exp(-0.03 * 0.5), det.Price, 6);
    }
}
