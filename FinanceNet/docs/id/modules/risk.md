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

## Model VaR lanjutan (v0.2)

```csharp
// Filtered historical simulation: residual tersaring GARCH(1,1) atau EWMA diskalakan ke volatilitas hari ini.
var fhs = AdvancedVar.FilteredHistorical(returns, 0.99);                    // .Estimate, .CurrentVolatility
VarEstimate tenDay = AdvancedVar.FilteredHistoricalMultiPeriod(returns, horizon: 10, 0.99);

// Teori nilai ekstrem: ekor Generalized Pareto dipasang pada kerugian di atas kuantil 95%.
GpdTailFit tail = AdvancedVar.FitTail(returns, thresholdQuantile: 0.95);   // .Xi, .Beta, .ValueAtRisk(p)
VarEstimate evt = AdvancedVar.ExtremeValue(returns, 0.999);

// Likuiditas: add-on spread Bangia dkk., dan likuidasi bertahap selama T hari.
double lvar = AdvancedVar.LiquidityAdjusted(marketVar: 0.021, spreadMean: 0.004, spreadStdDev: 0.0015);
double liq10 = AdvancedVar.OrderlyLiquidation(oneDayVar: 0.021, days: 10);  // √((1+T)(1+2T)/(6T)), bukan √T

// Transaksi what-if pada skenario historis (baris = skenario, kolom = instrumen).
double dVar = AdvancedVar.IncrementalHistorical(weights, trade, scenarios, 0.99);
double[] parts = AdvancedVar.ComponentHistorical(weights, scenarios, 0.99);
double quick = AdvancedVar.IncrementalMarginal(weights, trade, covariance, 0.99); // orde pertama Euler
```

`VarMethod.FilteredHistorical` dan `VarMethod.ExtremeValue` juga bisa dipakai lewat `ValueAtRisk.Estimate`.

## Pendekatan standar FRTB (delta, vega, curvature, DRC)

```csharp
var sens = new[]
{
    new GirrSensitivity("USD", TenorYears: 2, Sensitivity: 1_200_000),   // perubahan PV per 1bp / 0.0001
    new GirrSensitivity("USD", TenorYears: 10, Sensitivity: -800_000),
    new GirrSensitivity("IDR", TenorYears: 5, Sensitivity: 300_000),
};
FrtbDeltaResult girr = FrtbSa.GirrDelta(sens, domesticCurrency: "IDR");
Console.WriteLine($"{girr.Charge:N0} (skenario korelasi {girr.Binding})");
```

`FrtbSa.Aggregate` adalah mesin generik metode berbasis sensitivitas: Kᵦ dalam bucket, agregasi antar
bucket dengan spesifikasi alternatif Sᵦ, dan maksimum dari skenario korelasi low / medium / high.
Gunakan untuk kelas risiko lain dengan memberikan sensitivitas tertimbang dan korelasi Anda sendiri.
`GirrDelta` menyediakan parameter delta GIRR sebagaimana tertulis di MAR21 (Januari 2019).

### Vega dan curvature (GIRR)

```csharp
var vega = FrtbSa.GirrVega(new[]
{
    // vega × implied vol, per jatuh tempo opsi × sisa jatuh tempo underlying
    new GirrVegaSensitivity("USD", OptionMaturityYears: 1, UnderlyingMaturityYears: 5, Sensitivity: 200_000),
    new GirrVegaSensitivity("USD", OptionMaturityYears: 3, UnderlyingMaturityYears: 10, Sensitivity: -150_000),
    new GirrVegaSensitivity("EUR", OptionMaturityYears: 0.5, UnderlyingMaturityYears: 1, Sensitivity: 80_000),
});
var curvature = FrtbSa.GirrCurvature(new[]
{
    // V(x + 1.7%) − V(x), V(x − 1.7%) − V(x), dan total delta terhadap kurva
    new GirrCurvatureSensitivity("USD", UpChange: -30_000, DownChange: 50_000, Delta: -1_000_000),
    new GirrCurvatureSensitivity("EUR", UpChange: 10_000, DownChange: -2_000, Delta: 400_000),
});
var sbm = FrtbSa.SensitivitiesBasedCharge(vega.ByScenario, curvature.ByScenario);
```

- **Vega** (MAR21.25, MAR21.92-95): RW = min(55%·√(LH/10), 100%); GIRR memiliki LH = 60, sehingga
  0.55·√6 = 1.347 dibatasi menjadi 100% (`VegaRiskWeight(FrtbVegaClass.EquityLargeCap)` = 0.55·√2 =
  77.78%). Dalam satu mata uang ρ = min(e^(−1%·|ΔT|/min T) dari jatuh tempo opsi × faktor yang sama dari
  jatuh tempo underlying, 1); γ = 50%; diagregasi dengan mesin delta. Pada contoh di atas, dua vertex USD
  memiliki ρ = e^(−0.02)·e^(−0.01) = 0.9704 dan K_USD = 65,370.24 (medium). Charge-nya 121,133.27
  (medium), 117,898.26 (high), **124,284.09 (low, mengikat)**.
- **Curvature** (MAR21.5, MAR21.98-101): CVR⁺ = −(ΔV⁺ − RW·s), CVR⁻ = −(ΔV⁻ + RW·s), dengan RW = 1.7%
  (pergeseran paralel semua kurva satu mata uang, satu faktor risiko per mata uang). USD: RW·s = −17,000,
  CVR⁺ = 13,000, CVR⁻ = −33,000 → skenario naik dipilih, K = 13,000. EUR: CVR⁺ = −3,200, CVR⁻ = −4,800,
  keduanya negatif → K = 0 dan seri diputus oleh ΣCVR yang lebih besar (naik, S = −3,200). Antar mata
  uang γ = 50%² = 25%: √(13,000² + 2·0.25·13,000·(−3,200)) = 12,173.74 (medium), 11,958.26 (high),
  **12,385.48 (low, mengikat)**. Dalam bucket ψ = 0 menghapus suku silang dua CVR negatif dan
  Kᵦ± = √max(0, …) dibatasi bawah di nol; antar bucket ψ(Sᵦ, S꜀) berlaku sama dan, seperti pada delta,
  jumlah dalam yang negatif membatasi Sᵦ ke [−Kᵦ, Kᵦ].
- **Total** (MAR21.7): `SensitivitiesBasedCharge` menjumlahkan komponen per skenario sebelum mengambil
  maksimum: low 136,669.57, medium 133,307.01, high 129,856.52 → **136,669.57**.
- Kelas risiko lain: susun bucket dengan `VegaRiskWeight(FrtbVegaClass…)` dan
  `VegaCorrelation(ρ_delta, Tₖ, Tₗ)` untuk `Aggregate`, dan berikan korelasi delta yang dikuadratkan ke
  `AggregateCurvature` dengan `CurvatureExposure` untuk setiap faktor risiko.

### Default risk charge, non-sekuritisasi (MAR22)

```csharp
var drc = FrtbDrc.Charge(new[]
{
    new DrcPosition("X", FrtbDrcBucket.Corporate, FrtbCreditQuality.A, FrtbSeniority.SeniorDebt, 1_000_000, 1_020_000, MaturityYears: 3),
    new DrcPosition("X", FrtbDrcBucket.Corporate, FrtbCreditQuality.A, FrtbSeniority.Equity, -200_000, -200_000, MaturityYears: 0.25),
    new DrcPosition("Y", FrtbDrcBucket.Corporate, FrtbCreditQuality.BB, FrtbSeniority.SeniorDebt, -500_000, -490_000, MaturityYears: 0.5),
    new DrcPosition("Y", FrtbDrcBucket.Corporate, FrtbCreditQuality.BB, FrtbSeniority.CoveredBond, 300_000, 300_000, MaturityYears: 2),
    new DrcPosition("Z", FrtbDrcBucket.Corporate, FrtbCreditQuality.BBB, FrtbSeniority.NonSeniorDebt, 100_000, 90_000, MaturityYears: 0.1),
    new DrcPosition("Z", FrtbDrcBucket.Corporate, FrtbCreditQuality.BBB, FrtbSeniority.SeniorDebt, -50_000, -50_000, MaturityYears: 1),
    new DrcPosition("S", FrtbDrcBucket.Sovereign, FrtbCreditQuality.AA, FrtbSeniority.SeniorDebt, 400_000, 400_000, MaturityYears: 5),
});
```

Notional dan nilai pasar bertanda (short < 0). JTD bruto = LGD·notional + (MV − notional), dibatasi bawah
di 0 untuk long dan dibatasi atas di 0 untuk short; LGD 100% ekuitas / non-senior, 75% senior, 25% covered
bond. Setiap JTD diskalakan dengan max(min(M, 1), 0.25). Per obligor, short dapat meng-offset long dengan
senioritas yang sama atau lebih tinggi. Per bucket HBR = Σ long / (Σ long + Σ |short|) dan
DRCᵦ = max(Σ RW·long − HBR·Σ RW·|short|, 0), RW: AAA 0.5%, AA 2%, A 3%, BBB 6%, BB 15%, B 30%, CCC 50%,
tanpa peringkat 15%, gagal bayar 100%. Total = Σ DRCᵦ.

| Obligor | JTD bruto terskala | Net long | Net short |
|---|---|---|---|
| X (A) | 770,000; −200,000·0.25 = −50,000 (short junior meng-offset) | 720,000 | 0 |
| Y (BB) | (−375,000 + 10,000)·0.5 = −182,500; 75,000 (covered, lebih senior) | 0 | −107,500 |
| Z (BBB) | 90,000·0.25 = 22,500; −37,500 (short senior tidak bisa meng-offset long junior) | 22,500 | −37,500 |
| S (AA) | 300,000 | 300,000 | 0 |

Korporasi: HBR = 742,500 / 887,500 = 0.8366; Σ RW·long = 21,600 + 1,350 = 22,950; Σ RW·|short| =
16,125 + 2,250 = 18,375; DRC = 22,950 − 0.8366·18,375 = 7,577.11. Sovereign: 0.02·300,000 = 6,000.
`drc.Charge` = **13,577.11**.

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

- FRTB: delta, vega dan curvature memiliki parameter bawaan hanya untuk GIRR (kelas risiko lain lewat
  `Aggregate` / `AggregateCurvature` dengan parameter Anda; bobot risiko vega mencakup semua kelas);
  vega/curvature untuk inflasi dan cross-currency basis GIRR, pemetaan sensitivitas ke vertex, DRC untuk
  sekuritisasi dan correlation trading portfolio, serta residual risk add-on belum diimplementasikan.
  Bobot risiko curvature adalah input (bawaan 1.7%). Periksa parameternya terhadap teks yang diterapkan
  regulator Anda sebelum memakai angkanya untuk modal regulasi.
- EVT memerlukan cukup exceedance: `FitTail` menolak kurang dari 10 kerugian di atas threshold, dan
  `ExtremeValue` mensyaratkan tingkat keyakinan berada di luar kuantil threshold.

- Enam skenario stress historis memakai **besaran guncangan yang dibulatkan dan bersifat ilustratif**.
  Angkanya menggambarkan magnitudo yang masuk akal, bukan kalibrasi untuk keperluan regulasi.
- `ScaleToHorizon` memakai aturan akar waktu, yang mengasumsikan return saling independen.
- `PortfolioSimulation` merebalancing ke bobot konstan di setiap langkah dengan GBM berkorelasi.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
