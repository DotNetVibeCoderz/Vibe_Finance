//! Option pricing: closed-form Black-Scholes(-Merton) with greeks, Monte Carlo European pricing
//! and geometric Brownian motion path simulation.

use crate::normal::{cdf, pdf};
use crate::rng::{stream_seed, FastRandom};

/// Fixed number of RNG streams for Monte Carlo. Independent of the thread count, so results
/// are identical however many threads run them (and the managed fallback can reproduce them).
pub const MC_CHUNKS: usize = 64;

#[derive(Clone, Copy, Default, Debug, PartialEq)]
pub struct Greeks {
    pub price: f64,
    pub delta: f64,
    pub gamma: f64,
    pub vega: f64,
    pub theta: f64,
    pub rho: f64,
}

/// Black-Scholes-Merton price and greeks. Vega per 1.00 of volatility, theta per year,
/// rho per 1.00 of rate. Degenerate inputs (t ≤ 0 or vol ≤ 0) price at discounted intrinsic.
pub fn black_scholes(s: f64, k: f64, t: f64, r: f64, q: f64, vol: f64, call: bool) -> Greeks {
    if t <= 0.0 {
        let itm = if call { s > k } else { s < k };
        let price = if call { (s - k).max(0.0) } else { (k - s).max(0.0) };
        let delta = if itm { if call { 1.0 } else { -1.0 } } else { 0.0 };
        return Greeks { price, delta, ..Default::default() };
    }
    let dq = (-q * t).exp();
    let dr = (-r * t).exp();
    if vol <= 0.0 {
        let fwd = s * dq - k * dr;
        let (price, delta) = if call {
            if fwd > 0.0 { (fwd, dq) } else { (0.0, 0.0) }
        } else if fwd < 0.0 {
            (-fwd, -dq)
        } else {
            (0.0, 0.0)
        };
        return Greeks { price, delta, ..Default::default() };
    }
    let sqrt_t = t.sqrt();
    let vs = vol * sqrt_t;
    let d1 = ((s / k).ln() + (r - q + 0.5 * vol * vol) * t) / vs;
    let d2 = d1 - vs;
    let nd1 = pdf(d1);
    let gamma = dq * nd1 / (s * vs);
    let vega = s * dq * nd1 * sqrt_t;
    let decay = -s * dq * nd1 * vol / (2.0 * sqrt_t);
    if call {
        let (c1, c2) = (cdf(d1), cdf(d2));
        Greeks {
            price: s * dq * c1 - k * dr * c2,
            delta: dq * c1,
            gamma,
            vega,
            theta: decay - r * k * dr * c2 + q * s * dq * c1,
            rho: k * t * dr * c2,
        }
    } else {
        let (c1, c2) = (cdf(-d1), cdf(-d2));
        Greeks {
            price: k * dr * c2 - s * dq * c1,
            delta: -dq * c1,
            gamma,
            vega,
            theta: decay + r * k * dr * c2 - q * s * dq * c1,
            rho: -k * t * dr * c2,
        }
    }
}

#[derive(Clone, Copy, Debug)]
pub struct McParams {
    pub s: f64,
    pub k: f64,
    pub t: f64,
    pub r: f64,
    pub q: f64,
    pub vol: f64,
    pub call: bool,
    pub antithetic: bool,
}

/// Paths assigned to chunk `c` of `MC_CHUNKS`.
#[inline]
pub fn chunk_paths(paths: usize, c: usize) -> usize {
    paths / MC_CHUNKS + usize::from(c < paths % MC_CHUNKS)
}

fn mc_chunk(p: &McParams, paths: usize, seed: u64) -> (f64, f64) {
    let mut rng = FastRandom::new(seed);
    let drift = (p.r - p.q - 0.5 * p.vol * p.vol) * p.t;
    let diff = p.vol * p.t.sqrt();
    let payoff = |st: f64| if p.call { (st - p.k).max(0.0) } else { (p.k - st).max(0.0) };
    let (mut s1, mut s2) = (0.0, 0.0);
    for _ in 0..paths {
        let z = rng.gaussian();
        let mut v = payoff(p.s * (drift + diff * z).exp());
        if p.antithetic {
            v = 0.5 * (v + payoff(p.s * (drift - diff * z).exp()));
        }
        s1 += v;
        s2 += v * v;
    }
    (s1, s2)
}

/// Monte Carlo European option: returns (price, standard error).
/// `threads == 0` uses all available cores.
pub fn monte_carlo_european(p: &McParams, paths: usize, seed: u64, threads: usize) -> (f64, f64) {
    let threads = if threads == 0 {
        std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1)
    } else {
        threads
    }
    .min(MC_CHUNKS)
    .max(1);
    let mut results = vec![(0.0f64, 0.0f64); MC_CHUNKS];
    if threads == 1 {
        for (c, slot) in results.iter_mut().enumerate() {
            *slot = mc_chunk(p, chunk_paths(paths, c), stream_seed(seed, c as u64));
        }
    } else {
        let per = MC_CHUNKS.div_ceil(threads);
        std::thread::scope(|scope| {
            for (w, block) in results.chunks_mut(per).enumerate() {
                scope.spawn(move || {
                    for (j, slot) in block.iter_mut().enumerate() {
                        let c = w * per + j;
                        *slot = mc_chunk(p, chunk_paths(paths, c), stream_seed(seed, c as u64));
                    }
                });
            }
        });
    }
    let (mut s1, mut s2) = (0.0, 0.0);
    for (a, b) in results {
        s1 += a;
        s2 += b;
    }
    let n = paths as f64;
    let mean = s1 / n;
    let var = ((s2 - n * mean * mean) / (n - 1.0)).max(0.0);
    let disc = (-p.r * p.t).exp();
    (disc * mean, disc * (var / n).sqrt())
}

fn gbm_path(out: &mut [f64], s0: f64, mu: f64, sigma: f64, dt: f64, seed: u64) {
    let mut rng = FastRandom::new(seed);
    let drift = (mu - 0.5 * sigma * sigma) * dt;
    let diff = sigma * dt.sqrt();
    let mut s = s0;
    out[0] = s;
    for v in out.iter_mut().skip(1) {
        s *= (drift + diff * rng.gaussian()).exp();
        *v = s;
    }
}

/// GBM paths, row-major `paths x (steps + 1)`. Path `p` uses stream `stream_seed(seed, p)`.
pub fn simulate_gbm(out: &mut [f64], s0: f64, mu: f64, sigma: f64, dt: f64, steps: usize, paths: usize, seed: u64, threads: usize) {
    let width = steps + 1;
    let threads = if threads == 0 {
        std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1)
    } else {
        threads
    }
    .max(1);
    if threads == 1 || paths < 64 {
        for (p, row) in out.chunks_mut(width).enumerate().take(paths) {
            gbm_path(row, s0, mu, sigma, dt, stream_seed(seed, p as u64));
        }
        return;
    }
    let per = paths.div_ceil(threads);
    std::thread::scope(|scope| {
        for (w, block) in out[..paths * width].chunks_mut(per * width).enumerate() {
            scope.spawn(move || {
                for (j, row) in block.chunks_mut(width).enumerate() {
                    let p = w * per + j;
                    gbm_path(row, s0, mu, sigma, dt, stream_seed(seed, p as u64));
                }
            });
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn put_call_parity() {
        let c = black_scholes(100.0, 95.0, 0.75, 0.04, 0.01, 0.3, true);
        let p = black_scholes(100.0, 95.0, 0.75, 0.04, 0.01, 0.3, false);
        let lhs = c.price - p.price;
        let rhs = 100.0 * (-0.01f64 * 0.75).exp() - 95.0 * (-0.04f64 * 0.75).exp();
        assert!((lhs - rhs).abs() < 1e-12);
    }

    #[test]
    fn textbook_value() {
        // Hull: S=42, K=40, r=10%, sigma=20%, T=0.5 -> call 4.76, put 0.81
        let c = black_scholes(42.0, 40.0, 0.5, 0.1, 0.0, 0.2, true);
        let p = black_scholes(42.0, 40.0, 0.5, 0.1, 0.0, 0.2, false);
        assert!((c.price - 4.759422).abs() < 1e-5);
        assert!((p.price - 0.808600).abs() < 1e-5);
    }

    #[test]
    fn mc_converges_and_is_thread_count_invariant() {
        let p = McParams { s: 100.0, k: 100.0, t: 1.0, r: 0.05, q: 0.0, vol: 0.2, call: true, antithetic: true };
        let (a, se) = monte_carlo_european(&p, 400_000, 7, 1);
        let (b, _) = monte_carlo_european(&p, 400_000, 7, 4);
        assert_eq!(a, b);
        let exact = black_scholes(100.0, 100.0, 1.0, 0.05, 0.0, 0.2, true).price;
        assert!((a - exact).abs() < 4.0 * se, "{a} vs {exact} se {se}");
    }

    #[test]
    fn gbm_thread_invariant() {
        let mut a = vec![0.0; 200 * 11];
        let mut b = vec![0.0; 200 * 11];
        simulate_gbm(&mut a, 100.0, 0.05, 0.2, 1.0 / 252.0, 10, 200, 3, 1);
        simulate_gbm(&mut b, 100.0, 0.05, 0.2, 1.0 / 252.0, 10, 200, 3, 4);
        assert_eq!(a, b);
        assert_eq!(a[0], 100.0);
    }
}
