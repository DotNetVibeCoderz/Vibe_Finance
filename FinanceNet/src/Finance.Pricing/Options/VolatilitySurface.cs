using Finance.Mathematics;

namespace Finance.Pricing.Options;

/// <summary>A market option quote used to build a surface.</summary>
public readonly record struct OptionQuote(double Expiry, double Strike, OptionType Type, double Price);

/// <summary>
/// Implied-volatility surface on an (expiry × strike) grid. Within an expiry the smile is interpolated
/// in strike (natural cubic spline, flat extrapolation); across expiries total variance σ²T is interpolated
/// linearly, which keeps calendar arbitrage out when the input slices are arbitrage-free.
/// </summary>
public sealed class VolatilitySurface
{
    private readonly double[] _expiries;
    private readonly double[] _strikes;
    private readonly double[,] _vols;
    private readonly IInterpolator[] _smiles;

    /// <param name="expiries">Ascending expiries in years.</param>
    /// <param name="strikes">Ascending strikes.</param>
    /// <param name="vols">vols[i, j] = implied vol for expiry i and strike j.</param>
    public VolatilitySurface(double[] expiries, double[] strikes, double[,] vols)
    {
        if (vols.GetLength(0) != expiries.Length || vols.GetLength(1) != strikes.Length)
            throw new ArgumentException("Vol grid shape does not match expiries × strikes.");
        _expiries = (double[])expiries.Clone();
        _strikes = (double[])strikes.Clone();
        _vols = (double[,])vols.Clone();
        _smiles = new IInterpolator[expiries.Length];
        for (int i = 0; i < expiries.Length; i++)
        {
            var row = new double[strikes.Length];
            for (int j = 0; j < strikes.Length; j++) row[j] = vols[i, j];
            _smiles[i] = strikes.Length >= 3 ? new CubicSplineInterpolator(_strikes, row) : new LinearInterpolator(_strikes, row);
        }
    }

    public IReadOnlyList<double> Expiries => _expiries;
    public IReadOnlyList<double> Strikes => _strikes;

    /// <summary>Grid value.</summary>
    public double GridVol(int expiryIndex, int strikeIndex) => _vols[expiryIndex, strikeIndex];

    /// <summary>Interpolated implied volatility.</summary>
    public double Volatility(double expiry, double strike)
    {
        if (expiry <= _expiries[0] || _expiries.Length == 1) return _smiles[0].Evaluate(strike);
        if (expiry >= _expiries[^1]) return _smiles[^1].Evaluate(strike);
        int i = Array.BinarySearch(_expiries, expiry);
        if (i >= 0) return _smiles[i].Evaluate(strike);
        i = ~i - 1;
        double t0 = _expiries[i], t1 = _expiries[i + 1];
        double v0 = _smiles[i].Evaluate(strike), v1 = _smiles[i + 1].Evaluate(strike);
        double w = (expiry - t0) / (t1 - t0);
        double totalVar = (1 - w) * v0 * v0 * t0 + w * v1 * v1 * t1;
        return Math.Sqrt(totalVar / expiry);
    }

    /// <summary>Smile at an expiry sampled on <paramref name="points"/> strikes across the grid range.</summary>
    public (double[] Strikes, double[] Vols) Smile(double expiry, int points = 41)
    {
        var k = new double[points];
        var v = new double[points];
        double lo = _strikes[0], hi = _strikes[^1];
        for (int i = 0; i < points; i++)
        {
            k[i] = lo + (hi - lo) * i / (points - 1);
            v[i] = Volatility(expiry, k[i]);
        }
        return (k, v);
    }

    /// <summary>
    /// Builds a surface from option prices: each quote is inverted to an implied vol; where a grid cell has
    /// several quotes (e.g. call and put) their vols are averaged. Missing cells are filled by linear interpolation
    /// along the strike axis.
    /// </summary>
    public static VolatilitySurface FromQuotes(double spot, double rate, double dividendYield, IEnumerable<OptionQuote> quotes)
    {
        var list = quotes.ToList();
        var expiries = list.Select(q => q.Expiry).Distinct().Order().ToArray();
        var strikes = list.Select(q => q.Strike).Distinct().Order().ToArray();
        var sum = new double[expiries.Length, strikes.Length];
        var cnt = new int[expiries.Length, strikes.Length];
        foreach (var q in list)
        {
            double iv = BlackScholes.ImpliedVolatility(q.Type, q.Price, spot, q.Strike, q.Expiry, rate, dividendYield);
            if (double.IsNaN(iv) || iv <= 0) continue;
            int i = Array.IndexOf(expiries, q.Expiry), j = Array.IndexOf(strikes, q.Strike);
            sum[i, j] += iv;
            cnt[i, j]++;
        }
        var vols = new double[expiries.Length, strikes.Length];
        for (int i = 0; i < expiries.Length; i++)
        {
            var xs = new List<double>();
            var ys = new List<double>();
            for (int j = 0; j < strikes.Length; j++)
                if (cnt[i, j] > 0) { xs.Add(strikes[j]); ys.Add(sum[i, j] / cnt[i, j]); }
            if (xs.Count == 0) throw new ArgumentException($"No valid quotes for expiry {expiries[i]}.");
            IInterpolator? fill = xs.Count >= 2 ? new LinearInterpolator(xs.ToArray(), ys.ToArray()) : null;
            for (int j = 0; j < strikes.Length; j++)
                vols[i, j] = cnt[i, j] > 0 ? sum[i, j] / cnt[i, j] : fill?.Evaluate(strikes[j]) ?? ys[0];
        }
        return new VolatilitySurface(expiries, strikes, vols);
    }
}
