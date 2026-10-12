// Finance.Net GPU kernels (WGSL with the f64 and i64 extensions), ports of Finance.GPU's ILGPU kernels.
// SPIR-V has no 64-bit exp/log/cos, so they are implemented here with range reduction + polynomials
// (relative error ~1e-16), using only add, multiply, divide, sqrt and exponent manipulation.
// Negative f64 literals are written as (0.0lf - x): naga 24 cannot negate f64 constants.
//
// Bindings: 0 = f64 parameters, 1 = u32 parameters, 2 = f64 input data, 3 = f64 output data.

@group(0) @binding(0) var<storage, read> fp: array<f64>;
@group(0) @binding(1) var<storage, read> ip: array<u32>;
@group(0) @binding(2) var<storage, read> inp: array<f64>;
@group(0) @binding(3) var<storage, read_write> outp: array<f64>;

const LN2_HI: f64 = 0.693147180369123816490lf;
const LN2_LO: f64 = 1.90821492927058770002e-10lf;
const INV_LN2: f64 = 1.44269504088896338700lf;
const HALF_PI: f64 = 1.57079632679489661923lf;

fn u64_of(lo: u32, hi: u32) -> u64 {
    return (u64(hi) << 32u) | u64(lo);
}

// 2^k for integer k in [-1022, 1023] built from the exponent bits.
fn pow2(k: i32) -> f64 {
    return bitcast<f64>(u64(k + 1023) << 52u);
}

fn exp64(x: f64) -> f64 {
    if (x > 709.0lf) { return bitcast<f64>(0x7FF0000000000000lu); }
    if (x < (0.0lf - 745.0lf)) { return 0.0lf; }
    let kf = floor(x * INV_LN2 + 0.5lf);
    let r = (x - kf * LN2_HI) - kf * LN2_LO;            // |r| <= ln2/2
    // exp(r) by Taylor series to r^13 (error < 2e-17 on |r| <= 0.347)
    var p = 1.0lf / 6227020800.0lf;
    p = p * r + 1.0lf / 479001600.0lf;
    p = p * r + 1.0lf / 39916800.0lf;
    p = p * r + 1.0lf / 3628800.0lf;
    p = p * r + 1.0lf / 362880.0lf;
    p = p * r + 1.0lf / 40320.0lf;
    p = p * r + 1.0lf / 5040.0lf;
    p = p * r + 1.0lf / 720.0lf;
    p = p * r + 1.0lf / 120.0lf;
    p = p * r + 1.0lf / 24.0lf;
    p = p * r + 1.0lf / 6.0lf;
    p = p * r + 0.5lf;
    p = p * r + 1.0lf;
    p = p * r + 1.0lf;
    let k = i32(kf);
    // Split the scale so subnormal and near-overflow results stay representable.
    if (k < -1000) { return p * pow2(k + 1000) * pow2(-1000); }
    if (k > 1000) { return p * pow2(k - 1000) * pow2(1000); }
    return p * pow2(k);
}

// Natural log for x > 0 (normal numbers): x = m·2^e with m in [sqrt(1/2), sqrt(2)), log m = 2·atanh(s), s = (m-1)/(m+1).
fn log64(x: f64) -> f64 {
    if (x <= 0.0lf) { return bitcast<f64>(0xFFF8000000000000lu); }
    let bits = bitcast<u64>(x);
    var e = i32((bits >> 52u) & 0x7FFlu) - 1023;
    var m = bitcast<f64>((bits & 0x000FFFFFFFFFFFFFlu) | 0x3FF0000000000000lu);   // [1, 2)
    if (m > 1.41421356237309504880lf) { m = m * 0.5lf; e = e + 1; }
    let s = (m - 1.0lf) / (m + 1.0lf);
    let s2 = s * s;
    var t = 1.0lf / 25.0lf;
    t = t * s2 + 1.0lf / 23.0lf;
    t = t * s2 + 1.0lf / 21.0lf;
    t = t * s2 + 1.0lf / 19.0lf;
    t = t * s2 + 1.0lf / 17.0lf;
    t = t * s2 + 1.0lf / 15.0lf;
    t = t * s2 + 1.0lf / 13.0lf;
    t = t * s2 + 1.0lf / 11.0lf;
    t = t * s2 + 1.0lf / 9.0lf;
    t = t * s2 + 1.0lf / 7.0lf;
    t = t * s2 + 1.0lf / 5.0lf;
    t = t * s2 + 1.0lf / 3.0lf;
    t = t * s2 + 1.0lf;
    let ef = f64(e);
    return 2.0lf * s * t + ef * LN2_LO + ef * LN2_HI;
}

fn sin_poly(a: f64) -> f64 {   // |a| <= pi/4
    let a2 = a * a;
    var p = (0.0lf - 1.0lf) / 1307674368000.0lf;
    p = p * a2 + 1.0lf / 6227020800.0lf;
    p = p * a2 - 1.0lf / 39916800.0lf;
    p = p * a2 + 1.0lf / 362880.0lf;
    p = p * a2 - 1.0lf / 5040.0lf;
    p = p * a2 + 1.0lf / 120.0lf;
    p = p * a2 - 1.0lf / 6.0lf;
    return a + a * a2 * p;
}

fn cos_poly(a: f64) -> f64 {   // |a| <= pi/4
    let a2 = a * a;
    var p = 1.0lf / 20922789888000.0lf;
    p = p * a2 - 1.0lf / 87178291200.0lf;
    p = p * a2 + 1.0lf / 479001600.0lf;
    p = p * a2 - 1.0lf / 3628800.0lf;
    p = p * a2 + 1.0lf / 40320.0lf;
    p = p * a2 - 1.0lf / 720.0lf;
    p = p * a2 + 1.0lf / 24.0lf;
    p = p * a2 - 0.5lf;
    return 1.0lf + a2 * p;
}

// cos(2·pi·u) for u in [0, 1], reduced exactly in turns: octant o = round(8u) / quadrant arithmetic.
fn cos_turns(u: f64) -> f64 {
    let q = floor(u * 4.0lf + 0.5lf);                    // nearest quarter turn
    let f = u - q * 0.25lf;                              // |f| <= 1/8 turn
    let a = f * 4.0lf * HALF_PI;                         // |a| <= pi/4
    let qi = i32(q) & 3;
    if (qi == 0) { return cos_poly(a); }
    if (qi == 1) { return -sin_poly(a); }
    if (qi == 2) { return -cos_poly(a); }
    return sin_poly(a);
}

// ---------- counter-based RNG (identical to GpuMath) ----------

fn mix(z0: u64) -> u64 {
    var z = z0;
    z = (z ^ (z >> 30u)) * 0xBF58476D1CE4E5B9lu;
    z = (z ^ (z >> 27u)) * 0x94D049BB133111EBlu;
    return z ^ (z >> 31u);
}

fn uniform(seed: u64, counter: u64) -> f64 {
    return f64((mix(seed + (counter + 1lu) * 0x9E3779B97F4A7C15lu) >> 11u) + 1lu) * (1.0lf / 9007199254740992.0lf);
}

fn gaussian(seed: u64, counter: u64) -> f64 {
    let u1 = uniform(seed, 2lu * counter);
    let u2 = uniform(seed, 2lu * counter + 1lu);
    return sqrt((0.0lf - 2.0lf) * log64(u1)) * cos_turns(u2);
}

// ---------- normal distribution (Cody erfc, same constants as GpuMath) ----------

fn erfc64(x: f64) -> f64 {
    let ax = abs(x);
    var result: f64;
    if (ax < 0.5lf) {
        let z = x * x;
        let num = (((1.857777061846031527e-1lf * z + 3.161123743870565597lf) * z + 1.138641541510501556e2lf) * z + 3.774852376853020208e2lf) * z + 3.209377589138469473e3lf;
        let den = (((z + 2.360129095234412093e1lf) * z + 2.440246379344441733e2lf) * z + 1.282616526077372276e3lf) * z + 2.844236833439170622e3lf;
        return 1.0lf - x * num / den;
    }
    if (ax < 4.0lf) {
        let num = (((((((0.0lf - 1.368648573827167067e-7lf) * ax + 5.641955174789739711e-1lf) * ax + 7.211758250883093659lf) * ax + 4.316222722205673530e1lf) * ax
            + 1.529892850469404039e2lf) * ax + 3.393208167343436870e2lf) * ax + 4.519189537118729422e2lf) * ax + 3.004592610201616005e2lf;
        let den = ((((((ax + 1.278272731962942351e1lf) * ax + 7.700015293522947295e1lf) * ax + 2.775854447439876434e2lf) * ax + 6.389802644656311665e2lf) * ax
            + 9.313540948506096211e2lf) * ax + 7.909509253278980272e2lf) * ax + 3.004592609569832933e2lf;
        result = exp64(-ax * ax) * num / den;
    } else {
        let z = 1.0lf / (ax * ax);
        let num = ((((0.0lf - 2.23192459734184686e-2lf) * z - 2.78661308609647788e-1lf) * z - 2.26956593539686930e-1lf) * z - 4.94730910623250734e-2lf) * z - 2.99610707703542174e-3lf;
        let den = (((z + 1.98733201817135256lf) * z + 1.05167510706793207lf) * z + 1.91308926107829841e-1lf) * z + 1.06209230528467918e-2lf;
        result = exp64(-ax * ax) / ax * (0.56418958354775628695lf + z * num / den);
    }
    if (x < 0.0lf) { return 2.0lf - result; }
    return result;
}

fn norm_cdf(x: f64) -> f64 { return 0.5lf * erfc64(-x * 0.70710678118654752440lf); }
fn norm_pdf(x: f64) -> f64 { return 0.39894228040143267794lf * exp64((0.0lf - 0.5lf) * x * x); }

fn thread_index(gid: vec3<u32>, groups: vec3<u32>) -> u32 {
    return gid.x + gid.y * groups.x * 64u;
}

// ---------- Monte Carlo (European and arithmetic Asian, antithetic pairs) ----------
// fp: spot, strike, time, rate, dividend, vol.  ip: isCall, steps, pptLo, pptHi, totalLo, totalHi, seedLo, seedHi, threads.
// outp: [sums(threads), sumsq(threads)].

fn payoff(s: f64, strike: f64, call: bool) -> f64 {
    var v = strike - s;
    if (call) { v = s - strike; }
    return max(v, 0.0lf);
}

@compute @workgroup_size(64)
fn mc_european(@builtin(global_invocation_id) gid: vec3<u32>, @builtin(num_workgroups) groups: vec3<u32>) {
    let t = thread_index(gid, groups);
    let threads = ip[8];
    if (t >= threads) { return; }
    let spot = fp[0]; let strike = fp[1]; let time = fp[2]; let rate = fp[3]; let dividend = fp[4]; let vol = fp[5];
    let call = ip[0] != 0u;
    let ppt = u64_of(ip[2], ip[3]);
    let total = u64_of(ip[4], ip[5]);
    let seed = u64_of(ip[6], ip[7]);
    let start = u64(t) * ppt;
    var end = start + ppt;
    if (end > total) { end = total; }
    let drift = (rate - dividend - 0.5lf * vol * vol) * time;
    let diff = vol * sqrt(time);
    var s1 = 0.0lf;
    var s2 = 0.0lf;
    for (var p = start; p < end; p = p + 1lu) {
        let z = gaussian(seed, p);
        let up = spot * exp64(drift + diff * z);
        let dn = spot * exp64(drift - diff * z);
        let v = 0.5lf * (payoff(up, strike, call) + payoff(dn, strike, call));
        s1 = s1 + v;
        s2 = s2 + v * v;
    }
    outp[t] = s1;
    outp[threads + t] = s2;
}

@compute @workgroup_size(64)
fn mc_asian(@builtin(global_invocation_id) gid: vec3<u32>, @builtin(num_workgroups) groups: vec3<u32>) {
    let t = thread_index(gid, groups);
    let threads = ip[8];
    if (t >= threads) { return; }
    let spot = fp[0]; let strike = fp[1]; let time = fp[2]; let rate = fp[3]; let dividend = fp[4]; let vol = fp[5];
    let call = ip[0] != 0u;
    let steps = ip[1];
    let ppt = u64_of(ip[2], ip[3]);
    let total = u64_of(ip[4], ip[5]);
    let seed = u64_of(ip[6], ip[7]);
    let start = u64(t) * ppt;
    var end = start + ppt;
    if (end > total) { end = total; }
    let dt = time / f64(steps);
    let drift = (rate - dividend - 0.5lf * vol * vol) * dt;
    let diff = vol * sqrt(dt);
    var s1 = 0.0lf;
    var s2 = 0.0lf;
    for (var p = start; p < end; p = p + 1lu) {
        var up = spot;
        var dn = spot;
        var sum_up = 0.0lf;
        var sum_dn = 0.0lf;
        let base_counter = p * u64(steps);
        for (var j = 0u; j < steps; j = j + 1u) {
            let z = gaussian(seed, base_counter + u64(j));
            up = up * exp64(drift + diff * z);
            dn = dn * exp64(drift - diff * z);
            sum_up = sum_up + up;
            sum_dn = sum_dn + dn;
        }
        let v = 0.5lf * (payoff(sum_up / f64(steps), strike, call) + payoff(sum_dn / f64(steps), strike, call));
        s1 = s1 + v;
        s2 = s2 + v * v;
    }
    outp[t] = s1;
    outp[threads + t] = s2;
}

// ---------- batch Black-Scholes ----------
// inp: 7 planes of n (spot, strike, time, rate, dividend, vol, isCall 0/1); outp: 6 planes (price, delta, gamma, vega, theta, rho).
// ip[0] = n.

@compute @workgroup_size(64)
fn black_scholes(@builtin(global_invocation_id) gid: vec3<u32>, @builtin(num_workgroups) groups: vec3<u32>) {
    let i = thread_index(gid, groups);
    let n = ip[0];
    if (i >= n) { return; }
    let s = inp[i]; let k = inp[n + i]; let t = inp[2u * n + i]; let r = inp[3u * n + i]; let q = inp[4u * n + i]; let vol = inp[5u * n + i];
    let call = inp[6u * n + i] != 0.0lf;
    var price = 0.0lf; var delta = 0.0lf; var gamma = 0.0lf; var vega = 0.0lf; var theta = 0.0lf; var rho = 0.0lf;
    if (t <= 0.0lf) {
        var intrinsic = k - s;
        if (call) { intrinsic = s - k; }
        price = max(intrinsic, 0.0lf);
        if (intrinsic > 0.0lf) { delta = select((0.0lf - 1.0lf), 1.0lf, call); }
    } else {
        let dq = exp64(-q * t);
        let dr = exp64(-r * t);
        if (vol <= 0.0lf) {
            let fwd = s * dq - k * dr;
            if (call) {
                if (fwd > 0.0lf) { price = fwd; delta = dq; }
            } else {
                if (fwd < 0.0lf) { price = -fwd; delta = -dq; }
            }
        } else {
            let sqrt_t = sqrt(t);
            let vs = vol * sqrt_t;
            let d1 = (log64(s / k) + (r - q + 0.5lf * vol * vol) * t) / vs;
            let d2 = d1 - vs;
            let nd1 = norm_pdf(d1);
            gamma = dq * nd1 / (s * vs);
            vega = s * dq * nd1 * sqrt_t;
            let decay = -s * dq * nd1 * vol / (2.0lf * sqrt_t);
            if (call) {
                let c1 = norm_cdf(d1); let c2 = norm_cdf(d2);
                price = s * dq * c1 - k * dr * c2;
                delta = dq * c1;
                theta = decay - r * k * dr * c2 + q * s * dq * c1;
                rho = k * t * dr * c2;
            } else {
                let c1 = norm_cdf(-d1); let c2 = norm_cdf(-d2);
                price = k * dr * c2 - s * dq * c1;
                delta = -dq * c1;
                theta = decay + r * k * dr * c2 - q * s * dq * c1;
                rho = -k * t * dr * c2;
            }
        }
    }
    outp[i] = price;
    outp[n + i] = delta;
    outp[2u * n + i] = gamma;
    outp[3u * n + i] = vega;
    outp[4u * n + i] = theta;
    outp[5u * n + i] = rho;
}

// ---------- Monte Carlo portfolio VaR ----------
// inp: chol (assets²), weights (assets), drifts (assets). fp[0] = sqrt(horizon).
// ip: assets, scenarios, seedLo, seedHi. outp[scenario] = portfolio return.

@compute @workgroup_size(64)
fn portfolio_return(@builtin(global_invocation_id) gid: vec3<u32>, @builtin(num_workgroups) groups: vec3<u32>) {
    let sc = thread_index(gid, groups);
    let n = ip[0];
    if (sc >= ip[1]) { return; }
    let seed = u64_of(ip[2], ip[3]);
    let sqrt_h = fp[0];
    let base_counter = u64(sc) * u64(n);
    var total = 0.0lf;
    for (var asset = 0u; asset < n; asset = asset + 1u) {
        var shock = 0.0lf;
        for (var k = 0u; k <= asset; k = k + 1u) {
            shock = shock + inp[asset * n + k] * gaussian(seed, base_counter + u64(k));
        }
        total = total + inp[n * n + asset] * (exp64(inp[n * n + n + asset] + sqrt_h * shock) - 1.0lf);
    }
    outp[sc] = total;
}
