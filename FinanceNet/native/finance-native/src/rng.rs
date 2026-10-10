//! xoshiro256** with SplitMix64 seeding and a Marsaglia polar normal sampler.
//! Bit-for-bit the same algorithm as `Finance.Core.FastRandom`, so the managed fallback
//! reproduces native Monte Carlo results.

const GOLDEN: u64 = 0x9E37_79B9_7F4A_7C15;

pub struct FastRandom {
    s: [u64; 4],
    spare: f64,
    has_spare: bool,
}

#[inline]
fn split_mix(x: &mut u64) -> u64 {
    *x = x.wrapping_add(GOLDEN);
    let mut z = *x;
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^ (z >> 31)
}

/// Seed of the `index`-th independent stream derived from `seed` (chunks, paths).
#[inline]
pub fn stream_seed(seed: u64, index: u64) -> u64 {
    seed.wrapping_add((index + 1).wrapping_mul(GOLDEN))
}

impl FastRandom {
    pub fn new(mut seed: u64) -> Self {
        let s0 = split_mix(&mut seed);
        let s1 = split_mix(&mut seed);
        let s2 = split_mix(&mut seed);
        let s3 = split_mix(&mut seed);
        FastRandom { s: [s0, s1, s2, s3], spare: 0.0, has_spare: false }
    }

    #[inline]
    pub fn next_u64(&mut self) -> u64 {
        let s = &mut self.s;
        let result = s[1].wrapping_mul(5).rotate_left(7).wrapping_mul(9);
        let t = s[1] << 17;
        s[2] ^= s[0];
        s[3] ^= s[1];
        s[1] ^= s[2];
        s[0] ^= s[3];
        s[2] ^= t;
        s[3] = s[3].rotate_left(45);
        result
    }

    #[inline]
    pub fn next_f64(&mut self) -> f64 {
        (self.next_u64() >> 11) as f64 * (1.0 / (1u64 << 53) as f64)
    }

    #[inline]
    pub fn gaussian(&mut self) -> f64 {
        if self.has_spare {
            self.has_spare = false;
            return self.spare;
        }
        loop {
            let u = 2.0 * self.next_f64() - 1.0;
            let v = 2.0 * self.next_f64() - 1.0;
            let s = u * u + v * v;
            if s < 1.0 && s != 0.0 {
                let m = (-2.0 * s.ln() / s).sqrt();
                self.spare = v * m;
                self.has_spare = true;
                return u * m;
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn uniform_in_unit_interval_and_mean_half() {
        let mut r = FastRandom::new(1);
        let n = 200_000;
        let mut s = 0.0;
        for _ in 0..n {
            let u = r.next_f64();
            assert!((0.0..1.0).contains(&u));
            s += u;
        }
        assert!((s / n as f64 - 0.5).abs() < 0.005);
    }

    #[test]
    fn gaussian_moments() {
        let mut r = FastRandom::new(42);
        let n = 400_000;
        let (mut s, mut s2) = (0.0, 0.0);
        for _ in 0..n {
            let z = r.gaussian();
            s += z;
            s2 += z * z;
        }
        let m = s / n as f64;
        assert!(m.abs() < 0.01);
        assert!((s2 / n as f64 - m * m - 1.0).abs() < 0.01);
    }
}
