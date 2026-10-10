using ILGPU.Algorithms;

namespace Finance.GPU;

/// <summary>
/// Kernel-safe math: no allocations, no managed objects, only XMath intrinsics. The same code
/// runs on the host, which is how the CPU reference implementations reproduce GPU results.
/// </summary>
public static class GpuMath
{
    private const ulong Golden = 0x9E3779B97F4A7C15UL;
    private const double TwoPi = 6.283185307179586476925;

    /// <summary>SplitMix64 finaliser: a stateless 64-bit hash.</summary>
    public static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>
    /// Counter-based uniform in (0, 1]: the value for (<paramref name="seed"/>, <paramref name="counter"/>)
    /// is fixed, so any thread can generate any draw without shared state.
    /// </summary>
    public static double Uniform(ulong seed, ulong counter)
        => ((Mix(seed + (counter + 1) * Golden) >> 11) + 1) * (1.0 / 9007199254740992.0);

    /// <summary>Standard normal draw number <paramref name="counter"/> (Box-Muller, cosine branch).</summary>
    public static double Gaussian(ulong seed, ulong counter)
    {
        double u1 = Uniform(seed, 2 * counter);
        double u2 = Uniform(seed, 2 * counter + 1);
        return XMath.Sqrt(-2.0 * XMath.Log(u1)) * XMath.Cos(TwoPi * u2);
    }

    /// <summary>Standard normal CDF (Cody's erfc, same constants as Finance.Mathematics.Normal).</summary>
    public static double NormCdf(double x) => 0.5 * Erfc(-x * 0.70710678118654752440);

    /// <summary>Standard normal density.</summary>
    public static double NormPdf(double x) => 0.39894228040143267794 * XMath.Exp(-0.5 * x * x);

    /// <summary>Complementary error function.</summary>
    public static double Erfc(double x)
    {
        double ax = x < 0 ? -x : x;
        double result;
        if (ax < 0.5)
        {
            double z = x * x;
            double num = (((1.857777061846031527e-1 * z + 3.161123743870565597) * z + 1.138641541510501556e2) * z + 3.774852376853020208e2) * z + 3.209377589138469473e3;
            double den = (((z + 2.360129095234412093e1) * z + 2.440246379344441733e2) * z + 1.282616526077372276e3) * z + 2.844236833439170622e3;
            return 1.0 - x * num / den;
        }
        if (ax < 4)
        {
            double num = ((((((-1.368648573827167067e-7 * ax + 5.641955174789739711e-1) * ax + 7.211758250883093659) * ax + 4.316222722205673530e1) * ax
                + 1.529892850469404039e2) * ax + 3.393208167343436870e2) * ax + 4.519189537118729422e2) * ax + 3.004592610201616005e2;
            double den = ((((((ax + 1.278272731962942351e1) * ax + 7.700015293522947295e1) * ax + 2.775854447439876434e2) * ax + 6.389802644656311665e2) * ax
                + 9.313540948506096211e2) * ax + 7.909509253278980272e2) * ax + 3.004592609569832933e2;
            result = XMath.Exp(-ax * ax) * num / den;
        }
        else
        {
            double z = 1.0 / (ax * ax);
            double num = (((-2.23192459734184686e-2 * z - 2.78661308609647788e-1) * z - 2.26956593539686930e-1) * z - 4.94730910623250734e-2) * z - 2.99610707703542174e-3;
            double den = (((z + 1.98733201817135256) * z + 1.05167510706793207) * z + 1.91308926107829841e-1) * z + 1.06209230528467918e-2;
            result = XMath.Exp(-ax * ax) / ax * (0.56418958354775628695 + z * num / den);
        }
        return x < 0 ? 2.0 - result : result;
    }
}
