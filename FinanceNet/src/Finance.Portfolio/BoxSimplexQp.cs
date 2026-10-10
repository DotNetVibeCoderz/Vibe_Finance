using Finance.Mathematics;

namespace Finance.Portfolio;

/// <summary>
/// Solves min ½ wᵀQw + cᵀw subject to Σw = total and lower ≤ wᵢ ≤ upper, with accelerated projected
/// gradient (FISTA + adaptive restart) and an exact projection onto the capped simplex. Q must be PSD.
/// </summary>
internal sealed class BoxSimplexQp
{
    private readonly Matrix _q;
    private readonly double _lipschitz;

    public BoxSimplexQp(Matrix q, double lower, double upper, double total = 1.0)
    {
        if (!q.IsSquare) throw new ArgumentException("Q must be square.");
        _q = q;
        Lower = lower;
        Upper = upper;
        Total = total;
        int n = q.Rows;
        if (lower * n > total + 1e-12 || upper * n < total - 1e-12)
            throw new ArgumentException($"Weight bounds [{lower}, {upper}] cannot sum to {total} with {n} assets.");
        _lipschitz = Math.Max(LargestEigenvalue(q) * 1.02, 1e-12);
    }

    public double Lower { get; }
    public double Upper { get; }
    public double Total { get; }
    public int Size => _q.Rows;

    public void Project(Span<double> w) => Optimizer.ProjectOntoSimplex(w, Total, Lower, Upper);

    /// <summary>Minimises the QP with linear term <paramref name="c"/>, warm-starting from <paramref name="warm"/>.</summary>
    public double[] Solve(ReadOnlySpan<double> c, double[]? warm = null, double tolerance = 1e-13, int maxIterations = 200_000)
    {
        int n = Size;
        var x = warm is null ? Enumerable.Repeat(Total / n, n).ToArray() : (double[])warm.Clone();
        Project(x);
        var y = (double[])x.Clone();
        var xNew = new double[n];
        var g = new double[n];
        double t = 1, step = 1 / _lipschitz;
        for (int it = 0; it < maxIterations; it++)
        {
            for (int i = 0; i < n; i++) g[i] = Matrix.Dot(_q.Row(i), y) + c[i];
            for (int i = 0; i < n; i++) xNew[i] = y[i] - step * g[i];
            Project(xNew);

            double change = 0, scale = 0, restart = 0;
            for (int i = 0; i < n; i++)
            {
                change = Math.Max(change, Math.Abs(xNew[i] - x[i]));
                scale = Math.Max(scale, Math.Abs(xNew[i]));
                restart += (y[i] - xNew[i]) * (xNew[i] - x[i]);
            }

            double tNew = 0.5 * (1 + Math.Sqrt(1 + 4 * t * t));
            if (restart > 0)
            {
                tNew = 1;
                Array.Copy(xNew, y, n);
            }
            else
            {
                double beta = (t - 1) / tNew;
                for (int i = 0; i < n; i++) y[i] = xNew[i] + beta * (xNew[i] - x[i]);
            }
            Array.Copy(xNew, x, n);
            t = tNew;
            if (it > 3 && change <= tolerance * (1 + scale)) break;
        }
        return x;
    }

    private static double LargestEigenvalue(Matrix q)
    {
        int n = q.Rows;
        var v = Enumerable.Repeat(1.0 / Math.Sqrt(n), n).ToArray();
        // Perturb so the start vector is not orthogonal to the dominant eigenvector by accident.
        for (int i = 0; i < n; i++) v[i] += 1e-3 * (i + 1) / n;
        double lambda = 0;
        for (int it = 0; it < 500; it++)
        {
            var w = q.Multiply(v);
            double norm = Math.Sqrt(Matrix.Dot(w, w));
            if (norm == 0) return 0;
            for (int i = 0; i < n; i++) v[i] = w[i] / norm;
            if (Math.Abs(norm - lambda) <= 1e-10 * norm) return norm;
            lambda = norm;
        }
        // Power iteration converges from below; fall back to the Gershgorin bound if it stalled.
        double gersh = 0;
        for (int i = 0; i < n; i++)
        {
            double s = 0;
            for (int j = 0; j < n; j++) s += Math.Abs(q[i, j]);
            gersh = Math.Max(gersh, s);
        }
        return Math.Max(lambda, gersh);
    }
}
