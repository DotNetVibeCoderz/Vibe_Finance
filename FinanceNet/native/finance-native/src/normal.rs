//! Standard normal distribution: Cody's erfc rational approximations, ported constant for
//! constant from `Finance.Mathematics.Normal` so native and managed prices agree.

pub const INV_SQRT_2PI: f64 = 0.398_942_280_401_432_677_94;
const SQRT2: f64 = 1.414_213_562_373_095_048_80;

#[inline]
pub fn pdf(x: f64) -> f64 {
    INV_SQRT_2PI * (-0.5 * x * x).exp()
}

#[inline]
pub fn cdf(x: f64) -> f64 {
    0.5 * erfc(-x / SQRT2)
}

pub fn erf(x: f64) -> f64 {
    let ax = x.abs();
    if ax >= 0.5 {
        return 1.0 - erfc(x);
    }
    const P0: f64 = 3.209377589138469473e3;
    const P1: f64 = 3.774852376853020208e2;
    const P2: f64 = 1.138641541510501556e2;
    const P3: f64 = 3.161123743870565597;
    const P4: f64 = 1.857777061846031527e-1;
    const Q0: f64 = 2.844236833439170622e3;
    const Q1: f64 = 1.282616526077372276e3;
    const Q2: f64 = 2.440246379344441733e2;
    const Q3: f64 = 2.360129095234412093e1;
    let z = x * x;
    let num = (((P4 * z + P3) * z + P2) * z + P1) * z + P0;
    let den = (((z + Q3) * z + Q2) * z + Q1) * z + Q0;
    x * num / den
}

pub fn erfc(x: f64) -> f64 {
    let ax = x.abs();
    if ax < 0.5 {
        return 1.0 - erf(x);
    }
    let result = if ax < 4.0 {
        const P0: f64 = 3.004592610201616005e2;
        const P1: f64 = 4.519189537118729422e2;
        const P2: f64 = 3.393208167343436870e2;
        const P3: f64 = 1.529892850469404039e2;
        const P4: f64 = 4.316222722205673530e1;
        const P5: f64 = 7.211758250883093659;
        const P6: f64 = 5.641955174789739711e-1;
        const P7: f64 = -1.368648573827167067e-7;
        const Q0: f64 = 3.004592609569832933e2;
        const Q1: f64 = 7.909509253278980272e2;
        const Q2: f64 = 9.313540948506096211e2;
        const Q3: f64 = 6.389802644656311665e2;
        const Q4: f64 = 2.775854447439876434e2;
        const Q5: f64 = 7.700015293522947295e1;
        const Q6: f64 = 1.278272731962942351e1;
        let num = ((((((P7 * ax + P6) * ax + P5) * ax + P4) * ax + P3) * ax + P2) * ax + P1) * ax + P0;
        let den = ((((((ax + Q6) * ax + Q5) * ax + Q4) * ax + Q3) * ax + Q2) * ax + Q1) * ax + Q0;
        (-ax * ax).exp() * num / den
    } else {
        const P0: f64 = -2.99610707703542174e-3;
        const P1: f64 = -4.94730910623250734e-2;
        const P2: f64 = -2.26956593539686930e-1;
        const P3: f64 = -2.78661308609647788e-1;
        const P4: f64 = -2.23192459734184686e-2;
        const Q0: f64 = 1.06209230528467918e-2;
        const Q1: f64 = 1.91308926107829841e-1;
        const Q2: f64 = 1.05167510706793207;
        const Q3: f64 = 1.98733201817135256;
        let z = 1.0 / (ax * ax);
        let num = (((P4 * z + P3) * z + P2) * z + P1) * z + P0;
        let den = (((z + Q3) * z + Q2) * z + Q1) * z + Q0;
        (-ax * ax).exp() / ax * (0.564_189_583_547_756_286_95 + z * num / den)
    };
    if x < 0.0 { 2.0 - result } else { result }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn known_values() {
        assert!((cdf(0.0) - 0.5).abs() < 1e-16);
        assert!((cdf(1.959963984540054) - 0.975).abs() < 1e-14);
        assert!((cdf(-1.0) - 0.15865525393145707).abs() < 1e-15);
        assert!((cdf(5.0) - 0.9999997133484281).abs() < 1e-15);
    }
}
