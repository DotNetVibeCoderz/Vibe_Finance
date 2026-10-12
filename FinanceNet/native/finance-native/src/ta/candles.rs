//! Catalogue entries: the CDL_* candlestick patterns (port of `Candlesticks.cs`).
//! Signals: +100 bullish, -100 bearish, 0 none, marked on the bar that completes the pattern.

use super::core::{net_max, net_min};
use super::{one, Ohlcv, Output};

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum P {
    Doji, DragonflyDoji, GravestoneDoji, LongLeggedDoji, Hammer, InvertedHammer, HangingMan, ShootingStar,
    BullishMarubozu, BearishMarubozu, SpinningTop, BullishEngulfing, BearishEngulfing, BullishHarami, BearishHarami,
    PiercingLine, DarkCloudCover, MorningStar, EveningStar, ThreeWhiteSoldiers, ThreeBlackCrows, TweezerTop,
    TweezerBottom, ThreeInsideUp, ThreeInsideDown, BullishBeltHold, BearishBeltHold, InsideBar, OutsideBar,
    BullishKicker, BearishKicker, RisingThreeMethods, FallingThreeMethods,
    HighWave, Takuri, BullishDojiStar, BearishDojiStar, OnNeck, InNeck, Thrusting, MatchingLow, HomingPigeon,
    BullishSeparatingLines, BearishSeparatingLines, BullishCounterattack, BearishCounterattack,
    ThreeOutsideUp, ThreeOutsideDown, BullishAbandonedBaby, BearishAbandonedBaby, MorningDojiStar, EveningDojiStar,
    TwoCrows, UpsideGapTwoCrows, IdenticalThreeCrows, AdvanceBlock, UpsideTasukiGap, DownsideTasukiGap, StickSandwich,
    ThreeLineStrike,
}

/// (id, pattern) in enum declaration order.
const TABLE: &[(&str, P)] = &[
    ("CDL_DOJI", P::Doji),
    ("CDL_DRAGONFLYDOJI", P::DragonflyDoji),
    ("CDL_GRAVESTONEDOJI", P::GravestoneDoji),
    ("CDL_LONGLEGGEDDOJI", P::LongLeggedDoji),
    ("CDL_HAMMER", P::Hammer),
    ("CDL_INVERTEDHAMMER", P::InvertedHammer),
    ("CDL_HANGINGMAN", P::HangingMan),
    ("CDL_SHOOTINGSTAR", P::ShootingStar),
    ("CDL_BULLISHMARUBOZU", P::BullishMarubozu),
    ("CDL_BEARISHMARUBOZU", P::BearishMarubozu),
    ("CDL_SPINNINGTOP", P::SpinningTop),
    ("CDL_BULLISHENGULFING", P::BullishEngulfing),
    ("CDL_BEARISHENGULFING", P::BearishEngulfing),
    ("CDL_BULLISHHARAMI", P::BullishHarami),
    ("CDL_BEARISHHARAMI", P::BearishHarami),
    ("CDL_PIERCINGLINE", P::PiercingLine),
    ("CDL_DARKCLOUDCOVER", P::DarkCloudCover),
    ("CDL_MORNINGSTAR", P::MorningStar),
    ("CDL_EVENINGSTAR", P::EveningStar),
    ("CDL_THREEWHITESOLDIERS", P::ThreeWhiteSoldiers),
    ("CDL_THREEBLACKCROWS", P::ThreeBlackCrows),
    ("CDL_TWEEZERTOP", P::TweezerTop),
    ("CDL_TWEEZERBOTTOM", P::TweezerBottom),
    ("CDL_THREEINSIDEUP", P::ThreeInsideUp),
    ("CDL_THREEINSIDEDOWN", P::ThreeInsideDown),
    ("CDL_BULLISHBELTHOLD", P::BullishBeltHold),
    ("CDL_BEARISHBELTHOLD", P::BearishBeltHold),
    ("CDL_INSIDEBAR", P::InsideBar),
    ("CDL_OUTSIDEBAR", P::OutsideBar),
    ("CDL_BULLISHKICKER", P::BullishKicker),
    ("CDL_BEARISHKICKER", P::BearishKicker),
    ("CDL_RISINGTHREEMETHODS", P::RisingThreeMethods),
    ("CDL_FALLINGTHREEMETHODS", P::FallingThreeMethods),
    ("CDL_HIGHWAVE", P::HighWave),
    ("CDL_TAKURI", P::Takuri),
    ("CDL_BULLISHDOJISTAR", P::BullishDojiStar),
    ("CDL_BEARISHDOJISTAR", P::BearishDojiStar),
    ("CDL_ONNECK", P::OnNeck),
    ("CDL_INNECK", P::InNeck),
    ("CDL_THRUSTING", P::Thrusting),
    ("CDL_MATCHINGLOW", P::MatchingLow),
    ("CDL_HOMINGPIGEON", P::HomingPigeon),
    ("CDL_BULLISHSEPARATINGLINES", P::BullishSeparatingLines),
    ("CDL_BEARISHSEPARATINGLINES", P::BearishSeparatingLines),
    ("CDL_BULLISHCOUNTERATTACK", P::BullishCounterattack),
    ("CDL_BEARISHCOUNTERATTACK", P::BearishCounterattack),
    ("CDL_THREEOUTSIDEUP", P::ThreeOutsideUp),
    ("CDL_THREEOUTSIDEDOWN", P::ThreeOutsideDown),
    ("CDL_BULLISHABANDONEDBABY", P::BullishAbandonedBaby),
    ("CDL_BEARISHABANDONEDBABY", P::BearishAbandonedBaby),
    ("CDL_MORNINGDOJISTAR", P::MorningDojiStar),
    ("CDL_EVENINGDOJISTAR", P::EveningDojiStar),
    ("CDL_TWOCROWS", P::TwoCrows),
    ("CDL_UPSIDEGAPTWOCROWS", P::UpsideGapTwoCrows),
    ("CDL_IDENTICALTHREECROWS", P::IdenticalThreeCrows),
    ("CDL_ADVANCEBLOCK", P::AdvanceBlock),
    ("CDL_UPSIDETASUKIGAP", P::UpsideTasukiGap),
    ("CDL_DOWNSIDETASUKIGAP", P::DownsideTasukiGap),
    ("CDL_STICKSANDWICH", P::StickSandwich),
    ("CDL_THREELINESTRIKE", P::ThreeLineStrike),
];

/// Ids implemented by this module.
pub const IDS: &[&str] = &[
    "CDL_DOJI", "CDL_DRAGONFLYDOJI", "CDL_GRAVESTONEDOJI", "CDL_LONGLEGGEDDOJI", "CDL_HAMMER",
    "CDL_INVERTEDHAMMER", "CDL_HANGINGMAN", "CDL_SHOOTINGSTAR", "CDL_BULLISHMARUBOZU", "CDL_BEARISHMARUBOZU",
    "CDL_SPINNINGTOP", "CDL_BULLISHENGULFING", "CDL_BEARISHENGULFING", "CDL_BULLISHHARAMI", "CDL_BEARISHHARAMI",
    "CDL_PIERCINGLINE", "CDL_DARKCLOUDCOVER", "CDL_MORNINGSTAR", "CDL_EVENINGSTAR", "CDL_THREEWHITESOLDIERS",
    "CDL_THREEBLACKCROWS", "CDL_TWEEZERTOP", "CDL_TWEEZERBOTTOM", "CDL_THREEINSIDEUP", "CDL_THREEINSIDEDOWN",
    "CDL_BULLISHBELTHOLD", "CDL_BEARISHBELTHOLD", "CDL_INSIDEBAR", "CDL_OUTSIDEBAR", "CDL_BULLISHKICKER",
    "CDL_BEARISHKICKER", "CDL_RISINGTHREEMETHODS", "CDL_FALLINGTHREEMETHODS", "CDL_HIGHWAVE", "CDL_TAKURI",
    "CDL_BULLISHDOJISTAR", "CDL_BEARISHDOJISTAR", "CDL_ONNECK", "CDL_INNECK", "CDL_THRUSTING", "CDL_MATCHINGLOW",
    "CDL_HOMINGPIGEON", "CDL_BULLISHSEPARATINGLINES", "CDL_BEARISHSEPARATINGLINES", "CDL_BULLISHCOUNTERATTACK",
    "CDL_BEARISHCOUNTERATTACK", "CDL_THREEOUTSIDEUP", "CDL_THREEOUTSIDEDOWN", "CDL_BULLISHABANDONEDBABY",
    "CDL_BEARISHABANDONEDBABY", "CDL_MORNINGDOJISTAR", "CDL_EVENINGDOJISTAR", "CDL_TWOCROWS",
    "CDL_UPSIDEGAPTWOCROWS", "CDL_IDENTICALTHREECROWS", "CDL_ADVANCEBLOCK", "CDL_UPSIDETASUKIGAP",
    "CDL_DOWNSIDETASUKIGAP", "CDL_STICKSANDWICH", "CDL_THREELINESTRIKE",
];

pub fn compute(id: &str, x: &Ohlcv, _p: &[f64]) -> Output {
    let (name, pat) = TABLE.iter().find(|(k, _)| *k == id)?;
    let bars = to_bars(x);
    let v = (0..bars.len()).map(|i| at(&bars, i, *pat) as f64).collect();
    Some(one(name, v))
}

#[derive(Clone, Copy)]
struct Bar {
    open: f64,
    high: f64,
    low: f64,
    close: f64,
}

impl Bar {
    #[inline]
    fn range(&self) -> f64 {
        self.high - self.low
    }
}

fn to_bars(x: &Ohlcv) -> Vec<Bar> {
    let n = x.open.len().min(x.high.len()).min(x.low.len()).min(x.close.len());
    (0..n)
        .map(|i| Bar { open: x.open[i], high: x.high[i], low: x.low[i], close: x.close[i] })
        .collect()
}

const DOJI_BODY: f64 = 0.1;
const LONG_SHADOW: f64 = 2.0;

#[inline]
fn body(b: &Bar) -> f64 {
    (b.close - b.open).abs()
}
#[inline]
fn upper(b: &Bar) -> f64 {
    b.high - net_max(b.open, b.close)
}
#[inline]
fn lower(b: &Bar) -> f64 {
    net_min(b.open, b.close) - b.low
}
#[inline]
fn bull(b: &Bar) -> bool {
    b.close > b.open
}
#[inline]
fn bear(b: &Bar) -> bool {
    b.close < b.open
}
#[inline]
fn mid(b: &Bar) -> f64 {
    (b.open + b.close) / 2.0
}

fn avg_body(bars: &[Bar], i: usize) -> f64 {
    let lookback = 10usize;
    let start = i.saturating_sub(lookback);
    if start >= i {
        return body(&bars[i]);
    }
    let mut s = 0.0;
    for b in &bars[start..i] {
        s += body(b);
    }
    s / (i - start) as f64
}

fn trend(bars: &[Bar], i: usize) -> i32 {
    if i < 5 {
        return 0;
    }
    let mut s = 0.0;
    for b in &bars[i - 5..i] {
        s += b.close;
    }
    let sma = s / 5.0;
    let c = bars[i - 1].close;
    if c > sma {
        1
    } else if c < sma {
        -1
    } else {
        0
    }
}

#[inline]
fn is_doji(b: &Bar) -> bool {
    b.range() > 0.0 && body(b) <= DOJI_BODY * b.range()
}
#[inline]
fn is_long(bars: &[Bar], i: usize) -> bool {
    body(&bars[i]) > avg_body(bars, i)
}
#[inline]
fn near(a: f64, b: f64, x: &Bar, y: &Bar) -> bool {
    (a - b).abs() <= 0.05 * net_max(x.range(), y.range())
}
#[inline]
fn body_top(b: &Bar) -> f64 {
    net_max(b.open, b.close)
}
#[inline]
fn body_bottom(b: &Bar) -> f64 {
    net_min(b.open, b.close)
}

#[inline]
fn sig(c: bool, s: i32) -> i32 {
    if c { s } else { 0 }
}

fn at(bars: &[Bar], i: usize, pattern: P) -> i32 {
    let b = &bars[i];
    let range = b.range();
    let body_b = body(b);
    match pattern {
        P::Doji => return sig(is_doji(b), 100),
        P::DragonflyDoji => {
            return sig(is_doji(b) && upper(b) <= 0.1 * range && lower(b) >= 0.6 * range, 100)
        }
        P::GravestoneDoji => {
            return sig(is_doji(b) && lower(b) <= 0.1 * range && upper(b) >= 0.6 * range, -100)
        }
        P::LongLeggedDoji => {
            return sig(is_doji(b) && upper(b) >= 0.3 * range && lower(b) >= 0.3 * range, 100)
        }
        P::Hammer => {
            return sig(
                body_b > 0.0
                    && lower(b) >= LONG_SHADOW * body_b
                    && upper(b) <= 0.3 * body_b + 0.05 * range
                    && trend(bars, i) < 0,
                100,
            )
        }
        P::HangingMan => {
            return sig(
                body_b > 0.0
                    && lower(b) >= LONG_SHADOW * body_b
                    && upper(b) <= 0.3 * body_b + 0.05 * range
                    && trend(bars, i) > 0,
                -100,
            )
        }
        P::InvertedHammer => {
            return sig(
                body_b > 0.0
                    && upper(b) >= LONG_SHADOW * body_b
                    && lower(b) <= 0.3 * body_b + 0.05 * range
                    && trend(bars, i) < 0,
                100,
            )
        }
        P::ShootingStar => {
            return sig(
                body_b > 0.0
                    && upper(b) >= LONG_SHADOW * body_b
                    && lower(b) <= 0.3 * body_b + 0.05 * range
                    && trend(bars, i) > 0,
                -100,
            )
        }
        P::BullishMarubozu => {
            return sig(bull(b) && range > 0.0 && body_b >= 0.95 * range && is_long(bars, i), 100)
        }
        P::BearishMarubozu => {
            return sig(bear(b) && range > 0.0 && body_b >= 0.95 * range && is_long(bars, i), -100)
        }
        P::SpinningTop => {
            return sig(
                range > 0.0
                    && body_b > DOJI_BODY * range
                    && body_b <= 0.3 * range
                    && upper(b) > body_b
                    && lower(b) > body_b,
                if bull(b) { 100 } else { -100 },
            )
        }
        P::HighWave => {
            return sig(
                body_b > 0.0 && body_b <= 0.3 * range && upper(b) >= 3.0 * body_b && lower(b) >= 3.0 * body_b,
                if bull(b) { 100 } else { -100 },
            )
        }
        P::Takuri => {
            return sig(
                is_doji(b) && upper(b) <= 0.1 * range && lower(b) >= 0.75 * range && lower(b) >= 3.0 * body_b,
                100,
            )
        }
        _ => {}
    }

    if i < 1 {
        return 0;
    }
    let p = &bars[i - 1];
    match pattern {
        P::BullishEngulfing => {
            return sig(bear(p) && bull(b) && b.open <= p.close && b.close >= p.open && body_b > body(p), 100)
        }
        P::BearishEngulfing => {
            return sig(bull(p) && bear(b) && b.open >= p.close && b.close <= p.open && body_b > body(p), -100)
        }
        P::BullishHarami => {
            return sig(
                bear(p) && is_long(bars, i - 1) && bull(b) && b.open > p.close && b.close < p.open,
                100,
            )
        }
        P::BearishHarami => {
            return sig(
                bull(p) && is_long(bars, i - 1) && bear(b) && b.open < p.close && b.close > p.open,
                -100,
            )
        }
        P::PiercingLine => {
            return sig(
                bear(p)
                    && is_long(bars, i - 1)
                    && bull(b)
                    && b.open < p.low
                    && b.close > mid(p)
                    && b.close < p.open,
                100,
            )
        }
        P::DarkCloudCover => {
            return sig(
                bull(p)
                    && is_long(bars, i - 1)
                    && bear(b)
                    && b.open > p.high
                    && b.close < mid(p)
                    && b.close > p.open,
                -100,
            )
        }
        P::TweezerTop => {
            return sig(
                trend(bars, i - 1) > 0
                    && bull(p)
                    && bear(b)
                    && (b.high - p.high).abs() <= 0.05 * net_max(range, p.range()),
                -100,
            )
        }
        P::TweezerBottom => {
            return sig(
                trend(bars, i - 1) < 0
                    && bear(p)
                    && bull(b)
                    && (b.low - p.low).abs() <= 0.05 * net_max(range, p.range()),
                100,
            )
        }
        P::BullishBeltHold => {
            return sig(
                trend(bars, i) < 0 && bull(b) && is_long(bars, i) && lower(b) <= 0.05 * range && b.open < p.low,
                100,
            )
        }
        P::BearishBeltHold => {
            return sig(
                trend(bars, i) > 0 && bear(b) && is_long(bars, i) && upper(b) <= 0.05 * range && b.open > p.high,
                -100,
            )
        }
        P::InsideBar => {
            return sig(b.high < p.high && b.low > p.low, if bull(b) { 100 } else { -100 })
        }
        P::OutsideBar => {
            return sig(b.high > p.high && b.low < p.low, if bull(b) { 100 } else { -100 })
        }
        P::BullishKicker => {
            return sig(
                bear(p) && bull(b) && b.open > p.open && is_long(bars, i) && is_long(bars, i - 1),
                100,
            )
        }
        P::BearishKicker => {
            return sig(
                bull(p) && bear(b) && b.open < p.open && is_long(bars, i) && is_long(bars, i - 1),
                -100,
            )
        }
        P::BullishDojiStar => {
            return sig(bear(p) && is_long(bars, i - 1) && is_doji(b) && body_top(b) < p.close, 100)
        }
        P::BearishDojiStar => {
            return sig(bull(p) && is_long(bars, i - 1) && is_doji(b) && body_bottom(b) > p.close, -100)
        }
        P::OnNeck => {
            return sig(
                bear(p) && is_long(bars, i - 1) && bull(b) && b.open < p.low && near(b.close, p.low, b, p),
                -100,
            )
        }
        P::InNeck => {
            return sig(
                bear(p)
                    && is_long(bars, i - 1)
                    && bull(b)
                    && b.open < p.low
                    && b.close >= p.close
                    && b.close <= p.close + 0.1 * body(p),
                -100,
            )
        }
        P::Thrusting => {
            return sig(
                bear(p)
                    && is_long(bars, i - 1)
                    && bull(b)
                    && b.open < p.low
                    && b.close > p.close + 0.1 * body(p)
                    && b.close < mid(p),
                -100,
            )
        }
        P::MatchingLow => {
            return sig(
                trend(bars, i - 1) < 0
                    && bear(p)
                    && is_long(bars, i - 1)
                    && bear(b)
                    && near(b.close, p.close, b, p),
                100,
            )
        }
        P::HomingPigeon => {
            return sig(
                trend(bars, i - 1) < 0
                    && bear(p)
                    && is_long(bars, i - 1)
                    && bear(b)
                    && b.open < p.open
                    && b.close > p.close,
                100,
            )
        }
        P::BullishSeparatingLines => {
            return sig(
                trend(bars, i - 1) > 0
                    && bear(p)
                    && bull(b)
                    && is_long(bars, i)
                    && near(b.open, p.open, b, p)
                    && lower(b) <= 0.05 * range,
                100,
            )
        }
        P::BearishSeparatingLines => {
            return sig(
                trend(bars, i - 1) < 0
                    && bull(p)
                    && bear(b)
                    && is_long(bars, i)
                    && near(b.open, p.open, b, p)
                    && upper(b) <= 0.05 * range,
                -100,
            )
        }
        P::BullishCounterattack => {
            return sig(
                bear(p)
                    && is_long(bars, i - 1)
                    && bull(b)
                    && is_long(bars, i)
                    && b.open < p.close
                    && near(b.close, p.close, b, p),
                100,
            )
        }
        P::BearishCounterattack => {
            return sig(
                bull(p)
                    && is_long(bars, i - 1)
                    && bear(b)
                    && is_long(bars, i)
                    && b.open > p.close
                    && near(b.close, p.close, b, p),
                -100,
            )
        }
        _ => {}
    }

    if i < 2 {
        return 0;
    }
    let q = &bars[i - 2];
    match pattern {
        P::MorningStar => {
            return sig(
                bear(q)
                    && is_long(bars, i - 2)
                    && body(p) <= 0.3 * body(q)
                    && net_max(p.open, p.close) < q.close
                    && bull(b)
                    && b.close > mid(q),
                100,
            )
        }
        P::EveningStar => {
            return sig(
                bull(q)
                    && is_long(bars, i - 2)
                    && body(p) <= 0.3 * body(q)
                    && net_min(p.open, p.close) > q.close
                    && bear(b)
                    && b.close < mid(q),
                -100,
            )
        }
        P::ThreeWhiteSoldiers => {
            return sig(
                bull(q)
                    && bull(p)
                    && bull(b)
                    && p.close > q.close
                    && b.close > p.close
                    && p.open > q.open
                    && p.open < q.close
                    && b.open > p.open
                    && b.open < p.close
                    && upper(b) <= 0.3 * body_b
                    && upper(p) <= 0.3 * body(p),
                100,
            )
        }
        P::ThreeBlackCrows => {
            return sig(
                bear(q)
                    && bear(p)
                    && bear(b)
                    && p.close < q.close
                    && b.close < p.close
                    && p.open < q.open
                    && p.open > q.close
                    && b.open < p.open
                    && b.open > p.close
                    && lower(b) <= 0.3 * body_b
                    && lower(p) <= 0.3 * body(p),
                -100,
            )
        }
        P::ThreeInsideUp => {
            return sig(
                bear(q)
                    && is_long(bars, i - 2)
                    && bull(p)
                    && p.open > q.close
                    && p.close < q.open
                    && bull(b)
                    && b.close > q.open,
                100,
            )
        }
        P::ThreeInsideDown => {
            return sig(
                bull(q)
                    && is_long(bars, i - 2)
                    && bear(p)
                    && p.open < q.close
                    && p.close > q.open
                    && bear(b)
                    && b.close < q.open,
                -100,
            )
        }
        P::ThreeOutsideUp => {
            return sig(
                bear(q)
                    && bull(p)
                    && p.open <= q.close
                    && p.close >= q.open
                    && body(p) > body(q)
                    && b.close > p.close,
                100,
            )
        }
        P::ThreeOutsideDown => {
            return sig(
                bull(q)
                    && bear(p)
                    && p.open >= q.close
                    && p.close <= q.open
                    && body(p) > body(q)
                    && b.close < p.close,
                -100,
            )
        }
        P::BullishAbandonedBaby => {
            return sig(
                bear(q)
                    && is_long(bars, i - 2)
                    && is_doji(p)
                    && p.high < q.low
                    && bull(b)
                    && b.low > p.high
                    && b.close > mid(q),
                100,
            )
        }
        P::BearishAbandonedBaby => {
            return sig(
                bull(q)
                    && is_long(bars, i - 2)
                    && is_doji(p)
                    && p.low > q.high
                    && bear(b)
                    && b.high < p.low
                    && b.close < mid(q),
                -100,
            )
        }
        P::MorningDojiStar => {
            return sig(
                bear(q)
                    && is_long(bars, i - 2)
                    && is_doji(p)
                    && body_top(p) < q.close
                    && bull(b)
                    && b.close > mid(q),
                100,
            )
        }
        P::EveningDojiStar => {
            return sig(
                bull(q)
                    && is_long(bars, i - 2)
                    && is_doji(p)
                    && body_bottom(p) > q.close
                    && bear(b)
                    && b.close < mid(q),
                -100,
            )
        }
        P::TwoCrows => {
            return sig(
                trend(bars, i - 2) > 0
                    && bull(q)
                    && is_long(bars, i - 2)
                    && bear(p)
                    && body_bottom(p) > q.close
                    && bear(b)
                    && b.open < p.open
                    && b.open > p.close
                    && b.close > q.open
                    && b.close < q.close,
                -100,
            )
        }
        P::UpsideGapTwoCrows => {
            return sig(
                trend(bars, i - 2) > 0
                    && bull(q)
                    && is_long(bars, i - 2)
                    && bear(p)
                    && body_bottom(p) > q.close
                    && bear(b)
                    && b.open > p.open
                    && b.close < p.close
                    && b.close > q.close,
                -100,
            )
        }
        P::IdenticalThreeCrows => {
            return sig(
                bear(q)
                    && bear(p)
                    && bear(b)
                    && is_long(bars, i - 2)
                    && p.close < q.close
                    && b.close < p.close
                    && near(p.open, q.close, p, q)
                    && near(b.open, p.close, b, p),
                -100,
            )
        }
        P::AdvanceBlock => {
            return sig(
                trend(bars, i - 2) > 0
                    && bull(q)
                    && bull(p)
                    && bull(b)
                    && p.close > q.close
                    && b.close > p.close
                    && p.open > q.open
                    && p.open <= q.close
                    && b.open > p.open
                    && b.open <= p.close
                    && body(p) < body(q)
                    && body_b < body(p)
                    && upper(b) > upper(q),
                -100,
            )
        }
        P::UpsideTasukiGap => {
            return sig(
                bull(q)
                    && bull(p)
                    && p.open > q.high
                    && bear(b)
                    && b.open > p.open
                    && b.open < p.close
                    && b.close < p.open
                    && b.close > q.high,
                100,
            )
        }
        P::DownsideTasukiGap => {
            return sig(
                bear(q)
                    && bear(p)
                    && p.open < q.low
                    && bull(b)
                    && b.open < p.open
                    && b.open > p.close
                    && b.close > p.open
                    && b.close < q.low,
                -100,
            )
        }
        P::StickSandwich => {
            return sig(
                bear(q) && bull(p) && p.low > q.close && bear(b) && near(b.close, q.close, b, q),
                100,
            )
        }
        _ => {}
    }

    if i < 3 {
        return 0;
    }
    if pattern == P::ThreeLineStrike {
        let a = &bars[i - 3];
        if bull(a)
            && bull(q)
            && bull(p)
            && q.close > a.close
            && p.close > q.close
            && bear(b)
            && b.open > p.close
            && b.close < a.open
        {
            return 100;
        }
        if bear(a)
            && bear(q)
            && bear(p)
            && q.close < a.close
            && p.close < q.close
            && bull(b)
            && b.open < p.close
            && b.close > a.open
        {
            return -100;
        }
        return 0;
    }

    if i < 4 {
        return 0;
    }
    let first = &bars[i - 4];
    match pattern {
        P::RisingThreeMethods => {
            if !(bull(first) && is_long(bars, i - 4) && bull(b) && is_long(bars, i) && b.close > first.close) {
                return 0;
            }
            for k in &bars[i - 3..i] {
                if k.high > first.high || k.low < first.low || body(k) >= body(first) {
                    return 0;
                }
            }
            100
        }
        P::FallingThreeMethods => {
            if !(bear(first) && is_long(bars, i - 4) && bear(b) && is_long(bars, i) && b.close < first.close) {
                return 0;
            }
            for k in &bars[i - 3..i] {
                if k.high > first.high || k.low < first.low || body(k) >= body(first) {
                    return 0;
                }
            }
            -100
        }
        _ => 0,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn bar(o: f64, h: f64, l: f64, c: f64) -> Bar {
        Bar { open: o, high: h, low: l, close: c }
    }

    /// Five small flat-ish bars establishing a trend (+1 rising closes, -1 falling closes), body ~0.5.
    fn ctx(up: bool) -> Vec<Bar> {
        (0..6)
            .map(|k| {
                let base = if up { 100.0 + k as f64 } else { 110.0 - k as f64 };
                bar(base, base + 0.8, base - 0.8, if up { base + 0.5 } else { base - 0.5 })
            })
            .collect()
    }

    fn last(bars: &[Bar], p: P) -> i32 {
        at(bars, bars.len() - 1, p)
    }

    #[test]
    fn patterns_fire() {
        // Single-bar.
        assert_eq!(last(&[bar(10.0, 11.0, 9.0, 10.05)], P::Doji), 100);
        assert_eq!(last(&[bar(10.9, 11.0, 9.0, 10.95)], P::DragonflyDoji), 100);
        assert_eq!(last(&[bar(9.05, 11.0, 9.0, 9.1)], P::GravestoneDoji), -100);
        assert_eq!(last(&[bar(10.0, 11.0, 9.0, 10.05)], P::LongLeggedDoji), 100);
        assert_eq!(last(&[bar(10.0, 11.0, 9.0, 10.4)], P::SpinningTop), 100);

        // Hammer in a downtrend / shooting star in an uptrend.
        let mut v = ctx(false);
        let c = v.last().unwrap().close;
        v.push(bar(c - 1.0, c - 0.5, c - 4.0, c - 0.5));
        assert_eq!(last(&v, P::Hammer), 100);
        assert_eq!(last(&v, P::HangingMan), 0);
        let mut v = ctx(true);
        let c = v.last().unwrap().close;
        v.push(bar(c + 1.0, c + 4.0, c + 0.5, c + 0.5));
        assert_eq!(last(&v, P::ShootingStar), -100);

        // Marubozu after small bodies.
        let mut v = ctx(true);
        v.push(bar(106.0, 110.0, 106.0, 110.0));
        assert_eq!(last(&v, P::BullishMarubozu), 100);

        // Engulfing.
        let v = vec![bar(10.0, 10.2, 9.4, 9.5), bar(9.4, 10.3, 9.3, 10.2)];
        assert_eq!(last(&v, P::BullishEngulfing), 100);
        assert_eq!(last(&v, P::OutsideBar), 100);
        let v = vec![bar(9.5, 10.1, 9.4, 10.0), bar(10.1, 10.2, 9.3, 9.4)];
        assert_eq!(last(&v, P::BearishEngulfing), -100);

        // Harami: long bear then small bull inside.
        let v = vec![bar(10.0, 10.1, 9.9, 10.05), bar(10.0, 10.1, 7.9, 8.0), bar(8.5, 9.0, 8.4, 8.9)];
        assert_eq!(last(&v, P::BullishHarami), 100);
        assert_eq!(last(&v, P::InsideBar), 100);

        // Piercing line.
        let v = vec![bar(10.0, 10.1, 9.9, 10.05), bar(10.0, 10.1, 7.9, 8.0), bar(7.5, 9.6, 7.4, 9.5)];
        assert_eq!(last(&v, P::PiercingLine), 100);

        // Morning star.
        let v = vec![
            bar(10.0, 10.1, 9.9, 10.05),
            bar(10.0, 10.1, 7.9, 8.0),
            bar(7.6, 7.8, 7.4, 7.7),
            bar(7.8, 9.6, 7.7, 9.5),
        ];
        assert_eq!(last(&v, P::MorningStar), 100);

        // Evening doji star.
        let v = vec![
            bar(10.0, 10.1, 9.9, 10.05),
            bar(8.0, 10.1, 7.9, 10.0),
            bar(10.5, 10.8, 10.2, 10.51),
            bar(10.3, 10.4, 8.4, 8.5),
        ];
        assert_eq!(last(&v, P::EveningDojiStar), -100);

        // Three white soldiers / three black crows.
        let v = vec![bar(10.0, 11.05, 9.9, 11.0), bar(10.5, 12.05, 10.4, 12.0), bar(11.5, 13.05, 11.4, 13.0)];
        assert_eq!(last(&v, P::ThreeWhiteSoldiers), 100);
        let v = vec![bar(13.0, 13.1, 11.95, 12.0), bar(12.5, 12.6, 10.95, 11.0), bar(11.5, 11.6, 9.95, 10.0)];
        assert_eq!(last(&v, P::ThreeBlackCrows), -100);

        // Three line strike (bullish sequence wiped out -> +100 per TA-Lib convention).
        let v = vec![
            bar(10.0, 11.05, 9.9, 11.0),
            bar(10.5, 12.05, 10.4, 12.0),
            bar(11.5, 13.05, 11.4, 13.0),
            bar(13.5, 13.6, 9.4, 9.5),
        ];
        assert_eq!(last(&v, P::ThreeLineStrike), 100);

        // Rising three methods.
        let v = vec![
            bar(10.0, 10.1, 9.9, 10.05),
            bar(10.0, 12.1, 9.9, 12.0),
            bar(11.8, 11.9, 11.4, 11.5),
            bar(11.5, 11.6, 11.1, 11.2),
            bar(11.2, 11.3, 10.9, 11.0),
            bar(11.0, 12.6, 10.9, 12.5),
        ];
        assert_eq!(last(&v, P::RisingThreeMethods), 100);

        // Upside tasuki gap.
        let v = vec![bar(10.0, 11.0, 9.9, 11.0), bar(11.5, 12.6, 11.4, 12.5), bar(12.0, 12.1, 11.2, 11.3)];
        assert_eq!(last(&v, P::UpsideTasukiGap), 100);

        // Stick sandwich.
        let v = vec![bar(11.0, 11.1, 9.9, 10.0), bar(10.2, 11.1, 10.1, 11.0), bar(11.0, 11.1, 9.95, 10.01)];
        assert_eq!(last(&v, P::StickSandwich), 100);
    }

    #[test]
    fn short_and_empty_inputs() {
        let x = Ohlcv { open: &[], high: &[], low: &[], close: &[], volume: &[], day: &[] };
        for id in IDS {
            let r = compute(id, &x, &[]).unwrap().unwrap();
            assert!(r[0].1.is_empty());
        }
        let o = [1.0];
        let x = Ohlcv { open: &o, high: &o, low: &o, close: &o, volume: &o, day: &o };
        for id in IDS {
            assert_eq!(compute(id, &x, &[]).unwrap().unwrap()[0].1.len(), 1);
        }
        assert_eq!(IDS.len(), TABLE.len());
        for (k, (id, _)) in TABLE.iter().enumerate() {
            assert_eq!(IDS[k], *id);
        }
        assert!(compute("SMA", &x, &[]).is_none());
    }
}
