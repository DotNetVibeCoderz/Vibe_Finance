using System.Runtime.CompilerServices;

namespace Finance.Core;

/// <summary>
/// xoshiro256** generator with a Marsaglia polar normal sampler. Seeded runs are reproducible
/// across platforms, which <see cref="Random"/> does not promise. Not thread-safe: give each
/// thread its own instance (see <see cref="Split"/>).
/// </summary>
public sealed class FastRandom
{
    private ulong _s0, _s1, _s2, _s3;
    private double _spare;
    private bool _hasSpare;

    public FastRandom(ulong seed = 0x9E3779B97F4A7C15UL)
    {
        // SplitMix64 expansion so that nearby seeds give unrelated streams.
        _s0 = SplitMix(ref seed);
        _s1 = SplitMix(ref seed);
        _s2 = SplitMix(ref seed);
        _s3 = SplitMix(ref seed);
    }

    /// <summary>A generator seeded from the clock.</summary>
    public static FastRandom Create() => new((ulong)Environment.TickCount64 ^ (ulong)DateTime.UtcNow.Ticks);

    /// <summary>Uniform 64-bit integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong NextUInt64()
    {
        ulong result = BitRotate(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitRotate(_s3, 45);
        return result;
    }

    /// <summary>Uniform in [0, 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform in [min, max).</summary>
    public double NextDouble(double min, double max) => min + (max - min) * NextDouble();

    /// <summary>Uniform integer in [0, maxExclusive).</summary>
    public int NextInt(int maxExclusive) => (int)((NextUInt64() >> 33) % (ulong)maxExclusive);

    /// <summary>Standard normal sample.</summary>
    public double NextGaussian()
    {
        if (_hasSpare)
        {
            _hasSpare = false;
            return _spare;
        }
        double u, v, s;
        do
        {
            u = 2.0 * NextDouble() - 1.0;
            v = 2.0 * NextDouble() - 1.0;
            s = u * u + v * v;
        } while (s >= 1.0 || s == 0.0);
        double m = System.Math.Sqrt(-2.0 * System.Math.Log(s) / s);
        _spare = v * m;
        _hasSpare = true;
        return u * m;
    }

    /// <summary>Normal sample with the given mean and standard deviation.</summary>
    public double NextGaussian(double mean, double stdDev) => mean + stdDev * NextGaussian();

    /// <summary>Fills a span with standard normal samples.</summary>
    public void FillGaussian(Span<double> destination)
    {
        for (int i = 0; i < destination.Length; i++) destination[i] = NextGaussian();
    }

    /// <summary>An independent generator derived from this one (for per-thread streams).</summary>
    public FastRandom Split() => new(NextUInt64());

    private static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong BitRotate(ulong x, int k) => (x << k) | (x >> (64 - k));
}
