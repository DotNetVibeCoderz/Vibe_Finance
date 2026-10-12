//! Catalogue entries: see ta/mod.rs for the module layout.
//! Second wave: cycles (Hilbert transform family, Ehlers filters; Cycles.cs) and trend (Extended.cs).

use super::core::{ema, net_max, net_min, rma, sma, true_range};
use super::rolling::{self, first_valid, nans};
use super::{many, n, one, p, Ohlcv, Output};
use std::f64::consts::PI;

/// Ids implemented by this module.
pub const IDS: &[&str] = &[
    "HT_DCPERIOD",
    "HT_DCPHASE",
    "HT_PHASOR",
    "HT_SINE",
    "HT_TRENDLINE",
    "HT_TRENDMODE",
    "MAMA",
    "SUPERSMOOTHER",
    "ROOFING",
    "DECYCLER",
    "LAGUERRERSI",
    "ITREND",
    "CG",
    "FRAMA",
    "SINWMA",
    "GMA",
    "EHMA",
    "ALLIGATOR",
    "GATOR",
    "FRACTALS",
    "ZIGZAG",
    "VHF",
    "ER",
    "TII",
    "PFE",
    "RWI",
];

pub fn compute(id: &str, x: &Ohlcv, pr: &[f64]) -> Output {
    let c = x.close;
    Some(match id {
        "HT_DCPERIOD" => one(id, hilbert_transform(c).period),
        "HT_DCPHASE" => one(id, hilbert_transform(c).phase),
        "HT_PHASOR" => {
            let h = hilbert_transform(c);
            many(vec![("InPhase", h.in_phase), ("Quadrature", h.quad)])
        }
        "HT_SINE" => {
            let h = hilbert_transform(c);
            many(vec![("Sine", h.sine), ("LeadSine", h.lead)])
        }
        "HT_TRENDLINE" => one(id, hilbert_transform(c).trend),
        "HT_TRENDMODE" => one(id, hilbert_transform(c).mode),
        "MAMA" => mama(c, p(pr, 0), p(pr, 1)).map(|(m, f)| vec![("MAMA".to_string(), m), ("FAMA".to_string(), f)]),
        "SUPERSMOOTHER" => one(id, super_smoother(c, n(pr, 0))),
        "ROOFING" => one(id, super_smoother(&high_pass(c, n(pr, 0)), n(pr, 1))),
        "DECYCLER" => one(id, decycler(c, n(pr, 0))),
        "LAGUERRERSI" => one(id, laguerre_rsi(c, p(pr, 0))),
        "ITREND" => one(id, instantaneous_trendline(c, p(pr, 0))),
        "CG" => one(id, center_of_gravity(c, n(pr, 0))),
        "FRAMA" => one(id, frama(x.high, x.low, c, n(pr, 0))),
        "SINWMA" => one(id, sine_wma(c, n(pr, 0))),
        "GMA" => one(id, geometric_ma(c, n(pr, 0))),
        "EHMA" => one(id, ehma(c, n(pr, 0))),
        "ALLIGATOR" => {
            let (j, t, l) = alligator(x.high, x.low);
            many(vec![("Jaw", j), ("Teeth", t), ("Lips", l)])
        }
        "GATOR" => {
            let (u, l) = gator(x.high, x.low);
            many(vec![("Upper", u), ("Lower", l)])
        }
        "FRACTALS" => {
            let (u, d) = fractals(x.high, x.low, n(pr, 0));
            many(vec![("Up", u), ("Down", d)])
        }
        "ZIGZAG" => one(id, zigzag(x.high, x.low, p(pr, 0))),
        "VHF" => one(id, vhf(c, n(pr, 0))),
        "ER" => one(id, efficiency_ratio(c, n(pr, 0))),
        "TII" => one(id, trend_intensity(c, n(pr, 0), n(pr, 1))),
        "PFE" => one(id, pfe(c, n(pr, 0), n(pr, 1))),
        "RWI" => {
            let (h, l) = random_walk_index(x.high, x.low, c, n(pr, 0));
            many(vec![("High", h), ("Low", l)])
        }
        _ => return None,
    })
}

// ---------------- Cycles (port of Cycles.cs) ----------------

const HILBERT_WARMUP: usize = 50;
const RAD2DEG: f64 = 180.0 / PI;

struct HilbertResult {
    period: Vec<f64>,
    phase: Vec<f64>,
    in_phase: Vec<f64>,
    quad: Vec<f64>,
    sine: Vec<f64>,
    lead: Vec<f64>,
    trend: Vec<f64>,
    mode: Vec<f64>,
}

/// .NET Math.Clamp (NaN passes through; caller guarantees min <= max).
#[inline]
fn clamp(v: f64, min: f64, max: f64) -> f64 {
    if v < min {
        min
    } else if v > max {
        max
    } else {
        v
    }
}

/// .NET Math.Sign as a double (NaN is treated as 0; the managed code would throw).
#[inline]
fn sign(v: f64) -> f64 {
    if v > 0.0 {
        1.0
    } else if v < 0.0 {
        -1.0
    } else {
        0.0
    }
}

#[inline]
fn hilbert(v: &[f64], t: usize, adj: f64) -> f64 {
    (0.0962 * v[t] + 0.5769 * v[t - 2] - 0.5769 * v[t - 4] - 0.0962 * v[t - 6]) * adj
}

fn hilbert_transform(x: &[f64]) -> HilbertResult {
    let n = x.len();
    let mut r = HilbertResult {
        period: nans(n),
        phase: nans(n),
        in_phase: nans(n),
        quad: nans(n),
        sine: nans(n),
        lead: nans(n),
        trend: nans(n),
        mode: nans(n),
    };
    let s = first_valid(x);
    if n - s <= HILBERT_WARMUP {
        return r;
    }
    let mut smooth = vec![0.0; n];
    let mut det = vec![0.0; n];
    let mut i1 = vec![0.0; n];
    let mut q1 = vec![0.0; n];
    let mut itrend = vec![0.0; n];
    let (mut per, mut i2p, mut q2p, mut rep, mut imp, mut sp) = (0.0f64, 0.0f64, 0.0f64, 0.0f64, 0.0f64, 0.0f64);
    let (mut prev_sine, mut prev_lead, mut prev_phase) = (0.0f64, 0.0f64, 0.0f64);
    let mut days_in_trend: i64 = 0;
    for t in s..n {
        let k = t - s;
        smooth[t] = if k >= 3 { (4.0 * x[t] + 3.0 * x[t - 1] + 2.0 * x[t - 2] + x[t - 3]) / 10.0 } else { x[t] };
        if k < 6 {
            continue;
        }
        let adj = 0.075 * per + 0.54;
        det[t] = hilbert(&smooth, t, adj);
        q1[t] = hilbert(&det, t, adj);
        i1[t] = det[t - 3];
        let ji = hilbert(&i1, t, adj);
        let jq = hilbert(&q1, t, adj);
        let i2 = 0.2 * (i1[t] - jq) + 0.8 * i2p;
        let q2 = 0.2 * (q1[t] + ji) + 0.8 * q2p;
        let re = 0.2 * (i2 * i2p + q2 * q2p) + 0.8 * rep;
        let im = 0.2 * (i2 * q2p - q2 * i2p) + 0.8 * imp;
        i2p = i2;
        q2p = q2;
        rep = re;
        imp = im;
        let mut new_per = per;
        if im != 0.0 && re != 0.0 {
            new_per = 360.0 / ((im / re).atan() * RAD2DEG);
        }
        if new_per > 1.5 * per && per > 0.0 {
            new_per = 1.5 * per;
        }
        if new_per < 0.67 * per {
            new_per = 0.67 * per;
        }
        new_per = clamp(new_per, 6.0, 50.0);
        per = 0.2 * new_per + 0.8 * per;
        sp = 0.33 * per + 0.67 * sp;

        // Dominant cycle phase: one-period DFT of the smoothed price.
        let dc = ((sp + 0.5) as i64).max(1) as usize;
        let (mut real, mut imag) = (0.0f64, 0.0f64);
        let mut j = 0usize;
        while j < dc && j <= k {
            let a = 2.0 * PI * j as f64 / dc as f64;
            real += a.sin() * smooth[t - j];
            imag += a.cos() * smooth[t - j];
            j += 1;
        }
        let mut ph = if imag.abs() > 0.0 { (real / imag).atan() * RAD2DEG } else { 90.0 * sign(real) };
        ph += 90.0;
        if sp > 0.0 {
            ph += 360.0 / sp;
        }
        if imag < 0.0 {
            ph += 180.0;
        }
        if ph > 315.0 {
            ph -= 360.0;
        }

        // Instantaneous trendline: average over one dominant cycle, then a 4-bar WMA.
        let mut sum = 0.0;
        let mut cnt = 0i64;
        let mut j = 0usize;
        while j < dc && j <= k {
            sum += x[t - j];
            cnt += 1;
            j += 1;
        }
        itrend[t] = sum / cnt as f64;
        let tl = if k >= 9 {
            (4.0 * itrend[t] + 3.0 * itrend[t - 1] + 2.0 * itrend[t - 2] + itrend[t - 3]) / 10.0
        } else {
            itrend[t]
        };

        let sn = (ph / RAD2DEG).sin();
        let ld = ((ph + 45.0) / RAD2DEG).sin();
        let mut tm = 1.0;
        if (sn > ld && prev_sine <= prev_lead) || (sn < ld && prev_sine >= prev_lead) {
            days_in_trend = 0;
            tm = 0.0;
        }
        days_in_trend += 1;
        if (days_in_trend as f64) < 0.5 * sp {
            tm = 0.0;
        }
        let d_phase = ph - prev_phase;
        if sp != 0.0 && d_phase > 0.67 * 360.0 / sp && d_phase < 1.5 * 360.0 / sp {
            tm = 0.0;
        }
        if tl != 0.0 && ((smooth[t] - tl) / tl).abs() >= 0.015 {
            tm = 1.0;
        }
        prev_sine = sn;
        prev_lead = ld;
        prev_phase = ph;

        if k < HILBERT_WARMUP {
            continue;
        }
        r.period[t] = sp;
        r.phase[t] = ph;
        r.in_phase[t] = i1[t];
        r.quad[t] = q1[t];
        r.sine[t] = sn;
        r.lead[t] = ld;
        r.trend[t] = tl;
        r.mode[t] = tm;
    }
    r
}

fn mama(x: &[f64], fast_limit: f64, slow_limit: f64) -> Result<(Vec<f64>, Vec<f64>), String> {
    let n = x.len();
    let mut ma = nans(n);
    let mut fa = nans(n);
    let s = first_valid(x);
    if n - s <= HILBERT_WARMUP {
        return Ok((ma, fa));
    }
    // Math.Clamp(alpha, slow, fast) throws when slow > fast; it is reached once the loop passes k = 6.
    if slow_limit > fast_limit {
        return Err(format!("MAMA: slow limit {slow_limit} must not exceed fast limit {fast_limit}"));
    }
    let mut smooth = vec![0.0; n];
    let mut det = vec![0.0; n];
    let mut i1 = vec![0.0; n];
    let mut q1 = vec![0.0; n];
    let (mut per, mut i2p, mut q2p, mut rep, mut imp, mut prev_phase) = (0.0f64, 0.0f64, 0.0f64, 0.0f64, 0.0f64, 0.0f64);
    let mut m = x[s];
    let mut f = x[s];
    for t in s..n {
        let k = t - s;
        smooth[t] = if k >= 3 { (4.0 * x[t] + 3.0 * x[t - 1] + 2.0 * x[t - 2] + x[t - 3]) / 10.0 } else { x[t] };
        if k < 6 {
            continue;
        }
        let adj = 0.075 * per + 0.54;
        det[t] = hilbert(&smooth, t, adj);
        q1[t] = hilbert(&det, t, adj);
        i1[t] = det[t - 3];
        let ji = hilbert(&i1, t, adj);
        let jq = hilbert(&q1, t, adj);
        let i2 = 0.2 * (i1[t] - jq) + 0.8 * i2p;
        let q2 = 0.2 * (q1[t] + ji) + 0.8 * q2p;
        let re = 0.2 * (i2 * i2p + q2 * q2p) + 0.8 * rep;
        let im = 0.2 * (i2 * q2p - q2 * i2p) + 0.8 * imp;
        i2p = i2;
        q2p = q2;
        rep = re;
        imp = im;
        let mut new_per = per;
        if im != 0.0 && re != 0.0 {
            new_per = 360.0 / ((im / re).atan() * RAD2DEG);
        }
        if new_per > 1.5 * per && per > 0.0 {
            new_per = 1.5 * per;
        }
        if new_per < 0.67 * per {
            new_per = 0.67 * per;
        }
        per = 0.2 * clamp(new_per, 6.0, 50.0) + 0.8 * per;

        let phase = if i1[t] != 0.0 { (q1[t] / i1[t]).atan() * RAD2DEG } else { 0.0 };
        let delta = net_max(1.0, prev_phase - phase);
        prev_phase = phase;
        let alpha = clamp(fast_limit / delta, slow_limit, fast_limit);
        m = alpha * x[t] + (1.0 - alpha) * m;
        f = 0.5 * alpha * m + (1.0 - 0.5 * alpha) * f;
        if k < HILBERT_WARMUP {
            continue;
        }
        ma[t] = m;
        fa[t] = f;
    }
    Ok((ma, fa))
}

fn super_smoother(x: &[f64], period: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    if x.len() - s < 3 {
        return r;
    }
    let pd = period as f64;
    let a = (-(2.0f64).sqrt() * PI / pd).exp();
    let c2 = 2.0 * a * ((2.0f64).sqrt() * PI / pd).cos();
    let c3 = -a * a;
    let c1 = 1.0 - c2 - c3;
    let (mut f1, mut f2) = (x[s], x[s]);
    for i in s..x.len() {
        let f = if i < s + 2 { x[i] } else { c1 * (x[i] + x[i - 1]) / 2.0 + c2 * f1 + c3 * f2 };
        f2 = f1;
        f1 = f;
        if i + 1 >= s + period {
            r[i] = f;
        }
    }
    r
}

fn high_pass(x: &[f64], period: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    if x.len() - s < 3 {
        return r;
    }
    let w = (2.0f64).sqrt() * PI / period as f64;
    let a = (w.cos() + w.sin() - 1.0) / w.cos();
    let (mut h1, mut h2) = (0.0f64, 0.0f64);
    for i in s + 2..x.len() {
        let h = (1.0 - a / 2.0) * (1.0 - a / 2.0) * (x[i] - 2.0 * x[i - 1] + x[i - 2]) + 2.0 * (1.0 - a) * h1
            - (1.0 - a) * (1.0 - a) * h2;
        h2 = h1;
        h1 = h;
        if i + 1 >= s + period {
            r[i] = h;
        }
    }
    r
}

fn decycler(x: &[f64], period: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    if x.len() - s < 2 {
        return r;
    }
    let w = 2.0 * PI / period as f64;
    let a = (w.cos() + w.sin() - 1.0) / w.cos();
    let mut hp = 0.0;
    for i in s + 1..x.len() {
        hp = (1.0 - a / 2.0) * (x[i] - x[i - 1]) + (1.0 - a) * hp;
        if i + 1 >= s + period {
            r[i] = x[i] - hp;
        }
    }
    r
}

fn laguerre_rsi(x: &[f64], gamma: f64) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    if s >= x.len() {
        return r;
    }
    let (mut l0, mut l1, mut l2, mut l3) = (x[s], x[s], x[s], x[s]);
    for i in s..x.len() {
        let (p0, p1, p2) = (l0, l1, l2);
        l0 = (1.0 - gamma) * x[i] + gamma * p0;
        l1 = -gamma * l0 + p0 + gamma * p1;
        l2 = -gamma * l1 + p1 + gamma * p2;
        l3 = -gamma * l2 + p2 + gamma * l3;
        let (mut cu, mut cd) = (0.0f64, 0.0f64);
        for d in [l0 - l1, l1 - l2, l2 - l3] {
            if d >= 0.0 {
                cu += d;
            } else {
                cd -= d;
            }
        }
        if i >= s + 4 {
            r[i] = if cu + cd > 0.0 { cu / (cu + cd) } else { 0.5 };
        }
    }
    r
}

fn instantaneous_trendline(x: &[f64], alpha: f64) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    if x.len() - s < 3 {
        return r;
    }
    let a = alpha;
    let (mut it1, mut it2) = (0.0f64, 0.0f64);
    for i in s..x.len() {
        let k = i - s;
        let it = if k < 7 {
            if k >= 2 { (x[i] + 2.0 * x[i - 1] + x[i - 2]) / 4.0 } else { x[i] }
        } else {
            (a - a * a / 4.0) * x[i] + 0.5 * a * a * x[i - 1] - (a - 0.75 * a * a) * x[i - 2] + 2.0 * (1.0 - a) * it1
                - (1.0 - a) * (1.0 - a) * it2
        };
        it2 = it1;
        it1 = it;
        if k >= 7 {
            r[i] = it;
        }
    }
    r
}

fn center_of_gravity(x: &[f64], period: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    for i in (s + period - 1)..x.len() {
        let (mut num, mut den) = (0.0f64, 0.0f64);
        for j in 0..period {
            num += (j + 1) as f64 * x[i - j];
            den += x[i - j];
        }
        r[i] = if den != 0.0 { -num / den } else { f64::NAN };
    }
    r
}

// ---------------- Trend (port of Extended.cs) ----------------

fn frama(high: &[f64], low: &[f64], close: &[f64], period: usize) -> Vec<f64> {
    let n = close.len();
    let mut r = nans(n);
    let period = if period % 2 == 1 { period + 1 } else { period };
    let half = period / 2;
    if n < period {
        return r;
    }
    fn range(h: &[f64], l: &[f64], from: usize, to: usize) -> f64 {
        let (mut hi, mut lo) = (f64::MIN, f64::MAX);
        for k in from..=to {
            hi = net_max(hi, h[k]);
            lo = net_min(lo, l[k]);
        }
        hi - lo
    }
    let (hf, pf) = (half as f64, period as f64);
    let mut f = close[period - 1];
    for i in period - 1..n {
        let n1 = range(high, low, i + 1 - half, i) / hf;
        let n2 = range(high, low, i + 1 - period, i - half) / hf;
        let n3 = range(high, low, i + 1 - period, i) / pf;
        let mut alpha = 1.0;
        if n1 + n2 > 0.0 && n3 > 0.0 {
            let d = ((n1 + n2).ln() - n3.ln()) / (2.0f64).ln();
            alpha = clamp((-4.6 * (d - 1.0)).exp(), 0.01, 1.0);
        }
        f = if i == period - 1 { close[i] } else { alpha * close[i] + (1.0 - alpha) * f };
        r[i] = f;
    }
    r
}

fn weighted(x: &[f64], weights: &[f64]) -> Vec<f64> {
    let p = weights.len();
    let mut r = nans(x.len());
    let s = first_valid(x);
    let mut sw = 0.0;
    for w in weights {
        sw += *w;
    }
    for i in (s + p - 1)..x.len() {
        let mut acc = 0.0;
        for j in 0..p {
            acc += weights[j] * x[i + 1 + j - p];
        }
        r[i] = acc / sw;
    }
    r
}

fn sine_wma(x: &[f64], period: usize) -> Vec<f64> {
    let w: Vec<f64> = (0..period).map(|j| (PI * (j + 1) as f64 / (period + 1) as f64).sin()).collect();
    weighted(x, &w)
}

fn geometric_ma(x: &[f64], period: usize) -> Vec<f64> {
    let logs: Vec<f64> = x.iter().map(|&v| if v > 0.0 { v.ln() } else { f64::NAN }).collect();
    let mut m = rolling::mean(&logs, period);
    for v in m.iter_mut() {
        *v = v.exp();
    }
    m
}

fn ehma(x: &[f64], period: usize) -> Vec<f64> {
    let half = ema(x, (period / 2).max(1));
    let full = ema(x, period);
    let diff: Vec<f64> = (0..x.len()).map(|i| 2.0 * half[i] - full[i]).collect();
    let sq = ((period as f64).sqrt().round_ties_even() as usize).max(1);
    ema(&diff, sq)
}

fn alligator(high: &[f64], low: &[f64]) -> (Vec<f64>, Vec<f64>, Vec<f64>) {
    let mp: Vec<f64> = (0..high.len()).map(|i| (high[i] + low[i]) / 2.0).collect();
    (
        rolling::shift(&rma(&mp, 13), 8),
        rolling::shift(&rma(&mp, 8), 5),
        rolling::shift(&rma(&mp, 5), 3),
    )
}

fn gator(high: &[f64], low: &[f64]) -> (Vec<f64>, Vec<f64>) {
    let (j, t, l) = alligator(high, low);
    let up: Vec<f64> = (0..j.len()).map(|i| (j[i] - t[i]).abs()).collect();
    let lo: Vec<f64> = (0..j.len()).map(|i| -(t[i] - l[i]).abs()).collect();
    (up, lo)
}

fn fractals(high: &[f64], low: &[f64], wing: usize) -> (Vec<f64>, Vec<f64>) {
    let n = high.len();
    let mut up = nans(n);
    let mut dn = nans(n);
    for i in 2 * wing..n {
        let c = i - wing;
        let (mut is_up, mut is_down) = (true, true);
        for k in 1..=wing {
            is_up &= high[c] > high[c - k] && high[c] > high[c + k];
            is_down &= low[c] < low[c - k] && low[c] < low[c + k];
        }
        if is_up {
            up[i] = high[c];
        }
        if is_down {
            dn[i] = low[c];
        }
    }
    (up, dn)
}

fn zigzag(high: &[f64], low: &[f64], percent: f64) -> Vec<f64> {
    let n = high.len();
    let mut r = nans(n);
    if n == 0 {
        return r;
    }
    let mut pivots: Vec<(usize, f64)> = Vec::new();
    let mut dir = 0i32;
    let mut ext_idx = 0usize;
    let mut ext = (high[0] + low[0]) / 2.0;
    let th = percent / 100.0;
    pivots.push((0, ext));
    for i in 1..n {
        let last = pivots[pivots.len() - 1].1;
        if dir >= 0 && high[i] > ext && (dir == 1 || high[i] >= last * (1.0 + th)) {
            ext = high[i];
            ext_idx = i;
            dir = 1;
        } else if dir <= 0 && low[i] < ext && (dir == -1 || low[i] <= last * (1.0 - th)) {
            ext = low[i];
            ext_idx = i;
            dir = -1;
        } else if dir == 1 && low[i] <= ext * (1.0 - th) {
            pivots.push((ext_idx, ext));
            ext = low[i];
            ext_idx = i;
            dir = -1;
        } else if dir == -1 && high[i] >= ext * (1.0 + th) {
            pivots.push((ext_idx, ext));
            ext = high[i];
            ext_idx = i;
            dir = 1;
        }
    }
    if ext_idx != pivots[pivots.len() - 1].0 {
        pivots.push((ext_idx, ext));
    }
    for q in 1..pivots.len() {
        let (i0, v0) = pivots[q - 1];
        let (i1, v1) = pivots[q];
        // C# loop `for (i = i0; i <= i1; i++)` runs nothing when i1 < i0.
        let mut i = i0;
        while i <= i1 {
            r[i] = if i1 == i0 { v1 } else { v0 + (v1 - v0) * (i - i0) as f64 / (i1 - i0) as f64 };
            i += 1;
        }
    }
    r
}

fn abs_diff(close: &[f64]) -> Vec<f64> {
    let n = close.len();
    let mut d = nans(n);
    for i in 1..n {
        d[i] = (close[i] - close[i - 1]).abs();
    }
    d
}

fn vhf(close: &[f64], period: usize) -> Vec<f64> {
    let n = close.len();
    let hi = rolling::max(close, period);
    let lo = rolling::min(close, period);
    let sum = rolling::sum(&abs_diff(close), period);
    (0..n).map(|i| if sum[i] > 0.0 { (hi[i] - lo[i]) / sum[i] } else { f64::NAN }).collect()
}

fn efficiency_ratio(close: &[f64], period: usize) -> Vec<f64> {
    let n = close.len();
    let mut r = nans(n);
    let sum = rolling::sum(&abs_diff(close), period);
    for i in period..n {
        r[i] = if sum[i] > 0.0 { (close[i] - close[i - period]).abs() / sum[i] } else { 0.0 };
    }
    r
}

fn trend_intensity(close: &[f64], period: usize, window: usize) -> Vec<f64> {
    let sm = sma(close, period);
    let n = close.len();
    let mut pos = nans(n);
    let mut neg = nans(n);
    for i in 0..n {
        if sm[i].is_nan() {
            continue;
        }
        let d = close[i] - sm[i];
        pos[i] = net_max(d, 0.0);
        neg[i] = net_max(-d, 0.0);
    }
    let sp = rolling::sum(&pos, window);
    let sn = rolling::sum(&neg, window);
    let mut r = nans(n);
    for i in 0..n {
        if sp[i] + sn[i] > 0.0 {
            r[i] = 100.0 * sp[i] / (sp[i] + sn[i]);
        }
    }
    r
}

fn pfe(close: &[f64], period: usize, smooth: usize) -> Vec<f64> {
    let n = close.len();
    let mut raw = nans(n);
    let pf = period as f64;
    for i in period..n {
        let d = close[i] - close[i - period];
        let mut path = 0.0;
        for k in i + 1 - period..=i {
            let dd = close[k] - close[k - 1];
            path += (dd * dd + 1.0).sqrt();
        }
        raw[i] = sign(d) * 100.0 * (d * d + pf * pf).sqrt() / path;
    }
    ema(&raw, smooth)
}

fn random_walk_index(high: &[f64], low: &[f64], close: &[f64], period: usize) -> (Vec<f64>, Vec<f64>) {
    let n = close.len();
    let tr = true_range(high, low, close);
    let mut rh = nans(n);
    let mut rl = nans(n);
    for i in period..n {
        let (mut bh, mut bl, mut tr_sum) = (0.0f64, 0.0f64, 0.0f64);
        for k in 1..=period {
            tr_sum += tr[i + 1 - k];
            if k < 2 {
                continue;
            }
            let kf = k as f64;
            let denom = tr_sum / kf * kf.sqrt();
            if denom <= 0.0 {
                continue;
            }
            bh = net_max(bh, (high[i] - low[i - k]) / denom);
            bl = net_max(bl, (high[i - k] - low[i]) / denom);
        }
        rh[i] = bh;
        rl[i] = bl;
    }
    (rh, rl)
}
