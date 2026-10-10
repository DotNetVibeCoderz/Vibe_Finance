namespace Finance.Portfolio;

internal enum ConstraintKind { LessOrEqual, GreaterOrEqual, Equal }

internal enum LpStatus { Optimal, Infeasible, Unbounded }

/// <summary>
/// Small dense two-phase tableau simplex for min cᵀx s.t. rows, x ≥ 0, using Bland's rule (no cycling).
/// Sized for the CVaR cutting-plane master problems: tens of variables, a few hundred rows.
/// </summary>
internal sealed class LinearProgram
{
    private const double Eps = 1e-11;
    private readonly int _n;
    private readonly List<(double[] A, ConstraintKind Kind, double B)> _rows = new();

    public LinearProgram(int variables) => _n = variables;

    public double[] Objective { get; set; } = Array.Empty<double>();

    public void Add(double[] a, ConstraintKind kind, double b)
    {
        if (a.Length != _n) throw new ArgumentException("Row length mismatch.");
        _rows.Add(((double[])a.Clone(), kind, b));
    }

    public (LpStatus Status, double[] X, double Value) Solve()
    {
        int m = _rows.Count;
        // Column layout: [x (n)] [slack/surplus (one per inequality)] [artificial (per >= or = row)]
        int slackCount = _rows.Count(r => r.Kind != ConstraintKind.Equal);
        var rows = _rows.Select(r =>
        {
            if (r.B >= 0) return r;
            var flipped = r.A.Select(v => -v).ToArray();
            var kind = r.Kind switch
            {
                ConstraintKind.LessOrEqual => ConstraintKind.GreaterOrEqual,
                ConstraintKind.GreaterOrEqual => ConstraintKind.LessOrEqual,
                _ => ConstraintKind.Equal,
            };
            return (A: flipped, Kind: kind, B: -r.B);
        }).ToList();
        int artCount = rows.Count(r => r.Kind != ConstraintKind.LessOrEqual);
        int cols = _n + slackCount + artCount;
        var t = new double[m + 1, cols + 1]; // last row: objective, last column: rhs
        var basis = new int[m];
        int s = _n, a = _n + slackCount;
        for (int i = 0; i < m; i++)
        {
            var (row, kind, b) = rows[i];
            for (int j = 0; j < _n; j++) t[i, j] = row[j];
            t[i, cols] = b;
            switch (kind)
            {
                case ConstraintKind.LessOrEqual:
                    t[i, s] = 1;
                    basis[i] = s++;
                    break;
                case ConstraintKind.GreaterOrEqual:
                    t[i, s++] = -1;
                    t[i, a] = 1;
                    basis[i] = a++;
                    break;
                default:
                    t[i, a] = 1;
                    basis[i] = a++;
                    break;
            }
        }

        // Phase 1: minimise the sum of artificials.
        if (artCount > 0)
        {
            for (int j = 0; j <= cols; j++) t[m, j] = 0;
            for (int i = 0; i < m; i++)
                if (basis[i] >= _n + slackCount)
                    for (int j = 0; j <= cols; j++) t[m, j] -= t[i, j];
            for (int j = _n + slackCount; j < cols; j++) t[m, j] = 0;
            if (!Iterate(t, basis, m, cols, cols)) return (LpStatus.Unbounded, new double[_n], double.NaN);
            if (-t[m, cols] > 1e-8) return (LpStatus.Infeasible, new double[_n], double.NaN);
            // Drive artificials out of the basis where possible.
            for (int i = 0; i < m; i++)
            {
                if (basis[i] < _n + slackCount) continue;
                for (int j = 0; j < _n + slackCount; j++)
                    if (Math.Abs(t[i, j]) > 1e-9) { Pivot(t, basis, m, cols, i, j); break; }
            }
        }

        // Phase 2 over original + slack columns (artificials are barred from entering).
        for (int j = 0; j <= cols; j++) t[m, j] = 0;
        for (int j = 0; j < _n; j++) t[m, j] = Objective[j];
        for (int i = 0; i < m; i++)
        {
            int bj = basis[i];
            double c = bj < _n ? Objective[bj] : 0;
            if (c == 0) continue;
            for (int j = 0; j <= cols; j++) t[m, j] -= c * t[i, j];
        }
        if (!Iterate(t, basis, m, cols, _n + slackCount)) return (LpStatus.Unbounded, new double[_n], double.NegativeInfinity);

        var x = new double[_n];
        for (int i = 0; i < m; i++)
            if (basis[i] < _n) x[basis[i]] = t[i, cols];
        double value = 0;
        for (int j = 0; j < _n; j++) value += Objective[j] * x[j];
        return (LpStatus.Optimal, x, value);
    }

    /// <summary>Simplex iterations with Bland's rule; columns ≥ <paramref name="enterLimit"/> may not enter.</summary>
    private static bool Iterate(double[,] t, int[] basis, int m, int cols, int enterLimit)
    {
        for (int iter = 0; iter < 50_000; iter++)
        {
            int enter = -1;
            for (int j = 0; j < enterLimit; j++)
                if (t[m, j] < -Eps) { enter = j; break; }
            if (enter < 0) return true;

            int leave = -1;
            double best = double.PositiveInfinity;
            for (int i = 0; i < m; i++)
            {
                if (t[i, enter] <= Eps) continue;
                double ratio = t[i, cols] / t[i, enter];
                if (ratio < best - 1e-12 || (Math.Abs(ratio - best) <= 1e-12 && leave >= 0 && basis[i] < basis[leave]))
                {
                    best = ratio;
                    leave = i;
                }
            }
            if (leave < 0) return false;
            Pivot(t, basis, m, cols, leave, enter);
        }
        return true;
    }

    private static void Pivot(double[,] t, int[] basis, int m, int cols, int r, int c)
    {
        double p = t[r, c];
        for (int j = 0; j <= cols; j++) t[r, j] /= p;
        for (int i = 0; i <= m; i++)
        {
            if (i == r) continue;
            double f = t[i, c];
            if (f == 0) continue;
            for (int j = 0; j <= cols; j++) t[i, j] -= f * t[r, j];
        }
        basis[r] = c;
    }
}
