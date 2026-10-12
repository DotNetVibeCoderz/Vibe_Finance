//! Catalogue entries: see ta/mod.rs for the module layout.

use super::{Ohlcv, Output};

/// Ids implemented by this module.
pub const IDS: &[&str] = &[];

pub fn compute(id: &str, _x: &Ohlcv, _p: &[f64]) -> Output {
    match id {
        _ => None,
    }
}
