# Finance.ML

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/ml.md) · Package: `Gravicode.Finance.ML`

Machine learning for financial data, following *Advances in Financial Machine Learning* (López de
Prado) where it matters: feature engineering from bars, labelling (fixed horizon, triple barrier,
meta-labels), fractional differentiation, leakage-free cross-validation, models implemented
in-house, forecasting, reinforcement learning (tabular, linear and deep RL via **Gravicode.RLNet**),
factor models, Alphalens-style factor research and explainable AI (SHAP, permutation importance,
PDP/ICE).

Namespace: `Finance.ML`.

## Key concepts

- **Leakage** — financial labels span several bars, so a plain k-fold leaks future information into
  training. **Purged k-fold** removes training samples whose label windows overlap the test fold,
  and an **embargo** drops a few more after it.
- **Triple-barrier labelling** — for each entry, put a profit-take and a stop-loss barrier (scaled
  by recent volatility) and a time limit; the label is whichever is hit first.
- **Meta-labelling** — a primary model chooses the side; a secondary model learns *whether to act*
  on it.
- **Fractional differentiation** — makes a price series stationary while keeping more memory than
  plain returns (d between 0 and 1).
- **Information coefficient (IC)** — the rank correlation between a factor today and returns
  tomorrow; the core metric of factor research.
- **SHAP values** — split one prediction into additive per-feature contributions relative to a
  baseline (Kernel SHAP is model-agnostic).

## Features and labels

```csharp
using Finance.Core.Market;
using Finance.ML;

Bar[] bars = SyntheticMarket.GeometricBrownianBars(3000, seed: 5);
FeatureMatrix fm = FeatureEngineering.FromBars(bars, new FeatureOptions { MomentumWindows = new[] { 5, 20, 60 }, IncludeMacd = true });
Console.WriteLine($"{fm.Rows} rows × {fm.Columns} features: {string.Join(", ", fm.Names)}");

double[] fwd = FeatureEngineering.ForwardReturns(bars, fm, horizon: 5);    // target aligned with fm rows

List<BarrierLabel> events = Labeling.TripleBarrier(bars, maxHolding: 10, multiplier: 2, volatilityWindow: 20);
var (labels, valid) = Labeling.FixedHorizon(bars.Select(b => b.Close).ToArray(), horizon: 5, threshold: 0.002);
double[] fracDiff = FractionalDifferentiation.Differentiate(bars.Select(b => b.Close).ToArray(), d: 0.4);

Standardizer scaler = fm.FitStandardizer();
var xScaled = scaler.Transform(fm.Values);
```

## Models and validation

All models implement `IRegressor` (`Fit(Matrix, ReadOnlySpan<double>)`, `Predict(row)`) or
`IClassifier` (`Fit`, `PredictProba(row)`).

```csharp
var forest = new RandomForestClassifier(trees: 200, new TreeOptions { MaxDepth = 6, MinSamplesLeaf = 20 });
forest.Fit(xTrain, yTrain);                         // y in {0, 1}
double[] p = forest.PredictProbaAll(xTest);
Console.WriteLine($"AUC {Metrics.RocAuc(yTest, p):F3}, accuracy {Metrics.Accuracy(yTest, p.Select(v => v > 0.5 ? 1.0 : 0).ToArray()):P1}");
double[] importance = forest.FeatureImportances;

var gbm = new GradientBoostingRegressor(estimators: 300, learningRate: 0.05);
var cv = CrossValidation.Evaluate(() => new RidgeRegressionModel(lambda: 1.0), x, y,
    CrossValidation.PurgedKFold(x.Rows, folds: 5, labelHorizon: 5, embargoFraction: 0.01));
// also CrossValidation.WalkForward(n, trainSize, testSize, expanding: true, gap: 5)
```

Available models: `LinearRegressionModel`, `RidgeRegressionModel`, `LogisticRegressionModel`,
`DecisionTreeRegressor/Classifier`, `RandomForestRegressor/Classifier`,
`GradientBoostingRegressor`. Trees find splits with a sorted sweep of running statistics (O(n log n)
per feature, not O(n²)).

## Forecasting

```csharp
IForecaster ar = new AutoRegressiveModel(order: 5);
ar.Fit(series);
double[] next = ar.Forecast(10);

var hw = new HoltWintersModel(seasonLength: 12);    // additive trend + season; NaN parameters are fitted
hw.Fit(monthlySeries);
var arima = new ArimaModel(p: 2, d: 1);
```

## Reinforcement learning

`TradingEnvironment` turns features and next-period returns into a step-by-step market:
actions `Short`, `Flat`, `Long`; reward = position × return − transaction cost.

```csharp
using Finance.Mathematics;

var env = new TradingEnvironment(features, nextReturns, transactionCost: 0.0005);

var q = new QLearningAgent(binsPerFeature: 5);
q.Discretize(features);                            // required before training the tabular agent
q.Train(env, episodes: 200);
EpisodeResult res = q.Evaluate(env);               // TotalReward, Rewards, Positions, Equity

var linear = new LinearQAgent(learningRate: 0.01);
linear.Train(env, episodes: 100);
```

**Deep RL with Gravicode.RLNet** — DQN (double-Q, dueling) or PPO on a Gym-style environment:

```csharp
using RLNet.Training;

var rlEnv = new RlNetTradingEnvironment(features, nextReturns, transactionCost: 0.0005, episodeLength: 250);
var agent = new DeepTradingAgent(DeepRlAlgorithm.Ppo, hiddenSizes: new[] { 64, 64 }, seed: 7);
TrainingReport report = agent.Train(rlEnv, new TrainingOptions { MaxEpisodes = 300, Seed = 7 });
Console.WriteLine($"{report.Episodes} episodes, final average return {report.FinalAverageReturn:F3}");

EpisodeResult test = agent.Evaluate(new TradingEnvironment(testFeatures, testReturns));   // ITradingAgent, same as the others
```

## Factor models and research

```csharp
FactorExposure ff = FactorModels.TimeSeriesRegression(stockReturns, factorReturns, new[] { "MKT", "SMB", "HML" });
Console.WriteLine($"alpha {ff.AnnualizedAlpha():P2} (t={ff.AlphaTStat:F2}), betas {string.Join(", ", ff.Betas.Select(b => b.ToString("F2")))}, R² {ff.RSquared:F2}");

PcaFactorModel pca = FactorModels.Pca(returnsMatrix, components: 3);
Console.WriteLine($"3 factors explain {pca.CumulativeExplained(3):P1}");

FamaMacBethResult fmb = FactorModels.FamaMacBeth(periodReturns, periodExposures, new[] { "Value", "Momentum" });

// Alphalens-style tear sheet: factor[t][asset] and prices[t][asset]
FactorTearSheet sheet = FactorResearch.Analyze(factorPanel, pricePanel, quantiles: 5, horizon: 1, maxDecay: 10);
Console.WriteLine($"mean rank IC {sheet.MeanRankIc:F3}, IC IR {sheet.IcInformationRatio:F2}, long-short {sheet.LongShortSpread:P2}, turnover {sheet.TopQuantileTurnover:P0}");
```

## Explainability

```csharp
var shap = new KernelShap(gbm, background: xTrain, maxBackground: 50);
ShapExplanation e = shap.Explain(xTest.Row(0), fm.Names);
foreach (var (name, value) in e.Ranked().Take(5)) Console.WriteLine($"{name,-16} {value:+0.0000;-0.0000}");

double[] perm = Explain.PermutationImportance(gbm, xTest, yTest, repeats: 5);
PartialDependence pdp = Explain.PartialDependence(gbm, xTest, feature: 0, gridPoints: 20);   // Grid, Average, Individual (ICE)
var (factorContrib, specific, total) = Explain.FactorRiskAttribution(exposures, factorCovariance, specificVariance);
```

To explain a classifier, wrap it: `classifier.AsProbabilityRegressor()`.

## Notes and limitations

- Models are implemented in-house for transparency and zero native dependencies; they are not a
  replacement for large-scale gradient-boosting libraries on millions of rows.
- RL results on synthetic data demonstrate the mechanics; they are not evidence of a tradable edge.
- `ForwardReturns` leaves the final `horizon` rows without a target (`NaN`); drop them before
  fitting.

---

Made by Gravicode Studios, led by Kang Fadhil
