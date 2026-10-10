using Finance.Core;
using Finance.Mathematics;
using Xunit;

namespace Finance.Foundation.Tests;

public class StatsTests
{
    private static readonly double[] Data = { 2, 4, 4, 4, 5, 5, 7, 9 };

    [Fact]
    public void MeanAndVariance_MatchTextbook()
    {
        Assert.Equal(5.0, Stats.Mean(Data), 12);
        Assert.Equal(4.0, Stats.Variance(Data, ddof: 0), 12);
        Assert.Equal(32.0 / 7, Stats.Variance(Data), 12);
    }

    [Fact]
    public void Variance_IsStableOnPriceLevelData()
    {
        // A naive one-pass formula loses all precision here.
        var x = new double[1000];
        for (int i = 0; i < x.Length; i++) x[i] = 1e9 + (i % 2 == 0 ? 1 : -1);
        Assert.Equal(1.0, Stats.Variance(x, 0), 9);
    }

    [Fact]
    public void Quantile_UsesLinearInterpolation()
    {
        double[] x = { 1, 2, 3, 4 };
        Assert.Equal(2.5, Stats.Quantile(x, 0.5), 12);
        Assert.Equal(1.3, Stats.Quantile(x, 0.1), 12); // numpy.quantile([1,2,3,4], 0.1)
    }

    [Fact]
    public void Correlation_OfLinearData_IsOne()
    {
        double[] x = { 1, 2, 3, 4, 5 };
        double[] y = { 3, 5, 7, 9, 11 };
        Assert.Equal(1.0, Stats.Correlation(x, y), 12);
        Assert.Equal(1.0, Stats.SpearmanCorrelation(x, new double[] { 1, 4, 9, 16, 25 }), 12);
    }

    [Fact]
    public void Rank_AveragesTies()
    {
        Assert.Equal(new[] { 1.0, 2.5, 2.5, 4.0 }, Stats.Rank(new double[] { 1, 5, 5, 9 }));
    }

    [Fact]
    public void SkewAndKurtosis_OfNormalSample_AreNearZero()
    {
        var rng = new FastRandom(11);
        var x = new double[200_000];
        rng.FillGaussian(x);
        Assert.InRange(Stats.Skewness(x), -0.02, 0.02);
        Assert.InRange(Stats.ExcessKurtosis(x), -0.05, 0.05);
    }

    [Fact]
    public void CovarianceMatrix_MatchesPairwiseCovariance()
    {
        var rng = new FastRandom(5);
        var m = new Matrix(50, 3);
        for (int i = 0; i < 50; i++)
            for (int j = 0; j < 3; j++) m[i, j] = rng.NextGaussian() + j * m[i, 0];
        var cov = Stats.CovarianceMatrix(m);
        for (int a = 0; a < 3; a++)
            for (int b = 0; b < 3; b++)
                Assert.Equal(Stats.Covariance(m.Column(a), m.Column(b)), cov[a, b], 12);
    }

    [Fact]
    public void LedoitWolf_ShrinkageIsInUnitInterval_AndKeepsTrace()
    {
        var rng = new FastRandom(9);
        var m = new Matrix(30, 10);
        for (int i = 0; i < 30; i++)
            for (int j = 0; j < 10; j++) m[i, j] = rng.NextGaussian();
        var (cov, shrink) = Stats.LedoitWolf(m);
        Assert.InRange(shrink, 0, 1);
        var sample = Stats.CovarianceMatrix(m, ddof: 0);
        double t1 = 0, t2 = 0;
        for (int i = 0; i < 10; i++) { t1 += cov[i, i]; t2 += sample[i, i]; }
        Assert.Equal(t2, t1, 10);
        Assert.True(cov.TryCholesky(out _));
    }
}

public class MatrixTests
{
    private static Matrix Spd() => Matrix.FromArray(new double[,] { { 4, 12, -16 }, { 12, 37, -43 }, { -16, -43, 98 } });

    [Fact]
    public void Cholesky_KnownFactor()
    {
        var l = Spd().Cholesky();
        double[,] expected = { { 2, 0, 0 }, { 6, 1, 0 }, { -8, 5, 3 } };
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) Assert.Equal(expected[i, j], l[i, j], 12);
    }

    [Fact]
    public void InverseTimesMatrix_IsIdentity()
    {
        var a = Matrix.FromArray(new double[,] { { 0, 2, 1 }, { 1, 1, 0 }, { 3, 0, 4 } });
        var p = a * a.Inverse();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) Assert.Equal(i == j ? 1 : 0, p[i, j], 12);
    }

    [Fact]
    public void Solve_AndDeterminant()
    {
        var a = Matrix.FromArray(new double[,] { { 2, 1, -1 }, { -3, -1, 2 }, { -2, 1, 2 } });
        var x = a.Solve(new double[] { 8, -11, -3 });
        Assert.Equal(2, x[0], 12);
        Assert.Equal(3, x[1], 12);
        Assert.Equal(-1, x[2], 12);
        Assert.Equal(-1, a.Determinant(), 12);
    }

    [Fact]
    public void SymmetricEigen_ReconstructsMatrix()
    {
        var a = Spd();
        var (values, vectors) = a.SymmetricEigen();
        Assert.True(values[0] >= values[1] && values[1] >= values[2]);
        var recon = vectors * Matrix.Diagonal(values) * vectors.Transpose();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) Assert.Equal(a[i, j], recon[i, j], 8);
    }

    [Fact]
    public void QuadraticForm_MatchesExplicitProduct()
    {
        var a = Spd();
        double[] w = { 0.2, 0.3, 0.5 };
        var aw = a.Multiply(w);
        Assert.Equal(Matrix.Dot(w, aw), a.QuadraticForm(w), 12);
    }
}

public class DistributionTests
{
    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(1.0, 0.8413447460685429)]
    [InlineData(-1.96, 0.024997895148220435)]
    [InlineData(3.0, 0.9986501019683699)]
    [InlineData(-6.0, 9.865876450376946e-10)]
    public void NormalCdf_MatchesReference(double x, double expected)
    {
        Assert.Equal(expected, Normal.Cdf(x), 1e-14 + 1e-12 * expected);
    }

    [Theory]
    [InlineData(0.975, 1.959963984540054)]
    [InlineData(0.01, -2.3263478740408408)]
    [InlineData(1e-8, -5.612001244174789)]
    public void NormalInverseCdf_MatchesReference(double p, double expected)
    {
        Assert.Equal(expected, Normal.InverseCdf(p), 1e-10);
    }

    [Fact]
    public void InverseCdf_RoundTrips()
    {
        for (double p = 0.001; p < 1; p += 0.0137)
            Assert.Equal(p, Normal.Cdf(Normal.InverseCdf(p)), 1e-13);
    }

    [Theory]
    [InlineData(2.0, 5.0, 0.9490302605850709)]
    [InlineData(-1.5, 3.0, 0.11529193262241165)] // closed form for nu = 3
    public void StudentTCdf_MatchesScipy(double x, double nu, double expected)
    {
        Assert.Equal(expected, StudentT.Cdf(x, nu), 1e-10);
    }

    [Fact]
    public void StudentTInverse_RoundTrips()
    {
        Assert.Equal(2.570581835636314, StudentT.InverseCdf(0.975, 5), 1e-8);
    }
}

public class NumericsTests
{
    [Fact]
    public void Brent_FindsCubeRoot()
    {
        double r = RootFinding.Brent(x => x * x * x - 2, 0, 2);
        Assert.Equal(Math.Cbrt(2), r, 11);
    }

    [Fact]
    public void Newton_FindsSquareRoot()
    {
        Assert.Equal(Math.Sqrt(2), RootFinding.Newton(x => x * x - 2, x => 2 * x, 1), 12);
    }

    [Fact]
    public void SimplexProjection_RespectsBoundsAndSum()
    {
        double[] v = { 0.9, -0.3, 0.6, 0.1 };
        Optimizer.ProjectOntoSimplex(v, 1.0, 0.0, 0.5);
        Assert.Equal(1.0, v.Sum(), 12);
        Assert.All(v, w => Assert.InRange(w, -1e-15, 0.5 + 1e-15));
    }

    [Fact]
    public void ProjectedGradient_SolvesMinVarianceOnSimplex()
    {
        // Two uncorrelated assets: min-variance weights are proportional to 1/variance.
        double[] var = { 0.04, 0.01 };
        var r = Optimizer.ProjectedGradient(
            w => var[0] * w[0] * w[0] + var[1] * w[1] * w[1],
            (w, g) => { g[0] = 2 * var[0] * w[0]; g[1] = 2 * var[1] * w[1]; },
            new[] { 0.5, 0.5 },
            w => Optimizer.ProjectOntoSimplex(w));
        Assert.Equal(0.2, r.X[0], 6);
        Assert.Equal(0.8, r.X[1], 6);
    }

    [Fact]
    public void NelderMead_MinimisesRosenbrock()
    {
        var r = Optimizer.NelderMead(x => Math.Pow(1 - x[0], 2) + 100 * Math.Pow(x[1] - x[0] * x[0], 2), new[] { -1.2, 1.0 }, maxIterations: 20000, tolerance: 1e-14);
        Assert.Equal(1.0, r.X[0], 3);
        Assert.Equal(1.0, r.X[1], 3);
    }

    [Fact]
    public void CubicSpline_InterpolatesSmoothFunction()
    {
        var xs = Enumerable.Range(0, 21).Select(i => i * 0.5).ToArray();
        var spline = new CubicSplineInterpolator(xs, xs.Select(Math.Sin).ToArray());
        Assert.Equal(Math.Sin(3.3), spline.Evaluate(3.3), 3);
        Assert.Equal(Math.Sin(2.0), spline.Evaluate(2.0), 12);
    }

    [Fact]
    public void Integration_OfGaussianDensity()
    {
        Assert.Equal(Normal.Cdf(1) - Normal.Cdf(-1), Integration.GaussLegendre(Normal.Pdf, -1, 1), 13);
        Assert.Equal(1.0 / 3, Integration.Simpson(x => x * x, 0, 1), 12);
    }

    [Fact]
    public void Ols_RecoversCoefficients()
    {
        var rng = new FastRandom(3);
        int n = 500;
        var x = new Matrix(n, 2);
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            x[i, 0] = rng.NextGaussian();
            x[i, 1] = rng.NextGaussian();
            y[i] = 0.5 + 1.5 * x[i, 0] - 2.0 * x[i, 1] + 0.01 * rng.NextGaussian();
        }
        var fit = Regression.Ols(x, y);
        Assert.Equal(0.5, fit.Intercept, 2);
        Assert.Equal(1.5, fit.Slopes[0], 2);
        Assert.Equal(-2.0, fit.Slopes[1], 2);
        Assert.True(fit.RSquared > 0.999);
    }
}
