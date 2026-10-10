//! Risk kernels: historical VaR/CVaR and the sample covariance matrix.

/// Index of the VaR order statistic: `ceil((1 - confidence) * n) - 1`, clamped to `[0, n)`.
/// The 1e-9 slack stops representation error (0.05 * 100 = 5.000000000000004) adding a point.
#[inline]
pub fn tail_index(n: usize, confidence: f64) -> usize {
    let k = ((1.0 - confidence) * n as f64 - 1e-9).ceil() as i64 - 1;
    k.clamp(0, n as i64 - 1) as usize
}

/// Historical VaR and CVaR of a return series, both reported as positive losses.
/// VaR is minus the k-th smallest return; CVaR is minus the mean of the k+1 smallest.
pub fn historical_var(returns: &[f64], confidence: f64) -> (f64, f64) {
    let n = returns.len();
    let k = tail_index(n, confidence);
    let mut buf = returns.to_vec();
    buf.select_nth_unstable_by(k, |a, b| a.total_cmp(b));
    let var = -buf[k];
    let mut s = 0.0;
    for &v in &buf[..=k] {
        s += v;
    }
    (var, -s / (k + 1) as f64)
}

/// Sample covariance of the columns of a row-major `rows x cols` matrix.
pub fn covariance(data: &[f64], rows: usize, cols: usize, ddof: usize, out: &mut [f64]) {
    let mut means = vec![0.0; cols];
    for r in 0..rows {
        let row = &data[r * cols..(r + 1) * cols];
        for c in 0..cols {
            means[c] += row[c];
        }
    }
    for m in means.iter_mut() {
        *m /= rows as f64;
    }
    for v in out.iter_mut() {
        *v = 0.0;
    }
    let mut centered = vec![0.0; cols];
    for r in 0..rows {
        let row = &data[r * cols..(r + 1) * cols];
        for c in 0..cols {
            centered[c] = row[c] - means[c];
        }
        for a in 0..cols {
            let ca = centered[a];
            let dst = &mut out[a * cols..(a + 1) * cols];
            for b in a..cols {
                dst[b] += ca * centered[b];
            }
        }
    }
    let scale = 1.0 / (rows - ddof) as f64;
    for a in 0..cols {
        for b in a..cols {
            let v = out[a * cols + b] * scale;
            out[a * cols + b] = v;
            out[b * cols + a] = v;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn var_of_uniform_grid() {
        let r: Vec<f64> = (0..100).map(|i| (i as f64 - 50.0) / 1000.0).collect();
        let (var, cvar) = historical_var(&r, 0.95);
        // k = ceil(5) - 1 = 4 -> 5th smallest = -0.046
        assert!((var - 0.046).abs() < 1e-15);
        assert!((cvar - 0.048).abs() < 1e-15);
    }

    #[test]
    fn covariance_two_columns() {
        let d = [1.0, 2.0, 2.0, 4.0, 3.0, 6.0];
        let mut o = [0.0; 4];
        covariance(&d, 3, 2, 1, &mut o);
        assert!((o[0] - 1.0).abs() < 1e-15);
        assert!((o[1] - 2.0).abs() < 1e-15);
        assert!((o[3] - 4.0).abs() < 1e-15);
    }
}
