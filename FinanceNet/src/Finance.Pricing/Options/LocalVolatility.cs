using Finance.Core;

namespace Finance.Pricing.Options;

/// <summary>
/// Dupire local volatility derived from an implied-volatility surface, in Gatheral's total-variance form:
/// <c>σ²_loc(T, K) = ∂w/∂T / (1 − (y/w)·w_y + ¼(−¼ − 1/w + y²/w²)·w_y² + ½·w_yy)</c>, with w(y, T) = σ²_imp·T and
/// y = ln(K/F). Under that diffusion every European on the surface reprices to its input implied volatility.
/// </summary>
/// <remarks>
/// Derivatives are taken by central finite differences on the (interpolated) surface, so the result is only as
/// smooth as the surface. Where calendar or butterfly arbitrage makes the numerator or denominator non-positive,
/// the local variance is floored, and <see cref="Floored"/> counts how often that happened.
/// </remarks>
public sealed class LocalVolatility
{
    private readonly VolatilitySurface _surface;

    public LocalVolatility(VolatilitySurface implied, double spot, double rate, double dividendYield = 0)
    {
        Guard.Positive(spot);
        _surface = implied;
        Spot = spot;
        Rate = rate;
        DividendYield = dividendYield;
    }

    public double Spot { get; }
    public double Rate { get; }
    public double DividendYield { get; }

    /// <summary>Number of evaluations where the Dupire ratio had to be floored (a sign of an arbitrageable surface).</summary>
    public int Floored { get; private set; }

    private double Forward(double t) => Spot * Math.Exp((Rate - DividendYield) * t);

    private double TotalVariance(double y, double t)
    {
        double k = Forward(t) * Math.Exp(y), v = _surface.Volatility(t, k);
        return v * v * t;
    }

    /// <summary>Local volatility at time <paramref name="t"/> and spot level <paramref name="s"/>.</summary>
    public double Volatility(double t, double s)
    {
        t = Math.Max(t, 1e-4);
        double y = Math.Log(s / Forward(t));
        double ht = Math.Min(1e-3, 0.5 * t), hy = 1e-3;
        double w = TotalVariance(y, t);
        double wT = (TotalVariance(y, t + ht) - TotalVariance(y, t - ht)) / (2 * ht);
        double wUp = TotalVariance(y + hy, t), wDn = TotalVariance(y - hy, t);
        double wy = (wUp - wDn) / (2 * hy), wyy = (wUp - 2 * w + wDn) / (hy * hy);
        double den = 1 - y / w * wy + 0.25 * (-0.25 - 1 / w + y * y / (w * w)) * wy * wy + 0.5 * wyy;
        double local = wT / den;
        if (!(wT > 0) || !(den > 0) || !(local > 0))
        {
            Floored++;
            local = Math.Max(1e-8, w / t); // fall back to the implied variance
        }
        return Math.Sqrt(local);
    }

    /// <summary>
    /// European option price by Monte Carlo under the local-vol diffusion (log-Euler, <paramref name="steps"/> steps,
    /// antithetic). Local vol is tabulated on a (time × log-spot) grid once and interpolated bilinearly.
    /// </summary>
    public MonteCarloResult PriceEuropean(OptionType type, double strike, double expiry, int paths = 100_000, int steps = 100, ulong seed = 42)
    {
        double dt = expiry / steps, atm = _surface.Volatility(expiry, Forward(expiry));
        double lo = Math.Log(Spot) - 6 * atm * Math.Sqrt(expiry), hi = Math.Log(Spot) + 6 * atm * Math.Sqrt(expiry);
        const int nx = 241;
        double dx = (hi - lo) / (nx - 1);
        var grid = new double[steps, nx];
        for (int k = 0; k < steps; k++)
            for (int j = 0; j < nx; j++) grid[k, j] = Volatility(k * dt, Math.Exp(lo + j * dx));

        var rng = new FastRandom(seed);
        double drift = (Rate - DividendYield) * dt, sq = Math.Sqrt(dt), sum = 0, sumSq = 0;
        int pairs = paths / 2;
        for (int p = 0; p < pairs; p++)
        {
            double xa = Math.Log(Spot), xb = xa;
            for (int k = 0; k < steps; k++)
            {
                double z = rng.NextGaussian();
                double va = Lookup(grid, k, xa, lo, dx, nx), vb = Lookup(grid, k, xb, lo, dx, nx);
                xa += drift - 0.5 * va * va * dt + va * sq * z;
                xb += drift - 0.5 * vb * vb * dt - vb * sq * z;
            }
            double pay = 0.5 * (Payoff(type, Math.Exp(xa), strike) + Payoff(type, Math.Exp(xb), strike));
            sum += pay;
            sumSq += pay * pay;
        }
        double mean = sum / pairs, var = (sumSq / pairs - mean * mean) * pairs / (pairs - 1);
        double df = Math.Exp(-Rate * expiry);
        return new MonteCarloResult(df * mean, df * Math.Sqrt(var / pairs), 2 * pairs);
    }

    private static double Payoff(OptionType type, double s, double k) => type == OptionType.Call ? Math.Max(s - k, 0) : Math.Max(k - s, 0);

    private static double Lookup(double[,] grid, int k, double x, double lo, double dx, int nx)
    {
        double pos = Math.Clamp((x - lo) / dx, 0, nx - 1.000001);
        int j = (int)pos;
        double w = pos - j;
        return (1 - w) * grid[k, j] + w * grid[k, j + 1];
    }
}
