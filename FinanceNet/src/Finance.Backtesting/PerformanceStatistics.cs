namespace Finance.Backtesting;

/// <summary>Summary statistics of an equity curve and its trades.</summary>
public sealed record PerformanceStatistics
{
    public double TotalReturn { get; init; }
    /// <summary>Compound annual growth rate.</summary>
    public double Cagr { get; init; }
    public double AnnualVolatility { get; init; }
    public double SharpeRatio { get; init; }
    public double SortinoRatio { get; init; }
    /// <summary>Largest peak-to-trough decline as a positive fraction (0.25 = −25%).</summary>
    public double MaxDrawdown { get; init; }
    /// <summary>Longest stretch, in bars, spent below a previous equity peak.</summary>
    public int MaxDrawdownDuration { get; init; }
    /// <summary>CAGR / max drawdown.</summary>
    public double CalmarRatio { get; init; }
    public int NumberOfTrades { get; init; }
    /// <summary>Fraction of round trips with positive PnL.</summary>
    public double WinRate { get; init; }
    /// <summary>Gross profit / gross loss (∞ with no losing trades).</summary>
    public double ProfitFactor { get; init; }
    public double AverageTradePnl { get; init; }
    public double AverageTradeReturn { get; init; }
    public double BestTradePnl { get; init; }
    public double WorstTradePnl { get; init; }
    /// <summary>Average gross exposure as a fraction of equity.</summary>
    public double Exposure { get; init; }
    public int Periods { get; init; }

    /// <summary>
    /// Computes statistics from an equity curve. <paramref name="riskFreeRate"/> is annual; per-period excess
    /// returns use rf / periodsPerYear. Sortino uses downside deviation √(mean(min(r − rf, 0)²)).
    /// </summary>
    public static PerformanceStatistics Compute(ReadOnlySpan<double> equity, double periodsPerYear = 252, double riskFreeRate = 0,
        IReadOnlyList<RoundTrip>? trades = null, ReadOnlySpan<double> grossExposure = default)
    {
        int n = equity.Length;
        if (n == 0) return new PerformanceStatistics();
        double total = equity[^1] / equity[0] - 1;
        double years = (n - 1) / periodsPerYear;
        double cagr = years > 0 && equity[^1] > 0 ? Math.Pow(equity[^1] / equity[0], 1 / years) - 1 : 0;

        double rfPer = riskFreeRate / periodsPerYear;
        int m = n - 1;
        double mean = 0, downside = 0;
        var r = new double[Math.Max(m, 0)];
        for (int i = 1; i < n; i++)
        {
            r[i - 1] = equity[i - 1] != 0 ? equity[i] / equity[i - 1] - 1 : 0;
            mean += r[i - 1];
        }
        mean = m > 0 ? mean / m : 0;
        double ss = 0;
        foreach (var v in r)
        {
            ss += (v - mean) * (v - mean);
            double d = Math.Min(v - rfPer, 0);
            downside += d * d;
        }
        double std = m > 1 ? Math.Sqrt(ss / (m - 1)) : 0;
        downside = m > 0 ? Math.Sqrt(downside / m) : 0;
        double sqrtP = Math.Sqrt(periodsPerYear);

        double peak = equity[0], maxDd = 0;
        int ddLen = 0, maxDdLen = 0;
        for (int i = 0; i < n; i++)
        {
            if (equity[i] >= peak) { peak = equity[i]; ddLen = 0; }
            else
            {
                ddLen++;
                maxDdLen = Math.Max(maxDdLen, ddLen);
                maxDd = Math.Max(maxDd, 1 - equity[i] / peak);
            }
        }

        double exposure = 0;
        if (grossExposure.Length == n)
        {
            for (int i = 0; i < n; i++) exposure += equity[i] != 0 ? grossExposure[i] / equity[i] : 0;
            exposure /= n;
        }

        int count = trades?.Count ?? 0;
        double wins = 0, grossProfit = 0, grossLoss = 0, pnlSum = 0, retSum = 0, best = 0, worst = 0;
        if (trades is not null)
        {
            best = double.NegativeInfinity;
            worst = double.PositiveInfinity;
            foreach (var t in trades)
            {
                if (t.Pnl > 0) { wins++; grossProfit += t.Pnl; }
                else grossLoss -= t.Pnl;
                pnlSum += t.Pnl;
                retSum += t.ReturnPct;
                best = Math.Max(best, t.Pnl);
                worst = Math.Min(worst, t.Pnl);
            }
            if (count == 0) { best = 0; worst = 0; }
        }

        return new PerformanceStatistics
        {
            TotalReturn = total,
            Cagr = cagr,
            AnnualVolatility = std * sqrtP,
            SharpeRatio = std > 0 ? (mean - rfPer) / std * sqrtP : 0,
            SortinoRatio = downside > 0 ? (mean - rfPer) / downside * sqrtP : 0,
            MaxDrawdown = maxDd,
            MaxDrawdownDuration = maxDdLen,
            CalmarRatio = maxDd > 0 ? cagr / maxDd : 0,
            NumberOfTrades = count,
            WinRate = count > 0 ? wins / count : 0,
            ProfitFactor = grossLoss > 0 ? grossProfit / grossLoss : (grossProfit > 0 ? double.PositiveInfinity : 0),
            AverageTradePnl = count > 0 ? pnlSum / count : 0,
            AverageTradeReturn = count > 0 ? retSum / count : 0,
            BestTradePnl = best,
            WorstTradePnl = worst,
            Exposure = exposure,
            Periods = n,
        };
    }

    /// <summary>Statistics from a series of per-period returns (equity starts at 1).</summary>
    public static PerformanceStatistics FromReturns(ReadOnlySpan<double> returns, double periodsPerYear = 252, double riskFreeRate = 0)
    {
        var equity = new double[returns.Length + 1];
        equity[0] = 1;
        for (int i = 0; i < returns.Length; i++) equity[i + 1] = equity[i] * (1 + returns[i]);
        return Compute(equity, periodsPerYear, riskFreeRate);
    }

    public override string ToString() =>
        $"Return {TotalReturn:P2} | CAGR {Cagr:P2} | Vol {AnnualVolatility:P2} | Sharpe {SharpeRatio:F2} | Sortino {SortinoRatio:F2} | " +
        $"MaxDD {MaxDrawdown:P2} | Trades {NumberOfTrades} | Win {WinRate:P1} | PF {ProfitFactor:F2}";
}
