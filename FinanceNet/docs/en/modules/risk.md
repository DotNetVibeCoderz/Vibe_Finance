# Finance.Risk

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/risk.md) · Package: `Gravicode.Finance.Risk`

Market-risk measurement and performance analytics: Value-at-Risk and Expected Shortfall by five
methods, VaR attribution and backtesting, stress testing, Monte Carlo portfolio simulation,
pyfolio-style performance metrics and volatility models (GARCH, EWMA).

Namespace: `Finance.Risk`.

## Key concepts

- **VaR** — the loss that is not exceeded with a given confidence over a horizon (99% one-day VaR
  of 2% = on 99 days out of 100 you lose less than 2%). **Expected Shortfall (CVaR)** is the
  average loss on the days beyond VaR, so it sees the shape of the tail.
- **Methods** — *historical* (empirical quantile), *parametric normal*, *Student-t* (fat tails),
  *Cornish-Fisher* (adjusts the normal quantile for skew and kurtosis) and *Monte Carlo*.
- **Component VaR** — splits portfolio VaR into per-asset contributions that sum to the total
  (Euler allocation).
- **VaR backtesting** — count exceptions (days loss > VaR). *Kupiec* tests whether the count fits
  the confidence level; *Christoffersen* tests whether exceptions cluster; the *Basel traffic
  light* classifies the model green/yellow/red.
- **Stress testing** — apply factor shocks (equities −40%, rates +200bp…) to positions with
  sensitivities (beta, duration).
- **GARCH(1,1)** — volatility that reacts to shocks and mean-reverts: σ²ₜ = ω + α·ε²ₜ₋₁ + β·σ²ₜ₋₁.

## Value-at-Risk

```csharp
using Finance.Risk;

VarEstimate hist = ValueAtRisk.Historical(returns, confidence: 0.99);
VarEstimate norm = ValueAtRisk.ParametricNormal(returns, 0.99);
VarEstimate t5 = ValueAtRisk.ParametricStudentT(returns, degreesOfFreedom: 5, confidence: 0.99);
VarEstimate cf = ValueAtRisk.CornishFisher(returns, 0.99);
VarEstimate any = ValueAtRisk.Estimate(returns, VarMethod.CornishFisher, 0.975);

// Monte Carlo for a multi-asset portfolio (daily means and covariance)
VarEstimate mc = ValueAtRisk.MonteCarlo(weights, dailyMeans, dailyCovariance, confidence: 0.99, simulations: 200_000);

Console.WriteLine($"VaR {hist.ValueAtRisk:P2}  ES {hist.ExpectedShortfall:P2}  on $1M: {hist.ValueAtRiskAmount(1_000_000):N0}");
double tenDay = ValueAtRisk.ScaleToHorizon(hist.ValueAtRisk, 10);   // square-root-of-time
```

All estimates are **positive loss fractions**.

### Attribution and backtesting

```csharp
VarDecomposition d = VarAttribution.Decompose(weights, covariance, 0.99);
// d.Marginal, d.Component (sums to d.PortfolioVar), d.PercentContribution
double incremental = VarAttribution.Incremental(weights, delta: new[] { 0.05, -0.05, 0, 0 }, covariance);

VarBacktestResult bt = VarBacktest.Run(realisedReturns, varForecasts, confidence: 0.99);
Console.WriteLine($"{bt.Exceptions}/{bt.Observations} exceptions (expected {bt.ExpectedExceptions:F1}), " +
                  $"Kupiec p={bt.KupiecPValue:F3}, Christoffersen p={bt.ChristoffersenPValue:F3}, zone {bt.Zone}, passes {bt.Passes()}");
```

## Advanced VaR models (v0.2)

```csharp
// Filtered historical simulation: GARCH(1,1)- or EWMA-filtered residuals rescaled to today's volatility.
var fhs = AdvancedVar.FilteredHistorical(returns, 0.99);                    // .Estimate, .CurrentVolatility
VarEstimate tenDay = AdvancedVar.FilteredHistoricalMultiPeriod(returns, horizon: 10, 0.99);

// Extreme value theory: a Generalized Pareto tail fitted to losses above the 95% quantile.
GpdTailFit tail = AdvancedVar.FitTail(returns, thresholdQuantile: 0.95);   // .Xi, .Beta, .ValueAtRisk(p)
VarEstimate evt = AdvancedVar.ExtremeValue(returns, 0.999);

// Liquidity: Bangia et al. spread add-on, and orderly liquidation over T days.
double lvar = AdvancedVar.LiquidityAdjusted(marketVar: 0.021, spreadMean: 0.004, spreadStdDev: 0.0015);
double liq10 = AdvancedVar.OrderlyLiquidation(oneDayVar: 0.021, days: 10);  // √((1+T)(1+2T)/(6T)), not √T

// What-if trades on historical scenarios (rows = scenarios, columns = instruments).
double dVar = AdvancedVar.IncrementalHistorical(weights, trade, scenarios, 0.99);
double[] parts = AdvancedVar.ComponentHistorical(weights, scenarios, 0.99);
double quick = AdvancedVar.IncrementalMarginal(weights, trade, covariance, 0.99); // Euler first-order
```

`VarMethod.FilteredHistorical` and `VarMethod.ExtremeValue` also work through `ValueAtRisk.Estimate`.

## FRTB standardised approach (delta, vega, curvature, DRC)

```csharp
var sens = new[]
{
    new GirrSensitivity("USD", TenorYears: 2, Sensitivity: 1_200_000),   // PV change per 1bp / 0.0001
    new GirrSensitivity("USD", TenorYears: 10, Sensitivity: -800_000),
    new GirrSensitivity("IDR", TenorYears: 5, Sensitivity: 300_000),
};
FrtbDeltaResult girr = FrtbSa.GirrDelta(sens, domesticCurrency: "IDR");
Console.WriteLine($"{girr.Charge:N0} ({girr.Binding} correlation scenario)");
```

`FrtbSa.Aggregate` is the generic sensitivities-based engine: within-bucket Kᵦ, across-bucket
aggregation with the alternative Sᵦ specification, and the max over the low / medium / high
correlation scenarios. Use it for other risk classes by supplying your own weighted sensitivities
and correlations. `GirrDelta` supplies the GIRR delta parameters as written in MAR21 (January 2019).

### Vega and curvature (GIRR)

```csharp
var vega = FrtbSa.GirrVega(new[]
{
    // vega × implied vol, by option maturity × underlying residual maturity
    new GirrVegaSensitivity("USD", OptionMaturityYears: 1, UnderlyingMaturityYears: 5, Sensitivity: 200_000),
    new GirrVegaSensitivity("USD", OptionMaturityYears: 3, UnderlyingMaturityYears: 10, Sensitivity: -150_000),
    new GirrVegaSensitivity("EUR", OptionMaturityYears: 0.5, UnderlyingMaturityYears: 1, Sensitivity: 80_000),
});
var curvature = FrtbSa.GirrCurvature(new[]
{
    // V(x + 1.7%) − V(x), V(x − 1.7%) − V(x), and the summed delta to the curve
    new GirrCurvatureSensitivity("USD", UpChange: -30_000, DownChange: 50_000, Delta: -1_000_000),
    new GirrCurvatureSensitivity("EUR", UpChange: 10_000, DownChange: -2_000, Delta: 400_000),
});
var sbm = FrtbSa.SensitivitiesBasedCharge(vega.ByScenario, curvature.ByScenario);
```

- **Vega** (MAR21.25, MAR21.92-95): RW = min(55%·√(LH/10), 100%); GIRR has LH = 60, so 0.55·√6 = 1.347
  is capped to 100% (`VegaRiskWeight(FrtbVegaClass.EquityLargeCap)` = 0.55·√2 = 77.78%). Within a
  currency ρ = min(e^(−1%·|ΔT|/min T) of the option maturities × the same factor of the underlying
  maturities, 1); γ = 50%; aggregated by the delta engine. Above, the two USD vertices have
  ρ = e^(−0.02)·e^(−0.01) = 0.9704 and K_USD = 65,370.24 (medium). The charge is 121,133.27 (medium),
  117,898.26 (high), **124,284.09 (low, binding)**.
- **Curvature** (MAR21.5, MAR21.98-101): CVR⁺ = −(ΔV⁺ − RW·s), CVR⁻ = −(ΔV⁻ + RW·s), with RW = 1.7%
  (a parallel shift of all curves of a currency, one risk factor per currency). USD: RW·s = −17,000,
  CVR⁺ = 13,000, CVR⁻ = −33,000 → up selected, K = 13,000. EUR: CVR⁺ = −3,200, CVR⁻ = −4,800, both
  negative → K = 0 and the tie goes to the larger ΣCVR (up, S = −3,200). Across currencies
  γ = 50%² = 25%: √(13,000² + 2·0.25·13,000·(−3,200)) = 12,173.74 (medium), 11,958.26 (high),
  **12,385.48 (low, binding)**. Within a bucket ψ = 0 drops cross terms of two negative CVRs and
  Kᵦ± = √max(0, …) floors at zero; across buckets ψ(Sᵦ, S꜀) does the same and, as for delta, a
  negative inner sum caps Sᵦ to [−Kᵦ, Kᵦ].
- **Total** (MAR21.7): `SensitivitiesBasedCharge` adds the components scenario by scenario before
  taking the maximum: low 136,669.57, medium 133,307.01, high 129,856.52 → **136,669.57**.
- Other risk classes: build buckets with `VegaRiskWeight(FrtbVegaClass…)` and
  `VegaCorrelation(ρ_delta, Tₖ, Tₗ)` for `Aggregate`, and pass squared delta correlations to
  `AggregateCurvature` with `CurvatureExposure` for each risk factor.

### Default risk charge, non-securitisations (MAR22)

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

Notional and market value are signed (short < 0). Gross JTD = LGD·notional + (MV − notional), floored at
0 for longs and capped at 0 for shorts; LGD 100% equity / non-senior, 75% senior, 25% covered bonds.
Each JTD is scaled by max(min(M, 1), 0.25). Per obligor a short offsets longs of the same or higher
seniority. Per bucket HBR = Σ long / (Σ long + Σ |short|) and DRCᵦ = max(Σ RW·long − HBR·Σ RW·|short|, 0),
RW: AAA 0.5%, AA 2%, A 3%, BBB 6%, BB 15%, B 30%, CCC 50%, unrated 15%, defaulted 100%. Total = Σ DRCᵦ.

| Obligor | Gross scaled JTDs | Net long | Net short |
|---|---|---|---|
| X (A) | 770,000; −200,000·0.25 = −50,000 (junior short offsets) | 720,000 | 0 |
| Y (BB) | (−375,000 + 10,000)·0.5 = −182,500; 75,000 (covered, more senior) | 0 | −107,500 |
| Z (BBB) | 90,000·0.25 = 22,500; −37,500 (senior short cannot offset a junior long) | 22,500 | −37,500 |
| S (AA) | 300,000 | 300,000 | 0 |

Corporates: HBR = 742,500 / 887,500 = 0.8366; Σ RW·long = 21,600 + 1,350 = 22,950; Σ RW·|short| =
16,125 + 2,250 = 18,375; DRC = 22,950 − 0.8366·18,375 = 7,577.11. Sovereigns: 0.02·300,000 = 6,000.
`drc.Charge` = **13,577.11**.

## Stress testing

```csharp
var book = new List<StressPosition>
{
    StressPosition.Equity("US equities", 600_000, beta: 1.0),
    StressPosition.Equity("Tech", 200_000, beta: 1.4),
    StressPosition.Bond("Treasuries", 300_000, modifiedDuration: 7.5, spreadDuration: 0, convexity: 70),
    StressPosition.Single("Gold", 100_000, RiskFactors.Commodity, sensitivity: 0.6),
};

foreach (StressResult r in StressTester.RunAll(book))             // all built-in historical scenarios, worst first
    Console.WriteLine($"{r.Scenario,-28} {r.TotalPnl,14:N0} ({r.ReturnOnPortfolio:P1})");

var custom = StressScenario.Custom("Rates +300bp, equities -25%", (RiskFactors.Rates, 0.03), (RiskFactors.Equity, -0.25));
StressResult one = StressTester.Run(book, custom);
double k = StressTester.ReverseStressMultiplier(book, custom, targetLoss: 250_000);   // how much worse to lose $250k
```

Built-in `StressScenario.Historical`: Global Financial Crisis 2008, Dot-com Bust 2000-02, COVID-19
Crash 2020, Rates Shock 2022, Black Monday 1987 and Taper Tantrum 2013. Factor names are in
`RiskFactors` (`Equity`, `Rates`, `Credit`, `Fx`, `Commodity`, `Volatility`, `Crypto`).

## Monte Carlo simulation

```csharp
SimulationResult sim = PortfolioSimulation.Run(initialValue: 1_000_000, weights, annualDrift, annualCovariance,
    years: 1, stepsPerYear: 252, paths: 10_000, seed: 42, samplePaths: 20);

double p5 = sim.Percentile(0.05);
double lossProbability = sim.ProbabilityOfLoss;
VarEstimate horizonVar = sim.ValueAtRisk(0.95);
// sim.Fan[k][t] at sim.FanPercentiles (5/25/50/75/95%) over sim.Times — ready for a fan chart
```

## Performance metrics

```csharp
PerformanceReport rep = PerformanceReport.Create(strategyReturns, benchmarkReturns, riskFreeRate: 0.03, periodsPerYear: 252);
Console.WriteLine(rep);                           // text tearsheet
foreach (var (metric, value) in rep.ToRows()) Console.WriteLine($"{metric}: {value}");

double sharpe = PerformanceMetrics.SharpeRatio(returns, riskFreeRate: 0.03);
double sortino = PerformanceMetrics.SortinoRatio(returns);
double mdd = PerformanceMetrics.MaxDrawdown(returns);
var (alpha, beta) = PerformanceMetrics.AlphaBeta(returns, benchmarkReturns);
double ir = PerformanceMetrics.InformationRatio(returns, benchmarkReturns);
double[] rollingSharpe = PerformanceMetrics.RollingSharpe(returns, window: 126);
```

`PerformanceReport` includes cumulative and annual return, volatility, Sharpe, Sortino, Calmar,
Omega, max drawdown and duration, stability, tail ratio, skew, excess kurtosis, downside deviation,
hit rate, best/worst period, 95% VaR/ES and — with a benchmark — alpha, beta, information ratio and
tracking error.

## Volatility models

```csharp
Garch11Model garch = VolatilityModels.FitGarch11(returns);
Console.WriteLine($"ω={garch.Omega:E2} α={garch.Alpha:F3} β={garch.Beta:F3}, long-run vol {garch.LongRunVolatility():P1}");
double[] next10 = garch.ForecastVolatility(10);

double[] riskMetrics = VolatilityModels.Ewma(returns, lambda: 0.94);
double parkinson = VolatilityModels.Parkinson(bars);      // high-low range estimator

var (portVol, contrib, pct) = RiskAttribution.VolatilityContributions(weights, covariance);
```

## Notes and limitations

- FRTB: delta, vega and curvature have built-in parameters for GIRR only (other risk classes through
  `Aggregate` / `AggregateCurvature` with your parameters; vega risk weights cover every class); GIRR
  inflation and cross-currency-basis vega/curvature, the vertex mapping of sensitivities, the DRC for
  securitisations and the correlation trading portfolio, and the residual risk add-on are not
  implemented. The curvature risk weight is an input (default 1.7%). Check the parameters against the
  text your supervisor applies before using the numbers for regulatory capital.
- EVT needs enough exceedances: `FitTail` refuses fewer than 10 losses above the threshold, and
  `ExtremeValue` requires the confidence to lie beyond the threshold quantile.

- The six historical stress scenarios use **rounded, stylised shock sizes**. They illustrate
  plausible magnitudes; they are not calibrated for regulatory use.
- `ScaleToHorizon` uses the square-root-of-time rule, which assumes independent returns.
- `PortfolioSimulation` rebalances to constant weights every step under correlated GBM.

---

Made by Gravicode Studios, led by Kang Fadhil
