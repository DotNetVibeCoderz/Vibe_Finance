using System.Globalization;
using System.Text;
using Finance.Mathematics;

namespace Finance.Risk;

/// <summary>
/// Pyfolio/empyrical-style performance statistics on simple periodic returns. Annualisation uses
/// <c>periodsPerYear</c> (252 for daily data). Risk-free rates are annual and converted per period.
/// </summary>
public static class PerformanceMetrics
{
    /// <summary>Total compounded return.</summary>
    public static double CumulativeReturn(ReadOnlySpan<double> returns)
    {
        double w = 1;
        foreach (var r in returns) w *= 1 + r;
        return w - 1;
    }

    /// <summary>Compounded wealth index starting at 1 (length n + 1).</summary>
    public static double[] WealthIndex(ReadOnlySpan<double> returns)
    {
        var w = new double[returns.Length + 1];
        w[0] = 1;
        for (int i = 0; i < returns.Length; i++) w[i + 1] = w[i] * (1 + returns[i]);
        return w;
    }

    /// <summary>Compound annual growth rate.</summary>
    public static double AnnualReturn(ReadOnlySpan<double> returns, double periodsPerYear = 252)
    {
        if (returns.Length == 0) return 0;
        double total = 1 + CumulativeReturn(returns);
        return total <= 0 ? -1 : Math.Pow(total, periodsPerYear / returns.Length) - 1;
    }

    /// <summary>Annualised standard deviation.</summary>
    public static double AnnualVolatility(ReadOnlySpan<double> returns, double periodsPerYear = 252)
        => Stats.StdDev(returns) * Math.Sqrt(periodsPerYear);

    /// <summary>Annualised Sharpe ratio of excess returns.</summary>
    public static double SharpeRatio(ReadOnlySpan<double> returns, double riskFreeRate = 0, double periodsPerYear = 252)
    {
        double rf = riskFreeRate / periodsPerYear, m = Stats.Mean(returns) - rf, s = Stats.StdDev(returns);
        return s > 0 ? m / s * Math.Sqrt(periodsPerYear) : double.NaN;
    }

    /// <summary>Downside deviation below <paramref name="requiredReturn"/> (annual), annualised.</summary>
    public static double DownsideDeviation(ReadOnlySpan<double> returns, double requiredReturn = 0, double periodsPerYear = 252)
    {
        double target = requiredReturn / periodsPerYear, s = 0;
        foreach (var r in returns)
        {
            double d = Math.Min(r - target, 0);
            s += d * d;
        }
        return Math.Sqrt(s / returns.Length) * Math.Sqrt(periodsPerYear);
    }

    /// <summary>Annualised Sortino ratio.</summary>
    public static double SortinoRatio(ReadOnlySpan<double> returns, double requiredReturn = 0, double periodsPerYear = 252)
    {
        double dd = DownsideDeviation(returns, requiredReturn, periodsPerYear);
        double excess = (Stats.Mean(returns) - requiredReturn / periodsPerYear) * periodsPerYear;
        return dd > 0 ? excess / dd : double.NaN;
    }

    /// <summary>Drawdown series (≤ 0) of the wealth index, one value per return.</summary>
    public static double[] Drawdowns(ReadOnlySpan<double> returns)
    {
        var dd = new double[returns.Length];
        double w = 1, peak = 1;
        for (int i = 0; i < returns.Length; i++)
        {
            w *= 1 + returns[i];
            if (w > peak) peak = w;
            dd[i] = w / peak - 1;
        }
        return dd;
    }

    /// <summary>Maximum drawdown as a negative fraction (−0.35 = −35%).</summary>
    public static double MaxDrawdown(ReadOnlySpan<double> returns)
    {
        double w = 1, peak = 1, mdd = 0;
        foreach (var r in returns)
        {
            w *= 1 + r;
            if (w > peak) peak = w;
            mdd = Math.Min(mdd, w / peak - 1);
        }
        return mdd;
    }

    /// <summary>Longest stretch (in periods) spent below a previous peak.</summary>
    public static int MaxDrawdownDuration(ReadOnlySpan<double> returns)
    {
        double w = 1, peak = 1;
        int cur = 0, max = 0;
        foreach (var r in returns)
        {
            w *= 1 + r;
            if (w >= peak) { peak = w; cur = 0; }
            else max = Math.Max(max, ++cur);
        }
        return max;
    }

    /// <summary>Calmar ratio: CAGR / |max drawdown|.</summary>
    public static double CalmarRatio(ReadOnlySpan<double> returns, double periodsPerYear = 252)
    {
        double mdd = MaxDrawdown(returns);
        return mdd < 0 ? AnnualReturn(returns, periodsPerYear) / -mdd : double.NaN;
    }

    /// <summary>Omega ratio: gains above the threshold over losses below it.</summary>
    public static double OmegaRatio(ReadOnlySpan<double> returns, double annualThreshold = 0, double periodsPerYear = 252)
    {
        double th = Math.Pow(1 + annualThreshold, 1 / periodsPerYear) - 1, up = 0, down = 0;
        foreach (var r in returns)
        {
            double d = r - th;
            if (d > 0) up += d; else down -= d;
        }
        return down > 0 ? up / down : double.PositiveInfinity;
    }

    /// <summary>Tail ratio: |95th percentile| / |5th percentile|.</summary>
    public static double TailRatio(ReadOnlySpan<double> returns)
    {
        double lo = Math.Abs(Stats.Quantile(returns, 0.05));
        return lo > 0 ? Math.Abs(Stats.Quantile(returns, 0.95)) / lo : double.NaN;
    }

    /// <summary>Stability: R² of a linear fit to cumulative log returns.</summary>
    public static double Stability(ReadOnlySpan<double> returns)
    {
        int n = returns.Length;
        if (n < 3) return double.NaN;
        var t = new double[n];
        var y = new double[n];
        double c = 0;
        for (int i = 0; i < n; i++)
        {
            c += Math.Log(1 + returns[i]);
            y[i] = c;
            t[i] = i;
        }
        double r = Stats.Correlation(t, y);
        return r * r;
    }

    /// <summary>Fraction of periods with a positive return.</summary>
    public static double HitRate(ReadOnlySpan<double> returns)
    {
        int k = 0;
        foreach (var r in returns) if (r > 0) k++;
        return returns.Length > 0 ? (double)k / returns.Length : double.NaN;
    }

    /// <summary>
    /// CAPM alpha (annualised, compounded) and beta of <paramref name="returns"/> versus <paramref name="benchmark"/>,
    /// fitted on excess returns.
    /// </summary>
    public static (double Alpha, double Beta) AlphaBeta(ReadOnlySpan<double> returns, ReadOnlySpan<double> benchmark, double riskFreeRate = 0, double periodsPerYear = 252)
    {
        Finance.Core.Guard.SameLength(returns.Length, benchmark.Length, "returns and benchmark");
        double rf = riskFreeRate / periodsPerYear;
        var x = new double[returns.Length];
        var y = new double[returns.Length];
        for (int i = 0; i < x.Length; i++) { x[i] = benchmark[i] - rf; y[i] = returns[i] - rf; }
        double beta = Stats.Covariance(x, y) / Stats.Variance(x);
        double alpha = Stats.Mean(y) - beta * Stats.Mean(x);
        return (Math.Pow(1 + alpha, periodsPerYear) - 1, beta);
    }

    /// <summary>Annualised tracking error of active returns.</summary>
    public static double TrackingError(ReadOnlySpan<double> returns, ReadOnlySpan<double> benchmark, double periodsPerYear = 252)
        => Stats.StdDev(Active(returns, benchmark)) * Math.Sqrt(periodsPerYear);

    /// <summary>Annualised information ratio.</summary>
    public static double InformationRatio(ReadOnlySpan<double> returns, ReadOnlySpan<double> benchmark, double periodsPerYear = 252)
    {
        var a = Active(returns, benchmark);
        double s = Stats.StdDev(a);
        return s > 0 ? Stats.Mean(a) / s * Math.Sqrt(periodsPerYear) : double.NaN;
    }

    /// <summary>Rolling annualised Sharpe ratio (NaN for the first window - 1 values).</summary>
    public static double[] RollingSharpe(ReadOnlySpan<double> returns, int window, double periodsPerYear = 252)
    {
        var res = new double[returns.Length];
        for (int i = 0; i < res.Length; i++)
            res[i] = i + 1 < window ? double.NaN : SharpeRatio(returns.Slice(i + 1 - window, window), 0, periodsPerYear);
        return res;
    }

    /// <summary>Rolling annualised volatility (O(n) running sums).</summary>
    public static double[] RollingVolatility(ReadOnlySpan<double> returns, int window, double periodsPerYear = 252)
    {
        var res = new double[returns.Length];
        double s = 0, ss = 0;
        for (int i = 0; i < returns.Length; i++)
        {
            s += returns[i];
            ss += returns[i] * returns[i];
            if (i >= window)
            {
                s -= returns[i - window];
                ss -= returns[i - window] * returns[i - window];
            }
            if (i + 1 < window) { res[i] = double.NaN; continue; }
            double var = Math.Max((ss - s * s / window) / (window - 1), 0);
            res[i] = Math.Sqrt(var * periodsPerYear);
        }
        return res;
    }

    private static double[] Active(ReadOnlySpan<double> returns, ReadOnlySpan<double> benchmark)
    {
        Finance.Core.Guard.SameLength(returns.Length, benchmark.Length, "returns and benchmark");
        var a = new double[returns.Length];
        for (int i = 0; i < a.Length; i++) a[i] = returns[i] - benchmark[i];
        return a;
    }
}

/// <summary>A tear-sheet summary of a return series (pyfolio's <c>perf_stats</c>).</summary>
public sealed record PerformanceReport
{
    public int Periods { get; init; }
    public double CumulativeReturn { get; init; }
    public double AnnualReturn { get; init; }
    public double AnnualVolatility { get; init; }
    public double SharpeRatio { get; init; }
    public double SortinoRatio { get; init; }
    public double CalmarRatio { get; init; }
    public double OmegaRatio { get; init; }
    public double MaxDrawdown { get; init; }
    public int MaxDrawdownDuration { get; init; }
    public double Stability { get; init; }
    public double TailRatio { get; init; }
    public double Skewness { get; init; }
    public double ExcessKurtosis { get; init; }
    public double DownsideDeviation { get; init; }
    public double HitRate { get; init; }
    public double BestPeriod { get; init; }
    public double WorstPeriod { get; init; }
    public double DailyValueAtRisk95 { get; init; }
    public double ExpectedShortfall95 { get; init; }
    public double? Alpha { get; init; }
    public double? Beta { get; init; }
    public double? InformationRatio { get; init; }
    public double? TrackingError { get; init; }

    /// <summary>Computes every statistic.</summary>
    public static PerformanceReport Create(ReadOnlySpan<double> returns, ReadOnlySpan<double> benchmark = default, double riskFreeRate = 0, double periodsPerYear = 252)
    {
        Finance.Core.Guard.MinLength(returns.Length, 4);
        var var = ValueAtRisk.Historical(returns, 0.95);
        bool hasBench = benchmark.Length == returns.Length;
        (double a, double b) ab = hasBench ? PerformanceMetrics.AlphaBeta(returns, benchmark, riskFreeRate, periodsPerYear) : (0, 0);
        return new PerformanceReport
        {
            Periods = returns.Length,
            CumulativeReturn = PerformanceMetrics.CumulativeReturn(returns),
            AnnualReturn = PerformanceMetrics.AnnualReturn(returns, periodsPerYear),
            AnnualVolatility = PerformanceMetrics.AnnualVolatility(returns, periodsPerYear),
            SharpeRatio = PerformanceMetrics.SharpeRatio(returns, riskFreeRate, periodsPerYear),
            SortinoRatio = PerformanceMetrics.SortinoRatio(returns, 0, periodsPerYear),
            CalmarRatio = PerformanceMetrics.CalmarRatio(returns, periodsPerYear),
            OmegaRatio = PerformanceMetrics.OmegaRatio(returns, 0, periodsPerYear),
            MaxDrawdown = PerformanceMetrics.MaxDrawdown(returns),
            MaxDrawdownDuration = PerformanceMetrics.MaxDrawdownDuration(returns),
            Stability = PerformanceMetrics.Stability(returns),
            TailRatio = PerformanceMetrics.TailRatio(returns),
            Skewness = Stats.Skewness(returns),
            ExcessKurtosis = Stats.ExcessKurtosis(returns),
            DownsideDeviation = PerformanceMetrics.DownsideDeviation(returns, 0, periodsPerYear),
            HitRate = PerformanceMetrics.HitRate(returns),
            BestPeriod = Stats.Max(returns),
            WorstPeriod = Stats.Min(returns),
            DailyValueAtRisk95 = var.ValueAtRisk,
            ExpectedShortfall95 = var.ExpectedShortfall,
            Alpha = hasBench ? ab.a : null,
            Beta = hasBench ? ab.b : null,
            InformationRatio = hasBench ? PerformanceMetrics.InformationRatio(returns, benchmark, periodsPerYear) : null,
            TrackingError = hasBench ? PerformanceMetrics.TrackingError(returns, benchmark, periodsPerYear) : null,
        };
    }

    /// <summary>Name/value rows for display in tables and grids.</summary>
    public IReadOnlyList<(string Metric, string Value)> ToRows()
    {
        static string P(double v) => double.IsFinite(v) ? v.ToString("P2", CultureInfo.InvariantCulture) : "n/a";
        static string F(double v) => double.IsFinite(v) ? v.ToString("F2", CultureInfo.InvariantCulture) : "n/a";
        var rows = new List<(string, string)>
        {
            ("Periods", Periods.ToString(CultureInfo.InvariantCulture)),
            ("Cumulative return", P(CumulativeReturn)),
            ("Annual return", P(AnnualReturn)),
            ("Annual volatility", P(AnnualVolatility)),
            ("Sharpe ratio", F(SharpeRatio)),
            ("Sortino ratio", F(SortinoRatio)),
            ("Calmar ratio", F(CalmarRatio)),
            ("Omega ratio", F(OmegaRatio)),
            ("Max drawdown", P(MaxDrawdown)),
            ("Max drawdown duration", MaxDrawdownDuration.ToString(CultureInfo.InvariantCulture)),
            ("Stability", F(Stability)),
            ("Tail ratio", F(TailRatio)),
            ("Skew", F(Skewness)),
            ("Excess kurtosis", F(ExcessKurtosis)),
            ("Downside deviation", P(DownsideDeviation)),
            ("Hit rate", P(HitRate)),
            ("Best period", P(BestPeriod)),
            ("Worst period", P(WorstPeriod)),
            ("VaR 95% (1 period)", P(DailyValueAtRisk95)),
            ("ES 95% (1 period)", P(ExpectedShortfall95)),
        };
        if (Alpha is { } a) rows.Add(("Alpha", P(a)));
        if (Beta is { } b) rows.Add(("Beta", F(b)));
        if (InformationRatio is { } ir) rows.Add(("Information ratio", F(ir)));
        if (TrackingError is { } te) rows.Add(("Tracking error", P(te)));
        return rows;
    }

    /// <summary>Plain-text table.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var (m, v) in ToRows()) sb.Append(m.PadRight(26)).AppendLine(v.PadLeft(12));
        return sb.ToString();
    }
}
