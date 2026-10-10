using Finance.Core;
using Finance.Mathematics;

namespace Finance.ML;

/// <summary>Trading actions: target position -1, 0 or +1.</summary>
public enum TradingAction { Short = 0, Flat = 1, Long = 2 }

/// <summary>
/// A single-asset trading environment. The state at step t is the feature row t plus the current
/// position; acting sets the position held over (t, t+1] and the reward is position × return
/// minus <see cref="TransactionCost"/> × |change in position|.
/// </summary>
public sealed class TradingEnvironment
{
    private readonly Matrix _features;
    private readonly double[] _returns;

    /// <param name="features">State features, one row per step.</param>
    /// <param name="nextReturns">Simple return from step t to t+1 for each row.</param>
    /// <param name="transactionCost">Proportional cost per unit of position change.</param>
    public TradingEnvironment(Matrix features, double[] nextReturns, double transactionCost = 0.0005)
    {
        if (features.Rows != nextReturns.Length) throw new ArgumentException("One return per feature row.");
        _features = features;
        _returns = nextReturns;
        TransactionCost = transactionCost;
    }

    public double TransactionCost { get; }
    internal Matrix Features => _features;
    internal double[] NextReturns => _returns;
    public int Step { get; private set; }
    public int Position { get; private set; }
    public int Length => _features.Rows;
    public bool Done => Step >= _features.Rows;
    public int FeatureCount => _features.Columns;

    /// <summary>Current observation (features of the current step).</summary>
    public ReadOnlySpan<double> Observation => _features.Row(Math.Min(Step, _features.Rows - 1));

    public void Reset() { Step = 0; Position = 0; }

    /// <summary>Applies an action and returns the reward.</summary>
    public double Act(TradingAction action)
    {
        if (Done) throw new InvalidOperationException("Episode finished.");
        int target = (int)action - 1;
        double reward = target * _returns[Step] - TransactionCost * Math.Abs(target - Position);
        Position = target;
        Step++;
        return reward;
    }
}

/// <summary>Result of running a policy through an environment.</summary>
public sealed record EpisodeResult(double TotalReward, double[] Rewards, int[] Positions, double[] Equity);

/// <summary>Common agent surface.</summary>
public interface ITradingAgent
{
    TradingAction Act(ReadOnlySpan<double> observation, int position, bool explore);

    /// <summary>Trains for a number of episodes; returns the total reward of each episode.</summary>
    double[] Train(TradingEnvironment env, int episodes);
}

public static class TradingAgentExtensions
{
    /// <summary>Greedy evaluation run (no exploration).</summary>
    public static EpisodeResult Evaluate(this ITradingAgent agent, TradingEnvironment env)
    {
        env.Reset();
        var rewards = new List<double>();
        var pos = new List<int>();
        var eq = new List<double>();
        double equity = 1;
        while (!env.Done)
        {
            var a = agent.Act(env.Observation, env.Position, false);
            double r = env.Act(a);
            rewards.Add(r);
            pos.Add(env.Position);
            equity *= 1 + r;
            eq.Add(equity);
        }
        return new EpisodeResult(rewards.Sum(), rewards.ToArray(), pos.ToArray(), eq.ToArray());
    }
}

/// <summary>
/// Tabular Q-learning. Each continuous feature is discretised into quantile bins learnt from the
/// training data; the table is indexed by (bins…, position).
/// </summary>
public sealed class QLearningAgent : ITradingAgent
{
    private readonly int _bins;
    private readonly Dictionary<long, double[]> _q = new();
    private double[][] _edges = Array.Empty<double[]>();
    private readonly FastRandom _rng;

    public QLearningAgent(int binsPerFeature = 5, double learningRate = 0.1, double discount = 0.95, double epsilon = 0.1, ulong seed = 1)
    {
        _bins = binsPerFeature;
        LearningRate = learningRate;
        Discount = discount;
        Epsilon = epsilon;
        _rng = new FastRandom(seed);
    }

    public double LearningRate { get; }
    public double Discount { get; }
    public double Epsilon { get; set; }
    public int StatesVisited => _q.Count;

    /// <summary>Learns bin edges from the environment's features.</summary>
    public void Discretize(Matrix features)
    {
        _edges = new double[features.Columns][];
        for (int j = 0; j < features.Columns; j++)
        {
            var c = features.Column(j);
            Array.Sort(c);
            _edges[j] = Enumerable.Range(1, _bins - 1).Select(k => Stats.QuantileSorted(c, (double)k / _bins)).ToArray();
        }
    }

    private long Key(ReadOnlySpan<double> obs, int position)
    {
        long key = position + 1;
        for (int j = 0; j < obs.Length; j++)
        {
            int b = 0;
            var e = _edges[j];
            while (b < e.Length && obs[j] > e[b]) b++;
            key = key * (_bins + 1) + b;
        }
        return key;
    }

    private double[] Row(long key)
    {
        if (!_q.TryGetValue(key, out var v)) { v = new double[3]; _q[key] = v; }
        return v;
    }

    public TradingAction Act(ReadOnlySpan<double> observation, int position, bool explore)
    {
        if (explore && _rng.NextDouble() < Epsilon) return (TradingAction)_rng.NextInt(3);
        var q = Row(Key(observation, position));
        int best = 0;
        for (int a = 1; a < 3; a++) if (q[a] > q[best]) best = a;
        return (TradingAction)best;
    }

    public double[] Train(TradingEnvironment env, int episodes)
    {
        if (_edges.Length == 0) throw new InvalidOperationException("Call Discretize first.");
        var totals = new double[episodes];
        for (int ep = 0; ep < episodes; ep++)
        {
            env.Reset();
            while (!env.Done)
            {
                long s = Key(env.Observation, env.Position);
                var a = Act(env.Observation, env.Position, true);
                double r = env.Act(a);
                totals[ep] += r;
                double next = 0;
                if (!env.Done) next = Row(Key(env.Observation, env.Position)).Max();
                var q = Row(s);
                q[(int)a] += LearningRate * (r + Discount * next - q[(int)a]);
            }
        }
        return totals;
    }
}

/// <summary>
/// Semi-gradient Q-learning with a linear function approximator per action:
/// Q(s, a) = w_aᵀ φ(s), with φ = [1, features…, position].
/// </summary>
public sealed class LinearQAgent : ITradingAgent
{
    private double[][] _w = Array.Empty<double[]>();
    private readonly FastRandom _rng;

    public LinearQAgent(double learningRate = 0.01, double discount = 0.9, double epsilon = 0.1, ulong seed = 2)
    {
        LearningRate = learningRate;
        Discount = discount;
        Epsilon = epsilon;
        _rng = new FastRandom(seed);
    }

    public double LearningRate { get; }
    public double Discount { get; }
    public double Epsilon { get; set; }
    /// <summary>Weights per action (Short, Flat, Long).</summary>
    public double[][] Weights => _w;

    private double[] Phi(ReadOnlySpan<double> obs, int position)
    {
        var f = new double[obs.Length + 2];
        f[0] = 1;
        obs.CopyTo(f.AsSpan(1));
        f[^1] = position;
        return f;
    }

    private double Q(double[] phi, int a) => Matrix.Dot(_w[a], phi);

    public TradingAction Act(ReadOnlySpan<double> observation, int position, bool explore)
    {
        if (_w.Length == 0)
        {
            int len = observation.Length + 2;
            _w = new[] { new double[len], new double[len], new double[len] };
        }
        if (explore && _rng.NextDouble() < Epsilon) return (TradingAction)_rng.NextInt(3);
        var phi = Phi(observation, position);
        int best = 0;
        for (int a = 1; a < 3; a++) if (Q(phi, a) > Q(phi, best)) best = a;
        return (TradingAction)best;
    }

    public double[] Train(TradingEnvironment env, int episodes)
    {
        var totals = new double[episodes];
        for (int ep = 0; ep < episodes; ep++)
        {
            env.Reset();
            while (!env.Done)
            {
                var phi = Phi(env.Observation, env.Position);
                var a = (int)Act(env.Observation, env.Position, true);
                double r = env.Act((TradingAction)a);
                totals[ep] += r;
                double target = r;
                if (!env.Done)
                {
                    var phiNext = Phi(env.Observation, env.Position);
                    target += Discount * Enumerable.Range(0, 3).Max(b => Q(phiNext, b));
                }
                double err = Math.Clamp(target - Q(phi, a), -1, 1);
                Matrix.Axpy(LearningRate * err, phi, _w[a]);
            }
        }
        return totals;
    }
}
