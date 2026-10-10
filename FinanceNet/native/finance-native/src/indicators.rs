//! Technical indicators and series transforms. Leading positions without a full window are NaN.
//! Every algorithm here is mirrored line for line by `Finance.RustBridge.ManagedKernels`.

/// Simple moving average via a sliding sum.
pub fn sma(x: &[f64], period: usize, out: &mut [f64]) {
    let n = x.len();
    let mut sum = 0.0;
    for i in 0..n {
        sum += x[i];
        if i >= period {
            sum -= x[i - period];
        }
        out[i] = if i + 1 >= period { sum / period as f64 } else { f64::NAN };
    }
}

/// Exponential moving average, alpha = 2 / (period + 1), seeded with the SMA of the first window.
pub fn ema(x: &[f64], period: usize, out: &mut [f64]) {
    let n = x.len();
    let alpha = 2.0 / (period as f64 + 1.0);
    let mut e = 0.0;
    for i in 0..n {
        if i + 1 < period {
            e += x[i];
            out[i] = f64::NAN;
        } else if i + 1 == period {
            e = (e + x[i]) / period as f64;
            out[i] = e;
        } else {
            e += alpha * (x[i] - e);
            out[i] = e;
        }
    }
}

#[inline]
fn rsi_value(gain: f64, loss: f64) -> f64 {
    if loss == 0.0 {
        if gain == 0.0 { 50.0 } else { 100.0 }
    } else {
        100.0 - 100.0 / (1.0 + gain / loss)
    }
}

/// Wilder's RSI. The first value appears at index `period`.
pub fn rsi(x: &[f64], period: usize, out: &mut [f64]) {
    let n = x.len();
    for v in out.iter_mut() {
        *v = f64::NAN;
    }
    if n <= period {
        return;
    }
    let (mut g, mut l) = (0.0, 0.0);
    for i in 1..=period {
        let d = x[i] - x[i - 1];
        if d > 0.0 { g += d } else { l -= d }
    }
    let p = period as f64;
    g /= p;
    l /= p;
    out[period] = rsi_value(g, l);
    for i in period + 1..n {
        let d = x[i] - x[i - 1];
        let (gi, li) = if d > 0.0 { (d, 0.0) } else { (0.0, -d) };
        g = (g * (p - 1.0) + gi) / p;
        l = (l * (p - 1.0) + li) / p;
        out[i] = rsi_value(g, l);
    }
}

/// Rolling standard deviation (two-pass per window; exact, no cancellation drift).
pub fn rolling_std(x: &[f64], period: usize, ddof: usize, out: &mut [f64]) {
    let n = x.len();
    for i in 0..n {
        if i + 1 < period {
            out[i] = f64::NAN;
            continue;
        }
        let w = &x[i + 1 - period..=i];
        let mut s = 0.0;
        for &v in w {
            s += v;
        }
        let m = s / period as f64;
        let mut ss = 0.0;
        for &v in w {
            let d = v - m;
            ss += d * d;
        }
        out[i] = (ss / (period - ddof) as f64).sqrt();
    }
}

/// Bollinger bands: middle = window mean, bands = middle ± k · population std.
pub fn bollinger(x: &[f64], period: usize, k: f64, mid: &mut [f64], upper: &mut [f64], lower: &mut [f64]) {
    let n = x.len();
    for i in 0..n {
        if i + 1 < period {
            mid[i] = f64::NAN;
            upper[i] = f64::NAN;
            lower[i] = f64::NAN;
            continue;
        }
        let w = &x[i + 1 - period..=i];
        let mut s = 0.0;
        for &v in w {
            s += v;
        }
        let m = s / period as f64;
        let mut ss = 0.0;
        for &v in w {
            let d = v - m;
            ss += d * d;
        }
        let sd = (ss / period as f64).sqrt();
        mid[i] = m;
        upper[i] = m + k * sd;
        lower[i] = m - k * sd;
    }
}

/// Log returns; `out.len() == x.len() - 1`.
pub fn log_returns(x: &[f64], out: &mut [f64]) {
    for i in 0..x.len() - 1 {
        out[i] = (x[i + 1] / x[i]).ln();
    }
}

/// Maximum drawdown as a positive fraction, with the peak and trough indices.
pub fn max_drawdown(x: &[f64]) -> (f64, usize, usize) {
    let (mut peak, mut peak_i) = (x[0], 0usize);
    let (mut mdd, mut best_peak, mut best_trough) = (0.0, 0usize, 0usize);
    for (i, &v) in x.iter().enumerate() {
        if v > peak {
            peak = v;
            peak_i = i;
        }
        let dd = if peak != 0.0 { (peak - v) / peak } else { 0.0 };
        if dd > mdd {
            mdd = dd;
            best_peak = peak_i;
            best_trough = i;
        }
    }
    (mdd, best_peak, best_trough)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sma_matches_definition() {
        let x = [1.0, 2.0, 3.0, 4.0, 5.0];
        let mut o = [0.0; 5];
        sma(&x, 3, &mut o);
        assert!(o[1].is_nan());
        assert_eq!(o[2], 2.0);
        assert_eq!(o[4], 4.0);
    }

    #[test]
    fn ema_seeded_with_sma() {
        let x = [1.0, 2.0, 3.0, 4.0];
        let mut o = [0.0; 4];
        ema(&x, 3, &mut o);
        assert_eq!(o[2], 2.0);
        assert!((o[3] - 3.0).abs() < 1e-15); // 2 + 0.5 * (4 - 2)
    }

    #[test]
    fn rsi_monotone_up_is_100() {
        let x: Vec<f64> = (0..30).map(|i| i as f64).collect();
        let mut o = vec![0.0; 30];
        rsi(&x, 14, &mut o);
        assert!(o[13].is_nan());
        assert_eq!(o[14], 100.0);
        assert_eq!(o[29], 100.0);
    }

    #[test]
    fn drawdown() {
        let x = [100.0, 120.0, 90.0, 110.0, 60.0, 130.0];
        let (d, p, t) = max_drawdown(&x);
        assert!((d - 0.5).abs() < 1e-15);
        assert_eq!((p, t), (1, 4));
    }

    #[test]
    fn rolling_std_constant_is_zero() {
        let x = [5.0; 10];
        let mut o = [0.0; 10];
        rolling_std(&x, 4, 1, &mut o);
        assert_eq!(o[9], 0.0);
    }
}
