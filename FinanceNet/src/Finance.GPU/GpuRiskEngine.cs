using System.Diagnostics;
using Finance.Mathematics;

namespace Finance.GPU;

/// <summary>Monte Carlo portfolio VaR result (losses as positive fractions of portfolio value).</summary>
/// <param name="VaR">Value at risk at the requested confidence.</param>
/// <param name="CVaR">Conditional VaR / expected shortfall beyond <paramref name="VaR"/>.</param>
/// <param name="MeanReturn">Mean simulated portfolio return over the horizon.</param>
/// <param name="Scenarios">Number of scenarios simulated.</param>
/// <param name="Device">Device that ran the simulation.</param>
/// <param name="Elapsed">Wall-clock time including transfers and the host-side sort.</param>
public sealed record GpuVaRResult(double VaR, double CVaR, double MeanReturn, long Scenarios, string Device, TimeSpan Elapsed);

/// <summary>
/// Monte Carlo portfolio VaR on a <see cref="GpuContext"/>. Asset returns over the horizon are
/// log-normal with correlated shocks (Cholesky factor of the covariance computed on the host);
/// the portfolio return is the weighted sum of simple asset returns.
/// </summary>
public sealed class GpuRiskEngine
{
    private readonly GpuContext _ctx;

    public GpuRiskEngine(GpuContext context) => _ctx = context;

    /// <summary>Simulates <paramref name="scenarios"/> horizon returns on the device and reports VaR/CVaR.</summary>
    /// <param name="weights">Portfolio weights.</param>
    /// <param name="annualDrift">Annual expected log-drift per asset (mu).</param>
    /// <param name="annualCovariance">Annualised covariance matrix of log returns.</param>
    /// <param name="horizonYears">Horizon in years (e.g. 1/252 for one day, 10/252 for ten days).</param>
    /// <param name="scenarios">Number of scenarios.</param>
    /// <param name="confidence">Confidence level, e.g. 0.99.</param>
    /// <param name="seed">RNG seed.</param>
    public GpuVaRResult MonteCarloVaR(double[] weights, double[] annualDrift, Matrix annualCovariance, double horizonYears,
        int scenarios = 1_000_000, double confidence = 0.99, ulong seed = 42)
    {
        var (chol, drifts, args) = Prepare(weights, annualDrift, annualCovariance, horizonYears, scenarios, confidence, seed);
        var sw = Stopwatch.StartNew();
        var returns = new double[scenarios];
        _ctx.Run(e => { e.PortfolioReturns(chol, weights, drifts, args, scenarios, returns); return 0; });
        return Summarise(returns, confidence, _ctx.Device.Name, sw.Elapsed);
    }

    /// <summary>The identical simulation run on host threads (validation and CPU baseline).</summary>
    public static GpuVaRResult MonteCarloVaRReference(double[] weights, double[] annualDrift, Matrix annualCovariance, double horizonYears,
        int scenarios = 1_000_000, double confidence = 0.99, ulong seed = 42)
    {
        var (chol, drifts, args) = Prepare(weights, annualDrift, annualCovariance, horizonYears, scenarios, confidence, seed);
        var sw = Stopwatch.StartNew();
        var returns = new double[scenarios];
        Parallel.For(0, scenarios, s => returns[s] = GpuKernels.PortfolioReturnBody(s, chol, weights, drifts, args));
        return Summarise(returns, confidence, "host reference", sw.Elapsed);
    }

    private static (double[] Chol, double[] Drifts, VarArgs Args) Prepare(double[] weights, double[] drift, Matrix cov, double horizon,
        int scenarios, double confidence, ulong seed)
    {
        int n = weights.Length;
        if (drift.Length != n || cov.Rows != n || cov.Columns != n) throw new ArgumentException("Weights, drifts and covariance dimensions differ.");
        if (!(horizon > 0)) throw new ArgumentOutOfRangeException(nameof(horizon));
        if (scenarios < 100) throw new ArgumentOutOfRangeException(nameof(scenarios), "Use at least 100 scenarios.");
        if (!(confidence > 0 && confidence < 1)) throw new ArgumentOutOfRangeException(nameof(confidence));
        var chol = cov.Cholesky().Data;
        var drifts = new double[n];
        for (int i = 0; i < n; i++) drifts[i] = (drift[i] - 0.5 * cov[i, i]) * horizon;
        return (chol, drifts, new VarArgs { Assets = n, SqrtHorizon = Math.Sqrt(horizon), Seed = seed });
    }

    private static GpuVaRResult Summarise(double[] returns, double confidence, string device, TimeSpan kernelTime)
    {
        var sw = Stopwatch.StartNew();
        double mean = 0;
        foreach (var r in returns) mean += r;
        mean /= returns.Length;
        Array.Sort(returns);
        int n = returns.Length;
        int k = (int)Math.Clamp((long)Math.Ceiling((1 - confidence) * n - 1e-9) - 1, 0, n - 1);
        double tail = 0;
        for (int i = 0; i <= k; i++) tail += returns[i];
        return new GpuVaRResult(-returns[k], -tail / (k + 1), mean, n, device, kernelTime + sw.Elapsed);
    }
}
