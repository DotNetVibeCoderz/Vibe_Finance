namespace Finance.Mathematics;

/// <summary>A one-dimensional interpolant.</summary>
public interface IInterpolator
{
    double Evaluate(double x);
}

/// <summary>Piecewise-linear interpolation with flat extrapolation.</summary>
public sealed class LinearInterpolator : IInterpolator
{
    private readonly double[] _x, _y;

    public LinearInterpolator(double[] x, double[] y)
    {
        Validate(x, y);
        _x = (double[])x.Clone();
        _y = (double[])y.Clone();
    }

    public double Evaluate(double x)
    {
        if (x <= _x[0]) return _y[0];
        if (x >= _x[^1]) return _y[^1];
        int i = Interval(_x, x);
        double t = (x - _x[i]) / (_x[i + 1] - _x[i]);
        return _y[i] + t * (_y[i + 1] - _y[i]);
    }

    internal static void Validate(double[] x, double[] y)
    {
        if (x.Length != y.Length) throw new ArgumentException("x and y differ in length.");
        if (x.Length < 2) throw new ArgumentException("Need at least two points.");
        for (int i = 1; i < x.Length; i++)
            if (!(x[i] > x[i - 1])) throw new ArgumentException("x must be strictly increasing.");
    }

    /// <summary>Index i with x[i] ≤ v &lt; x[i+1], for v inside the grid.</summary>
    internal static int Interval(double[] x, double v)
    {
        int i = Array.BinarySearch(x, v);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, x.Length - 2);
    }
}

/// <summary>Natural cubic spline (zero second derivative at both ends).</summary>
public sealed class CubicSplineInterpolator : IInterpolator
{
    private readonly double[] _x, _y, _m;

    public CubicSplineInterpolator(double[] x, double[] y)
    {
        LinearInterpolator.Validate(x, y);
        _x = (double[])x.Clone();
        _y = (double[])y.Clone();
        int n = x.Length;
        _m = new double[n];
        if (n == 2) return;
        // Tridiagonal system for the second derivatives (Thomas algorithm).
        var a = new double[n];
        var b = new double[n];
        var c = new double[n];
        var r = new double[n];
        b[0] = 1;
        b[n - 1] = 1;
        for (int i = 1; i < n - 1; i++)
        {
            double h0 = x[i] - x[i - 1], h1 = x[i + 1] - x[i];
            a[i] = h0;
            b[i] = 2 * (h0 + h1);
            c[i] = h1;
            r[i] = 6 * ((y[i + 1] - y[i]) / h1 - (y[i] - y[i - 1]) / h0);
        }
        for (int i = 1; i < n; i++)
        {
            double w = a[i] / b[i - 1];
            b[i] -= w * c[i - 1];
            r[i] -= w * r[i - 1];
        }
        _m[n - 1] = r[n - 1] / b[n - 1];
        for (int i = n - 2; i >= 0; i--) _m[i] = (r[i] - c[i] * _m[i + 1]) / b[i];
    }

    public double Evaluate(double x)
    {
        if (x <= _x[0]) return _y[0];
        if (x >= _x[^1]) return _y[^1];
        int i = LinearInterpolator.Interval(_x, x);
        double h = _x[i + 1] - _x[i];
        double a = (_x[i + 1] - x) / h, b = (x - _x[i]) / h;
        return a * _y[i] + b * _y[i + 1] + ((a * a * a - a) * _m[i] + (b * b * b - b) * _m[i + 1]) * h * h / 6;
    }
}

/// <summary>Linear interpolation on log(y): the standard choice for discount factors.</summary>
public sealed class LogLinearInterpolator : IInterpolator
{
    private readonly LinearInterpolator _inner;

    public LogLinearInterpolator(double[] x, double[] y)
    {
        if (y.Any(v => v <= 0)) throw new ArgumentException("Log-linear interpolation needs positive values.");
        _inner = new LinearInterpolator(x, y.Select(v => Math.Log(v)).ToArray());
    }

    public double Evaluate(double x) => Math.Exp(_inner.Evaluate(x));
}

/// <summary>Numerical integration.</summary>
public static class Integration
{
    /// <summary>Composite Simpson's rule with <paramref name="intervals"/> (rounded up to even) intervals.</summary>
    public static double Simpson(Func<double, double> f, double a, double b, int intervals = 200)
    {
        if (intervals % 2 == 1) intervals++;
        double h = (b - a) / intervals, s = f(a) + f(b);
        for (int i = 1; i < intervals; i++) s += f(a + i * h) * (i % 2 == 1 ? 4 : 2);
        return s * h / 3;
    }

    private static readonly double[] GlNodes =
    {
        -0.9739065285171717, -0.8650633666889845, -0.6794095682990244, -0.4333953941292472, -0.1488743389816312,
        0.1488743389816312, 0.4333953941292472, 0.6794095682990244, 0.8650633666889845, 0.9739065285171717,
    };

    private static readonly double[] GlWeights =
    {
        0.0666713443086881, 0.1494513491505806, 0.2190863625159820, 0.2692667193099963, 0.2955242247147529,
        0.2955242247147529, 0.2692667193099963, 0.2190863625159820, 0.1494513491505806, 0.0666713443086881,
    };

    /// <summary>10-point Gauss-Legendre on each of <paramref name="panels"/> sub-intervals.</summary>
    public static double GaussLegendre(Func<double, double> f, double a, double b, int panels = 8)
    {
        double total = 0, w = (b - a) / panels;
        for (int p = 0; p < panels; p++)
        {
            double lo = a + p * w, mid = lo + w / 2, half = w / 2;
            for (int i = 0; i < GlNodes.Length; i++) total += GlWeights[i] * f(mid + half * GlNodes[i]);
        }
        return total * w / 2;
    }

    /// <summary>Trapezoid rule over sampled points.</summary>
    public static double Trapezoid(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        double s = 0;
        for (int i = 1; i < x.Length; i++) s += 0.5 * (y[i] + y[i - 1]) * (x[i] - x[i - 1]);
        return s;
    }
}
