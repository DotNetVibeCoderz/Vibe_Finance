namespace Finance.RustBridge;

/// <summary>Maximum drawdown as a positive fraction, with the peak and trough positions.</summary>
public readonly record struct DrawdownInfo(double Depth, int PeakIndex, int TroughIndex);

/// <summary>Historical tail risk: VaR and CVaR (expected shortfall), both as positive losses.</summary>
public readonly record struct TailRisk(double VaR, double CVaR);

/// <summary>A Monte Carlo price estimate with its standard error.</summary>
public readonly record struct MonteCarloEstimate(double Price, double StdError, long Paths)
{
    /// <summary>95% confidence interval half-width.</summary>
    public double HalfWidth95 => 1.959963984540054 * StdError;
}

/// <summary>Outputs of a batch Black-Scholes run (one entry per option).</summary>
public sealed class BlackScholesBatchResult
{
    public BlackScholesBatchResult(int n)
    {
        Price = new double[n];
        Delta = new double[n];
        Gamma = new double[n];
        Vega = new double[n];
        Theta = new double[n];
        Rho = new double[n];
    }

    public double[] Price { get; }
    public double[] Delta { get; }
    public double[] Gamma { get; }
    /// <summary>Per 1.00 change in volatility.</summary>
    public double[] Vega { get; }
    /// <summary>Per year.</summary>
    public double[] Theta { get; }
    /// <summary>Per 1.00 change in the rate.</summary>
    public double[] Rho { get; }
}

/// <summary>Inputs of a batch Black-Scholes run as parallel arrays.</summary>
public readonly ref struct OptionBatch
{
    public OptionBatch(ReadOnlySpan<double> spot, ReadOnlySpan<double> strike, ReadOnlySpan<double> time,
        ReadOnlySpan<double> rate, ReadOnlySpan<double> dividend, ReadOnlySpan<double> volatility, ReadOnlySpan<bool> isCall)
    {
        int n = spot.Length;
        if (strike.Length != n || time.Length != n || rate.Length != n || dividend.Length != n || volatility.Length != n || isCall.Length != n)
            throw new ArgumentException("All option batch arrays must have the same length.");
        Spot = spot;
        Strike = strike;
        Time = time;
        Rate = rate;
        Dividend = dividend;
        Volatility = volatility;
        IsCall = isCall;
    }

    public ReadOnlySpan<double> Spot { get; }
    public ReadOnlySpan<double> Strike { get; }
    public ReadOnlySpan<double> Time { get; }
    public ReadOnlySpan<double> Rate { get; }
    public ReadOnlySpan<double> Dividend { get; }
    public ReadOnlySpan<double> Volatility { get; }
    public ReadOnlySpan<bool> IsCall { get; }
    public int Count => Spot.Length;
}

/// <summary>Monte Carlo European option inputs.</summary>
public readonly record struct EuropeanOptionSpec(double Spot, double Strike, double Time, double Rate, double Dividend, double Volatility, bool IsCall)
{
    internal void Validate()
    {
        if (!(Spot > 0) || !(Strike > 0) || !(Time > 0) || !(Volatility >= 0))
            throw new ArgumentException("Spot, strike and time must be positive and volatility non-negative.");
    }
}
