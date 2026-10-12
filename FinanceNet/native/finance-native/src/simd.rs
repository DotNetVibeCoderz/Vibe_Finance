//! Runtime SIMD dispatch. Hot loops are written once as `#[inline(always)]` bodies and compiled three times:
//! baseline (SSE2 on x86-64, NEON on aarch64), AVX2+FMA and AVX-512F. The loops chosen are element-wise (no
//! reassociated reductions) and Rust never contracts `a * b + c` into an FMA, so every level returns bit-identical
//! results; only the vector width changes. The level is detected once (CPUID) and can be forced with the
//! `FINANCE_NATIVE_SIMD` environment variable (`scalar`, `avx2`, `avx512`) or `fnat_set_simd_level`.

use std::sync::atomic::{AtomicI32, Ordering};

pub const SCALAR: i32 = 0;
pub const AVX2: i32 = 1;
pub const AVX512: i32 = 2;

static LEVEL: AtomicI32 = AtomicI32::new(-1);

/// Highest level this CPU supports.
pub fn supported() -> i32 {
    #[cfg(target_arch = "x86_64")]
    {
        if std::arch::is_x86_feature_detected!("avx512f") {
            return AVX512;
        }
        if std::arch::is_x86_feature_detected!("avx2") && std::arch::is_x86_feature_detected!("fma") {
            return AVX2;
        }
    }
    SCALAR
}

/// Level in use (detected on first call, honouring FINANCE_NATIVE_SIMD).
pub fn level() -> i32 {
    let l = LEVEL.load(Ordering::Relaxed);
    if l >= 0 {
        return l;
    }
    let wanted = match std::env::var("FINANCE_NATIVE_SIMD").as_deref() {
        Ok("scalar") => SCALAR,
        Ok("avx2") => AVX2,
        Ok("avx512") => AVX512,
        _ => i32::MAX,
    };
    let l = wanted.min(supported());
    LEVEL.store(l, Ordering::Relaxed);
    l
}

/// Forces a level (clamped to what the CPU supports); returns the level now in use.
pub fn set_level(l: i32) -> i32 {
    let l = l.clamp(SCALAR, AVX512).min(supported());
    LEVEL.store(l, Ordering::Relaxed);
    l
}

pub fn name(l: i32) -> &'static str {
    match l {
        AVX512 => "avx512",
        AVX2 => "avx2",
        _ => "scalar",
    }
}

/// Generates `$name(args)` dispatching to baseline / AVX2 / AVX-512 copies of the `#[inline(always)]` body `$body`.
macro_rules! multiversion {
    ($vis:vis fn $name:ident($($arg:ident : $ty:ty),*) => $body:path) => {
        $vis fn $name($($arg: $ty),*) {
            #[cfg(target_arch = "x86_64")]
            {
                #[target_feature(enable = "avx512f")]
                unsafe fn v512($($arg: $ty),*) { $body($($arg),*) }
                #[target_feature(enable = "avx2,fma")]
                unsafe fn v256($($arg: $ty),*) { $body($($arg),*) }
                match $crate::simd::level() {
                    // SAFETY: the level never exceeds what CPUID reported (see `supported`).
                    $crate::simd::AVX512 => return unsafe { v512($($arg),*) },
                    $crate::simd::AVX2 => return unsafe { v256($($arg),*) },
                    _ => {}
                }
            }
            $body($($arg),*)
        }
    };
}


/// `y[i] += a * x[i]`, the inner loop of the covariance and correlation kernels.
#[inline(always)]
pub fn axpy_body(a: f64, x: &[f64], y: &mut [f64]) {
    let n = x.len().min(y.len());
    let (x, y) = (&x[..n], &mut y[..n]);
    for i in 0..n {
        y[i] += a * x[i];
    }
}

multiversion!(pub fn axpy(a: f64, x: &[f64], y: &mut [f64]) => axpy_body);

/// `out[i] = x[i] - m[i]`.
#[inline(always)]
pub fn sub_body(x: &[f64], m: &[f64], out: &mut [f64]) {
    let n = x.len().min(m.len()).min(out.len());
    let (x, m, out) = (&x[..n], &m[..n], &mut out[..n]);
    for i in 0..n {
        out[i] = x[i] - m[i];
    }
}

multiversion!(pub fn sub(x: &[f64], m: &[f64], out: &mut [f64]) => sub_body);

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_supported_level_is_bit_identical() {
        let x: Vec<f64> = (0..1003).map(|i| (i as f64 * 0.37).sin()).collect();
        let mut reference = vec![0.5; 1003];
        set_level(SCALAR);
        axpy(1.7, &x, &mut reference);
        for l in [AVX2, AVX512] {
            if set_level(l) != l {
                eprintln!("{} not supported here - skipped", name(l));
                continue;
            }
            let mut y = vec![0.5; 1003];
            axpy(1.7, &x, &mut y);
            assert!(y.iter().zip(&reference).all(|(a, b)| a.to_bits() == b.to_bits()), "{} differs", name(l));
        }
        LEVEL.store(-1, Ordering::Relaxed);
    }
}
