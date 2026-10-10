# Finance.Math

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/math.md) · Package: `Gravicode.Finance.Math`

Numerical building blocks sized for finance: descriptive statistics, covariance estimation, a
dense matrix type, probability distributions, root finders, optimisers, interpolation, numerical
integration and least-squares regression. Large or exotic decompositions (eigen, SVD) are delegated
to **GraviNum** from Gravicode.Science.

> **Namespace is `Finance.Mathematics`**, not `Finance.Math`. A namespace called `Math` would hide
> `System.Math` in every `Finance.*` namespace, so the project keeps its architectural name while
> the namespace avoids the clash.

## Key concepts

- **Sample vs population variance** — `ddof` (delta degrees of freedom) is `1` for the sample
  estimator, `0` for the population one. Variance is computed in two passes (mean first) because
  the one-pass formula loses precision on price-level data.
- **Covariance shrinkage** — with few observations per asset the sample covariance is noisy.
  Ledoit-Wolf shrinks it toward a scaled identity by an intensity estimated from the data.
- **Root finding** — implied volatility and yield-to-maturity are roots of `f(x) = 0`. Brent's
  method is bracketed (never diverges); Newton is faster when a derivative is available.
- **Projected gradient on the simplex** — portfolio weights that sum to 1 within bounds live on a
  capped simplex; projecting each gradient step back onto it solves many long-only problems.

## API tour

### Statistics

```csharp
using Finance.Mathematics;

double[] r = { 0.01, -0.004, 0.007, 0.002, -0.012 };
double mean = Stats.Mean(r), sd = Stats.StdDev(r), skew = Stats.Skewness(r), kurt = Stats.ExcessKurtosis(r);
double p05 = Stats.Quantile(r, 0.05);          // linear interpolation, like numpy.quantile
double rho = Stats.SpearmanCorrelation(r, new double[] { 3, 1, 4, 2, 0 });
double[] z = Stats.ZScore(r);
```

`Sum`, `Mean`, `Variance` and `Matrix.Dot` are vectorised with `Vector<double>`.

### Matrices and covariance

```csharp
var returns = new Matrix(250, 3);              // rows = observations, columns = assets
// ... fill returns[i, j] ...
Matrix cov = Stats.CovarianceMatrix(returns);
Matrix corr = Stats.CovarianceToCorrelation(cov);
var (shrunk, intensity) = Stats.LedoitWolf(returns);

Matrix l = cov.Cholesky();                     // throws if not positive definite; TryCholesky does not
double[] x = cov.Solve(new double[] { 1, 0, 0 });
double variance = cov.QuadraticForm(new[] { 0.5, 0.3, 0.2 });   // wᵀΣw
var (eigenvalues, eigenvectors) = cov.SymmetricEigen();          // descending, via GraviNum

var nd = cov.ToNdArray();                       // hand over to GraviNum
Matrix back = Matrix.FromNdArray(nd);
```

`Matrix` is row-major; `Row(i)` returns a writable `Span<double>` view without copying.

### Distributions

```csharp
double p = Normal.Cdf(1.96);                   // ≈ 0.975, accurate to ~1e-15
double q = Normal.InverseCdf(0.99);            // 2.3263…
double t = StudentT.InverseCdf(0.975, nu: 5);  // 2.5706…
double lg = SpecialFunctions.LogGamma(4.5);
```

### Root finding, optimisation, interpolation, integration

```csharp
double root = RootFinding.Brent(v => v * v * v - 2, 0, 2);
double nr = RootFinding.Newton(v => v * v - 2, v => 2 * v, x0: 1);

double[] w = { 0.9, -0.3, 0.6, 0.1 };
Optimizer.ProjectOntoSimplex(w, total: 1.0, lower: 0.0, upper: 0.5);   // in place

OptimizationResult rosen = Optimizer.NelderMead(
    v => Math.Pow(1 - v[0], 2) + 100 * Math.Pow(v[1] - v[0] * v[0], 2), new[] { -1.2, 1.0 });

var spline = new CubicSplineInterpolator(new double[] { 0, 1, 2, 3 }, new double[] { 0, 1, 4, 9 });
double y = spline.Evaluate(1.5);
var dfCurve = new LogLinearInterpolator(new double[] { 1, 2, 5 }, new double[] { 0.97, 0.94, 0.85 });

double area = Integration.GaussLegendre(Normal.Pdf, -1, 1);
```

`Optimizer.ProjectedGradient(f, gradient, x0, project)` is the general accelerated projected
gradient solver (FISTA with Armijo backtracking) used by the portfolio module.

### Regression

```csharp
RegressionResult fit = Regression.Ols(xMatrix, yValues);         // intercept by default
Console.WriteLine($"alpha {fit.Intercept:F4}, beta {fit.Slopes[0]:F3}, R² {fit.RSquared:F3}, t {fit.TStatistics[1]:F2}");
double[] ridge = Regression.Ridge(xMatrix, yValues, lambda: 0.1);
var beta = Regression.Simple(marketReturns, stockReturns).Slopes[0];
```

`RegressionResult` carries coefficients, standard errors, t-statistics, R², adjusted R², residual
standard error and residuals.

## Notes and limitations

- `Matrix` is for small-to-medium dense problems (hundreds of assets). Its inverse, solve and
  determinant use LU with partial pivoting; very large systems should go through GraviNum.
- `Regression.Ols` uses the normal equations, adequate for well-conditioned factor regressions;
  highly collinear designs should use `Regression.Ridge`.

---

Made by Gravicode Studios, led by Kang Fadhil
