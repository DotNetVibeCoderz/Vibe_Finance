# Finance.Math

[← Indeks](../index.md) · [English](../../en/modules/math.md) · Paket: `Gravicode.Finance.Math`

Fondasi numerik berukuran pas untuk keuangan: statistik deskriptif, estimasi kovarians, tipe matriks
padat, distribusi probabilitas, pencari akar, optimizer, interpolasi, integrasi numerik, dan regresi
kuadrat terkecil. Dekomposisi yang besar atau khusus (eigen, SVD) didelegasikan ke **GraviNum** dari
Gravicode.Science.

> **Namespace-nya `Finance.Mathematics`**, bukan `Finance.Math`. Namespace bernama `Math` akan
> menutupi `System.Math` di setiap namespace `Finance.*`, jadi nama proyek tetap sesuai arsitektur
> sementara namespace-nya menghindari bentrokan itu.

## Konsep utama

- **Variansi sampel vs populasi** — `ddof` (delta degrees of freedom) bernilai `1` untuk estimator
  sampel dan `0` untuk populasi. Variansi dihitung dua tahap (rata-rata dulu) karena rumus satu tahap
  kehilangan presisi pada data level harga.
- **Shrinkage kovarians** — dengan sedikit observasi per aset, kovarians sampel sangat berisik.
  Ledoit-Wolf "menyusutkannya" ke arah matriks identitas berskala dengan intensitas yang diestimasi dari
  data.
- **Pencarian akar** — implied volatility dan yield-to-maturity adalah akar dari `f(x) = 0`. Metode
  Brent menggunakan bracket (tidak pernah divergen); Newton lebih cepat jika turunannya tersedia.
- **Projected gradient pada simplex** — bobot portofolio yang berjumlah 1 dengan batas atas/bawah berada
  di "capped simplex"; memproyeksikan setiap langkah gradien kembali ke sana menyelesaikan banyak masalah
  long-only.

## Tur API

### Statistik

```csharp
using Finance.Mathematics;

double[] r = { 0.01, -0.004, 0.007, 0.002, -0.012 };
double mean = Stats.Mean(r), sd = Stats.StdDev(r), skew = Stats.Skewness(r), kurt = Stats.ExcessKurtosis(r);
double p05 = Stats.Quantile(r, 0.05);          // interpolasi linear, seperti numpy.quantile
double rho = Stats.SpearmanCorrelation(r, new double[] { 3, 1, 4, 2, 0 });
double[] z = Stats.ZScore(r);
```

`Sum`, `Mean`, `Variance`, dan `Matrix.Dot` divektorisasi dengan `Vector<double>`.

### Matriks dan kovarians

```csharp
var returns = new Matrix(250, 3);              // baris = observasi, kolom = aset
// ... isi returns[i, j] ...
Matrix cov = Stats.CovarianceMatrix(returns);
Matrix corr = Stats.CovarianceToCorrelation(cov);
var (shrunk, intensity) = Stats.LedoitWolf(returns);

Matrix l = cov.Cholesky();                     // melempar exception jika tidak definit positif; TryCholesky tidak
double[] x = cov.Solve(new double[] { 1, 0, 0 });
double variance = cov.QuadraticForm(new[] { 0.5, 0.3, 0.2 });   // wᵀΣw
var (eigenvalues, eigenvectors) = cov.SymmetricEigen();          // menurun, lewat GraviNum

var nd = cov.ToNdArray();                       // serahkan ke GraviNum
Matrix back = Matrix.FromNdArray(nd);
```

`Matrix` disimpan row-major; `Row(i)` mengembalikan view `Span<double>` yang bisa ditulis tanpa
penyalinan.

### Distribusi

```csharp
double p = Normal.Cdf(1.96);                   // ≈ 0,975, akurat hingga ~1e-15
double q = Normal.InverseCdf(0.99);            // 2,3263…
double t = StudentT.InverseCdf(0.975, nu: 5);  // 2,5706…
double lg = SpecialFunctions.LogGamma(4.5);
```

### Pencarian akar, optimasi, interpolasi, integrasi

```csharp
double root = RootFinding.Brent(v => v * v * v - 2, 0, 2);
double nr = RootFinding.Newton(v => v * v - 2, v => 2 * v, x0: 1);

double[] w = { 0.9, -0.3, 0.6, 0.1 };
Optimizer.ProjectOntoSimplex(w, total: 1.0, lower: 0.0, upper: 0.5);   // langsung mengubah array

OptimizationResult rosen = Optimizer.NelderMead(
    v => Math.Pow(1 - v[0], 2) + 100 * Math.Pow(v[1] - v[0] * v[0], 2), new[] { -1.2, 1.0 });

var spline = new CubicSplineInterpolator(new double[] { 0, 1, 2, 3 }, new double[] { 0, 1, 4, 9 });
double y = spline.Evaluate(1.5);
var dfCurve = new LogLinearInterpolator(new double[] { 1, 2, 5 }, new double[] { 0.97, 0.94, 0.85 });

double area = Integration.GaussLegendre(Normal.Pdf, -1, 1);
```

`Optimizer.ProjectedGradient(f, gradient, x0, project)` adalah solver projected gradient umum yang
dipercepat (FISTA dengan backtracking Armijo) dan dipakai oleh modul portofolio.

### Regresi

```csharp
RegressionResult fit = Regression.Ols(xMatrix, yValues);         // dengan intercept secara bawaan
Console.WriteLine($"alpha {fit.Intercept:F4}, beta {fit.Slopes[0]:F3}, R² {fit.RSquared:F3}, t {fit.TStatistics[1]:F2}");
double[] ridge = Regression.Ridge(xMatrix, yValues, lambda: 0.1);
var beta = Regression.Simple(marketReturns, stockReturns).Slopes[0];
```

`RegressionResult` memuat koefisien, standard error, statistik-t, R², R² teradjust, residual standard
error, dan residual.

## Catatan dan batasan

- `Matrix` dirancang untuk masalah padat berukuran kecil sampai menengah (ratusan aset). Invers, solve,
  dan determinan memakai LU dengan partial pivoting; sistem yang sangat besar sebaiknya lewat GraviNum.
- `Regression.Ols` memakai persamaan normal, cukup untuk regresi faktor yang terkondisi baik; desain yang
  sangat kolinear sebaiknya memakai `Regression.Ridge`.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
