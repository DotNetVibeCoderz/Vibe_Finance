using Finance.Mathematics;

namespace Finance.TimeSeries;

/// <summary>Return and drawdown transforms over price or return arrays.</summary>
public static class Returns
{
    /// <summary>Simple returns p[t]/p[t-1] - 1; length n-1.</summary>
    public static double[] Simple(ReadOnlySpan<double> prices)
    {
        if (prices.Length < 2) return Array.Empty<double>();
        var r = new double[prices.Length - 1];
        for (int i = 1; i < prices.Length; i++) r[i - 1] = prices[i] / prices[i - 1] - 1;
        return r;
    }

    /// <summary>Log returns ln(p[t]/p[t-1]); length n-1.</summary>
    public static double[] Log(ReadOnlySpan<double> prices)
    {
        if (prices.Length < 2) return Array.Empty<double>();
        var r = new double[prices.Length - 1];
        for (int i = 1; i < prices.Length; i++) r[i - 1] = Math.Log(prices[i] / prices[i - 1]);
        return r;
    }

    /// <summary>Cumulative compounded return series: prod(1+r) - 1.</summary>
    public static double[] Cumulative(ReadOnlySpan<double> returns)
    {
        var c = new double[returns.Length];
        double acc = 1;
        for (int i = 0; i < returns.Length; i++)
        {
            acc *= 1 + returns[i];
            c[i] = acc - 1;
        }
        return c;
    }

    /// <summary>Equity curve starting at <paramref name="initial"/>: initial * prod(1+r), length n+1.</summary>
    public static double[] EquityCurve(ReadOnlySpan<double> returns, double initial = 1.0)
    {
        var e = new double[returns.Length + 1];
        e[0] = initial;
        for (int i = 0; i < returns.Length; i++) e[i + 1] = e[i] * (1 + returns[i]);
        return e;
    }

    /// <summary>Total compounded return.</summary>
    public static double Total(ReadOnlySpan<double> returns)
    {
        double acc = 1;
        foreach (var r in returns) acc *= 1 + r;
        return acc - 1;
    }

    /// <summary>Compound annual growth rate from periodic returns.</summary>
    public static double Annualized(ReadOnlySpan<double> returns, double periodsPerYear = 252)
    {
        if (returns.Length == 0) return 0;
        double growth = 1 + Total(returns);
        return growth <= 0 ? -1 : Math.Pow(growth, periodsPerYear / returns.Length) - 1;
    }

    /// <summary>Annualised volatility (sample standard deviation × √periods).</summary>
    public static double AnnualizedVolatility(ReadOnlySpan<double> returns, double periodsPerYear = 252)
        => Stats.StdDev(returns) * Math.Sqrt(periodsPerYear);

    /// <summary>
    /// Drawdown of an equity/price series: value / running max - 1 (≤ 0).
    /// </summary>
    public static double[] Drawdown(ReadOnlySpan<double> equity)
    {
        var dd = new double[equity.Length];
        double peak = double.NegativeInfinity;
        for (int i = 0; i < equity.Length; i++)
        {
            if (equity[i] > peak) peak = equity[i];
            dd[i] = equity[i] / peak - 1;
        }
        return dd;
    }

    /// <summary>Drawdown series computed from returns (equity starts at 1, so length n).</summary>
    public static double[] DrawdownFromReturns(ReadOnlySpan<double> returns)
    {
        var dd = new double[returns.Length];
        double eq = 1, peak = 1;
        for (int i = 0; i < returns.Length; i++)
        {
            eq *= 1 + returns[i];
            if (eq > peak) peak = eq;
            dd[i] = eq / peak - 1;
        }
        return dd;
    }

    /// <summary>Maximum drawdown (a negative number, or 0) of an equity series, with peak and trough indices.</summary>
    public static (double MaxDrawdown, int PeakIndex, int TroughIndex) MaxDrawdown(ReadOnlySpan<double> equity)
    {
        double peak = double.NegativeInfinity, worst = 0;
        int peakIdx = 0, bestPeak = 0, trough = 0;
        for (int i = 0; i < equity.Length; i++)
        {
            if (equity[i] > peak) { peak = equity[i]; peakIdx = i; }
            double dd = equity[i] / peak - 1;
            if (dd < worst) { worst = dd; bestPeak = peakIdx; trough = i; }
        }
        return (worst, bestPeak, trough);
    }

    /// <summary>Excess returns r - rf/periods.</summary>
    public static double[] Excess(ReadOnlySpan<double> returns, double annualRiskFree, double periodsPerYear = 252)
    {
        var e = new double[returns.Length];
        double rf = annualRiskFree / periodsPerYear;
        for (int i = 0; i < e.Length; i++) e[i] = returns[i] - rf;
        return e;
    }

    /// <summary>Sharpe ratio (annualised).</summary>
    public static double Sharpe(ReadOnlySpan<double> returns, double annualRiskFree = 0, double periodsPerYear = 252)
    {
        var ex = Excess(returns, annualRiskFree, periodsPerYear);
        double sd = Stats.StdDev(ex);
        return sd > 1e-12 ? Stats.Mean(ex) / sd * Math.Sqrt(periodsPerYear) : 0;
    }

    /// <summary>Sortino ratio (annualised, downside deviation relative to the risk-free rate).</summary>
    public static double Sortino(ReadOnlySpan<double> returns, double annualRiskFree = 0, double periodsPerYear = 252)
    {
        var ex = Excess(returns, annualRiskFree, periodsPerYear);
        double down = 0;
        foreach (var r in ex) if (r < 0) down += r * r;
        double dd = Math.Sqrt(down / Math.Max(1, ex.Length));
        return dd > 0 ? Stats.Mean(ex) / dd * Math.Sqrt(periodsPerYear) : 0;
    }

    /// <summary>Calmar ratio: annualised return / |max drawdown|.</summary>
    public static double Calmar(ReadOnlySpan<double> returns, double periodsPerYear = 252)
    {
        var mdd = MaxDrawdown(EquityCurve(returns)).MaxDrawdown;
        return mdd < 0 ? Annualized(returns, periodsPerYear) / -mdd : 0;
    }

    /// <summary>Resamples daily returns to period returns by compounding consecutive groups of a key.</summary>
    public static (DateTime[] Dates, double[] Returns) Compound(ReadOnlySpan<DateTime> dates, ReadOnlySpan<double> returns, Func<DateTime, int> periodKey)
    {
        var d = new List<DateTime>();
        var r = new List<double>();
        int i = 0;
        while (i < returns.Length)
        {
            int key = periodKey(dates[i]);
            double acc = 1;
            int j = i;
            while (j < returns.Length && periodKey(dates[j]) == key) { acc *= 1 + returns[j]; j++; }
            d.Add(dates[j - 1]);
            r.Add(acc - 1);
            i = j;
        }
        return (d.ToArray(), r.ToArray());
    }

    /// <summary>Monthly compounded returns.</summary>
    public static (DateTime[] Dates, double[] Returns) Monthly(ReadOnlySpan<DateTime> dates, ReadOnlySpan<double> returns)
        => Compound(dates, returns, d => d.Year * 12 + d.Month);

    /// <summary>Yearly compounded returns.</summary>
    public static (DateTime[] Dates, double[] Returns) Yearly(ReadOnlySpan<DateTime> dates, ReadOnlySpan<double> returns)
        => Compound(dates, returns, d => d.Year);
}
