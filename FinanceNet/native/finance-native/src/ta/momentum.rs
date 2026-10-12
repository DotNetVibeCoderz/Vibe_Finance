//! Catalogue entries: see ta/mod.rs for the module layout.
//! First-wave momentum block (ports of Finance.TimeSeries/Indicators/Momentum.cs).

use super::core::{ema, net_max, net_min, sma, wma};
use super::rolling::{self, first_valid, nans};
use super::{many, n, one, Ohlcv, Output};

/// Ids implemented by this module.
pub const IDS: &[&str] = &[
    "RSI", "STOCHRSI", "STOCH", "STOCHF", "WILLR", "CCI", "ROC", "ROCR", "MOM", "TRIX", "UO", "AO", "AC", "MACD", "PPO",
    "APO", "CMO", "DPO", "KST", "TSI", "BOP", "COPPOCK", "FISHER", "RVGI", "ELDERRAY", "STC", "CRSI", "QSTICK",
    "MASSINDEX",
];

pub fn compute(id: &str, x: &Ohlcv, p: &[f64]) -> Output {
    let (o, h, l, c) = (x.open, x.high, x.low, x.close);
    Some(match id {
        "RSI" => one("RSI", rsi(c, n(p, 0))),
        "STOCHRSI" => {
            let (k, d) = stoch_rsi(c, n(p, 0), n(p, 1), n(p, 2), n(p, 3));
            many(vec![("%K", k), ("%D", d)])
        }
        "STOCH" => {
            let (k, d) = stochastic(h, l, c, n(p, 0), n(p, 1), n(p, 2));
            many(vec![("%K", k), ("%D", d)])
        }
        "STOCHF" => {
            let (k, d) = stochastic(h, l, c, n(p, 0), 1, n(p, 1));
            many(vec![("%K", k), ("%D", d)])
        }
        "WILLR" => one("WILLR", williams_r(h, l, c, n(p, 0))),
        "CCI" => one("CCI", cci(h, l, c, n(p, 0))),
        "ROC" => one("ROC", roc(c, n(p, 0))),
        "ROCR" => one("ROCR", roc_ratio(c, n(p, 0))),
        "MOM" => one("MOM", rolling::diff(c, n(p, 0))),
        "TRIX" => one("TRIX", trix(c, n(p, 0))),
        "UO" => one("UO", ultimate(h, l, c, n(p, 0), n(p, 1), n(p, 2))),
        "AO" => one("AO", awesome(h, l, n(p, 0), n(p, 1))),
        "AC" => {
            let mut ao = awesome(h, l, 5, 34);
            let s = sma(&ao, 5);
            for i in 0..ao.len() {
                ao[i] -= s[i];
            }
            one("AC", ao)
        }
        "MACD" => {
            let (line, sig, hist) = macd(c, n(p, 0), n(p, 1), n(p, 2), false);
            many(vec![("MACD", line), ("Signal", sig), ("Histogram", hist)])
        }
        "PPO" => {
            let (line, sig, hist) = macd(c, n(p, 0), n(p, 1), n(p, 2), true);
            many(vec![("PPO", line), ("Signal", sig), ("Histogram", hist)])
        }
        "APO" => one("APO", apo(c, n(p, 0), n(p, 1))),
        "CMO" => one("CMO", cmo(c, n(p, 0))),
        "DPO" => one("DPO", dpo(c, n(p, 0))),
        "KST" => {
            let (k, s) = kst(c);
            many(vec![("KST", k), ("Signal", s)])
        }
        "TSI" => {
            let (t, s) = tsi(c, n(p, 0), n(p, 1), n(p, 2));
            many(vec![("TSI", t), ("Signal", s)])
        }
        "BOP" => {
            let r = (0..c.len())
                .map(|i| {
                    let range = h[i] - l[i];
                    if range > 0.0 { (c[i] - o[i]) / range } else { 0.0 }
                })
                .collect();
            one("BOP", r)
        }
        "COPPOCK" => {
            let mut a = roc(c, n(p, 1));
            let b = roc(c, n(p, 2));
            for i in 0..a.len() {
                a[i] += b[i];
            }
            one("COPPOCK", wma(&a, n(p, 0)))
        }
        "FISHER" => {
            let (f, t) = fisher(h, l, n(p, 0));
            many(vec![("Fisher", f), ("Trigger", t)])
        }
        "RVGI" => {
            let (r, s) = rvgi(o, h, l, c, n(p, 0));
            many(vec![("RVI", r), ("Signal", s)])
        }
        "ELDERRAY" => {
            let e = ema(c, n(p, 0));
            let bull = (0..e.len()).map(|i| h[i] - e[i]).collect();
            let bear = (0..e.len()).map(|i| l[i] - e[i]).collect();
            many(vec![("Bull", bull), ("Bear", bear)])
        }
        "STC" => {
            let cycle = n(p, 0);
            let m = apo(c, n(p, 1), n(p, 2));
            let pf = stoch_smooth(&m, cycle);
            one("STC", stoch_smooth(&pf, cycle))
        }
        "CRSI" => one("CRSI", connors_rsi(c, n(p, 0), n(p, 1), n(p, 2))),
        "QSTICK" => {
            let d: Vec<f64> = (0..c.len()).map(|i| c[i] - o[i]).collect();
            one("QSTICK", sma(&d, n(p, 0)))
        }
        "MASSINDEX" => one("MASSINDEX", mass_index(h, l, n(p, 0), n(p, 1))),
        _ => return None,
    })
}

fn rsi_value(gain: f64, loss: f64) -> f64 {
    if loss == 0.0 {
        if gain == 0.0 { 50.0 } else { 100.0 }
    } else {
        100.0 - 100.0 / (1.0 + gain / loss)
    }
}

fn rsi(x: &[f64], period: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    if x.len() - s <= period {
        return r;
    }
    let (mut gain, mut loss) = (0.0f64, 0.0f64);
    for i in s + 1..=s + period {
        let d = x[i] - x[i - 1];
        if d > 0.0 {
            gain += d;
        } else {
            loss -= d;
        }
    }
    let pf = period as f64;
    gain /= pf;
    loss /= pf;
    r[s + period] = rsi_value(gain, loss);
    let pm1 = (period - 1) as f64;
    for i in s + period + 1..x.len() {
        let d = x[i] - x[i - 1];
        gain = (gain * pm1 + net_max(d, 0.0)) / pf;
        loss = (loss * pm1 + net_max(-d, 0.0)) / pf;
        r[i] = rsi_value(gain, loss);
    }
    r
}

fn stoch_rsi(x: &[f64], rp: usize, sp: usize, ks: usize, ds: usize) -> (Vec<f64>, Vec<f64>) {
    let r = rsi(x, rp);
    let hi = rolling::max(&r, sp);
    let lo = rolling::min(&r, sp);
    let mut raw = nans(x.len());
    for i in 0..raw.len() {
        let range = hi[i] - lo[i];
        if !range.is_nan() {
            raw[i] = if range > 0.0 { 100.0 * (r[i] - lo[i]) / range } else { 0.0 };
        }
    }
    let k = sma(&raw, ks);
    let d = sma(&k, ds);
    (k, d)
}

fn stochastic(h: &[f64], l: &[f64], c: &[f64], kp: usize, ks: usize, dp: usize) -> (Vec<f64>, Vec<f64>) {
    let hh = rolling::max(h, kp);
    let ll = rolling::min(l, kp);
    let mut raw = nans(c.len());
    for i in 0..raw.len() {
        let range = hh[i] - ll[i];
        if !range.is_nan() {
            raw[i] = if range > 0.0 { 100.0 * (c[i] - ll[i]) / range } else { 50.0 };
        }
    }
    let k = if ks > 1 { sma(&raw, ks) } else { raw };
    let d = sma(&k, dp);
    (k, d)
}

fn williams_r(h: &[f64], l: &[f64], c: &[f64], period: usize) -> Vec<f64> {
    let hh = rolling::max(h, period);
    let ll = rolling::min(l, period);
    let mut r = nans(c.len());
    for i in 0..r.len() {
        let range = hh[i] - ll[i];
        if !range.is_nan() {
            r[i] = if range > 0.0 { -100.0 * (hh[i] - c[i]) / range } else { -50.0 };
        }
    }
    r
}

fn cci(h: &[f64], l: &[f64], c: &[f64], period: usize) -> Vec<f64> {
    let tp: Vec<f64> = (0..c.len()).map(|i| (h[i] + l[i] + c[i]) / 3.0).collect();
    let m = sma(&tp, period);
    let mut r = nans(tp.len());
    for i in period - 1..tp.len() {
        let mut mad = 0.0f64;
        for k in i + 1 - period..=i {
            mad += (tp[k] - m[i]).abs();
        }
        mad /= period as f64;
        r[i] = if mad > 0.0 { (tp[i] - m[i]) / (0.015 * mad) } else { 0.0 };
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

fn trix(x: &[f64], period: usize) -> Vec<f64> {
    let e3 = ema(&ema(&ema(x, period), period), period);
    let mut r = nans(x.len());
    for i in 1..x.len() {
        r[i] = 100.0 * (e3[i] / e3[i - 1] - 1.0);
    }
    r
}

fn ultimate(h: &[f64], l: &[f64], c: &[f64], p1: usize, p2: usize, p3: usize) -> Vec<f64> {
    let len = c.len();
    let mut bp = nans(len);
    let mut tr = nans(len);
    for i in 1..len {
        let low_true = net_min(l[i], c[i - 1]);
        let high_true = net_max(h[i], c[i - 1]);
        bp[i] = c[i] - low_true;
        tr[i] = high_true - low_true;
    }
    let (b1, t1) = (rolling::sum(&bp, p1), rolling::sum(&tr, p1));
    let (b2, t2) = (rolling::sum(&bp, p2), rolling::sum(&tr, p2));
    let (b3, t3) = (rolling::sum(&bp, p3), rolling::sum(&tr, p3));
    let mut r = nans(len);
    for i in 0..len {
        if b3[i].is_nan() || t1[i] == 0.0 || t2[i] == 0.0 || t3[i] == 0.0 {
            continue;
        }
        r[i] = 100.0 * (4.0 * b1[i] / t1[i] + 2.0 * b2[i] / t2[i] + b3[i] / t3[i]) / 7.0;
    }
    r
}

fn median_price(h: &[f64], l: &[f64]) -> Vec<f64> {
    (0..h.len()).map(|i| (h[i] + l[i]) / 2.0).collect()
}

fn awesome(h: &[f64], l: &[f64], fast: usize, slow: usize) -> Vec<f64> {
    let mp = median_price(h, l);
    let mut f = sma(&mp, fast);
    let s = sma(&mp, slow);
    for i in 0..f.len() {
        f[i] -= s[i];
    }
    f
}

fn macd(x: &[f64], fast: usize, slow: usize, signal: usize, pct: bool) -> (Vec<f64>, Vec<f64>, Vec<f64>) {
    let f = ema(x, fast);
    let s = ema(x, slow);
    let line: Vec<f64> = (0..x.len())
        .map(|i| if pct { 100.0 * (f[i] - s[i]) / s[i] } else { f[i] - s[i] })
        .collect();
    let sig = ema(&line, signal);
    let hist = (0..x.len()).map(|i| line[i] - sig[i]).collect();
    (line, sig, hist)
}

fn apo(x: &[f64], fast: usize, slow: usize) -> Vec<f64> {
    let mut f = ema(x, fast);
    let s = ema(x, slow);
    for i in 0..f.len() {
        f[i] -= s[i];
    }
    f
}

fn cmo(x: &[f64], period: usize) -> Vec<f64> {
    let len = x.len();
    let mut up = nans(len);
    let mut dn = nans(len);
    let s = first_valid(x);
    for i in s + 1..len {
        let d = x[i] - x[i - 1];
        up[i] = net_max(d, 0.0);
        dn[i] = net_max(-d, 0.0);
    }
    let su = rolling::sum(&up, period);
    let sd = rolling::sum(&dn, period);
    let mut r = nans(len);
    for i in 0..len {
        let t = su[i] + sd[i];
        if !t.is_nan() {
            r[i] = if t > 0.0 { 100.0 * (su[i] - sd[i]) / t } else { 0.0 };
        }
    }
    r
}

fn dpo(x: &[f64], period: usize) -> Vec<f64> {
    let m = sma(x, period);
    let shift = period / 2 + 1;
    let mut r = nans(x.len());
    for i in shift.max(period - 1)..x.len() {
        r[i] = x[i - shift] - m[i];
    }
    r
}

fn kst(x: &[f64]) -> (Vec<f64>, Vec<f64>) {
    let r1 = sma(&roc(x, 10), 10);
    let r2 = sma(&roc(x, 15), 10);
    let r3 = sma(&roc(x, 20), 10);
    let r4 = sma(&roc(x, 30), 15);
    let k: Vec<f64> = (0..x.len()).map(|i| r1[i] + 2.0 * r2[i] + 3.0 * r3[i] + 4.0 * r4[i]).collect();
    let s = sma(&k, 9);
    (k, s)
}

fn tsi(x: &[f64], long: usize, short: usize, signal: usize) -> (Vec<f64>, Vec<f64>) {
    let m = rolling::diff(x, 1);
    let a: Vec<f64> = m.iter().map(|v| v.abs()).collect();
    let num = ema(&ema(&m, long), short);
    let den = ema(&ema(&a, long), short);
    let mut t = nans(x.len());
    for i in 0..t.len() {
        if den[i] > 0.0 {
            t[i] = 100.0 * num[i] / den[i];
        }
    }
    let s = ema(&t, signal);
    (t, s)
}

fn fisher(h: &[f64], l: &[f64], period: usize) -> (Vec<f64>, Vec<f64>) {
    let mp = median_price(h, l);
    let hh = rolling::max(&mp, period);
    let ll = rolling::min(&mp, period);
    let mut f = nans(mp.len());
    let mut trig = nans(mp.len());
    let (mut value, mut fish) = (0.0f64, 0.0f64);
    for i in period - 1..mp.len() {
        let range = hh[i] - ll[i];
        let raw = if range > 0.0 { (mp[i] - ll[i]) / range - 0.5 } else { 0.0 };
        value = net_clamp(0.66 * raw * 2.0 + 0.67 * value, -0.999, 0.999);
        let prev = fish;
        fish = 0.5 * ((1.0 + value) / (1.0 - value)).ln() + 0.5 * fish;
        f[i] = fish;
        trig[i] = prev;
    }
    (f, trig)
}

/// .NET Math.Clamp: NaN passes through.
fn net_clamp(v: f64, lo: f64, hi: f64) -> f64 {
    if v < lo {
        lo
    } else if v > hi {
        hi
    } else {
        v
    }
}

fn rvgi(o: &[f64], h: &[f64], l: &[f64], c: &[f64], period: usize) -> (Vec<f64>, Vec<f64>) {
    let len = c.len();
    let mut num = nans(len);
    let mut den = nans(len);
    for i in 3..len {
        num[i] = ((c[i] - o[i]) + 2.0 * (c[i - 1] - o[i - 1]) + 2.0 * (c[i - 2] - o[i - 2]) + (c[i - 3] - o[i - 3])) / 6.0;
        den[i] = ((h[i] - l[i]) + 2.0 * (h[i - 1] - l[i - 1]) + 2.0 * (h[i - 2] - l[i - 2]) + (h[i - 3] - l[i - 3])) / 6.0;
    }
    let sn = sma(&num, period);
    let sd = sma(&den, period);
    let mut rvi = nans(len);
    for i in 0..len {
        if sd[i] != 0.0 && !sd[i].is_nan() {
            rvi[i] = sn[i] / sd[i];
        }
    }
    let mut sig = nans(len);
    for i in 3..len {
        if !rvi[i - 3].is_nan() {
            sig[i] = (rvi[i] + 2.0 * rvi[i - 1] + 2.0 * rvi[i - 2] + rvi[i - 3]) / 6.0;
        }
    }
    (rvi, sig)
}

fn stoch_smooth(v: &[f64], cycle: usize) -> Vec<f64> {
    let hi = rolling::max(v, cycle);
    let lo = rolling::min(v, cycle);
    let mut r = nans(v.len());
    let mut prev = f64::NAN;
    for i in 0..v.len() {
        if hi[i].is_nan() {
            continue;
        }
        let range = hi[i] - lo[i];
        let k = if range > 0.0 {
            100.0 * (v[i] - lo[i]) / range
        } else if prev.is_nan() {
            50.0
        } else {
            prev
        };
        prev = if prev.is_nan() { k } else { prev + 0.5 * (k - prev) };
        r[i] = prev;
    }
    r
}

fn connors_rsi(x: &[f64], rp: usize, sp: usize, rank: usize) -> Vec<f64> {
    let len = x.len();
    let mut streak = vec![0.0f64; len];
    for i in 1..len {
        streak[i] = if x[i] > x[i - 1] {
            if streak[i - 1] > 0.0 { streak[i - 1] + 1.0 } else { 1.0 }
        } else if x[i] < x[i - 1] {
            if streak[i - 1] < 0.0 { streak[i - 1] - 1.0 } else { -1.0 }
        } else {
            0.0
        };
    }
    let a = rsi(x, rp);
    let b = rsi(&streak, sp);
    let rc = roc(x, 1);
    let c = rolling::percent_rank(&rc, rank + 1);
    (0..len).map(|i| (a[i] + b[i] + c[i]) / 3.0).collect()
}

fn mass_index(h: &[f64], l: &[f64], ema_p: usize, sum_p: usize) -> Vec<f64> {
    let range: Vec<f64> = (0..h.len()).map(|i| h[i] - l[i]).collect();
    let e1 = ema(&range, ema_p);
    let e2 = ema(&e1, ema_p);
    let mut ratio = nans(range.len());
    for i in 0..ratio.len() {
        if e2[i] > 0.0 {
            ratio[i] = e1[i] / e2[i];
        }
    }
    rolling::sum(&ratio, sum_p)
}
