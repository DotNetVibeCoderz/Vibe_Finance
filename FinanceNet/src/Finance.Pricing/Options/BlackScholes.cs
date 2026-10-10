using Finance.Mathematics;

namespace Finance.Pricing.Options;

/// <summary>Call or put.</summary>
public enum OptionType { Call, Put }

/// <summary>European or American exercise.</summary>
public enum ExerciseStyle { European, American }

/// <summary>
/// First- and second-order sensitivities. Vega, volga and vanna are per 1.00 (100 vol points) of volatility,
/// theta is per year, rho per 1.00 of rate, and charm is the change in delta per year of calendar time.
/// </summary>
public readonly record struct Greeks(
    double Price, double Delta, double Gamma, double Vega, double Theta, double Rho,
    double Vanna, double Volga, double Charm)
{
    /// <summary>Vega per one volatility point (0.01).</summary>
    public double VegaPerPoint => Vega / 100;

    /// <summary>Theta per calendar day (365-day year).</summary>
    public double ThetaPerDay => Theta / 365;
}

/// <summary>
/// Black-Scholes-Merton model for European options on an asset with continuous dividend yield q.
/// </summary>
public static class BlackScholes
{
    /// <summary>Option price.</summary>
    /// <param name="type">Call or put.</param>
    /// <param name="spot">Spot price S.</param>
    /// <param name="strike">Strike K.</param>
    /// <param name="time">Time to expiry in years.</param>
    /// <param name="rate">Continuously compounded risk-free rate.</param>
    /// <param name="dividendYield">Continuous dividend (or foreign rate / cost of carry offset).</param>
    /// <param name="volatility">Annual volatility.</param>
    public static double Price(OptionType type, double spot, double strike, double time, double rate, double dividendYield, double volatility)
    {
        if (time <= 0 || volatility <= 0)
        {
            double fwdIntrinsic = type == OptionType.Call
                ? spot * Math.Exp(-dividendYield * Math.Max(time, 0)) - strike * Math.Exp(-rate * Math.Max(time, 0))
                : strike * Math.Exp(-rate * Math.Max(time, 0)) - spot * Math.Exp(-dividendYield * Math.Max(time, 0));
            return Math.Max(fwdIntrinsic, 0);
        }
        double sqrtT = Math.Sqrt(time);
        double d1 = (Math.Log(spot / strike) + (rate - dividendYield + 0.5 * volatility * volatility) * time) / (volatility * sqrtT);
        double d2 = d1 - volatility * sqrtT;
        double dfq = Math.Exp(-dividendYield * time), dfr = Math.Exp(-rate * time);
        return type == OptionType.Call
            ? spot * dfq * Normal.Cdf(d1) - strike * dfr * Normal.Cdf(d2)
            : strike * dfr * Normal.Cdf(-d2) - spot * dfq * Normal.Cdf(-d1);
    }

    /// <summary>Price without dividends.</summary>
    public static double Price(OptionType type, double spot, double strike, double time, double rate, double volatility)
        => Price(type, spot, strike, time, rate, 0, volatility);

    /// <summary>Price and all analytic greeks.</summary>
    public static Greeks Greeks(OptionType type, double spot, double strike, double time, double rate, double dividendYield, double volatility)
    {
        Finance.Core.Guard.Positive(time);
        Finance.Core.Guard.Positive(volatility);
        double sqrtT = Math.Sqrt(time);
        double vst = volatility * sqrtT;
        double d1 = (Math.Log(spot / strike) + (rate - dividendYield + 0.5 * volatility * volatility) * time) / vst;
        double d2 = d1 - vst;
        double dfq = Math.Exp(-dividendYield * time), dfr = Math.Exp(-rate * time);
        double nd1 = Normal.Pdf(d1);
        double Nd1 = Normal.Cdf(d1), Nd2 = Normal.Cdf(d2);

        double gamma = dfq * nd1 / (spot * vst);
        double vega = spot * dfq * nd1 * sqrtT;
        double vanna = -dfq * nd1 * d2 / volatility;
        double volga = vega * d1 * d2 / volatility;
        double charmCommon = dfq * nd1 * (2 * (rate - dividendYield) * time - d2 * vst) / (2 * time * vst);

        if (type == OptionType.Call)
        {
            double price = spot * dfq * Nd1 - strike * dfr * Nd2;
            double delta = dfq * Nd1;
            double theta = -spot * dfq * nd1 * volatility / (2 * sqrtT) - rate * strike * dfr * Nd2 + dividendYield * spot * dfq * Nd1;
            double rho = strike * time * dfr * Nd2;
            double charm = dividendYield * dfq * Nd1 - charmCommon;
            return new Greeks(price, delta, gamma, vega, theta, rho, vanna, volga, charm);
        }
        else
        {
            double Nm1 = Normal.Cdf(-d1), Nm2 = Normal.Cdf(-d2);
            double price = strike * dfr * Nm2 - spot * dfq * Nm1;
            double delta = -dfq * Nm1;
            double theta = -spot * dfq * nd1 * volatility / (2 * sqrtT) + rate * strike * dfr * Nm2 - dividendYield * spot * dfq * Nm1;
            double rho = -strike * time * dfr * Nm2;
            double charm = -dividendYield * dfq * Nm1 - charmCommon;
            return new Greeks(price, delta, gamma, vega, theta, rho, vanna, volga, charm);
        }
    }

    /// <summary>
    /// Implied volatility from a market price: Newton-Raphson on vega from the Brenner-Subrahmanyam guess,
    /// falling back to Brent on [1e-6, 10] when Newton leaves the domain. Returns NaN if the price violates
    /// no-arbitrage bounds.
    /// </summary>
    public static double ImpliedVolatility(OptionType type, double price, double spot, double strike, double time, double rate, double dividendYield = 0, double tolerance = 1e-10)
    {
        double dfq = Math.Exp(-dividendYield * time), dfr = Math.Exp(-rate * time);
        double lower = type == OptionType.Call ? Math.Max(spot * dfq - strike * dfr, 0) : Math.Max(strike * dfr - spot * dfq, 0);
        double upper = type == OptionType.Call ? spot * dfq : strike * dfr;
        if (price < lower - 1e-12 || price > upper + 1e-12 || time <= 0) return double.NaN;
        if (price <= lower + 1e-14) return 0;

        double sigma = Math.Sqrt(2 * Math.PI / time) * price / (spot * dfq);
        sigma = Math.Clamp(sigma, 0.01, 3);
        for (int i = 0; i < 50; i++)
        {
            double sqrtT = Math.Sqrt(time);
            double d1 = (Math.Log(spot / strike) + (rate - dividendYield + 0.5 * sigma * sigma) * time) / (sigma * sqrtT);
            double vega = spot * dfq * Normal.Pdf(d1) * sqrtT;
            double diff = Price(type, spot, strike, time, rate, dividendYield, sigma) - price;
            if (Math.Abs(diff) < tolerance) return sigma;
            if (vega < 1e-10) break;
            double next = sigma - diff / vega;
            if (!(next > 0 && next < 10)) break;
            sigma = next;
        }
        Func<double, double> f = s => Price(type, spot, strike, time, rate, dividendYield, s) - price;
        if (f(1e-6) > 0) return 1e-6;
        if (f(10) < 0) return double.NaN;
        return RootFinding.Brent(f, 1e-6, 10, tolerance * 1e-2);
    }

    /// <summary>d1 term.</summary>
    public static double D1(double spot, double strike, double time, double rate, double dividendYield, double volatility)
        => (Math.Log(spot / strike) + (rate - dividendYield + 0.5 * volatility * volatility) * time) / (volatility * Math.Sqrt(time));
}

/// <summary>Black (1976) model for options on futures/forwards.</summary>
public static class Black76
{
    /// <summary>Price of an option on a forward <paramref name="forward"/>, discounted at <paramref name="rate"/>.</summary>
    public static double Price(OptionType type, double forward, double strike, double time, double rate, double volatility)
        => Math.Exp(-rate * time) * Undiscounted(type, forward, strike, time, volatility);

    /// <summary>Undiscounted Black price (used for caps, floors and swaptions with an annuity).</summary>
    public static double Undiscounted(OptionType type, double forward, double strike, double time, double volatility)
    {
        if (time <= 0 || volatility <= 0) return Math.Max(type == OptionType.Call ? forward - strike : strike - forward, 0);
        double vst = volatility * Math.Sqrt(time);
        double d1 = (Math.Log(forward / strike) + 0.5 * vst * vst) / vst, d2 = d1 - vst;
        return type == OptionType.Call
            ? forward * Normal.Cdf(d1) - strike * Normal.Cdf(d2)
            : strike * Normal.Cdf(-d2) - forward * Normal.Cdf(-d1);
    }

    /// <summary>Black implied volatility of an undiscounted price.</summary>
    public static double ImpliedVolatility(OptionType type, double undiscountedPrice, double forward, double strike, double time)
    {
        double intrinsic = Math.Max(type == OptionType.Call ? forward - strike : strike - forward, 0);
        if (undiscountedPrice <= intrinsic) return 0;
        return RootFinding.Brent(s => Undiscounted(type, forward, strike, time, s) - undiscountedPrice, 1e-8, 10, 1e-12);
    }
}

/// <summary>Bachelier (normal) model: the forward follows arithmetic Brownian motion.</summary>
public static class Bachelier
{
    /// <summary>Price with normal (absolute) volatility <paramref name="normalVolatility"/>.</summary>
    public static double Price(OptionType type, double forward, double strike, double time, double rate, double normalVolatility)
        => Math.Exp(-rate * time) * Undiscounted(type, forward, strike, time, normalVolatility);

    /// <summary>Undiscounted Bachelier price.</summary>
    public static double Undiscounted(OptionType type, double forward, double strike, double time, double normalVolatility)
    {
        double sst = normalVolatility * Math.Sqrt(time);
        double omega = type == OptionType.Call ? 1 : -1;
        if (sst <= 0) return Math.Max(omega * (forward - strike), 0);
        double d = (forward - strike) / sst;
        return omega * (forward - strike) * Normal.Cdf(omega * d) + sst * Normal.Pdf(d);
    }

    /// <summary>Normal implied volatility of an undiscounted price.</summary>
    public static double ImpliedVolatility(OptionType type, double undiscountedPrice, double forward, double strike, double time)
    {
        double intrinsic = Math.Max(type == OptionType.Call ? forward - strike : strike - forward, 0);
        if (undiscountedPrice <= intrinsic) return 0;
        double hi = Math.Max(1e-4, undiscountedPrice * 10 / Math.Sqrt(time));
        while (Undiscounted(type, forward, strike, time, hi) < undiscountedPrice) hi *= 2;
        return RootFinding.Brent(s => Undiscounted(type, forward, strike, time, s) - undiscountedPrice, 0, hi, 1e-14);
    }
}
