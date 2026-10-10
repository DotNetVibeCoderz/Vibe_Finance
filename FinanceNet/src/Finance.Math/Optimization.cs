namespace Finance.Mathematics;

/// <summary>Result of an optimisation run.</summary>
public sealed record OptimizationResult(double[] X, double Value, int Iterations, bool Converged);

/// <summary>
/// Optimisers used by portfolio construction and calibration.
/// </summary>
public static class Optimizer
{
    /// <summary>
    /// Euclidean projection of <paramref name="v"/> onto the capped simplex
    /// { w : sum w = <paramref name="total"/>, lower ≤ w ≤ upper } (in place).
    /// Solved by bisection on the shift τ in w = clip(v - τ, lower, upper), which is exact for box bounds.
    /// </summary>
    public static void ProjectOntoSimplex(Span<double> v, double total = 1.0, double lower = 0.0, double upper = double.PositiveInfinity)
    {
        int n = v.Length;
        if (n == 0) return;
        if (lower * n > total + 1e-12 || upper * n < total - 1e-12)
            throw new ArgumentException("Bounds make the simplex empty.");

        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var x in v)
        {
            lo = Math.Min(lo, x - upper);
            hi = Math.Max(hi, x - lower);
        }
        if (double.IsInfinity(lo)) lo = Stats.Min(v) - total - 1;

        double SumAt(double tau, ReadOnlySpan<double> vv)
        {
            double s = 0;
            foreach (var x in vv) s += Math.Clamp(x - tau, lower, upper);
            return s;
        }

        // SumAt is non-increasing in tau.
        for (int it = 0; it < 200; it++)
        {
            double mid = 0.5 * (lo + hi);
            if (SumAt(mid, v) > total) lo = mid;
            else hi = mid;
            if (hi - lo < 1e-15 * (1 + Math.Abs(lo))) break;
        }
        double t = 0.5 * (lo + hi);
        for (int i = 0; i < n; i++) v[i] = Math.Clamp(v[i] - t, lower, upper);
    }

    /// <summary>
    /// Projected gradient descent with Armijo backtracking (FISTA-style acceleration) for
    /// min f(x) subject to x in a convex set given by <paramref name="project"/>.
    /// </summary>
    public static OptimizationResult ProjectedGradient(
        Func<double[], double> f,
        Action<double[], double[]> gradient,
        double[] x0,
        Action<double[]> project,
        int maxIterations = 5000,
        double tolerance = 1e-10)
    {
        int n = x0.Length;
        var x = (double[])x0.Clone();
        project(x);
        var y = (double[])x.Clone();
        var g = new double[n];
        var xNew = new double[n];
        double step = 1.0, t = 1.0, fx = f(x);
        for (int it = 1; it <= maxIterations; it++)
        {
            gradient(y, g);
            double fy = f(y);
            double fNew;
            while (true)
            {
                for (int i = 0; i < n; i++) xNew[i] = y[i] - step * g[i];
                project(xNew);
                fNew = f(xNew);
                double lin = 0, quad = 0;
                for (int i = 0; i < n; i++)
                {
                    double d = xNew[i] - y[i];
                    lin += g[i] * d;
                    quad += d * d;
                }
                if (fNew <= fy + lin + quad / (2 * step) + 1e-15 || step < 1e-20) break;
                step *= 0.5;
            }

            double change = 0;
            for (int i = 0; i < n; i++) change = Math.Max(change, Math.Abs(xNew[i] - x[i]));

            double tNew = 0.5 * (1 + Math.Sqrt(1 + 4 * t * t));
            double momentum = (t - 1) / tNew;
            // Restart momentum if the objective went up (keeps FISTA monotone).
            if (fNew > fx) momentum = 0;
            for (int i = 0; i < n; i++)
            {
                y[i] = xNew[i] + momentum * (xNew[i] - x[i]);
                x[i] = xNew[i];
            }
            project(y);
            t = momentum == 0 ? 1 : tNew;
            fx = fNew;
            step *= 1.5;
            if (change < tolerance) return new OptimizationResult(x, fx, it, true);
        }
        return new OptimizationResult(x, fx, maxIterations, false);
    }

    /// <summary>Derivative-free Nelder-Mead simplex minimiser (calibration of small models).</summary>
    public static OptimizationResult NelderMead(Func<double[], double> f, double[] x0, double initialStep = 0.1, int maxIterations = 5000, double tolerance = 1e-10)
    {
        int n = x0.Length;
        var pts = new double[n + 1][];
        var vals = new double[n + 1];
        pts[0] = (double[])x0.Clone();
        for (int i = 0; i < n; i++)
        {
            var p = (double[])x0.Clone();
            p[i] += p[i] != 0 ? initialStep * Math.Abs(p[i]) : initialStep;
            pts[i + 1] = p;
        }
        for (int i = 0; i <= n; i++) vals[i] = f(pts[i]);

        var centroid = new double[n];
        var trial = new double[n];
        var trial2 = new double[n];
        for (int it = 1; it <= maxIterations; it++)
        {
            Array.Sort(vals, pts);
            if (Math.Abs(vals[n] - vals[0]) < tolerance * (Math.Abs(vals[0]) + 1e-20) + 1e-300)
                return new OptimizationResult(pts[0], vals[0], it, true);

            Array.Clear(centroid);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) centroid[j] += pts[i][j] / n;

            for (int j = 0; j < n; j++) trial[j] = centroid[j] + (centroid[j] - pts[n][j]);
            double fr = f(trial);
            if (fr < vals[0])
            {
                for (int j = 0; j < n; j++) trial2[j] = centroid[j] + 2 * (centroid[j] - pts[n][j]);
                double fe = f(trial2);
                if (fe < fr) { pts[n] = (double[])trial2.Clone(); vals[n] = fe; }
                else { pts[n] = (double[])trial.Clone(); vals[n] = fr; }
            }
            else if (fr < vals[n - 1])
            {
                pts[n] = (double[])trial.Clone();
                vals[n] = fr;
            }
            else
            {
                bool outside = fr < vals[n];
                for (int j = 0; j < n; j++)
                    trial2[j] = outside ? centroid[j] + 0.5 * (trial[j] - centroid[j]) : centroid[j] + 0.5 * (pts[n][j] - centroid[j]);
                double fc = f(trial2);
                if (fc < Math.Min(fr, vals[n]))
                {
                    pts[n] = (double[])trial2.Clone();
                    vals[n] = fc;
                }
                else
                {
                    for (int i = 1; i <= n; i++)
                    {
                        for (int j = 0; j < n; j++) pts[i][j] = pts[0][j] + 0.5 * (pts[i][j] - pts[0][j]);
                        vals[i] = f(pts[i]);
                    }
                }
            }
        }
        Array.Sort(vals, pts);
        return new OptimizationResult(pts[0], vals[0], maxIterations, false);
    }

    /// <summary>Golden-section search for a unimodal function on [a, b].</summary>
    public static double GoldenSection(Func<double, double> f, double a, double b, double tolerance = 1e-10)
    {
        double gr = (Math.Sqrt(5) - 1) / 2;
        double c = b - gr * (b - a), d = a + gr * (b - a);
        double fc = f(c), fd = f(d);
        while (Math.Abs(b - a) > tolerance)
        {
            if (fc < fd) { b = d; d = c; fd = fc; c = b - gr * (b - a); fc = f(c); }
            else { a = c; c = d; fc = fd; d = a + gr * (b - a); fd = f(d); }
        }
        return 0.5 * (a + b);
    }
}
