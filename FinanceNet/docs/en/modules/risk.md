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

- The six historical stress scenarios use **rounded, stylised shock sizes**. They illustrate
  plausible magnitudes; they are not calibrated for regulatory use.
- `ScaleToHorizon` uses the square-root-of-time rule, which assumes independent returns.
- `PortfolioSimulation` rebalances to constant weights every step under correlated GBM.

---

Made by Gravicode Studios, led by Kang Fadhil
