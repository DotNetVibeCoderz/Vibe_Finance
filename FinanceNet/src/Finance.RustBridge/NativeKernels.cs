using System.Runtime.InteropServices;

namespace Finance.RustBridge;

/// <summary>
/// Direct, safe wrappers over the Rust kernels. Every method throws
/// <see cref="InvalidOperationException"/> when the native library is not loaded; prefer the
/// <see cref="FastIndicators"/>, <see cref="FastRisk"/> and <see cref="FastPricing"/> facades,
/// which fall back to <see cref="ManagedKernels"/> automatically.
/// </summary>
public static unsafe class NativeKernels
{
    private static void EnsureLoaded()
    {
        if (!RustAccelerator.IsLoaded)
            throw new InvalidOperationException("finance_native is not available: " + RustAccelerator.LoadError);
    }

    private static void CheckOutput(int needed, int have)
    {
        if (have < needed) throw new ArgumentException("Output span is too short.");
    }

    public static void Sma(ReadOnlySpan<double> x, int period, Span<double> output) => Window(x, period, output, 0);
    public static void Ema(ReadOnlySpan<double> x, int period, Span<double> output) => Window(x, period, output, 1);
    public static void Rsi(ReadOnlySpan<double> x, int period, Span<double> output) => Window(x, period, output, 2);

    private static void Window(ReadOnlySpan<double> x, int period, Span<double> output, int kind)
    {
        EnsureLoaded();
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        CheckOutput(x.Length, output.Length);
        fixed (double* px = x)
        fixed (double* po = output)
        {
            int status = kind switch
            {
                0 => NativeMethods.Sma(px, (nuint)x.Length, (nuint)period, po),
                1 => NativeMethods.Ema(px, (nuint)x.Length, (nuint)period, po),
                _ => NativeMethods.Rsi(px, (nuint)x.Length, (nuint)period, po),
            };
            NativeMethods.Check(status, kind switch { 0 => "fnat_sma", 1 => "fnat_ema", _ => "fnat_rsi" });
        }
    }

    public static void RollingStd(ReadOnlySpan<double> x, int period, int ddof, Span<double> output)
    {
        EnsureLoaded();
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        if (ddof < 0 || ddof >= period) throw new ArgumentOutOfRangeException(nameof(ddof));
        CheckOutput(x.Length, output.Length);
        fixed (double* px = x)
        fixed (double* po = output)
            NativeMethods.Check(NativeMethods.RollingStd(px, (nuint)x.Length, (nuint)period, (nuint)ddof, po), "fnat_rolling_std");
    }

    public static void Bollinger(ReadOnlySpan<double> x, int period, double k, Span<double> middle, Span<double> upper, Span<double> lower)
    {
        EnsureLoaded();
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        CheckOutput(x.Length, Math.Min(middle.Length, Math.Min(upper.Length, lower.Length)));
        fixed (double* px = x)
        fixed (double* pm = middle)
        fixed (double* pu = upper)
        fixed (double* pl = lower)
            NativeMethods.Check(NativeMethods.Bollinger(px, (nuint)x.Length, (nuint)period, k, pm, pu, pl), "fnat_bollinger");
    }

    public static void LogReturns(ReadOnlySpan<double> x, Span<double> output)
    {
        EnsureLoaded();
        if (x.Length < 2) throw new ArgumentException("Need at least two prices.");
        CheckOutput(x.Length - 1, output.Length);
        fixed (double* px = x)
        fixed (double* po = output)
            NativeMethods.Check(NativeMethods.LogReturns(px, (nuint)x.Length, po), "fnat_log_returns");
    }

    public static DrawdownInfo MaxDrawdown(ReadOnlySpan<double> x)
    {
        EnsureLoaded();
        if (x.IsEmpty) throw new ArgumentException("Series is empty.");
        double depth;
        nuint peak, trough;
        fixed (double* px = x)
            NativeMethods.Check(NativeMethods.MaxDrawdown(px, (nuint)x.Length, &depth, &peak, &trough), "fnat_max_drawdown");
        return new DrawdownInfo(depth, (int)peak, (int)trough);
    }

    public static TailRisk HistoricalVaR(ReadOnlySpan<double> returns, double confidence)
    {
        EnsureLoaded();
        if (returns.IsEmpty) throw new ArgumentException("No returns.");
        if (!(confidence > 0 && confidence < 1)) throw new ArgumentOutOfRangeException(nameof(confidence));
        double var, cvar;
        fixed (double* pr = returns)
            NativeMethods.Check(NativeMethods.HistoricalVaR(pr, (nuint)returns.Length, confidence, &var, &cvar), "fnat_historical_var");
        return new TailRisk(var, cvar);
    }

    public static void Covariance(ReadOnlySpan<double> data, int rows, int cols, int ddof, Span<double> output)
    {
        EnsureLoaded();
        if (cols <= 0 || rows <= ddof) throw new ArgumentException("Not enough observations.");
        if (data.Length < rows * cols) throw new ArgumentException("Data buffer is too short.");
        CheckOutput(cols * cols, output.Length);
        fixed (double* pd = data)
        fixed (double* po = output)
            NativeMethods.Check(NativeMethods.Covariance(pd, (nuint)rows, (nuint)cols, (nuint)ddof, po), "fnat_covariance");
    }

    /// <summary>Batch Black-Scholes; any empty output span is skipped.</summary>
    public static void BlackScholesBatch(OptionBatch batch, Span<double> price, Span<double> delta = default, Span<double> gamma = default,
        Span<double> vega = default, Span<double> theta = default, Span<double> rho = default)
    {
        EnsureLoaded();
        int n = batch.Count;
        foreach (var len in new[] { price.Length, delta.Length, gamma.Length, vega.Length, theta.Length, rho.Length })
            if (len != 0 && len < n) throw new ArgumentException("Output span is too short.");
        var calls = MemoryMarshal.AsBytes(batch.IsCall);
        fixed (double* ps = batch.Spot)
        fixed (double* pk = batch.Strike)
        fixed (double* pt = batch.Time)
        fixed (double* pr = batch.Rate)
        fixed (double* pq = batch.Dividend)
        fixed (double* pv = batch.Volatility)
        fixed (byte* pc = calls)
        fixed (double* o1 = price)
        fixed (double* o2 = delta)
        fixed (double* o3 = gamma)
        fixed (double* o4 = vega)
        fixed (double* o5 = theta)
        fixed (double* o6 = rho)
        {
            // fixed on an empty span yields null, which the native side reads as "skip".
            NativeMethods.Check(NativeMethods.BlackScholesBatch(ps, pk, pt, pr, pq, pv, pc, (nuint)n, o1, o2, o3, o4, o5, o6), "fnat_black_scholes_batch");
        }
    }

    /// <summary>Monte Carlo European option on the Rust thread pool (<paramref name="threads"/> 0 = all cores).</summary>
    public static MonteCarloEstimate MonteCarloEuropean(EuropeanOptionSpec o, long paths, ulong seed = 42, bool antithetic = true, int threads = 0)
    {
        EnsureLoaded();
        o.Validate();
        if (paths < 2) throw new ArgumentOutOfRangeException(nameof(paths));
        double price, se;
        NativeMethods.Check(NativeMethods.MonteCarloEuropean(o.Spot, o.Strike, o.Time, o.Rate, o.Dividend, o.Volatility,
            o.IsCall ? (byte)1 : (byte)0, antithetic ? (byte)1 : (byte)0, (ulong)paths, seed, (uint)Math.Max(0, threads), &price, &se), "fnat_mc_european");
        return new MonteCarloEstimate(price, se, paths);
    }

    /// <summary>GBM paths into a row-major paths × (steps + 1) buffer.</summary>
    public static void SimulateGbm(double s0, double mu, double sigma, double dt, int steps, int paths, ulong seed, Span<double> output, int threads = 0)
    {
        EnsureLoaded();
        if (paths <= 0 || steps < 0) throw new ArgumentException("Invalid GBM shape.");
        CheckOutput(paths * (steps + 1), output.Length);
        fixed (double* po = output)
            NativeMethods.Check(NativeMethods.SimulateGbm(s0, mu, sigma, dt, (nuint)steps, (nuint)paths, seed, (uint)Math.Max(0, threads), po), "fnat_simulate_gbm");
    }
}
