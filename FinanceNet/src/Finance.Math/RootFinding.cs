namespace Finance.Mathematics;

/// <summary>One-dimensional root finders (implied volatility, yield to maturity, bootstrapping).</summary>
public static class RootFinding
{
    /// <summary>
    /// Brent's method on a bracket [a, b] where f(a) and f(b) have opposite signs.
    /// Converges superlinearly and never leaves the bracket.
    /// </summary>
    public static double Brent(Func<double, double> f, double a, double b, double tolerance = 1e-12, int maxIterations = 200)
    {
        double fa = f(a), fb = f(b);
        if (fa == 0) return a;
        if (fb == 0) return b;
        if (Math.Sign(fa) == Math.Sign(fb)) throw new ArgumentException("Root is not bracketed.");

        if (Math.Abs(fa) < Math.Abs(fb)) { (a, b) = (b, a); (fa, fb) = (fb, fa); }
        double c = a, fc = fa, d = b - a;
        bool bisected = true;
        for (int i = 0; i < maxIterations; i++)
        {
            double s;
            if (fa != fc && fb != fc)
            {
                s = a * fb * fc / ((fa - fb) * (fa - fc)) + b * fa * fc / ((fb - fa) * (fb - fc)) + c * fa * fb / ((fc - fa) * (fc - fb));
            }
            else
            {
                s = b - fb * (b - a) / (fb - fa);
            }

            double lo = (3 * a + b) / 4;
            bool outside = !(s > Math.Min(lo, b) && s < Math.Max(lo, b));
            if (outside
                || (bisected && Math.Abs(s - b) >= Math.Abs(b - c) / 2)
                || (!bisected && Math.Abs(s - b) >= Math.Abs(c - d) / 2)
                || (bisected && Math.Abs(b - c) < tolerance)
                || (!bisected && Math.Abs(c - d) < tolerance))
            {
                s = (a + b) / 2;
                bisected = true;
            }
            else bisected = false;

            double fs = f(s);
            d = c;
            c = b;
            fc = fb;
            if (Math.Sign(fa) != Math.Sign(fs)) { b = s; fb = fs; }
            else { a = s; fa = fs; }
            if (Math.Abs(fa) < Math.Abs(fb)) { (a, b) = (b, a); (fa, fb) = (fb, fa); }
            if (fb == 0 || Math.Abs(b - a) < tolerance) return b;
        }
        return b;
    }

    /// <summary>Newton-Raphson with an analytic derivative. Returns NaN if it fails to converge.</summary>
    public static double Newton(Func<double, double> f, Func<double, double> df, double x0, double tolerance = 1e-12, int maxIterations = 100)
    {
        double x = x0;
        for (int i = 0; i < maxIterations; i++)
        {
            double fx = f(x), d = df(x);
            if (d == 0 || double.IsNaN(d)) return double.NaN;
            double step = fx / d;
            x -= step;
            if (Math.Abs(step) < tolerance * (1 + Math.Abs(x))) return x;
        }
        return double.NaN;
    }

    /// <summary>Bisection: slow but unconditionally robust.</summary>
    public static double Bisection(Func<double, double> f, double a, double b, double tolerance = 1e-12, int maxIterations = 500)
    {
        double fa = f(a);
        if (Math.Sign(fa) == Math.Sign(f(b))) throw new ArgumentException("Root is not bracketed.");
        for (int i = 0; i < maxIterations && b - a > tolerance; i++)
        {
            double m = 0.5 * (a + b), fm = f(m);
            if (fm == 0) return m;
            if (Math.Sign(fm) == Math.Sign(fa)) { a = m; fa = fm; }
            else b = m;
        }
        return 0.5 * (a + b);
    }
}
