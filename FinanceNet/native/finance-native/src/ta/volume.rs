//! Catalogue entries: see ta/mod.rs for the module layout.
//!
//! First-wave volatility, volume, statistics and price-transform entries (ports of TrendVolatility.cs and
//! VolumeStats.cs).

use super::core::{atr, ema, net_max, net_min, sma, true_range};
use super::rolling::{self, nans};
use super::{many, n, one, p, Ohlcv, Output};

/// Ids implemented by this module.
pub const IDS: &[&str] = &[
    "SVWAP",
    // Volatility
    "TRANGE", "ATR", "NATR", "BBANDS", "BBPCT", "BBW", "KC", "DC", "CHANDELIER", "CHAIKINVOL", "HV", "PARKINSON",
    "GARMANKLASS", "ROGERSSATCHELL", "ULCER", "CHOP", "STDDEV",
    // Volume
    "OBV", "VWAP", "RVWAP", "MFI", "AD", "CMF", "ADOSC", "FI", "EOM", "VPT", "NVI", "PVI", "VO", "KVO", "VROC",
    // Statistics
    "ZSCORE", "VAR", "SKEW", "KURT", "MEDIAN", "PCTRANK", "CORREL", "HURST",
    // Price transforms
    "TYPPRICE", "MEDPRICE", "WCLPRICE", "AVGPRICE", "HEIKINASHI",
];

pub fn compute(id: &str, x: &Ohlcv, ps: &[f64]) -> Output {
    let (o, h, l, c, v) = (x.open, x.high, x.low, x.close, x.volume);
    Some(match id {
        // ---------------- Volatility ----------------
        "SVWAP" => one(id, session_vwap(x.day, h, l, c, v)),
        "TRANGE" => one(id, true_range(h, l, c)),
        "ATR" => one(id, atr(h, l, c, n(ps, 0))),
        "NATR" => one(id, natr(h, l, c, n(ps, 0))),
        "BBANDS" => {
            let (u, m, lo, _, _) = bollinger(c, n(ps, 0), p(ps, 1));
            many(vec![("Upper", u), ("Middle", m), ("Lower", lo)])
        }
        "BBPCT" => one(id, bollinger(c, n(ps, 0), p(ps, 1)).3),
        "BBW" => one(id, bollinger(c, n(ps, 0), p(ps, 1)).4),
        "KC" => {
            let (u, m, lo) = keltner(h, l, c, n(ps, 0), n(ps, 1), p(ps, 2));
            many(vec![("Upper", u), ("Middle", m), ("Lower", lo)])
        }
        "DC" => {
            let (u, m, lo) = donchian(h, l, n(ps, 0));
            many(vec![("Upper", u), ("Middle", m), ("Lower", lo)])
        }
        "CHANDELIER" => {
            let (le, se) = chandelier(h, l, c, n(ps, 0), p(ps, 1));
            many(vec![("LongExit", le), ("ShortExit", se)])
        }
        "CHAIKINVOL" => one(id, chaikin_volatility(h, l, n(ps, 0), n(ps, 1))),
        "HV" => {
            let period = n(ps, 0);
            if period <= 1 {
                return Some(Err("HV: Rolling.StdDev needs window > ddof".into()));
            }
            one(id, historical_volatility(c, period, 252.0))
        }
        "PARKINSON" => one(id, parkinson(h, l, n(ps, 0), 252.0)),
        "GARMANKLASS" => one(id, garman_klass(o, h, l, c, n(ps, 0), 252.0)),
        "ROGERSSATCHELL" => one(id, rogers_satchell(o, h, l, c, n(ps, 0), 252.0)),
        "ULCER" => one(id, ulcer(c, n(ps, 0))),
        "CHOP" => one(id, choppiness(h, l, c, n(ps, 0))),
        "STDDEV" => one(id, rolling::std_dev(c, n(ps, 0), 0)),

        // ---------------- Volume ----------------
        "OBV" => one(id, obv(c, v)),
        "VWAP" => one(id, vwap(h, l, c, v)),
        "RVWAP" => one(id, vwma(&typical_price(h, l, c), v, n(ps, 0))),
        "MFI" => one(id, mfi(h, l, c, v, n(ps, 0))),
        "AD" => one(id, accumulation_distribution(h, l, c, v)),
        "CMF" => one(id, cmf(h, l, c, v, n(ps, 0))),
        "ADOSC" => one(id, apo(&accumulation_distribution(h, l, c, v), n(ps, 0), n(ps, 1))),
        "FI" => one(id, force_index(c, v, n(ps, 0))),
        "EOM" => one(id, ease_of_movement(h, l, v, n(ps, 0), 1e8)),
        "VPT" => one(id, vpt(c, v)),
        "NVI" => one(id, volume_index(c, v, false)),
        "PVI" => one(id, volume_index(c, v, true)),
        "VO" => one(id, ppo_line(v, n(ps, 0), n(ps, 1))),
        "KVO" => {
            let (k, s) = klinger(h, l, c, v, n(ps, 0), n(ps, 1), n(ps, 2));
            many(vec![("KVO", k), ("Signal", s)])
        }
        "VROC" => one(id, roc(v, n(ps, 0))),

        // ---------------- Statistics ----------------
        "ZSCORE" => {
            let period = n(ps, 0);
            if period <= 1 {
                return Some(Err("ZSCORE: Rolling.StdDev needs window > ddof".into()));
            }
            one(id, rolling::z_score(c, period))
        }
        "VAR" => one(id, rolling::variance(c, n(ps, 0), 0)),
        "SKEW" => one(id, rolling::apply(&log_returns(c), n(ps, 0), skewness)),
        "KURT" => one(id, rolling::apply(&log_returns(c), n(ps, 0), excess_kurtosis)),
        "MEDIAN" => one(id, rolling::apply(c, n(ps, 0), median)),
        "PCTRANK" => one(id, rolling::percent_rank(c, n(ps, 0))),
        "CORREL" => {
            let t: Vec<f64> = (0..c.len()).map(|i| i as f64).collect();
            one(id, rolling::correlation(c, &t, n(ps, 0)))
        }
        "HURST" => match hurst(c, n(ps, 0)) {
            Ok(r) => one(id, r),
            Err(e) => Err(e),
        },

        // ---------------- Price transforms ----------------
        "TYPPRICE" => one(id, typical_price(h, l, c)),
        "MEDPRICE" => one(id, (0..h.len()).map(|i| (h[i] + l[i]) / 2.0).collect()),
        "WCLPRICE" => one(id, (0..c.len()).map(|i| (h[i] + l[i] + 2.0 * c[i]) / 4.0).collect()),
        "AVGPRICE" => one(id, (0..c.len()).map(|i| (o[i] + h[i] + l[i] + c[i]) / 4.0).collect()),
        "HEIKINASHI" => {
            let (ho, hh, hl, hc) = heikin_ashi(o, h, l, c);
            many(vec![("Open", ho), ("High", hh), ("Low", hl), ("Close", hc)])
        }
        _ => return None,
    })
}

// ---------------- Shared small ports ----------------

fn typical_price(h: &[f64], l: &[f64], c: &[f64]) -> Vec<f64> {
    (0..c.len()).map(|i| (h[i] + l[i] + c[i]) / 3.0).collect()
}

/// Ta.Roc: 100·(x[t]/x[t-n] - 1).
fn roc(x: &[f64], period: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    for i in period..x.len() {
        r[i] = 100.0 * (x[i] / x[i - period] - 1.0);
    }
    r
}

/// Ta.Apo: EMA(fast) - EMA(slow).
fn apo(x: &[f64], fast: usize, slow: usize) -> Vec<f64> {
    let mut f = ema(x, fast);
    let s = ema(x, slow);
    for i in 0..f.len() {
        f[i] -= s[i];
    }
    f
}

/// Ta.Ppo(...).Ppo: 100·(EMA fast - EMA slow)/EMA slow.
fn ppo_line(x: &[f64], fast: usize, slow: usize) -> Vec<f64> {
    let f = ema(x, fast);
    let s = ema(x, slow);
    (0..x.len()).map(|i| 100.0 * (f[i] - s[i]) / s[i]).collect()
}

/// Ta.Vwma: Σ(p·v)/Σv over the window.
fn vwma(price: &[f64], volume: &[f64], period: usize) -> Vec<f64> {
    let pv: Vec<f64> = (0..price.len()).map(|i| price[i] * volume[i]).collect();
    let mut num = rolling::sum(&pv, period);
    let den = rolling::sum(volume, period);
    for i in 0..num.len() {
        num[i] = if den[i] > 0.0 { num[i] / den[i] } else { f64::NAN };
    }
    num
}

/// IndicatorCatalog.Log: ln(c[t]/c[t-1]) with NaN at 0.
fn log_returns(c: &[f64]) -> Vec<f64> {
    let mut r = nans(c.len());
    for i in 1..c.len() {
        r[i] = (c[i] / c[i - 1]).ln();
    }
    r
}

// ---------------- Volatility ----------------

fn natr(h: &[f64], l: &[f64], c: &[f64], period: usize) -> Vec<f64> {
    let mut a = atr(h, l, c, period);
    for i in 0..a.len() {
        a[i] = 100.0 * a[i] / c[i];
    }
    a
}

#[allow(clippy::type_complexity)]
fn bollinger(x: &[f64], period: usize, k: f64) -> (Vec<f64>, Vec<f64>, Vec<f64>, Vec<f64>, Vec<f64>) {
    let mid = sma(x, period);
    let sd = rolling::std_dev(x, period, 0);
    let n = x.len();
    let (mut up, mut lo, mut pb, mut bw) = (vec![0.0; n], vec![0.0; n], vec![0.0; n], vec![0.0; n]);
    for i in 0..n {
        up[i] = mid[i] + k * sd[i];
        lo[i] = mid[i] - k * sd[i];
        let w = up[i] - lo[i];
        pb[i] = if w > 0.0 { (x[i] - lo[i]) / w } else { f64::NAN };
        bw[i] = if mid[i] != 0.0 { w / mid[i] } else { f64::NAN };
    }
    (up, mid, lo, pb, bw)
}

fn keltner(h: &[f64], l: &[f64], c: &[f64], period: usize, atr_period: usize, mult: f64) -> (Vec<f64>, Vec<f64>, Vec<f64>) {
    let mid = ema(c, period);
    let a = atr(h, l, c, atr_period);
    let n = c.len();
    let (mut up, mut lo) = (vec![0.0; n], vec![0.0; n]);
    for i in 0..n {
        up[i] = mid[i] + mult * a[i];
        lo[i] = mid[i] - mult * a[i];
    }
    (up, mid, lo)
}

fn donchian(h: &[f64], l: &[f64], period: usize) -> (Vec<f64>, Vec<f64>, Vec<f64>) {
    let up = rolling::max(h, period);
    let lo = rolling::min(l, period);
    let mid = (0..up.len()).map(|i| 0.5 * (up[i] + lo[i])).collect();
    (up, mid, lo)
}

fn chandelier(h: &[f64], l: &[f64], c: &[f64], period: usize, mult: f64) -> (Vec<f64>, Vec<f64>) {
    let a = atr(h, l, c, period);
    let mut hh = rolling::max(h, period);
    let mut ll = rolling::min(l, period);
    for i in 0..a.len() {
        hh[i] -= mult * a[i];
        ll[i] += mult * a[i];
    }
    (hh, ll)
}

fn chaikin_volatility(h: &[f64], l: &[f64], ema_period: usize, roc_period: usize) -> Vec<f64> {
    let range: Vec<f64> = (0..h.len()).map(|i| h[i] - l[i]).collect();
    roc(&ema(&range, ema_period), roc_period)
}

fn historical_volatility(c: &[f64], period: usize, ppy: f64) -> Vec<f64> {
    let lr = log_returns(c);
    let mut sd = rolling::std_dev(&lr, period, 1);
    let f = 100.0 * ppy.sqrt();
    for v in sd.iter_mut() {
        *v *= f;
    }
    sd
}

fn parkinson(h: &[f64], l: &[f64], period: usize, ppy: f64) -> Vec<f64> {
    let v: Vec<f64> = (0..h.len())
        .map(|i| {
            let x = (h[i] / l[i]).ln();
            x * x / (4.0 * 2f64.ln())
        })
        .collect();
    let mut m = rolling::mean(&v, period);
    for e in m.iter_mut() {
        *e = 100.0 * (*e * ppy).sqrt();
    }
    m
}

fn garman_klass(o: &[f64], h: &[f64], l: &[f64], c: &[f64], period: usize, ppy: f64) -> Vec<f64> {
    let k = 2.0 * 2f64.ln() - 1.0;
    let v: Vec<f64> = (0..c.len())
        .map(|i| {
            let hl = (h[i] / l[i]).ln();
            let co = (c[i] / o[i]).ln();
            0.5 * hl * hl - k * co * co
        })
        .collect();
    let mut m = rolling::mean(&v, period);
    for e in m.iter_mut() {
        *e = 100.0 * (net_max(0.0, *e) * ppy).sqrt();
    }
    m
}

fn rogers_satchell(o: &[f64], h: &[f64], l: &[f64], c: &[f64], period: usize, ppy: f64) -> Vec<f64> {
    let v: Vec<f64> = (0..c.len())
        .map(|i| {
            let hc = (h[i] / c[i]).ln();
            let ho = (h[i] / o[i]).ln();
            let lc = (l[i] / c[i]).ln();
            let lo = (l[i] / o[i]).ln();
            hc * ho + lc * lo
        })
        .collect();
    let mut m = rolling::mean(&v, period);
    for e in m.iter_mut() {
        *e = 100.0 * (net_max(0.0, *e) * ppy).sqrt();
    }
    m
}

fn ulcer(c: &[f64], period: usize) -> Vec<f64> {
    let hh = rolling::max(c, period);
    let mut sq = nans(c.len());
    for i in 0..sq.len() {
        if hh[i].is_nan() {
            continue;
        }
        let dd = 100.0 * (c[i] - hh[i]) / hh[i];
        sq[i] = dd * dd;
    }
    let mut m = rolling::mean(&sq, period);
    for e in m.iter_mut() {
        *e = e.sqrt();
    }
    m
}

fn choppiness(h: &[f64], l: &[f64], c: &[f64], period: usize) -> Vec<f64> {
    let tr = true_range(h, l, c);
    let st = rolling::sum(&tr, period);
    let hh = rolling::max(h, period);
    let ll = rolling::min(l, period);
    let mut r = nans(c.len());
    let lg = (period as f64).log10();
    for i in 0..r.len() {
        let range = hh[i] - ll[i];
        if range > 0.0 {
            r[i] = 100.0 * (st[i] / range).log10() / lg;
        }
    }
    r
}

// ---------------- Volume ----------------

fn obv(c: &[f64], v: &[f64]) -> Vec<f64> {
    let mut r = vec![0.0; c.len()];
    if r.is_empty() {
        return r;
    }
    r[0] = v[0];
    for i in 1..r.len() {
        let d = if c[i] > c[i - 1] {
            v[i]
        } else if c[i] < c[i - 1] {
            -v[i]
        } else {
            0.0
        };
        r[i] = r[i - 1] + d;
    }
    r
}

fn vwap(h: &[f64], l: &[f64], c: &[f64], v: &[f64]) -> Vec<f64> {
    let mut r = nans(c.len());
    let (mut pv, mut vv) = (0.0f64, 0.0f64);
    for i in 0..r.len() {
        pv += (h[i] + l[i] + c[i]) / 3.0 * v[i];
        vv += v[i];
        if vv > 0.0 {
            r[i] = pv / vv;
        }
    }
    r
}

fn mfi(h: &[f64], l: &[f64], c: &[f64], v: &[f64], period: usize) -> Vec<f64> {
    let tp = typical_price(h, l, c);
    let n = tp.len();
    let mut pos = nans(n);
    let mut neg = nans(n);
    for i in 1..n {
        let mf = tp[i] * v[i];
        pos[i] = if tp[i] > tp[i - 1] { mf } else { 0.0 };
        neg[i] = if tp[i] < tp[i - 1] { mf } else { 0.0 };
    }
    let sp = rolling::sum(&pos, period);
    let sn = rolling::sum(&neg, period);
    let mut r = nans(n);
    for i in 0..n {
        if sp[i].is_nan() {
            continue;
        }
        let t = sp[i] + sn[i];
        r[i] = if t > 0.0 { 100.0 * sp[i] / t } else { 50.0 };
    }
    r
}

#[inline]
fn clv_volume(h: f64, l: f64, c: f64, v: f64) -> f64 {
    if h > l { ((c - l) - (h - c)) / (h - l) * v } else { 0.0 }
}

fn accumulation_distribution(h: &[f64], l: &[f64], c: &[f64], v: &[f64]) -> Vec<f64> {
    let mut r = vec![0.0; c.len()];
    let mut ad = 0.0;
    for i in 0..r.len() {
        ad += clv_volume(h[i], l[i], c[i], v[i]);
        r[i] = ad;
    }
    r
}

fn cmf(h: &[f64], l: &[f64], c: &[f64], v: &[f64], period: usize) -> Vec<f64> {
    let mfv: Vec<f64> = (0..c.len()).map(|i| clv_volume(h[i], l[i], c[i], v[i])).collect();
    let mut a = rolling::sum(&mfv, period);
    let b = rolling::sum(v, period);
    for i in 0..a.len() {
        a[i] = if b[i] > 0.0 { a[i] / b[i] } else { 0.0 };
    }
    for e in a.iter_mut().take(period - 1) {
        *e = f64::NAN;
    }
    a
}

fn force_index(c: &[f64], v: &[f64], period: usize) -> Vec<f64> {
    let mut f = nans(c.len());
    for i in 1..f.len() {
        f[i] = (c[i] - c[i - 1]) * v[i];
    }
    ema(&f, period)
}

fn ease_of_movement(h: &[f64], l: &[f64], v: &[f64], period: usize, scale: f64) -> Vec<f64> {
    let mut e = nans(h.len());
    for i in 1..e.len() {
        let mv = (h[i] + l[i]) / 2.0 - (h[i - 1] + l[i - 1]) / 2.0;
        let range = h[i] - l[i];
        e[i] = if v[i] > 0.0 && range > 0.0 { mv / (v[i] / scale / range) } else { 0.0 };
    }
    sma(&e, period)
}

fn vpt(c: &[f64], v: &[f64]) -> Vec<f64> {
    let mut r = vec![0.0; c.len()];
    for i in 1..r.len() {
        r[i] = r[i - 1] + v[i] * (c[i] - c[i - 1]) / c[i - 1];
    }
    r
}

/// Ta.Nvi (positive = false) / Ta.Pvi (positive = true).
fn volume_index(c: &[f64], v: &[f64], positive: bool) -> Vec<f64> {
    let mut r = vec![0.0; c.len()];
    if r.is_empty() {
        return r;
    }
    r[0] = 1000.0;
    for i in 1..r.len() {
        let hit = if positive { v[i] > v[i - 1] } else { v[i] < v[i - 1] };
        r[i] = if hit { r[i - 1] * c[i] / c[i - 1] } else { r[i - 1] };
    }
    r
}

fn klinger(h: &[f64], l: &[f64], c: &[f64], v: &[f64], fast: usize, slow: usize, signal: usize) -> (Vec<f64>, Vec<f64>) {
    let n = c.len();
    let mut vf = nans(n);
    for i in 1..n {
        let tp = h[i] + l[i] + c[i];
        let prev = h[i - 1] + l[i - 1] + c[i - 1];
        vf[i] = if tp > prev { v[i] } else { -v[i] };
    }
    let kvo = apo(&vf, fast, slow);
    let s = ema(&kvo, signal);
    (kvo, s)
}

// ---------------- Statistics (ports of Finance.Mathematics.Stats) ----------------

/// Lane count of System.Numerics.Vector<double> on the reference machine (AVX2).
const LANES: usize = 4;

/// Stats.Sum (SIMD lanes, then a scalar tail).
fn stats_sum(x: &[f64]) -> f64 {
    let mut s = 0.0;
    let mut i = 0;
    if x.len() >= LANES * 2 {
        let mut acc = [0.0f64; LANES];
        let blocks = x.len() / LANES;
        for k in 0..blocks {
            for j in 0..LANES {
                acc[j] += x[k * LANES + j];
            }
        }
        s = acc.iter().sum();
        i = blocks * LANES;
    }
    while i < x.len() {
        s += x[i];
        i += 1;
    }
    s
}

fn stats_mean(x: &[f64]) -> f64 {
    if x.is_empty() {
        return f64::NAN;
    }
    stats_sum(x) / x.len() as f64
}

/// Stats.Variance (two-pass, SIMD lanes).
fn stats_variance(x: &[f64], ddof: usize) -> f64 {
    let n = x.len();
    if n <= ddof {
        return f64::NAN;
    }
    let m = stats_mean(x);
    let mut s = 0.0;
    let mut i = 0;
    if n >= LANES * 2 {
        let mut acc = [0.0f64; LANES];
        let blocks = n / LANES;
        for k in 0..blocks {
            for j in 0..LANES {
                let d = x[k * LANES + j] - m;
                acc[j] += d * d;
            }
        }
        s = acc.iter().sum();
        i = blocks * LANES;
    }
    while i < n {
        let d = x[i] - m;
        s += d * d;
        i += 1;
    }
    s / (n - ddof) as f64
}

fn skewness(x: &[f64]) -> f64 {
    let n = x.len();
    if n < 3 {
        return f64::NAN;
    }
    let m = stats_mean(x);
    let (mut m2, mut m3) = (0.0f64, 0.0f64);
    for &v in x {
        let d = v - m;
        m2 += d * d;
        m3 += d * d * d;
    }
    m2 /= n as f64;
    m3 /= n as f64;
    if m2 == 0.0 {
        return 0.0;
    }
    let g1 = m3 / m2.powf(1.5);
    let nf = n as f64;
    (nf * (nf - 1.0)).sqrt() / (nf - 2.0) * g1
}

fn excess_kurtosis(x: &[f64]) -> f64 {
    let n = x.len();
    if n < 4 {
        return f64::NAN;
    }
    let m = stats_mean(x);
    let (mut m2, mut m4) = (0.0f64, 0.0f64);
    for &v in x {
        let d = v - m;
        let d2 = d * d;
        m2 += d2;
        m4 += d2 * d2;
    }
    m2 /= n as f64;
    m4 /= n as f64;
    if m2 == 0.0 {
        return 0.0;
    }
    let g2 = m4 / (m2 * m2) - 3.0;
    let nf = n as f64;
    (nf - 1.0) / ((nf - 2.0) * (nf - 3.0)) * ((nf + 1.0) * g2 + 6.0)
}

/// Stats.Median = Stats.Quantile(x, 0.5): sort (Array.Sort puts NaN first), then linear interpolation.
fn median(x: &[f64]) -> f64 {
    if x.is_empty() {
        return f64::NAN;
    }
    let mut s = x.to_vec();
    s.sort_by(|a, b| match (a.is_nan(), b.is_nan()) {
        (true, true) => std::cmp::Ordering::Equal,
        (true, false) => std::cmp::Ordering::Less,
        (false, true) => std::cmp::Ordering::Greater,
        _ => a.partial_cmp(b).unwrap(),
    });
    let h = (s.len() - 1) as f64 * 0.5;
    let lo = h.floor() as usize;
    let frac = h - lo as f64;
    if lo + 1 < s.len() { s[lo] + frac * (s[lo + 1] - s[lo]) } else { s[lo] }
}

/// Ta.Hurst: rolling rescaled-range Hurst exponent of log returns.
fn hurst(c: &[f64], period: usize) -> Result<Vec<f64>, String> {
    let lr = log_returns(c);
    let mut r = nans(lr.len());
    let s = rolling::first_valid(&lr);
    let mut i = s + period - 1;
    while i < lr.len() {
        r[i] = hurst_rs(&lr[i + 1 - period..=i])?;
        i += 1;
    }
    Ok(r)
}

fn hurst_rs(r: &[f64]) -> Result<f64, String> {
    let mut sizes = Vec::new();
    let mut s = 8usize;
    while s <= r.len() / 2 {
        sizes.push(s);
        s *= 2;
    }
    if sizes.len() < 2 {
        return Ok(f64::NAN);
    }
    let mut xs = vec![0.0; sizes.len()];
    let mut ys = vec![0.0; sizes.len()];
    for (k, &size) in sizes.iter().enumerate() {
        let chunks = r.len() / size;
        let mut rs_sum = 0.0;
        let mut used = 0usize;
        for ch in 0..chunks {
            let w = &r[ch * size..ch * size + size];
            let m = stats_mean(w);
            let (mut cum, mut max, mut min) = (0.0f64, f64::NEG_INFINITY, f64::INFINITY);
            for &v in w {
                cum += v - m;
                max = net_max(max, cum);
                min = net_min(min, cum);
            }
            let sd = stats_variance(w, 0).sqrt();
            if sd > 0.0 {
                rs_sum += (max - min) / sd;
                used += 1;
            }
        }
        xs[k] = (size as f64).ln();
        ys[k] = if used > 0 { (rs_sum / used as f64).ln() } else { f64::NAN };
    }
    if ys.iter().any(|v| v.is_nan()) {
        return Ok(f64::NAN);
    }
    ols_slope(&xs, &ys)
}

/// Regression.Simple(xs, ys).Coefficients[1]: normal equations with intercept, LU-with-partial-pivoting inverse,
/// beta = inv · Xᵀy.
fn ols_slope(x: &[f64], y: &[f64]) -> Result<f64, String> {
    let n = x.len();
    if n <= 2 {
        return Err("HURST: Regression.Ols needs more observations than parameters.".into());
    }
    let mut a = [[0.0f64; 2]; 2];
    let mut xty = [0.0f64; 2];
    for i in 0..n {
        let row = [1.0, x[i]];
        for p in 0..2 {
            xty[p] += row[p] * y[i];
            for q in p..2 {
                a[p][q] += row[p] * row[q];
            }
        }
    }
    a[1][0] = a[0][1];
    // Factor
    let mut perm = [0usize, 1];
    for k in 0..2 {
        let mut piv = k;
        let mut max = a[k][k].abs();
        for i in k + 1..2 {
            let v = a[i][k].abs();
            if v > max {
                max = v;
                piv = i;
            }
        }
        if max < 1e-300 {
            return Err("HURST: matrix is singular.".into());
        }
        if piv != k {
            a.swap(k, piv);
            perm.swap(k, piv);
        }
        let pivot = a[k][k];
        for i in k + 1..2 {
            let f = a[i][k] / pivot;
            a[i][k] = f;
            if f != 0.0 {
                for j in k + 1..2 {
                    a[i][j] += -f * a[k][j];
                }
            }
        }
    }
    // Inverse, column by column.
    let mut inv = [[0.0f64; 2]; 2];
    for j in 0..2 {
        let mut col = [0.0f64; 2];
        for i in 0..2 {
            col[i] = if perm[i] == j { 1.0 } else { 0.0 };
        }
        for i in 0..2 {
            let mut d = 0.0;
            for t in 0..i {
                d += a[i][t] * col[t];
            }
            col[i] -= d;
        }
        for i in (0..2).rev() {
            let mut d = 0.0;
            for t in i + 1..2 {
                d += a[i][t] * col[t];
            }
            col[i] -= d;
            col[i] /= a[i][i];
        }
        for i in 0..2 {
            inv[i][j] = col[i];
        }
    }
    Ok(inv[1][0] * xty[0] + inv[1][1] * xty[1])
}

// ---------------- Price transforms ----------------

fn heikin_ashi(o: &[f64], h: &[f64], l: &[f64], c: &[f64]) -> (Vec<f64>, Vec<f64>, Vec<f64>, Vec<f64>) {
    let n = c.len();
    let (mut ro, mut rh, mut rl, mut rc) = (vec![0.0; n], vec![0.0; n], vec![0.0; n], vec![0.0; n]);
    if n == 0 {
        return (ro, rh, rl, rc);
    }
    let mut prev_open = (o[0] + c[0]) / 2.0;
    let mut prev_close = (o[0] + h[0] + l[0] + c[0]) / 4.0;
    for i in 0..n {
        let cc = (o[i] + h[i] + l[i] + c[i]) / 4.0;
        let oo = if i == 0 { prev_open } else { (prev_open + prev_close) / 2.0 };
        ro[i] = oo;
        rh[i] = net_max(h[i], net_max(oo, cc));
        rl[i] = net_min(l[i], net_min(oo, cc));
        rc[i] = cc;
        prev_open = oo;
        prev_close = cc;
    }
    (ro, rh, rl, rc)
}

/// VWAP that resets when the calendar day changes (Ta.SessionVwap).
fn session_vwap(day: &[f64], high: &[f64], low: &[f64], close: &[f64], volume: &[f64]) -> Vec<f64> {
    let mut r = nans(close.len());
    let (mut pv, mut v) = (0.0f64, 0.0f64);
    for i in 0..r.len() {
        if i > 0 && day[i] != day[i - 1] {
            pv = 0.0;
            v = 0.0;
        }
        pv += (high[i] + low[i] + close[i]) / 3.0 * volume[i];
        v += volume[i];
        if v > 0.0 {
            r[i] = pv / v;
        }
    }
    r
}
