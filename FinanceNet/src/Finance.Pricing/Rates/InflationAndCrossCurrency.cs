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
/// Convexity adjustment for year-on-year inflation forwards in the lognormal forward-CPI market model of
/// Mercurio (2005), "Pricing inflation-indexed derivatives", Quantitative Finance 5(3) (also Brigo &amp; Mercurio,
/// <i>Interest Rate Models</i>, 2nd ed., §16.3). Each forward CPI Iⱼ(t) = E^{Tⱼ}[I(Tⱼ)] is a driftless lognormal
/// under its own Tⱼ-forward measure with volatility σ_I; the nominal forward rate Fᵢ for (Tᵢ₋₁, Tᵢ] is lognormal
/// with volatility σ_N. Under the Tᵢ-forward measure the YoY ratio has expectation
/// <code>
/// E^{Tᵢ}[I(Tᵢ)/I(Tᵢ₋₁)] = Iᵢ(0)/Iᵢ₋₁(0) · exp(Dᵢ),
/// Dᵢ = σ_I·Tᵢ₋₁·[ ρ_{I,N}·σ_N·τᵢFᵢ(0)/(1 + τᵢFᵢ(0)) − ρ_{I,I}·σ_I + σ_I ]
/// </code>
/// where ρ_{I,N} is the correlation of the forward CPI Iᵢ₋₁ with the nominal forward Fᵢ and ρ_{I,I} the correlation
/// of the consecutive forward CPIs Iᵢ₋₁ and Iᵢ (the nominal forward is frozen at its time-0 value in the drift, the
/// usual approximation). The first period (Tᵢ₋₁ = 0) needs no adjustment, and zero volatilities give exp(D) = 1.
/// </summary>
/// <param name="InflationVol">Forward-CPI volatility σ_I (same for every forward index).</param>
/// <param name="NominalVol">Lognormal volatility σ_N of the nominal forward rates.</param>
/// <param name="InflationNominalCorrelation">ρ_{I,N}: forward CPI vs nominal forward rate.</param>
/// <param name="InflationCorrelation">ρ_{I,I}: correlation of consecutive forward CPIs (1 when they move together).</param>
public sealed record YoYConvexityModel(double InflationVol, double NominalVol, double InflationNominalCorrelation, double InflationCorrelation = 1)
{
    /// <summary>The exponent Dᵢ for a YoY period (tPrev, tPrev + tau] whose simple nominal forward rate is <paramref name="nominalForward"/>.</summary>
    public double Exponent(double tPrev, double tau, double nominalForward)
    {
        if (tPrev <= 0) return 0;
        double x = tau * nominalForward / (1 + tau * nominalForward);
        return InflationVol * tPrev * (InflationNominalCorrelation * NominalVol * x - InflationCorrelation * InflationVol + InflationVol);
    }

    /// <summary>Multiplicative adjustment exp(Dᵢ) applied to the curve's forward YoY ratio.</summary>
    public double Factor(double tPrev, double tau, double nominalForward) => Math.Exp(Exponent(tPrev, tau, nominalForward));
}

/// <summary>
/// Year-on-year inflation swap: each period the inflation leg pays N·(I(tᵢ)/I(tᵢ₋₁) − 1) against N·τ·K. Without a
/// <see cref="YoYConvexityModel"/> the expected ratio is the curve's forward ratio Iᵢ(0)/Iᵢ₋₁(0), which ignores the
/// convexity from the payment being made at tᵢ (small for short tenors, but not zero). Passing a model applies the
/// Mercurio market-model adjustment, with nominal forwards taken from the discount curve.
/// </summary>
public sealed record YearOnYearInflationSwap(double Notional, double FixedRate, double Maturity, int Frequency = 1, bool PayFixed = true)
{
    private int Periods => (int)Math.Round(Maturity * Frequency);
    private double Tau => 1.0 / Frequency;

    /// <summary>
    /// Expected YoY index ratio E^{Tᵢ}[I(tᵢ)/I(tᵢ₋₁)] for period <paramref name="i"/> (1-based): the curve ratio,
    /// times the convexity factor when <paramref name="convexity"/> is given.
    /// </summary>
    public double ExpectedRatio(int i, YieldCurve discount, InflationCurve inflation, YoYConvexityModel? convexity = null)
    {
        double t0 = (i - 1) * Tau, t1 = i * Tau;
        double ratio = inflation.IndexRatio(t1) / inflation.IndexRatio(t0);
        if (convexity is null) return ratio;
        double fwd = (discount.DiscountFactor(t0) / discount.DiscountFactor(t1) - 1) / Tau;
        return ratio * convexity.Factor(t0, Tau, fwd);
    }

    public double InflationLegValue(YieldCurve discount, InflationCurve inflation, YoYConvexityModel? convexity = null)
    {
        double v = 0;
        for (int i = 1; i <= Periods; i++)
            v += (ExpectedRatio(i, discount, inflation, convexity) - 1) * discount.DiscountFactor(i * Tau);
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

    public double Npv(YieldCurve discount, InflationCurve inflation, YoYConvexityModel? convexity = null)
    {
        double v = InflationLegValue(discount, inflation, convexity) - FixedLegValue(discount);
        return PayFixed ? v : -v;
    }

    /// <summary>Breakeven fixed rate.</summary>
    public double ParRate(YieldCurve discount, InflationCurve inflation, YoYConvexityModel? convexity = null)
        => InflationLegValue(discount, inflation, convexity) / (Notional * Annuity(discount));
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
/// (domestic per unit of foreign). For the mark-to-market resetting variant see <see cref="MtmCrossCurrencySwap"/>.
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

/// <summary>One reset period of a <see cref="MtmCrossCurrencySwap"/>, in model time.</summary>
/// <param name="Start">Period start tᵢ₋₁ (the reset fixing).</param>
/// <param name="End">Period end tᵢ (coupon payment and notional exchange).</param>
/// <param name="ForwardFx">Forward FX X(tᵢ₋₁) = S·P_for(tᵢ₋₁)/P_dom(tᵢ₋₁), domestic per unit of foreign.</param>
/// <param name="ResettingNotional">Notional of the resetting leg for this period, in that leg's currency.</param>
/// <param name="NotionalExchange">Net notional the resetting leg's receiver gets at <paramref name="End"/>: Nᵢ − Nᵢ₊₁,
/// or Nₙ at maturity (the initial exchange −N₁ happens at the first period's start).</param>
public sealed record MtmResetPeriod(double Start, double End, double ForwardFx, double ResettingNotional, double NotionalExchange);

/// <summary>
/// Mark-to-market resetting cross-currency swap: receive the domestic leg, pay the foreign leg, NPV in domestic
/// currency. The non-resetting leg keeps a constant notional with initial and final exchange. The resetting leg's
/// notional is reset at the start of every one of its periods to the then-prevailing FX rate times the constant
/// notional (Nᵢ = N_for·X(tᵢ₋₁) when the domestic leg resets, Nᵢ = N_dom / X(tᵢ₋₁) when the foreign leg resets), and
/// at each reset the difference Nᵢ − Nᵢ₊₁ is exchanged, so the FX exposure never builds up beyond one period.
/// </summary>
/// <remarks>
/// Valued on deterministic curves: the reset FX is the forward X(t) = S·P_for(t)/P_dom(t) from the two legs' discount
/// curves (the collateral-consistent curves), so the resetting leg is a strip of one-period constant-notional legs
/// struck at forward FX. The small convexity from FX/rate correlation over each fixing-to-payment period is ignored.
/// With each leg forecast and discounted on one curve and zero spreads, every one-period resetting leg is worth zero
/// and the swap is worth zero. The resetting leg's <see cref="CrossCurrencyLeg.Notional"/> is not used (it is set by
/// the resets); its reset periods follow its own <see cref="CrossCurrencyLeg.Frequency"/>.
/// </remarks>
/// <param name="Domestic">Domestic leg (received).</param>
/// <param name="Foreign">Foreign leg (paid).</param>
/// <param name="Maturity">Maturity in years.</param>
/// <param name="Start">Start in years (a forward-starting swap fixes its first notional at the forward FX).</param>
/// <param name="ResetDomestic">True when the domestic leg resets (e.g. the USD leg of a EUR/USD MtM swap valued in USD); false when the foreign leg does.</param>
public sealed record MtmCrossCurrencySwap(CrossCurrencyLeg Domestic, CrossCurrencyLeg Foreign, double Maturity, double Start = 0, bool ResetDomestic = true)
{
    private CrossCurrencyLeg Resetting => ResetDomestic ? Domestic : Foreign;

    /// <summary>Forward FX (domestic per foreign) for time <paramref name="t"/>: S·P_for(t)/P_dom(t).</summary>
    public double ForwardFx(double spotFx, double t) => spotFx * Foreign.Discount.DiscountFactor(t) / Domestic.Discount.DiscountFactor(t);

    /// <summary>Reset periods with forward FX, the resetting notional and the notional exchanged at each period end.</summary>
    public IReadOnlyList<MtmResetPeriod> ResetSchedule(double spotFx)
    {
        double tau = 1.0 / Resetting.Frequency;
        int n = (int)Math.Round((Maturity - Start) * Resetting.Frequency);
        var notionals = new double[n];
        for (int i = 0; i < n; i++)
        {
            double x = ForwardFx(spotFx, Start + i * tau);
            notionals[i] = ResetDomestic ? Foreign.Notional * x : Domestic.Notional / x;
        }
        return Enumerable.Range(0, n).Select(i => new MtmResetPeriod(Start + i * tau, Start + (i + 1) * tau,
            ForwardFx(spotFx, Start + i * tau), notionals[i], i < n - 1 ? notionals[i] - notionals[i + 1] : notionals[i])).ToArray();
    }

    /// <summary>PV of the resetting leg in its own currency: Σ Nᵢ·[cᵢτP(tᵢ) + P(tᵢ) − P(tᵢ₋₁)].</summary>
    public double ResettingLegValue(double spotFx)
        => ResetSchedule(spotFx).Sum(p => (Resetting with { Notional = p.ResettingNotional }).Value(p.Start, p.End));

    /// <summary>NPV in domestic currency to the receiver of the domestic leg.</summary>
    public double Npv(double spotFx)
        => ResetDomestic
            ? ResettingLegValue(spotFx) - spotFx * Foreign.Value(Start, Maturity)
            : Domestic.Value(Start, Maturity) - spotFx * ResettingLegValue(spotFx);

    /// <summary>The spread on the foreign leg that makes the swap worth zero (the MtM cross-currency basis).</summary>
    public double ParForeignSpread(double spotFx)
    {
        // The NPV is linear in the foreign spread.
        double v0 = (this with { Foreign = Foreign with { Rate = 0 } }).Npv(spotFx);
        double v1 = (this with { Foreign = Foreign with { Rate = 1 } }).Npv(spotFx);
        return v0 / (v0 - v1);
    }
}
