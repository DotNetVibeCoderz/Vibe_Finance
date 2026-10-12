//! Catalogue entries: see ta/mod.rs for the module layout.
//!
//! First-wave trend / overlap block (IndicatorCatalog.cs, SMA .. PIVOTSCAM). Ports of the Ta functions in
//! MovingAverages.cs, TrendVolatility.cs and Momentum.cs (Cmo, Vortex).

use super::core::{atr, ema, net_max, net_min, net_round, rma, sma, true_range, wma};
use super::rolling::{self, first_valid, nans};
use super::{many, n, one, p, Ohlcv, Output};

/// Ids implemented by this module.
pub const IDS: &[&str] = &[
    "SMA",
    "EMA",
    "WMA",
    "RMA",
    "DEMA",
    "TEMA",
    "TRIMA",
    "HMA",
    "KAMA",
    "T3",
    "VWMA",
    "ZLEMA",
    "ALMA",
    "MCGINLEY",
    "VIDYA",
    "MIDPOINT",
    "MIDPRICE",
    "ENV",
    "ICHIMOKU",
    "PSAR",
    "SUPERTREND",
    "DMI",
    "ADX",
    "ADXR",
    "DX",
    "AROON",
    "AROONOSC",
    "VORTEX",
    "LINREG",
    "LINREGSLOPE",
    "LINREGINTERCEPT",
    "LINREGANGLE",
    "TSF",
    "PIVOTS",
    "PIVOTSFIB",
    "PIVOTSCAM",
];

pub fn compute(id: &str, x: &Ohlcv, pr: &[f64]) -> Output {
    let c = x.close;
    let (h, l) = (x.high, x.low);
    let r = match id {
        "SMA" => one("SMA", sma(c, n(pr, 0))),
        "EMA" => one("EMA", ema(c, n(pr, 0))),
        "WMA" => one("WMA", wma(c, n(pr, 0))),
        "RMA" => one("RMA", rma(c, n(pr, 0))),
        "DEMA" => one("DEMA", dema(c, n(pr, 0))),
        "TEMA" => one("TEMA", tema(c, n(pr, 0))),
        "TRIMA" => one("TRIMA", trima(c, n(pr, 0))),
        "HMA" => one("HMA", hma(c, n(pr, 0))),
        "KAMA" => one("KAMA", kama(c, n(pr, 0), n(pr, 1), n(pr, 2))),
        "T3" => one("T3", t3(c, n(pr, 0), p(pr, 1))),
        "VWMA" => one("VWMA", vwma(c, x.volume, n(pr, 0))),
        "ZLEMA" => one("ZLEMA", zlema(c, n(pr, 0))),
        "ALMA" => one("ALMA", alma(c, n(pr, 0), p(pr, 1), p(pr, 2))),
        "MCGINLEY" => one("MCGINLEY", mcginley(c, n(pr, 0))),
        "VIDYA" => one("VIDYA", vidya(c, n(pr, 0), n(pr, 1))),
        "MIDPOINT" => one("MIDPOINT", mid_price(c, c, n(pr, 0))),
        "MIDPRICE" => one("MIDPRICE", mid_price(h, l, n(pr, 0))),
        "ENV" => {
            let (u, m, lo) = envelopes(c, n(pr, 0), p(pr, 1));
            many(vec![("Upper", u), ("Middle", m), ("Lower", lo)])
        }
        "ICHIMOKU" => {
            let (t, k, a, b, ch) = ichimoku(h, l, c, n(pr, 0), n(pr, 1), n(pr, 2), 26);
            many(vec![("Tenkan", t), ("Kijun", k), ("SenkouA", a), ("SenkouB", b), ("Chikou", ch)])
        }
        "PSAR" => one("PSAR", parabolic_sar(h, l, p(pr, 0), p(pr, 1))),
        "SUPERTREND" => {
            let (line, dir) = supertrend(h, l, c, n(pr, 0), p(pr, 1));
            many(vec![("Supertrend", line), ("Direction", dir)])
        }
        "DMI" => {
            let d = dmi(h, l, c, n(pr, 0));
            many(vec![("+DI", d.plus_di), ("-DI", d.minus_di), ("ADX", d.adx)])
        }
        "ADX" => one("ADX", dmi(h, l, c, n(pr, 0)).adx),
        "ADXR" => one("ADXR", dmi(h, l, c, n(pr, 0)).adxr),
        "DX" => one("DX", dmi(h, l, c, n(pr, 0)).dx),
        "AROON" => {
            let (up, dn, _) = aroon(h, l, n(pr, 0));
            many(vec![("Up", up), ("Down", dn)])
        }
        "AROONOSC" => one("AROONOSC", aroon(h, l, n(pr, 0)).2),
        "VORTEX" => {
            let (vp, vm) = vortex(h, l, c, n(pr, 0));
            many(vec![("+VI", vp), ("-VI", vm)])
        }
        "LINREG" => one("LinReg", linreg(c, n(pr, 0)).value),
        "LINREGSLOPE" => one("LINREGSLOPE", linreg(c, n(pr, 0)).slope),
        "LINREGINTERCEPT" => one("LINREGINTERCEPT", linreg(c, n(pr, 0)).intercept),
        "LINREGANGLE" => one("LINREGANGLE", linreg(c, n(pr, 0)).angle),
        "TSF" => one("TSF", linreg(c, n(pr, 0)).forecast),
        "PIVOTS" | "PIVOTSFIB" => {
            let m = if id == "PIVOTS" { Pivot::Classic } else { Pivot::Fibonacci };
            let v = pivot_points(h, l, c, m);
            let [pp, r1, s1, r2, s2, _, _] = v;
            many(vec![("Pivot", pp), ("R1", r1), ("S1", s1), ("R2", r2), ("S2", s2)])
        }
        "PIVOTSCAM" => {
            let [_, r1, s1, r2, s2, r3, s3] = pivot_points(h, l, c, Pivot::Camarilla);
            many(vec![("R1", r1), ("S1", s1), ("R2", r2), ("S2", s2), ("R3", r3), ("S3", s3)])
        }
        _ => return None,
    };
    Some(r)
}

fn dema(x: &[f64], period: usize) -> Vec<f64> {
    let e1 = ema(x, period);
    let e2 = ema(&e1, period);
    (0..x.len()).map(|i| 2.0 * e1[i] - e2[i]).collect()
}

fn tema(x: &[f64], period: usize) -> Vec<f64> {
    let e1 = ema(x, period);
    let e2 = ema(&e1, period);
    let e3 = ema(&e2, period);
    (0..x.len()).map(|i| 3.0 * e1[i] - 3.0 * e2[i] + e3[i]).collect()
}

fn trima(x: &[f64], period: usize) -> Vec<f64> {
    let (mut n1, mut n2) = (period.div_ceil(2), period / 2 + 1);
    if period % 2 == 0 {
        n1 = period / 2;
        n2 = period / 2 + 1;
    }
    sma(&sma(x, n1), n2)
}

fn hma(x: &[f64], period: usize) -> Vec<f64> {
    let half = wma(x, (period / 2).max(1));
    let full = wma(x, period);
    let diff: Vec<f64> = (0..x.len()).map(|i| 2.0 * half[i] - full[i]).collect();
    let sq = net_round((period as f64).sqrt()) as usize;
    wma(&diff, sq.max(1))
}

fn kama(x: &[f64], period: usize, fast: usize, slow: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    if x.len() - s <= period {
        return r;
    }
    let fsc = 2.0 / (fast as f64 + 1.0);
    let ssc = 2.0 / (slow as f64 + 1.0);
    let mut volatility = 0.0;
    for i in s + 1..=s + period {
        volatility += (x[i] - x[i - 1]).abs();
    }
    let mut k = x[s + period];
    r[s + period] = k;
    for i in s + period + 1..x.len() {
        volatility += (x[i] - x[i - 1]).abs() - (x[i - period] - x[i - period - 1]).abs();
        let er = if volatility > 0.0 { (x[i] - x[i - period]).abs() / volatility } else { 0.0 };
        let sc = er * (fsc - ssc) + ssc;
        k += sc * sc * (x[i] - k);
        r[i] = k;
    }
    r
}

fn t3(x: &[f64], period: usize, v_factor: f64) -> Vec<f64> {
    let e1 = ema(x, period);
    let e2 = ema(&e1, period);
    let e3 = ema(&e2, period);
    let e4 = ema(&e3, period);
    let e5 = ema(&e4, period);
    let e6 = ema(&e5, period);
    let a = v_factor;
    let a2 = a * a;
    let a3 = a2 * a;
    let c1 = -a3;
    let c2 = 3.0 * a2 + 3.0 * a3;
    let c3 = -6.0 * a2 - 3.0 * a - 3.0 * a3;
    let c4 = 1.0 + 3.0 * a + a3 + 3.0 * a2;
    (0..x.len()).map(|i| c1 * e6[i] + c2 * e5[i] + c3 * e4[i] + c4 * e3[i]).collect()
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

fn zlema(x: &[f64], period: usize) -> Vec<f64> {
    let lag = (period - 1) / 2;
    let mut adj = nans(x.len());
    for i in lag..x.len() {
        adj[i] = 2.0 * x[i] - x[i - lag];
    }
    ema(&adj, period)
}

fn alma(x: &[f64], period: usize, offset: f64, sigma: f64) -> Vec<f64> {
    let mut r = nans(x.len());
    let mut w = vec![0.0f64; period];
    let m = offset * (period as f64 - 1.0);
    let s = period as f64 / sigma;
    let mut norm = 0.0;
    for (i, wi) in w.iter_mut().enumerate() {
        let d = i as f64 - m;
        *wi = (-d * d / (2.0 * s * s)).exp();
        norm += *wi;
    }
    let start = first_valid(x);
    for i in start + period - 1..x.len() {
        let mut acc = 0.0;
        for (k, wk) in w.iter().enumerate() {
            acc += wk * x[i + 1 + k - period];
        }
        r[i] = acc / norm;
    }
    r
}

fn mcginley(x: &[f64], period: usize) -> Vec<f64> {
    let mut r = nans(x.len());
    let s = first_valid(x);
    if s >= x.len() {
        return r;
    }
    let mut md = x[s];
    r[s] = md;
    for i in s + 1..x.len() {
        let ratio = x[i] / md;
        md += (x[i] - md) / (period as f64 * ratio.powf(4.0));
        r[i] = md;
    }
    r
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

fn vidya(x: &[f64], period: usize, cmo_period: usize) -> Vec<f64> {
    let cm = cmo(x, cmo_period);
    let mut r = nans(x.len());
    let alpha = 2.0 / (period as f64 + 1.0);
    let s = first_valid(&cm);
    if s >= x.len() {
        return r;
    }
    let mut v = x[s];
    r[s] = v;
    for i in s + 1..x.len() {
        let k = alpha * cm[i].abs() / 100.0;
        v += k * (x[i] - v);
        r[i] = v;
    }
    r
}

/// Ta.MidPrice (Ta.Midpoint is the same with high = low = x).
fn mid_price(high: &[f64], low: &[f64], period: usize) -> Vec<f64> {
    let mut hi = rolling::max(high, period);
    let lo = rolling::min(low, period);
    for i in 0..hi.len() {
        hi[i] = 0.5 * (hi[i] + lo[i]);
    }
    hi
}

fn envelopes(x: &[f64], period: usize, percent: f64) -> (Vec<f64>, Vec<f64>, Vec<f64>) {
    let mid = sma(x, period);
    let up = mid.iter().map(|m| m * (1.0 + percent / 100.0)).collect();
    let lo = mid.iter().map(|m| m * (1.0 - percent / 100.0)).collect();
    (up, mid, lo)
}

type Five = (Vec<f64>, Vec<f64>, Vec<f64>, Vec<f64>, Vec<f64>);

fn ichimoku(high: &[f64], low: &[f64], close: &[f64], tenkan: usize, kijun: usize, senkou_b: usize, displacement: usize) -> Five {
    let t = mid_price(high, low, tenkan);
    let k = mid_price(high, low, kijun);
    let b = mid_price(high, low, senkou_b);
    let a: Vec<f64> = (0..close.len()).map(|i| 0.5 * (t[i] + k[i])).collect();
    let mut chikou = nans(close.len());
    let mut i = 0;
    while i + displacement < close.len() {
        chikou[i] = close[i + displacement];
        i += 1;
    }
    (t, k, a, b, chikou)
}

fn parabolic_sar(high: &[f64], low: &[f64], step: f64, max: f64) -> Vec<f64> {
    let len = high.len();
    let mut r = nans(len);
    if len < 2 {
        return r;
    }
    let mut long_pos = high[1] + low[1] >= high[0] + low[0];
    let mut af = step;
    let mut ep = if long_pos { net_max(high[0], high[1]) } else { net_min(low[0], low[1]) };
    let mut sar = if long_pos { net_min(low[0], low[1]) } else { net_max(high[0], high[1]) };
    r[1] = sar;
    for i in 2..len {
        sar += af * (ep - sar);
        if long_pos {
            sar = net_min(sar, net_min(low[i - 1], low[i - 2]));
            if low[i] < sar {
                long_pos = false;
                sar = ep;
                ep = low[i];
                af = step;
            } else if high[i] > ep {
                ep = high[i];
                af = net_min(af + step, max);
            }
        } else {
            sar = net_max(sar, net_max(high[i - 1], high[i - 2]));
            if high[i] > sar {
                long_pos = true;
                sar = ep;
                ep = high[i];
                af = step;
            } else if low[i] < ep {
                ep = low[i];
                af = net_min(af + step, max);
            }
        }
        r[i] = sar;
    }
    r
}

fn supertrend(high: &[f64], low: &[f64], close: &[f64], period: usize, multiplier: f64) -> (Vec<f64>, Vec<f64>) {
    let len = close.len();
    let at = atr(high, low, close, period);
    let mut line = nans(len);
    let mut dir = nans(len);
    let (mut upper, mut lower) = (f64::NAN, f64::NAN);
    let mut d = 1i32;
    for i in 0..len {
        if at[i].is_nan() {
            continue;
        }
        let mid = (high[i] + low[i]) / 2.0;
        let bu = mid + multiplier * at[i];
        let bl = mid - multiplier * at[i];
        if upper.is_nan() {
            upper = bu;
            lower = bl;
            d = if close[i] >= mid { 1 } else { -1 };
        } else {
            upper = if bu < upper || close[i - 1] > upper { bu } else { upper };
            lower = if bl > lower || close[i - 1] < lower { bl } else { lower };
            if d == 1 && close[i] < lower {
                d = -1;
            } else if d == -1 && close[i] > upper {
                d = 1;
            }
        }
        line[i] = if d == 1 { lower } else { upper };
        dir[i] = d as f64;
    }
    (line, dir)
}

struct Dmi {
    plus_di: Vec<f64>,
    minus_di: Vec<f64>,
    dx: Vec<f64>,
    adx: Vec<f64>,
    adxr: Vec<f64>,
}

fn dmi(high: &[f64], low: &[f64], close: &[f64], period: usize) -> Dmi {
    let len = close.len();
    let mut plus_di = nans(len);
    let mut minus_di = nans(len);
    let mut dx = nans(len);
    if len <= period {
        return Dmi { plus_di, minus_di, dx, adx: nans(len), adxr: nans(len) };
    }
    let pf = period as f64;
    let (mut s_tr, mut s_plus, mut s_minus) = (0.0f64, 0.0f64, 0.0f64);
    for i in 1..len {
        let up = high[i] - high[i - 1];
        let down = low[i - 1] - low[i];
        let pdm = if up > down && up > 0.0 { up } else { 0.0 };
        let mdm = if down > up && down > 0.0 { down } else { 0.0 };
        let tr = net_max(high[i] - low[i], net_max((high[i] - close[i - 1]).abs(), (low[i] - close[i - 1]).abs()));
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
        let p = if s_tr > 0.0 { 100.0 * s_plus / s_tr } else { 0.0 };
        let m = if s_tr > 0.0 { 100.0 * s_minus / s_tr } else { 0.0 };
        plus_di[i] = p;
        minus_di[i] = m;
        dx[i] = if p + m > 0.0 { 100.0 * (p - m).abs() / (p + m) } else { 0.0 };
    }
    let adx = rma(&dx, period);
    let mut adxr = nans(len);
    for i in period - 1..len {
        if !adx[i].is_nan() && !adx[i + 1 - period].is_nan() {
            adxr[i] = 0.5 * (adx[i] + adx[i + 1 - period]);
        }
    }
    Dmi { plus_di, minus_di, dx, adx, adxr }
}

fn aroon(high: &[f64], low: &[f64], period: usize) -> (Vec<f64>, Vec<f64>, Vec<f64>) {
    let len = high.len();
    let imax = rolling::arg_max(high, period + 1);
    let imin = rolling::arg_min(low, period + 1);
    let mut up = nans(len);
    let mut dn = nans(len);
    let mut osc = nans(len);
    let pf = period as f64;
    for i in 0..len {
        if imax[i] < 0 {
            continue;
        }
        let ii = i as isize;
        up[i] = 100.0 * (period as isize - (ii - imax[i])) as f64 / pf;
        dn[i] = 100.0 * (period as isize - (ii - imin[i])) as f64 / pf;
        osc[i] = up[i] - dn[i];
    }
    (up, dn, osc)
}

fn vortex(high: &[f64], low: &[f64], close: &[f64], period: usize) -> (Vec<f64>, Vec<f64>) {
    let len = close.len();
    let mut vp = nans(len);
    let mut vm = nans(len);
    let mut tr = true_range(high, low, close);
    if !tr.is_empty() {
        tr[0] = f64::NAN;
    }
    for i in 1..len {
        vp[i] = (high[i] - low[i - 1]).abs();
        vm[i] = (low[i] - high[i - 1]).abs();
    }
    let mut sp = rolling::sum(&vp, period);
    let mut sm = rolling::sum(&vm, period);
    let st = rolling::sum(&tr, period);
    for i in 0..len {
        sp[i] /= st[i];
        sm[i] /= st[i];
    }
    (sp, sm)
}

struct LinReg {
    value: Vec<f64>,
    slope: Vec<f64>,
    intercept: Vec<f64>,
    angle: Vec<f64>,
    forecast: Vec<f64>,
}

fn linreg(x: &[f64], period: usize) -> LinReg {
    let len = x.len();
    let mut value = nans(len);
    let mut slope = nans(len);
    let mut intercept = nans(len);
    let mut angle = nans(len);
    let mut forecast = nans(len);
    let s = first_valid(x);
    if len - s < period {
        return LinReg { value, slope, intercept, angle, forecast };
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
        intercept[i] = b;
        value[i] = b + m * (pf - 1.0);
        forecast[i] = b + m * pf;
        angle[i] = m.atan() * 180.0 / std::f64::consts::PI;
    }
    LinReg { value, slope, intercept, angle, forecast }
}

#[derive(Clone, Copy, PartialEq)]
enum Pivot {
    Classic,
    Fibonacci,
    Camarilla,
}

fn pivot_points(high: &[f64], low: &[f64], close: &[f64], method: Pivot) -> [Vec<f64>; 7] {
    let len = close.len();
    let mut o: [Vec<f64>; 7] = std::array::from_fn(|_| nans(len));
    for i in 1..len {
        let (h, l, c) = (high[i - 1], low[i - 1], close[i - 1]);
        let range = h - l;
        let pp = (h + l + c) / 3.0;
        o[0][i] = pp;
        let v = match method {
            Pivot::Fibonacci => [
                pp + 0.382 * range,
                pp - 0.382 * range,
                pp + 0.618 * range,
                pp - 0.618 * range,
                pp + range,
                pp - range,
            ],
            Pivot::Camarilla => [
                c + range * 1.1 / 12.0,
                c - range * 1.1 / 12.0,
                c + range * 1.1 / 6.0,
                c - range * 1.1 / 6.0,
                c + range * 1.1 / 4.0,
                c - range * 1.1 / 4.0,
            ],
            Pivot::Classic => [
                2.0 * pp - l,
                2.0 * pp - h,
                pp + range,
                pp - range,
                h + 2.0 * (pp - l),
                l - 2.0 * (h - pp),
            ],
        };
        for (k, vk) in v.into_iter().enumerate() {
            o[k + 1][i] = vk;
        }
    }
    o
}
