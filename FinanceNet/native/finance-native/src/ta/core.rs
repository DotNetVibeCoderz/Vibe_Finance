//! Core moving averages and true range shared by every indicator module (ports of Ta.Sma/Ema/EmaAlpha/Rma/Wma,
//! Ta.TrueRange and Ta.Atr).

use super::rolling::{first_valid, nans};

pub fn sma(x: &[f64], period: usize) -> Vec<f64> {
    super::rolling::mean(x, period)
}

/// EMA with an explicit smoothing factor, seeded with the SMA of the first `period` valid values.
pub fn ema_alpha(x: &[f64], period: usize, alpha: f64) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    if period == 0 || x.len() - s < period {
        return r;
    }
    let mut v = 0.0;
    for i in s..s + period {
        v += x[i];
    }
    v /= period as f64;
    r[s + period - 1] = v;
    for i in s + period..x.len() {
        v += alpha * (x[i] - v);
        r[i] = v;
    }
    r
}

/// EMA, α = 2/(period+1), SMA-seeded (TA-Lib convention).
pub fn ema(x: &[f64], period: usize) -> Vec<f64> {
    ema_alpha(x, period, 2.0 / (period as f64 + 1.0))
}

/// Wilder smoothing, α = 1/period.
pub fn rma(x: &[f64], period: usize) -> Vec<f64> {
    ema_alpha(x, period, 1.0 / period as f64)
}

/// Linearly weighted moving average (weights 1..period), O(n).
pub fn wma(x: &[f64], period: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    if period == 0 || x.len() - s < period {
        return r;
    }
    let p = period as f64;
    let denom = (period * (period + 1)) as f64 / 2.0;
    let (mut weighted, mut plain) = (0.0f64, 0.0f64);
    for i in 0..period {
        weighted += (i + 1) as f64 * x[s + i];
        plain += x[s + i];
    }
    r[s + period - 1] = weighted / denom;
    for i in s + period..x.len() {
        weighted += p * x[i] - plain;
        plain += x[i] - x[i - period];
        r[i] = weighted / denom;
    }
    r
}

/// True range; element 0 is H - L.
pub fn true_range(high: &[f64], low: &[f64], close: &[f64]) -> Vec<f64> {
    let n = close.len();
    let mut r = vec![0.0; n];
    if n == 0 {
        return r;
    }
    r[0] = high[0] - low[0];
    for i in 1..n {
        r[i] = net_max(high[i] - low[i], net_max((high[i] - close[i - 1]).abs(), (low[i] - close[i - 1]).abs()));
    }
    r
}

/// Average true range (Wilder); the first value averages TR[1..period].
pub fn atr(high: &[f64], low: &[f64], close: &[f64], period: usize) -> Vec<f64> {
    let mut tr = true_range(high, low, close);
    if !tr.is_empty() {
        tr[0] = f64::NAN;
    }
    rma(&tr, period)
}

/// .NET Math.Max semantics: NaN if either argument is NaN.
#[inline]
pub fn net_max(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() { f64::NAN } else { a.max(b) }
}

/// .NET Math.Min semantics: NaN if either argument is NaN.
#[inline]
pub fn net_min(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() { f64::NAN } else { a.min(b) }
}

/// .NET Math.Round (banker's rounding, MidpointRounding.ToEven).
#[inline]
pub fn net_round(v: f64) -> f64 {
    v.round_ties_even()
}
