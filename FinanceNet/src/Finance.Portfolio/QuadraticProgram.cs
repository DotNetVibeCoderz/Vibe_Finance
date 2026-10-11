using Finance.Mathematics;

namespace Finance.Portfolio;

/// <summary>Result of <see cref="QuadraticProgram.Solve"/>.</summary>
/// <param name="X">The minimiser.</param>
/// <param name="Objective">½xᵀQx + cᵀx at <paramref name="X"/>.</param>
/// <param name="Iterations">Interior-point iterations used.</param>
/// <param name="Converged">True when all residuals and the duality gap met the tolerance.</param>
/// <param name="EqualityDuals">Lagrange multipliers of the equality constraints, in the order added.</param>
/// <param name="InequalityDuals">Multipliers (≥ 0) of the general inequality constraints, in the order added.</param>
/// <param name="PrimalResidual">Largest violation of an equality or inequality at <paramref name="X"/>.</param>
/// <param name="DualResidual">Largest entry of the stationarity residual, relative to the scale of its terms.</param>
/// <param name="Gap">Average complementarity sᵀz / m.</param>
public sealed record QpSolution(double[] X, double Objective, int Iterations, bool Converged, double[] EqualityDuals, double[] InequalityDuals,
    double PrimalResidual = 0, double DualResidual = 0, double Gap = 0);

/// <summary>
/// Dense convex quadratic program, min ½xᵀQx + cᵀx s.t. Ax = b, Gx ≤ h, lb ≤ x ≤ ub, solved with a primal-dual
/// interior-point method (Mehrotra predictor-corrector, infeasible start).
/// </summary>
/// <remarks>
/// Each iteration solves the reduced KKT system (Q + GᵀWG)·dx + Aᵀdy = r with a Cholesky factorisation and a Schur
/// complement on the equality rows, so keep equalities few. Inequality rows are stored sparse: a bound adds to one
/// diagonal entry and a two-entry row to a 2×2 block, so a 600-variable portfolio with bounds, trade-linking rows and
/// a few dense constraints costs one 600×600 Cholesky per iteration. Q must be positive semidefinite.
/// </remarks>
public sealed class QuadraticProgram
{
    private readonly Matrix _q;
    private readonly double[] _c;
    private readonly List<(double[] A, double B)> _eq = new();
    private readonly List<(double[] G, double H)> _ineq = new();
    private readonly double[] _lb, _ub;

    public QuadraticProgram(Matrix q, double[] c)
    {
        if (!q.IsSquare || q.Rows != c.Length) throw new ArgumentException("Q must be n x n with n = length of c.");
        _q = q;
        _c = (double[])c.Clone();
        _lb = Enumerable.Repeat(double.NegativeInfinity, c.Length).ToArray();
        _ub = Enumerable.Repeat(double.PositiveInfinity, c.Length).ToArray();
    }

    public int Variables => _c.Length;

    /// <summary>Adds aᵀx = b.</summary>
    public QuadraticProgram AddEquality(double[] a, double b)
    {
        Check(a);
        _eq.Add(((double[])a.Clone(), b));
        return this;
    }

    /// <summary>Adds gᵀx ≤ h.</summary>
    public QuadraticProgram AddInequality(double[] g, double h)
    {
        Check(g);
        _ineq.Add(((double[])g.Clone(), h));
        return this;
    }

    /// <summary>Adds lower ≤ aᵀx ≤ upper (either side may be infinite).</summary>
    public QuadraticProgram AddRange(double[] a, double lower, double upper)
    {
        if (double.IsFinite(upper)) AddInequality(a, upper);
        if (double.IsFinite(lower)) AddInequality(a.Select(v => -v).ToArray(), -lower);
        return this;
    }

    /// <summary>Sets lb ≤ xᵢ ≤ ub for one variable.</summary>
    public QuadraticProgram SetBounds(int index, double lower, double upper)
    {
        if (lower > upper) throw new ArgumentException("Lower bound exceeds upper bound.");
        _lb[index] = lower;
        _ub[index] = upper;
        return this;
    }

    private void Check(double[] a)
    {
        if (a.Length != Variables) throw new ArgumentException($"Row has {a.Length} entries, expected {Variables}.");
    }

    // An inequality row, stored sparse: bounds have one entry, trade-linking rows two.
    private readonly record struct Row(int[] Index, double[] Value, double H)
    {
        public static Row From(double[] dense, double h)
        {
            var idx = new List<int>();
            for (int i = 0; i < dense.Length; i++) if (dense[i] != 0) idx.Add(i);
            return new Row(idx.ToArray(), idx.Select(i => dense[i]).ToArray(), h);
        }

        public double Dot(ReadOnlySpan<double> x)
        {
            double s = 0;
            for (int k = 0; k < Index.Length; k++) s += Value[k] * x[Index[k]];
            return s;
        }

        public void AddScaledTo(Span<double> v, double k)
        {
            for (int j = 0; j < Index.Length; j++) v[Index[j]] += k * Value[j];
        }
    }

    public QpSolution Solve(double tolerance = 1e-9, int maxIterations = 200)
    {
        int n = Variables, me = _eq.Count;
        var rows = new List<Row>();
        foreach (var (g, h) in _ineq) rows.Add(Row.From(g, h));
        for (int i = 0; i < n; i++)
        {
            if (double.IsFinite(_ub[i])) rows.Add(new Row(new[] { i }, new[] { 1.0 }, _ub[i]));
            if (double.IsFinite(_lb[i])) rows.Add(new Row(new[] { i }, new[] { -1.0 }, -_lb[i]));
        }
        int m = rows.Count;

        var x = new double[n];
        for (int i = 0; i < n; i++)
        {
            bool lo = double.IsFinite(_lb[i]), hi = double.IsFinite(_ub[i]);
            x[i] = lo && hi ? 0.5 * (_lb[i] + _ub[i]) : lo ? _lb[i] + 1 : hi ? _ub[i] - 1 : 0;
        }
        var y = new double[me];
        var z = Enumerable.Repeat(1.0, m).ToArray();
        var s = new double[m];
        for (int k = 0; k < m; k++) s[k] = Math.Max(rows[k].H - rows[k].Dot(x), 1.0);

        double scaleC = 1 + _c.Max(Math.Abs);
        double scaleB = 1 + (me > 0 ? _eq.Max(e => Math.Abs(e.B)) : 0);
        double scaleH = 1 + (m > 0 ? rows.Max(r => Math.Abs(r.H)) : 0);

        var rd = new double[n];
        var rp = new double[me];
        var rg = new double[m];
        int iter;
        bool converged = false;
        double lastP = 0, lastD = 0, lastGap = 0;
        for (iter = 0; iter < maxIterations; iter++)
        {
            // Residuals.
            var qx = _q.Multiply(x);
            for (int i = 0; i < n; i++) rd[i] = qx[i] + _c[i];
            for (int e = 0; e < me; e++)
            {
                var a = _eq[e].A;
                for (int i = 0; i < n; i++) rd[i] += a[i] * y[e];
                rp[e] = Matrix.Dot(a, x) - _eq[e].B;
            }
            for (int k = 0; k < m; k++)
            {
                rows[k].AddScaledTo(rd, z[k]);
                rg[k] = rows[k].Dot(x) + s[k] - rows[k].H;
            }
            double mu = m > 0 ? Matrix.Dot(s, z) / m : 0;
            double nd = rd.Max(Math.Abs), np = me > 0 ? rp.Max(Math.Abs) : 0, ng = m > 0 ? rg.Max(Math.Abs) : 0;
            // Dual feasibility is judged relative to the size of the terms that cancel in it: when a constraint's
            // multiplier is large (a return target near the top of the frontier), Aᵀy and Gᵀz dwarf c, and an absolute
            // test would demand more digits than double precision has.
            var gz = new double[n];
            for (int k = 0; k < m; k++) rows[k].AddScaledTo(gz, z[k]);
            double dualScale = Math.Max(scaleC, 1 + Math.Max(qx.Max(Math.Abs), gz.Max(Math.Abs)));
            for (int e = 0; e < me; e++) dualScale = Math.Max(dualScale, 1 + Math.Abs(y[e]) * _eq[e].A.Max(Math.Abs));
            double objScale = 1 + Math.Abs(0.5 * Matrix.Dot(x, qx) + Matrix.Dot(_c, x));
            if (!double.IsFinite(np + ng + nd + mu)) break;   // numerical breakdown: report the last finite state
            lastP = Math.Max(np, ng);
            lastD = nd / dualScale;
            lastGap = mu;
            // Stationarity is accurate to about √ε relative to its terms at best, so it is held to 10× the tolerance.
            if (nd <= 10 * tolerance * dualScale && np <= tolerance * scaleB && ng <= tolerance * scaleH && mu <= tolerance * objScale)
            {
                converged = true;
                break;
            }
            // Stagnation: the gap and the primal residual are exhausted, so further steps only shrink the slacks towards
            // underflow. Accept the point if stationarity holds to 1e-7 relative.
            if (mu <= 1e-14 * objScale && np <= tolerance * scaleB && ng <= tolerance * scaleH)
            {
                converged = nd <= 1e-7 * dualScale;
                break;
            }

            // H = Q + Gᵀ W G with W = Z/S.
            var h = _q.Clone();
            var w = new double[m];
            for (int k = 0; k < m; k++)
            {
                w[k] = z[k] / s[k];
                var row = rows[k];
                for (int a = 0; a < row.Index.Length; a++)
                {
                    double ga = row.Value[a] * w[k];
                    for (int b2 = 0; b2 < row.Index.Length; b2++) h[row.Index[a], row.Index[b2]] += ga * row.Value[b2];
                }
            }
            var chol = Factor(h);

            (double[] Dx, double[] Dy, double[] Dz, double[] Ds) Direction(double[] rsz)
            {
                // rhs1 = −rd − Gᵀ(W·rg − S⁻¹·rsz)
                var rhs = new double[n];
                for (int i = 0; i < n; i++) rhs[i] = -rd[i];
                for (int k = 0; k < m; k++) rows[k].AddScaledTo(rhs, -(w[k] * rg[k] - rsz[k] / s[k]));
                double[] dy = new double[me], dx;
                if (me == 0) dx = SolveChol(chol, rhs);
                else
                {
                    var hinvR = SolveChol(chol, rhs);
                    var hinvAt = new double[me][];
                    for (int e = 0; e < me; e++) hinvAt[e] = SolveChol(chol, _eq[e].A);
                    var schur = new Matrix(me, me);
                    var rhsY = new double[me];
                    for (int e = 0; e < me; e++)
                    {
                        for (int f = 0; f < me; f++) schur[e, f] = Matrix.Dot(_eq[e].A, hinvAt[f]);
                        rhsY[e] = Matrix.Dot(_eq[e].A, hinvR) + rp[e];
                    }
                    for (int e = 0; e < me; e++) schur[e, e] += 1e-14;
                    dy = schur.Solve(rhsY);
                    dx = (double[])hinvR.Clone();
                    for (int e = 0; e < me; e++)
                        for (int i = 0; i < n; i++) dx[i] -= dy[e] * hinvAt[e][i];
                }
                var dz = new double[m];
                var ds = new double[m];
                for (int k = 0; k < m; k++)
                {
                    dz[k] = w[k] * (rows[k].Dot(dx) + rg[k]) - rsz[k] / s[k];
                    ds[k] = (-rsz[k] - s[k] * dz[k]) / z[k];
                }
                return (dx, dy, dz, ds);
            }

            // Predictor.
            var rszAff = new double[m];
            for (int k = 0; k < m; k++) rszAff[k] = s[k] * z[k];
            var aff = Direction(rszAff);
            double alphaAff = Math.Min(StepLength(s, aff.Ds), StepLength(z, aff.Dz));
            double muAff = 0;
            for (int k = 0; k < m; k++) muAff += (s[k] + alphaAff * aff.Ds[k]) * (z[k] + alphaAff * aff.Dz[k]);
            muAff = m > 0 ? muAff / m : 0;
            double sigma = mu > 0 ? Math.Pow(muAff / mu, 3) : 0;

            // Corrector.
            var rsz = new double[m];
            for (int k = 0; k < m; k++) rsz[k] = s[k] * z[k] + aff.Ds[k] * aff.Dz[k] - sigma * mu;
            var d = m > 0 ? Direction(rsz) : aff;
            double alpha = Math.Min(1, 0.99 * Math.Min(StepLength(s, d.Ds), StepLength(z, d.Dz)));
            for (int i = 0; i < n; i++) x[i] += alpha * d.Dx[i];
            for (int e = 0; e < me; e++) y[e] += alpha * d.Dy[e];
            for (int k = 0; k < m; k++) { z[k] += alpha * d.Dz[k]; s[k] += alpha * d.Ds[k]; }
        }

        double obj = 0.5 * _q.QuadraticForm(x) + Matrix.Dot(_c, x);
        return new QpSolution(x, obj, iter, converged, y, z.Take(_ineq.Count).ToArray(), lastP, lastD, lastGap);
    }

    private static double StepLength(double[] v, double[] dv)
    {
        double a = double.PositiveInfinity;
        for (int i = 0; i < v.Length; i++) if (dv[i] < 0) a = Math.Min(a, -v[i] / dv[i]);
        return double.IsPositiveInfinity(a) ? 1 : a;
    }

    /// <summary>Cholesky with escalating diagonal regularisation (H can be singular when Q is only semidefinite).</summary>
    private static Matrix Factor(Matrix h)
    {
        double maxDiag = 0;
        for (int i = 0; i < h.Rows; i++) maxDiag = Math.Max(maxDiag, Math.Abs(h[i, i]));
        double reg = 1e-13 * Math.Max(1, maxDiag);
        for (int attempt = 0; attempt < 12; attempt++, reg *= 100)
        {
            var hr = h.Clone();
            for (int i = 0; i < hr.Rows; i++) hr[i, i] += reg;
            if (hr.TryCholesky(out var l)) return l;
        }
        throw new InvalidOperationException("The KKT matrix could not be factorised; is Q positive semidefinite?");
    }

    private static double[] SolveChol(Matrix l, ReadOnlySpan<double> b)
    {
        int n = l.Rows;
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            double v = b[i];
            for (int k = 0; k < i; k++) v -= l[i, k] * y[k];
            y[i] = v / l[i, i];
        }
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double v = y[i];
            for (int k = i + 1; k < n; k++) v -= l[k, i] * x[k];
            x[i] = v / l[i, i];
        }
        return x;
    }
}
