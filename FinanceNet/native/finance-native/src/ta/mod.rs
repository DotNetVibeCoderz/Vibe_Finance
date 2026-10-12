//! The full Finance.TimeSeries indicator catalogue in Rust. `compute(id, ...)` mirrors
//! `IndicatorCatalog.Get(id).Compute(frame, parameters)`: same ids, same parameter order (as declared in the
//! catalogue), same output series names and order, same NaN warm-up. The managed implementation is the reference;
//! Finance.Native.Tests compares every entry.
//!
//! Module layout (each owns a contiguous block of IndicatorCatalog.cs registrations):
//! * `trend`    — first-wave trend / overlap entries
//! * `momentum` — first-wave momentum entries
//! * `volume`   — first-wave volatility, volume, statistics and price-transform entries
//! * `candles`  — the CDL_* candlestick patterns
//! * `extended_a` — second wave: cycles and trend
//! * `extended_b` — second wave: momentum, volatility, volume, statistics, price transforms

pub mod candles;
pub mod core;
pub mod extended_a;
pub mod extended_b;
pub mod momentum;
pub mod rolling;
pub mod trend;
pub mod volume;

/// Bar data. All slices have the same length.
pub struct Ohlcv<'a> {
    pub open: &'a [f64],
    pub high: &'a [f64],
    pub low: &'a [f64],
    pub close: &'a [f64],
    pub volume: &'a [f64],
    /// Calendar day of each bar (any monotone day number, e.g. days since 0001-01-01); used by session indicators.
    pub day: &'a [f64],
}

impl Ohlcv<'_> {
    pub fn len(&self) -> usize {
        self.close.len()
    }

    pub fn is_empty(&self) -> bool {
        self.close.is_empty()
    }
}

/// One named output series.
pub type Series = (String, Vec<f64>);

/// `None` = the id is not handled by this module; `Some(Err)` = invalid arguments (the managed code would throw).
pub type Output = Option<Result<Vec<Series>, String>>;

/// Catalogue parameter `i` as a whole number, exactly like `IndicatorInput.N`: max(1, Math.Round(v)) with
/// banker's rounding.
#[inline]
pub fn n(p: &[f64], i: usize) -> usize {
    let v = core::net_round(p[i]);
    if v >= 1.0 { v as usize } else { 1 }
}

/// Catalogue parameter `i` as a real number (`IndicatorInput.P`).
#[inline]
pub fn p(p: &[f64], i: usize) -> f64 {
    p[i]
}

/// Single named output.
pub fn one(name: &str, v: Vec<f64>) -> Result<Vec<Series>, String> {
    Ok(vec![(name.to_string(), v)])
}

/// Several named outputs.
pub fn many(items: Vec<(&str, Vec<f64>)>) -> Result<Vec<Series>, String> {
    Ok(items.into_iter().map(|(k, v)| (k.to_string(), v)).collect())
}

/// Computes catalogue entry `id` (case-insensitive) with `params` in declaration order.
pub fn compute(id: &str, x: &Ohlcv, params: &[f64]) -> Output {
    let id = id.to_ascii_uppercase();
    let id = id.as_str();
    trend::compute(id, x, params)
        .or_else(|| momentum::compute(id, x, params))
        .or_else(|| volume::compute(id, x, params))
        .or_else(|| extended_a::compute(id, x, params))
        .or_else(|| extended_b::compute(id, x, params))
        .or_else(|| candles::compute(id, x, params))
}

/// Every id the native catalogue implements.
pub fn ids() -> Vec<&'static str> {
    let mut v = Vec::new();
    v.extend_from_slice(trend::IDS);
    v.extend_from_slice(momentum::IDS);
    v.extend_from_slice(volume::IDS);
    v.extend_from_slice(extended_a::IDS);
    v.extend_from_slice(extended_b::IDS);
    v.extend_from_slice(candles::IDS);
    v
}
