//! Port of Finance.TimeSeries.Rolling: O(n) rolling-window statistics aligned with the input, NaN until the
//! window is full, leading NaNs skipped. Same arithmetic order as the managed code so results agree to rounding.

pub fn nans(n: usize) -> Vec<f64> {
    vec![f64::NAN; n]
}

/// Index of the first non-NaN element (or length if none).
pub fn first_valid(x: &[f64]) -> usize {
    x.iter().position(|v| !v.is_nan()).unwrap_or(x.len())
}

/// Rolling sum (Kahan-compensated add and remove).
pub fn sum(x: &[f64], window: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    let (mut acc, mut comp) = (0.0f64, 0.0f64);
    for i in s..x.len() {
        let add = x[i] - if i >= s + window { x[i - window] } else { 0.0 };
        let y = add - comp;
        let t = acc + y;
        comp = (t - acc) - y;
        acc = t;
        if i - s + 1 >= window {
            r[i] = acc;
        }
    }
    r
}

pub fn mean(x: &[f64], window: usize) -> Vec<f64> {
    let mut r = sum(x, window);
    let inv = 1.0 / window as f64;
    for v in r.iter_mut() {
        *v *= inv;
    }
    r
}

/// Rolling variance (Welford add/remove). Requires window > ddof.
pub fn variance(x: &[f64], window: usize, ddof: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    if window <= ddof {
        return r;
    }
    let s = first_valid(x);
    let (mut mean, mut m2) = (0.0f64, 0.0f64);
    let mut count = 0usize;
    let w = window as f64;
    for i in s..x.len() {
        let v = x[i];
        if count < window {
            count += 1;
            let d = v - mean;
            mean += d / count as f64;
            m2 += d * (v - mean);
        } else {
            let old = x[i - window];
            let old_mean = mean;
            mean += (v - old) / w;
            m2 += (v - old) * (v - mean + old - old_mean);
        }
        if count == window {
            r[i] = m2.max(0.0) / (window - ddof) as f64;
        }
    }
    r
}

pub fn std_dev(x: &[f64], window: usize, ddof: usize) -> Vec<f64> {
    let mut r = variance(x, window, ddof);
    for v in r.iter_mut() {
        *v = v.sqrt();
    }
    r
}

/// Index of the rolling extreme (ties keep the most recent index), or -1 during warm-up.
pub fn extreme_index(x: &[f64], window: usize, max: bool) -> Vec<isize> {
    let mut result = vec![-1isize; x.len()];
    let mut dq = vec![0usize; x.len()];
    let (mut head, mut tail) = (0usize, 0usize);
    let s = first_valid(x);
    for i in s..x.len() {
        while head < tail && dq[head] + window <= i {
            head += 1;
        }
        while head < tail && (if max { x[dq[tail - 1]] <= x[i] } else { x[dq[tail - 1]] >= x[i] }) {
            tail -= 1;
        }
        dq[tail] = i;
        tail += 1;
        if i - s + 1 >= window {
            result[i] = dq[head] as isize;
        }
    }
    result
}

pub fn arg_max(x: &[f64], window: usize) -> Vec<isize> {
    extreme_index(x, window, true)
}

pub fn arg_min(x: &[f64], window: usize) -> Vec<isize> {
    extreme_index(x, window, false)
}

fn extreme(x: &[f64], window: usize, max: bool) -> Vec<f64> {
    let idx = extreme_index(x, window, max);
    let mut r = nans(x.len());
    for i in 0..r.len() {
        if idx[i] >= 0 {
            r[i] = x[idx[i] as usize];
        }
    }
    r
}

pub fn max(x: &[f64], window: usize) -> Vec<f64> {
    extreme(x, window, true)
}

pub fn min(x: &[f64], window: usize) -> Vec<f64> {
    extreme(x, window, false)
}

pub fn covariance(x: &[f64], y: &[f64], window: usize, ddof: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x).max(first_valid(y));
    let (mut sx, mut sy, mut sxy) = (0.0f64, 0.0f64, 0.0f64);
    let w = window as f64;
    for i in s..x.len() {
        sx += x[i];
        sy += y[i];
        sxy += x[i] * y[i];
        if i >= s + window {
            sx -= x[i - window];
            sy -= y[i - window];
            sxy -= x[i - window] * y[i - window];
        }
        if i - s + 1 >= window {
            r[i] = (sxy - sx * sy / w) / (window as f64 - ddof as f64);
        }
    }
    r
}

pub fn correlation(x: &[f64], y: &[f64], window: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x).max(first_valid(y));
    let (mut sx, mut sy, mut sxx, mut syy, mut sxy) = (0.0f64, 0.0f64, 0.0f64, 0.0f64, 0.0f64);
    let w = window as f64;
    for i in s..x.len() {
        let (a, b) = (x[i], y[i]);
        sx += a;
        sy += b;
        sxx += a * a;
        syy += b * b;
        sxy += a * b;
        if i >= s + window {
            let (c, d) = (x[i - window], y[i - window]);
            sx -= c;
            sy -= d;
            sxx -= c * c;
            syy -= d * d;
            sxy -= c * d;
        }
        if i - s + 1 >= window {
            let cov = sxy - sx * sy / w;
            let vx = sxx - sx * sx / w;
            let vy = syy - sy * sy / w;
            r[i] = if vx > 0.0 && vy > 0.0 { cov / (vx * vy).sqrt() } else { f64::NAN };
        }
    }
    r
}

pub fn beta(asset: &[f64], benchmark: &[f64], window: usize) -> Vec<f64> {
    let mut cov = covariance(asset, benchmark, window, 1);
    let v = variance(benchmark, window, 1);
    for i in 0..cov.len() {
        cov[i] = if v[i] > 0.0 { cov[i] / v[i] } else { f64::NAN };
    }
    cov
}

pub fn z_score(x: &[f64], window: usize) -> Vec<f64> {
    let m = mean(x, window);
    let sd = std_dev(x, window, 1);
    (0..x.len()).map(|i| if sd[i] > 0.0 { (x[i] - m[i]) / sd[i] } else { f64::NAN }).collect()
}

/// EWMA seeded with the first valid value.
pub fn ewma(x: &[f64], alpha: f64) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    if s == x.len() {
        return r;
    }
    let mut v = x[s];
    r[s] = v;
    for i in s + 1..x.len() {
        v += alpha * (x[i] - v);
        r[i] = v;
    }
    r
}

/// Rolling percentile rank of the latest value within its window, in [0, 100].
pub fn percent_rank(x: &[f64], window: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    if window == 0 {
        return r;
    }
    let mut i = s + window - 1;
    while i < x.len() {
        let mut below = 0usize;
        for k in i + 1 - window..i {
            if x[k] < x[i] {
                below += 1;
            }
        }
        r[i] = 100.0 * below as f64 / (window as f64 - 1.0);
        i += 1;
    }
    r
}

/// Generic rolling apply (O(n·window)).
pub fn apply(x: &[f64], window: usize, f: impl Fn(&[f64]) -> f64) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    if window == 0 {
        return r;
    }
    let mut i = s + window - 1;
    while i < x.len() {
        r[i] = f(&x[i + 1 - window..=i]);
        i += 1;
    }
    r
}

/// Shift by `periods` (positive = lag), filling with NaN.
pub fn shift(x: &[f64], periods: isize) -> Vec<f64> {
    let n = x.len() as isize;
    (0..n)
        .map(|i| {
            let j = i - periods;
            if j >= 0 && j < n { x[j as usize] } else { f64::NAN }
        })
        .collect()
}

/// x[t] - x[t - lag].
pub fn diff(x: &[f64], lag: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    for i in lag..x.len() {
        r[i] = x[i] - x[i - lag];
    }
    r
}
