using System.Diagnostics;

namespace Finance.GPU;

/// <summary>A GPU Monte Carlo estimate.</summary>
/// <param name="Price">Discounted mean payoff.</param>
/// <param name="StdError">Standard error of <paramref name="Price"/>.</param>
/// <param name="Paths">Antithetic path pairs simulated.</param>
/// <param name="Device">Device that ran the kernel.</param>
/// <param name="Elapsed">Wall-clock time including transfers.</param>
public sealed record GpuMonteCarloResult(double Price, double StdError, long Paths, string Device, TimeSpan Elapsed)
{
    /// <summary>95% confidence interval half-width.</summary>
    public double HalfWidth95 => 1.959963984540054 * StdError;
}

/// <summary>Outputs of a GPU batch Black-Scholes run.</summary>
public sealed record GpuGreeksBatch(double[] Price, double[] Delta, double[] Gamma, double[] Vega, double[] Theta, double[] Rho, string Device, TimeSpan Elapsed);

/// <summary>
/// Option pricing on a <see cref="GpuContext"/>: Monte Carlo European and arithmetic-Asian options
/// (antithetic, counter-based RNG, so results are reproducible for a seed) and batch Black-Scholes.
/// </summary>
public sealed class GpuOptionPricer
{
    private readonly GpuContext _ctx;

    public GpuOptionPricer(GpuContext context) => _ctx = context;

    /// <summary>Monte Carlo price of a European option with <paramref name="paths"/> antithetic pairs.</summary>
    public GpuMonteCarloResult MonteCarloEuropean(double spot, double strike, double time, double rate, double dividend, double volatility,
        bool isCall, long paths = 1_000_000, ulong seed = 42)
    {
        var args = Args(spot, strike, time, rate, dividend, volatility, isCall, paths, seed, 1);
        return RunMc(asian: false, args);
    }

    /// <summary>
    /// Monte Carlo price of an arithmetic-average Asian option with <paramref name="steps"/> equally spaced
    /// fixings (the average excludes the initial spot).
    /// </summary>
    public GpuMonteCarloResult MonteCarloAsian(double spot, double strike, double time, double rate, double dividend, double volatility,
        bool isCall, int steps = 252, long paths = 200_000, ulong seed = 42)
    {
        if (steps <= 0) throw new ArgumentOutOfRangeException(nameof(steps));
        var args = Args(spot, strike, time, rate, dividend, volatility, isCall, paths, seed, steps);
        return RunMc(asian: true, args);
    }

    /// <summary>
    /// Host execution of exactly the same European computation (same threads split, same RNG
    /// counters, same summation order). Used to validate the GPU and as a baseline.
    /// </summary>
    public GpuMonteCarloResult MonteCarloEuropeanReference(double spot, double strike, double time, double rate, double dividend, double volatility,
        bool isCall, long paths = 1_000_000, ulong seed = 42)
    {
        var args = Args(spot, strike, time, rate, dividend, volatility, isCall, paths, seed, 1);
        var sw = Stopwatch.StartNew();
        int threads = (int)((paths + args.PathsPerThread - 1) / args.PathsPerThread);
        var s1 = new double[threads];
        var s2 = new double[threads];
        Parallel.For(0, threads, t =>
        {
            GpuKernels.EuropeanBody(t, args, out double a, out double b);
            s1[t] = a;
            s2[t] = b;
        });
        return Finish(args, s1, s2, "host reference", sw.Elapsed);
    }

    /// <summary>Batch Black-Scholes-Merton price and greeks (vega per 1.00 vol, theta per year, rho per 1.00 rate).</summary>
    public GpuGreeksBatch BlackScholesBatch(double[] spot, double[] strike, double[] time, double[] rate, double[] dividend, double[] volatility, bool[] isCall)
    {
        int n = spot.Length;
        if (strike.Length != n || time.Length != n || rate.Length != n || dividend.Length != n || volatility.Length != n || isCall.Length != n)
            throw new ArgumentException("All input arrays must have the same length.");
        var sw = Stopwatch.StartNew();
        var packed = new double[7 * n];
        spot.CopyTo(packed, 0);
        strike.CopyTo(packed, n);
        time.CopyTo(packed, 2 * n);
        rate.CopyTo(packed, 3 * n);
        dividend.CopyTo(packed, 4 * n);
        volatility.CopyTo(packed, 5 * n);
        for (int i = 0; i < n; i++) packed[6 * n + i] = isCall[i] ? 1 : 0;

        var result = new double[6 * n];
        if (n > 0) _ctx.Run(e => { e.BlackScholes(packed, n, result); return 0; });
        return new GpuGreeksBatch(result[..n], result[n..(2 * n)], result[(2 * n)..(3 * n)], result[(3 * n)..(4 * n)],
            result[(4 * n)..(5 * n)], result[(5 * n)..], _ctx.Device.Name, sw.Elapsed);
    }

    private McArgs Args(double spot, double strike, double time, double rate, double dividend, double vol, bool call, long paths, ulong seed, int steps)
    {
        if (!(spot > 0) || !(strike > 0) || !(time > 0) || !(vol >= 0)) throw new ArgumentException("Spot, strike and time must be positive and volatility non-negative.");
        if (paths < 2) throw new ArgumentOutOfRangeException(nameof(paths));
        long threads = Math.Min(paths, _ctx.McThreads);
        return new McArgs
        {
            Spot = spot, Strike = strike, Time = time, Rate = rate, Dividend = dividend, Vol = vol,
            IsCall = call ? 1 : 0, Steps = steps, TotalPaths = paths, Seed = seed,
            PathsPerThread = (paths + threads - 1) / threads,
        };
    }

    private GpuMonteCarloResult RunMc(bool asian, McArgs args)
    {
        var sw = Stopwatch.StartNew();
        int threads = (int)((args.TotalPaths + args.PathsPerThread - 1) / args.PathsPerThread);
        var s1 = new double[threads];
        var s2 = new double[threads];
        _ctx.Run(e => { e.MonteCarlo(asian, args, threads, s1, s2); return 0; });
        return Finish(args, s1, s2, _ctx.Device.Name, sw.Elapsed);
    }

    private static GpuMonteCarloResult Finish(McArgs a, double[] s1, double[] s2, string device, TimeSpan elapsed)
    {
        double sum = 0, sumSq = 0;
        for (int i = 0; i < s1.Length; i++) { sum += s1[i]; sumSq += s2[i]; }
        double n = a.TotalPaths, mean = sum / n;
        double var = Math.Max(0, (sumSq - n * mean * mean) / (n - 1));
        double disc = Math.Exp(-a.Rate * a.Time);
        return new GpuMonteCarloResult(disc * mean, disc * Math.Sqrt(var / n), a.TotalPaths, device, elapsed);
    }
}
