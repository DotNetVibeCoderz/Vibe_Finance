//! finance_native — Rust accelerators for Finance.Net, exposed through a C ABI.
//!
//! Conventions for every exported function:
//! * arrays are `(pointer, length)` pairs of `f64`, outputs are caller-allocated;
//! * the return value is a status code (`FNAT_OK` = 0, negative on error);
//! * panics never cross the boundary — they are caught and reported as `FNAT_ERR_PANIC`.
//!
//! Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.

pub mod indicators;
pub mod normal;
pub mod pricing;
pub mod risk;
pub mod rng;
pub mod ta;

use std::panic::{catch_unwind, AssertUnwindSafe};
use std::slice;

pub const FNAT_OK: i32 = 0;
pub const FNAT_ERR_NULL: i32 = -1;
pub const FNAT_ERR_ARG: i32 = -2;
pub const FNAT_ERR_PANIC: i32 = -3;

/// Version packed as major * 10000 + minor * 100 + patch.
#[no_mangle]
pub extern "C" fn fnat_version() -> u32 {
    let major: u32 = env!("CARGO_PKG_VERSION_MAJOR").parse().unwrap_or(0);
    let minor: u32 = env!("CARGO_PKG_VERSION_MINOR").parse().unwrap_or(0);
    let patch: u32 = env!("CARGO_PKG_VERSION_PATCH").parse().unwrap_or(0);
    major * 10000 + minor * 100 + patch
}

fn guard<F: FnOnce() -> i32>(f: F) -> i32 {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or(FNAT_ERR_PANIC)
}

unsafe fn input<'a>(p: *const f64, n: usize) -> Option<&'a [f64]> {
    if p.is_null() { None } else { Some(slice::from_raw_parts(p, n)) }
}

unsafe fn output<'a>(p: *mut f64, n: usize) -> Option<&'a mut [f64]> {
    if p.is_null() { None } else { Some(slice::from_raw_parts_mut(p, n)) }
}

macro_rules! window_fn {
    ($name:ident, $kernel:path) => {
        /// Window indicator: `out` has length `n`; positions before the first full window are NaN.
        #[no_mangle]
        pub unsafe extern "C" fn $name(x: *const f64, n: usize, period: usize, out: *mut f64) -> i32 {
            guard(|| {
                if period == 0 {
                    return FNAT_ERR_ARG;
                }
                match (input(x, n), output(out, n)) {
                    (Some(x), Some(o)) => {
                        $kernel(x, period, o);
                        FNAT_OK
                    }
                    _ => FNAT_ERR_NULL,
                }
            })
        }
    };
}

window_fn!(fnat_sma, indicators::sma);
window_fn!(fnat_ema, indicators::ema);
window_fn!(fnat_rsi, indicators::rsi);

/// Rolling standard deviation with `ddof` delta degrees of freedom (`ddof < period`).
#[no_mangle]
pub unsafe extern "C" fn fnat_rolling_std(x: *const f64, n: usize, period: usize, ddof: usize, out: *mut f64) -> i32 {
    guard(|| {
        if period == 0 || ddof >= period {
            return FNAT_ERR_ARG;
        }
        match (input(x, n), output(out, n)) {
            (Some(x), Some(o)) => {
                indicators::rolling_std(x, period, ddof, o);
                FNAT_OK
            }
            _ => FNAT_ERR_NULL,
        }
    })
}

/// Bollinger bands (population standard deviation).
#[no_mangle]
pub unsafe extern "C" fn fnat_bollinger(x: *const f64, n: usize, period: usize, k: f64, mid: *mut f64, upper: *mut f64, lower: *mut f64) -> i32 {
    guard(|| {
        if period == 0 {
            return FNAT_ERR_ARG;
        }
        match (input(x, n), output(mid, n), output(upper, n), output(lower, n)) {
            (Some(x), Some(m), Some(u), Some(l)) => {
                indicators::bollinger(x, period, k, m, u, l);
                FNAT_OK
            }
            _ => FNAT_ERR_NULL,
        }
    })
}

/// Log returns: `out` has length `n - 1`.
#[no_mangle]
pub unsafe extern "C" fn fnat_log_returns(x: *const f64, n: usize, out: *mut f64) -> i32 {
    guard(|| {
        if n < 2 {
            return FNAT_ERR_ARG;
        }
        match (input(x, n), output(out, n - 1)) {
            (Some(x), Some(o)) => {
                indicators::log_returns(x, o);
                FNAT_OK
            }
            _ => FNAT_ERR_NULL,
        }
    })
}

/// Maximum drawdown (positive fraction) plus peak and trough indices.
#[no_mangle]
pub unsafe extern "C" fn fnat_max_drawdown(x: *const f64, n: usize, depth: *mut f64, peak: *mut usize, trough: *mut usize) -> i32 {
    guard(|| {
        if n == 0 {
            return FNAT_ERR_ARG;
        }
        if depth.is_null() || peak.is_null() || trough.is_null() {
            return FNAT_ERR_NULL;
        }
        match input(x, n) {
            Some(x) => {
                let (d, p, t) = indicators::max_drawdown(x);
                *depth = d;
                *peak = p;
                *trough = t;
                FNAT_OK
            }
            None => FNAT_ERR_NULL,
        }
    })
}

/// Historical VaR and CVaR (positive losses) at `confidence` in (0, 1).
#[no_mangle]
pub unsafe extern "C" fn fnat_historical_var(r: *const f64, n: usize, confidence: f64, var: *mut f64, cvar: *mut f64) -> i32 {
    guard(|| {
        if n == 0 || !(confidence > 0.0 && confidence < 1.0) {
            return FNAT_ERR_ARG;
        }
        if var.is_null() || cvar.is_null() {
            return FNAT_ERR_NULL;
        }
        match input(r, n) {
            Some(r) => {
                let (v, c) = risk::historical_var(r, confidence);
                *var = v;
                *cvar = c;
                FNAT_OK
            }
            None => FNAT_ERR_NULL,
        }
    })
}

/// Sample covariance of the columns of a row-major `rows x cols` matrix into `out` (`cols x cols`).
#[no_mangle]
pub unsafe extern "C" fn fnat_covariance(data: *const f64, rows: usize, cols: usize, ddof: usize, out: *mut f64) -> i32 {
    guard(|| {
        if cols == 0 || rows <= ddof {
            return FNAT_ERR_ARG;
        }
        match (input(data, rows * cols), output(out, cols * cols)) {
            (Some(d), Some(o)) => {
                risk::covariance(d, rows, cols, ddof, o);
                FNAT_OK
            }
            _ => FNAT_ERR_NULL,
        }
    })
}

/// Batch Black-Scholes-Merton. Inputs are arrays of length `n` (`is_call`: 1 = call, 0 = put).
/// Any output pointer may be null to skip that quantity.
#[no_mangle]
pub unsafe extern "C" fn fnat_black_scholes_batch(
    spot: *const f64, strike: *const f64, time: *const f64, rate: *const f64, dividend: *const f64, vol: *const f64,
    is_call: *const u8, n: usize,
    price: *mut f64, delta: *mut f64, gamma: *mut f64, vega: *mut f64, theta: *mut f64, rho: *mut f64,
) -> i32 {
    guard(|| {
        let (s, k, t, r, q, v) = match (input(spot, n), input(strike, n), input(time, n), input(rate, n), input(dividend, n), input(vol, n)) {
            (Some(a), Some(b), Some(c), Some(d), Some(e), Some(f)) => (a, b, c, d, e, f),
            _ => return FNAT_ERR_NULL,
        };
        if is_call.is_null() {
            return FNAT_ERR_NULL;
        }
        let calls = slice::from_raw_parts(is_call, n);
        let mut outs = [output(price, n), output(delta, n), output(gamma, n), output(vega, n), output(theta, n), output(rho, n)];
        for i in 0..n {
            let g = pricing::black_scholes(s[i], k[i], t[i], r[i], q[i], v[i], calls[i] != 0);
            let vals = [g.price, g.delta, g.gamma, g.vega, g.theta, g.rho];
            for (o, val) in outs.iter_mut().zip(vals) {
                if let Some(o) = o {
                    o[i] = val;
                }
            }
        }
        FNAT_OK
    })
}

/// Monte Carlo European option price and standard error. `threads == 0` uses all cores;
/// results do not depend on the thread count.
#[no_mangle]
pub unsafe extern "C" fn fnat_mc_european(
    spot: f64, strike: f64, time: f64, rate: f64, dividend: f64, vol: f64, is_call: u8, antithetic: u8,
    paths: u64, seed: u64, threads: u32, price: *mut f64, std_error: *mut f64,
) -> i32 {
    guard(|| {
        if paths < 2 || !(spot > 0.0) || !(strike > 0.0) || !(time > 0.0) || !(vol >= 0.0) {
            return FNAT_ERR_ARG;
        }
        if price.is_null() || std_error.is_null() {
            return FNAT_ERR_NULL;
        }
        let p = pricing::McParams { s: spot, k: strike, t: time, r: rate, q: dividend, vol, call: is_call != 0, antithetic: antithetic != 0 };
        let (pr, se) = pricing::monte_carlo_european(&p, paths as usize, seed, threads as usize);
        *price = pr;
        *std_error = se;
        FNAT_OK
    })
}

/// GBM paths into `out` (row-major, `paths x (steps + 1)`).
#[no_mangle]
pub unsafe extern "C" fn fnat_simulate_gbm(
    s0: f64, mu: f64, sigma: f64, dt: f64, steps: usize, paths: usize, seed: u64, threads: u32, out: *mut f64,
) -> i32 {
    guard(|| {
        if paths == 0 || !(dt > 0.0) || !(sigma >= 0.0) {
            return FNAT_ERR_ARG;
        }
        match output(out, paths * (steps + 1)) {
            Some(o) => {
                pricing::simulate_gbm(o, s0, mu, sigma, dt, steps, paths, seed, threads as usize);
                FNAT_OK
            }
            None => FNAT_ERR_NULL,
        }
    })
}

pub const FNAT_ERR_UNKNOWN: i32 = -4;
pub const FNAT_ERR_BUFFER: i32 = -5;

/// Catalogue indicator `id` (UTF-8, case-insensitive) on bars of length `n` (`day` = calendar day number per bar). `params` are the catalogue parameters
/// in declaration order. Up to `max_out` series are written row-major into `out` (`max_out × n`) and their names,
/// separated by '\n', into `names` (`names_cap` bytes, not terminated). Returns the number of series, or a negative
/// status: `FNAT_ERR_UNKNOWN` for an id the native catalogue lacks, `FNAT_ERR_ARG` where the managed code would
/// throw, `FNAT_ERR_BUFFER` when `out`/`names` are too small.
#[no_mangle]
pub unsafe extern "C" fn fnat_indicator(
    id: *const u8, id_len: usize, open: *const f64, high: *const f64, low: *const f64, close: *const f64, volume: *const f64,
    day: *const f64, n: usize, params: *const f64, nparams: usize, out: *mut f64, max_out: usize, names: *mut u8, names_cap: usize,
) -> i32 {
    guard(|| {
        if id.is_null() || names.is_null() || (nparams > 0 && params.is_null()) {
            return FNAT_ERR_NULL;
        }
        let Ok(id) = std::str::from_utf8(slice::from_raw_parts(id, id_len)) else { return FNAT_ERR_ARG };
        let (Some(o), Some(h), Some(l), Some(c), Some(v), Some(d)) =
            (input(open, n), input(high, n), input(low, n), input(close, n), input(volume, n), input(day, n))
        else {
            return FNAT_ERR_NULL;
        };
        let ps: &[f64] = if nparams == 0 { &[] } else { slice::from_raw_parts(params, nparams) };
        let bars = ta::Ohlcv { open: o, high: h, low: l, close: c, volume: v, day: d };
        let series = match ta::compute(id, &bars, ps) {
            None => return FNAT_ERR_UNKNOWN,
            Some(Err(_)) => return FNAT_ERR_ARG,
            Some(Ok(s)) => s,
        };
        let joined = series.iter().map(|(k, _)| k.as_str()).collect::<Vec<_>>().join("\n");
        if series.len() > max_out || joined.len() > names_cap {
            return FNAT_ERR_BUFFER;
        }
        let Some(dst) = output(out, max_out * n) else { return FNAT_ERR_NULL };
        for (k, (_, values)) in series.iter().enumerate() {
            if values.len() != n {
                return FNAT_ERR_ARG;
            }
            dst[k * n..(k + 1) * n].copy_from_slice(values);
        }
        slice::from_raw_parts_mut(names, joined.len()).copy_from_slice(joined.as_bytes());
        series.len() as i32
    })
}

/// Writes the ids the native catalogue implements, separated by '\n', into `buf`; returns the byte length needed
/// (call with `cap = 0` to size the buffer).
#[no_mangle]
pub unsafe extern "C" fn fnat_indicator_ids(buf: *mut u8, cap: usize) -> i32 {
    guard(|| {
        let joined = ta::ids().join("\n");
        if !buf.is_null() && cap >= joined.len() {
            slice::from_raw_parts_mut(buf, joined.len()).copy_from_slice(joined.as_bytes());
        }
        joined.len() as i32
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ffi_rejects_null_and_bad_args() {
        unsafe {
            let mut o = [0.0; 3];
            assert_eq!(fnat_sma(std::ptr::null(), 3, 2, o.as_mut_ptr()), FNAT_ERR_NULL);
            let x = [1.0, 2.0, 3.0];
            assert_eq!(fnat_sma(x.as_ptr(), 3, 0, o.as_mut_ptr()), FNAT_ERR_ARG);
            assert_eq!(fnat_sma(x.as_ptr(), 3, 2, o.as_mut_ptr()), FNAT_OK);
            assert_eq!(o[2], 2.5);
        }
        assert_eq!(fnat_version(), 800);
    }
}
