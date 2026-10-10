using Finance.Core;
using Finance.Mathematics;
using Finance.TimeSeries;
using Xunit;

namespace Finance.Analytics.Tests;

public class RollingReturnsTests
{
    private static double[] Data(int n = 200, int nanPrefix = 3)
    {
        var rng = new FastRandom(5);
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = i < nanPrefix ? double.NaN : 100 + 10 * rng.NextGaussian();
        return x;
    }

    private static void Close(double expected, double actual, double tol = 1e-9)
    {
        if (double.IsNaN(expected)) Assert.True(double.IsNaN(actual), $"expected NaN, got {actual}");
        else Assert.True(Math.Abs(expected - actual) <= tol * (1 + Math.Abs(expected)), $"expected {expected}, got {actual}");
    }

    private static double[] Naive(double[] x, int w, Func<double[], double> f)
    {
        var r = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            if (i < w - 1) { r[i] = double.NaN; continue; }
            var win = x[(i - w + 1)..(i + 1)];
            r[i] = win.Any(double.IsNaN) ? double.NaN : f(win);
        }
        return r;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void RollingStatsMatchNaive(int w)
    {
        var x = Data();
        var mean = Rolling.Mean(x, w);
        var max = Rolling.Max(x, w);
        var min = Rolling.Min(x, w);
        var nm = Naive(x, w, a => a.Average());
        var nmax = Naive(x, w, a => a.Max());
        var nmin = Naive(x, w, a => a.Min());
        for (int i = 0; i < x.Length; i++)
        {
            Close(nm[i], mean[i]);
            Close(nmax[i], max[i]);
            Close(nmin[i], min[i]);
        }
        if (w > 1)
        {
            var sd = Rolling.StdDev(x, w);
            var nsd = Naive(x, w, a => Stats.StdDev(a));
            for (int i = 0; i < x.Length; i++) Close(nsd[i], sd[i], 1e-8);
        }
    }

    [Fact]
    public void RollingCorrelationAndBetaMatchNaive()
    {
        var x = Data(150, 0);
        var rng = new FastRandom(9);
        var y = x.Select(v => 0.5 * v + 3 * rng.NextGaussian()).ToArray();
        const int w = 30;
        var c = Rolling.Correlation(x, y, w);
        var b = Rolling.Beta(y, x, w);
        for (int i = w - 1; i < x.Length; i++)
        {
            var xs = x[(i - w + 1)..(i + 1)];
            var ys = y[(i - w + 1)..(i + 1)];
            Close(Stats.Correlation(xs, ys), c[i], 1e-7);
            Close(Stats.Covariance(ys, xs) / Stats.Variance(xs), b[i], 1e-7);
        }
    }

    [Fact]
    public void LongSeriesRollingSumDoesNotDrift()
    {
        var x = Enumerable.Range(0, 200_000).Select(i => 1e6 + (i % 7) * 0.1).ToArray();
        var s = Rolling.Sum(x, 10);
        double expected = x[^10..].Sum();
        Assert.Equal(expected, s[^1], 6);
    }

    [Fact]
    public void ReturnsAndDrawdown()
    {
        double[] p = { 100, 110, 99, 120, 60, 90 };
        var r = Returns.Simple(p);
        Assert.Equal(0.1, r[0], 12);
        Assert.Equal(-0.1, r[1], 12);
        Assert.Equal(p[^1] / p[0] - 1, Returns.Total(r), 12);
        var cum = Returns.Cumulative(r);
        Assert.Equal(-0.1, cum[^1], 12);
        var (mdd, peak, trough) = Returns.MaxDrawdown(p);
        Assert.Equal(-0.5, mdd, 12);
        Assert.Equal(3, peak);
        Assert.Equal(4, trough);
        var dd = Returns.Drawdown(p);
        Assert.Equal(0, dd[3]);
        Assert.Equal(-0.25, dd[5], 12);
        Assert.Equal(Math.Log(1.1), Returns.Log(p)[0], 12);
    }

    [Fact]
    public void SharpeOfConstantExcessIsZeroVolGuarded()
    {
        var r = Enumerable.Repeat(0.001, 100).ToArray();
        Assert.Equal(0, Returns.Sharpe(r));
        var rng = new FastRandom(2);
        var noisy = Enumerable.Range(0, 5000).Select(_ => 0.0005 + 0.01 * rng.NextGaussian()).ToArray();
        double s = Returns.Sharpe(noisy);
        double expected = Stats.Mean(noisy) / Stats.StdDev(noisy) * Math.Sqrt(252);
        Assert.Equal(expected, s, 12);
    }

    [Fact]
    public void EwmaVolatilityRecursion()
    {
        double[] r = { 0.01, -0.02, 0.015 };
        var v = Rolling.EwmaVolatility(r, 0.9);
        double v1 = 0.9 * 0.0001 + 0.1 * 0.0001;
        double v2 = 0.9 * v1 + 0.1 * 0.0004;
        Assert.Equal(Math.Sqrt(v2), v[2], 12);
    }

    [Fact]
    public void AlignmentHelpers()
    {
        var t1 = new[] { new DateTime(2024, 1, 1), new DateTime(2024, 1, 2), new DateTime(2024, 1, 4) };
        var t2 = new[] { new DateTime(2024, 1, 2), new DateTime(2024, 1, 3), new DateTime(2024, 1, 4) };
        var (t, a, b) = SeriesAlignment.InnerJoin(t1, new double[] { 1, 2, 4 }, t2, new double[] { 20, 30, 40 });
        Assert.Equal(new[] { t1[1], t1[2] }, t);
        Assert.Equal(new double[] { 2, 4 }, a);
        Assert.Equal(new double[] { 20, 40 }, b);
        var asof = SeriesAlignment.AsOf(t2, t1, new double[] { 1, 2, 4 });
        Assert.Equal(new double[] { 2, 2, 4 }, asof);
    }
}
