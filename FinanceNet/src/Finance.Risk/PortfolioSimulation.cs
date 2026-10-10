using Finance.Core;
using Finance.Mathematics;

namespace Finance.Risk;

/// <summary>Output of a Monte Carlo portfolio simulation.</summary>
public sealed class SimulationResult
{
    internal SimulationResult(double initialValue, double[] times, double[] terminal, double[][] fan, double[] fanLevels, double[][]? samplePaths)
    {
        InitialValue = initialValue;
        Times = times;
        TerminalValues = terminal;
        FanPercentiles = fanLevels;
        Fan = fan;
        SamplePaths = samplePaths ?? Array.Empty<double[]>();
    }

    public double InitialValue { get; }

    /// <summary>Time grid in years (starts at 0).</summary>
    public double[] Times { get; }

    /// <summary>Sorted terminal portfolio values.</summary>
    public double[] TerminalValues { get; }

    /// <summary>Percentile levels of <see cref="Fan"/> (e.g. 0.05, 0.25, 0.5, 0.75, 0.95).</summary>
    public double[] FanPercentiles { get; }

    /// <summary>Fan[k][t] = value at percentile <see cref="FanPercentiles"/>[k] at time index t (for fan charts).</summary>
    public double[][] Fan { get; }

    /// <summary>A handful of full paths for plotting.</summary>
    public double[][] SamplePaths { get; }

    public double MeanTerminalValue => Stats.Mean(TerminalValues);
    public double MedianTerminalValue => Stats.QuantileSorted(TerminalValues, 0.5);

    /// <summary>Terminal-value percentile.</summary>
    public double Percentile(double p) => Stats.QuantileSorted(TerminalValues, p);

    /// <summary>Probability of ending below the initial value.</summary>
    public double ProbabilityOfLoss => (double)TerminalValues.Count(v => v < InitialValue) / TerminalValues.Length;

    /// <summary>Horizon VaR/ES of the terminal return distribution.</summary>
    public VarEstimate ValueAtRisk(double confidence = 0.95)
    {
        var r = new double[TerminalValues.Length];
        for (int i = 0; i < r.Length; i++) r[i] = TerminalValues[i] / InitialValue - 1;
        return Risk.ValueAtRisk.Historical(r, confidence) with { Method = VarMethod.MonteCarlo };
    }
}

/// <summary>
/// Monte Carlo simulation of a constantly rebalanced multi-asset portfolio under correlated GBM.
/// </summary>
public static class PortfolioSimulation
{
    /// <summary>Runs the simulation.</summary>
    /// <param name="initialValue">Starting portfolio value.</param>
    /// <param name="weights">Asset weights (rebalanced every step).</param>
    /// <param name="annualDrift">Expected annual (arithmetic) return per asset.</param>
    /// <param name="annualCovariance">Annual covariance matrix of log returns.</param>
    /// <param name="years">Horizon in years.</param>
    /// <param name="stepsPerYear">Time steps per year.</param>
    /// <param name="paths">Number of paths.</param>
    /// <param name="seed">RNG seed.</param>
    /// <param name="samplePaths">How many full paths to keep for plotting.</param>
    public static SimulationResult Run(double initialValue, double[] weights, double[] annualDrift, Matrix annualCovariance,
        double years = 1, int stepsPerYear = 252, int paths = 10_000, ulong seed = 42, int samplePaths = 20)
    {
        int n = weights.Length;
        Guard.SameLength(n, annualDrift.Length, "weights and drifts");
        int steps = Math.Max(1, (int)Math.Round(years * stepsPerYear));
        double dt = years / steps, sqdt = Math.Sqrt(dt);
        var l = annualCovariance.Cholesky();
        var driftStep = new double[n];
        for (int i = 0; i < n; i++) driftStep[i] = (annualDrift[i] - 0.5 * annualCovariance[i, i]) * dt;

        // values[t][p]: storing per step lets percentiles be computed per time without a second pass.
        var values = new double[steps + 1][];
        for (int t = 0; t <= steps; t++) values[t] = new double[paths];
        Array.Fill(values[0], initialValue);

        const int block = 1024;
        int blocks = (paths + block - 1) / block;
        var root = new FastRandom(seed);
        var seeds = new ulong[blocks];
        for (int b = 0; b < blocks; b++) seeds[b] = root.NextUInt64();
        Parallel.For(0, blocks, b =>
        {
            var rng = new FastRandom(seeds[b]);
            var z = new double[n];
            int end = Math.Min(paths, (b + 1) * block);
            for (int p = b * block; p < end; p++)
            {
                double v = initialValue;
                for (int t = 1; t <= steps; t++)
                {
                    for (int k = 0; k < n; k++) z[k] = rng.NextGaussian();
                    double growth = 0;
                    for (int i = 0; i < n; i++)
                    {
                        double e = 0;
                        for (int k = 0; k <= i; k++) e += l[i, k] * z[k];
                        growth += weights[i] * (Math.Exp(driftStep[i] + e * sqdt) - 1);
                    }
                    v *= 1 + growth;
                    values[t][p] = v;
                }
            }
        });

        double[] levels = { 0.05, 0.25, 0.5, 0.75, 0.95 };
        var fan = new double[levels.Length][];
        for (int k = 0; k < levels.Length; k++) fan[k] = new double[steps + 1];
        var buffer = new double[paths];
        for (int t = 0; t <= steps; t++)
        {
            values[t].CopyTo(buffer, 0);
            Array.Sort(buffer);
            for (int k = 0; k < levels.Length; k++) fan[k][t] = Stats.QuantileSorted(buffer, levels[k]);
        }
        int keep = Math.Min(samplePaths, paths);
        var sample = new double[keep][];
        for (int s = 0; s < keep; s++)
        {
            sample[s] = new double[steps + 1];
            for (int t = 0; t <= steps; t++) sample[s][t] = values[t][s];
        }
        var terminal = (double[])values[steps].Clone();
        Array.Sort(terminal);
        var times = new double[steps + 1];
        for (int t = 0; t <= steps; t++) times[t] = t * dt;
        return new SimulationResult(initialValue, times, terminal, fan, levels, sample);
    }
}
