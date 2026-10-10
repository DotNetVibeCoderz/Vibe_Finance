using Finance.Mathematics;

namespace Finance.Backtesting;

/// <summary>Output of a vectorised backtest.</summary>
/// <param name="Returns">Strategy return per period (Returns[0] only carries the initial trading cost).</param>
/// <param name="Equity">Equity per period, starting from the initial capital.</param>
/// <param name="Turnover">|Δposition| traded at each close.</param>
public sealed record VectorizedResult(double[] Returns, double[] Equity, double[] Turnover, PerformanceStatistics Statistics);

/// <summary>
/// Vectorised (vectorbt-style) backtesting: positions are decided at each close and earn the next period's
/// return, so there is no look-ahead. Costs are charged on turnover at the close where the position changes.
/// </summary>
public static class VectorizedBacktester
{
    /// <summary>
    /// Single asset. <paramref name="positions"/>[t] is the exposure (fraction of equity, negative = short) held
    /// from close t to close t+1.
    /// </summary>
    public static VectorizedResult Run(ReadOnlySpan<double> prices, ReadOnlySpan<double> positions, double costBps = 0,
        double periodsPerYear = 252, double initialCapital = 1, double riskFreeRate = 0)
    {
        int n = prices.Length;
        if (positions.Length != n) throw new ArgumentException("Prices and positions must have the same length.");
        var r = new double[n];
        var turnover = new double[n];
        double cost = costBps / 10_000;
        double prev = 0;
        for (int t = 0; t < n; t++)
        {
            double gross = t > 0 ? positions[t - 1] * (prices[t] / prices[t - 1] - 1) : 0;
            turnover[t] = Math.Abs(positions[t] - prev);
            r[t] = gross - turnover[t] * cost;
            prev = positions[t];
        }
        return Finish(r, turnover, periodsPerYear, initialCapital, riskFreeRate);
    }

    /// <summary>
    /// Multi-asset. <paramref name="prices"/> and <paramref name="weights"/> are T x N; weights row t is held from
    /// close t to close t+1. Weights drift is ignored (daily rebalancing to the stated weights).
    /// </summary>
    public static VectorizedResult Run(Matrix prices, Matrix weights, double costBps = 0, double periodsPerYear = 252,
        double initialCapital = 1, double riskFreeRate = 0)
    {
        if (prices.Rows != weights.Rows || prices.Columns != weights.Columns) throw new ArgumentException("Shape mismatch.");
        int n = prices.Rows, k = prices.Columns;
        var r = new double[n];
        var turnover = new double[n];
        double cost = costBps / 10_000;
        for (int t = 0; t < n; t++)
        {
            double gross = 0, traded = 0;
            for (int j = 0; j < k; j++)
            {
                if (t > 0) gross += weights[t - 1, j] * (prices[t, j] / prices[t - 1, j] - 1);
                traded += Math.Abs(weights[t, j] - (t > 0 ? weights[t - 1, j] : 0));
            }
            turnover[t] = traded;
            r[t] = gross - traded * cost;
        }
        return Finish(r, turnover, periodsPerYear, initialCapital, riskFreeRate);
    }

    /// <summary>
    /// Converts signals to positions: values are clipped to [−1, 1] (or [0, 1] when <paramref name="allowShort"/> is
    /// false). NaN means "keep the previous position".
    /// </summary>
    public static double[] PositionsFromSignals(ReadOnlySpan<double> signals, bool allowShort = true)
    {
        var p = new double[signals.Length];
        double last = 0;
        for (int i = 0; i < signals.Length; i++)
        {
            if (!double.IsNaN(signals[i])) last = Math.Clamp(signals[i], allowShort ? -1 : 0, 1);
            p[i] = last;
        }
        return p;
    }

    /// <summary>
    /// Long-only crossover positions: 1 while the fast simple moving average is above the slow one, else 0.
    /// </summary>
    public static double[] CrossoverPositions(ReadOnlySpan<double> prices, int fast, int slow, bool allowShort = false)
    {
        var f = Indicators.Sma(prices, fast);
        var s = Indicators.Sma(prices, slow);
        var p = new double[prices.Length];
        for (int i = 0; i < p.Length; i++)
        {
            if (double.IsNaN(f[i]) || double.IsNaN(s[i])) continue;
            p[i] = f[i] > s[i] ? 1 : (allowShort ? -1 : 0);
        }
        return p;
    }

    private static VectorizedResult Finish(double[] r, double[] turnover, double ppy, double capital, double rf)
    {
        var equity = new double[r.Length];
        double e = capital;
        for (int t = 0; t < r.Length; t++)
        {
            e *= 1 + r[t];
            equity[t] = e;
        }
        var curve = new double[r.Length + 1];
        curve[0] = capital;
        Array.Copy(equity, 0, curve, 1, equity.Length);
        return new VectorizedResult(r, equity, turnover, PerformanceStatistics.Compute(curve, ppy, rf));
    }
}
