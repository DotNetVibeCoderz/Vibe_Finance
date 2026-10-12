using Finance.Core;
using Finance.Mathematics;
using Finance.ML;

namespace Finance.AI.Tests;

/// <summary>
/// Portfolio RL on Gravicode.RLNet. The environment's arithmetic is checked by hand; the agents are checked on a
/// market where the right answer is known (one asset has a much better risk-adjusted drift), out of sample.
/// </summary>
public class PortfolioRlTests
{
    /// <summary>Asset 0: steady winner; asset 1: noisy loser; asset 2: noisy, flat.</summary>
    private static Matrix Market(int rows, ulong seed)
    {
        var rng = new FastRandom(seed);
        var m = new Matrix(rows, 3);
        for (int t = 0; t < rows; t++)
        {
            m[t, 0] = 0.0015 + 0.004 * rng.NextGaussian();
            m[t, 1] = -0.0010 + 0.020 * rng.NextGaussian();
            m[t, 2] = 0.0000 + 0.015 * rng.NextGaussian();
        }
        return m;
    }

    [Fact]
    public void EnvironmentChargesCostsAndDriftsWeights()
    {
        // Two assets + cash. Returns +10% and −5%; target 50/30/20 from all-cash: turnover = 0.5 + 0.3 + 0.8 = 1.6.
        var r = Matrix.FromRows(new[] { new[] { 0.0, 0.0 }, new[] { 0.10, -0.05 }, new[] { 0.0, 0.0 } });
        var env = new PortfolioAllocationEnvironment(r, lookback: 1, transactionCost: 0.001, rewardScale: 1);
        env.Reset(1);
        // Scores whose softmax (temperature 3) is exactly 0.5 / 0.3 / 0.2.
        float[] a = { (float)(Math.Log(0.5) / 3), (float)(Math.Log(0.3) / 3), (float)(Math.Log(0.2) / 3) };
        var w = PortfolioAllocationEnvironment.ToWeights(a, 3);
        Assert.Equal(0.5, w[0], 6);
        Assert.Equal(0.2, w[2], 6);

        var step = env.Step(a);
        double gross = 0.5 * 0.10 + 0.3 * -0.05;               // 0.035
        double net = (1 + gross) * (1 - 0.001 * 1.6) - 1;
        Assert.Equal(net, env.LastPortfolioReturn, 6);
        Assert.Equal(Math.Log(1 + net), step.Reward, 5);
        Assert.Equal(0.5 * 1.10 / 1.035, env.Weights[0], 6);    // drift after the period
        Assert.Equal(0.2 / 1.035, env.Weights[2], 6);
    }

    [Fact]
    public void SacLearnsToFavourTheDominantAssetOutOfSample()
    {
        using var agent = new PortfolioRlAgent(PortfolioRlAlgorithm.Sac, lookback: 5, hiddenSizes: new[] { 32, 32 }, seed: 3)
        {
            EpisodeLength = 100,
        };
        agent.Train(Market(1500, 1), steps: 6000);
        var run = agent.Evaluate(Market(400, 99));
        double avgWinner = run.Weights.Average(w => w[0]);
        Assert.True(avgWinner > 0.5, $"average weight on the dominant asset {avgWinner:P0}");
        Assert.True(run.Sharpe() > 1, $"out-of-sample Sharpe {run.Sharpe():F2}");
    }

    [Fact]
    public void PpoPicksTheDominantPortfolio()
    {
        using var agent = new PortfolioRlAgent(PortfolioRlAlgorithm.Ppo, lookback: 5, hiddenSizes: new[] { 32, 32 }, seed: 5)
        {
            EpisodeLength = 100,
            PpoOptions = new RLNet.Agents.PpoOptions
            {
                HiddenSizes = new[] { 32, 32 }, LearningRate = 3e-4f, Gamma = 0.5f, RolloutLength = 1024, MinibatchSize = 64, Epochs = 4, EntropyCoefficient = 0.005f,
            },
        };
        agent.Train(Market(1500, 2), steps: 60_000);
        var run = agent.Evaluate(Market(400, 98));
        double allInWinner = run.Weights.Count(w => w[0] > 0.99) / (double)run.Weights.Length;
        Assert.True(allInWinner > 0.7, $"chose the all-in-winner portfolio {allInWinner:P0} of the time");
    }

    [Fact]
    public void GpuBackendTrains()
    {
        using var agent = new PortfolioRlAgent(PortfolioRlAlgorithm.Sac, lookback: 5, hiddenSizes: new[] { 16 }, seed: 1, useGpu: true)
        {
            EpisodeLength = 50,
            SacOptions = new RLNet.Agents.SacOptions { HiddenSizes = new[] { 16 }, LearningStarts = 64, BatchSize = 32 },
        };
        var report = agent.Train(Market(300, 4), steps: 300);
        Assert.False(string.IsNullOrEmpty(agent.Backend.Name));
        Assert.True(report.TotalSteps >= 300);
        Assert.All(agent.Evaluate(Market(100, 5)).Returns, r => Assert.True(double.IsFinite(r)));
    }
}
