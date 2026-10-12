// Finance.Net GPU kernels for AMD ROCm/HIP, compiled at run time with hiprtc.
// Ports of Finance.GPU's ILGPU kernels with the same counter-based RNG (SplitMix64 + Box-Muller).
// Device math (exp, log, sqrt, cos) is the HIP double-precision library.

typedef unsigned long long u64;
typedef unsigned int u32;

__device__ inline u64 mix64(u64 z)
{
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
    return z ^ (z >> 31);
}

__device__ inline double uniform01(u64 seed, u64 counter)
{
    return (double)((mix64(seed + (counter + 1ULL) * 0x9E3779B97F4A7C15ULL) >> 11) + 1ULL) * (1.0 / 9007199254740992.0);
}

__device__ inline double gaussian(u64 seed, u64 counter)
{
    double u1 = uniform01(seed, 2ULL * counter);
    double u2 = uniform01(seed, 2ULL * counter + 1ULL);
    return sqrt(-2.0 * log(u1)) * cos(6.283185307179586476925 * u2);
}

__device__ double erfc64(double x)
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
    if (ax < 4.0)
    {
        double num = ((((((-1.368648573827167067e-7 * ax + 5.641955174789739711e-1) * ax + 7.211758250883093659) * ax + 4.316222722205673530e1) * ax
            + 1.529892850469404039e2) * ax + 3.393208167343436870e2) * ax + 4.519189537118729422e2) * ax + 3.004592610201616005e2;
        double den = ((((((ax + 1.278272731962942351e1) * ax + 7.700015293522947295e1) * ax + 2.775854447439876434e2) * ax + 6.389802644656311665e2) * ax
            + 9.313540948506096211e2) * ax + 7.909509253278980272e2) * ax + 3.004592609569832933e2;
        result = exp(-ax * ax) * num / den;
    }
    else
    {
        double z = 1.0 / (ax * ax);
        double num = (((-2.23192459734184686e-2 * z - 2.78661308609647788e-1) * z - 2.26956593539686930e-1) * z - 4.94730910623250734e-2) * z - 2.99610707703542174e-3;
        double den = (((z + 1.98733201817135256) * z + 1.05167510706793207) * z + 1.91308926107829841e-1) * z + 1.06209230528467918e-2;
        result = exp(-ax * ax) / ax * (0.56418958354775628695 + z * num / den);
    }
    return x < 0 ? 2.0 - result : result;
}

__device__ inline double norm_cdf(double x) { return 0.5 * erfc64(-x * 0.70710678118654752440); }
__device__ inline double norm_pdf(double x) { return 0.39894228040143267794 * exp(-0.5 * x * x); }
__device__ inline double payoff(double s, double k, bool call) { double v = call ? s - k : k - s; return v > 0 ? v : 0; }
__device__ inline u32 tid() { return blockIdx.x * blockDim.x + threadIdx.x + blockIdx.y * gridDim.x * blockDim.x; }

// fp: spot, strike, time, rate, dividend, vol. ip: isCall, steps, pptLo, pptHi, totalLo, totalHi, seedLo, seedHi, threads.
extern "C" __global__ void mc_european(const double* fp, const u32* ip, const double* inp, double* outp)
{
    u32 t = tid(), threads = ip[8];
    if (t >= threads) return;
    double spot = fp[0], strike = fp[1], time = fp[2], rate = fp[3], dividend = fp[4], vol = fp[5];
    bool call = ip[0] != 0;
    u64 ppt = ((u64)ip[3] << 32) | ip[2], total = ((u64)ip[5] << 32) | ip[4], seed = ((u64)ip[7] << 32) | ip[6];
    u64 start = (u64)t * ppt, end = start + ppt;
    if (end > total) end = total;
    double drift = (rate - dividend - 0.5 * vol * vol) * time, diff = vol * sqrt(time);
    double s1 = 0, s2 = 0;
    for (u64 p = start; p < end; p++)
    {
        double z = gaussian(seed, p);
        double v = 0.5 * (payoff(spot * exp(drift + diff * z), strike, call) + payoff(spot * exp(drift - diff * z), strike, call));
        s1 += v;
        s2 += v * v;
    }
    outp[t] = s1;
    outp[threads + t] = s2;
}

extern "C" __global__ void mc_asian(const double* fp, const u32* ip, const double* inp, double* outp)
{
    u32 t = tid(), threads = ip[8];
    if (t >= threads) return;
    double spot = fp[0], strike = fp[1], time = fp[2], rate = fp[3], dividend = fp[4], vol = fp[5];
    bool call = ip[0] != 0;
    u32 steps = ip[1];
    u64 ppt = ((u64)ip[3] << 32) | ip[2], total = ((u64)ip[5] << 32) | ip[4], seed = ((u64)ip[7] << 32) | ip[6];
    u64 start = (u64)t * ppt, end = start + ppt;
    if (end > total) end = total;
    double dt = time / steps, drift = (rate - dividend - 0.5 * vol * vol) * dt, diff = vol * sqrt(dt);
    double s1 = 0, s2 = 0;
    for (u64 p = start; p < end; p++)
    {
        double up = spot, dn = spot, sumUp = 0, sumDn = 0;
        u64 base = p * steps;
        for (u32 j = 0; j < steps; j++)
        {
            double z = gaussian(seed, base + j);
            up *= exp(drift + diff * z);
            dn *= exp(drift - diff * z);
            sumUp += up;
            sumDn += dn;
        }
        double v = 0.5 * (payoff(sumUp / steps, strike, call) + payoff(sumDn / steps, strike, call));
        s1 += v;
        s2 += v * v;
    }
    outp[t] = s1;
    outp[threads + t] = s2;
}

// inp: 7 planes of n; outp: 6 planes. ip[0] = n.
extern "C" __global__ void black_scholes(const double* fp, const u32* ip, const double* inp, double* outp)
{
    u32 i = tid(), n = ip[0];
    if (i >= n) return;
    double s = inp[i], k = inp[n + i], t = inp[2 * n + i], r = inp[3 * n + i], q = inp[4 * n + i], vol = inp[5 * n + i];
    bool call = inp[6 * n + i] != 0;
    double price = 0, delta = 0, gamma = 0, vega = 0, theta = 0, rho = 0;
    if (t <= 0)
    {
        double intrinsic = call ? s - k : k - s;
        price = intrinsic > 0 ? intrinsic : 0;
        delta = intrinsic > 0 ? (call ? 1.0 : -1.0) : 0;
    }
    else
    {
        double dq = exp(-q * t), dr = exp(-r * t);
        if (vol <= 0)
        {
            double fwd = s * dq - k * dr;
            if (call) { if (fwd > 0) { price = fwd; delta = dq; } }
            else if (fwd < 0) { price = -fwd; delta = -dq; }
        }
        else
        {
            double sqrtT = sqrt(t), vs = vol * sqrtT;
            double d1 = (log(s / k) + (r - q + 0.5 * vol * vol) * t) / vs, d2 = d1 - vs;
            double nd1 = norm_pdf(d1);
            gamma = dq * nd1 / (s * vs);
            vega = s * dq * nd1 * sqrtT;
            double decay = -s * dq * nd1 * vol / (2 * sqrtT);
            if (call)
            {
                double c1 = norm_cdf(d1), c2 = norm_cdf(d2);
                price = s * dq * c1 - k * dr * c2; delta = dq * c1;
                theta = decay - r * k * dr * c2 + q * s * dq * c1; rho = k * t * dr * c2;
            }
            else
            {
                double c1 = norm_cdf(-d1), c2 = norm_cdf(-d2);
                price = k * dr * c2 - s * dq * c1; delta = -dq * c1;
                theta = decay + r * k * dr * c2 - q * s * dq * c1; rho = -k * t * dr * c2;
            }
        }
    }
    outp[i] = price; outp[n + i] = delta; outp[2 * n + i] = gamma;
    outp[3 * n + i] = vega; outp[4 * n + i] = theta; outp[5 * n + i] = rho;
}

// inp: chol (assets²), weights, drifts. fp[0] = sqrt(horizon). ip: assets, scenarios, seedLo, seedHi.
extern "C" __global__ void portfolio_return(const double* fp, const u32* ip, const double* inp, double* outp)
{
    u32 sc = tid(), n = ip[0];
    if (sc >= ip[1]) return;
    u64 seed = ((u64)ip[3] << 32) | ip[2];
    u64 base = (u64)sc * n;
    double total = 0;
    for (u32 asset = 0; asset < n; asset++)
    {
        double shock = 0;
        for (u32 k = 0; k <= asset; k++) shock += inp[asset * n + k] * gaussian(seed, base + k);
        total += inp[n * n + asset] * (exp(inp[n * n + n + asset] + fp[0] * shock) - 1.0);
    }
    outp[sc] = total;
}
