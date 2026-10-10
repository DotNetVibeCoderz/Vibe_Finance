namespace Finance.Risk;

/// <summary>Basel traffic-light zone for a 250-day, 99% VaR backtest.</summary>
public enum BaselZone { Green, Yellow, Red }

/// <summary>Result of a VaR backtest.</summary>
/// <param name="Observations">Number of periods tested.</param>
/// <param name="Exceptions">Periods where the loss exceeded VaR.</param>
/// <param name="ExpectedExceptions">Observations × (1 - confidence).</param>
/// <param name="KupiecStatistic">Kupiec proportion-of-failures likelihood ratio (χ²₁).</param>
/// <param name="KupiecPValue">p-value of the Kupiec test.</param>
/// <param name="ChristoffersenStatistic">Christoffersen independence likelihood ratio (χ²₁).</param>
/// <param name="ChristoffersenPValue">p-value of the independence test.</param>
/// <param name="ConditionalCoverageStatistic">Kupiec + independence (χ²₂).</param>
/// <param name="ConditionalCoveragePValue">p-value of the conditional-coverage test.</param>
/// <param name="Zone">Basel traffic-light zone (scaled to 250 observations).</param>
public sealed record VarBacktestResult(int Observations, int Exceptions, double ExpectedExceptions,
    double KupiecStatistic, double KupiecPValue, double ChristoffersenStatistic, double ChristoffersenPValue,
    double ConditionalCoverageStatistic, double ConditionalCoveragePValue, BaselZone Zone)
{
    /// <summary>True when neither coverage nor independence is rejected at <paramref name="significance"/>.</summary>
    public bool Passes(double significance = 0.05) => KupiecPValue > significance && ChristoffersenPValue > significance;
}

/// <summary>Statistical VaR backtests (Kupiec 1995, Christoffersen 1998).</summary>
public static class VarBacktest
{
    /// <summary>
    /// Backtests VaR forecasts against realised returns. <paramref name="varForecasts"/> are positive loss
    /// fractions; an exception is a realised return below -VaR.
    /// </summary>
    public static VarBacktestResult Run(ReadOnlySpan<double> returns, ReadOnlySpan<double> varForecasts, double confidence = 0.99)
    {
        Finance.Core.Guard.SameLength(returns.Length, varForecasts.Length, "returns and VaR forecasts");
        var hits = new bool[returns.Length];
        for (int i = 0; i < hits.Length; i++) hits[i] = returns[i] < -varForecasts[i];
        return FromExceptions(hits, confidence);
    }

    /// <summary>Backtests a constant VaR.</summary>
    public static VarBacktestResult Run(ReadOnlySpan<double> returns, double var, double confidence = 0.99)
    {
        var hits = new bool[returns.Length];
        for (int i = 0; i < hits.Length; i++) hits[i] = returns[i] < -var;
        return FromExceptions(hits, confidence);
    }

    /// <summary>Runs the tests on an exception indicator sequence.</summary>
    public static VarBacktestResult FromExceptions(IReadOnlyList<bool> hits, double confidence = 0.99)
    {
        int t = hits.Count;
        int x = hits.Count(h => h);
        double p = 1 - confidence;
        double lrPof = Kupiec(t, x, p);

        int n00 = 0, n01 = 0, n10 = 0, n11 = 0;
        for (int i = 1; i < t; i++)
        {
            bool prev = hits[i - 1], cur = hits[i];
            if (!prev && !cur) n00++;
            else if (!prev && cur) n01++;
            else if (prev && !cur) n10++;
            else n11++;
        }
        double pi0 = n00 + n01 > 0 ? (double)n01 / (n00 + n01) : 0;
        double pi1 = n10 + n11 > 0 ? (double)n11 / (n10 + n11) : 0;
        double pi = (double)(n01 + n11) / Math.Max(1, n00 + n01 + n10 + n11);
        double lrInd = -2 * (XLogY(n00 + n10, 1 - pi) + XLogY(n01 + n11, pi))
                       + 2 * (XLogY(n00, 1 - pi0) + XLogY(n01, pi0) + XLogY(n10, 1 - pi1) + XLogY(n11, pi1));
        lrInd = Math.Max(lrInd, 0);
        double lrCc = lrPof + lrInd;

        double scaled = x * 250.0 / Math.Max(1, t);
        var zone = scaled <= 4.5 ? BaselZone.Green : scaled < 9.5 ? BaselZone.Yellow : BaselZone.Red;
        return new VarBacktestResult(t, x, t * p, lrPof, ChiSquare1PValue(lrPof), lrInd, ChiSquare1PValue(lrInd), lrCc, Math.Exp(-lrCc / 2), zone);
    }

    /// <summary>Kupiec proportion-of-failures likelihood ratio.</summary>
    public static double Kupiec(int observations, int exceptions, double expectedRate)
    {
        int t = observations, x = exceptions;
        double phat = (double)x / t;
        double lr = -2 * (XLogY(t - x, 1 - expectedRate) + XLogY(x, expectedRate))
                    + 2 * (XLogY(t - x, 1 - phat) + XLogY(x, phat));
        return Math.Max(lr, 0);
    }

    /// <summary>Upper-tail probability of a χ² variable with one degree of freedom.</summary>
    public static double ChiSquare1PValue(double statistic) => statistic <= 0 ? 1 : Finance.Mathematics.Normal.Erfc(Math.Sqrt(statistic / 2));

    private static double XLogY(double x, double y) => x == 0 ? 0 : x * Math.Log(y);
}
