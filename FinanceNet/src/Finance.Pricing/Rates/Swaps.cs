using Finance.Pricing.Options;

namespace Finance.Pricing.Rates;

/// <summary>
/// Vanilla fixed-for-floating interest-rate swap starting at <paramref name="Start"/> (years) and
/// maturing at <paramref name="Maturity"/>. Supports dual-curve valuation (separate forecast curve).
/// </summary>
/// <param name="Notional">Notional amount.</param>
/// <param name="FixedRate">Fixed coupon rate.</param>
/// <param name="Maturity">End time in years.</param>
/// <param name="PayFixed">True for a payer swap (pay fixed, receive float).</param>
/// <param name="FixedFrequency">Fixed-leg payments per year.</param>
/// <param name="FloatFrequency">Floating-leg payments per year.</param>
/// <param name="Start">Start time in years (0 = spot-starting, positive = forward-starting).</param>
public sealed record InterestRateSwap(double Notional, double FixedRate, double Maturity, bool PayFixed = true,
    int FixedFrequency = 1, int FloatFrequency = 4, double Start = 0)
{
    /// <summary>Fixed-leg annuity Σ τ P(0, t_i) per unit notional.</summary>
    public double Annuity(YieldCurve discount)
    {
        double tau = 1.0 / FixedFrequency, a = 0;
        int n = (int)Math.Round((Maturity - Start) * FixedFrequency);
        for (int i = 1; i <= n; i++) a += tau * discount.DiscountFactor(Start + i * tau);
        return a;
    }

    /// <summary>Present value of the fixed leg (positive number).</summary>
    public double FixedLegValue(YieldCurve discount) => Notional * FixedRate * Annuity(discount);

    /// <summary>Present value of the floating leg (positive number) using forwards from <paramref name="forecast"/>.</summary>
    public double FloatLegValue(YieldCurve discount, YieldCurve? forecast = null)
    {
        forecast ??= discount;
        double tau = 1.0 / FloatFrequency, pv = 0;
        int n = (int)Math.Round((Maturity - Start) * FloatFrequency);
        for (int i = 1; i <= n; i++)
        {
            double t0 = Start + (i - 1) * tau, t1 = Start + i * tau;
            double fwd = (forecast.DiscountFactor(t0) / forecast.DiscountFactor(t1) - 1) / tau;
            pv += fwd * tau * discount.DiscountFactor(t1);
        }
        return Notional * pv;
    }

    /// <summary>Net present value to the holder (payer: float - fixed).</summary>
    public double Npv(YieldCurve discount, YieldCurve? forecast = null)
    {
        double v = FloatLegValue(discount, forecast) - FixedLegValue(discount);
        return PayFixed ? v : -v;
    }

    /// <summary>Fixed rate that makes the swap worth zero.</summary>
    public double ParRate(YieldCurve discount, YieldCurve? forecast = null)
        => FloatLegValue(discount, forecast) / (Notional * Annuity(discount));

    /// <summary>Change in NPV for a +1bp parallel shift of both curves.</summary>
    public double Dv01(YieldCurve discount, YieldCurve? forecast = null)
    {
        var f = forecast ?? discount;
        return Npv(discount.Shifted(1e-4), f.Shifted(1e-4)) - Npv(discount, forecast);
    }
}

/// <summary>European swaption on a swap starting at <paramref name="Expiry"/> with length <paramref name="Tenor"/>.</summary>
public sealed record Swaption(double Expiry, double Tenor, double Strike, bool Payer = true, double Notional = 1, int Frequency = 1)
{
    private InterestRateSwap Underlying => new(Notional, Strike, Expiry + Tenor, Payer, Frequency, Frequency, Expiry);

    /// <summary>Forward swap rate.</summary>
    public double ForwardSwapRate(YieldCurve curve)
        => (curve.DiscountFactor(Expiry) - curve.DiscountFactor(Expiry + Tenor)) / Underlying.Annuity(curve);

    /// <summary>Forward annuity (per unit notional).</summary>
    public double Annuity(YieldCurve curve) => Underlying.Annuity(curve);

    /// <summary>Price with a Black (lognormal) swaption volatility.</summary>
    public double PriceBlack(YieldCurve curve, double volatility)
        => Notional * Annuity(curve) * Black76.Undiscounted(Payer ? OptionType.Call : OptionType.Put, ForwardSwapRate(curve), Strike, Expiry, volatility);

    /// <summary>Price with a Bachelier (normal, absolute) swaption volatility.</summary>
    public double PriceBachelier(YieldCurve curve, double normalVolatility)
        => Notional * Annuity(curve) * Bachelier.Undiscounted(Payer ? OptionType.Call : OptionType.Put, ForwardSwapRate(curve), Strike, Expiry, normalVolatility);
}

/// <summary>Forwards and futures under cost-of-carry.</summary>
public static class Forwards
{
    /// <summary>Forward price F = S·exp((r + storage - q - convenience)·T).</summary>
    public static double Price(double spot, double rate, double time, double dividendYield = 0, double storageCost = 0, double convenienceYield = 0)
        => spot * Math.Exp((rate + storageCost - dividendYield - convenienceYield) * time);

    /// <summary>Forward price of an asset paying known discrete income with present value <paramref name="incomePv"/>.</summary>
    public static double PriceWithIncome(double spot, double incomePv, double rate, double time) => (spot - incomePv) * Math.Exp(rate * time);

    /// <summary>Value of an existing long forward with delivery price <paramref name="deliveryPrice"/>.</summary>
    public static double Value(double forwardPrice, double deliveryPrice, double rate, double time)
        => (forwardPrice - deliveryPrice) * Math.Exp(-rate * time);

    /// <summary>Basis = spot - futures.</summary>
    public static double Basis(double spot, double futures) => spot - futures;

    /// <summary>Implied repo (carry) rate from a spot/futures pair.</summary>
    public static double ImpliedRepoRate(double spot, double futures, double time, double dividendYield = 0)
        => Math.Log(futures / spot) / time + dividendYield;

    /// <summary>FX forward by covered interest parity: F = S·exp((r_dom - r_for)·T). Spot quoted as domestic per foreign.</summary>
    public static double FxForward(double spot, double domesticRate, double foreignRate, double time)
        => spot * Math.Exp((domesticRate - foreignRate) * time);

    /// <summary>FX forward points (F - S) scaled by <paramref name="pipFactor"/> (10,000 for most pairs).</summary>
    public static double FxForwardPoints(double spot, double domesticRate, double foreignRate, double time, double pipFactor = 10_000)
        => (FxForward(spot, domesticRate, foreignRate, time) - spot) * pipFactor;

    /// <summary>Hedge ratio (minimum-variance) h = ρ σ_S / σ_F.</summary>
    public static double MinimumVarianceHedgeRatio(double correlation, double spotVol, double futuresVol) => correlation * spotVol / futuresVol;
}
