using Finance.Mathematics;
using RLNet.Agents;
using RLNet.Environments;
using RLNet.Neural;
using RLNet.Spaces;
using RLNet.Training;

namespace Finance.ML;

/// <summary>
/// The market simulation shared by the portfolio environments: a window of past returns as the state, target weights
/// as the decision, log return net of proportional costs on turnover as the reward, and weights that drift with
/// prices between decisions.
/// </summary>
internal sealed class PortfolioSimulator
{
    private readonly Matrix _returns;
    private readonly int _lookback, _episodeLength;
    private readonly bool _randomStart;
    private int _start, _t;

    public PortfolioSimulator(Matrix returns, int lookback, double transactionCost, int episodeLength, double rewardScale, bool includeCash)
    {
        if (lookback < 1 || returns.Rows <= lookback + 1) throw new ArgumentException("Need more rows of returns than the lookback.");
        _returns = returns;
        _lookback = lookback;
        Assets = returns.Columns;
        IncludeCash = includeCash;
        Weights = new double[Holdings];
        TransactionCost = transactionCost;
        RewardScale = rewardScale;
        int available = returns.Rows - lookback;
        _episodeLength = episodeLength > 0 ? Math.Min(episodeLength, available) : available;
        _randomStart = episodeLength > 0 && episodeLength < available;
    }

    public int Assets { get; }
    public bool IncludeCash { get; }
    /// <summary>Assets plus the cash sleeve when present.</summary>
    public int Holdings => Assets + (IncludeCash ? 1 : 0);
    public double TransactionCost { get; }
    public double RewardScale { get; }
    public int EpisodeLength => _episodeLength;
    public int ObservationSize => _lookback * Assets + Holdings;
    /// <summary>Current (drifted) weights.</summary>
    public double[] Weights { get; }
    public double LastPortfolioReturn { get; private set; }
    public double LastTurnover { get; private set; }
    public int CurrentRow => _start + _lookback + _t;

    public void Reset(Func<int, int> nextInt)
    {
        _start = _randomStart ? nextInt(_returns.Rows - _lookback - _episodeLength + 1) : 0;
        _t = 0;
        Array.Clear(Weights);
        if (IncludeCash) Weights[^1] = 1; else Array.Fill(Weights, 1.0 / Assets);
    }

    public void WriteObservation(Span<float> destination)
    {
        int k = 0, end = _start + _lookback + _t;
        for (int r = end - _lookback; r < end; r++)
            for (int j = 0; j < Assets; j++) destination[k++] = (float)(_returns[r, j] * 100);   // percent: a better scale for a network
        for (int j = 0; j < Holdings; j++) destination[k++] = (float)Weights[j];
    }

    /// <summary>Rebalances to <paramref name="target"/>, earns one period, and returns (reward, done).</summary>
    public (double Reward, bool Done) Step(ReadOnlySpan<double> target)
    {
        int row = CurrentRow;
        double turnover = 0, gross = 0;
        for (int j = 0; j < Holdings; j++) turnover += Math.Abs(target[j] - Weights[j]);
        for (int j = 0; j < Assets; j++) gross += target[j] * _returns[row, j];
        double net = (1 + gross) * (1 - TransactionCost * turnover) - 1;
        // Weights drift with the period's returns before the next decision.
        double total = 1 + gross;
        for (int j = 0; j < Holdings; j++)
        {
            double r = j < Assets ? _returns[row, j] : 0;
            Weights[j] = total > 0 ? target[j] * (1 + r) / total : target[j];
        }
        LastPortfolioReturn = net;
        LastTurnover = turnover;
        _t++;
        return (Math.Log(Math.Max(1 + net, 1e-9)) * RewardScale, _t >= _episodeLength);
    }
}

/// <summary>
/// Continuous portfolio-allocation environment for RLNet's SAC and TD3: the action is one score in [-1, 1] per holding
/// (assets, plus cash when enabled), turned into long-only, fully invested weights by a softmax with temperature
/// <see cref="Temperature"/>. Observation = the last <c>lookback</c> returns of every asset (in percent) followed by the
/// current drifted weights. Reward = scaled log return net of proportional costs on turnover.
/// </summary>
public sealed class PortfolioAllocationEnvironment : ContinuousEnvironmentBase
{
    private readonly PortfolioSimulator _sim;
    private readonly double[] _target;

    /// <param name="returns">Simple returns, one row per period, one column per asset.</param>
    /// <param name="lookback">Periods of history in the observation.</param>
    /// <param name="transactionCost">Proportional cost per unit of turnover.</param>
    /// <param name="episodeLength">Steps per episode from a random start; 0 = the whole series from the start.</param>
    /// <param name="rewardScale">Multiplier on the log return (period returns are small for a network).</param>
    /// <param name="includeCash">Add a zero-return cash sleeve the agent can hide in.</param>
    public PortfolioAllocationEnvironment(Matrix returns, int lookback = 20, double transactionCost = 0.001, int episodeLength = 0,
        double rewardScale = 100, bool includeCash = true)
        : this(new PortfolioSimulator(returns, lookback, transactionCost, episodeLength, rewardScale, includeCash)) { }

    private PortfolioAllocationEnvironment(PortfolioSimulator sim)
        : base(BoxSpace.Unbounded(sim.ObservationSize), BoxSpace.Uniform(sim.Holdings, -1, 1), sim.EpisodeLength)
    {
        _sim = sim;
        _target = new double[sim.Holdings];
    }

    public override string Name => "Finance.Net Portfolio Allocation";

    /// <summary>Softmax temperature applied to the action scores (higher = more concentrated weights).</summary>
    public double Temperature { get; init; } = 3;

    public int Holdings => _sim.Holdings;
    public IReadOnlyList<double> Weights => _sim.Weights;
    public double LastPortfolioReturn => _sim.LastPortfolioReturn;
    public double LastTurnover => _sim.LastTurnover;
    public int CurrentRow => _sim.CurrentRow;

    /// <summary>Softmax of <paramref name="scores"/> × <paramref name="temperature"/>: long-only weights summing to 1.</summary>
    public static double[] ToWeights(ReadOnlySpan<float> scores, double temperature)
    {
        var w = new double[scores.Length];
        double max = double.MinValue, sum = 0;
        for (int j = 0; j < w.Length; j++) max = Math.Max(max, temperature * scores[j]);
        for (int j = 0; j < w.Length; j++) sum += w[j] = Math.Exp(temperature * scores[j] - max);
        for (int j = 0; j < w.Length; j++) w[j] /= sum;
        return w;
    }

    protected override void OnReset() => _sim.Reset(n => Random.NextInt(n));

    protected override void WriteObservation(Span<float> destination) => _sim.WriteObservation(destination);

    protected override StepResult OnStep(ReadOnlySpan<float> action)
    {
        var w = ToWeights(action, Temperature);
        Array.Copy(w, _target, w.Length);
        var (reward, done) = _sim.Step(_target);
        return Advance((float)reward, done);
    }
}

/// <summary>
/// Discrete counterpart for PPO (RLNet's PPO is discrete): each action selects one target portfolio — all-in one asset,
/// the equal-weight portfolio, or (when enabled) all cash. Same observation and reward as
/// <see cref="PortfolioAllocationEnvironment"/>.
/// </summary>
public sealed class PortfolioTemplateEnvironment : DiscreteEnvironmentBase
{
    private readonly PortfolioSimulator _sim;
    private readonly double[][] _templates;

    public PortfolioTemplateEnvironment(Matrix returns, int lookback = 20, double transactionCost = 0.001, int episodeLength = 0,
        double rewardScale = 100, bool includeCash = true)
        : this(new PortfolioSimulator(returns, lookback, transactionCost, episodeLength, rewardScale, includeCash)) { }

    private PortfolioTemplateEnvironment(PortfolioSimulator sim)
        : base(BoxSpace.Unbounded(sim.ObservationSize), new DiscreteSpace(Templates(sim).Length), sim.EpisodeLength)
    {
        _sim = sim;
        _templates = Templates(sim);
    }

    private static double[][] Templates(PortfolioSimulator sim)
    {
        var list = new List<double[]>();
        for (int j = 0; j < sim.Assets; j++)
        {
            var w = new double[sim.Holdings];
            w[j] = 1;
            list.Add(w);
        }
        var eq = new double[sim.Holdings];
        for (int j = 0; j < sim.Assets; j++) eq[j] = 1.0 / sim.Assets;
        list.Add(eq);
        if (sim.IncludeCash)
        {
            var cash = new double[sim.Holdings];
            cash[^1] = 1;
            list.Add(cash);
        }
        return list.ToArray();
    }

    public override string Name => "Finance.Net Portfolio Templates";

    /// <summary>Target weights of each action: one per asset, then equal weight, then cash.</summary>
    public IReadOnlyList<double[]> TargetPortfolios => _templates;
    public IReadOnlyList<double> Weights => _sim.Weights;
    public double LastPortfolioReturn => _sim.LastPortfolioReturn;

    protected override void OnReset() => _sim.Reset(n => Random.NextInt(n));

    protected override void WriteObservation(Span<float> destination) => _sim.WriteObservation(destination);

    protected override StepResult OnStep(int action)
    {
        var (reward, done) = _sim.Step(_templates[action]);
        return Advance((float)reward, done);
    }
}

/// <summary>RL algorithm behind a <see cref="PortfolioRlAgent"/>.</summary>
public enum PortfolioRlAlgorithm { Sac, Td3, Ppo }

/// <summary>Out-of-sample run of a trained portfolio agent.</summary>
/// <param name="Weights">Target weights chosen at each step (rows = steps, columns = holdings).</param>
/// <param name="Returns">Net portfolio return per step.</param>
/// <param name="Equity">Wealth path starting at 1.</param>
/// <param name="AverageTurnover">Mean turnover per rebalance.</param>
public sealed record PortfolioRlRun(double[][] Weights, double[] Returns, double[] Equity, double AverageTurnover)
{
    public double TotalReturn => Equity[^1] - 1;

    /// <summary>Annualised Sharpe ratio of <see cref="Returns"/>.</summary>
    public double Sharpe(double periodsPerYear = 252)
    {
        double m = Returns.Average(), sd = Math.Sqrt(Returns.Sum(r => (r - m) * (r - m)) / Math.Max(1, Returns.Length - 1));
        return sd > 0 ? m / sd * Math.Sqrt(periodsPerYear) : 0;
    }
}

/// <summary>
/// A deep-RL portfolio allocator on Gravicode.RLNet: Soft Actor-Critic or TD3 choosing continuous weights, or PPO choosing
/// among target portfolios. With <c>useGpu</c> the networks run on RLNet.Gpu's ILGPU backend (CUDA, OpenCL or CPU
/// fallback). The backend only pays off for wide networks and large batches, so it is off by default.
/// </summary>
public sealed class PortfolioRlAgent : IDisposable
{
    private readonly int _lookback;
    private readonly int[] _hidden;
    private readonly int? _seed;
    private IContinuousAgent? _continuous;
    private IDiscreteAgent? _discrete;
    private double[][]? _templates;
    private double _temperature = 3;

    public PortfolioRlAgent(PortfolioRlAlgorithm algorithm = PortfolioRlAlgorithm.Sac, int lookback = 20, int[]? hiddenSizes = null,
        int? seed = 7, bool useGpu = false)
    {
        Algorithm = algorithm;
        _lookback = lookback;
        _hidden = hiddenSizes ?? new[] { 64, 64 };
        _seed = seed;
        Backend = useGpu ? RLNet.Gpu.GpuComputeBackend.TryCreate(preferCuda: true) : CpuComputeBackend.Instance;
    }

    public PortfolioRlAlgorithm Algorithm { get; }

    /// <summary>Device the networks run on.</summary>
    public IComputeBackend Backend { get; }

    public double TransactionCost { get; init; } = 0.001;
    public bool IncludeCash { get; init; } = true;
    /// <summary>Steps per training episode (random start); 0 = whole series.</summary>
    public int EpisodeLength { get; init; } = 250;
    public SacOptions? SacOptions { get; init; }
    public Td3Options? Td3Options { get; init; }
    public PpoOptions? PpoOptions { get; init; }
    public TrainingReport? LastReport { get; private set; }

    /// <summary>Trains on a returns matrix (rows = periods, columns = assets) for <paramref name="steps"/> environment steps.</summary>
    public TrainingReport Train(Matrix returns, int steps = 20_000)
    {
        var options = new TrainingOptions { MaxSteps = steps, Seed = _seed };
        if (Algorithm == PortfolioRlAlgorithm.Ppo)
        {
            var env = new PortfolioTemplateEnvironment(returns, _lookback, TransactionCost, EpisodeLength, includeCash: IncludeCash);
            _templates = env.TargetPortfolios.ToArray();
            _discrete ??= new PpoAgent(env.ObservationSpace, (DiscreteSpace)env.ActionSpace, PpoOptions ?? new PpoOptions
            {
                HiddenSizes = _hidden, LearningRate = 3e-4f, Gamma = 0.95f, RolloutLength = 1024, MinibatchSize = 64, Epochs = 4, EntropyCoefficient = 0.01f,
            }, _seed, Backend);
            LastReport = Trainer.Train(env, _discrete, options);
        }
        else
        {
            var env = new PortfolioAllocationEnvironment(returns, _lookback, TransactionCost, EpisodeLength, includeCash: IncludeCash);
            _temperature = env.Temperature;
            var box = env.ActionSpace;
            _continuous ??= Algorithm == PortfolioRlAlgorithm.Sac
                ? new SacAgent(env.ObservationSpace, box, SacOptions ?? new SacOptions
                {
                    HiddenSizes = _hidden, LearningRate = 3e-4f, Gamma = 0.95f, BatchSize = 128, LearningStarts = 1000, AutoTuneTemperature = true,
                }, null, _seed, Backend)
                : new Td3Agent(env.ObservationSpace, box, Td3Options ?? new Td3Options { HiddenSizes = _hidden }, null, _seed, Backend);
            LastReport = Trainer.Train(env, _continuous, options);
        }
        return LastReport;
    }

    /// <summary>Runs the trained policy deterministically over <paramref name="returns"/> (typically out of sample).</summary>
    public PortfolioRlRun Evaluate(Matrix returns)
    {
        var sim = new PortfolioSimulator(returns, _lookback, TransactionCost, 0, 1, IncludeCash);
        sim.Reset(_ => 0);
        var obs = new float[sim.ObservationSize];
        var weights = new List<double[]>();
        var rets = new List<double>();
        double turnover = 0;
        bool done = false;
        while (!done)
        {
            sim.WriteObservation(obs);
            var w = Decide(obs, sim.Holdings);
            (_, done) = sim.Step(w);
            weights.Add(w);
            rets.Add(sim.LastPortfolioReturn);
            turnover += sim.LastTurnover;
        }
        var equity = new double[rets.Count];
        double v = 1;
        for (int i = 0; i < rets.Count; i++) equity[i] = v *= 1 + rets[i];
        return new PortfolioRlRun(weights.ToArray(), rets.ToArray(), equity, turnover / Math.Max(1, rets.Count));
    }

    private double[] Decide(float[] obs, int holdings)
    {
        if (Algorithm == PortfolioRlAlgorithm.Ppo)
        {
            if (_discrete is null || _templates is null) throw new InvalidOperationException("Train the agent first.");
            return (double[])_templates[_discrete.SelectAction(obs, deterministic: true)].Clone();
        }
        if (_continuous is null) throw new InvalidOperationException("Train the agent first.");
        var a = new float[holdings];
        _continuous.SelectAction(obs, a, deterministic: true);
        return PortfolioAllocationEnvironment.ToWeights(a, _temperature);
    }

    public void Dispose()
    {
        if (Backend is IDisposable d && !ReferenceEquals(Backend, CpuComputeBackend.Instance)) d.Dispose();
    }
}
