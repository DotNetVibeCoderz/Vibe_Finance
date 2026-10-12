//! Catalogue entries: see ta/mod.rs for the module layout.
//!
//! Second wave: momentum, volatility, volume, statistics and price transforms (ports of Indicators/Extended.cs and
//! the helpers it calls).

use super::core::{atr, ema, ema_alpha, net_max, net_min, rma, sma, true_range};
use super::rolling::{self, first_valid, nans};
use super::{many, n, one, p, Ohlcv, Output};

/// Ids implemented by this module.
pub const IDS: &[&str] = &[
    "PLUS_DI", "MINUS_DI", "PLUS_DM", "MINUS_DM", "ROCP", "ROCR100", "PMO", "RMI", "SMI", "INERTIA", "PSL", "IMI",
    "PGO", "CFO", "BIAS", "DSS", "WAVETREND", "KDJ", "WAD", "SQUEEZE", "YANGZHANG", "EWMAVOL", "RVOLIDX", "STARC",
    "ACCBANDS", "SEB", "ADR", "TMF", "BWMFI", "VZO", "RVOL", "VWMACD", "VPCI", "VPROFILE", "TPO", "RSQ", "STDERR",
    "MAD", "MAX", "MIN", "MAXINDEX", "MININDEX", "SUM", "ENTROPY", "AUTOCORR", "CV", "QUANTILE", "SHARPE",
    "DRAWDOWN", "LOGRET", "PCTRET",
];

pub fn compute(id: &str, x: &Ohlcv, q: &[f64]) -> Output {
    let (o, h, l, c, v) = (x.open, x.high, x.low, x.close, x.volume);
    let r = match id {
        // Momentum
        "PLUS_DI" => one(id, dmi(h, l, c, n(q, 0)).0),
        "MINUS_DI" => one(id, dmi(h, l, c, n(q, 0)).1),
        "PLUS_DM" => one(id, directional_movement(h, l, n(q, 0)).0),
        "MINUS_DM" => one(id, directional_movement(h, l, n(q, 0)).1),
        "ROCP" => one(id, roc_fraction(c, n(q, 0))),
        "ROCR100" => one(id, roc_ratio(c, n(q, 0)).into_iter().map(|v| 100.0 * v).collect()),
        "PMO" => {
            let (a, b) = pmo(c, n(q, 0), n(q, 1), n(q, 2));
            many(vec![("PMO", a), ("Signal", b)])
        }
        "RMI" => one(id, rmi(c, n(q, 0), n(q, 1))),
        "SMI" => {
            let (a, b) = smi(h, l, c, n(q, 0), n(q, 1), n(q, 2), n(q, 3));
            many(vec![("SMI", a), ("Signal", b)])
        }
        "INERTIA" => one(id, linear_regression(&relative_volatility_index(c, 10, n(q, 1)), n(q, 0)).value),
        "PSL" => one(id, psychological_line(c, n(q, 0))),
        "IMI" => one(id, intraday_momentum(o, c, n(q, 0))),
        "PGO" => one(id, pretty_good_oscillator(h, l, c, n(q, 0))),
        "CFO" => one(id, forecast_oscillator(c, n(q, 0))),
        "BIAS" => one(id, bias(c, n(q, 0))),
        "DSS" => one(id, double_smoothed_stochastic(h, l, c, n(q, 0), n(q, 1))),
        "WAVETREND" => {
            let (a, b) = wave_trend(h, l, c, n(q, 0), n(q, 1));
            many(vec![("WT1", a), ("WT2", b)])
        }
        "KDJ" => {
            let (k, d) = stochastic(h, l, c, n(q, 0), n(q, 1), n(q, 2));
            let j = (0..k.len()).map(|i| 3.0 * k[i] - 2.0 * d[i]).collect();
            many(vec![("K", k), ("D", d), ("J", j)])
        }
        "WAD" => one(id, williams_ad(h, l, c)),
        "SQUEEZE" => {
            let (s, m) = squeeze(h, l, c, n(q, 0), p(q, 1), p(q, 2));
            many(vec![("Squeeze", s), ("Momentum", m)])
        }
        // Volatility
        "YANGZHANG" => match yang_zhang(o, h, l, c, n(q, 0), 252.0) {
            Ok(r) => one(id, r),
            Err(e) => Err(e),
        },
        "EWMAVOL" => one(id, ewma_volatility(c, p(q, 0), 252.0)),
        "RVOLIDX" => one(id, relative_volatility_index(c, n(q, 0), n(q, 1))),
        "STARC" => {
            let (period, atr_p, k) = (n(q, 0), n(q, 1), p(q, 2));
            let mid = sma(c, period);
            let a = atr(h, l, c, atr_p);
            let up = (0..mid.len()).map(|i| mid[i] + k * a[i]).collect();
            let lo = (0..mid.len()).map(|i| mid[i] - k * a[i]).collect();
            many(vec![("Upper", up), ("Middle", mid), ("Lower", lo)])
        }
        "ACCBANDS" => {
            let (period, factor) = (n(q, 0), p(q, 1));
            let len = c.len();
            let mut u = vec![0.0; len];
            let mut lw = vec![0.0; len];
            for i in 0..len {
                let w = if h[i] + l[i] != 0.0 { (h[i] - l[i]) / (h[i] + l[i]) } else { 0.0 };
                u[i] = h[i] * (1.0 + factor * w);
                lw[i] = l[i] * (1.0 - factor * w);
            }
            many(vec![("Upper", sma(&u, period)), ("Middle", sma(c, period)), ("Lower", sma(&lw, period))])
        }
        "SEB" => {
            let (period, k) = (n(q, 0), p(q, 1));
            let mid = linear_regression(c, period).value;
            let se = standard_error(c, period);
            let up = (0..mid.len()).map(|i| mid[i] + k * se[i]).collect();
            let lo = (0..mid.len()).map(|i| mid[i] - k * se[i]).collect();
            many(vec![("Upper", up), ("Middle", mid), ("Lower", lo)])
        }
        "ADR" => {
            let rng: Vec<f64> = (0..h.len()).map(|i| h[i] - l[i]).collect();
            one(id, sma(&rng, n(q, 0)))
        }
        // Volume
        "TMF" => one(id, twiggs_money_flow(h, l, c, v, n(q, 0))),
        "BWMFI" => {
            let mut r = nans(h.len());
            for i in 0..r.len() {
                if v[i] > 0.0 {
                    r[i] = (h[i] - l[i]) / v[i];
                }
            }
            one(id, r)
        }
        "VZO" => match volume_zone_oscillator(c, v, n(q, 0)) {
            Ok(r) => one(id, r),
            Err(e) => Err(e),
        },
        "RVOL" => {
            let avg = rolling::shift(&rolling::mean(v, n(q, 0)), 1);
            let mut r = nans(v.len());
            for i in 0..r.len() {
                if avg[i] > 0.0 {
                    r[i] = v[i] / avg[i];
                }
            }
            one(id, r)
        }
        "VWMACD" => {
            let f = vwma(c, v, n(q, 0));
            let s = vwma(c, v, n(q, 1));
            let m: Vec<f64> = (0..f.len()).map(|i| f[i] - s[i]).collect();
            let sig = ema(&m, n(q, 2));
            let hist = (0..m.len()).map(|i| m[i] - sig[i]).collect();
            many(vec![("MACD", m), ("Signal", sig), ("Histogram", hist)])
        }
        "VPCI" => one(id, vpci(c, v, n(q, 0), n(q, 1))),
        "VPROFILE" => {
            let (a, b, d) = profile(h, l, Some(v), n(q, 0), n(q, 1), 0.70);
            many(vec![("POC", a), ("VAH", b), ("VAL", d)])
        }
        "TPO" => {
            let (a, b, d) = profile(h, l, None, n(q, 0), n(q, 1), 0.70);
            many(vec![("POC", a), ("VAH", b), ("VAL", d)])
        }
        // Statistics
        "RSQ" => {
            let t: Vec<f64> = (0..c.len()).map(|i| i as f64).collect();
            let mut r = rolling::correlation(c, &t, n(q, 0));
            for x in r.iter_mut() {
                *x *= *x;
            }
            one(id, r)
        }
        "STDERR" => one(id, standard_error(c, n(q, 0))),
        "MAD" => one(id, mean_absolute_deviation(c, n(q, 0))),
        "MAX" => one(id, rolling::max(c, n(q, 0))),
        "MIN" => one(id, rolling::min(c, n(q, 0))),
        "MAXINDEX" => one(id, bars_since_extreme(c, n(q, 0), true)),
        "MININDEX" => one(id, bars_since_extreme(c, n(q, 0), false)),
        "SUM" => one(id, rolling::sum(c, n(q, 0))),
        "ENTROPY" => one(id, entropy(c, n(q, 0))),
        "AUTOCORR" => {
            let ret = log_return(c);
            let lagged = rolling::shift(&ret, n(q, 1) as isize);
            one(id, rolling::correlation(&ret, &lagged, n(q, 0)))
        }
        "CV" => {
            let period = n(q, 0);
            if period <= 1 {
                return Some(Err("window must exceed ddof".into()));
            }
            let m = rolling::mean(c, period);
            let sd = std_dev(c, period, 1);
            let mut r = nans(c.len());
            for i in 0..r.len() {
                if m[i] != 0.0 && !m[i].is_nan() {
                    r[i] = sd[i] / m[i];
                }
            }
            one(id, r)
        }
        "QUANTILE" => one(id, rolling_quantile(c, n(q, 0), p(q, 1))),
        "SHARPE" => {
            let period = n(q, 0);
            if period <= 1 {
                return Some(Err("window must exceed ddof".into()));
            }
            let ret = log_return(c);
            let m = rolling::mean(&ret, period);
            let sd = std_dev(&ret, period, 1);
            let mut r = nans(c.len());
            for i in 0..r.len() {
                if sd[i] > 0.0 {
                    r[i] = m[i] / sd[i] * 252f64.sqrt();
                }
            }
            one(id, r)
        }
        "DRAWDOWN" => {
            let mut r = nans(c.len());
            let mut peak = f64::MIN;
            for i in 0..r.len() {
                if c[i].is_nan() {
                    continue;
                }
                peak = net_max(peak, c[i]);
                r[i] = if peak > 0.0 { 100.0 * (c[i] / peak - 1.0) } else { 0.0 };
            }
            one(id, r)
        }
        // Price transforms
        "LOGRET" => one(id, log_return(c)),
        "PCTRET" => one(id, roc(c, 1)),
        _ => return None,
    };
    Some(r)
}

// ---------------- helpers ----------------

/// .NET Math.Sign (throws on NaN).
fn sign(v: f64) -> Result<f64, String> {
    if v.is_nan() {
        Err("Math.Sign: NaN".into())
    } else if v > 0.0 {
        Ok(1.0)
    } else if v < 0.0 {
        Ok(-1.0)
    } else {
        Ok(0.0)
    }
}

/// Rolling.Variance with .NET Math.Max(0, m2) semantics (NaN propagates).
fn variance(x: &[f64], window: usize, ddof: usize) -> Vec<f64> {
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
            r[i] = net_max(0.0, m2) / (window - ddof) as f64;
        }
    }
    r
}

fn std_dev(x: &[f64], window: usize, ddof: usize) -> Vec<f64> {
    let mut r = variance(x, window, ddof);
    for v in r.iter_mut() {
        *v = v.sqrt();
    }
    r
}

fn log_return(c: &[f64]) -> Vec<f64> {
    let mut r = nans(c.len());
    for i in 1..c.len() {
        r[i] = (c[i] / c[i - 1]).ln();
    }
    r
}

fn roc(x: &[f64], period: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    for i in period..x.len() {
        r[i] = 100.0 * (x[i] / x[i - period] - 1.0);
    }
    r
}

fn roc_ratio(x: &[f64], period: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    for i in period..x.len() {
        r[i] = x[i] / x[i - period];
    }
    r
}

fn roc_fraction(x: &[f64], period: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    for i in period..x.len() {
        r[i] = if x[i - period] != 0.0 { (x[i] - x[i - period]) / x[i - period] } else { f64::NAN };
    }
    r
}

fn vwma(price: &[f64], volume: &[f64], period: usize) -> Vec<f64> {
    let pv: Vec<f64> = (0..price.len()).map(|i| price[i] * volume[i]).collect();
    let mut num = rolling::sum(&pv, period);
    let den = rolling::sum(volume, period);
    for i in 0..num.len() {
        num[i] = if den[i] > 0.0 { num[i] / den[i] } else { f64::NAN };
    }
    num
}

/// Ta.Dmi: (+DI, −DI).
fn dmi(high: &[f64], low: &[f64], close: &[f64], period: usize) -> (Vec<f64>, Vec<f64>) {
    let len = close.len();
    let mut plus = nans(len);
    let mut minus = nans(len);
    if len <= period {
        return (plus, minus);
    }
    let pf = period as f64;
    let (mut s_tr, mut s_plus, mut s_minus) = (0.0f64, 0.0f64, 0.0f64);
    for i in 1..len {
        let up = high[i] - high[i - 1];
        let down = low[i - 1] - low[i];
        let pdm = if up > down && up > 0.0 { up } else { 0.0 };
        let mdm = if down > up && down > 0.0 { down } else { 0.0 };
        let tr = net_max(
            high[i] - low[i],
            net_max((high[i] - close[i - 1]).abs(), (low[i] - close[i - 1]).abs()),
        );
        if i <= period {
            s_tr += tr;
            s_plus += pdm;
            s_minus += mdm;
            if i < period {
                continue;
            }
        } else {
            s_tr = s_tr - s_tr / pf + tr;
            s_plus = s_plus - s_plus / pf + pdm;
            s_minus = s_minus - s_minus / pf + mdm;
        }
        plus[i] = if s_tr > 0.0 { 100.0 * s_plus / s_tr } else { 0.0 };
        minus[i] = if s_tr > 0.0 { 100.0 * s_minus / s_tr } else { 0.0 };
    }
    (plus, minus)
}

fn directional_movement(high: &[f64], low: &[f64], period: usize) -> (Vec<f64>, Vec<f64>) {
    let len = high.len();
    let mut pv = nans(len);
    let mut mv = nans(len);
    let pf = period as f64;
    let (mut sp, mut sm) = (0.0f64, 0.0f64);
    for i in 1..len {
        let up = high[i] - high[i - 1];
        let down = low[i - 1] - low[i];
        let pdm = if up > down && up > 0.0 { up } else { 0.0 };
        let mdm = if down > up && down > 0.0 { down } else { 0.0 };
        if i <= period {
            sp += pdm;
            sm += mdm;
            if i < period {
                continue;
            }
        } else {
            sp = sp - sp / pf + pdm;
            sm = sm - sm / pf + mdm;
        }
        pv[i] = sp;
        mv[i] = sm;
    }
    (pv, mv)
}

fn pmo(close: &[f64], first: usize, second: usize, signal: usize) -> (Vec<f64>, Vec<f64>) {
    let r = roc(close, 1);
    let mut s1 = ema_alpha(&r, 1, 2.0 / first as f64);
    for v in s1.iter_mut() {
        *v *= 10.0;
    }
    let pm = ema_alpha(&s1, 1, 2.0 / second as f64);
    let sig = ema(&pm, signal);
    (pm, sig)
}

fn rmi(close: &[f64], period: usize, momentum: usize) -> Vec<f64> {
    let len = close.len();
    let mut up = nans(len);
    let mut dn = nans(len);
    for i in momentum..len {
        let d = close[i] - close[i - momentum];
        up[i] = net_max(d, 0.0);
        dn[i] = net_max(-d, 0.0);
    }
    let au = rma(&up, period);
    let ad = rma(&dn, period);
    let mut r = nans(len);
    for i in 0..len {
        if !au[i].is_nan() {
            r[i] = if au[i] + ad[i] > 0.0 { 100.0 * au[i] / (au[i] + ad[i]) } else { 50.0 };
        }
    }
    r
}

fn smi(
    high: &[f64],
    low: &[f64],
    close: &[f64],
    period: usize,
    smooth1: usize,
    smooth2: usize,
    signal: usize,
) -> (Vec<f64>, Vec<f64>) {
    let len = close.len();
    let hh = rolling::max(high, period);
    let ll = rolling::min(low, period);
    let mut rel = nans(len);
    let mut rng = nans(len);
    for i in 0..len {
        if hh[i].is_nan() {
            continue;
        }
        rel[i] = close[i] - (hh[i] + ll[i]) / 2.0;
        rng[i] = hh[i] - ll[i];
    }
    let num = ema(&ema(&rel, smooth1), smooth2);
    let den = ema(&ema(&rng, smooth1), smooth2);
    let mut s = nans(len);
    for i in 0..len {
        if den[i] > 0.0 {
            s[i] = 100.0 * num[i] / (0.5 * den[i]);
        }
    }
    let sig = ema(&s, signal);
    (s, sig)
}

fn relative_volatility_index(close: &[f64], std_period: usize, period: usize) -> Vec<f64> {
    let len = close.len();
    let sd = std_dev(close, std_period, 0);
    let mut up = nans(len);
    let mut dn = nans(len);
    for i in 1..len {
        if sd[i].is_nan() {
            continue;
        }
        up[i] = if close[i] > close[i - 1] { sd[i] } else { 0.0 };
        dn[i] = if close[i] < close[i - 1] { sd[i] } else { 0.0 };
    }
    let u = ema(&up, period);
    let d = ema(&dn, period);
    let mut r = nans(len);
    for i in 0..len {
        if !u[i].is_nan() {
            r[i] = if u[i] + d[i] > 0.0 { 100.0 * u[i] / (u[i] + d[i]) } else { 50.0 };
        }
    }
    r
}

struct LinReg {
    value: Vec<f64>,
    slope: Vec<f64>,
    intercept: Vec<f64>,
    forecast: Vec<f64>,
}

/// Ta.LinearRegression (angle omitted).
fn linear_regression(x: &[f64], period: usize) -> LinReg {
    let len = x.len();
    let mut value = nans(len);
    let mut slope = nans(len);
    let mut icpt = nans(len);
    let mut tsf = nans(len);
    let s = first_valid(x);
    if len - s < period {
        return LinReg { value, slope, intercept: icpt, forecast: tsf };
    }
    let pf = period as f64;
    let sum_x = (period * (period - 1)) as f64 / 2.0;
    let sum_x2 = (pf - 1.0) * pf * (2.0 * pf - 1.0) / 6.0;
    let div = sum_x * sum_x - pf * sum_x2;
    let (mut sum_y, mut sum_xy) = (0.0f64, 0.0f64);
    for k in 0..period {
        sum_y += x[s + k];
        sum_xy += k as f64 * x[s + k];
    }
    for i in s + period - 1..len {
        if i > s + period - 1 {
            let old = x[i - period];
            sum_xy = sum_xy - (sum_y - old) + (period - 1) as f64 * x[i];
            sum_y += x[i] - old;
        }
        let m = (pf * sum_xy - sum_x * sum_y) / -div;
        let b = (sum_y - m * sum_x) / pf;
        slope[i] = m;
        icpt[i] = b;
        value[i] = b + m * (period - 1) as f64;
        tsf[i] = b + m * pf;
    }
    LinReg { value, slope, intercept: icpt, forecast: tsf }
}

fn psychological_line(close: &[f64], period: usize) -> Vec<f64> {
    let len = close.len();
    let mut up = nans(len);
    for i in 1..len {
        up[i] = if close[i] > close[i - 1] { 100.0 } else { 0.0 };
    }
    rolling::mean(&up, period)
}

fn intraday_momentum(open: &[f64], close: &[f64], period: usize) -> Vec<f64> {
    let len = close.len();
    let mut g = vec![0.0; len];
    let mut lv = vec![0.0; len];
    for i in 0..len {
        let d = close[i] - open[i];
        g[i] = net_max(d, 0.0);
        lv[i] = net_max(-d, 0.0);
    }
    let sg = rolling::sum(&g, period);
    let sl = rolling::sum(&lv, period);
    let mut r = nans(len);
    for i in 0..len {
        if !sg[i].is_nan() {
            r[i] = if sg[i] + sl[i] > 0.0 { 100.0 * sg[i] / (sg[i] + sl[i]) } else { 50.0 };
        }
    }
    r
}

fn pretty_good_oscillator(high: &[f64], low: &[f64], close: &[f64], period: usize) -> Vec<f64> {
    let m = sma(close, period);
    let a = ema(&true_range(high, low, close), period);
    let mut r = nans(close.len());
    for i in 0..r.len() {
        if a[i] > 0.0 {
            r[i] = (close[i] - m[i]) / a[i];
        }
    }
    r
}

fn forecast_oscillator(close: &[f64], period: usize) -> Vec<f64> {
    let tsf = linear_regression(close, period).forecast;
    let mut r = nans(close.len());
    for i in 0..r.len() {
        if close[i] != 0.0 && !tsf[i].is_nan() {
            r[i] = 100.0 * (close[i] - tsf[i]) / close[i];
        }
    }
    r
}

fn bias(close: &[f64], period: usize) -> Vec<f64> {
    let m = sma(close, period);
    let mut r = nans(close.len());
    for i in 0..r.len() {
        if m[i] != 0.0 && !m[i].is_nan() {
            r[i] = 100.0 * (close[i] - m[i]) / m[i];
        }
    }
    r
}

fn double_smoothed_stochastic(high: &[f64], low: &[f64], close: &[f64], period: usize, smooth: usize) -> Vec<f64> {
    let len = close.len();
    let hh = rolling::max(high, period);
    let ll = rolling::min(low, period);
    let mut k1 = nans(len);
    for i in 0..len {
        if hh[i] > ll[i] {
            k1[i] = 100.0 * (close[i] - ll[i]) / (hh[i] - ll[i]);
        } else if !hh[i].is_nan() {
            k1[i] = 50.0;
        }
    }
    let e1 = ema(&k1, smooth);
    let h2 = rolling::max(&e1, period);
    let l2 = rolling::min(&e1, period);
    let mut k2 = nans(len);
    for i in 0..len {
        if h2[i] > l2[i] {
            k2[i] = 100.0 * (e1[i] - l2[i]) / (h2[i] - l2[i]);
        } else if !h2[i].is_nan() {
            k2[i] = 50.0;
        }
    }
    ema(&k2, smooth)
}

fn wave_trend(high: &[f64], low: &[f64], close: &[f64], channel: usize, average: usize) -> (Vec<f64>, Vec<f64>) {
    let len = close.len();
    let ap: Vec<f64> = (0..len).map(|i| (high[i] + low[i] + close[i]) / 3.0).collect();
    let esa = ema(&ap, channel);
    let mut dev = nans(len);
    for i in 0..len {
        if !esa[i].is_nan() {
            dev[i] = (ap[i] - esa[i]).abs();
        }
    }
    let d = ema(&dev, channel);
    let mut ci = nans(len);
    for i in 0..len {
        if d[i] > 0.0 {
            ci[i] = (ap[i] - esa[i]) / (0.015 * d[i]);
        }
    }
    let wt1 = ema(&ci, average);
    let wt2 = sma(&wt1, 4);
    (wt1, wt2)
}

fn stochastic(
    high: &[f64],
    low: &[f64],
    close: &[f64],
    k_period: usize,
    k_smooth: usize,
    d_period: usize,
) -> (Vec<f64>, Vec<f64>) {
    let hh = rolling::max(high, k_period);
    let ll = rolling::min(low, k_period);
    let mut raw = nans(close.len());
    for i in 0..raw.len() {
        let range = hh[i] - ll[i];
        if !range.is_nan() {
            raw[i] = if range > 0.0 { 100.0 * (close[i] - ll[i]) / range } else { 50.0 };
        }
    }
    let k = if k_smooth > 1 { sma(&raw, k_smooth) } else { raw };
    let d = sma(&k, d_period);
    (k, d)
}

fn williams_ad(high: &[f64], low: &[f64], close: &[f64]) -> Vec<f64> {
    let len = close.len();
    let mut r = nans(len);
    if len == 0 {
        return r;
    }
    let mut acc = 0.0;
    r[0] = 0.0;
    for i in 1..len {
        let trh = net_max(high[i], close[i - 1]);
        let trl = net_min(low[i], close[i - 1]);
        if close[i] > close[i - 1] {
            acc += close[i] - trl;
        } else if close[i] < close[i - 1] {
            acc += close[i] - trh;
        }
        r[i] = acc;
    }
    r
}

fn squeeze(high: &[f64], low: &[f64], close: &[f64], period: usize, bb_k: f64, kc_k: f64) -> (Vec<f64>, Vec<f64>) {
    let len = close.len();
    let mid = sma(close, period);
    let sd = std_dev(close, period, 0);
    let a = rma(&true_range(high, low, close), period);
    let hh = rolling::max(high, period);
    let ll = rolling::min(low, period);
    let mut sq = nans(len);
    let mut delta = nans(len);
    for i in 0..len {
        if mid[i].is_nan() || a[i].is_nan() {
            continue;
        }
        let upper = mid[i] + bb_k * sd[i];
        let lower = mid[i] - bb_k * sd[i];
        sq[i] = if upper < mid[i] + kc_k * a[i] && lower > mid[i] - kc_k * a[i] { 1.0 } else { 0.0 };
        delta[i] = close[i] - ((hh[i] + ll[i]) / 2.0 + mid[i]) / 2.0;
    }
    (sq, linear_regression(&delta, period).value)
}

fn yang_zhang(
    open: &[f64],
    high: &[f64],
    low: &[f64],
    close: &[f64],
    period: usize,
    periods_per_year: f64,
) -> Result<Vec<f64>, String> {
    if period <= 1 {
        return Err("window must exceed ddof".into());
    }
    let len = close.len();
    let mut ov = nans(len);
    let mut oc = nans(len);
    let mut rs = nans(len);
    for i in 1..len {
        ov[i] = (open[i] / close[i - 1]).ln();
        oc[i] = (close[i] / open[i]).ln();
        rs[i] = (high[i] / close[i]).ln() * (high[i] / open[i]).ln() + (low[i] / close[i]).ln() * (low[i] / open[i]).ln();
    }
    let vo = variance(&ov, period, 1);
    let vc = variance(&oc, period, 1);
    let mrs = rolling::mean(&rs, period);
    let pf = period as f64;
    let k = 0.34 / (1.34 + (pf + 1.0) / (pf - 1.0));
    let mut r = nans(len);
    for i in 0..len {
        if !vo[i].is_nan() {
            r[i] = 100.0 * net_max(0.0, (vo[i] + k * vc[i] + (1.0 - k) * mrs[i]) * periods_per_year).sqrt();
        }
    }
    Ok(r)
}

fn ewma_volatility(close: &[f64], lambda: f64, periods_per_year: f64) -> Vec<f64> {
    let len = close.len();
    let mut r = nans(len);
    if len < 2 {
        return r;
    }
    let ret: Vec<f64> = (1..len).map(|i| (close[i] / close[i - 1]).ln()).collect();
    // Rolling.EwmaVolatility
    let mut v = nans(ret.len());
    let mut var = ret[0] * ret[0];
    v[0] = var.sqrt();
    for i in 1..ret.len() {
        var = lambda * var + (1.0 - lambda) * ret[i - 1] * ret[i - 1];
        v[i] = var.sqrt();
    }
    for i in 0..v.len() {
        r[i + 1] = v[i] * 100.0 * periods_per_year.sqrt();
    }
    r
}

fn standard_error(x: &[f64], period: usize) -> Vec<f64> {
    let lr = linear_regression(x, period);
    let mut r = nans(x.len());
    let denom = if period >= 3 { (period - 2) as f64 } else { 1.0 };
    for i in period.saturating_sub(1)..x.len() {
        if lr.slope[i].is_nan() {
            continue;
        }
        let mut ss = 0.0;
        for j in 0..period {
            let fit = lr.intercept[i] + lr.slope[i] * j as f64;
            let d = x[i + 1 - period + j] - fit;
            ss += d * d;
        }
        r[i] = (ss / denom).sqrt();
    }
    r
}

fn twiggs_money_flow(high: &[f64], low: &[f64], close: &[f64], volume: &[f64], period: usize) -> Vec<f64> {
    let len = close.len();
    let mut ad = nans(len);
    let mut vol = nans(len);
    for i in 1..len {
        let th = net_max(high[i], close[i - 1]);
        let tl = net_min(low[i], close[i - 1]);
        ad[i] = if th > tl { volume[i] * ((close[i] - tl) - (th - close[i])) / (th - tl) } else { 0.0 };
        vol[i] = volume[i];
    }
    let sa = rma(&ad, period);
    let sv = rma(&vol, period);
    let mut r = nans(len);
    for i in 0..len {
        if sv[i] > 0.0 {
            r[i] = sa[i] / sv[i];
        }
    }
    r
}

fn volume_zone_oscillator(close: &[f64], volume: &[f64], period: usize) -> Result<Vec<f64>, String> {
    let len = close.len();
    let mut sv = nans(len);
    let mut v = nans(len);
    for i in 1..len {
        sv[i] = sign(close[i] - close[i - 1])? * volume[i];
        v[i] = volume[i];
    }
    let es = ema(&sv, period);
    let ev = ema(&v, period);
    let mut r = nans(len);
    for i in 0..len {
        if ev[i] > 0.0 {
            r[i] = 100.0 * es[i] / ev[i];
        }
    }
    Ok(r)
}

fn vpci(close: &[f64], volume: &[f64], short_p: usize, long_p: usize) -> Vec<f64> {
    let vw_l = vwma(close, volume, long_p);
    let sm_l = sma(close, long_p);
    let vw_s = vwma(close, volume, short_p);
    let sm_s = sma(close, short_p);
    let v_s = sma(volume, short_p);
    let v_l = sma(volume, long_p);
    let mut r = nans(close.len());
    for i in 0..r.len() {
        if sm_s[i] != 0.0 && v_l[i] > 0.0 && !sm_l[i].is_nan() {
            r[i] = (vw_l[i] - sm_l[i]) * (vw_s[i] / sm_s[i]) * (v_s[i] / v_l[i]);
        }
    }
    r
}

/// Ta.Profile: rolling volume (Some) or TPO (None) profile.
fn profile(
    high: &[f64],
    low: &[f64],
    volume: Option<&[f64]>,
    period: usize,
    bins: usize,
    value_area: f64,
) -> (Vec<f64>, Vec<f64>, Vec<f64>) {
    let len = high.len();
    let mut poc = nans(len);
    let mut vah = nans(len);
    let mut val = nans(len);
    let mut hist = vec![0.0f64; bins];
    let last_bin = bins as i64 - 1;
    let clamp = |v: f64| -> usize {
        // C# (int) truncation (saturating, NaN -> 0) then Math.Clamp(.., 0, bins - 1)
        let t = v as i64;
        t.clamp(0, last_bin) as usize
    };
    for i in period.saturating_sub(1)..len {
        let (mut hi, mut lo) = (f64::MIN, f64::MAX);
        for k in i + 1 - period..=i {
            hi = net_max(hi, high[k]);
            lo = net_min(lo, low[k]);
        }
        if !(hi > lo) {
            continue;
        }
        let width = (hi - lo) / bins as f64;
        for b in hist.iter_mut() {
            *b = 0.0;
        }
        let mut total = 0.0;
        for k in i + 1 - period..=i {
            let b0 = clamp((low[k] - lo) / width);
            let b1 = clamp((high[k] - lo) / width);
            let w = match volume {
                None => 1.0,
                Some(vv) => vv[k] / (b1 - b0 + 1) as f64,
            };
            for b in b0..=b1 {
                hist[b] += w;
                total += w;
            }
        }
        let mut poc_bin = 0usize;
        for b in 1..bins {
            if hist[b] > hist[poc_bin] {
                poc_bin = b;
            }
        }
        let (mut a, mut z) = (poc_bin, poc_bin);
        let mut acc = hist[poc_bin];
        while acc < value_area * total && (a > 0 || z < bins - 1) {
            let below = if a > 0 { hist[a - 1] } else { -1.0 };
            let above = if z < bins - 1 { hist[z + 1] } else { -1.0 };
            if above >= below {
                z += 1;
                acc += hist[z];
            } else {
                a -= 1;
                acc += hist[a];
            }
        }
        poc[i] = lo + (poc_bin as f64 + 0.5) * width;
        vah[i] = lo + (z + 1) as f64 * width;
        val[i] = lo + a as f64 * width;
    }
    (poc, vah, val)
}

fn mean_absolute_deviation(x: &[f64], period: usize) -> Vec<f64> {
    let m = rolling::mean(x, period);
    let mut r = nans(x.len());
    for i in 0..r.len() {
        if m[i].is_nan() {
            continue;
        }
        let mut s = 0.0;
        for k in i + 1 - period..=i {
            s += (x[k] - m[i]).abs();
        }
        r[i] = s / period as f64;
    }
    r
}

fn bars_since_extreme(x: &[f64], period: usize, max: bool) -> Vec<f64> {
    let idx = rolling::extreme_index(x, period, max);
    let mut r = nans(x.len());
    for i in 0..r.len() {
        if idx[i] >= 0 {
            r[i] = (i as isize - idx[i]) as f64;
        }
    }
    r
}

fn entropy(x: &[f64], period: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    let mut i = s + period - 1;
    while i < x.len() {
        let mut sum = 0.0;
        for k in i + 1 - period..=i {
            sum += x[k];
        }
        if !(sum <= 0.0) {
            let mut h = 0.0;
            for k in i + 1 - period..=i {
                let pk = x[k] / sum;
                if pk > 0.0 {
                    h -= pk * pk.log2();
                }
            }
            r[i] = h;
        }
        i += 1;
    }
    r
}

/// .NET Array.Sort<double> ordering: NaN sorts before everything, NaN == NaN.
fn net_cmp(a: &f64, b: &f64) -> std::cmp::Ordering {
    use std::cmp::Ordering::*;
    match (a.is_nan(), b.is_nan()) {
        (true, true) => Equal,
        (true, false) => Less,
        (false, true) => Greater,
        _ => a.partial_cmp(b).unwrap_or(Equal),
    }
}

fn rolling_quantile(x: &[f64], period: usize, q: f64) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    let mut buf = vec![0.0f64; period];
    let mut i = s + period - 1;
    while i < x.len() {
        buf.copy_from_slice(&x[i + 1 - period..=i]);
        buf.sort_by(net_cmp);
        let pos = q * (period - 1) as f64;
        let lo = pos.floor() as i64;
        let hi = (period as i64 - 1).min(lo + 1);
        let (lo_u, hi_u) = (lo as usize, hi as usize);
        r[i] = buf[lo_u] + (pos - lo as f64) * (buf[hi_u] - buf[lo_u]);
        i += 1;
    }
    r
}
