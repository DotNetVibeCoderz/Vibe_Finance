namespace Finance.Pricing.Rates;

/// <summary>
/// Forward inflation index curve, stored as the ratio I(T)/I(0) at pillars. Between pillars, log I is
/// interpolated linearly (a constant forward inflation rate); beyond the last pillar, the last forward rate
/// continues. Seasonality and indexation lag are not modelled.
/// </summary>
public sealed class InflationCurve
{
    private readonly double[] _times, _logRatios;

    public InflationCurve(double baseIndex, double[] times, double[] indexRatios)
    {
        Finance.Core.Guard.SameLength(times.Length, indexRatios.Length, "times and index ratios");
        if (times.Length == 0) throw new ArgumentException("An inflation curve needs at least one pillar.");
        BaseIndex = baseIndex;
        _times = (double[])times.Clone();
        _logRatios = indexRatios.Select(r => Math.Log(r)).ToArray();
    }

    /// <summary>Bootstraps from zero-coupon inflation swap par rates: I(T)/I(0) = (1 + K_T)^T.</summary>
    public static InflationCurve FromZeroCouponSwaps(double baseIndex, double[] maturities, double[] parRates)
        => new(baseIndex, maturities, maturities.Select((t, i) => Math.Pow(1 + parRates[i], t)).ToArray());

    /// <summary>I(0).</summary>
    public double BaseIndex { get; }

    /// <summary>Forward index ratio I(T)/I(0).</summary>
    public double IndexRatio(double t)
    {
        if (t <= 0) return 1;
        double logR;
        if (t <= _times[0]) logR = _logRatios[0] * t / _times[0];
        else if (t >= _times[^1])
        {
            double slope = _times.Length > 1
                ? (_logRatios[^1] - _logRatios[^2]) / (_times[^1] - _times[^2])
                : _logRatios[0] / _times[0];
            logR = _logRatios[^1] + slope * (t - _times[^1]);
        }
        else
        {
            int i = Array.BinarySearch(_times, t);
            if (i >= 0) logR = _logRatios[i];
            else
            {
                i = ~i;
                double w = (t - _times[i - 1]) / (_times[i] - _times[i - 1]);
                logR = (1 - w) * _logRatios[i - 1] + w * _logRatios[i];
            }
        }
        return Math.Exp(logR);
    }

    /// <summary>Forward index level I(T).</summary>
    public double ForwardIndex(double t) => BaseIndex * IndexRatio(t);

    /// <summary>Zero-coupon inflation rate (annual compounding) to T.</summary>
    public double ZeroInflationRate(double t) => Math.Pow(IndexRatio(t), 1 / t) - 1;
}

/// <summary>
/// Zero-coupon inflation swap: at maturity one side pays N·[(1 + K)^T − 1], the other N·[I(T)/I(0) − 1].
/// </summary>
public sealed record ZeroCouponInflationSwap(double Notional, double FixedRate, double Maturity, bool PayFixed = true)
{
    public double InflationLegValue(YieldCurve discount, InflationCurve inflation)
        => Notional * (inflation.IndexRatio(Maturity) - 1) * discount.DiscountFactor(Maturity);

    public double FixedLegValue(YieldCurve discount)
        => Notional * (Math.Pow(1 + FixedRate, Maturity) - 1) * discount.DiscountFactor(Maturity);

    /// <summary>NPV to the holder (fixed payer receives inflation).</summary>
    public double Npv(YieldCurve discount, InflationCurve inflation)
    {
        double v = InflationLegValue(discount, inflation) - FixedLegValue(discount);
        return PayFixed ? v : -v;
    }

    /// <summary>Breakeven fixed rate.</summary>
    public double ParRate(InflationCurve inflation) => inflation.ZeroInflationRate(Maturity);
}

/// <summary>
/// Year-on-year inflation swap: each period the inflation leg pays N·(I(tᵢ)/I(tᵢ₋₁) − 1) against N·τ·K. Forward
/// index ratios come straight from the curve, so the YoY convexity adjustment (which depends on the
/// correlation of inflation and nominal rates) is ignored. It is small for short tenors, but it is not zero.
/// </summary>
public sealed record YearOnYearInflationSwap(double Notional, double FixedRate, double Maturity, int Frequency = 1, bool PayFixed = true)
{
    private int Periods => (int)Math.Round(Maturity * Frequency);
    private double Tau => 1.0 / Frequency;

    public double InflationLegValue(YieldCurve discount, InflationCurve inflation)
    {
        double v = 0;
        for (int i = 1; i <= Periods; i++)
            v += (inflation.IndexRatio(i * Tau) / inflation.IndexRatio((i - 1) * Tau) - 1) * discount.DiscountFactor(i * Tau);
        return Notional * v;
    }

    /// <summary>Σ τ·P(tᵢ) per unit notional.</summary>
    public double Annuity(YieldCurve discount)
    {
        double a = 0;
        for (int i = 1; i <= Periods; i++) a += Tau * discount.DiscountFactor(i * Tau);
        return a;
    }

    public double FixedLegValue(YieldCurve discount) => Notional * FixedRate * Annuity(discount);

    public double Npv(YieldCurve discount, InflationCurve inflation)
    {
        double v = InflationLegValue(discount, inflation) - FixedLegValue(discount);
        return PayFixed ? v : -v;
    }

    /// <summary>Breakeven fixed rate.</summary>
    public double ParRate(YieldCurve discount, InflationCurve inflation)
        => InflationLegValue(discount, inflation) / (Notional * Annuity(discount));
}

/// <summary>One leg of a cross-currency swap, in its own currency.</summary>
/// <param name="Notional">Leg notional in the leg's currency.</param>
/// <param name="Discount">Discount curve for this currency's cash flows (for a collateralised trade, the
/// curve consistent with the collateral currency, i.e. basis-adjusted for the non-collateral leg).</param>
/// <param name="Forecast">Forward curve for a floating leg; null makes the leg fixed.</param>
/// <param name="Rate">Fixed coupon (fixed leg) or spread over the floating index (floating leg).</param>
/// <param name="Frequency">Payments per year.</param>
public sealed record CrossCurrencyLeg(double Notional, YieldCurve Discount, YieldCurve? Forecast, double Rate, int Frequency = 4)
{
    public bool IsFloating => Forecast is not null;

    /// <summary>PV of coupons plus the final notional, minus the initial exchange at <paramref name="start"/>.</summary>
    public double Value(double start, double maturity)
    {
        double tau = 1.0 / Frequency, pv = 0;
        int n = (int)Math.Round((maturity - start) * Frequency);
        for (int i = 1; i <= n; i++)
        {
            double t0 = start + (i - 1) * tau, t1 = start + i * tau;
            double coupon = IsFloating
                ? (Forecast!.DiscountFactor(t0) / Forecast.DiscountFactor(t1) - 1) / tau + Rate
                : Rate;
            pv += coupon * tau * Discount.DiscountFactor(t1);
        }
        pv += Discount.DiscountFactor(maturity) - Discount.DiscountFactor(start);
        return Notional * pv;
    }

    /// <summary>PV of one unit of spread/coupon on this leg: Σ τ P(tᵢ)·N.</summary>
    public double Annuity(double start, double maturity)
    {
        double tau = 1.0 / Frequency, a = 0;
        int n = (int)Math.Round((maturity - start) * Frequency);
        for (int i = 1; i <= n; i++) a += tau * Discount.DiscountFactor(start + i * tau);
        return Notional * a;
    }
}

/// <summary>
/// Constant-notional cross-currency swap with initial and final notional exchange: receive the domestic leg,
/// pay the foreign leg. Valued in domestic currency as PV_dom − S·PV_for, where S is the spot FX rate
/// (domestic per unit of foreign). Mark-to-market resetting notionals are not modelled.
/// </summary>
public sealed record CrossCurrencySwap(CrossCurrencyLeg Domestic, CrossCurrencyLeg Foreign, double Maturity, double Start = 0)
{
    /// <summary>NPV in domestic currency to the receiver of the domestic leg.</summary>
    public double Npv(double spotFx) => Domestic.Value(Start, Maturity) - spotFx * Foreign.Value(Start, Maturity);

    /// <summary>
    /// The spread on the foreign leg that makes the swap worth zero: the cross-currency basis implied by these curves.
    /// </summary>
    public double ParForeignSpread(double spotFx)
    {
        var zero = this with { Foreign = Foreign with { Rate = 0 } };
        return zero.Npv(spotFx) / (spotFx * Foreign.Annuity(Start, Maturity));
    }
}
