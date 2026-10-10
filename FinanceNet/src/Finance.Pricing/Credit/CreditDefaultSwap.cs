using Finance.Mathematics;
using Finance.Pricing.Rates;

namespace Finance.Pricing.Credit;

/// <summary>Piecewise-constant hazard-rate curve: λ(t) = Hazards[i] on (Times[i-1], Times[i]], flat beyond the last pillar.</summary>
public sealed class HazardCurve
{
    private readonly double[] _times, _hazards;

    public HazardCurve(double[] times, double[] hazards)
    {
        Finance.Core.Guard.SameLength(times.Length, hazards.Length, "times and hazards");
        if (times.Length == 0) throw new ArgumentException("Need at least one pillar.");
        _times = (double[])times.Clone();
        _hazards = (double[])hazards.Clone();
    }

    public static HazardCurve Flat(double hazard) => new(new[] { 100.0 }, new[] { hazard });

    public IReadOnlyList<double> Times => _times;
    public IReadOnlyList<double> Hazards => _hazards;

    /// <summary>Survival probability Q(t) = exp(-∫λ).</summary>
    public double SurvivalProbability(double t)
    {
        if (t <= 0) return 1;
        double integral = 0, prev = 0;
        for (int i = 0; i < _times.Length; i++)
        {
            double end = Math.Min(t, _times[i]);
            integral += _hazards[i] * (end - prev);
            prev = _times[i];
            if (t <= _times[i]) return Math.Exp(-integral);
        }
        integral += _hazards[^1] * (t - prev);
        return Math.Exp(-integral);
    }

    /// <summary>Default probability by time t.</summary>
    public double DefaultProbability(double t) => 1 - SurvivalProbability(t);
}

/// <summary>
/// Credit default swap under the reduced-form (hazard-rate) model, with accrual-on-default on the premium leg
/// and the protection leg integrated on a fine (weekly) grid.
/// </summary>
/// <param name="Notional">Notional.</param>
/// <param name="Spread">Running spread (decimal, 0.01 = 100bp).</param>
/// <param name="Maturity">Maturity in years.</param>
/// <param name="Frequency">Premium payments per year.</param>
/// <param name="Recovery">Recovery rate.</param>
public sealed record CreditDefaultSwap(double Notional, double Spread, double Maturity, int Frequency = 4, double Recovery = 0.4)
{
    /// <summary>Risky annuity (risky PV01 per unit spread, per unit notional) including accrual on default.</summary>
    public double RiskyAnnuity(YieldCurve discount, HazardCurve hazard)
    {
        double tau = 1.0 / Frequency, a = 0;
        int n = (int)Math.Round(Maturity * Frequency);
        for (int i = 1; i <= n; i++)
        {
            double t0 = (i - 1) * tau, t1 = i * tau;
            double q0 = hazard.SurvivalProbability(t0), q1 = hazard.SurvivalProbability(t1);
            double df = discount.DiscountFactor(t1);
            a += tau * df * q1 + 0.5 * tau * discount.DiscountFactor(0.5 * (t0 + t1)) * (q0 - q1);
        }
        return a;
    }

    /// <summary>Premium (fee) leg PV.</summary>
    public double PremiumLegValue(YieldCurve discount, HazardCurve hazard) => Notional * Spread * RiskyAnnuity(discount, hazard);

    /// <summary>Protection (contingent) leg PV.</summary>
    public double ProtectionLegValue(YieldCurve discount, HazardCurve hazard)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(Maturity * 52));
        double dt = Maturity / steps, pv = 0, qPrev = 1;
        for (int i = 1; i <= steps; i++)
        {
            double t = i * dt, q = hazard.SurvivalProbability(t);
            pv += discount.DiscountFactor(t - 0.5 * dt) * (qPrev - q);
            qPrev = q;
        }
        return Notional * (1 - Recovery) * pv;
    }

    /// <summary>Value to the protection buyer: protection - premium.</summary>
    public double Npv(YieldCurve discount, HazardCurve hazard) => ProtectionLegValue(discount, hazard) - PremiumLegValue(discount, hazard);

    /// <summary>Par spread making the CDS worth zero.</summary>
    public double ParSpread(YieldCurve discount, HazardCurve hazard)
        => ProtectionLegValue(discount, hazard) / (Notional * RiskyAnnuity(discount, hazard));

    /// <summary>Risky duration (risky annuity).</summary>
    public double RiskyDuration(YieldCurve discount, HazardCurve hazard) => RiskyAnnuity(discount, hazard);

    /// <summary>
    /// Bootstraps a piecewise-constant hazard curve from par spreads quoted at increasing maturities.
    /// </summary>
    public static HazardCurve BootstrapHazard(YieldCurve discount, double[] maturities, double[] parSpreads, double recovery = 0.4, int frequency = 4)
    {
        Finance.Core.Guard.SameLength(maturities.Length, parSpreads.Length, "maturities and spreads");
        var times = new List<double>();
        var hazards = new List<double>();
        for (int k = 0; k < maturities.Length; k++)
        {
            times.Add(maturities[k]);
            hazards.Add(parSpreads[k] / (1 - recovery));
            var cds = new CreditDefaultSwap(1, parSpreads[k], maturities[k], frequency, recovery);
            var t = times.ToArray();
            var h = hazards.ToArray();
            double Err(double lambda)
            {
                h[^1] = lambda;
                return cds.Npv(discount, new HazardCurve(t, h));
            }
            hazards[k] = RootFinding.Brent(Err, 1e-8, 5, 1e-14);
        }
        return new HazardCurve(times.ToArray(), hazards.ToArray());
    }

    /// <summary>Credit-triangle approximation λ ≈ s / (1 - R).</summary>
    public static double CreditTriangleHazard(double spread, double recovery = 0.4) => spread / (1 - recovery);
}
