using Finance.Core;
using Finance.Mathematics;

namespace Finance.Pricing.Options;

/// <summary>Monte Carlo simulation settings.</summary>
/// <param name="Paths">Number of samples (an antithetic pair counts as one sample).</param>
/// <param name="Steps">Monitoring steps per path (ignored for European payoffs).</param>
/// <param name="Seed">Seed; equal seeds give identical results regardless of thread count.</param>
/// <param name="Antithetic">Use antithetic variates.</param>
public sealed record MonteCarloSettings(int Paths = 100_000, int Steps = 252, ulong Seed = 42, bool Antithetic = true);

/// <summary>Monte Carlo estimate with its standard error.</summary>
public readonly record struct MonteCarloResult(double Price, double StandardError, int Paths)
{
    /// <summary>95% confidence interval.</summary>
    public (double Lower, double Upper) ConfidenceInterval95 => (Price - 1.959964 * StandardError, Price + 1.959964 * StandardError);
}

/// <summary>Barrier direction and effect.</summary>
public enum BarrierType { UpAndOut, UpAndIn, DownAndOut, DownAndIn }

/// <summary>
/// Monte Carlo pricer under Black-Scholes dynamics (exact GBM steps). Work is split into fixed blocks,
/// each with its own generator derived from the seed, so results are reproducible and parallel.
/// </summary>
public sealed class MonteCarloPricer
{
    private const int BlockSize = 4096;

    public MonteCarloPricer(double spot, double rate, double dividendYield, double volatility, MonteCarloSettings? settings = null)
    {
        Guard.Positive(spot);
        Guard.NonNegative(volatility);
        Spot = spot;
        Rate = rate;
        DividendYield = dividendYield;
        Volatility = volatility;
        Settings = settings ?? new MonteCarloSettings();
    }

    public double Spot { get; }
    public double Rate { get; }
    public double DividendYield { get; }
    public double Volatility { get; }
    public MonteCarloSettings Settings { get; }

    /// <summary>European vanilla option (terminal value sampled directly).</summary>
    public MonteCarloResult European(OptionType type, double strike, double time)
    {
        double drift = (Rate - DividendYield - 0.5 * Volatility * Volatility) * time;
        double vol = Volatility * Math.Sqrt(time);
        double sign = type == OptionType.Call ? 1 : -1;
        double disc = Math.Exp(-Rate * time);
        bool anti = Settings.Antithetic;
        var (mean, se) = Run(rng =>
        {
            double z = rng.NextGaussian();
            double v = Math.Max(sign * (Spot * Math.Exp(drift + vol * z) - strike), 0);
            if (anti) v = 0.5 * (v + Math.Max(sign * (Spot * Math.Exp(drift - vol * z) - strike), 0));
            return v;
        });
        return new MonteCarloResult(disc * mean, disc * se, Settings.Paths);
    }

    /// <summary>
    /// Generic path-dependent payoff. <paramref name="payoff"/> receives the simulated prices at
    /// t = dt, 2dt, ..., T (the spot itself is not included) and returns the undiscounted payoff.
    /// </summary>
    public MonteCarloResult PathDependent(double time, Func<ReadOnlySpan<double>, double> payoff)
    {
        int steps = Settings.Steps;
        double dt = time / steps;
        double drift = (Rate - DividendYield - 0.5 * Volatility * Volatility) * dt;
        double vol = Volatility * Math.Sqrt(dt);
        double disc = Math.Exp(-Rate * time);
        bool anti = Settings.Antithetic;
        var (mean, se) = RunPaths(steps, (rng, path, mirror, z) =>
        {
            rng.FillGaussian(z);
            Simulate(path, z, drift, vol, 1);
            double v = payoff(path);
            if (anti)
            {
                Simulate(mirror, z, drift, vol, -1);
                v = 0.5 * (v + payoff(mirror));
            }
            return v;
        });
        return new MonteCarloResult(disc * mean, disc * se, Settings.Paths);
    }

    /// <summary>
    /// Arithmetic-average Asian option with discrete averaging over the <see cref="MonteCarloSettings.Steps"/>
    /// monitoring dates, using the geometric-average Asian (closed form) as a control variate.
    /// </summary>
    public MonteCarloResult AsianArithmetic(OptionType type, double strike, double time, bool useControlVariate = true)
    {
        int n = Settings.Steps;
        double dt = time / n;
        double drift = (Rate - DividendYield - 0.5 * Volatility * Volatility) * dt;
        double vol = Volatility * Math.Sqrt(dt);
        double sign = type == OptionType.Call ? 1 : -1;
        double disc = Math.Exp(-Rate * time);
        bool anti = Settings.Antithetic;
        double geoExact = GeometricAsianPrice(type, Spot, strike, time, Rate, DividendYield, Volatility, n) / disc;

        int blocks = (Settings.Paths + BlockSize - 1) / BlockSize;
        var sums = new double[blocks][];
        var root = new FastRandom(Settings.Seed);
        var seeds = new ulong[blocks];
        for (int b = 0; b < blocks; b++) seeds[b] = root.NextUInt64();

        Parallel.For(0, blocks, b =>
        {
            var rng = new FastRandom(seeds[b]);
            var z = new double[n];
            int count = Math.Min(BlockSize, Settings.Paths - b * BlockSize);
            double sa = 0, sg = 0, saa = 0, sgg = 0, sag = 0;
            for (int p = 0; p < count; p++)
            {
                rng.FillGaussian(z);
                var (a1, g1) = AsianSample(z, drift, vol, strike, sign, 1);
                if (anti)
                {
                    var (a2, g2) = AsianSample(z, drift, vol, strike, sign, -1);
                    a1 = 0.5 * (a1 + a2);
                    g1 = 0.5 * (g1 + g2);
                }
                sa += a1; sg += g1; saa += a1 * a1; sgg += g1 * g1; sag += a1 * g1;
            }
            sums[b] = new[] { count, sa, sg, saa, sgg, sag };
        });

        double N = 0, SA = 0, SG = 0, SAA = 0, SGG = 0, SAG = 0;
        foreach (var s in sums) { N += s[0]; SA += s[1]; SG += s[2]; SAA += s[3]; SGG += s[4]; SAG += s[5]; }
        double ma = SA / N, mg = SG / N;
        double varA = (SAA - N * ma * ma) / (N - 1);
        if (!useControlVariate)
            return new MonteCarloResult(disc * ma, disc * Math.Sqrt(varA / N), (int)N);
        double varG = (SGG - N * mg * mg) / (N - 1);
        double cov = (SAG - N * ma * mg) / (N - 1);
        double beta = varG > 0 ? cov / varG : 0;
        double est = ma - beta * (mg - geoExact);
        double varCv = Math.Max(varA - 2 * beta * cov + beta * beta * varG, 0);
        return new MonteCarloResult(disc * est, disc * Math.Sqrt(varCv / N), (int)N);
    }

    private (double Arith, double Geo) AsianSample(double[] z, double drift, double vol, double strike, double sign, double dir)
    {
        double logS = Math.Log(Spot), sum = 0, sumLog = 0;
        for (int i = 0; i < z.Length; i++)
        {
            logS += drift + dir * vol * z[i];
            sum += Math.Exp(logS);
            sumLog += logS;
        }
        double arith = sum / z.Length, geo = Math.Exp(sumLog / z.Length);
        return (Math.Max(sign * (arith - strike), 0), Math.Max(sign * (geo - strike), 0));
    }

    /// <summary>Discretely monitored barrier option (monitoring at every step) with an optional cash rebate paid at expiry.</summary>
    public MonteCarloResult Barrier(OptionType type, BarrierType barrierType, double strike, double barrier, double time, double rebate = 0)
    {
        double sign = type == OptionType.Call ? 1 : -1;
        bool up = barrierType is BarrierType.UpAndOut or BarrierType.UpAndIn;
        bool knockIn = barrierType is BarrierType.UpAndIn or BarrierType.DownAndIn;
        return PathDependent(time, path =>
        {
            bool hit = false;
            foreach (var s in path)
            {
                if (up ? s >= barrier : s <= barrier) { hit = true; break; }
            }
            bool alive = knockIn ? hit : !hit;
            return alive ? Math.Max(sign * (path[^1] - strike), 0) : rebate;
        });
    }

    /// <summary>Floating-strike lookback: call pays S_T - min S, put pays max S - S_T (spot included in the extremum).</summary>
    public MonteCarloResult LookbackFloatingStrike(OptionType type, double time)
    {
        double spot = Spot;
        return PathDependent(time, path =>
        {
            double min = spot, max = spot;
            foreach (var s in path)
            {
                if (s < min) min = s;
                if (s > max) max = s;
            }
            return type == OptionType.Call ? path[^1] - min : max - path[^1];
        });
    }

    /// <summary>Fixed-strike lookback: call pays max(max S - K, 0), put pays max(K - min S, 0).</summary>
    public MonteCarloResult LookbackFixedStrike(OptionType type, double strike, double time)
    {
        double spot = Spot;
        return PathDependent(time, path =>
        {
            double min = spot, max = spot;
            foreach (var s in path)
            {
                if (s < min) min = s;
                if (s > max) max = s;
            }
            return type == OptionType.Call ? Math.Max(max - strike, 0) : Math.Max(strike - min, 0);
        });
    }

    /// <summary>
    /// Closed-form price of a discretely monitored geometric-average Asian option
    /// (averaging dates t_i = i T / n, i = 1..n).
    /// </summary>
    public static double GeometricAsianPrice(OptionType type, double spot, double strike, double time, double rate, double dividendYield, double volatility, int fixings)
    {
        double n = fixings;
        double muG = Math.Log(spot) + (rate - dividendYield - 0.5 * volatility * volatility) * time * (n + 1) / (2 * n);
        double sigG = volatility * Math.Sqrt(time * (n + 1) * (2 * n + 1) / (6 * n * n));
        double d1 = (muG - Math.Log(strike) + sigG * sigG) / sigG, d2 = d1 - sigG;
        double fwd = Math.Exp(muG + 0.5 * sigG * sigG);
        double disc = Math.Exp(-rate * time);
        return type == OptionType.Call
            ? disc * (fwd * Normal.Cdf(d1) - strike * Normal.Cdf(d2))
            : disc * (strike * Normal.Cdf(-d2) - fwd * Normal.Cdf(-d1));
    }

    private void Simulate(double[] path, double[] z, double drift, double vol, double dir)
    {
        double logS = Math.Log(Spot);
        for (int i = 0; i < path.Length; i++)
        {
            logS += drift + dir * vol * z[i];
            path[i] = Math.Exp(logS);
        }
    }

    private (double Mean, double StdErr) Run(Func<FastRandom, double> sample)
        => RunPaths(0, (rng, _, _, _) => sample(rng));

    private (double Mean, double StdErr) RunPaths(int steps, Func<FastRandom, double[], double[], double[], double> sample)
    {
        int paths = Settings.Paths;
        int blocks = (paths + BlockSize - 1) / BlockSize;
        var root = new FastRandom(Settings.Seed);
        var seeds = new ulong[blocks];
        for (int b = 0; b < blocks; b++) seeds[b] = root.NextUInt64();
        var partial = new (double Sum, double SumSq)[blocks];
        Parallel.For(0, blocks, b =>
        {
            var rng = new FastRandom(seeds[b]);
            var path = new double[steps];
            var mirror = new double[steps];
            var z = new double[steps];
            int count = Math.Min(BlockSize, paths - b * BlockSize);
            double s = 0, ss = 0;
            for (int p = 0; p < count; p++)
            {
                double v = sample(rng, path, mirror, z);
                s += v;
                ss += v * v;
            }
            partial[b] = (s, ss);
        });
        double sum = 0, sumSq = 0;
        foreach (var (s, ss) in partial) { sum += s; sumSq += ss; }
        double mean = sum / paths;
        double variance = Math.Max((sumSq - paths * mean * mean) / (paths - 1), 0);
        return (mean, Math.Sqrt(variance / paths));
    }
}
