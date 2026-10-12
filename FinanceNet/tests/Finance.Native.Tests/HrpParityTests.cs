using Finance.Core;
using Finance.Mathematics;
using Finance.Portfolio;
using Finance.RustBridge;
using Xunit;

namespace Finance.Native.Tests;

public class HrpParityTests
{
    /// <summary>Random factor-model covariance (k factors + idiosyncratic noise), well conditioned.</summary>
    internal static Matrix Covariance(int n, ulong seed)
    {
        var rng = new FastRandom(seed);
        int k = 5;
        var beta = new double[n, k];
        for (int i = 0; i < n; i++)
            for (int f = 0; f < k; f++) beta[i, f] = rng.NextGaussian() * (f == i % k ? 1.0 : 0.3);
        var cov = new Matrix(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double s = 0;
                for (int f = 0; f < k; f++) s += beta[i, f] * beta[j, f];
                cov[i, j] = 0.01 * s + (i == j ? 0.02 + 0.01 * rng.NextDouble() : 0);
            }
        // Symmetrise the noise-free part exactly.
        for (int i = 0; i < n; i++)
            for (int j = 0; j < i; j++) cov[j, i] = cov[i, j];
        return cov;
    }

    [Theory]
    [InlineData(LinkageMethod.Single)]
    [InlineData(LinkageMethod.Complete)]
    [InlineData(LinkageMethod.Average)]
    public void NativeHrpMatchesManaged(LinkageMethod linkage)
    {
        Assert.True(RustAccelerator.IsAvailable, RustAccelerator.LoadError);
        foreach (int n in new[] { 1, 2, 3, 17, 80 })
        {
            var cov = Covariance(n, (ulong)(n + 3));
            var managed = HierarchicalRiskParity.Optimize(cov, linkage: linkage);
            var native = FastPortfolio.Hrp(cov, linkage: linkage);
            Assert.Equal(managed.Order, native.Order);
            Assert.Equal(managed.Linkage, native.Linkage);
            for (int i = 0; i < n; i++) Assert.Equal(managed.Weights[i], native.Weights[i], 14);
            Assert.Equal(1.0, native.Weights.Sum(), 12);
        }
    }
}

public class SimdLevelTests
{
    /// <summary>Each SIMD level the CPU supports gives bit-identical covariance; AVX-512 runs only on CPUs that have it.</summary>
    [Fact]
    public void CovarianceIsBitIdenticalAcrossSimdLevels()
    {
        Assert.True(RustAccelerator.IsAvailable, RustAccelerator.LoadError);
        var rng = new FastRandom(2);
        int rows = 300, cols = 37;
        var data = Enumerable.Range(0, rows * cols).Select(_ => rng.NextGaussian()).ToArray();
        var before = RustAccelerator.SimdLevel;
        try
        {
            RustAccelerator.SimdLevel = NativeSimdLevel.Scalar;
            Assert.Equal(NativeSimdLevel.Scalar, RustAccelerator.SimdLevel);
            var reference = FastRisk.Covariance(data, rows, cols);
            foreach (var level in new[] { NativeSimdLevel.Avx2, NativeSimdLevel.Avx512 })
            {
                RustAccelerator.SimdLevel = level;
                if (RustAccelerator.SimdLevel != level) continue;               // CPU lacks it
                var v = FastRisk.Covariance(data, rows, cols);
                Assert.True(reference.Zip(v).All(p => BitConverter.DoubleToInt64Bits(p.First) == BitConverter.DoubleToInt64Bits(p.Second)), $"{level} differs");
            }
        }
        finally { RustAccelerator.SimdLevel = before; }
        Assert.Equal(RustAccelerator.SupportedSimdLevel, RustAccelerator.SimdLevel);
    }
}
