using Finance.Core.Time;
using Finance.Mathematics;
using Finance.Pricing.Rates;

namespace Finance.Pricing.Credit;

/// <summary>
/// Standardised CDS priced the way the ISDA CDS Standard Model does it: piecewise-constant hazard rates and
/// piecewise-flat forward rates, with the protection leg and the accrual-on-default integrated in closed form
/// on every sub-interval, a fixed running coupon (100bp or 500bp), and upfront ↔ quoted-spread conversion
/// through a flat hazard curve.
/// </summary>
/// <remarks>
/// Two ways to build one. The positional constructor is the year-fraction form: regular periods of
/// <c>1/Frequency</c> years from today, premium accruing as <c>τ·AccrualFactor</c>, no step-in, accrued or
/// settlement lag. <see cref="Standard"/> builds the dated contract from a trade date and tenor: IMM maturity, a
/// <see cref="CdsSchedule"/> of business-day-adjusted Act/360 periods (the last one including the maturity day),
/// protection from the trade date to maturity inclusive, accrued premium from the accrual start to T inclusive,
/// and the upfront quoted clean at the T+3 cash settlement date. Model times are Act/365F from the trade date.
/// Within these conventions the integrals are exact (no time-stepping). Like the ISDA model, forward rates are
/// taken flat between curve nodes: exact for a <see cref="CurveInterpolation.LogLinearDiscount"/> curve, a small
/// approximation for other interpolations. The accrual-on-default integral is the exact one, not the 2009
/// "Markit fix" approximation some ISDA builds use.
/// </remarks>
/// <param name="Notional">Notional.</param>
/// <param name="Maturity">Maturity in years (protection end).</param>
/// <param name="Coupon">Standard running coupon (0.01 or 0.05).</param>
/// <param name="Recovery">Recovery rate assumption (0.4 senior, 0.25 subordinated).</param>
/// <param name="Frequency">Coupon payments per year (4 for standard contracts); ignored when <see cref="Schedule"/> is set.</param>
/// <param name="AccrualFactor">Accrual day-count factor applied to model-time year fractions; 365/360 for Act/360.</param>
public sealed record IsdaCds(double Notional, double Maturity, double Coupon = 0.01, double Recovery = 0.4, int Frequency = 4,
    double AccrualFactor = 365.0 / 360.0)
{
    /// <summary>Standard coupons.</summary>
    public const double InvestmentGradeCoupon = 0.01, HighYieldCoupon = 0.05;

    /// <summary>Dated premium schedule; null for the year-fraction form.</summary>
    public CdsSchedule? Schedule { get; init; }

    /// <summary>
    /// The standard CDS traded on <paramref name="tradeDate"/> with the given tenor (e.g. 5Y): IMM maturity under
    /// <paramref name="roll"/>, ISDA schedule on <paramref name="calendar"/> (weekends only when null), Act/360 premium.
    /// </summary>
    public static IsdaCds Standard(double notional, DateOnly tradeDate, Tenor tenor, double coupon = InvestmentGradeCoupon,
        double recovery = 0.4, BusinessCalendar? calendar = null, CdsRollConvention roll = CdsRollConvention.SemiAnnual2015)
        => FromSchedule(notional, CdsSchedule.Standard(tradeDate, tenor, calendar, roll), coupon, recovery);

    /// <summary>A dated CDS on an explicit schedule (Act/360 premium, protection from the trade date to maturity inclusive).</summary>
    public static IsdaCds FromSchedule(double notional, CdsSchedule schedule, double coupon = InvestmentGradeCoupon, double recovery = 0.4)
        => new(notional, schedule.TimeOf(schedule.MaturityDate), coupon, recovery, 4, 365.0 / 360.0) { Schedule = schedule };

    /// <summary>
    /// A premium period in model time: default observation window (S, E], coupon fraction α, payment time.
    /// The coupon is paid on survival to E; a default at u ∈ (S, E] pays accrual (u − S)·AccrualFactor.
    /// </summary>
    private readonly record struct Period(double S, double E, double Alpha, double Pay);

    private Period[] Periods()
    {
        if (Schedule is { } s)
            // ISDA observes survival one day before each accrual date, so a default on day d earns accrual through d.
            return s.Periods.Select(p => new Period(s.TimeOf(p.AccrualStart.AddDays(-1)), s.TimeOf(p.AccrualEnd.AddDays(-1)),
                p.AccrualFraction, s.TimeOf(p.PaymentDate))).ToArray();
        double tau = 1.0 / Frequency;
        int n = (int)Math.Round(Maturity * Frequency);
        return Enumerable.Range(1, n).Select(i => new Period((i - 1) * tau, i * tau, tau * AccrualFactor, i * tau)).ToArray();
    }

    /// <summary>Accrued Act/360 fraction at step-in (accrual start to T inclusive); 0 in the year-fraction form.</summary>
    public double AccruedFraction => Schedule?.AccruedFraction ?? 0;

    /// <summary>Accrued premium per unit notional: Coupon · <see cref="AccruedFraction"/>.</summary>
    public double AccruedPremium => Coupon * AccruedFraction;

    private double CashSettlementDiscount(YieldCurve discount)
        => Schedule is { } s ? discount.DiscountFactor(s.TimeOf(s.CashSettlementDate)) : 1;

    /// <summary>Union of hazard pillars, discount pillars and the given dates inside (a, b], sorted.</summary>
    private static double[] Knots(double a, double b, YieldCurve discount, HazardCurve hazard)
        => hazard.Times.Concat(discount.Times).Where(t => t > a && t < b).Append(b).Distinct().Order().ToArray();

    private static double HazardAt(HazardCurve h, double t)
    {
        for (int i = 0; i < h.Times.Count; i++) if (t <= h.Times[i]) return h.Hazards[i];
        return h.Hazards[^1];
    }

    /// <summary>
    /// Protection leg per unit notional, before (1 − R): Σ over sub-intervals of P·Q at the start times
    /// λ/(λ + f)·(1 − e^{−(λ + f)Δ}), exact for constant λ and f on each piece.
    /// </summary>
    public double ProtectionLegValue(YieldCurve discount, HazardCurve hazard)
    {
        double pv = 0, a = 0;
        foreach (var b in Knots(0, Maturity, discount, hazard))
        {
            double dt = b - a, pq = discount.DiscountFactor(a) * hazard.SurvivalProbability(a);
            double lambda = HazardAt(hazard, 0.5 * (a + b));
            double f = -Math.Log(discount.DiscountFactor(b) / discount.DiscountFactor(a)) / dt;
            double rho = lambda + f;
            pv += pq * (Math.Abs(rho) < 1e-14 ? lambda * dt : lambda / rho * (1 - Math.Exp(-rho * dt)));
            a = b;
        }
        return Notional * (1 - Recovery) * pv;
    }

    /// <summary>
    /// Risky PV01 per unit coupon and unit notional, with the full first coupon: Σ αᵢ·P(payᵢ)Q(Eᵢ) plus the accrual
    /// paid on default, ∫ (u − Sᵢ)·AccrualFactor·λP(u)Q(u) du over (max(Sᵢ, 0), Eᵢ], in closed form on each sub-interval.
    /// </summary>
    public double RiskyPv01(YieldCurve discount, HazardCurve hazard)
    {
        double pv = 0;
        foreach (var p in Periods())
        {
            pv += p.Alpha * discount.DiscountFactor(p.Pay) * hazard.SurvivalProbability(p.E);
            double a = Math.Max(p.S, 0);
            foreach (var b in Knots(a, p.E, discount, hazard))
            {
                double dt = b - a, pq = discount.DiscountFactor(a) * hazard.SurvivalProbability(a);
                double lambda = HazardAt(hazard, 0.5 * (a + b));
                double f = -Math.Log(discount.DiscountFactor(b) / discount.DiscountFactor(a)) / dt;
                double rho = lambda + f, c = a - p.S, e = Math.Exp(-rho * dt);
                // ∫₀^Δ (c + v)·λ·e^{−ρv} dv
                double integral = Math.Abs(rho) < 1e-12
                    ? lambda * (c * dt + 0.5 * dt * dt)
                    : lambda * (c * (1 - e) / rho + (1 - e * (1 + rho * dt)) / (rho * rho));
                pv += AccrualFactor * pq * integral;
                a = b;
            }
        }
        return pv;
    }

    /// <summary>
    /// Clean risky PV01: <see cref="RiskyPv01"/> less the accrued rebated at cash settlement,
    /// AccruedFraction·P(t_cs). Equal to the full RPV01 in the year-fraction form.
    /// </summary>
    public double CleanRiskyPv01(YieldCurve discount, HazardCurve hazard)
        => RiskyPv01(discount, hazard) - AccruedFraction * CashSettlementDiscount(discount);

    /// <summary>Par spread: protection / clean RPV01 (the coupon at which the clean upfront is zero).</summary>
    public double ParSpread(YieldCurve discount, HazardCurve hazard)
        => ProtectionLegValue(discount, hazard) / (Notional * CleanRiskyPv01(discount, hazard));

    /// <summary>
    /// Clean upfront ("points upfront") paid by the protection buyer, as a fraction of notional, valued at the cash
    /// settlement date: (protection/N − coupon·clean RPV01) / P(t_cs). Positive when the credit trades wider than the
    /// coupon. In the year-fraction form t_cs = 0 and there is no accrued.
    /// </summary>
    public double Upfront(YieldCurve discount, HazardCurve hazard)
        => (ProtectionLegValue(discount, hazard) / Notional - Coupon * CleanRiskyPv01(discount, hazard)) / CashSettlementDiscount(discount);

    /// <summary>Cash the buyer pays at settlement (currency): N·(clean upfront − accrued premium).</summary>
    public double CashSettlement(YieldCurve discount, HazardCurve hazard) => Notional * (Upfront(discount, hazard) - AccruedPremium);

    /// <summary>The flat hazard rate whose par spread equals <paramref name="quotedSpread"/> (the market convention).</summary>
    public double FlatHazardFromQuotedSpread(YieldCurve discount, double quotedSpread)
        => RootFinding.Brent(l => ParSpread(discount, HazardCurve.Flat(l)) - quotedSpread, 1e-10, 10, 1e-15);

    /// <summary>Upfront (fraction of notional) implied by a quoted (conventional) spread.</summary>
    public double UpfrontFromQuotedSpread(YieldCurve discount, double quotedSpread)
        => Upfront(discount, HazardCurve.Flat(FlatHazardFromQuotedSpread(discount, quotedSpread)));

    /// <summary>Quoted (conventional) spread implied by an upfront.</summary>
    public double QuotedSpreadFromUpfront(YieldCurve discount, double upfront)
    {
        double lambda = RootFinding.Brent(l => Upfront(discount, HazardCurve.Flat(l)) - upfront, 1e-10, 10, 1e-15);
        return ParSpread(discount, HazardCurve.Flat(lambda));
    }

    /// <summary>CS01: change in the buyer's value (currency) for a +1bp move in the quoted spread.</summary>
    public double Cs01(YieldCurve discount, double quotedSpread)
        => Notional * (UpfrontFromQuotedSpread(discount, quotedSpread + 1e-4) - UpfrontFromQuotedSpread(discount, quotedSpread));
}
