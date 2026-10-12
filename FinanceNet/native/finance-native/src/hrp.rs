//! Hierarchical Risk Parity (port of Finance.Portfolio.HierarchicalRiskParity): correlation distance,
//! agglomerative clustering with Lance-Williams updates, quasi-diagonal leaf order, recursive bisection.
//! Same arithmetic and the same tie-breaking (active-list order) as the managed code, so results are identical.

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Linkage {
    Single,
    Complete,
    Average,
}

/// One dendrogram merge: (left, right, distance, size), SciPy convention (merge k creates cluster n + k).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Step {
    pub left: usize,
    pub right: usize,
    pub distance: f64,
    pub size: usize,
}

#[inline]
fn net_max(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() { f64::NAN } else { a.max(b) }
}

#[inline]
fn net_min(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() { f64::NAN } else { a.min(b) }
}

/// Correlation distance sqrt(½(1 − ρ)) from a row-major covariance matrix.
pub fn distance(cov: &[f64], n: usize) -> Vec<f64> {
    let mut d = vec![0.0; n * n];
    for a in 0..n {
        for b in 0..n {
            if a == b {
                continue;
            }
            let s = (cov[a * n + a] * cov[b * n + b]).sqrt();
            let corr = if s > 0.0 { cov[a * n + b] / s } else { 0.0 };
            d[a * n + b] = net_max(0.0, 0.5 * (1.0 - corr)).sqrt();
        }
    }
    d
}

/// Agglomerative clustering (O(n³)). `dist` is row-major n × n.
pub fn cluster(dist: &[f64], n: usize, method: Linkage) -> Vec<Step> {
    if n == 0 {
        return Vec::new();
    }
    let total = 2 * n - 1;
    let mut d = vec![0.0f64; total * total];
    for i in 0..n {
        d[i * total..i * total + n].copy_from_slice(&dist[i * n..(i + 1) * n]);
    }
    let mut size = vec![0usize; total];
    size[..n].fill(1);
    let mut active: Vec<usize> = (0..n).collect();
    let mut steps = Vec::with_capacity(n - 1);
    for k in 0..n - 1 {
        let mut best = f64::INFINITY;
        let (mut ba, mut bb) = (usize::MAX, usize::MAX);
        for x in 0..active.len() {
            let row = active[x] * total;
            for &ay in &active[x + 1..] {
                let v = d[row + ay];
                if v < best {
                    best = v;
                    ba = active[x];
                    bb = ay;
                }
            }
        }
        if ba == usize::MAX {
            // Every remaining distance is NaN or +inf: the managed code would fail too (index -1).
            return steps;
        }
        let id = n + k;
        size[id] = size[ba] + size[bb];
        steps.push(Step { left: ba.min(bb), right: ba.max(bb), distance: best, size: size[id] });
        active.retain(|&c| c != ba && c != bb);
        for &c in &active {
            let (da, db) = (d[ba * total + c], d[bb * total + c]);
            let v = match method {
                Linkage::Single => net_min(da, db),
                Linkage::Complete => net_max(da, db),
                Linkage::Average => (size[ba] as f64 * da + size[bb] as f64 * db) / (size[ba] + size[bb]) as f64,
            };
            d[id * total + c] = v;
            d[c * total + id] = v;
        }
        active.push(id);
    }
    steps
}

/// Leaf order of the dendrogram, left subtree before right.
pub fn quasi_diagonal(steps: &[Step], leaves: usize) -> Vec<usize> {
    if leaves == 1 {
        return vec![0];
    }
    let mut order = Vec::with_capacity(leaves);
    let mut stack = vec![leaves + steps.len() - 1];
    while let Some(c) = stack.pop() {
        if c < leaves {
            order.push(c);
            continue;
        }
        let s = steps[c - leaves];
        stack.push(s.right);
        stack.push(s.left);
    }
    order
}

fn cluster_variance(cov: &[f64], n: usize, members: &[usize]) -> f64 {
    let ivp: Vec<f64> = members.iter().map(|&m| 1.0 / cov[m * n + m]).collect();
    let s: f64 = ivp.iter().fold(0.0, |a, b| a + b);
    let mut v = 0.0;
    for i in 0..members.len() {
        for j in 0..members.len() {
            v += ivp[i] / s * cov[members[i] * n + members[j]] * ivp[j] / s;
        }
    }
    v
}

/// Top-down recursive bisection on `order` (breadth-first, like the managed queue).
pub fn recursive_bisection(cov: &[f64], n: usize, order: &[usize]) -> Vec<f64> {
    let mut w = vec![1.0; n];
    let mut queue = std::collections::VecDeque::new();
    queue.push_back(order.to_vec());
    while let Some(c) = queue.pop_front() {
        if c.len() < 2 {
            continue;
        }
        let half = c.len() / 2;
        let (left, right) = (c[..half].to_vec(), c[half..].to_vec());
        let (vl, vr) = (cluster_variance(cov, n, &left), cluster_variance(cov, n, &right));
        let alpha = 1.0 - vl / (vl + vr);
        for &i in &left {
            w[i] *= alpha;
        }
        for &i in &right {
            w[i] *= 1.0 - alpha;
        }
        queue.push_back(left);
        queue.push_back(right);
    }
    w
}

/// Full HRP: weights, leaf order and dendrogram.
pub fn hrp(cov: &[f64], n: usize, method: Linkage) -> (Vec<f64>, Vec<usize>, Vec<Step>) {
    let dist = distance(cov, n);
    let steps = cluster(&dist, n, method);
    if steps.len() + 1 < n {
        return (vec![f64::NAN; n], Vec::new(), steps);
    }
    let order = quasi_diagonal(&steps, n);
    let w = recursive_bisection(cov, n, &order);
    (w, order, steps)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn two_blocks_cluster_first_and_weights_sum_to_one() {
        // Assets 0,1 highly correlated, 2,3 highly correlated, blocks uncorrelated.
        let v = [0.04, 0.05, 0.02, 0.03];
        let rho = |a: usize, b: usize| if a == b { 1.0 } else if a / 2 == b / 2 { 0.9 } else { 0.0 };
        let n = 4;
        let mut cov = vec![0.0; n * n];
        for a in 0..n {
            for b in 0..n {
                cov[a * n + b] = rho(a, b) * (v[a] * v[b] as f64).sqrt();
            }
        }
        let (w, order, steps) = hrp(&cov, n, Linkage::Single);
        assert_eq!(steps.len(), 3);
        assert!(steps[..2].iter().any(|s| (s.left, s.right) == (0, 1)));
        assert!(steps[..2].iter().any(|s| (s.left, s.right) == (2, 3)));
        assert_eq!(order.len(), 4);
        assert!((w.iter().sum::<f64>() - 1.0).abs() < 1e-12);
        assert!(w.iter().all(|&x| x > 0.0));
    }
}
