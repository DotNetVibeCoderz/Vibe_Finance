using ILGPU;
using ILGPU.Algorithms;

namespace Finance.GPU;

/// <summary>Monte Carlo option arguments passed by value to kernels.</summary>
internal struct McArgs
{
    public double Spot, Strike, Time, Rate, Dividend, Vol;
    public int IsCall, Steps;
    public long PathsPerThread, TotalPaths;
    public ulong Seed;
}

/// <summary>Portfolio VaR arguments passed by value to kernels.</summary>
internal struct VarArgs
{
    public int Assets;
    public double SqrtHorizon;
    public ulong Seed;
}

/// <summary>
/// ILGPU kernels. Each kernel is a thin wrapper over a scalar "body" so the host can run the
/// identical computation (see the CPU reference paths in <see cref="GpuOptionPricer"/> and
/// <see cref="GpuRiskEngine"/>).
/// </summary>
internal static class GpuKernels
{
    // ---------- European Monte Carlo (antithetic pairs) ----------

    public static void European(Index1D i, ArrayView<double> sums, ArrayView<double> sumsq, McArgs a)
    {
        EuropeanBody(i, a, out double s1, out double s2);
        sums[i] = s1;
        sumsq[i] = s2;
    }

    public static void EuropeanBody(long thread, McArgs a, out double s1, out double s2)
    {
        long start = thread * a.PathsPerThread;
        long end = start + a.PathsPerThread;
        if (end > a.TotalPaths) end = a.TotalPaths;
        double drift = (a.Rate - a.Dividend - 0.5 * a.Vol * a.Vol) * a.Time;
        double diff = a.Vol * XMath.Sqrt(a.Time);
        s1 = 0;
        s2 = 0;
        for (long p = start; p < end; p++)
        {
            double z = GpuMath.Gaussian(a.Seed, (ulong)p);
            double up = a.Spot * XMath.Exp(drift + diff * z);
            double dn = a.Spot * XMath.Exp(drift - diff * z);
            double v = 0.5 * (Payoff(up, a) + Payoff(dn, a));
            s1 += v;
            s2 += v * v;
        }
    }

    private static double Payoff(double s, McArgs a)
    {
        double v = a.IsCall != 0 ? s - a.Strike : a.Strike - s;
        return v > 0 ? v : 0;
    }

    // ---------- Arithmetic Asian Monte Carlo (antithetic pairs) ----------

    public static void Asian(Index1D i, ArrayView<double> sums, ArrayView<double> sumsq, McArgs a)
    {
        AsianBody(i, a, out double s1, out double s2);
        sums[i] = s1;
        sumsq[i] = s2;
    }

    public static void AsianBody(long thread, McArgs a, out double s1, out double s2)
    {
        long start = thread * a.PathsPerThread;
        long end = start + a.PathsPerThread;
        if (end > a.TotalPaths) end = a.TotalPaths;
        double dt = a.Time / a.Steps;
        double drift = (a.Rate - a.Dividend - 0.5 * a.Vol * a.Vol) * dt;
        double diff = a.Vol * XMath.Sqrt(dt);
        s1 = 0;
        s2 = 0;
        for (long p = start; p < end; p++)
        {
            double up = a.Spot, dn = a.Spot, sumUp = 0, sumDn = 0;
            ulong baseCounter = (ulong)p * (ulong)a.Steps;
            for (int j = 0; j < a.Steps; j++)
            {
                double z = GpuMath.Gaussian(a.Seed, baseCounter + (ulong)j);
                up *= XMath.Exp(drift + diff * z);
                dn *= XMath.Exp(drift - diff * z);
                sumUp += up;
                sumDn += dn;
            }
            double v = 0.5 * (Payoff(sumUp / a.Steps, a) + Payoff(sumDn / a.Steps, a));
            s1 += v;
            s2 += v * v;
        }
    }

    // ---------- Batch Black-Scholes ----------
    // Input: 7 planes of n values (spot, strike, time, rate, dividend, vol, isCall as 0/1).
    // Output: 6 planes (price, delta, gamma, vega, theta, rho).

    public static void BlackScholes(Index1D i, ArrayView<double> input, ArrayView<double> output, int n)
    {
        BlackScholesBody(input[i], input[n + i], input[2 * n + i], input[3 * n + i], input[4 * n + i], input[5 * n + i], input[6 * n + i] != 0,
            out double price, out double delta, out double gamma, out double vega, out double theta, out double rho);
        output[i] = price;
        output[n + i] = delta;
        output[2 * n + i] = gamma;
        output[3 * n + i] = vega;
        output[4 * n + i] = theta;
        output[5 * n + i] = rho;
    }

    public static void BlackScholesBody(double s, double k, double t, double r, double q, double vol, bool call,
        out double price, out double delta, out double gamma, out double vega, out double theta, out double rho)
    {
        gamma = vega = theta = rho = 0;
        if (t <= 0)
        {
            double intrinsic = call ? s - k : k - s;
            price = intrinsic > 0 ? intrinsic : 0;
            delta = intrinsic > 0 ? (call ? 1 : -1) : 0;
            return;
        }
        double dq = XMath.Exp(-q * t), dr = XMath.Exp(-r * t);
        if (vol <= 0)
        {
            double fwd = s * dq - k * dr;
            if (call) { price = fwd > 0 ? fwd : 0; delta = fwd > 0 ? dq : 0; }
            else { price = fwd < 0 ? -fwd : 0; delta = fwd < 0 ? -dq : 0; }
            return;
        }
        double sqrtT = XMath.Sqrt(t), vs = vol * sqrtT;
        double d1 = (XMath.Log(s / k) + (r - q + 0.5 * vol * vol) * t) / vs, d2 = d1 - vs;
        double nd1 = GpuMath.NormPdf(d1);
        gamma = dq * nd1 / (s * vs);
        vega = s * dq * nd1 * sqrtT;
        double decay = -s * dq * nd1 * vol / (2 * sqrtT);
        if (call)
        {
            double c1 = GpuMath.NormCdf(d1), c2 = GpuMath.NormCdf(d2);
            price = s * dq * c1 - k * dr * c2;
            delta = dq * c1;
            theta = decay - r * k * dr * c2 + q * s * dq * c1;
            rho = k * t * dr * c2;
        }
        else
        {
            double c1 = GpuMath.NormCdf(-d1), c2 = GpuMath.NormCdf(-d2);
            price = k * dr * c2 - s * dq * c1;
            delta = -dq * c1;
            theta = decay + r * k * dr * c2 - q * s * dq * c1;
            rho = -k * t * dr * c2;
        }
    }

    // ---------- Monte Carlo portfolio VaR ----------
    // Asset i's horizon return is exp(drift_i + sqrt(h) * (L z)_i) - 1 with L the Cholesky factor of
    // the covariance. z_k is regenerated from the counter-based RNG wherever it is needed, so no
    // per-thread scratch array is required (cost O(n^2) per scenario).

    public static void PortfolioReturn(Index1D i, ArrayView<double> chol, ArrayView<double> weights, ArrayView<double> drifts, ArrayView<double> returns, VarArgs a)
    {
        double total = 0;
        int n = a.Assets;
        ulong baseCounter = (ulong)i.X * (ulong)n;
        for (int asset = 0; asset < n; asset++)
        {
            double shock = 0;
            for (int k = 0; k <= asset; k++)
                shock += chol[asset * n + k] * GpuMath.Gaussian(a.Seed, baseCounter + (ulong)k);
            total += weights[asset] * (XMath.Exp(drifts[asset] + a.SqrtHorizon * shock) - 1.0);
        }
        returns[i] = total;
    }

    /// <summary>Host version of <see cref="PortfolioReturn"/> for one scenario.</summary>
    public static double PortfolioReturnBody(long scenario, double[] chol, double[] weights, double[] drifts, VarArgs a)
    {
        double total = 0;
        int n = a.Assets;
        ulong baseCounter = (ulong)scenario * (ulong)n;
        for (int asset = 0; asset < n; asset++)
        {
            double shock = 0;
            for (int k = 0; k <= asset; k++)
                shock += chol[asset * n + k] * GpuMath.Gaussian(a.Seed, baseCounter + (ulong)k);
            total += weights[asset] * (XMath.Exp(drifts[asset] + a.SqrtHorizon * shock) - 1.0);
        }
        return total;
    }
}
