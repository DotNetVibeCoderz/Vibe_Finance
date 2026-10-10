namespace Finance.Mathematics;

/// <summary>
/// The normal distribution. <see cref="Cdf(double)"/> is accurate to ~1e-15 (W. J. Cody's erfc rational
/// approximations, via <see cref="Erfc"/>); <see cref="InverseCdf(double)"/> uses Acklam's algorithm plus one
/// Halley refinement step, giving full double precision.
/// </summary>
public static class Normal
{
    private const double InvSqrt2Pi = 0.39894228040143267794;
    private const double Sqrt2 = 1.41421356237309504880;

    /// <summary>Standard normal density.</summary>
    public static double Pdf(double x) => InvSqrt2Pi * Math.Exp(-0.5 * x * x);

    /// <summary>Normal density with mean <paramref name="mu"/> and standard deviation <paramref name="sigma"/>.</summary>
    public static double Pdf(double x, double mu, double sigma) => Pdf((x - mu) / sigma) / sigma;

    /// <summary>Standard normal cumulative distribution.</summary>
    public static double Cdf(double x) => 0.5 * Erfc(-x / Sqrt2);

    public static double Cdf(double x, double mu, double sigma) => Cdf((x - mu) / sigma);

    /// <summary>Standard normal quantile function.</summary>
    public static double InverseCdf(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;

        // Acklam's rational approximation.
        const double a1 = -3.969683028665376e+01, a2 = 2.209460984245205e+02, a3 = -2.759285104469687e+02;
        const double a4 = 1.383577518672690e+02, a5 = -3.066479806614716e+01, a6 = 2.506628277459239e+00;
        const double b1 = -5.447609879822406e+01, b2 = 1.615858368580409e+02, b3 = -1.556989798598866e+02;
        const double b4 = 6.680131188771972e+01, b5 = -1.328068155288572e+01;
        const double c1 = -7.784894002430293e-03, c2 = -3.223964580411365e-01, c3 = -2.400758277161838e+00;
        const double c4 = -2.549732539343734e+00, c5 = 4.374664141464968e+00, c6 = 2.938163982698783e+00;
        const double d1 = 7.784695709041462e-03, d2 = 3.224671290700398e-01, d3 = 2.445134137142996e+00;
        const double d4 = 3.754408661907416e+00;
        const double low = 0.02425, high = 1 - low;

        double x;
        if (p < low)
        {
            double q = Math.Sqrt(-2 * Math.Log(p));
            x = (((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) / ((((d1 * q + d2) * q + d3) * q + d4) * q + 1);
        }
        else if (p <= high)
        {
            double q = p - 0.5, r = q * q;
            x = (((((a1 * r + a2) * r + a3) * r + a4) * r + a5) * r + a6) * q / (((((b1 * r + b2) * r + b3) * r + b4) * r + b5) * r + 1);
        }
        else
        {
            double q = Math.Sqrt(-2 * Math.Log(1 - p));
            x = -(((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) / ((((d1 * q + d2) * q + d3) * q + d4) * q + 1);
        }

        // One Halley step.
        double e = Cdf(x) - p;
        double u = e * Math.Sqrt(2 * Math.PI) * Math.Exp(x * x / 2);
        return x - u / (1 + x * u / 2);
    }

    public static double InverseCdf(double p, double mu, double sigma) => mu + sigma * InverseCdf(p);

    /// <summary>Complementary error function (Cody 1969), relative error below 1e-15.</summary>
    public static double Erfc(double x)
    {
        double ax = Math.Abs(x);
        double result;
        if (ax < 0.5)
        {
            return 1 - Erf(x);
        }
        if (ax < 4)
        {
            const double p0 = 3.004592610201616005e2, p1 = 4.519189537118729422e2, p2 = 3.393208167343436870e2;
            const double p3 = 1.529892850469404039e2, p4 = 4.316222722205673530e1, p5 = 7.211758250883093659;
            const double p6 = 5.641955174789739711e-1, p7 = -1.368648573827167067e-7;
            const double q0 = 3.004592609569832933e2, q1 = 7.909509253278980272e2, q2 = 9.313540948506096211e2;
            const double q3 = 6.389802644656311665e2, q4 = 2.775854447439876434e2, q5 = 7.700015293522947295e1;
            const double q6 = 1.278272731962942351e1;
            double num = ((((((p7 * ax + p6) * ax + p5) * ax + p4) * ax + p3) * ax + p2) * ax + p1) * ax + p0;
            double den = ((((((ax + q6) * ax + q5) * ax + q4) * ax + q3) * ax + q2) * ax + q1) * ax + q0;
            result = Math.Exp(-ax * ax) * num / den;
        }
        else
        {
            const double p0 = -2.99610707703542174e-3, p1 = -4.94730910623250734e-2, p2 = -2.26956593539686930e-1;
            const double p3 = -2.78661308609647788e-1, p4 = -2.23192459734184686e-2;
            const double q0 = 1.06209230528467918e-2, q1 = 1.91308926107829841e-1, q2 = 1.05167510706793207;
            const double q3 = 1.98733201817135256;
            double z = 1 / (ax * ax);
            double num = (((p4 * z + p3) * z + p2) * z + p1) * z + p0;
            double den = (((z + q3) * z + q2) * z + q1) * z + q0;
            result = Math.Exp(-ax * ax) / ax * (0.56418958354775628695 + z * num / den);
        }
        return x < 0 ? 2 - result : result;
    }

    /// <summary>Error function.</summary>
    public static double Erf(double x)
    {
        double ax = Math.Abs(x);
        if (ax >= 0.5) return 1 - Erfc(x);
        const double p0 = 3.209377589138469473e3, p1 = 3.774852376853020208e2, p2 = 1.138641541510501556e2;
        const double p3 = 3.161123743870565597, p4 = 1.857777061846031527e-1;
        const double q0 = 2.844236833439170622e3, q1 = 1.282616526077372276e3, q2 = 2.440246379344441733e2;
        const double q3 = 2.360129095234412093e1;
        double z = x * x;
        double num = (((p4 * z + p3) * z + p2) * z + p1) * z + p0;
        double den = (((z + q3) * z + q2) * z + q1) * z + q0;
        return x * num / den;
    }
}

/// <summary>Student's t distribution (used by parametric VaR with fat tails and t-statistics).</summary>
public static class StudentT
{
    /// <summary>Density with <paramref name="nu"/> degrees of freedom.</summary>
    public static double Pdf(double x, double nu)
    {
        double logC = SpecialFunctions.LogGamma((nu + 1) / 2) - SpecialFunctions.LogGamma(nu / 2) - 0.5 * Math.Log(nu * Math.PI);
        return Math.Exp(logC - (nu + 1) / 2 * Math.Log(1 + x * x / nu));
    }

    /// <summary>Cumulative distribution via the regularised incomplete beta function.</summary>
    public static double Cdf(double x, double nu)
    {
        double t = nu / (nu + x * x);
        double tail = 0.5 * SpecialFunctions.RegularizedIncompleteBeta(nu / 2, 0.5, t);
        return x >= 0 ? 1 - tail : tail;
    }

    /// <summary>Quantile by bracketed Brent root finding on <see cref="Cdf"/>.</summary>
    public static double InverseCdf(double p, double nu)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        double guess = Normal.InverseCdf(p);
        double lo = guess - 10, hi = guess + 10;
        while (Cdf(lo, nu) > p) lo *= 2;
        while (Cdf(hi, nu) < p) hi *= 2;
        return RootFinding.Brent(x => Cdf(x, nu) - p, lo, hi, 1e-12);
    }
}

/// <summary>Gamma and beta special functions.</summary>
public static class SpecialFunctions
{
    private static readonly double[] Lanczos =
    {
        0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
        -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6,
        1.5056327351493116e-7,
    };

    /// <summary>log Γ(x) by the Lanczos approximation (g = 7).</summary>
    public static double LogGamma(double x)
    {
        if (x < 0.5) return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
        x -= 1;
        double a = Lanczos[0];
        double t = x + 7.5;
        for (int i = 1; i < 9; i++) a += Lanczos[i] / (x + i);
        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
    }

    public static double Gamma(double x) => Math.Exp(LogGamma(x));

    /// <summary>Regularised incomplete beta I_x(a, b) by Lentz's continued fraction.</summary>
    public static double RegularizedIncompleteBeta(double a, double b, double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double lnBeta = LogGamma(a + b) - LogGamma(a) - LogGamma(b);
        double front = Math.Exp(Math.Log(x) * a + Math.Log(1 - x) * b + lnBeta);
        if (x < (a + 1) / (a + b + 2)) return front * BetaContinuedFraction(a, b, x) / a;
        return 1 - front * BetaContinuedFraction(b, a, 1 - x) / b;
    }

    private static double BetaContinuedFraction(double a, double b, double x)
    {
        const double tiny = 1e-300;
        double f = 1, c = 1, d = 0;
        for (int i = 0; i <= 300; i++)
        {
            int m = i / 2;
            double num;
            if (i == 0) num = 1;
            else if (i % 2 == 0) num = m * (b - m) * x / ((a + 2 * m - 1) * (a + 2 * m));
            else num = -((a + m) * (a + b + m) * x) / ((a + 2 * m) * (a + 2 * m + 1));
            d = 1 + num * d;
            if (Math.Abs(d) < tiny) d = tiny;
            d = 1 / d;
            c = 1 + num / c;
            if (Math.Abs(c) < tiny) c = tiny;
            double cd = c * d;
            f *= cd;
            if (Math.Abs(1 - cd) < 1e-15) return f - 1;
        }
        return f - 1;
    }
}
