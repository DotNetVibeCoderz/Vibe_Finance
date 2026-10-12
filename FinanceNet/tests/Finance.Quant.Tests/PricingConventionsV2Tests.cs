using Finance.Core.Time;
using Finance.Pricing.Credit;
using Finance.Pricing.Rates;
using Xunit;

namespace Finance.Quant.Tests;

/// <summary>
/// CDS IMM schedules, YoY inflation convexity and MtM-resetting cross-currency swaps. References: hand-computed
/// calendars for known dates, the ISDA 2015 semi-annual roll examples, closed-form flat-curve integrals, the
/// Mercurio (2005) YoY formula and its limiting cases, and a strip of one-period constant-notional swaps priced by
/// the existing <see cref="CrossCurrencySwap"/>.
/// </summary>
public class PricingConventionsV2Tests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    // ---------------- IMM dates ----------------

    [Fact]
    public void ImmDatesAreThe20thOfQuarterMonths()
    {
        Assert.Equal(D(2026, 12, 20), CdsDates.NextImmDate(D(2026, 10, 12)));
        Assert.Equal(D(2027, 3, 20), CdsDates.NextImmDate(D(2026, 12, 20)));    // strictly after
        Assert.Equal(D(2026, 12, 20), CdsDates.NextImmDate(D(2026, 12, 19)));
        Assert.Equal(D(2026, 9, 20), CdsDates.PreviousImmDate(D(2026, 10, 12)));
        Assert.Equal(D(2026, 9, 20), CdsDates.PreviousImmDate(D(2026, 9, 20)));  // on or before
        Assert.Equal(D(2025, 12, 20), CdsDates.PreviousImmDate(D(2026, 1, 5)));
        Assert.Equal(D(2025, 12, 20), CdsDates.PreviousImmDate(D(2026, 3, 19)));
        Assert.True(CdsDates.IsImmDate(D(2027, 6, 20)));
        Assert.False(CdsDates.IsImmDate(D(2027, 7, 20)));
    }

    [Fact]
    public void SemiAnnualRollMatchesIsda2015Examples()
    {
        var fiveY = new Tenor(5, TenorUnit.Years);
        // ISDA 2015 roll change: trades from 20 Mar to 19 Sep mature in June, 20 Sep to 19 Mar in December.
        Assert.Equal(D(2020, 12, 20), CdsDates.StandardMaturity(D(2016, 3, 18), fiveY));
        Assert.Equal(D(2021, 6, 20), CdsDates.StandardMaturity(D(2016, 3, 21), fiveY));
        Assert.Equal(D(2021, 6, 20), CdsDates.StandardMaturity(D(2016, 6, 20), fiveY)); // no roll in June
        Assert.Equal(D(2021, 6, 20), CdsDates.StandardMaturity(D(2016, 9, 19), fiveY));
        Assert.Equal(D(2021, 12, 20), CdsDates.StandardMaturity(D(2016, 9, 20), fiveY));
        Assert.Equal(D(2021, 12, 20), CdsDates.StandardMaturity(D(2016, 12, 21), fiveY)); // no roll in December
        Assert.Equal(D(2031, 12, 20), CdsDates.StandardMaturity(D(2026, 10, 12), fiveY));
        Assert.Equal(D(2026, 12, 20), CdsDates.StandardMaturity(D(2026, 1, 10), new Tenor(1, TenorUnit.Years)));
        // Pre-2015 quarterly roll: the June trade already rolls to September.
        Assert.Equal(D(2021, 9, 20), CdsDates.StandardMaturity(D(2016, 6, 21), fiveY, CdsRollConvention.Quarterly));
        Assert.Throws<ArgumentException>(() => CdsDates.StandardMaturity(D(2016, 6, 21), new Tenor(2, TenorUnit.Months)));
    }

    [Fact]
    public void FiveYearScheduleFromTradeDateIsHandComputed()
    {
        // Trade Mon 12 Oct 2026. 20 Sep 2026 is a Sunday → accrual start Mon 21 Sep. 20 Dec 2026 Sun → 21 Dec,
        // 20 Mar 2027 Sat → 22 Mar, 20 Sep 2031 Sat → 22 Sep, maturity 20 Dec 2031 Sat → pays Mon 22 Dec.
        var s = CdsSchedule.Standard(D(2026, 10, 12), new Tenor(5, TenorUnit.Years));
        Assert.Equal(D(2031, 12, 20), s.MaturityDate);
        Assert.Equal(D(2026, 10, 13), s.StepInDate);
        Assert.Equal(D(2026, 10, 15), s.CashSettlementDate);   // T+3 business days
        Assert.Equal(D(2026, 9, 21), s.AccrualStartDate);
        Assert.Equal(22, s.AccruedDays);                       // 21 Sep .. 12 Oct inclusive
        Assert.Equal(21, s.Periods.Count);                     // Sep 2026 .. Dec 2031 quarters

        Assert.Equal(new CdsPeriod(D(2026, 9, 21), D(2026, 12, 21), D(2026, 12, 21), 91 / 360.0), s.Periods[0]);
        Assert.Equal(new CdsPeriod(D(2026, 12, 21), D(2027, 3, 22), D(2027, 3, 22), 91 / 360.0), s.Periods[1]);
        Assert.Equal(new CdsPeriod(D(2027, 3, 22), D(2027, 6, 21), D(2027, 6, 21), 91 / 360.0), s.Periods[2]);
        // Final period accrues to maturity inclusive (end = 21 Dec, unadjusted maturity + 1) and pays on the adjusted date.
        Assert.Equal(new CdsPeriod(D(2031, 9, 22), D(2031, 12, 21), D(2031, 12, 22), 90 / 360.0), s.Periods[^1]);
        // Periods tile 21 Sep 2026 .. 21 Dec 2031 = 1917 days.
        Assert.Equal(1917, s.Periods.Sum(p => p.Days));
        for (int i = 1; i < s.Periods.Count; i++) Assert.Equal(s.Periods[i - 1].AccrualEnd, s.Periods[i].AccrualStart);
    }

    [Fact]
    public void HolidaysPushCouponDatesFollowing()
    {
        var cal = new BusinessCalendar("Test");
        cal.AddHoliday(D(2026, 12, 21));                         // the Monday after the Sunday IMM date
        var s = CdsSchedule.Standard(D(2026, 10, 12), new Tenor(1, TenorUnit.Years), cal);
        Assert.Equal(D(2027, 12, 20), s.MaturityDate);
        Assert.Equal(D(2026, 12, 22), s.Periods[0].PaymentDate);
        Assert.Equal(92, s.Periods[0].Days);                     // 21 Sep .. 22 Dec
        Assert.Equal(D(2026, 12, 22), s.Periods[1].AccrualStart);
    }

    [Fact]
    public void WeekendImmDateAfterStepInUsesThePreviousImmDate()
    {
        // Trade Fri 19 Mar 2027: step-in Sat 20 Mar, but the adjusted IMM date is Mon 22 Mar (after step-in), so
        // accrual starts at 20 Dec 2026 (Sun) → 21 Dec; accrued 21 Dec .. 19 Mar inclusive = 89 days.
        var s = CdsSchedule.Standard(D(2027, 3, 19), new Tenor(5, TenorUnit.Years));
        Assert.Equal(D(2031, 12, 20), s.MaturityDate);
        Assert.Equal(D(2026, 12, 21), s.AccrualStartDate);
        Assert.Equal(89, s.AccruedDays);
        Assert.Equal(D(2027, 3, 22), s.Periods[0].PaymentDate);
    }

    // ---------------- Dated ISDA CDS ----------------

    [Fact]
    public void DatedProtectionLegMatchesFlatClosedForm()
    {
        const double lambda = 0.02, r = 0.03, R = 0.4;
        var cds = IsdaCds.Standard(1, D(2026, 10, 12), new Tenor(5, TenorUnit.Years), recovery: R);
        double T = 1895 / 365.0;   // 12 Oct 2026 → 20 Dec 2031, protection to maturity inclusive
        double expected = (1 - R) * lambda / (lambda + r) * (1 - Math.Exp(-(lambda + r) * T));
        Assert.Equal(expected, cds.ProtectionLegValue(YieldCurve.Flat(r), HazardCurve.Flat(lambda)), 12);
    }

    [Fact]
    public void DatedRiskyPv01MatchesFineGridIntegration()
    {
        const double lambda = 0.025, r = 0.035;
        var trade = D(2026, 10, 12);
        var cds = IsdaCds.Standard(1, trade, new Tenor(5, TenorUnit.Years));
        double T(DateOnly d) => (d.DayNumber - trade.DayNumber) / 365.0;
        double Q(double t) => Math.Exp(-lambda * Math.Max(t, 0));
        // Hand-listed coupon dates: unadjusted IMM dates from 20 Sep 2026 to 20 Dec 2031, adjusted for weekends.
        var imm = new List<DateOnly>();
        for (var d = D(2026, 9, 20); d <= D(2031, 12, 20); d = d.AddMonths(3)) imm.Add(d);
        DateOnly Adj(DateOnly d) => d.DayOfWeek == DayOfWeek.Saturday ? d.AddDays(2) : d.DayOfWeek == DayOfWeek.Sunday ? d.AddDays(1) : d;
        double pv = 0;
        for (int i = 1; i < imm.Count; i++)
        {
            var start = Adj(imm[i - 1]);
            var end = i == imm.Count - 1 ? imm[i].AddDays(1) : Adj(imm[i]);
            double days = end.DayNumber - start.DayNumber;
            // Coupon: paid on survival to the end of the last accrual day.
            pv += days / 360 * Math.Exp(-r * T(Adj(imm[i]))) * Q(T(end) - 1 / 365.0);
            // Accrual on default: default during day k earns the days from start through k; midpoint rule per day.
            for (var k = start; k < end; k = k.AddDays(1))
            {
                double u0 = T(k) - 1 / 365.0, u1 = T(k);
                if (u1 <= 0) continue;
                u0 = Math.Max(u0, 0);
                const int sub = 50;
                double h = (u1 - u0) / sub;
                for (int j = 0; j < sub; j++)
                {
                    double a = u0 + j * h, um = a + h / 2;
                    double accrued = (um - (T(start) - 1 / 365.0)) * 365 / 360;
                    pv += accrued * Math.Exp(-r * um) * (Q(a) - Q(a + h));
                }
            }
        }
        Assert.Equal(pv, cds.RiskyPv01(YieldCurve.Flat(r), HazardCurve.Flat(lambda)), 9);
    }

    [Fact]
    public void QuotedAtCouponHasZeroPointsAndRebatesAccrued()
    {
        var cds = IsdaCds.Standard(10_000_000, D(2026, 10, 12), new Tenor(5, TenorUnit.Years), IsdaCds.InvestmentGradeCoupon);
        var disc = YieldCurve.Flat(0.035);
        double lambda = cds.FlatHazardFromQuotedSpread(disc, 0.01);
        var hz = HazardCurve.Flat(lambda);
        Assert.Equal(0, cds.Upfront(disc, hz), 12);
        // The buyer pays the full first coupon, so the seller rebates 22 days of accrued: 10mm · 1% · 22/360.
        Assert.Equal(22 / 360.0, cds.AccruedFraction, 15);
        Assert.Equal(-10_000_000 * 0.01 * 22 / 360.0, cds.CashSettlement(disc, hz), 6);   // −6,111.11

        // Wider than the coupon: positive points, and quoted ↔ upfront round-trips.
        double up = cds.UpfrontFromQuotedSpread(disc, 0.03);
        Assert.True(up > 0.08 && up < 0.10, $"upfront {up:P3}");
        Assert.Equal(0.03, cds.QuotedSpreadFromUpfront(disc, up), 10);
        Assert.True(cds.Cs01(disc, 0.03) > 0);
    }

    [Fact]
    public void CleanUpfrontIsDirtyValueForwardedToCashSettlementPlusAccrued()
    {
        // ISDA convention: dirty PV at T, forwarded to T+3 business days, plus the accrued premium.
        var cds = IsdaCds.Standard(1, D(2026, 10, 12), new Tenor(5, TenorUnit.Years), IsdaCds.HighYieldCoupon);
        var disc = YieldCurve.Flat(0.04);
        var hz = HazardCurve.Flat(0.06);
        double dirty = cds.ProtectionLegValue(disc, hz) - 0.05 * cds.RiskyPv01(disc, hz);
        double dfCs = Math.Exp(-0.04 * 3 / 365.0);   // 12 Oct → 15 Oct
        Assert.Equal(dirty / dfCs + 0.05 * 22 / 360.0, cds.Upfront(disc, hz), 12);
    }

    // ---------------- YoY convexity ----------------

    private static readonly YieldCurve FlatNominal = YieldCurve.Flat(0.03);
    private static readonly InflationCurve FlatInflation = InflationCurve.FromZeroCouponSwaps(100, new[] { 1.0, 30 }, new[] { 0.025, 0.025 });

    [Fact]
    public void ZeroVolatilityReproducesTheUnadjustedPrice()
    {
        var infl = InflationCurve.FromZeroCouponSwaps(300, new[] { 1.0, 2, 5, 10 }, new[] { 0.031, 0.028, 0.025, 0.024 });
        var disc = new YieldCurve(new[] { 1.0, 2, 5, 10 }, new[] { 0.040, 0.039, 0.037, 0.038 });
        var yoy = new YearOnYearInflationSwap(1_000_000, 0.026, 10);
        var zero = new YoYConvexityModel(0, 0, 0.5, 0.8);
        Assert.Equal(yoy.Npv(disc, infl), yoy.Npv(disc, infl, zero));            // exactly
        Assert.Equal(yoy.ParRate(disc, infl), yoy.ParRate(disc, infl, zero));
    }

    [Fact]
    public void YoYAdjustmentMatchesMercurioFormula()
    {
        // Mercurio (2005): E[I(Tᵢ)/I(Tᵢ₋₁)] = (Iᵢ/Iᵢ₋₁)·exp(σ_I Tᵢ₋₁ [ρ_IN σ_N τF/(1+τF) − ρ_II σ_I + σ_I]).
        const double sI = 0.012, sN = 0.25, rhoIN = 0.4, rhoII = 0.9;
        var model = new YoYConvexityModel(sI, sN, rhoIN, rhoII);
        var yoy = new YearOnYearInflationSwap(1, 0.025, 10);
        double F = Math.Exp(0.03) - 1, x = F / (1 + F), num = 0, ann = 0;
        for (int i = 1; i <= 10; i++)
        {
            double ratio = 1.025 * Math.Exp(sI * (i - 1) * (rhoIN * sN * x - rhoII * sI + sI));
            Assert.Equal(ratio, yoy.ExpectedRatio(i, FlatNominal, FlatInflation, model), 12);
            num += (ratio - 1) * Math.Exp(-0.03 * i);
            ann += Math.Exp(-0.03 * i);
        }
        Assert.Equal(num / ann, yoy.ParRate(FlatNominal, FlatInflation, model), 12);
        // Here D grows by σ_I(ρ_IN σ_N x + (1−ρ_II)σ_I) = 0.012·(0.4·0.25·0.02955 + 0.1·0.012) ≈ 4.99e-5 per year of
        // fixing lag: about +2.2bp on the 10Y par rate.
        double bump = yoy.ParRate(FlatNominal, FlatInflation, model) - 0.025;
        Assert.InRange(bump * 1e4, 2.1, 2.3);
    }

    [Fact]
    public void YoYLimitingCases()
    {
        var yoy = new YearOnYearInflationSwap(1, 0.025, 10);
        // Uncorrelated with rates and perfectly correlated forward CPIs: the two variance terms cancel.
        Assert.Equal(0.025, yoy.ParRate(FlatNominal, FlatInflation, new YoYConvexityModel(0.02, 0.3, 0, 1)), 12);
        // The first period is fixed off today's index: never adjusted.
        Assert.Equal(1.025, yoy.ExpectedRatio(1, FlatNominal, FlatInflation, new YoYConvexityModel(0.05, 0.5, 0.9, 0.2)), 12);
        // Positive inflation/rates correlation raises the YoY rate, negative lowers it.
        Assert.True(yoy.ParRate(FlatNominal, FlatInflation, new YoYConvexityModel(0.02, 0.3, 0.5, 1)) > 0.025);
        Assert.True(yoy.ParRate(FlatNominal, FlatInflation, new YoYConvexityModel(0.02, 0.3, -0.5, 1)) < 0.025);
    }

    // ---------------- MtM-resetting cross-currency ----------------

    private static readonly YieldCurve UsdFlat = YieldCurve.Flat(0.04), EurFlat = YieldCurve.Flat(0.025);

    [Fact]
    public void MtmSwapAtZeroBasisOnConsistentCurvesIsWorthZero()
    {
        var usd = new YieldCurve(new[] { 1.0, 2, 5, 10 }, new[] { 0.040, 0.039, 0.037, 0.038 });
        var eur = new YieldCurve(new[] { 1.0, 5, 10 }, new[] { 0.025, 0.026, 0.028 });
        var dom = new CrossCurrencyLeg(0, usd, usd, 0);
        var fgn = new CrossCurrencyLeg(1_000_000, eur, eur, 0);
        var swap = new MtmCrossCurrencySwap(dom, fgn, Maturity: 7);
        Assert.Equal(0, swap.Npv(1.08), 6);
        Assert.Equal(0, swap.ParForeignSpread(1.08), 12);
        // Foreign leg resetting against a constant USD notional: also zero.
        var swapF = new MtmCrossCurrencySwap(dom with { Notional = 1_080_000 }, fgn, Maturity: 7, ResetDomestic: false);
        Assert.Equal(0, swapF.Npv(1.08), 6);
    }

    [Fact]
    public void NotionalResetsFollowTheForwardFx()
    {
        // Flat 4% USD and 2.5% EUR: X(t) = 1.10·e^{0.015t}; USD notional per quarter 1.1mm·e^{0.015(i−1)/4}.
        var swap = new MtmCrossCurrencySwap(new CrossCurrencyLeg(0, UsdFlat, UsdFlat, 0),
            new CrossCurrencyLeg(1_000_000, EurFlat, EurFlat, 0), Maturity: 1);
        var resets = swap.ResetSchedule(1.10);
        Assert.Equal(4, resets.Count);
        Assert.Equal(1_100_000, resets[0].ResettingNotional, 6);
        Assert.Equal(1_104_132.7440520392, resets[1].ResettingNotional, 6);
        for (int i = 0; i < 4; i++)
        {
            double n = 1_100_000 * Math.Exp(0.015 * i / 4);
            Assert.Equal(0.25 * i, resets[i].Start, 12);
            Assert.Equal(1.10 * Math.Exp(0.015 * i / 4), resets[i].ForwardFx, 12);
            Assert.Equal(n, resets[i].ResettingNotional, 6);
            // Each reset exchanges the difference Nᵢ − Nᵢ₊₁ (negative here: the USD notional grows); Nₙ at maturity.
            double expected = i < 3 ? n - 1_100_000 * Math.Exp(0.015 * (i + 1) / 4) : n;
            Assert.Equal(expected, resets[i].NotionalExchange, 6);
        }
        Assert.Equal(-4_132.7440520392, resets[0].NotionalExchange, 6);
    }

    [Fact]
    public void MtmSwapEqualsStripOfOnePeriodConstantNotionalSwaps()
    {
        // With basis (EUR discounted 20bp lower) and spreads on both legs, the MtM swap is the sum of one-period
        // constant-notional swaps whose USD notional is the forward FX at each period start times the EUR notional.
        var eurDisc = EurFlat.Shifted(-0.0020);
        var dom = new CrossCurrencyLeg(0, UsdFlat, UsdFlat.Shifted(0.001), 0.0010);
        var fgn = new CrossCurrencyLeg(1_000_000, eurDisc, EurFlat, -0.0015);
        const double S = 1.10;
        var mtm = new MtmCrossCurrencySwap(dom, fgn, Maturity: 5);
        double strip = 0;
        for (int i = 0; i < 20; i++)
        {
            double t0 = 0.25 * i, t1 = t0 + 0.25;
            double fwd = S * eurDisc.DiscountFactor(t0) / UsdFlat.DiscountFactor(t0);
            strip += new CrossCurrencySwap(dom with { Notional = 1_000_000 * fwd }, fgn, t1, t0).Npv(S);
        }
        Assert.Equal(strip, mtm.Npv(S), 6);

        double basis = mtm.ParForeignSpread(S);
        Assert.Equal(0, (mtm with { Foreign = fgn with { Rate = basis } }).Npv(S), 6);
        // The MtM basis differs from the constant-notional basis, but only by a fraction of a basis point.
        double constBasis = new CrossCurrencySwap(dom with { Notional = 1_100_000 }, fgn, 5).ParForeignSpread(S);
        Assert.NotEqual(constBasis, basis, 8);
        Assert.InRange(Math.Abs(constBasis - basis) * 1e4, 0, 1);
    }

    [Fact]
    public void ParFloatingResettingLegGivesTheConstantNotionalBasis()
    {
        // A USD leg forecast and discounted on one curve is a par floater in every period, so resetting its
        // notional changes nothing: the MtM basis equals the constant-notional basis exactly.
        var eurDisc = EurFlat.Shifted(-0.0020);
        var dom = new CrossCurrencyLeg(0, UsdFlat, UsdFlat, 0);
        var fgn = new CrossCurrencyLeg(1_000_000, eurDisc, EurFlat, 0);
        double mtm = new MtmCrossCurrencySwap(dom, fgn, 5).ParForeignSpread(1.10);
        double plain = new CrossCurrencySwap(dom with { Notional = 1_100_000 }, fgn, 5).ParForeignSpread(1.10);
        Assert.Equal(plain, mtm, 12);
        Assert.True(mtm < 0);
    }

    [Fact]
    public void EqualDiscountRatesMakeMtmEqualConstantNotional()
    {
        // Flat forward FX (same discount rates): every reset equals spot, so nothing resets.
        var disc = YieldCurve.Flat(0.03);
        var dom = new CrossCurrencyLeg(1_100_000, disc, YieldCurve.Flat(0.032), 0);
        var fgn = new CrossCurrencyLeg(1_000_000, disc, YieldCurve.Flat(0.029), 0.001);
        var mtm = new MtmCrossCurrencySwap(dom, fgn, 5);
        var plain = new CrossCurrencySwap(dom, fgn, 5);
        Assert.Equal(plain.Npv(1.10), mtm.Npv(1.10), 6);
        Assert.All(mtm.ResetSchedule(1.10).SkipLast(1), p => Assert.Equal(0, p.NotionalExchange, 6));
    }
}
