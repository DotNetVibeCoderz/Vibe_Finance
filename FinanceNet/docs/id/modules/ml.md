# Finance.ML

[← Indeks](../index.md) · [English](../../en/modules/ml.md) · Paket: `Gravicode.Finance.ML`

Machine learning untuk data keuangan, mengikuti *Advances in Financial Machine Learning* (López de
Prado) di bagian yang penting: rekayasa fitur dari bar, labeling (fixed horizon, triple barrier,
meta-label), diferensiasi fraksional, cross-validation bebas kebocoran, model yang diimplementasikan
sendiri, peramalan, reinforcement learning (tabular, linear, dan deep RL lewat **Gravicode.RLNet**),
model faktor, riset faktor ala Alphalens, serta explainable AI (SHAP, permutation importance, PDP/ICE).

Namespace: `Finance.ML`.

## Konsep utama

- **Kebocoran (leakage)** — label keuangan mencakup beberapa bar, sehingga k-fold biasa membocorkan
  informasi masa depan ke data latih. **Purged k-fold** membuang sampel latih yang jendela labelnya
  tumpang tindih dengan fold uji, dan **embargo** membuang beberapa sampel lagi setelahnya.
- **Labeling triple-barrier** — untuk setiap entry, pasang batas take-profit dan stop-loss (diskalakan
  dengan volatilitas terkini) serta batas waktu; labelnya adalah batas yang tersentuh lebih dulu.
- **Meta-labeling** — model primer memilih arah; model sekunder belajar *apakah perlu bertindak* atas
  sinyal itu.
- **Diferensiasi fraksional** — membuat deret harga stasioner sambil mempertahankan lebih banyak
  "memori" dibanding return biasa (d antara 0 dan 1).
- **Information coefficient (IC)** — korelasi peringkat antara faktor hari ini dan return besok; metrik
  inti riset faktor.
- **Nilai SHAP** — memecah satu prediksi menjadi kontribusi aditif per fitur relatif terhadap baseline
  (Kernel SHAP bersifat model-agnostik).

## Fitur dan label

```csharp
using Finance.Core.Market;
using Finance.ML;

Bar[] bars = SyntheticMarket.GeometricBrownianBars(3000, seed: 5);
FeatureMatrix fm = FeatureEngineering.FromBars(bars, new FeatureOptions { MomentumWindows = new[] { 5, 20, 60 }, IncludeMacd = true });
Console.WriteLine($"{fm.Rows} baris × {fm.Columns} fitur: {string.Join(", ", fm.Names)}");

double[] fwd = FeatureEngineering.ForwardReturns(bars, fm, horizon: 5);    // target sejajar dengan baris fm

List<BarrierLabel> events = Labeling.TripleBarrier(bars, maxHolding: 10, multiplier: 2, volatilityWindow: 20);
var (labels, valid) = Labeling.FixedHorizon(bars.Select(b => b.Close).ToArray(), horizon: 5, threshold: 0.002);
double[] fracDiff = FractionalDifferentiation.Differentiate(bars.Select(b => b.Close).ToArray(), d: 0.4);

Standardizer scaler = fm.FitStandardizer();
var xScaled = scaler.Transform(fm.Values);
```

## Model dan validasi

Semua model mengimplementasikan `IRegressor` (`Fit(Matrix, ReadOnlySpan<double>)`, `Predict(row)`) atau
`IClassifier` (`Fit`, `PredictProba(row)`).

```csharp
var forest = new RandomForestClassifier(trees: 200, new TreeOptions { MaxDepth = 6, MinSamplesLeaf = 20 });
forest.Fit(xTrain, yTrain);                         // y bernilai {0, 1}
double[] p = forest.PredictProbaAll(xTest);
Console.WriteLine($"AUC {Metrics.RocAuc(yTest, p):F3}, akurasi {Metrics.Accuracy(yTest, p.Select(v => v > 0.5 ? 1.0 : 0).ToArray()):P1}");
double[] importance = forest.FeatureImportances;

var gbm = new GradientBoostingRegressor(estimators: 300, learningRate: 0.05);
var cv = CrossValidation.Evaluate(() => new RidgeRegressionModel(lambda: 1.0), x, y,
    CrossValidation.PurgedKFold(x.Rows, folds: 5, labelHorizon: 5, embargoFraction: 0.01));
// juga CrossValidation.WalkForward(n, trainSize, testSize, expanding: true, gap: 5)
```

Model yang tersedia: `LinearRegressionModel`, `RidgeRegressionModel`, `LogisticRegressionModel`,
`DecisionTreeRegressor/Classifier`, `RandomForestRegressor/Classifier`, `GradientBoostingRegressor`.
Tree mencari split dengan sapuan terurut atas statistik berjalan (O(n log n) per fitur, bukan O(n²)).

## Peramalan

```csharp
IForecaster ar = new AutoRegressiveModel(order: 5);
ar.Fit(series);
double[] next = ar.Forecast(10);

var hw = new HoltWintersModel(seasonLength: 12);    // tren + musiman aditif; parameter NaN akan difit
hw.Fit(monthlySeries);
var arima = new ArimaModel(p: 2, d: 1);
```

## Reinforcement learning

`TradingEnvironment` mengubah fitur dan return periode berikutnya menjadi pasar langkah demi langkah:
aksi `Short`, `Flat`, `Long`; reward = posisi × return − biaya transaksi.

```csharp
using Finance.Mathematics;

var env = new TradingEnvironment(features, nextReturns, transactionCost: 0.0005);

var q = new QLearningAgent(binsPerFeature: 5);
q.Discretize(features);                            // wajib sebelum melatih agen tabular
q.Train(env, episodes: 200);
EpisodeResult res = q.Evaluate(env);               // TotalReward, Rewards, Positions, Equity

var linear = new LinearQAgent(learningRate: 0.01);
linear.Train(env, episodes: 100);
```

**Deep RL dengan Gravicode.RLNet** — DQN (double-Q, dueling) atau PPO pada environment bergaya Gym:

```csharp
using RLNet.Training;

var rlEnv = new RlNetTradingEnvironment(features, nextReturns, transactionCost: 0.0005, episodeLength: 250);
var agent = new DeepTradingAgent(DeepRlAlgorithm.Ppo, hiddenSizes: new[] { 64, 64 }, seed: 7);
TrainingReport report = agent.Train(rlEnv, new TrainingOptions { MaxEpisodes = 300, Seed = 7 });
Console.WriteLine($"{report.Episodes} episode, rata-rata return akhir {report.FinalAverageReturn:F3}");

EpisodeResult test = agent.Evaluate(new TradingEnvironment(testFeatures, testReturns));   // ITradingAgent, sama seperti yang lain
```

## Model dan riset faktor

```csharp
FactorExposure ff = FactorModels.TimeSeriesRegression(stockReturns, factorReturns, new[] { "MKT", "SMB", "HML" });
Console.WriteLine($"alpha {ff.AnnualizedAlpha():P2} (t={ff.AlphaTStat:F2}), beta {string.Join(", ", ff.Betas.Select(b => b.ToString("F2")))}, R² {ff.RSquared:F2}");

PcaFactorModel pca = FactorModels.Pca(returnsMatrix, components: 3);
Console.WriteLine($"3 faktor menjelaskan {pca.CumulativeExplained(3):P1}");

FamaMacBethResult fmb = FactorModels.FamaMacBeth(periodReturns, periodExposures, new[] { "Value", "Momentum" });

// Tear sheet ala Alphalens: factor[t][aset] dan prices[t][aset]
FactorTearSheet sheet = FactorResearch.Analyze(factorPanel, pricePanel, quantiles: 5, horizon: 1, maxDecay: 10);
Console.WriteLine($"rata-rata rank IC {sheet.MeanRankIc:F3}, IC IR {sheet.IcInformationRatio:F2}, long-short {sheet.LongShortSpread:P2}, turnover {sheet.TopQuantileTurnover:P0}");
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

Untuk menjelaskan classifier, bungkus terlebih dulu: `classifier.AsProbabilityRegressor()`.

## Catatan dan batasan

- Model diimplementasikan sendiri demi transparansi dan tanpa dependensi native; bukan pengganti
  library gradient boosting skala besar untuk jutaan baris.
- Hasil RL pada data sintetis menunjukkan mekanismenya, bukan bukti adanya keunggulan yang bisa
  ditradingkan.
- `ForwardReturns` membiarkan `horizon` baris terakhir tanpa target (`NaN`); buang baris itu sebelum
  fitting.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
