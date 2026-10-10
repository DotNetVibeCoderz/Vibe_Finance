using Finance.Mathematics;
using RLNet.Agents;
using RLNet.Environments;
using RLNet.Spaces;
using RLNet.Training;

namespace Finance.ML;

/// <summary>
/// The Finance.Net trading environment exposed as a Gravicode.RLNet discrete environment, so any
/// RLNet agent (DQN, PPO, A2C, Q-table) and its <see cref="Trainer"/> can learn on real or synthetic features.
/// Observation = feature row (as float) followed by the current position; actions 0/1/2 = short/flat/long.
/// Each episode is a window of <c>episodeLength</c> steps starting at a random row (or the whole series).
/// </summary>
public sealed class RlNetTradingEnvironment : DiscreteEnvironmentBase
{
    private readonly Matrix _features;
    private readonly double[] _returns;
    private readonly int _episodeLength;
    private readonly bool _randomStart;
    private int _start, _t, _position;

    /// <param name="features">State features, one row per step (standardise them first).</param>
    /// <param name="nextReturns">Simple return from step t to t+1.</param>
    /// <param name="transactionCost">Proportional cost per unit of position change.</param>
    /// <param name="episodeLength">Steps per episode; 0 = the full series from row 0.</param>
    /// <param name="rewardScale">Multiplier on rewards (daily returns are tiny for neural nets).</param>
    public RlNetTradingEnvironment(Matrix features, double[] nextReturns, double transactionCost = 0.0005, int episodeLength = 0, double rewardScale = 100)
        : base(BoxSpace.Unbounded(features.Columns + 1), new DiscreteSpace(3, new[] { "Short", "Flat", "Long" }),
            episodeLength > 0 ? Math.Min(episodeLength, features.Rows) : features.Rows)
    {
        if (features.Rows != nextReturns.Length) throw new ArgumentException("One return per feature row.");
        _features = features;
        _returns = nextReturns;
        TransactionCost = transactionCost;
        RewardScale = rewardScale;
        _episodeLength = episodeLength > 0 ? Math.Min(episodeLength, features.Rows) : features.Rows;
        _randomStart = episodeLength > 0 && episodeLength < features.Rows;
    }

    public override string Name => "Finance.Net Trading";
    public double TransactionCost { get; }
    public double RewardScale { get; }
    /// <summary>Current position (-1, 0, +1).</summary>
    public int Position => _position;
    /// <summary>Row index of the current step in the underlying data.</summary>
    public int CurrentRow => _start + _t;

    protected override void OnReset()
    {
        _start = _randomStart ? Random.NextInt(_features.Rows - _episodeLength + 1) : 0;
        _t = 0;
        _position = 0;
    }

    protected override void WriteObservation(Span<float> destination)
    {
        var row = _features.Row(Math.Min(_start + _t, _features.Rows - 1));
        for (int j = 0; j < row.Length; j++) destination[j] = (float)row[j];
        destination[row.Length] = _position;
    }

    protected override StepResult OnStep(int action)
    {
        int target = action - 1;
        int i = _start + _t;
        double reward = target * _returns[i] - TransactionCost * Math.Abs(target - _position);
        _position = target;
        _t++;
        bool end = _start + _t >= _features.Rows;
        return Advance((float)(reward * RewardScale), end);
    }
}

/// <summary>Which RLNet algorithm backs a <see cref="DeepTradingAgent"/>.</summary>
public enum DeepRlAlgorithm { Dqn, Ppo }

/// <summary>
/// A deep reinforcement-learning trading agent backed by Gravicode.RLNet (DQN with double-Q and
/// dueling heads, or PPO with GAE). Implements <see cref="ITradingAgent"/> so it is interchangeable with
/// the lightweight <see cref="QLearningAgent"/> and <see cref="LinearQAgent"/>.
/// </summary>
public sealed class DeepTradingAgent : ITradingAgent
{
    private readonly DeepRlAlgorithm _algorithm;
    private readonly int[] _hidden;
    private readonly int? _seed;
    private readonly int _episodeLength;
    private IDiscreteAgent? _agent;
    private float[] _obs = Array.Empty<float>();

    public DeepTradingAgent(DeepRlAlgorithm algorithm = DeepRlAlgorithm.Dqn, int[]? hiddenSizes = null, int episodeLength = 0, int? seed = 7)
    {
        _algorithm = algorithm;
        _hidden = hiddenSizes ?? new[] { 64, 64 };
        _episodeLength = episodeLength;
        _seed = seed;
    }

    /// <summary>Optional DQN settings applied when the agent is created.</summary>
    public DqnOptions? DqnOptions { get; init; }
    /// <summary>Optional PPO settings applied when the agent is created.</summary>
    public PpoOptions? PpoOptions { get; init; }
    /// <summary>The underlying RLNet agent (null until trained or <see cref="Initialize"/> is called).</summary>
    public IDiscreteAgent? Agent => _agent;
    /// <summary>The last RLNet training report.</summary>
    public TrainingReport? LastReport { get; private set; }

    /// <summary>Creates the network for an environment's spaces.</summary>
    public void Initialize(IDiscreteEnvironment env)
    {
        _agent = _algorithm switch
        {
            DeepRlAlgorithm.Dqn => new DqnAgent(env.ObservationSpace, env.ActionSpace, DqnOptions ?? new DqnOptions
            {
                HiddenSizes = _hidden,
                LearningRate = 1e-3f,
                Gamma = 0.9f,
                BatchSize = 64,
                LearningStarts = 500,
                TargetUpdateInterval = 250,
                Epsilon = Schedule.Linear(1f, 0.05f, 0.6f),
                DoubleQ = true,
                Dueling = true,
            }, seed: _seed),
            _ => new PpoAgent(env.ObservationSpace, env.ActionSpace, PpoOptions ?? new PpoOptions
            {
                HiddenSizes = _hidden,
                LearningRate = 3e-4f,
                Gamma = 0.9f,
                RolloutLength = 1024,
                MinibatchSize = 64,
                Epochs = 4,
                EntropyCoefficient = 0.01f,
            }, seed: _seed),
        };
        _obs = new float[env.ObservationSpace.FlatSize];
    }

    /// <summary>Trains on an RLNet environment with RLNet's <see cref="Trainer"/>.</summary>
    public TrainingReport Train(RlNetTradingEnvironment env, TrainingOptions options)
    {
        if (_agent is null) Initialize(env);
        LastReport = Trainer.Train(env, _agent!, options);
        return LastReport;
    }

    /// <summary>Trains for <paramref name="episodes"/> episodes; returns each episode's (scaled) return.</summary>
    public double[] Train(TradingEnvironment env, int episodes)
    {
        var rl = new RlNetTradingEnvironment(env.Features, env.NextReturns, env.TransactionCost, _episodeLength);
        var report = Train(rl, new TrainingOptions { MaxEpisodes = episodes, Seed = _seed });
        return report.Returns.Select(r => (double)r).ToArray();
    }

    public TradingAction Act(ReadOnlySpan<double> observation, int position, bool explore)
    {
        if (_agent is null) throw new InvalidOperationException("Train or Initialize the agent first.");
        for (int j = 0; j < observation.Length; j++) _obs[j] = (float)observation[j];
        _obs[observation.Length] = position;
        return (TradingAction)_agent.SelectAction(_obs, deterministic: !explore);
    }
}
