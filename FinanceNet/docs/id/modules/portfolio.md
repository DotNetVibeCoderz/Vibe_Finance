# Finance.Portfolio

[← Indeks](../index.md) · [English](../../en/modules/portfolio.md) · Paket: `Gravicode.Finance.Portfolio`

Konstruksi portofolio ala PyPortfolioOpt: estimator expected return dan model risiko, optimasi
mean-variance, Black-Litterman, risk parity dan risk budgeting, Hierarchical Risk Parity, optimasi
CVaR, portofolio heuristik, serta alokasi diskrit ke lembar saham utuh.

Namespace: `Finance.Portfolio`.

## Konsep utama

- **Mean-variance (Markowitz)** — pilih bobot *w* untuk menyeimbangkan expected return μᵀw dengan
  variansi wᵀΣw. Menggeser target membentuk **efficient frontier**; portofolio **max-Sharpe**
  (tangency) memberi return terbaik per unit risiko di atas suku bunga bebas risiko.
- **Risiko estimasi** — rata-rata sampel sangat berisik; kovarians yang di-shrink (Ledoit-Wolf) dan
  return berbasis prior (Black-Litterman) membuat bobot hasil optimasi jauh lebih stabil.
- **Black-Litterman** — dimulai dari return *tersirat pasar* yang membuat bobot kapitalisasi pasar
  optimal, lalu memadukan *pandangan* Anda (absolut: "return AAPL 10%"; relatif: "MSFT mengungguli
  GOOG 2%") dengan bobot sesuai tingkat keyakinan.
- **Risk parity** — setiap aset menyumbang porsi risiko portofolio yang sama, tanpa perlu ramalan
  return. *Risk budgeting* memperluasnya ke porsi target apa pun.
- **HRP** (López de Prado) — kelompokkan aset berdasarkan korelasi, urutkan agar aset yang mirip
  berdekatan, lalu bagi modal secara rekursif berdasarkan invers variansi. Tangguh ketika Σ
  terkondisi buruk.
- **Optimasi CVaR** (Rockafellar-Uryasev) — meminimalkan rata-rata kerugian di ekor terburuk skenario
  return, bukan variansi.

## Estimator

```csharp
using Finance.Mathematics;
using Finance.Portfolio;

Matrix prices = panel.ToMatrix();                     // baris = tanggal, kolom = aset
Matrix returns = ExpectedReturns.ReturnsFromPrices(prices);
double[] mu = ExpectedReturns.MeanHistorical(returns);           // dianualisasi, majemuk
double[] muEma = ExpectedReturns.EmaHistorical(returns, span: 500);
double[] muCapm = ExpectedReturns.Capm(returns, riskFreeRate: 0.03);

Matrix sample = RiskModels.SampleCovariance(returns);
Matrix lw = RiskModels.LedoitWolf(returns);           // pilihan bawaan yang disarankan
Matrix ewm = RiskModels.ExponentialCovariance(returns, span: 180);
```

## Optimasi mean-variance

```csharp
string[] names = { "AAPL", "MSFT", "NVDA", "TLT", "GLD" };
var mvo = new MeanVarianceOptimizer(mu, lw, lowerBound: 0, upperBound: 0.4, assets: names);

PortfolioWeights maxSharpe = mvo.MaxSharpe(riskFreeRate: 0.03).Clean();
PortfolioWeights minVol = mvo.MinVolatility();
PortfolioWeights target = mvo.EfficientReturn(0.15);      // risiko minimum untuk return 15%
PortfolioWeights capped = mvo.EfficientRisk(0.12);        // return maksimum untuk volatilitas 12%
PortfolioWeights util = mvo.MaxQuadraticUtility(riskAversion: 3);

Console.WriteLine(maxSharpe);                              // daftar aset: bobot
Console.WriteLine(mvo.Performance(maxSharpe, 0.03));      // Return 18.2%, Volatility 14.1%, Sharpe 1.078

foreach (FrontierPoint p in mvo.EfficientFrontier(points: 40, riskFreeRate: 0.03))
    Console.WriteLine($"{p.Volatility:P1} → {p.ExpectedReturn:P1} (Sharpe {p.SharpeRatio:F2})");
```

`lowerBound` negatif mengizinkan short. `PortfolioWeights` adalah `IReadOnlyList<double>` dengan
`Assets`, `ToDictionary()`, dan `Clean(cutoff)` (menolkan bobot yang sangat kecil lalu membulatkan).

## Optimasi terkendala dan rebalancing sadar biaya (v0.2)

```csharp
string[] sector = { "Tech", "Tech", "Bonds", "Energy" };
double[] beta = { 1.1, 1.3, 0.2, 0.9 };
var opt = new ConstrainedPortfolioOptimizer(mu, cov, assets)
{
    CurrentWeights = current,                          // mengaktifkan biaya dan turnover
    BuyCosts = Enumerable.Repeat(0.001, 4).ToArray(),  // 10bp per unit yang ditransaksikan (SellCosts default sama)
    MaxTurnover = 0.30,                                // Σ|w − w0| ≤ 30%
}
.WithBounds(0, 0.4)
.AddSectorLimit(sector, "Tech", 0, 0.5)
.AddFactorExposure("market", beta, 0.8, 1.0);

ConstrainedPortfolio r = opt.MaxQuadraticUtility(riskAversion: 4);   // atau MinVolatility(), EfficientReturn(0.10)
Console.WriteLine($"turnover {r.Turnover:P1}, biaya {r.TransactionCost:P3}, σ {r.Volatility:P2}");
```

Semuanya berjalan di atas `QuadraticProgram`, solver interior-point primal-dual padat (prediktor-korektor
Mehrotra) untuk min ½xᵀQx + cᵀx dengan kesamaan, pertidaksamaan, dan batas. Kelasnya publik, jadi QP konveks
lain juga bisa memakainya. Transaksi dimodelkan sebagai variabel beli/jual, sehingga biaya proporsional
menciptakan wilayah tanpa transaksi: perbaikan kecil tidak layak ditransaksikan. Masalah 200 aset dengan
biaya, turnover, dan batas (600 variabel QP) selesai sekitar dua detik.

### Frontier efisien hasil resampling

```csharp
IReadOnlyList<FrontierPoint> robust = ResampledFrontier.Compute(mu, cov, observations: 60, simulations: 200, points: 20);
```

Resampling Michaud: simulasikan histori dari N(μ, Σ), estimasi ulang, rata-ratakan bobot frontier per
peringkat, lalu evaluasi pada input asli. Titik-titiknya berada sedikit di dalam frontier klasik tetapi jauh
lebih terdiversifikasi dan stabil terhadap galat estimasi.

## Black-Litterman

```csharp
double[] prior = BlackLittermanModel.MarketImpliedPriorReturns(marketCaps, lw, riskAversion: 2.5);

var views = new BlackLittermanViews(names)
    .Absolute("NVDA", 0.25, confidence: 0.6)
    .Relative("MSFT", "AAPL", 0.03, confidence: 0.4);

BlackLittermanModel bl = BlackLittermanModel.Create(lw, prior, views, tau: 0.05, OmegaMethod.Idzorek);
double[] posterior = bl.Posterior;
PortfolioWeights blWeights = bl.Weights(riskAversion: 2.5);
// atau optimasi lebih lanjut: new MeanVarianceOptimizer(bl.Posterior, bl.PosteriorCovariance, 0, 0.4, names).MaxSharpe()
```

`OmegaMethod.HeLitterman` menetapkan ketidakpastian pandangan sebanding dengan τPΣPᵀ;
`OmegaMethod.Idzorek` menerjemahkan keyakinan setiap pandangan (0–1) menjadi ketidakpastiannya.

## Portofolio berbasis risiko

```csharp
PortfolioWeights erc = RiskParity.EqualRiskContribution(lw, names);
PortfolioWeights budget = RiskParity.RiskBudget(lw, new[] { 0.3, 0.3, 0.2, 0.1, 0.1 }, names);
double[] rc = RiskParity.RiskContributions(erc.AsSpan(), lw);      // sama besar berdasarkan konstruksinya

HrpResult hrp = HierarchicalRiskParity.OptimizeFromReturns(returns, names);
// hrp.Weights, hrp.Order (urutan daun quasi-diagonal), hrp.Linkage (langkah dendrogram untuk diplot)

var cvar = new CVaROptimizer(returns, beta: 0.95, lowerBound: 0, upperBound: 0.5, assets: names);
PortfolioWeights minCvar = cvar.MinCVaR();
Console.WriteLine($"CVaR {cvar.CVaR(minCvar.AsSpan()):P2}, VaR {cvar.VaR(minCvar.AsSpan()):P2}");

PortfolioWeights ew = HeuristicPortfolios.EqualWeight(names.Length, names);
PortfolioWeights iv = HeuristicPortfolios.InverseVolatility(lw, names);
PortfolioWeights md = HeuristicPortfolios.MaxDiversification(lw, assets: names);
```

## Dari bobot ke order

```csharp
double[] lastPrices = { 228.1, 415.6, 131.2, 92.4, 236.9 };
AllocationResult alloc = DiscreteAllocation.LargestRemainder(maxSharpe, lastPrices, budget: 50_000);
foreach (var (asset, shares) in alloc.Shares) Console.WriteLine($"{asset}: {shares}");
Console.WriteLine($"sisa kas {alloc.Leftover:N2}");
// DiscreteAllocation.Greedy(...) adalah skema pembulatan alternatif
```

`Rebalancer.Trades(...)` mengubah kepemilikan saat ini dan bobot target menjadi daftar
`RebalanceTrade(Asset, Side, Quantity, Price)`.

## Catatan dan batasan

- `QuadraticProgram` bersifat padat. Nyaman hingga beberapa ribu variabel; semesta besar yang jarang
  memerlukan faktorisasi KKT sparse. Batasi jumlah kendala kesamaan: masing-masing menambah Schur complement.

- Program kuadratik diselesaikan dengan metode projected gradient yang dipercepat pada capped simplex
  (`BoxSimplexQp`); hasilnya diverifikasi terhadap kondisi KKT dan rumus tertutup. Masalah
  mean-variance dengan batas (terutama saat menyapu efficient frontier) hasilnya benar tetapi agak
  lambat; kendala linear umum (batas sektor, turnover) ada di roadmap.
- `CVaROptimizer` menyelesaikan masalah Rockafellar-Uryasev dengan metode **cutting plane**. Metode ini
  diuji pada universe kecil; perilaku di atas sekitar **30 aset** belum diuji.
- `RiskModels.FixNonPositiveSemidefinite` memperbaiki matriks kovarians yang tidak PSD (misalnya hasil
  data pairwise-complete).

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
