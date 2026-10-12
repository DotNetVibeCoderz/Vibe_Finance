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

### Peramal neural (v0.4)

```csharp
// N-BEATS (interpretable): stack tren + musiman, ekspansi basis yang bisa dibaca kembali.
var nbeats = new NBeatsForecaster(lookback: 36, horizon: 12, new NBeatsOptions { Steps = 800 });
nbeats.Fit(series);
double[] next12 = nbeats.Forecast(series);
var (total, parts) = nbeats.Decompose(series);            // parts[NBeatsStackType.Trend], [Seasonality]; they sum to total

// Temporal Fusion Transformer: kovariat masa lalu, kovariat masa depan yang diketahui, ramalan kuantil.
var tft = new TemporalFusionTransformer(lookback: 14, horizon: 3, pastCovariates: 1, knownCovariates: 1,
    new TftOptions { Hidden = 12, Steps = 250 }, pastNames: new[] { "volume" }, knownNames: new[] { "promo" });
tft.Fit(y, past, known);                                  // past[t, k], known[t, k]; known must reach t + horizon in Predict
TftForecast f = tft.Predict(y, past, knownIncludingFuture);
double[] median = f.Median, lo = f.At(0.1), hi = f.At(0.9);
// f.Attention[h, t]: what each future step attends to; f.EncoderImportance / f.DecoderImportance: variable selection weights

// Beberapa deret dengan kovariat statis: satu model, jendela diambil lintas deret, penskalaan target per deret.
var multi = new TemporalFusionTransformer(lookback: 2, horizon: 6, knownCovariates: 2, options: new TftOptions { Steps = 400 },
    knownNames: new[] { "sin", "cos" }, staticCovariates: 2, staticNames: new[] { "store_type", "region" });
multi.Fit(new[] { new TftSeries(y0, Past: null, Known: known0, Static: new[] { 0.0, 1 }),
                  new TftSeries(y1, Past: null, Known: known1, Static: new[] { 2.0, 0 }) });
TftForecast g = multi.Predict(series: 1, y1Recent, known: known1IncludingFuture);   // di-unscale dengan mean/std deret 1
// g.StaticVariables / g.StaticImportance: bobot seleksi variabel statis untuk deret tersebut
```

Keduanya ditulis di atas autodiff reverse-mode GraviNum dan dilatih dengan Adam (gradient clipping, mini-batch).
TFT mengikuti Lim dkk. (2021): gated residual network, variable selection network, encoder-decoder LSTM,
interpretable multi-head attention (value bersama, mask kausal) dan head kuantil dengan pinball loss. Kovariat
statis memiliki variable selection network sendiri dan empat GRN konteks: c_s mengondisikan bobot seleksi
encoder/decoder, c_e mengondisikan GRN static enrichment setelah LSTM, dan c_c / c_h menginisialisasi cell dan
hidden state LSTM. `Fit(IReadOnlyList<TftSeries>)` melatih satu model pada beberapa deret (setiap target
distandarkan dengan mean/std-nya sendiri, kovariat dan statis dengan statistik gabungan); `Predict(series, …)`
meramal satu deret yang sudah dilatih. Tanpa kovariat statis model tidak berubah (tanpa vektor konteks, tanpa
lapisan enrichment), dan `Fit`/`Predict` satu deret bekerja seperti sebelumnya. Tes beda-hingga memeriksa
gradien melalui seluruh model, termasuk setiap parameter jalur statis. Hasil terukur di tes (laptop 4 core, CPU):

| Model | Data | Hasil | Waktu latih |
|---|---|---|---|
| N-BEATS (tren + musiman, 800 langkah) | tren + musim 12 langkah + noise | MAE di bawah 0,6× nilai terakhir dan di bawah seasonal naive | ~1 s |
| TFT (hidden 12, 250 langkah, batch 16) | musim + promo diketahui (+3) + kovariat tak relevan | MAE di bawah 0,6× seasonal naive; interval 80% mencakup 60–97%; menyalakan promo menggeser ramalan 2–4 | ~25 s |
| TFT multi-deret + 2 statis (hidden 12, 400 langkah, batch 16, lookback 2, horizon 6) | 8 deret, y = (1+s)·(10 + sin(2πt/24 + πs/2)) + 0,3(1+s)·noise, s ∈ {0..3} statis, ditambah statis tak relevan z ∈ {0,1}; setelah penskalaan per deret, deret hanya berbeda fase, yang tak bisa diungkap dua lag | MAE 0,635 vs seasonal naive 0,846 dan TFT yang sama tanpa statis 1,274 (batas noise ≈ 0,60); kepentingan statis s 0,83, z 0,17 (seed lain: MAE 0,63–0,67, bobot s 0,60–0,91) | ~20 s per model |

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

### Agen alokasi portofolio (v0.4)

SAC dan TD3 (bobot kontinu) atau PPO (memilih di antara portofolio target: tiap aset all-in, bobot sama, kas),
di atas Gravicode.RLNet. Observasinya adalah `lookback` return terakhir plus bobot saat ini; reward-nya log
return setelah biaya transaksi proporsional terhadap turnover, dan bobot bergeser mengikuti harga di antara
rebalancing. `useGpu: true` melatih di Gravicode.RLNet.Gpu (CUDA, lalu OpenCL) dan kembali ke CPU bila tidak ada.

```csharp
// returns: Matrix [periode, aset] berisi simple return
using var agent = new PortfolioRlAgent(PortfolioRlAlgorithm.Sac, lookback: 20, useGpu: true)
{
    TransactionCost = 0.001, IncludeCash = true, EpisodeLength = 250,
};
agent.Train(trainReturns, steps: 20_000);
PortfolioRlRun run = agent.Evaluate(testReturns);        // Weights per period, Returns, Equity, AverageTurnover
Console.WriteLine($"Sharpe {run.Sharpe():F2}, total {run.TotalReturn:P1}, turnover {run.AverageTurnover:P1}  [{agent.Backend.Name}]");

// Environment-nya adalah environment RLNet biasa untuk agen Anda sendiri:
var cont = new PortfolioAllocationEnvironment(trainReturns, lookback: 20);     // Box action → softmax weights
var disc = new PortfolioTemplateEnvironment(trainReturns, lookback: 20);       // pick one of N target portfolios
```

Di tes, SAC belajar memegang aset dominan pada pasar sintetis (bobot rata-rata di atas 0,5, Sharpe out-of-sample
di atas 1, ~16 s di CPU), dan PPO memilih template all-in-pemenang lebih dari 70% waktu.

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
