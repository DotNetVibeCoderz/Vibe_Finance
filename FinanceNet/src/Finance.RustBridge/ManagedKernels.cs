using Finance.Core;
using Finance.Mathematics;

namespace Finance.RustBridge;

/// <summary>
/// Managed implementations of every <c>finance_native</c> entry point, line for line the same
/// algorithms (and, for Monte Carlo, the same random streams). Used automatically when the
/// native library is missing, and directly by benchmarks and parity tests.
/// </summary>
public static class ManagedKernels
{
    /// <summary>Fixed number of Monte Carlo RNG streams (matches the Rust crate).</summary>
    public const int MonteCarloChunks = 64;

    private const ulong Golden = 0x9E3779B97F4A7C15UL;

    /// <summary>Seed of the <paramref name="index"/>-th independent stream derived from <paramref name="seed"/>.</summary>
    public static ulong StreamSeed(ulong seed, ulong index) => unchecked(seed + (index + 1) * Golden);

    /// <summary>Simple moving average (sliding sum). Leading positions are NaN.</summary>
    public static void Sma(ReadOnlySpan<double> x, int period, Span<double> output)
    {
        CheckWindow(x, period, output);
        double sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            sum += x[i];
            if (i >= period) sum -= x[i - period];
            output[i] = i + 1 >= period ? sum / period : double.NaN;
        }
    }

    /// <summary>EMA with alpha = 2/(period+1), seeded with the SMA of the first window.</summary>
    public static void Ema(ReadOnlySpan<double> x, int period, Span<double> output)
    {
        CheckWindow(x, period, output);
        double alpha = 2.0 / (period + 1.0), e = 0;
        for (int i = 0; i < x.Length; i++)
        {
            if (i + 1 < period)
            {
                e += x[i];
                output[i] = double.NaN;
            }
            else if (i + 1 == period)
            {
                e = (e + x[i]) / period;
                output[i] = e;
            }
            else
            {
                e += alpha * (x[i] - e);
                output[i] = e;
            }
        }
    }

    /// <summary>Wilder's RSI; the first value is at index <paramref name="period"/>.</summary>
    public static void Rsi(ReadOnlySpan<double> x, int period, Span<double> output)
    {
        CheckWindow(x, period, output);
        output[..x.Length].Fill(double.NaN);
        int n = x.Length;
        if (n <= period) return;
        double g = 0, l = 0;
        for (int i = 1; i <= period; i++)
        {
            double d = x[i] - x[i - 1];
            if (d > 0) g += d; else l -= d;
        }
        double p = period;
        g /= p;
        l /= p;
        output[period] = RsiValue(g, l);
        for (int i = period + 1; i < n; i++)
        {
            double d = x[i] - x[i - 1];
            double gi = d > 0 ? d : 0, li = d > 0 ? 0 : -d;
            g = (g * (p - 1) + gi) / p;
            l = (l * (p - 1) + li) / p;
            output[i] = RsiValue(g, l);
        }
    }

    private static double RsiValue(double gain, double loss)
        => loss == 0 ? (gain == 0 ? 50 : 100) : 100 - 100 / (1 + gain / loss);

    /// <summary>Rolling standard deviation (two-pass per window).</summary>
    public static void RollingStd(ReadOnlySpan<double> x, int period, int ddof, Span<double> output)
    {
        CheckWindow(x, period, output);
        if (ddof < 0 || ddof >= period) throw new ArgumentOutOfRangeException(nameof(ddof));
        for (int i = 0; i < x.Length; i++)
        {
            if (i + 1 < period)
            {
                output[i] = double.NaN;
                continue;
            }
            var w = x.Slice(i + 1 - period, period);
            double s = 0;
            foreach (var v in w) s += v;
            double m = s / period, ss = 0;
            foreach (var v in w)
            {
                double d = v - m;
                ss += d * d;
            }
            output[i] = Math.Sqrt(ss / (period - ddof));
        }
    }

    /// <summary>Bollinger bands: window mean ± k · population standard deviation.</summary>
    public static void Bollinger(ReadOnlySpan<double> x, int period, double k, Span<double> middle, Span<double> upper, Span<double> lower)
    {
        CheckWindow(x, period, middle);
        if (upper.Length < x.Length || lower.Length < x.Length) throw new ArgumentException("Output spans are too short.");
        for (int i = 0; i < x.Length; i++)
        {
            if (i + 1 < period)
            {
                middle[i] = upper[i] = lower[i] = double.NaN;
                continue;
            }
            var w = x.Slice(i + 1 - period, period);
            double s = 0;
            foreach (var v in w) s += v;
            double m = s / period, ss = 0;
            foreach (var v in w)
            {
                double d = v - m;
                ss += d * d;
            }
            double sd = Math.Sqrt(ss / period);
            middle[i] = m;
            upper[i] = m + k * sd;
            lower[i] = m - k * sd;
        }
    }

    /// <summary>Log returns; <paramref name="output"/> needs length n - 1.</summary>
    public static void LogReturns(ReadOnlySpan<double> x, Span<double> output)
    {
        if (x.Length < 2) throw new ArgumentException("Need at least two prices.");
        if (output.Length < x.Length - 1) throw new ArgumentException("Output span is too short.");
        for (int i = 0; i < x.Length - 1; i++) output[i] = Math.Log(x[i + 1] / x[i]);
    }

    /// <summary>Maximum drawdown of a price or equity series.</summary>
    public static DrawdownInfo MaxDrawdown(ReadOnlySpan<double> x)
    {
        if (x.IsEmpty) throw new ArgumentException("Series is empty.");
        double peak = x[0], mdd = 0;
        int peakI = 0, bestPeak = 0, bestTrough = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = x[i];
            if (v > peak) { peak = v; peakI = i; }
            double dd = peak != 0 ? (peak - v) / peak : 0;
            if (dd > mdd) { mdd = dd; bestPeak = peakI; bestTrough = i; }
        }
        return new DrawdownInfo(mdd, bestPeak, bestTrough);
    }

    /// <summary>Index of the VaR order statistic (matches the Rust crate, including the 1e-9 slack).</summary>
    public static int TailIndex(int n, double confidence)
    {
        long k = (long)Math.Ceiling((1 - confidence) * n - 1e-9) - 1;
        return (int)Math.Clamp(k, 0, n - 1);
    }

    /// <summary>Historical VaR/CVaR via quickselect (O(n) expected).</summary>
    public static TailRisk HistoricalVaR(ReadOnlySpan<double> returns, double confidence)
    {
        if (returns.IsEmpty) throw new ArgumentException("No returns.");
        if (!(confidence > 0 && confidence < 1)) throw new ArgumentOutOfRangeException(nameof(confidence));
        int k = TailIndex(returns.Length, confidence);
        var buf = returns.ToArray();
        Select(buf, k);
        double s = 0;
        for (int i = 0; i <= k; i++) s += buf[i];
        return new TailRisk(-buf[k], -s / (k + 1));
    }

    /// <summary>Partially orders <paramref name="a"/> so a[k] is the k-th smallest and a[..k] ≤ a[k].</summary>
    internal static void Select(double[] a, int k)
    {
        int lo = 0, hi = a.Length - 1;
        while (hi > lo)
        {
            int mid = lo + (hi - lo) / 2;
            // Median of three as pivot.
            if (a[mid] < a[lo]) (a[mid], a[lo]) = (a[lo], a[mid]);
            if (a[hi] < a[lo]) (a[hi], a[lo]) = (a[lo], a[hi]);
            if (a[hi] < a[mid]) (a[hi], a[mid]) = (a[mid], a[hi]);
            double pivot = a[mid];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (a[i] < pivot) i++;
                while (a[j] > pivot) j--;
                if (i <= j)
                {
                    (a[i], a[j]) = (a[j], a[i]);
                    i++;
                    j--;
                }
            }
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else return;
        }
    }

    /// <summary>Sample covariance of the columns of a row-major rows × cols buffer.</summary>
    public static void Covariance(ReadOnlySpan<double> data, int rows, int cols, int ddof, Span<double> output)
    {
        if (cols <= 0 || rows <= ddof) throw new ArgumentException("Not enough observations.");
        if (data.Length < rows * cols || output.Length < cols * cols) throw new ArgumentException("Buffer too short.");
        var means = new double[cols];
        for (int r = 0; r < rows; r++)
        {
            var row = data.Slice(r * cols, cols);
            for (int c = 0; c < cols; c++) means[c] += row[c];
        }
        for (int c = 0; c < cols; c++) means[c] /= rows;
        output[..(cols * cols)].Clear();
        var centered = new double[cols];
        for (int r = 0; r < rows; r++)
        {
            var row = data.Slice(r * cols, cols);
            for (int c = 0; c < cols; c++) centered[c] = row[c] - means[c];
            for (int a = 0; a < cols; a++)
            {
                double ca = centered[a];
                var dst = output.Slice(a * cols, cols);
                for (int b = a; b < cols; b++) dst[b] += ca * centered[b];
            }
        }
        double scale = 1.0 / (rows - ddof);
        for (int a = 0; a < cols; a++)
            for (int b = a; b < cols; b++)
            {
                double v = output[a * cols + b] * scale;
                output[a * cols + b] = v;
                output[b * cols + a] = v;
            }
    }

    /// <summary>
    /// Black-Scholes-Merton price and greeks for one option (vega per 1.00 vol, theta per year,
    /// rho per 1.00 rate). t ≤ 0 or vol ≤ 0 prices at (discounted) intrinsic value.
    /// </summary>
    public static (double Price, double Delta, double Gamma, double Vega, double Theta, double Rho) BlackScholes(
        double s, double k, double t, double r, double q, double vol, bool call)
    {
        if (t <= 0)
        {
            bool itm = call ? s > k : s < k;
            double intrinsic = call ? Math.Max(s - k, 0) : Math.Max(k - s, 0);
            return (intrinsic, itm ? (call ? 1 : -1) : 0, 0, 0, 0, 0);
        }
        double dq = Math.Exp(-q * t), dr = Math.Exp(-r * t);
        if (vol <= 0)
        {
            double fwd = s * dq - k * dr;
            if (call) return fwd > 0 ? (fwd, dq, 0, 0, 0, 0) : (0, 0, 0, 0, 0, 0);
            return fwd < 0 ? (-fwd, -dq, 0, 0, 0, 0) : (0, 0, 0, 0, 0, 0);
        }
        double sqrtT = Math.Sqrt(t), vs = vol * sqrtT;
        double d1 = (Math.Log(s / k) + (r - q + 0.5 * vol * vol) * t) / vs, d2 = d1 - vs;
        double nd1 = Normal.Pdf(d1);
        double gamma = dq * nd1 / (s * vs);
        double vega = s * dq * nd1 * sqrtT;
        double decay = -s * dq * nd1 * vol / (2 * sqrtT);
        if (call)
        {
            double c1 = Normal.Cdf(d1), c2 = Normal.Cdf(d2);
            return (s * dq * c1 - k * dr * c2, dq * c1, gamma, vega, decay - r * k * dr * c2 + q * s * dq * c1, k * t * dr * c2);
        }
        else
        {
            double c1 = Normal.Cdf(-d1), c2 = Normal.Cdf(-d2);
            return (k * dr * c2 - s * dq * c1, -dq * c1, gamma, vega, decay + r * k * dr * c2 - q * s * dq * c1, -k * t * dr * c2);
        }
    }

    /// <summary>Batch Black-Scholes; any empty output span is skipped.</summary>
    public static void BlackScholesBatch(OptionBatch batch, Span<double> price, Span<double> delta = default, Span<double> gamma = default,
        Span<double> vega = default, Span<double> theta = default, Span<double> rho = default)
    {
        int n = batch.Count;
        for (int i = 0; i < n; i++)
        {
            var g = BlackScholes(batch.Spot[i], batch.Strike[i], batch.Time[i], batch.Rate[i], batch.Dividend[i], batch.Volatility[i], batch.IsCall[i]);
            if (!price.IsEmpty) price[i] = g.Price;
            if (!delta.IsEmpty) delta[i] = g.Delta;
            if (!gamma.IsEmpty) gamma[i] = g.Gamma;
            if (!vega.IsEmpty) vega[i] = g.Vega;
            if (!theta.IsEmpty) theta[i] = g.Theta;
            if (!rho.IsEmpty) rho[i] = g.Rho;
        }
    }

    /// <summary>Paths assigned to Monte Carlo chunk <paramref name="c"/>.</summary>
    public static long ChunkPaths(long paths, int c) => paths / MonteCarloChunks + (c < paths % MonteCarloChunks ? 1 : 0);

    /// <summary>
    /// Monte Carlo European option. Runs the same 64 RNG streams as the Rust crate, in parallel,
    /// and combines them in a fixed order, so results do not depend on the thread count.
    /// </summary>
    public static MonteCarloEstimate MonteCarloEuropean(EuropeanOptionSpec o, long paths, ulong seed = 42, bool antithetic = true)
    {
        o.Validate();
        if (paths < 2) throw new ArgumentOutOfRangeException(nameof(paths));
        var sums = new (double S1, double S2)[MonteCarloChunks];
        Parallel.For(0, MonteCarloChunks, c => sums[c] = McChunk(o, ChunkPaths(paths, c), StreamSeed(seed, (ulong)c), antithetic));
        double s1 = 0, s2 = 0;
        foreach (var (a, b) in sums) { s1 += a; s2 += b; }
        double n = paths, mean = s1 / n;
        double var = Math.Max(0, (s2 - n * mean * mean) / (n - 1));
        double disc = Math.Exp(-o.Rate * o.Time);
        return new MonteCarloEstimate(disc * mean, disc * Math.Sqrt(var / n), paths);
    }

    private static (double, double) McChunk(EuropeanOptionSpec o, long paths, ulong seed, bool antithetic)
    {
        var rng = new FastRandom(seed);
        double drift = (o.Rate - o.Dividend - 0.5 * o.Volatility * o.Volatility) * o.Time;
        double diff = o.Volatility * Math.Sqrt(o.Time);
        double s1 = 0, s2 = 0;
        for (long i = 0; i < paths; i++)
        {
            double z = rng.NextGaussian();
            double st = o.Spot * Math.Exp(drift + diff * z);
            double v = o.IsCall ? Math.Max(st - o.Strike, 0) : Math.Max(o.Strike - st, 0);
            if (antithetic)
            {
                double st2 = o.Spot * Math.Exp(drift - diff * z);
                v = 0.5 * (v + (o.IsCall ? Math.Max(st2 - o.Strike, 0) : Math.Max(o.Strike - st2, 0)));
            }
            s1 += v;
            s2 += v * v;
        }
        return (s1, s2);
    }

    /// <summary>GBM paths into a row-major paths × (steps + 1) buffer; path p uses stream p.</summary>
    public static void SimulateGbm(double s0, double mu, double sigma, double dt, int steps, int paths, ulong seed, Span<double> output)
    {
        int width = CheckGbm(sigma, dt, steps, paths, output.Length);
        for (int p = 0; p < paths; p++) GbmPath(output.Slice(p * width, width), s0, mu, sigma, dt, StreamSeed(seed, (ulong)p));
    }

    /// <summary>Parallel GBM simulation into an array (same streams, so same values, as the span overload).</summary>
    public static void SimulateGbm(double s0, double mu, double sigma, double dt, int steps, int paths, ulong seed, double[] output)
    {
        int width = CheckGbm(sigma, dt, steps, paths, output.Length);
        Parallel.For(0, paths, p => GbmPath(output.AsSpan(p * width, width), s0, mu, sigma, dt, StreamSeed(seed, (ulong)p)));
    }

    private static int CheckGbm(double sigma, double dt, int steps, int paths, int length)
    {
        if (paths <= 0 || steps < 0 || !(dt > 0) || !(sigma >= 0)) throw new ArgumentException("Invalid GBM parameters.");
        int width = steps + 1;
        if (length < (long)paths * width) throw new ArgumentException("Output buffer is too short.");
        return width;
    }

    private static void GbmPath(Span<double> row, double s0, double mu, double sigma, double dt, ulong seed)
    {
        double drift = (mu - 0.5 * sigma * sigma) * dt, diff = sigma * Math.Sqrt(dt);
        var rng = new FastRandom(seed);
        double s = s0;
        row[0] = s;
        for (int j = 1; j < row.Length; j++)
        {
            s *= Math.Exp(drift + diff * rng.NextGaussian());
            row[j] = s;
        }
    }

    private static void CheckWindow(ReadOnlySpan<double> x, int period, Span<double> output)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        if (output.Length < x.Length) throw new ArgumentException("Output span is too short.");
    }
}
