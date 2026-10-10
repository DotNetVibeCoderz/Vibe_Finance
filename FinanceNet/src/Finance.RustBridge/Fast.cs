namespace Finance.RustBridge;

/// <summary>
/// Technical indicators on the Rust accelerator, falling back to managed code when it is absent.
/// Leading positions without a full window are NaN.
/// </summary>
public static class FastIndicators
{
    private static bool Native => RustAccelerator.IsAvailable;

    public static void Sma(ReadOnlySpan<double> x, int period, Span<double> output)
    {
        if (Native) NativeKernels.Sma(x, period, output); else ManagedKernels.Sma(x, period, output);
    }

    public static double[] Sma(ReadOnlySpan<double> x, int period)
    {
        var o = new double[x.Length];
        Sma(x, period, o);
        return o;
    }

    public static void Ema(ReadOnlySpan<double> x, int period, Span<double> output)
    {
        if (Native) NativeKernels.Ema(x, period, output); else ManagedKernels.Ema(x, period, output);
    }

    public static double[] Ema(ReadOnlySpan<double> x, int period)
    {
        var o = new double[x.Length];
        Ema(x, period, o);
        return o;
    }

    public static void Rsi(ReadOnlySpan<double> x, int period, Span<double> output)
    {
        if (Native) NativeKernels.Rsi(x, period, output); else ManagedKernels.Rsi(x, period, output);
    }

    public static double[] Rsi(ReadOnlySpan<double> x, int period = 14)
    {
        var o = new double[x.Length];
        Rsi(x, period, o);
        return o;
    }

    public static void RollingStd(ReadOnlySpan<double> x, int period, Span<double> output, int ddof = 1)
    {
        if (Native) NativeKernels.RollingStd(x, period, ddof, output); else ManagedKernels.RollingStd(x, period, ddof, output);
    }

    public static double[] RollingStd(ReadOnlySpan<double> x, int period, int ddof = 1)
    {
        var o = new double[x.Length];
        RollingStd(x, period, o, ddof);
        return o;
    }

    /// <summary>Bollinger bands (population standard deviation).</summary>
    public static (double[] Middle, double[] Upper, double[] Lower) Bollinger(ReadOnlySpan<double> x, int period = 20, double k = 2.0)
    {
        var m = new double[x.Length];
        var u = new double[x.Length];
        var l = new double[x.Length];
        if (Native) NativeKernels.Bollinger(x, period, k, m, u, l); else ManagedKernels.Bollinger(x, period, k, m, u, l);
        return (m, u, l);
    }

    public static double[] LogReturns(ReadOnlySpan<double> x)
    {
        var o = new double[Math.Max(0, x.Length - 1)];
        if (Native) NativeKernels.LogReturns(x, o); else ManagedKernels.LogReturns(x, o);
        return o;
    }

    public static DrawdownInfo MaxDrawdown(ReadOnlySpan<double> x)
        => Native ? NativeKernels.MaxDrawdown(x) : ManagedKernels.MaxDrawdown(x);
}

/// <summary>Risk kernels on the Rust accelerator with managed fallback.</summary>
public static class FastRisk
{
    /// <summary>Historical VaR and CVaR at <paramref name="confidence"/> (e.g. 0.99), as positive losses.</summary>
    public static TailRisk HistoricalVaR(ReadOnlySpan<double> returns, double confidence = 0.99)
        => RustAccelerator.IsAvailable ? NativeKernels.HistoricalVaR(returns, confidence) : ManagedKernels.HistoricalVaR(returns, confidence);

    /// <summary>Sample covariance matrix (row-major, cols × cols) of a row-major rows × cols buffer.</summary>
    public static double[] Covariance(ReadOnlySpan<double> data, int rows, int cols, int ddof = 1)
    {
        var o = new double[cols * cols];
        if (RustAccelerator.IsAvailable) NativeKernels.Covariance(data, rows, cols, ddof, o);
        else ManagedKernels.Covariance(data, rows, cols, ddof, o);
        return o;
    }
}

/// <summary>Option pricing and simulation on the Rust accelerator with managed fallback.</summary>
public static class FastPricing
{
    /// <summary>Batch Black-Scholes into caller buffers; empty output spans are skipped.</summary>
    public static void BlackScholesBatch(OptionBatch batch, Span<double> price, Span<double> delta = default, Span<double> gamma = default,
        Span<double> vega = default, Span<double> theta = default, Span<double> rho = default)
    {
        if (RustAccelerator.IsAvailable) NativeKernels.BlackScholesBatch(batch, price, delta, gamma, vega, theta, rho);
        else ManagedKernels.BlackScholesBatch(batch, price, delta, gamma, vega, theta, rho);
    }

    /// <summary>Batch Black-Scholes returning every greek.</summary>
    public static BlackScholesBatchResult BlackScholesBatch(OptionBatch batch)
    {
        var r = new BlackScholesBatchResult(batch.Count);
        BlackScholesBatch(batch, r.Price, r.Delta, r.Gamma, r.Vega, r.Theta, r.Rho);
        return r;
    }

    /// <summary>
    /// Monte Carlo European option. Native and managed paths use the same random streams, so
    /// they return the same estimate (to floating-point rounding) for the same seed.
    /// </summary>
    public static MonteCarloEstimate MonteCarloEuropean(EuropeanOptionSpec option, long paths = 1_000_000, ulong seed = 42, bool antithetic = true)
        => RustAccelerator.IsAvailable
            ? NativeKernels.MonteCarloEuropean(option, paths, seed, antithetic)
            : ManagedKernels.MonteCarloEuropean(option, paths, seed, antithetic);

    /// <summary>Simulates GBM price paths; returns a row-major paths × (steps + 1) array.</summary>
    public static double[] SimulateGbm(double s0, double mu, double sigma, double dt, int steps, int paths, ulong seed = 42)
    {
        var o = new double[(long)paths * (steps + 1)];
        if (RustAccelerator.IsAvailable) NativeKernels.SimulateGbm(s0, mu, sigma, dt, steps, paths, seed, o);
        else ManagedKernels.SimulateGbm(s0, mu, sigma, dt, steps, paths, seed, o);
        return o;
    }
}
