namespace Finance.Pricing.Options;

/// <summary>Lattice pricers for European and American vanilla options.</summary>
public static class LatticePricer
{
    /// <summary>
    /// Cox-Ross-Rubinstein binomial tree. Error is O(1/steps) with an odd/even oscillation;
    /// 500+ steps give ~1e-3 accuracy on typical equity options.
    /// </summary>
    public static double Binomial(OptionType type, ExerciseStyle style, double spot, double strike, double time,
        double rate, double dividendYield, double volatility, int steps = 500)
    {
        Finance.Core.Guard.Positive(steps);
        double dt = time / steps;
        double u = Math.Exp(volatility * Math.Sqrt(dt)), d = 1 / u;
        double growth = Math.Exp((rate - dividendYield) * dt);
        double p = (growth - d) / (u - d);
        if (p is <= 0 or >= 1) throw new ArgumentException("Time step too large for the given rates; increase steps.");
        double disc = Math.Exp(-rate * dt);
        double pu = disc * p, pd = disc * (1 - p);
        double sign = type == OptionType.Call ? 1 : -1;
        bool american = style == ExerciseStyle.American;

        var values = new double[steps + 1];
        double u2 = u * u;
        double s = spot * Math.Pow(d, steps);
        for (int i = 0; i <= steps; i++)
        {
            values[i] = Math.Max(sign * (s - strike), 0);
            s *= u2;
        }
        for (int n = steps - 1; n >= 0; n--)
        {
            s = spot * Math.Pow(d, n);
            for (int i = 0; i <= n; i++)
            {
                double cont = pu * values[i + 1] + pd * values[i];
                values[i] = american ? Math.Max(cont, sign * (s - strike)) : cont;
                s *= u2;
            }
        }
        return values[0];
    }

    /// <summary>
    /// Boyle trinomial tree in log-space with dx = σ√(3 dt). Smoother convergence than CRR.
    /// </summary>
    public static double Trinomial(OptionType type, ExerciseStyle style, double spot, double strike, double time,
        double rate, double dividendYield, double volatility, int steps = 300)
    {
        Finance.Core.Guard.Positive(steps);
        double dt = time / steps;
        double dx = volatility * Math.Sqrt(3 * dt);
        double nu = rate - dividendYield - 0.5 * volatility * volatility;
        double a = (volatility * volatility * dt + nu * nu * dt * dt) / (dx * dx);
        double b = nu * dt / dx;
        double disc = Math.Exp(-rate * dt);
        double pu = disc * 0.5 * (a + b), pd = disc * 0.5 * (a - b), pm = disc * (1 - a);
        double sign = type == OptionType.Call ? 1 : -1;
        bool american = style == ExerciseStyle.American;

        int width = 2 * steps + 1;
        var values = new double[width];
        var prices = new double[width];
        for (int j = 0; j < width; j++)
        {
            prices[j] = spot * Math.Exp((j - steps) * dx);
            values[j] = Math.Max(sign * (prices[j] - strike), 0);
        }
        var next = new double[width];
        for (int n = steps - 1; n >= 0; n--)
        {
            int offset = steps - n; // node j at level n sits at price index offset + j
            for (int j = 0; j <= 2 * n; j++)
            {
                int k = offset + j;
                double cont = pu * values[k + 1] + pm * values[k] + pd * values[k - 1];
                next[k] = american ? Math.Max(cont, sign * (prices[k] - strike)) : cont;
            }
            (values, next) = (next, values);
        }
        return values[steps];
    }
}
