using System.Numerics;
using Finance.Mathematics;

namespace Finance.Pricing.Options;

/// <summary>Heston (1993) stochastic-volatility parameters.</summary>
/// <param name="V0">Initial variance.</param>
/// <param name="Kappa">Mean-reversion speed of variance.</param>
/// <param name="Theta">Long-run variance.</param>
/// <param name="Sigma">Volatility of variance ("vol of vol").</param>
/// <param name="Rho">Correlation between spot and variance shocks.</param>
public sealed record HestonParameters(double V0, double Kappa, double Theta, double Sigma, double Rho)
{
    /// <summary>True when 2κθ ≥ σ², i.e. variance cannot reach zero.</summary>
    public bool FellerSatisfied => 2 * Kappa * Theta >= Sigma * Sigma;
}

/// <summary>
/// Semi-analytic Heston pricer using the "little Heston trap" characteristic function
/// (Albrecher et al., 2007), which avoids the branch-cut discontinuity of the original formulation.
/// </summary>
public static class HestonModel
{
    /// <summary>European option price.</summary>
    public static double Price(OptionType type, double spot, double strike, double time, double rate, double dividendYield, HestonParameters p, double upperLimit = 200, int panels = 80)
    {
        double lnK = Math.Log(strike);
        double fwd = spot * Math.Exp((rate - dividendYield) * time);
        Complex phiMinusI = new(fwd, 0); // E[S_T]

        double I1 = Integration.GaussLegendre(u =>
        {
            var cu = new Complex(u, 0);
            var val = Complex.Exp(-Complex.ImaginaryOne * cu * lnK) * CharacteristicFunction(cu - Complex.ImaginaryOne, spot, time, rate, dividendYield, p)
                      / (Complex.ImaginaryOne * cu * phiMinusI);
            return val.Real;
        }, 1e-10, upperLimit, panels);
        double I2 = Integration.GaussLegendre(u =>
        {
            var cu = new Complex(u, 0);
            var val = Complex.Exp(-Complex.ImaginaryOne * cu * lnK) * CharacteristicFunction(cu, spot, time, rate, dividendYield, p)
                      / (Complex.ImaginaryOne * cu);
            return val.Real;
        }, 1e-10, upperLimit, panels);

        double p1 = 0.5 + I1 / Math.PI, p2 = 0.5 + I2 / Math.PI;
        double call = spot * Math.Exp(-dividendYield * time) * p1 - strike * Math.Exp(-rate * time) * p2;
        if (type == OptionType.Call) return Math.Max(call, 0);
        double put = call - spot * Math.Exp(-dividendYield * time) + strike * Math.Exp(-rate * time);
        return Math.Max(put, 0);
    }

    /// <summary>Characteristic function E[exp(i u ln S_T)] (little-trap form).</summary>
    public static Complex CharacteristicFunction(Complex u, double spot, double time, double rate, double dividendYield, HestonParameters p)
    {
        var i = Complex.ImaginaryOne;
        double k = p.Kappa, th = p.Theta, s = p.Sigma, r = p.Rho;
        Complex b = k - r * s * i * u;
        Complex d = Complex.Sqrt(b * b + s * s * (i * u + u * u));
        Complex g = (b - d) / (b + d);
        Complex edt = Complex.Exp(-d * time);
        Complex C = k * th / (s * s) * ((b - d) * time - 2 * Complex.Log((1 - g * edt) / (1 - g)));
        Complex D = (b - d) / (s * s) * (1 - edt) / (1 - g * edt);
        return Complex.Exp(i * u * (Math.Log(spot) + (rate - dividendYield) * time) + C + D * p.V0);
    }

    /// <summary>
    /// Calibrates Heston parameters to market implied volatilities by Nelder-Mead on squared vol errors.
    /// Parameters are mapped to unconstrained space (log for positives, tanh for ρ).
    /// </summary>
    public static HestonParameters Calibrate(double spot, double rate, double dividendYield,
        IReadOnlyList<(double Time, double Strike, double ImpliedVol)> quotes, HestonParameters? initial = null, int maxIterations = 2000)
    {
        var start = initial ?? new HestonParameters(0.04, 1.5, 0.04, 0.5, -0.5);
        double[] x0 = { Math.Log(start.V0), Math.Log(start.Kappa), Math.Log(start.Theta), Math.Log(start.Sigma), Math.Atanh(Math.Clamp(start.Rho, -0.99, 0.99)) };
        static HestonParameters Map(double[] x) => new(Math.Exp(x[0]), Math.Exp(x[1]), Math.Exp(x[2]), Math.Exp(x[3]), Math.Tanh(x[4]));
        double Objective(double[] x)
        {
            var p = Map(x);
            if (p.Kappa > 50 || p.Sigma > 5 || p.V0 > 4 || p.Theta > 4) return 1e6;
            double err = 0;
            foreach (var q in quotes)
            {
                double price = Price(OptionType.Call, spot, q.Strike, q.Time, rate, dividendYield, p, 150, 40);
                double iv = BlackScholes.ImpliedVolatility(OptionType.Call, price, spot, q.Strike, q.Time, rate, dividendYield);
                double e = double.IsNaN(iv) ? 1 : iv - q.ImpliedVol;
                err += e * e;
            }
            return err;
        }
        var result = Optimizer.NelderMead(Objective, x0, 0.2, maxIterations, 1e-12);
        return Map(result.X);
    }
}

/// <summary>SABR model parameters (α, β, ρ, ν).</summary>
public sealed record SabrParameters(double Alpha, double Beta, double Rho, double Nu);

/// <summary>Hagan et al. (2002) lognormal implied-volatility approximation for SABR.</summary>
public static class SabrModel
{
    /// <summary>Black implied volatility at strike <paramref name="strike"/> for forward <paramref name="forward"/>.</summary>
    public static double ImpliedVolatility(double forward, double strike, double time, SabrParameters p)
    {
        double a = p.Alpha, b = p.Beta, r = p.Rho, v = p.Nu;
        double omb = 1 - b;
        double fk = forward * strike;
        double fkPow = Math.Pow(fk, omb / 2);
        double logFk = Math.Log(forward / strike);
        double correction = 1 + (omb * omb * a * a / (24 * fkPow * fkPow) + r * b * v * a / (4 * fkPow) + (2 - 3 * r * r) * v * v / 24) * time;

        if (Math.Abs(logFk) < 1e-7)
            return a / Math.Pow(forward, omb) * correction;

        double z = v / a * fkPow * logFk;
        double xz = Math.Log((Math.Sqrt(1 - 2 * r * z + z * z) + z - r) / (1 - r));
        double zOverX = Math.Abs(z) < 1e-12 ? 1 : z / xz;
        double denom = fkPow * (1 + omb * omb / 24 * logFk * logFk + Math.Pow(omb, 4) / 1920 * Math.Pow(logFk, 4));
        return a / denom * zOverX * correction;
    }

    /// <summary>
    /// Calibrates α, ρ, ν for a fixed β to a smile (strikes and Black vols) by Nelder-Mead.
    /// </summary>
    public static SabrParameters Calibrate(double forward, double time, double[] strikes, double[] vols, double beta = 0.5)
    {
        Finance.Core.Guard.SameLength(strikes.Length, vols.Length, "strikes and vols");
        int atm = 0;
        for (int i = 1; i < strikes.Length; i++)
            if (Math.Abs(strikes[i] - forward) < Math.Abs(strikes[atm] - forward)) atm = i;
        double alpha0 = vols[atm] * Math.Pow(forward, 1 - beta);
        double[] x0 = { Math.Log(alpha0), 0, Math.Log(0.5) };
        SabrParameters Map(double[] x) => new(Math.Exp(x[0]), beta, Math.Tanh(x[1]), Math.Exp(x[2]));
        var res = Optimizer.NelderMead(x =>
        {
            var p = Map(x);
            double err = 0;
            for (int i = 0; i < strikes.Length; i++)
            {
                double e = ImpliedVolatility(forward, strikes[i], time, p) - vols[i];
                err += double.IsNaN(e) ? 1 : e * e;
            }
            return err;
        }, x0, 0.1, 5000, 1e-14);
        return Map(res.X);
    }
}
