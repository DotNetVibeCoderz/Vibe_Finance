using Finance.Core.Market;

namespace Finance.Backtesting;

/// <summary>One evaluated parameter set.</summary>
public sealed record SweepResult<TParams>(TParams Parameters, double Score, BacktestResult Result);

/// <summary>Grid search over strategy parameters.</summary>
public static class ParameterSweep
{
    /// <summary>
    /// Backtests every parameter set (in parallel by default) and returns the results best-first by
    /// <paramref name="objective"/> (default: Sharpe ratio).
    /// </summary>
    public static IReadOnlyList<SweepResult<TParams>> Run<TParams>(IEnumerable<TParams> grid, Func<TParams, Strategy> factory,
        IReadOnlyDictionary<string, Bar[]> data, BacktestSettings? settings = null, Func<PerformanceStatistics, double>? objective = null, bool parallel = true)
    {
        objective ??= s => s.SharpeRatio;
        var engine = new BacktestEngine(settings);
        var items = grid.ToArray();
        var results = new SweepResult<TParams>[items.Length];
        void Eval(int i)
        {
            var r = engine.Run(factory(items[i]), data);
            double score = objective(r.Statistics);
            results[i] = new SweepResult<TParams>(items[i], double.IsNaN(score) ? double.NegativeInfinity : score, r);
        }
        if (parallel) Parallel.For(0, items.Length, Eval);
        else for (int i = 0; i < items.Length; i++) Eval(i);
        return results.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>Cartesian product of two parameter axes.</summary>
    public static IEnumerable<(T1, T2)> Grid<T1, T2>(IEnumerable<T1> a, IEnumerable<T2> b)
    {
        var bs = b.ToArray();
        foreach (var x in a)
            foreach (var y in bs) yield return (x, y);
    }

    /// <summary>Cartesian product of three parameter axes.</summary>
    public static IEnumerable<(T1, T2, T3)> Grid<T1, T2, T3>(IEnumerable<T1> a, IEnumerable<T2> b, IEnumerable<T3> c)
    {
        var bs = b.ToArray();
        var cs = c.ToArray();
        foreach (var x in a)
            foreach (var y in bs)
                foreach (var z in cs) yield return (x, y, z);
    }
}

/// <summary>One in-sample / out-of-sample window of a walk-forward run.</summary>
public sealed record WalkForwardWindow<TParams>(int Index, DateTime InSampleStart, DateTime InSampleEnd, DateTime OutOfSampleStart,
    DateTime OutOfSampleEnd, TParams BestParameters, double InSampleScore, PerformanceStatistics OutOfSample);

/// <summary>Result of walk-forward analysis.</summary>
/// <param name="Times">Timestamps of the stitched out-of-sample equity curve.</param>
/// <param name="Equity">Out-of-sample equity, chained across windows from the initial cash.</param>
public sealed record WalkForwardResult<TParams>(IReadOnlyList<WalkForwardWindow<TParams>> Windows, DateTime[] Times, double[] Equity, PerformanceStatistics Statistics);

/// <summary>
/// Walk-forward optimisation: on each rolling (or anchored) in-sample window pick the best parameters by grid
/// search, then trade them on the following out-of-sample window. Out-of-sample runs are fed the in-sample bars
/// first (with trading disabled) so indicators are warm when trading starts.
/// </summary>
public sealed class WalkForwardOptimizer<TParams>
{
    private readonly TParams[] _grid;
    private readonly Func<TParams, Strategy> _factory;

    public WalkForwardOptimizer(IEnumerable<TParams> grid, Func<TParams, Strategy> factory, BacktestSettings? settings = null)
    {
        _grid = grid.ToArray();
        if (_grid.Length == 0) throw new ArgumentException("Parameter grid is empty.");
        _factory = factory;
        Settings = settings ?? new BacktestSettings();
    }

    public BacktestSettings Settings { get; }

    /// <summary>Score used to choose parameters (default: Sharpe ratio).</summary>
    public Func<PerformanceStatistics, double> Objective { get; init; } = s => s.SharpeRatio;

    /// <summary>Runs the analysis over the merged timeline of <paramref name="data"/>.</summary>
    /// <param name="inSampleBars">Bars per in-sample window.</param>
    /// <param name="outOfSampleBars">Bars per out-of-sample window (also the step between windows).</param>
    /// <param name="anchored">Keep the in-sample start fixed at the first bar (expanding window).</param>
    public WalkForwardResult<TParams> Run(IReadOnlyDictionary<string, Bar[]> data, int inSampleBars, int outOfSampleBars, bool anchored = false)
    {
        Finance.Core.Guard.Positive(inSampleBars);
        Finance.Core.Guard.Positive(outOfSampleBars);
        var timeline = data.Values.SelectMany(b => b.Select(x => x.Time)).Distinct().Order().ToArray();
        if (timeline.Length < inSampleBars + outOfSampleBars) throw new ArgumentException("Not enough bars for one window.");

        var windows = new List<WalkForwardWindow<TParams>>();
        var times = new List<DateTime>();
        var equity = new List<double>();
        double level = Settings.InitialCash;
        for (int k = 0, start = 0; start + inSampleBars < timeline.Length; k++, start += outOfSampleBars)
        {
            int isStart = anchored ? 0 : start;
            int isEnd = start + inSampleBars - 1;
            int oosStart = isEnd + 1;
            int oosEnd = Math.Min(oosStart + outOfSampleBars - 1, timeline.Length - 1);

            var inSample = Slice(data, timeline[isStart], timeline[isEnd]);
            var sweep = ParameterSweep.Run(_grid, _factory, inSample, Settings, Objective);
            var best = sweep[0];

            var oosData = Slice(data, timeline[isStart], timeline[oosEnd]);
            var oosSettings = Settings with { StartTradingAt = timeline[oosStart] };
            var oos = new BacktestEngine(oosSettings).Run(_factory(best.Parameters), oosData);

            // Chain windows: each OOS run starts from cash, so scale it to the previous window's final level.
            for (int i = 0; i < oos.Equity.Length; i++)
            {
                double growth = oos.Equity[i] / oos.InitialCash;
                times.Add(oos.Times[i]);
                equity.Add(level * growth);
            }
            if (oos.Equity.Length > 0) level *= oos.Equity[^1] / oos.InitialCash;

            windows.Add(new WalkForwardWindow<TParams>(k, timeline[isStart], timeline[isEnd], timeline[oosStart], timeline[oosEnd],
                best.Parameters, best.Score, oos.Statistics));
            if (oosEnd == timeline.Length - 1) break;
        }
        var eq = equity.ToArray();
        return new WalkForwardResult<TParams>(windows, times.ToArray(), eq, PerformanceStatistics.Compute(eq, Settings.PeriodsPerYear, Settings.RiskFreeRate));
    }

    /// <summary>Bars of every symbol within [from, to].</summary>
    public static IReadOnlyDictionary<string, Bar[]> Slice(IReadOnlyDictionary<string, Bar[]> data, DateTime from, DateTime to)
        => data.ToDictionary(kv => kv.Key, kv => kv.Value.Where(b => b.Time >= from && b.Time <= to).ToArray(), StringComparer.Ordinal)
               .Where(kv => kv.Value.Length > 0)
               .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
}
