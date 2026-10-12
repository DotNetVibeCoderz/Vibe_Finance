//! Vectorisable batch Black-Scholes. libm's `exp`/`ln` are opaque calls that stop LLVM from vectorising, so this
//! module has branch-free `exp`, `ln`, `erfc` (Cody's rational approximations, same constants as `normal.rs`) built
//! from add/mul/div, bit manipulation and selects. The batch loop is compiled per SIMD level (`simd::multiversion`)
//! and processes `LANES` options at a time. AVX2 and AVX-512 run the same code and agree bit for bit; against the
//! libm-based scalar `pricing::black_scholes` the results differ by a few ulps (exp/log rounding). `fnat_black_scholes_batch`
//! uses it from AVX2 up; at the baseline level (2 lanes) it is slower than libm, so the scalar reference runs instead.
//! Measured (i7-8650U, 1M options, best of 5): libm scalar 104 ms, this kernel on AVX2 81 ms.

use crate::pricing::Greeks;

const LANES: usize = 8;
const LN2_HI: f64 = 0.693_147_180_369_123_816_490;
const LN2_LO: f64 = 1.908_214_929_270_587_700_02e-10;
const INV_LN2: f64 = 1.442_695_040_888_963_387_00;
const MAGIC: f64 = 6_755_399_441_055_744.0; // 1.5·2^52: adding it rounds to an integer held in the low bits
const SQRT2: f64 = 1.414_213_562_373_095_048_80;
const INV_SQRT_2PI: f64 = 0.398_942_280_401_432_677_94;

#[inline(always)]
fn sel(c: bool, a: f64, b: f64) -> f64 {
    if c { a } else { b }
}

/// e^x, relative error ~1e-16, branch-free.
#[inline(always)]
pub fn exp_bf(x: f64) -> f64 {
    let xc = x.max(-745.0).min(709.0);
    let t = xc * INV_LN2 + MAGIC;
    let kf = t - MAGIC;
    let r = (xc - kf * LN2_HI) - kf * LN2_LO;
    let mut p = 1.0 / 6_227_020_800.0;
    p = p * r + 1.0 / 479_001_600.0;
    p = p * r + 1.0 / 39_916_800.0;
    p = p * r + 1.0 / 3_628_800.0;
    p = p * r + 1.0 / 362_880.0;
    p = p * r + 1.0 / 40_320.0;
    p = p * r + 1.0 / 5_040.0;
    p = p * r + 1.0 / 720.0;
    p = p * r + 1.0 / 120.0;
    p = p * r + 1.0 / 24.0;
    p = p * r + 1.0 / 6.0;
    p = p * r + 0.5;
    p = p * r + 1.0;
    p = p * r + 1.0;
    // Two half scales keep subnormal and near-overflow results representable. k1 = floor(k/2) and k2 = k - k1 are
    // rounded with the same magic-number trick; 2^k comes from the low bits (u64 add and shift only, so the loop
    // vectorises on AVX2, which has no 64-bit arithmetic shift or i64->f64 conversion).
    let k1f = ((kf - 0.5) * 0.5 + MAGIC) - MAGIC;
    let k2f = kf - k1f;
    let pow2 = |kf: f64| f64::from_bits(((kf + MAGIC).to_bits().wrapping_sub(MAGIC.to_bits()).wrapping_add(1023)) << 52);
    let s1 = pow2(k1f);
    let s2 = pow2(k2f);
    let res = p * s1 * s2;
    let res = sel(x > 709.0, f64::INFINITY, res);
    let res = sel(x < -745.0, 0.0, res);
    sel(x.is_nan(), f64::NAN, res)
}

/// ln(x), relative error ~1e-16, branch-free (NaN for x < 0, -inf at 0).
#[inline(always)]
pub fn ln_bf(x: f64) -> f64 {
    let tiny = x < f64::MIN_POSITIVE;
    let xs = sel(tiny, x * 18_014_398_509_481_984.0, x); // 2^54 lifts subnormals into the normal range
    let bits = xs.to_bits();
    // Biased exponent as f64: (2^52 + field) - 2^52, exact.
    let field = f64::from_bits(0x4330_0000_0000_0000 | ((bits >> 52) & 0x7ff)) - 4_503_599_627_370_496.0;
    let mut ef = field - 1023.0 - sel(tiny, 54.0, 0.0);
    let mut m = f64::from_bits((bits & 0x000f_ffff_ffff_ffff) | 0x3ff0_0000_0000_0000);
    let big = m > SQRT2;
    m = sel(big, m * 0.5, m);
    ef += sel(big, 1.0, 0.0);
    let s = (m - 1.0) / (m + 1.0);
    let s2 = s * s;
    let mut t = 1.0 / 25.0;
    t = t * s2 + 1.0 / 23.0;
    t = t * s2 + 1.0 / 21.0;
    t = t * s2 + 1.0 / 19.0;
    t = t * s2 + 1.0 / 17.0;
    t = t * s2 + 1.0 / 15.0;
    t = t * s2 + 1.0 / 13.0;
    t = t * s2 + 1.0 / 11.0;
    t = t * s2 + 1.0 / 9.0;
    t = t * s2 + 1.0 / 7.0;
    t = t * s2 + 1.0 / 5.0;
    t = t * s2 + 1.0 / 3.0;
    t = t * s2 + 1.0;
    let res = 2.0 * s * t + ef * LN2_LO + ef * LN2_HI;
    let res = sel(x == f64::INFINITY, f64::INFINITY, res);
    let res = sel(x == 0.0, f64::NEG_INFINITY, res);
    sel(x < 0.0 || x.is_nan(), f64::NAN, res)
}

/// Complementary error function: Cody's three rational approximations evaluated together, the right one selected.
#[inline(always)]
pub fn erfc_bf(x: f64) -> f64 {
    let ax = x.abs();
    // |x| < 0.5: 1 - erf(x), erf = x·P(z)/Q(z)
    let z = x * x;
    let num0 = (((1.857777061846031527e-1 * z + 3.161123743870565597) * z + 1.138641541510501556e2) * z + 3.774852376853020208e2) * z
        + 3.209377589138469473e3;
    let den0 = (((z + 2.360129095234412093e1) * z + 2.440246379344441733e2) * z + 1.282616526077372276e3) * z + 2.844236833439170622e3;
    let small = 1.0 - x * num0 / den0;
    let e = exp_bf(-ax * ax);
    // 0.5 <= |x| < 4
    let num1 = ((((((-1.368648573827167067e-7 * ax + 5.641955174789739711e-1) * ax + 7.211758250883093659) * ax + 4.316222722205673530e1)
        * ax
        + 1.529892850469404039e2)
        * ax
        + 3.393208167343436870e2)
        * ax
        + 4.519189537118729422e2)
        * ax
        + 3.004592610201616005e2;
    let den1 = ((((((ax + 1.278272731962942351e1) * ax + 7.700015293522947295e1) * ax + 2.775854447439876434e2) * ax + 6.389802644656311665e2)
        * ax
        + 9.313540948506096211e2)
        * ax
        + 7.909509253278980272e2)
        * ax
        + 3.004592609569832933e2;
    let mid = e * num1 / den1;
    // |x| >= 4
    let axs = ax.max(0.5);
    let zi = 1.0 / (axs * axs);
    let num2 = (((-2.23192459734184686e-2 * zi - 2.78661308609647788e-1) * zi - 2.26956593539686930e-1) * zi - 4.94730910623250734e-2) * zi
        - 2.99610707703542174e-3;
    let den2 = (((zi + 1.98733201817135256) * zi + 1.05167510706793207) * zi + 1.91308926107829841e-1) * zi + 1.06209230528467918e-2;
    let tail = e / axs * (0.564_189_583_547_756_286_95 + zi * num2 / den2);
    let r = sel(ax < 4.0, mid, tail);
    let r = sel(x < 0.0, 2.0 - r, r);
    sel(ax < 0.5, small, r)
}

#[inline(always)]
fn cdf_bf(x: f64) -> f64 {
    0.5 * erfc_bf(-x / SQRT2)
}

/// One option, branch-free; same formulas as `pricing::black_scholes` (puts through an exact ±1 sign).
#[inline(always)]
fn one(s: f64, k: f64, t: f64, r: f64, q: f64, vol: f64, call: bool) -> Greeks {
    let sign = sel(call, 1.0, -1.0);
    let tt = sel(t > 0.0, t, 1.0);
    let vv = sel(vol > 0.0, vol, 1.0);
    let dq = exp_bf(-q * tt);
    let dr = exp_bf(-r * tt);
    let sqrt_t = tt.sqrt();
    let vs = vv * sqrt_t;
    let d1 = (ln_bf(s / k) + (r - q + 0.5 * vv * vv) * tt) / vs;
    let d2 = d1 - vs;
    let nd1 = INV_SQRT_2PI * exp_bf(-0.5 * d1 * d1);
    let c1 = cdf_bf(sign * d1);
    let c2 = cdf_bf(sign * d2);
    let gamma = dq * nd1 / (s * vs);
    let vega = s * dq * nd1 * sqrt_t;
    let decay = -s * dq * nd1 * vv / (2.0 * sqrt_t);
    let price = sign * (s * dq * c1 - k * dr * c2);
    let delta = sign * dq * c1;
    let theta = decay - sign * r * k * dr * c2 + sign * q * s * dq * c1;
    let rho = sign * k * tt * dr * c2;

    // Zero volatility: discounted forward intrinsic. Expired: intrinsic.
    let fwd = sign * (s * dq - k * dr);
    let zero_vol = vol <= 0.0;
    let expired = t <= 0.0;
    let intrinsic = sign * (s - k);
    let price = sel(zero_vol, fwd.max(0.0), price);
    let delta = sel(zero_vol, sel(fwd > 0.0, sign * dq, 0.0), delta);
    let price = sel(expired, intrinsic.max(0.0), price);
    let delta = sel(expired, sel(intrinsic > 0.0, sign, 0.0), delta);
    let flat = expired || zero_vol;
    Greeks {
        price,
        delta,
        gamma: sel(flat, 0.0, gamma),
        vega: sel(flat, 0.0, vega),
        theta: sel(flat, 0.0, theta),
        rho: sel(flat, 0.0, rho),
    }
}

/// Inputs and the six output planes for a batch.
pub struct Batch<'a> {
    pub s: &'a [f64],
    pub k: &'a [f64],
    pub t: &'a [f64],
    pub r: &'a [f64],
    pub q: &'a [f64],
    pub v: &'a [f64],
    pub call: &'a [u8],
}

#[inline(always)]
fn batch_body(b: &Batch, out: &mut [&mut [f64]; 6]) {
    let n = b.s.len();
    let mut i = 0;
    while i < n {
        let m = (n - i).min(LANES);
        let mut x = [[0.0f64; LANES]; 7];
        for (lane, src) in x.iter_mut().zip([b.s, b.k, b.t, b.r, b.q, b.v]) {
            lane[..m].copy_from_slice(&src[i..i + m]);
            let last = lane[m - 1];
            lane[m..].fill(last); // tail lanes repeat the last option; their results are dropped
        }
        for j in 0..LANES {
            x[6][j] = b.call[i + j.min(m - 1)] as f64;
        }
        let mut g = [[0.0f64; LANES]; 6];
        for j in 0..LANES {
            let r = one(x[0][j], x[1][j], x[2][j], x[3][j], x[4][j], x[5][j], x[6][j] != 0.0);
            g[0][j] = r.price;
            g[1][j] = r.delta;
            g[2][j] = r.gamma;
            g[3][j] = r.vega;
            g[4][j] = r.theta;
            g[5][j] = r.rho;
        }
        for (o, plane) in out.iter_mut().zip(&g) {
            if !o.is_empty() {
                o[i..i + m].copy_from_slice(&plane[..m]);
            }
        }
        i += m;
    }
}

crate::multiversion!(pub fn batch(b: &Batch, out: &mut [&mut [f64]; 6]) => batch_body);

#[cfg(test)]
mod tests {
    use super::*;

    fn rel(a: f64, b: f64) -> f64 {
        (a - b).abs() / b.abs().max(1.0)
    }

    #[test]
    fn exp_and_ln_match_libm() {
        let mut worst = (0.0f64, 0.0f64);
        for i in 0..200_000 {
            let x = -700.0 + 1400.0 * (i as f64) / 200_000.0;
            worst.0 = worst.0.max(((exp_bf(x) - x.exp()) / x.exp()).abs());
            let y = (i as f64 + 0.5) * 1e-3 * (1.0 + (i % 7) as f64 * 1e3);
            worst.1 = worst.1.max((ln_bf(y) - y.ln()).abs() / y.ln().abs().max(1.0));
        }
        assert!(worst.0 < 4e-16 && worst.1 < 4e-16, "exp {:e} ln {:e}", worst.0, worst.1);
        assert_eq!(exp_bf(800.0), f64::INFINITY);
        assert_eq!(exp_bf(-800.0), 0.0);
        assert!((exp_bf(-740.0) / (-740.0f64).exp() - 1.0).abs() < 1e-10); // subnormal result
        assert!((ln_bf(1e-310) - 1e-310f64.ln()).abs() < 1e-13);
        assert_eq!(ln_bf(0.0), f64::NEG_INFINITY);
        assert!(ln_bf(-1.0).is_nan() && exp_bf(f64::NAN).is_nan());
    }

    #[test]
    fn batch_matches_scalar_reference_including_edge_cases() {
        let n = 1003;
        let mut cols: [Vec<f64>; 6] = Default::default();
        let mut calls = Vec::new();
        for i in 0..n {
            let f = i as f64;
            cols[0].push(50.0 + (f * 7.31) % 100.0);
            cols[1].push(60.0 + (f * 3.17) % 80.0);
            cols[2].push(if i % 97 == 0 { 0.0 } else { 0.01 + (f * 0.37) % 3.0 });
            cols[3].push((f * 0.013) % 0.1 - 0.01);
            cols[4].push((f * 0.007) % 0.05);
            cols[5].push(if i % 89 == 0 { 0.0 } else { 0.05 + (f * 0.11) % 0.8 });
            calls.push((i % 2) as u8);
        }
        let b = Batch { s: &cols[0], k: &cols[1], t: &cols[2], r: &cols[3], q: &cols[4], v: &cols[5], call: &calls };
        let mut planes: [Vec<f64>; 6] = Default::default();
        for p in planes.iter_mut() {
            p.resize(n, f64::NAN);
        }
        {
            let [a, bb, c, d, e, f] = &mut planes;
            let mut out: [&mut [f64]; 6] = [a, bb, c, d, e, f];
            batch(&b, &mut out);
        }
        for i in 0..n {
            let g = crate::pricing::black_scholes(cols[0][i], cols[1][i], cols[2][i], cols[3][i], cols[4][i], cols[5][i], calls[i] != 0);
            for (name, got, want) in [("price", planes[0][i], g.price), ("delta", planes[1][i], g.delta), ("gamma", planes[2][i], g.gamma),
                ("vega", planes[3][i], g.vega), ("theta", planes[4][i], g.theta), ("rho", planes[5][i], g.rho)]
            {
                assert!(rel(got, want) < 1e-13, "row {i} {name}: {got} vs {want}");
            }
        }
    }
}

#[cfg(test)]
mod timing {
    /// `cargo test --release bs_timing -- --ignored --nocapture`: libm scalar loop vs the vectorised batch per SIMD level.
    #[test]
    #[ignore]
    fn bs_timing() {
        let n = 1_000_000;
        let s: Vec<f64> = (0..n).map(|i| 80.0 + (i % 400) as f64 * 0.1).collect();
        let k = vec![100.0; n];
        let t = vec![1.0; n];
        let r = vec![0.05; n];
        let q = vec![0.01; n];
        let v = vec![0.2; n];
        let c: Vec<u8> = (0..n).map(|i| (i % 2) as u8).collect();
        let best = |f: &mut dyn FnMut()| (0..5).map(|_| { let w = std::time::Instant::now(); f(); w.elapsed().as_secs_f64() * 1e3 }).fold(f64::MAX, f64::min);
        let mut sink = 0.0;
        let scalar = best(&mut || { for i in 0..n { sink += crate::pricing::black_scholes(s[i], k[i], t[i], r[i], q[i], v[i], c[i] != 0).price; } });
        println!("libm scalar loop: {scalar:.1} ms ({sink})");
        for l in [crate::simd::SCALAR, crate::simd::AVX2, crate::simd::AVX512] {
            if crate::simd::set_level(l) != l { continue; }
            let mut planes: [Vec<f64>; 6] = std::array::from_fn(|_| vec![0.0; n]);
            let b = super::Batch { s: &s, k: &k, t: &t, r: &r, q: &q, v: &v, call: &c };
            let ms = best(&mut || { let [a, bb, cc, d, e, f] = &mut planes; let mut o: [&mut [f64]; 6] = [a, bb, cc, d, e, f]; super::batch(&b, &mut o); });
            println!("bs_simd {}: {ms:.1} ms", crate::simd::name(l));
        }
    }
}
