using Finance.Mathematics;

namespace Finance.Portfolio;

/// <summary>
/// An immutable vector of portfolio weights with optional asset names.
/// </summary>
public sealed class PortfolioWeights : IReadOnlyList<double>
{
    private readonly double[] _weights;
    private readonly Dictionary<string, int>? _index;

    public PortfolioWeights(double[] weights, IReadOnlyList<string>? assets = null)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (assets is not null && assets.Count != weights.Length)
            throw new ArgumentException("Asset names and weights differ in length.");
        _weights = (double[])weights.Clone();
        Assets = assets ?? Enumerable.Range(0, weights.Length).Select(i => $"Asset{i + 1}").ToArray();
        _index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < Assets.Count; i++) _index[Assets[i]] = i;
    }

    /// <summary>Asset names (generated as Asset1..AssetN when none were given).</summary>
    public IReadOnlyList<string> Assets { get; }

    /// <summary>A copy of the raw weights.</summary>
    public double[] ToArray() => (double[])_weights.Clone();

    /// <summary>Read-only view of the weights.</summary>
    public ReadOnlySpan<double> AsSpan() => _weights;

    public int Count => _weights.Length;

    public double this[int index] => _weights[index];

    public double this[string asset] => _index!.TryGetValue(asset, out int i) ? _weights[i] : throw new KeyNotFoundException(asset);

    /// <summary>Sum of weights (1 for a fully invested portfolio).</summary>
    public double Sum => Stats.Sum(_weights);

    /// <summary>Sum of absolute weights (gross leverage).</summary>
    public double GrossExposure => _weights.Sum(Math.Abs);

    /// <summary>Asset → weight map.</summary>
    public IReadOnlyDictionary<string, double> ToDictionary()
    {
        var d = new Dictionary<string, double>(Count, StringComparer.Ordinal);
        for (int i = 0; i < Count; i++) d[Assets[i]] = _weights[i];
        return d;
    }

    /// <summary>
    /// Zeroes weights whose magnitude is below <paramref name="cutoff"/>, rounds, and rescales so the sum
    /// is preserved (PyPortfolioOpt's <c>clean_weights</c>).
    /// </summary>
    public PortfolioWeights Clean(double cutoff = 1e-4, int decimals = 5)
    {
        var w = ToArray();
        double before = w.Sum();
        for (int i = 0; i < w.Length; i++) if (Math.Abs(w[i]) < cutoff) w[i] = 0;
        double after = w.Sum();
        if (after != 0 && before != 0)
            for (int i = 0; i < w.Length; i++) w[i] *= before / after;
        for (int i = 0; i < w.Length; i++) w[i] = Math.Round(w[i], decimals);
        return new PortfolioWeights(w, Assets);
    }

    public IEnumerator<double> GetEnumerator() => ((IEnumerable<double>)_weights).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString()
        => string.Join(", ", Assets.Select((a, i) => $"{a}: {_weights[i]:P2}"));
}

/// <summary>Expected return, volatility and Sharpe ratio of a portfolio.</summary>
public readonly record struct PortfolioPerformance(double ExpectedReturn, double Volatility, double SharpeRatio)
{
    /// <summary>Computes the performance of <paramref name="weights"/> under (annualised) μ and Σ.</summary>
    public static PortfolioPerformance Compute(ReadOnlySpan<double> weights, ReadOnlySpan<double> expectedReturns, Matrix covariance, double riskFreeRate = 0)
    {
        double ret = Matrix.Dot(weights, expectedReturns);
        double vol = Math.Sqrt(Math.Max(0, covariance.QuadraticForm(weights)));
        return new PortfolioPerformance(ret, vol, vol > 0 ? (ret - riskFreeRate) / vol : double.NaN);
    }

    public override string ToString() => $"Return {ExpectedReturn:P2}, Volatility {Volatility:P2}, Sharpe {SharpeRatio:F3}";
}

/// <summary>A point on the efficient frontier.</summary>
public sealed record FrontierPoint(double ExpectedReturn, double Volatility, double SharpeRatio, PortfolioWeights Weights);
