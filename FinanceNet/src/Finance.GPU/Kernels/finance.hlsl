// Finance.Net GPU kernels for Direct3D 12 (HLSL, shader model 6.0 DXIL, precompiled by DXC: run compile-dxil.ps1
// after editing; Finance.Native.Tests fails while the embedded DXIL is stale). Ports of Finance.GPU's ILGPU kernels
// with the same counter-based RNG.
//
// Portability rules (the kernels must run on any Direct3D 12 GPU that reports double support, and on WARP):
//   * only base double instructions (add, mul, compare, select, float<->double conversion) are relied on: double
//     division, reciprocal, fma and int<->double conversion are optional "extended doubles" features, so division
//     is a float reciprocal seed refined by Newton steps (ddiv_), int<->double goes through float (exact for
//     |v| < 2^24) and uint->double uses the 2^52 bit trick;
//   * no 64-bit integers (an optional feature too): they are uint2 (x = low word, y = high word);
//   * no double sqrt/exp/log/cos in HLSL: sqrt is a float seed plus Newton steps, exp/log/cos are range reduction
//     plus polynomials (relative error ~1e-16);
//   * every floating literal carries the L suffix (unsuffixed HLSL literals are float and silently cost 7+ digits);
//   * helpers are branch-free and kernels avoid divergent if/else over doubles. (The first version, compiled to
//     shader model 5.1 by FXC, executed double selects and stores wrongly on both an Intel driver and WARP; the DXIL
//     path does not, but the branch-free form is kept.)
// Warnings are errors, so an accidental float intrinsic on a double fails the build.

cbuffer Grid : register(b0) { uint GridWidth; };       // threads per row of the dispatch grid
StructuredBuffer<double> fp : register(t0);
StructuredBuffer<uint> ip : register(t1);
StructuredBuffer<double> inp : register(t2);
RWStructuredBuffer<double> outp : register(u0);

static const double LN2_HI = 0.693147180369123816490L;
static const double LN2_LO = 1.90821492927058770002e-10L;
static const double INV_LN2 = 1.44269504088896338700L;
static const double HALF_PI = 1.57079632679489661923L;

// ---------- 64-bit unsigned arithmetic on uint2 ----------

uint2 add64(uint2 a, uint2 b)
{
    uint lo = a.x + b.x;
    return uint2(lo, a.y + b.y + (lo < a.x ? 1u : 0u));
}

uint2 mul32(uint a, uint b)          // full 32x32 -> 64 product
{
    uint a0 = a & 0xFFFFu, a1 = a >> 16, b0 = b & 0xFFFFu, b1 = b >> 16;
    uint p00 = a0 * b0, p01 = a0 * b1, p10 = a1 * b0, p11 = a1 * b1;
    uint mid = (p00 >> 16) + (p01 & 0xFFFFu) + (p10 & 0xFFFFu);
    return uint2((mid << 16) | (p00 & 0xFFFFu), p11 + (p01 >> 16) + (p10 >> 16) + (mid >> 16));
}

uint2 mul64(uint2 a, uint2 b)        // low 64 bits of a*b
{
    uint2 r = mul32(a.x, b.x);
    r.y += a.x * b.y + a.y * b.x;
    return r;
}

uint2 shr64(uint2 a, uint s)         // 0 < s < 32
{
    return uint2((a.x >> s) | (a.y << (32u - s)), a.y >> s);
}

uint2 xor64(uint2 a, uint2 b) { return uint2(a.x ^ b.x, a.y ^ b.y); }

// ---------- double helpers ----------

double dabs(double x) { return x < 0 ? -x : x; }

double drecip(double b)                  // 1/b to ~1 ulp with base instructions only
{
    double y = (double)(1.0f / (float)b);
    y = y * (2.0L - b * y);
    y = y * (2.0L - b * y);
    return y;
}

double ddiv_(double a, double b)
{
    double y = drecip(b);
    double q = a * y;
    return q + y * (a - b * q);
}

double u32_to_double(uint v) { return asdouble(v, 0x43300000u) - 4503599627370496.0L; }   // exact: (2^52 + v) - 2^52
double small_int_to_double(int v) { return (double)(float)v; }                            // exact for |v| < 2^24
int double_to_small_int(double v) { return (int)(float)v; }                               // truncates; |v| < 2^24

// Helpers below are branch-free (selects only); see the portability rules at the top.
double dsqrt(double x)
{
    double xs = x > 0 ? x : 1.0L;
    double y = (double)rsqrt((float)xs);
    y = y * (1.5L - 0.5L * xs * y * y);
    y = y * (1.5L - 0.5L * xs * y * y);
    double r = xs * y;
    r = r + (xs - r * r) * (0.5L * y);
    return x > 0 ? r : 0.0L;
}

double pow2(int k) { return asdouble(0u, (uint)(k + 1023) << 20); }

double dexp(double x)
{
    double xc = x > 709.0L ? 709.0L : (x < -745.0L ? -745.0L : x);
    double t = xc * INV_LN2;
    int k = double_to_small_int(t + (t >= 0 ? 0.5L : -0.5L));
    double kf = small_int_to_double(k);
    double r = (xc - kf * LN2_HI) - kf * LN2_LO;
    double p = 1.0L / 6227020800.0L;
    p = p * r + 1.0L / 479001600.0L;
    p = p * r + 1.0L / 39916800.0L;
    p = p * r + 1.0L / 3628800.0L;
    p = p * r + 1.0L / 362880.0L;
    p = p * r + 1.0L / 40320.0L;
    p = p * r + 1.0L / 5040.0L;
    p = p * r + 1.0L / 720.0L;
    p = p * r + 1.0L / 120.0L;
    p = p * r + 1.0L / 24.0L;
    p = p * r + 1.0L / 6.0L;
    p = p * r + 0.5L;
    p = p * r + 1.0L;
    p = p * r + 1.0L;
    int k1 = k >> 1, k2 = k - k1;                          // two half scales keep subnormal/huge results representable
    double result = p * pow2(k1) * pow2(k2);
    result = x > 709.0L ? asdouble(0u, 0x7FF00000u) : result;
    return x < -745.0L ? 0.0L : result;
}

double dlog(double x)
{
    uint lo, hi;
    asuint(x > 0 ? x : 1.0L, lo, hi);
    int e = (int)((hi >> 20) & 0x7FFu) - 1023;
    double m = asdouble(lo, (hi & 0x000FFFFFu) | 0x3FF00000u);
    bool big = m > 1.41421356237309504880L;
    m = big ? m * 0.5L : m;
    e = big ? e + 1 : e;
    double s = ddiv_(m - 1.0L, m + 1.0L);
    double s2 = s * s;
    double t = 1.0L / 25.0L;
    t = t * s2 + 1.0L / 23.0L;
    t = t * s2 + 1.0L / 21.0L;
    t = t * s2 + 1.0L / 19.0L;
    t = t * s2 + 1.0L / 17.0L;
    t = t * s2 + 1.0L / 15.0L;
    t = t * s2 + 1.0L / 13.0L;
    t = t * s2 + 1.0L / 11.0L;
    t = t * s2 + 1.0L / 9.0L;
    t = t * s2 + 1.0L / 7.0L;
    t = t * s2 + 1.0L / 5.0L;
    t = t * s2 + 1.0L / 3.0L;
    t = t * s2 + 1.0L;
    double ef = small_int_to_double(e);
    double result = 2.0L * s * t + ef * LN2_LO + ef * LN2_HI;
    return x > 0 ? result : asdouble(0u, 0xFFF80000u);
}

double sin_poly(double a)
{
    double a2 = a * a;
    double p = -1.0L / 1307674368000.0L;
    p = p * a2 + 1.0L / 6227020800.0L;
    p = p * a2 - 1.0L / 39916800.0L;
    p = p * a2 + 1.0L / 362880.0L;
    p = p * a2 - 1.0L / 5040.0L;
    p = p * a2 + 1.0L / 120.0L;
    p = p * a2 - 1.0L / 6.0L;
    return a + a * a2 * p;
}

double cos_poly(double a)
{
    double a2 = a * a;
    double p = 1.0L / 20922789888000.0L;
    p = p * a2 - 1.0L / 87178291200.0L;
    p = p * a2 + 1.0L / 479001600.0L;
    p = p * a2 - 1.0L / 3628800.0L;
    p = p * a2 + 1.0L / 40320.0L;
    p = p * a2 - 1.0L / 720.0L;
    p = p * a2 + 1.0L / 24.0L;
    p = p * a2 - 0.5L;
    return 1.0L + a2 * p;
}

double cos_turns(double u)           // cos(2·pi·u), u in [0, 1]
{
    int q = double_to_small_int(u * 4.0L + 0.5L);
    double f = u - small_int_to_double(q) * 0.25L;
    double a = f * 4.0L * HALF_PI;
    int qi = q & 3;
    double c = cos_poly(a), sn = sin_poly(a);
    return qi == 0 ? c : (qi == 1 ? -sn : (qi == 2 ? -c : sn));
}

// ---------- counter-based RNG (identical to GpuMath) ----------

uint2 mix64(uint2 z)
{
    z = mul64(xor64(z, shr64(z, 30u)), uint2(0x1CE4E5B9u, 0xBF58476Du));
    z = mul64(xor64(z, shr64(z, 27u)), uint2(0x133111EBu, 0x94D049BBu));
    return xor64(z, shr64(z, 31u));
}

double uniform01(uint2 seed, uint2 counter)
{
    uint2 c1 = add64(counter, uint2(1u, 0u));
    uint2 m = shr64(mix64(add64(seed, mul64(c1, uint2(0x7F4A7C15u, 0x9E3779B9u)))), 11u);
    return (u32_to_double(m.y) * 4294967296.0L + u32_to_double(m.x) + 1.0L) * (1.0L / 9007199254740992.0L);
}

double gaussian(uint2 seed, uint2 counter)
{
    uint2 c2 = uint2(counter.x << 1, (counter.y << 1) | (counter.x >> 31));
    double u1 = uniform01(seed, c2);
    double u2 = uniform01(seed, add64(c2, uint2(1u, 0u)));
    return dsqrt(-2.0L * dlog(u1)) * cos_turns(u2);
}

// ---------- normal distribution (Cody erfc, same constants as GpuMath) ----------

double erfc64(double x)
{
    double ax = dabs(x);
    double z = x * x;
    double num0 = (((1.857777061846031527e-1L * z + 3.161123743870565597L) * z + 1.138641541510501556e2L) * z + 3.774852376853020208e2L) * z + 3.209377589138469473e3L;
    double den0 = (((z + 2.360129095234412093e1L) * z + 2.440246379344441733e2L) * z + 1.282616526077372276e3L) * z + 2.844236833439170622e3L;
    double small = 1.0L - x * ddiv_(num0, den0);

    double e = dexp(-ax * ax);
    double num1 = ((((((-1.368648573827167067e-7L * ax + 5.641955174789739711e-1L) * ax + 7.211758250883093659L) * ax + 4.316222722205673530e1L) * ax
        + 1.529892850469404039e2L) * ax + 3.393208167343436870e2L) * ax + 4.519189537118729422e2L) * ax + 3.004592610201616005e2L;
    double den1 = ((((((ax + 1.278272731962942351e1L) * ax + 7.700015293522947295e1L) * ax + 2.775854447439876434e2L) * ax + 6.389802644656311665e2L) * ax
        + 9.313540948506096211e2L) * ax + 7.909509253278980272e2L) * ax + 3.004592609569832933e2L;
    double mid = e * ddiv_(num1, den1);

    double axs = ax > 0.5L ? ax : 0.5L;
    double zi = drecip(axs * axs);
    double num2 = (((-2.23192459734184686e-2L * zi - 2.78661308609647788e-1L) * zi - 2.26956593539686930e-1L) * zi - 4.94730910623250734e-2L) * zi - 2.99610707703542174e-3L;
    double den2 = (((zi + 1.98733201817135256L) * zi + 1.05167510706793207L) * zi + 1.91308926107829841e-1L) * zi + 1.06209230528467918e-2L;
    double tail = ddiv_(e, axs) * (0.56418958354775628695L + zi * ddiv_(num2, den2));

    double result = ax < 4.0L ? mid : tail;
    result = x < 0 ? 2.0L - result : result;
    return ax < 0.5L ? small : result;
}

double norm_cdf(double x) { return 0.5L * erfc64(-x * 0.70710678118654752440L); }
double norm_pdf(double x) { return 0.39894228040143267794L * dexp(-0.5L * x * x); }

uint thread_index(uint3 id) { return id.x + id.y * GridWidth; }

double payoff(double s, double strike, bool call)
{
    double v = call ? s - strike : strike - s;
    return v > 0 ? v : 0.0L;
}

// ---------- Monte Carlo (European and arithmetic Asian, antithetic pairs) ----------
// fp: spot, strike, time, rate, dividend, vol.  ip: isCall, steps, pptLo, pptHi, totalLo, totalHi, seedLo, seedHi, threads.

bool less64(uint2 a, uint2 b) { return a.y < b.y || (a.y == b.y && a.x < b.x); }

[numthreads(64, 1, 1)]
void mc_european(uint3 id : SV_DispatchThreadID)
{
    uint t = thread_index(id);
    uint threads = ip[8];
    if (t >= threads) return;
    double spot = fp[0], strike = fp[1], time = fp[2], rate = fp[3], dividend = fp[4], vol = fp[5];
    bool call = ip[0] != 0;
    uint2 ppt = uint2(ip[2], ip[3]), total = uint2(ip[4], ip[5]), seed = uint2(ip[6], ip[7]);
    uint2 p = mul64(uint2(t, 0u), ppt);
    uint2 end = add64(p, ppt);
    if (less64(total, end)) end = total;
    double drift = (rate - dividend - 0.5L * vol * vol) * time;
    double diff = vol * dsqrt(time);
    double s1 = 0, s2 = 0;
    [loop] while (less64(p, end))
    {
        double z = gaussian(seed, p);
        double up = spot * dexp(drift + diff * z);
        double dn = spot * dexp(drift - diff * z);
        double v = 0.5L * (payoff(up, strike, call) + payoff(dn, strike, call));
        s1 += v;
        s2 += v * v;
        p = add64(p, uint2(1u, 0u));
    }
    outp[t] = s1;
    outp[threads + t] = s2;
}

[numthreads(64, 1, 1)]
void mc_asian(uint3 id : SV_DispatchThreadID)
{
    uint t = thread_index(id);
    uint threads = ip[8];
    if (t >= threads) return;
    double spot = fp[0], strike = fp[1], time = fp[2], rate = fp[3], dividend = fp[4], vol = fp[5];
    bool call = ip[0] != 0;
    uint steps = ip[1];
    uint2 ppt = uint2(ip[2], ip[3]), total = uint2(ip[4], ip[5]), seed = uint2(ip[6], ip[7]);
    uint2 p = mul64(uint2(t, 0u), ppt);
    uint2 end = add64(p, ppt);
    if (less64(total, end)) end = total;
    double dt = ddiv_(time, small_int_to_double((int)steps));
    double drift = (rate - dividend - 0.5L * vol * vol) * dt;
    double diff = vol * dsqrt(dt);
    double s1 = 0, s2 = 0;
    [loop] while (less64(p, end))
    {
        double up = spot, dn = spot, sumUp = 0, sumDn = 0;
        uint2 baseCounter = mul64(p, uint2(steps, 0u));
        [loop] for (uint j = 0; j < steps; j++)
        {
            double z = gaussian(seed, add64(baseCounter, uint2(j, 0u)));
            up *= dexp(drift + diff * z);
            dn *= dexp(drift - diff * z);
            sumUp += up;
            sumDn += dn;
        }
        double inv = drecip(small_int_to_double((int)steps));
        double v = 0.5L * (payoff(sumUp * inv, strike, call) + payoff(sumDn * inv, strike, call));
        s1 += v;
        s2 += v * v;
        p = add64(p, uint2(1u, 0u));
    }
    outp[t] = s1;
    outp[threads + t] = s2;
}

// ---------- batch Black-Scholes ----------
// inp: 7 planes of n (spot, strike, time, rate, dividend, vol, isCall); outp: 6 planes. ip[0] = n.

[numthreads(64, 1, 1)]
void black_scholes(uint3 id : SV_DispatchThreadID)
{
    uint i = thread_index(id);
    uint n = ip[0];
    if (i >= n) return;
    double s = inp[i], k = inp[n + i], t = inp[2 * n + i], r = inp[3 * n + i], q = inp[4 * n + i], vol = inp[5 * n + i];
    bool call = inp[6 * n + i] != 0;
    // Branch-free: calls and puts share one formula with sign = ±1 (exact); the expired and zero-volatility cases are
    // chosen with selects at the end.
    double sign = call ? 1.0L : -1.0L;
    double tt = t > 0 ? t : 1.0L, vv = vol > 0 ? vol : 1.0L;
    double dq = dexp(-q * tt), dr = dexp(-r * tt);
    double sqrtT = dsqrt(tt), vs = vv * sqrtT;
    double d1 = ddiv_(dlog(ddiv_(s, k)) + (r - q + 0.5L * vv * vv) * tt, vs), d2 = d1 - vs;
    double nd1 = norm_pdf(d1);
    double c1 = 0.5L * erfc64(-(sign * d1) * 0.70710678118654752440L);
    double c2 = 0.5L * erfc64(-(sign * d2) * 0.70710678118654752440L);
    double decay = ddiv_(-s * dq * nd1 * vv, 2.0L * sqrtT);
    double price = sign * (s * dq * c1 - k * dr * c2);
    double delta = sign * dq * c1;
    double gamma = ddiv_(dq * nd1, s * vs);
    double vega = s * dq * nd1 * sqrtT;
    double theta = decay - sign * r * k * dr * c2 + sign * q * s * dq * c1;
    double rho = sign * k * tt * dr * c2;

    double fwd = sign * (s * dq - k * dr);                    // zero volatility: discounted intrinsic on the forward
    bool zeroVol = vol <= 0;
    price = zeroVol ? (fwd > 0 ? fwd : 0.0L) : price;
    delta = zeroVol ? (fwd > 0 ? sign * dq : 0.0L) : delta;
    double intrinsic = sign * (s - k);                         // expired: intrinsic value
    bool expired = t <= 0;
    price = expired ? (intrinsic > 0 ? intrinsic : 0.0L) : price;
    delta = expired ? (intrinsic > 0 ? sign : 0.0L) : delta;
    bool flat = expired || zeroVol;
    gamma = flat ? 0.0L : gamma;
    vega = flat ? 0.0L : vega;
    theta = flat ? 0.0L : theta;
    rho = flat ? 0.0L : rho;
    outp[i] = price;
    outp[n + i] = delta;
    outp[2 * n + i] = gamma;
    outp[3 * n + i] = vega;
    outp[4 * n + i] = theta;
    outp[5 * n + i] = rho;
}

// ---------- Monte Carlo portfolio VaR ----------
// inp: chol (assets²), weights, drifts. fp[0] = sqrt(horizon). ip: assets, scenarios, seedLo, seedHi.

[numthreads(64, 1, 1)]
void portfolio_return(uint3 id : SV_DispatchThreadID)
{
    uint sc = thread_index(id);
    uint n = ip[0];
    if (sc >= ip[1]) return;
    uint2 seed = uint2(ip[2], ip[3]);
    double sqrtH = fp[0];
    uint2 baseCounter = mul32(sc, n);
    double total = 0;
    [loop] for (uint asset = 0; asset < n; asset++)
    {
        double shock = 0;
        [loop] for (uint k = 0; k <= asset; k++)
            shock += inp[asset * n + k] * gaussian(seed, add64(baseCounter, uint2(k, 0u)));
        total += inp[n * n + asset] * (dexp(inp[n * n + n + asset] + sqrtH * shock) - 1.0L);
    }
    outp[sc] = total;
}
