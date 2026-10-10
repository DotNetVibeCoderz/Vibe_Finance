# Finance.Portfolio

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/portfolio.md) · Package: `Gravicode.Finance.Portfolio`

Portfolio construction in the spirit of PyPortfolioOpt: expected-return and risk-model estimators,
mean-variance optimisation, Black-Litterman, risk parity and risk budgeting, Hierarchical Risk
Parity, CVaR optimisation, heuristic portfolios and discrete allocation into whole shares.

Namespace: `Finance.Portfolio`.

## Key concepts

- **Mean-variance (Markowitz)** — choose weights *w* to trade expected return μᵀw against variance
  wᵀΣw. Sweeping the target traces the **efficient frontier**; the **max-Sharpe** (tangency)
  portfolio has the best return per unit of risk above the risk-free rate.
- **Estimation risk** — sample means are noisy; shrunk covariances (Ledoit-Wolf) and prior-based
  returns (Black-Litterman) make optimised weights far more stable.
- **Black-Litterman** — starts from the *market-implied* returns that would make market-cap weights
  optimal, then blends in your *views* (absolute: "AAPL returns 10%"; relative: "MSFT beats
  GOOG by 2%"), weighted by confidence.
- **Risk parity** — every asset contributes the same share of portfolio risk, with no return
  forecasts needed. *Risk budgeting* generalises this to any target shares.
- **HRP** (López de Prado) — cluster assets by correlation, order them so similar assets sit
  together, then split capital recursively by inverse variance. Robust when Σ is ill-conditioned.
- **CVaR optimisation** (Rockafellar-Uryasev) — minimise the average loss in the worst tail of
  return scenarios instead of variance.

## Estimators

```csharp
using Finance.Mathematics;
using Finance.Portfolio;

Matrix prices = panel.ToMatrix();                     // rows = dates, columns = assets
Matrix returns = ExpectedReturns.ReturnsFromPrices(prices);
double[] mu = ExpectedReturns.MeanHistorical(returns);           // annualised, compounding
double[] muEma = ExpectedReturns.EmaHistorical(returns, span: 500);
double[] muCapm = ExpectedReturns.Capm(returns, riskFreeRate: 0.03);

Matrix sample = RiskModels.SampleCovariance(returns);
Matrix lw = RiskModels.LedoitWolf(returns);           // recommended default
Matrix ewm = RiskModels.ExponentialCovariance(returns, span: 180);
```

## Mean-variance optimisation

```csharp
string[] names = { "AAPL", "MSFT", "NVDA", "TLT", "GLD" };
var mvo = new MeanVarianceOptimizer(mu, lw, lowerBound: 0, upperBound: 0.4, assets: names);

PortfolioWeights maxSharpe = mvo.MaxSharpe(riskFreeRate: 0.03).Clean();
PortfolioWeights minVol = mvo.MinVolatility();
PortfolioWeights target = mvo.EfficientReturn(0.15);      // minimum risk for a 15% return
PortfolioWeights capped = mvo.EfficientRisk(0.12);        // maximum return for 12% volatility
PortfolioWeights util = mvo.MaxQuadraticUtility(riskAversion: 3);

Console.WriteLine(maxSharpe);                              // asset: weight list
Console.WriteLine(mvo.Performance(maxSharpe, 0.03));      // Return 18.2%, Volatility 14.1%, Sharpe 1.078

foreach (FrontierPoint p in mvo.EfficientFrontier(points: 40, riskFreeRate: 0.03))
    Console.WriteLine($"{p.Volatility:P1} → {p.ExpectedReturn:P1} (Sharpe {p.SharpeRatio:F2})");
```

Negative `lowerBound` allows shorting. `PortfolioWeights` is an `IReadOnlyList<double>` with
`Assets`, `ToDictionary()` and `Clean(cutoff)` (zeroes tiny weights and rounds).

## Black-Litterman

```csharp
double[] prior = BlackLittermanModel.MarketImpliedPriorReturns(marketCaps, lw, riskAversion: 2.5);

var views = new BlackLittermanViews(names)
    .Absolute("NVDA", 0.25, confidence: 0.6)
    .Relative("MSFT", "AAPL", 0.03, confidence: 0.4);

BlackLittermanModel bl = BlackLittermanModel.Create(lw, prior, views, tau: 0.05, OmegaMethod.Idzorek);
double[] posterior = bl.Posterior;
PortfolioWeights blWeights = bl.Weights(riskAversion: 2.5);
// or optimise further: new MeanVarianceOptimizer(bl.Posterior, bl.PosteriorCovariance, 0, 0.4, names).MaxSharpe()
```

`OmegaMethod.HeLitterman` sets view uncertainty proportional to τPΣPᵀ; `OmegaMethod.Idzorek`
translates each view's confidence (0–1) into its uncertainty.

## Risk-based portfolios

```csharp
PortfolioWeights erc = RiskParity.EqualRiskContribution(lw, names);
PortfolioWeights budget = RiskParity.RiskBudget(lw, new[] { 0.3, 0.3, 0.2, 0.1, 0.1 }, names);
double[] rc = RiskParity.RiskContributions(erc.AsSpan(), lw);      // equal by construction

HrpResult hrp = HierarchicalRiskParity.OptimizeFromReturns(returns, names);
// hrp.Weights, hrp.Order (quasi-diagonal leaf order), hrp.Linkage (dendrogram steps for plotting)

var cvar = new CVaROptimizer(returns, beta: 0.95, lowerBound: 0, upperBound: 0.5, assets: names);
PortfolioWeights minCvar = cvar.MinCVaR();
Console.WriteLine($"CVaR {cvar.CVaR(minCvar.AsSpan()):P2}, VaR {cvar.VaR(minCvar.AsSpan()):P2}");

PortfolioWeights ew = HeuristicPortfolios.EqualWeight(names.Length, names);
PortfolioWeights iv = HeuristicPortfolios.InverseVolatility(lw, names);
PortfolioWeights md = HeuristicPortfolios.MaxDiversification(lw, assets: names);
```

## From weights to orders

```csharp
double[] lastPrices = { 228.1, 415.6, 131.2, 92.4, 236.9 };
AllocationResult alloc = DiscreteAllocation.LargestRemainder(maxSharpe, lastPrices, budget: 50_000);
foreach (var (asset, shares) in alloc.Shares) Console.WriteLine($"{asset}: {shares}");
Console.WriteLine($"cash left {alloc.Leftover:N2}");
// DiscreteAllocation.Greedy(...) is the alternative rounding scheme
```

`Rebalancer.Trades(...)` turns current holdings and target weights into a list of
`RebalanceTrade(Asset, Side, Quantity, Price)`.

## Notes and limitations

- The quadratic programs are solved by an accelerated projected-gradient method on the capped
  simplex (`BoxSimplexQp`); results are verified against KKT conditions and closed forms. Bounded
  mean-variance problems (and especially sweeping the efficient frontier) are correct but somewhat
  slow; general linear constraints (sector caps, turnover) are on the roadmap.
- `CVaROptimizer` solves the Rockafellar-Uryasev problem with a **cutting-plane** method. It is
  tested on small universes; behaviour above roughly **30 assets** is untested.
- `RiskModels.FixNonPositiveSemidefinite` repairs a covariance matrix that is not PSD (e.g. built
  from pairwise-complete data).

---

Made by Gravicode Studios, led by Kang Fadhil
