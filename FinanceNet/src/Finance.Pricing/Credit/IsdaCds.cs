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
/// What is simplified relative to the ISDA C library: times are year fractions from today rather than
/// business-day-adjusted IMM dates, there is no T+1 step-in or T+3 cash settlement, and the premium accrues
/// Act/360 as <c>τ·365/360</c> on calendar-year fractions. Within those conventions the integrals are exact
/// (no time-stepping), so the numbers match a hand evaluation of the model's formulas. Like the ISDA model, forward
/// rates are taken flat between curve nodes: exact for a <see cref="CurveInterpolation.LogLinearDiscount"/> curve,
/// a small approximation for other interpolations.
/// </remarks>
/// <param name="Notional">Notional.</param>
/// <param name="Maturity">Maturity in years.</param>
/// <param name="Coupon">Standard running coupon (0.01 or 0.05).</param>
/// <param name="Recovery">Recovery rate assumption (0.4 senior, 0.25 subordinated).</param>
/// <param name="Frequency">Coupon payments per year (4 for standard contracts).</param>
/// <param name="AccrualFactor">Accrual day-count factor applied to year fractions; 365/360 for Act/360.</param>
public sealed record IsdaCds(double Notional, double Maturity, double Coupon = 0.01, double Recovery = 0.4, int Frequency = 4,
    double AccrualFactor = 365.0 / 360.0)
{
    /// <summary>Standard coupons.</summary>
    public const double InvestmentGradeCoupon = 0.01, HighYieldCoupon = 0.05;

    private double[] PaymentTimes()
    {
        double tau = 1.0 / Frequency;
        int n = (int)Math.Round(Maturity * Frequency);
        return Enumerable.Range(1, n).Select(i => i * tau).ToArray();
    }

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
    /// Risky PV01 per unit coupon and unit notional: Σ τᵢ·P(tᵢ)Q(tᵢ) plus the accrual paid on default,
    /// ∫ (u − tᵢ₋₁)·λP(u)Q(u) du in closed form on each sub-interval.
    /// </summary>
    public double RiskyPv01(YieldCurve discount, HazardCurve hazard)
    {
        double pv = 0, prev = 0;
        foreach (var pay in PaymentTimes())
        {
            pv += (pay - prev) * AccrualFactor * discount.DiscountFactor(pay) * hazard.SurvivalProbability(pay);
            double a = prev;
            foreach (var b in Knots(prev, pay, discount, hazard))
            {
                double dt = b - a, pq = discount.DiscountFactor(a) * hazard.SurvivalProbability(a);
                double lambda = HazardAt(hazard, 0.5 * (a + b));
                double f = -Math.Log(discount.DiscountFactor(b) / discount.DiscountFactor(a)) / dt;
                double rho = lambda + f, c = a - prev, e = Math.Exp(-rho * dt);
                // ∫₀^Δ (c + v)·λ·e^{−ρv} dv
                double integral = Math.Abs(rho) < 1e-12
                    ? lambda * (c * dt + 0.5 * dt * dt)
                    : lambda * (c * (1 - e) / rho + (1 - e * (1 + rho * dt)) / (rho * rho));
                pv += AccrualFactor * pq * integral;
                a = b;
            }
            prev = pay;
        }
        return pv;
    }

    /// <summary>Par spread: protection / RPV01.</summary>
    public double ParSpread(YieldCurve discount, HazardCurve hazard)
        => ProtectionLegValue(discount, hazard) / (Notional * RiskyPv01(discount, hazard));

    /// <summary>
    /// Upfront paid by the protection buyer, as a fraction of notional: (protection − coupon·RPV01) / N.
    /// Positive when the credit trades wider than the coupon.
    /// </summary>
    public double Upfront(YieldCurve discount, HazardCurve hazard)
        => ProtectionLegValue(discount, hazard) / Notional - Coupon * RiskyPv01(discount, hazard);

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
