# Finance.Risk

[← Indeks](../index.md) · [English](../../en/modules/risk.md) · Paket: `Gravicode.Finance.Risk`

Pengukuran risiko pasar dan analitik kinerja: Value-at-Risk dan Expected Shortfall dengan lima metode,
atribusi dan backtesting VaR, stress testing, simulasi portofolio Monte Carlo, metrik kinerja ala
pyfolio, serta model volatilitas (GARCH, EWMA).

Namespace: `Finance.Risk`.

## Konsep utama

- **VaR** — kerugian yang tidak terlampaui pada tingkat keyakinan tertentu dalam suatu horizon (VaR
  satu hari 99% sebesar 2% = pada 99 dari 100 hari kerugian Anda di bawah 2%). **Expected Shortfall
  (CVaR)** adalah rata-rata kerugian pada hari-hari di luar VaR, sehingga ikut menangkap bentuk ekor
  distribusi.
- **Metode** — *historis* (kuantil empiris), *parametrik normal*, *Student-t* (ekor tebal),
  *Cornish-Fisher* (menyesuaikan kuantil normal dengan skewness dan kurtosis), dan *Monte Carlo*.
- **Component VaR** — memecah VaR portofolio menjadi kontribusi per aset yang jika dijumlahkan sama
  dengan totalnya (alokasi Euler).
- **Backtesting VaR** — hitung jumlah pelanggaran (hari dengan kerugian > VaR). Uji *Kupiec* memeriksa
  apakah jumlahnya sesuai tingkat keyakinan; uji *Christoffersen* memeriksa apakah pelanggaran
  bergerombol; *traffic light Basel* mengklasifikasikan model menjadi hijau/kuning/merah.
- **Stress testing** — terapkan guncangan faktor (saham −40%, suku bunga +200bp, dan sebagainya) ke
  posisi yang punya sensitivitas (beta, durasi).
- **GARCH(1,1)** — volatilitas yang bereaksi terhadap guncangan dan kembali ke rata-rata:
  σ²ₜ = ω + α·ε²ₜ₋₁ + β·σ²ₜ₋₁.

## Value-at-Risk

```csharp
using Finance.Risk;

VarEstimate hist = ValueAtRisk.Historical(returns, confidence: 0.99);
VarEstimate norm = ValueAtRisk.ParametricNormal(returns, 0.99);
VarEstimate t5 = ValueAtRisk.ParametricStudentT(returns, degreesOfFreedom: 5, confidence: 0.99);
VarEstimate cf = ValueAtRisk.CornishFisher(returns, 0.99);
VarEstimate any = ValueAtRisk.Estimate(returns, VarMethod.CornishFisher, 0.975);

// Monte Carlo untuk portofolio multi-aset (rata-rata dan kovarians harian)
VarEstimate mc = ValueAtRisk.MonteCarlo(weights, dailyMeans, dailyCovariance, confidence: 0.99, simulations: 200_000);

Console.WriteLine($"VaR {hist.ValueAtRisk:P2}  ES {hist.ExpectedShortfall:P2}  untuk $1 juta: {hist.ValueAtRiskAmount(1_000_000):N0}");
double tenDay = ValueAtRisk.ScaleToHorizon(hist.ValueAtRisk, 10);   // aturan akar waktu
```

Semua estimasi berupa **fraksi kerugian positif**.

### Atribusi dan backtesting

```csharp
VarDecomposition d = VarAttribution.Decompose(weights, covariance, 0.99);
// d.Marginal, d.Component (jumlahnya = d.PortfolioVar), d.PercentContribution
double incremental = VarAttribution.Incremental(weights, delta: new[] { 0.05, -0.05, 0, 0 }, covariance);

VarBacktestResult bt = VarBacktest.Run(realisedReturns, varForecasts, confidence: 0.99);
Console.WriteLine($"{bt.Exceptions}/{bt.Observations} pelanggaran (harapan {bt.ExpectedExceptions:F1}), " +
                  $"Kupiec p={bt.KupiecPValue:F3}, Christoffersen p={bt.ChristoffersenPValue:F3}, zona {bt.Zone}, lolos {bt.Passes()}");
```

## Stress testing

```csharp
var book = new List<StressPosition>
{
    StressPosition.Equity("Saham AS", 600_000, beta: 1.0),
    StressPosition.Equity("Teknologi", 200_000, beta: 1.4),
    StressPosition.Bond("Treasury", 300_000, modifiedDuration: 7.5, spreadDuration: 0, convexity: 70),
    StressPosition.Single("Emas", 100_000, RiskFactors.Commodity, sensitivity: 0.6),
};

foreach (StressResult r in StressTester.RunAll(book))             // semua skenario historis bawaan, terburuk dulu
    Console.WriteLine($"{r.Scenario,-28} {r.TotalPnl,14:N0} ({r.ReturnOnPortfolio:P1})");

var custom = StressScenario.Custom("Bunga +300bp, saham -25%", (RiskFactors.Rates, 0.03), (RiskFactors.Equity, -0.25));
StressResult one = StressTester.Run(book, custom);
double k = StressTester.ReverseStressMultiplier(book, custom, targetLoss: 250_000);   // seberapa parah agar rugi $250 ribu
```

`StressScenario.Historical` bawaan: Global Financial Crisis 2008, Dot-com Bust 2000-02, COVID-19
Crash 2020, Rates Shock 2022, Black Monday 1987, dan Taper Tantrum 2013. Nama faktor ada di
`RiskFactors` (`Equity`, `Rates`, `Credit`, `Fx`, `Commodity`, `Volatility`, `Crypto`).

## Simulasi Monte Carlo

```csharp
SimulationResult sim = PortfolioSimulation.Run(initialValue: 1_000_000, weights, annualDrift, annualCovariance,
    years: 1, stepsPerYear: 252, paths: 10_000, seed: 42, samplePaths: 20);

double p5 = sim.Percentile(0.05);
double lossProbability = sim.ProbabilityOfLoss;
VarEstimate horizonVar = sim.ValueAtRisk(0.95);
// sim.Fan[k][t] pada sim.FanPercentiles (5/25/50/75/95%) sepanjang sim.Times — siap untuk fan chart
```

## Metrik kinerja

```csharp
PerformanceReport rep = PerformanceReport.Create(strategyReturns, benchmarkReturns, riskFreeRate: 0.03, periodsPerYear: 252);
Console.WriteLine(rep);                           // tearsheet teks
foreach (var (metric, value) in rep.ToRows()) Console.WriteLine($"{metric}: {value}");

double sharpe = PerformanceMetrics.SharpeRatio(returns, riskFreeRate: 0.03);
double sortino = PerformanceMetrics.SortinoRatio(returns);
double mdd = PerformanceMetrics.MaxDrawdown(returns);
var (alpha, beta) = PerformanceMetrics.AlphaBeta(returns, benchmarkReturns);
double ir = PerformanceMetrics.InformationRatio(returns, benchmarkReturns);
double[] rollingSharpe = PerformanceMetrics.RollingSharpe(returns, window: 126);
```

`PerformanceReport` mencakup return kumulatif dan tahunan, volatilitas, Sharpe, Sortino, Calmar, Omega,
max drawdown beserta durasinya, stability, tail ratio, skewness, excess kurtosis, downside deviation,
hit rate, periode terbaik/terburuk, VaR/ES 95%, dan — jika ada benchmark — alpha, beta, information
ratio, serta tracking error.

## Model volatilitas

```csharp
Garch11Model garch = VolatilityModels.FitGarch11(returns);
Console.WriteLine($"ω={garch.Omega:E2} α={garch.Alpha:F3} β={garch.Beta:F3}, vol jangka panjang {garch.LongRunVolatility():P1}");
double[] next10 = garch.ForecastVolatility(10);

double[] riskMetrics = VolatilityModels.Ewma(returns, lambda: 0.94);
double parkinson = VolatilityModels.Parkinson(bars);      // estimator rentang high-low

var (portVol, contrib, pct) = RiskAttribution.VolatilityContributions(weights, covariance);
```

## Catatan dan batasan

- Enam skenario stress historis memakai **besaran guncangan yang dibulatkan dan bersifat ilustratif**.
  Angkanya menggambarkan magnitudo yang masuk akal, bukan kalibrasi untuk keperluan regulasi.
- `ScaleToHorizon` memakai aturan akar waktu, yang mengasumsikan return saling independen.
- `PortfolioSimulation` merebalancing ke bobot konstan di setiap langkah dengan GBM berkorelasi.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
