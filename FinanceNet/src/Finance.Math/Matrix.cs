using System.Numerics;
using System.Runtime.InteropServices;
using Gravicode.Science.GraviNum;

namespace Finance.Mathematics;

/// <summary>
/// Dense row-major matrix of doubles, sized for finance workloads (covariance matrices,
/// factor loadings, regression designs). Small decompositions run here; large or exotic ones
/// (SVD, eigen) are delegated to GraviNum through <see cref="ToNdArray"/>.
/// </summary>
public sealed class Matrix
{
    private readonly double[] _data;

    public Matrix(int rows, int columns)
    {
        if (rows < 0 || columns < 0) throw new ArgumentOutOfRangeException(nameof(rows));
        Rows = rows;
        Columns = columns;
        _data = new double[rows * columns];
    }

    /// <summary>Wraps an existing row-major buffer (no copy).</summary>
    public Matrix(int rows, int columns, double[] data)
    {
        if (data.Length != rows * columns) throw new ArgumentException("Buffer size does not match shape.");
        Rows = rows;
        Columns = columns;
        _data = data;
    }

    public int Rows { get; }
    public int Columns { get; }
    public bool IsSquare => Rows == Columns;

    /// <summary>The underlying row-major buffer.</summary>
    public double[] Data => _data;

    public double this[int r, int c]
    {
        get => _data[r * Columns + c];
        set => _data[r * Columns + c] = value;
    }

    /// <summary>A writable view of row <paramref name="r"/>.</summary>
    public Span<double> Row(int r) => _data.AsSpan(r * Columns, Columns);

    /// <summary>A copy of column <paramref name="c"/>.</summary>
    public double[] Column(int c)
    {
        var col = new double[Rows];
        for (int r = 0; r < Rows; r++) col[r] = _data[r * Columns + c];
        return col;
    }

    public static Matrix Identity(int n)
    {
        var m = new Matrix(n, n);
        for (int i = 0; i < n; i++) m[i, i] = 1;
        return m;
    }

    public static Matrix Diagonal(ReadOnlySpan<double> diag)
    {
        var m = new Matrix(diag.Length, diag.Length);
        for (int i = 0; i < diag.Length; i++) m[i, i] = diag[i];
        return m;
    }

    public static Matrix FromRows(IReadOnlyList<double[]> rows)
    {
        int r = rows.Count, c = r == 0 ? 0 : rows[0].Length;
        var m = new Matrix(r, c);
        for (int i = 0; i < r; i++)
        {
            if (rows[i].Length != c) throw new ArgumentException("Ragged rows.");
            rows[i].CopyTo(m.Row(i));
        }
        return m;
    }

    public static Matrix FromArray(double[,] a)
    {
        int r = a.GetLength(0), c = a.GetLength(1);
        var m = new Matrix(r, c);
        for (int i = 0; i < r; i++)
            for (int j = 0; j < c; j++) m[i, j] = a[i, j];
        return m;
    }

    /// <summary>Builds a matrix whose columns are the given vectors.</summary>
    public static Matrix FromColumns(IReadOnlyList<double[]> columns)
    {
        int c = columns.Count, r = c == 0 ? 0 : columns[0].Length;
        var m = new Matrix(r, c);
        for (int j = 0; j < c; j++)
            for (int i = 0; i < r; i++) m[i, j] = columns[j][i];
        return m;
    }

    public double[,] ToArray()
    {
        var a = new double[Rows, Columns];
        for (int i = 0; i < Rows; i++)
            for (int j = 0; j < Columns; j++) a[i, j] = this[i, j];
        return a;
    }

    public double[][] ToJagged()
    {
        var a = new double[Rows][];
        for (int i = 0; i < Rows; i++) a[i] = Row(i).ToArray();
        return a;
    }

    public Matrix Clone() => new(Rows, Columns, (double[])_data.Clone());

    public Matrix Transpose()
    {
        var t = new Matrix(Columns, Rows);
        for (int i = 0; i < Rows; i++)
            for (int j = 0; j < Columns; j++) t[j, i] = this[i, j];
        return t;
    }

    /// <summary>Matrix product. Uses the i-k-j loop order so the inner loop streams rows (SIMD friendly).</summary>
    public static Matrix operator *(Matrix a, Matrix b)
    {
        if (a.Columns != b.Rows) throw new ArgumentException($"Shape mismatch {a.Rows}x{a.Columns} * {b.Rows}x{b.Columns}.");
        var c = new Matrix(a.Rows, b.Columns);
        for (int i = 0; i < a.Rows; i++)
        {
            var ci = c.Row(i);
            for (int k = 0; k < a.Columns; k++)
            {
                double aik = a[i, k];
                if (aik == 0) continue;
                Axpy(aik, b.Row(k), ci);
            }
        }
        return c;
    }

    /// <summary>Matrix-vector product.</summary>
    public double[] Multiply(ReadOnlySpan<double> x)
    {
        if (x.Length != Columns) throw new ArgumentException("Vector length mismatch.");
        var y = new double[Rows];
        for (int i = 0; i < Rows; i++) y[i] = Dot(Row(i), x);
        return y;
    }

    /// <summary>Quadratic form xᵀ A x.</summary>
    public double QuadraticForm(ReadOnlySpan<double> x)
    {
        if (!IsSquare || x.Length != Rows) throw new ArgumentException("Shape mismatch.");
        double s = 0;
        for (int i = 0; i < Rows; i++) s += x[i] * Dot(Row(i), x);
        return s;
    }

    public static Matrix operator +(Matrix a, Matrix b) => Elementwise(a, b, 1);
    public static Matrix operator -(Matrix a, Matrix b) => Elementwise(a, b, -1);

    public static Matrix operator *(double k, Matrix a)
    {
        var r = a.Clone();
        for (int i = 0; i < r._data.Length; i++) r._data[i] *= k;
        return r;
    }

    private static Matrix Elementwise(Matrix a, Matrix b, double sign)
    {
        if (a.Rows != b.Rows || a.Columns != b.Columns) throw new ArgumentException("Shape mismatch.");
        var r = a.Clone();
        Axpy(sign, b._data, r._data);
        return r;
    }

    /// <summary>Lower Cholesky factor L with A = L Lᵀ. Throws when A is not positive definite.</summary>
    public Matrix Cholesky()
    {
        if (!TryCholesky(out var l)) throw new InvalidOperationException("Matrix is not positive definite.");
        return l;
    }

    public bool TryCholesky(out Matrix lower)
    {
        if (!IsSquare) throw new InvalidOperationException("Cholesky needs a square matrix.");
        int n = Rows;
        lower = new Matrix(n, n);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double s = this[i, j];
                s -= Dot(lower.Row(i)[..j], lower.Row(j)[..j]);
                if (i == j)
                {
                    if (s <= 0) return false;
                    lower[i, i] = Math.Sqrt(s);
                }
                else lower[i, j] = s / lower[j, j];
            }
        }
        return true;
    }

    /// <summary>Solves A x = b by LU with partial pivoting.</summary>
    public double[] Solve(ReadOnlySpan<double> b)
    {
        if (!IsSquare || b.Length != Rows) throw new ArgumentException("Shape mismatch.");
        int n = Rows;
        var lu = Clone();
        var perm = new int[n];
        for (int i = 0; i < n; i++) perm[i] = i;
        Factor(lu, perm);
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = b[perm[i]];
        SubstituteLu(lu, x);
        return x;
    }

    /// <summary>Inverse by LU with partial pivoting.</summary>
    public Matrix Inverse()
    {
        if (!IsSquare) throw new InvalidOperationException("Inverse needs a square matrix.");
        int n = Rows;
        var lu = Clone();
        var perm = new int[n];
        for (int i = 0; i < n; i++) perm[i] = i;
        Factor(lu, perm);
        var inv = new Matrix(n, n);
        var col = new double[n];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++) col[i] = perm[i] == j ? 1 : 0;
            SubstituteLu(lu, col);
            for (int i = 0; i < n; i++) inv[i, j] = col[i];
        }
        return inv;
    }

    /// <summary>Determinant via LU.</summary>
    public double Determinant()
    {
        if (!IsSquare) throw new InvalidOperationException("Determinant needs a square matrix.");
        int n = Rows;
        var lu = Clone();
        var perm = new int[n];
        for (int i = 0; i < n; i++) perm[i] = i;
        int swaps;
        try { swaps = Factor(lu, perm); }
        catch (InvalidOperationException) { return 0; }
        double det = swaps % 2 == 0 ? 1 : -1;
        for (int i = 0; i < n; i++) det *= lu[i, i];
        return det;
    }

    /// <summary>Symmetric eigen-decomposition (eigenvalues descending), delegated to GraviNum.</summary>
    public (double[] Values, Matrix Vectors) SymmetricEigen()
    {
        if (!IsSquare) throw new InvalidOperationException("Eigen needs a square matrix.");
        var result = Decomposition.SymmetricEigen(ToNdArray());
        var values = result.Values.ToArray();
        var vectors = FromNdArray(result.Vectors);
        return (values, vectors);
    }

    /// <summary>Copies into a GraviNum <see cref="NdArray"/>.</summary>
    public NdArray ToNdArray() => new((double[])_data.Clone(), new[] { Rows, Columns });

    /// <summary>Copies a 2-D GraviNum array into a <see cref="Matrix"/>.</summary>
    public static Matrix FromNdArray(NdArray array)
    {
        var shape = array.Shape;
        if (shape.Length != 2) throw new ArgumentException("Expected a 2-D array.");
        return new Matrix(shape[0], shape[1], array.ToArray());
    }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Rows; i++)
        {
            for (int j = 0; j < Columns; j++) sb.Append(this[i, j].ToString("F6").PadLeft(14));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Dot product (SIMD).</summary>
    public static double Dot(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        int n = x.Length, i = 0;
        double s = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<double>.Count * 2)
        {
            var vx = MemoryMarshal.Cast<double, Vector<double>>(x);
            var vy = MemoryMarshal.Cast<double, Vector<double>>(y[..n]);
            var acc = Vector<double>.Zero;
            for (int k = 0; k < vx.Length; k++) acc += vx[k] * vy[k];
            s = Vector.Sum(acc);
            i = vx.Length * Vector<double>.Count;
        }
        for (; i < n; i++) s += x[i] * y[i];
        return s;
    }

    /// <summary>y += a * x (SIMD).</summary>
    public static void Axpy(double a, ReadOnlySpan<double> x, Span<double> y)
    {
        int n = x.Length, i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<double>.Count * 2)
        {
            var vx = MemoryMarshal.Cast<double, Vector<double>>(x);
            var vy = MemoryMarshal.Cast<double, Vector<double>>(y[..n]);
            var va = new Vector<double>(a);
            for (int k = 0; k < vx.Length; k++) vy[k] += va * vx[k];
            i = vx.Length * Vector<double>.Count;
        }
        for (; i < n; i++) y[i] += a * x[i];
    }

    private static int Factor(Matrix lu, int[] perm)
    {
        int n = lu.Rows, swaps = 0;
        for (int k = 0; k < n; k++)
        {
            int p = k;
            double max = Math.Abs(lu[k, k]);
            for (int i = k + 1; i < n; i++)
            {
                double v = Math.Abs(lu[i, k]);
                if (v > max) { max = v; p = i; }
            }
            if (max < 1e-300) throw new InvalidOperationException("Matrix is singular.");
            if (p != k)
            {
                var tmp = lu.Row(k).ToArray();
                lu.Row(p).CopyTo(lu.Row(k));
                tmp.CopyTo(lu.Row(p));
                (perm[k], perm[p]) = (perm[p], perm[k]);
                swaps++;
            }
            double pivot = lu[k, k];
            for (int i = k + 1; i < n; i++)
            {
                double f = lu[i, k] / pivot;
                lu[i, k] = f;
                if (f != 0) Axpy(-f, lu.Row(k)[(k + 1)..], lu.Row(i)[(k + 1)..]);
            }
        }
        return swaps;
    }

    private static void SubstituteLu(Matrix lu, Span<double> x)
    {
        int n = lu.Rows;
        for (int i = 0; i < n; i++)
            x[i] -= Dot(lu.Row(i)[..i], x[..i]);
        for (int i = n - 1; i >= 0; i--)
        {
            x[i] -= Dot(lu.Row(i)[(i + 1)..], x[(i + 1)..]);
            x[i] /= lu[i, i];
        }
    }
}
