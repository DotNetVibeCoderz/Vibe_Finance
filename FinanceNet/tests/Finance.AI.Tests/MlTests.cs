using Finance.Core;
using Finance.Core.Market;
using Finance.Mathematics;
using Finance.ML;

namespace Finance.AI.Tests;

public class MlTests
{
    private static (Matrix X, double[] Y) LinearData(int n, ulong seed = 1, double noise = 0.1)
    {
        var rng = new FastRandom(seed);
        var x = new Matrix(n, 3);
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < 3; j++) x[i, j] = rng.NextGaussian();
            y[i] = 1.0 + 2 * x[i, 0] - 0.5 * x[i, 1] + noise * rng.NextGaussian();
        }
        return (x, y);
    }

    [Fact]
    public void LinearAndRidgeRecoverCoefficients()
    {
        var (x, y) = LinearData(500);
        var lin = new LinearRegressionModel();
        lin.Fit(x, y);
        Assert.Equal(2.0, lin.Result!.Coefficients[1], 1);
        var ridge = new RidgeRegressionModel(0.01);
        ridge.Fit(x, y);
        Assert.Equal(1.0, ridge.Coefficients[0], 1);
        Assert.True(Metrics.RSquared(y, ridge.PredictAll(x)) > 0.98);
    }

    [Fact]
    public void LogisticRegressionSeparatesClasses()
    {
        var (x, yLin) = LinearData(600);
        var y = yLin.Select(v => v > 1 ? 1.0 : 0.0).ToArray();
        var model = new LogisticRegressionModel();
        model.Fit(x, y);
        var p = model.PredictProbaAll(x);
        Assert.True(Metrics.Accuracy(y, p.Select(v => v >= 0.5 ? 1.0 : 0).ToArray()) > 0.95);
        Assert.True(Metrics.RocAuc(y, p) > 0.98);
        Assert.True(model.Coefficients[1] > 0 && model.Coefficients[2] < 0);
    }

    [Fact]
    public void TreesFitNonLinearTargets()
    {
        var rng = new FastRandom(3);
        int n = 800;
        var x = new Matrix(n, 2);
        var y = new double[n];
        var cls = new double[n];
        for (int i = 0; i < n; i++)
        {
            x[i, 0] = rng.NextDouble(-3, 3);
            x[i, 1] = rng.NextDouble(-3, 3);
            y[i] = Math.Sin(x[i, 0]) + (x[i, 1] > 0 ? 1 : 0);
            cls[i] = x[i, 0] * x[i, 1] > 0 ? 1 : 0;
        }
        var tree = new DecisionTreeRegressor(new TreeOptions { MaxDepth = 8, MinSamplesLeaf = 3 });
        tree.Fit(x, y);
        Assert.True(Metrics.RSquared(y, tree.PredictAll(x)) > 0.95);

        var gb = new GradientBoostingRegressor(150, 0.1);
        gb.Fit(x, y);
        Assert.True(gb.TrainingLoss[^1] < gb.TrainingLoss[0] / 5);
        Assert.True(Metrics.RSquared(y, gb.PredictAll(x)) > 0.95);

        var rf = new RandomForestClassifier(60, new TreeOptions { MaxDepth = 8, MinSamplesLeaf = 2, MaxFeatures = 2 });
        rf.Fit(x, cls);
        var pred = rf.PredictProbaAll(x).Select(p => p >= 0.5 ? 1.0 : 0).ToArray();
        Assert.True(Metrics.Accuracy(cls, pred) > 0.95);
        Assert.Equal(1.0, rf.FeatureImportances.Sum(), 6);

        var rfr = new RandomForestRegressor(40);
        rfr.Fit(x, y);
        Assert.True(Metrics.RSquared(y, rfr.PredictAll(x)) > 0.9);
    }

    [Fact]
    public void FeaturesHaveNoLookAhead()
    {
        var bars = SyntheticMarket.GeometricBrownianBars(300);
        var f = FeatureEngineering.FromBars(bars);
        Assert.True(f.Rows > 200);
        Assert.Equal(f.Names.Length, f.Columns);
        for (int i = 0; i < f.Rows; i++)
            for (int j = 0; j < f.Columns; j++) Assert.False(double.IsNaN(f.Values[i, j]));

        var changed = (Bar[])bars.Clone();
        changed[^1] = changed[^1] with { Close = changed[^1].Close * 2, High = changed[^1].High * 2 };
        var g = FeatureEngineering.FromBars(changed);
        for (int j = 0; j < f.Columns; j++) Assert.Equal(f.Values[f.Rows - 2, j], g.Values[g.Rows - 2, j], 12);

        var fwd = FeatureEngineering.ForwardReturns(bars, f, 1);
        Assert.True(double.IsNaN(fwd[^1]));
        Assert.False(double.IsNaN(fwd[0]));
    }

    [Fact]
    public void LabelingAndFractionalDifferentiation()
    {
        var close = new double[60];
        for (int i = 0; i < close.Length; i++) close[i] = 100 * Math.Exp(0.01 * i + 0.002 * Math.Sin(i * 1.3));
        var labels = Labeling.TripleBarrier(close, maxHolding: 10, upperMultiplier: 1, lowerMultiplier: 1, volatilityWindow: 10);
        Assert.NotEmpty(labels);
        Assert.All(labels, l => Assert.Equal(1, l.Label));
        Assert.Contains(labels, l => l.Hit == BarrierHit.Upper);
        var meta = Labeling.MetaLabels(labels, labels.Select(_ => -1).ToList());
        Assert.All(meta, m => Assert.Equal(0, m));

        var (fh, valid) = Labeling.FixedHorizon(close, 5);
        Assert.Equal(1, fh[0]);
        Assert.False(valid[^1]);

        var w = FractionalDifferentiation.Weights(0.5, 1e-3);
        Assert.Equal(1, w[0], 12);
        Assert.Equal(-0.5, w[1], 12);
        Assert.Equal(-0.125, w[2], 12);
        var d1 = FractionalDifferentiation.Differentiate(close, 1.0);
        Assert.Equal(close[10] - close[9], d1[10], 10);
    }

    [Fact]
    public void PurgedKFoldPurgesAndEmbargoes()
    {
        int n = 100, h = 5;
        foreach (var s in CrossValidation.PurgedKFold(n, 5, h, 0.03))
        {
            int start = s.Test.Min(), end = s.Test.Max();
            Assert.DoesNotContain(s.Train, i => i < start && i + h >= start);
            Assert.DoesNotContain(s.Train, i => i > end && i <= end + 3);
            Assert.Empty(s.Train.Intersect(s.Test));
        }
        var wf = CrossValidation.WalkForward(100, 40, 20, expanding: false, gap: 2).ToList();
        Assert.Equal(3, wf.Count);
        Assert.All(wf, s => Assert.True(s.Train.Max() + 2 < s.Test.Min() + 1));

        var (x, y) = LinearData(200);
        var scores = CrossValidation.Evaluate(() => new LinearRegressionModel(), x, y, CrossValidation.PurgedKFold(200, 4));
        Assert.All(scores, sc => Assert.True(sc < 0.05));
    }

    [Fact]
    public void ForecastersRecoverDynamics()
    {
        var rng = new FastRandom(9);
        var s = new double[2000];
        for (int t = 1; t < s.Length; t++) s[t] = 0.5 + 0.7 * s[t - 1] + 0.1 * rng.NextGaussian();
        var ar = new AutoRegressiveModel(1);
        ar.Fit(s);
        Assert.Equal(0.7, ar.Coefficients[0], 1);
        var f = ar.Forecast(200);
        Assert.Equal(0.5 / 0.3, f[^1], 1);

        var trend = Enumerable.Range(0, 100).Select(i => 10 + 2.0 * i + 0.01 * rng.NextGaussian()).ToArray();
        var arima = new ArimaModel(1, 1);
        arima.Fit(trend);
        Assert.Equal(10 + 2.0 * 104, arima.Forecast(5)[^1], 0);

        var seasonal = Enumerable.Range(0, 96).Select(i => 50 + 0.5 * i + 5 * Math.Sin(2 * Math.PI * i / 12)).ToArray();
        var hw = new HoltWintersModel(12);
        hw.Fit(seasonal);
        var hf = hw.Forecast(12);
        for (int h = 0; h < 12; h++)
        {
            int i = 96 + h;
            Assert.Equal(50 + 0.5 * i + 5 * Math.Sin(2 * Math.PI * i / 12), hf[h], 0);
        }
    }

    private static (Matrix Features, double[] Returns) PredictableMarket(int n, ulong seed = 4)
    {
        // The feature is a noisy preview of the next return's sign: a learnable edge.
        var rng = new FastRandom(seed);
        var x = new Matrix(n, 1);
        var r = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sign = rng.NextDouble() < 0.5 ? -1 : 1;
            r[i] = sign * (0.005 + 0.005 * rng.NextDouble());
            x[i, 0] = rng.NextDouble() < 0.9 ? sign : -sign;
        }
        return (x, r);
    }

    [Fact]
    public void LightweightAgentsLearnAnEdge()
    {
        var (x, r) = PredictableMarket(600);
        var env = new TradingEnvironment(x, r, 0.0001);
        var q = new QLearningAgent(binsPerFeature: 2);
        q.Discretize(x);
        q.Train(env, 30);
        Assert.True(q.Evaluate(env).TotalReward > 0.5);

        var lin = new LinearQAgent(learningRate: 0.05);
        lin.Train(new TradingEnvironment(x, r.Select(v => v * 100).ToArray(), 0.01), 20);
        Assert.True(lin.Evaluate(env).TotalReward > 0.5);
    }

    [Fact]
    public void RlNetDqnAgentLearnsAnEdge()
    {
        var (x, r) = PredictableMarket(400);
        var env = new TradingEnvironment(x, r, 0.0001);
        var agent = new DeepTradingAgent(DeepRlAlgorithm.Dqn, new[] { 32 }, episodeLength: 200)
        {
            DqnOptions = new RLNet.Agents.DqnOptions
            {
                HiddenSizes = new[] { 32 },
                LearningRate = 3e-3f,
                Gamma = 0.5f,
                LearningStarts = 300,
                BatchSize = 32,
                TargetUpdateInterval = 100,
                Epsilon = RLNet.Agents.Schedule.Linear(1f, 0.05f, 0.5f),
            },
        };
        var returns = agent.Train(env, 60);
        Assert.Equal(60, returns.Length);
        var eval = agent.Evaluate(env);
        Assert.True(eval.TotalReward > 0.5, $"DQN total reward {eval.TotalReward}");
    }

    [Fact]
    public void RlNetPpoAgentTrainsThroughTrainer()
    {
        var (x, r) = PredictableMarket(400, 8);
        var rlEnv = new RlNetTradingEnvironment(x, r, 0.0001, episodeLength: 100);
        var agent = new DeepTradingAgent(DeepRlAlgorithm.Ppo, new[] { 32 })
        {
            PpoOptions = new RLNet.Agents.PpoOptions { HiddenSizes = new[] { 32 }, LearningRate = 3e-3f, Gamma = 0.5f, RolloutLength = 512, MinibatchSize = 64, Epochs = 4 },
        };
        var report = agent.Train(rlEnv, new RLNet.Training.TrainingOptions { MaxSteps = 30_000, Seed = 3 });
        Assert.True(report.FinalAverageReturn > report.Returns.Take(5).Average(), $"PPO avg {report.FinalAverageReturn}");
        Assert.True(agent.Evaluate(new TradingEnvironment(x, r, 0.0001)).TotalReward > 0.5);
    }

    [Fact]
    public void FactorModelsRecoverExposures()
    {
        var rng = new FastRandom(21);
        int T = 500;
        var f = new Matrix(T, 2);
        var asset = new double[T];
        for (int t = 0; t < T; t++)
        {
            f[t, 0] = 0.01 * rng.NextGaussian();
            f[t, 1] = 0.01 * rng.NextGaussian();
            asset[t] = 0.0002 + 1.2 * f[t, 0] - 0.4 * f[t, 1] + 0.002 * rng.NextGaussian();
        }
        var exp = FactorModels.TimeSeriesRegression(asset, f, new[] { "MKT", "SMB" });
        Assert.Equal(1.2, exp.Betas[0], 1);
        Assert.Equal(-0.4, exp.Betas[1], 1);
        Assert.True(exp.RSquared > 0.9);

        // One-factor panel: PCA's first component should dominate.
        int n = 8;
        var panel = new Matrix(T, n);
        for (int t = 0; t < T; t++)
            for (int i = 0; i < n; i++) panel[t, i] = (0.5 + 0.1 * i) * f[t, 0] + 0.001 * rng.NextGaussian();
        var pca = FactorModels.Pca(panel, 3);
        Assert.True(pca.ExplainedVarianceRatio[0] > 0.95);
        Assert.True(pca.Loadings[n - 1, 0] > 0);

        // Fama-MacBeth: returns = 0.02 * exposure.
        var rets = new List<double[]>();
        var exposures = new List<Matrix>();
        for (int t = 0; t < 60; t++)
        {
            var e = new Matrix(30, 1);
            var rr = new double[30];
            for (int i = 0; i < 30; i++) { e[i, 0] = rng.NextGaussian(); rr[i] = 0.02 * e[i, 0] + 0.005 * rng.NextGaussian(); }
            rets.Add(rr);
            exposures.Add(e);
        }
        var fm = FactorModels.FamaMacBeth(rets, exposures, new[] { "Value" });
        Assert.Equal(0.02, fm.MeanPremia[1], 3);
        Assert.True(fm.TStatistics[1] > 10);
    }

    [Fact]
    public void FactorResearchFindsPredictiveSignal()
    {
        var rng = new FastRandom(5);
        int T = 120, n = 40;
        var prices = new List<double[]>();
        var factor = new List<double[]>();
        var cur = Enumerable.Repeat(100.0, n).ToArray();
        var signal = Enumerable.Range(0, n).Select(_ => rng.NextGaussian()).ToArray();
        for (int t = 0; t < T; t++)
        {
            prices.Add((double[])cur.Clone());
            factor.Add((double[])signal.Clone());
            for (int i = 0; i < n; i++) cur[i] *= 1 + 0.002 * signal[i] + 0.01 * rng.NextGaussian();
            for (int i = 0; i < n; i++) signal[i] = 0.9 * signal[i] + 0.3 * rng.NextGaussian();
        }
        var sheet = FactorResearch.Analyze(factor, prices, quantiles: 5, horizon: 1, maxDecay: 5);
        Assert.True(sheet.MeanRankIc > 0.05);
        Assert.True(sheet.LongShortSpread > 0);
        Assert.True(sheet.QuantileMeanReturns[^1] > sheet.QuantileMeanReturns[0]);
        Assert.InRange(sheet.TopQuantileTurnover, 0, 1);
        Assert.Equal(5, sheet.IcDecay.Length);
    }

    [Fact]
    public void KernelShapMatchesLinearAttribution()
    {
        var (x, y) = LinearData(300);
        var lin = new LinearRegressionModel();
        lin.Fit(x, y);
        var shap = new KernelShap(lin, x, maxBackground: 300);
        var row = x.Row(7).ToArray();
        var e = shap.Explain(row, new[] { "a", "b", "c" });
        Assert.Equal(e.Prediction - e.BaseValue, e.Values.Sum(), 9);
        var means = Enumerable.Range(0, 3).Select(j => Stats.Mean(x.Column(j))).ToArray();
        var exact = Explain.LinearAttribution(lin.Result!.Slopes, row, means);
        for (int j = 0; j < 3; j++) Assert.Equal(exact[j], e.Values[j], 6);
        Assert.Equal("a", e.Ranked().First().Name);

        // Sampling path (more than 12 features).
        var rng = new FastRandom(2);
        var wide = new Matrix(100, 14);
        for (int i = 0; i < 100; i++) for (int j = 0; j < 14; j++) wide[i, j] = rng.NextGaussian();
        var ks = new KernelShap(r => 3 * r[0] + r[13], wide, 30);
        var ew = ks.Explain(wide.Row(0), samples: 3000);
        Assert.Equal(ew.Prediction - ew.BaseValue, ew.Values.Sum(), 9);
        Assert.True(Math.Abs(ew.Values[0]) > Math.Abs(ew.Values[5]));
    }

    [Fact]
    public void PermutationImportanceAndPdp()
    {
        var (x, y) = LinearData(300);
        var lin = new LinearRegressionModel();
        lin.Fit(x, y);
        var imp = Explain.PermutationImportance(lin, x, y);
        Assert.True(imp[0] > imp[1] && imp[1] > imp[2]);
        var pd = Explain.PartialDependence(lin, x, 0, 10, 5);
        double slope = (pd.Average[^1] - pd.Average[0]) / (pd.Grid[^1] - pd.Grid[0]);
        Assert.Equal(2.0, slope, 1);
        Assert.Equal(5, pd.Individual.Length);

        var cov = Matrix.FromArray(new double[,] { { 0.04, 0.01 }, { 0.01, 0.09 } });
        var (contrib, spec, total) = Explain.FactorRiskAttribution(new[] { 1.0, 0.5 }, cov, 0.002);
        Assert.Equal(total, contrib.Sum() + spec, 12);
        Assert.Equal(0.04 + 2 * 0.5 * 0.01 + 0.25 * 0.09 + 0.002, total, 12);
    }
}
