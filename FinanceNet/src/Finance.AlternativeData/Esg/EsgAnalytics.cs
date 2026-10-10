namespace Finance.AlternativeData.Esg;

/// <summary>
/// ESG profile of an issuer. Pillar scores are on a 0-100 scale (higher = better);
/// <see cref="Controversy"/> is 0 (none) to 5 (severe); carbon intensity is tCO2e per USD 1M revenue.
/// </summary>
public sealed record EsgScore(
    string Symbol,
    double Environmental,
    double Social,
    double Governance,
    int Controversy = 0,
    double CarbonIntensity = double.NaN,
    string? Sector = null)
{
    /// <summary>Pillar-weighted total (default 1/3 each).</summary>
    public double Total(double wE = 1.0 / 3, double wS = 1.0 / 3, double wG = 1.0 / 3)
        => (wE * Environmental + wS * Social + wG * Governance) / (wE + wS + wG);
}

/// <summary>Portfolio-level ESG result.</summary>
public sealed record PortfolioEsg(double Environmental, double Social, double Governance, double Total, double Waci, double Coverage);

/// <summary>A controversy or threshold breach.</summary>
public sealed record EsgFlag(string Symbol, string Reason, double Weight);

/// <summary>ESG portfolio analytics: weighted scores, WACI, screens and flags.</summary>
public static class EsgAnalytics
{
    /// <summary>
    /// Weighted pillar scores and weighted-average carbon intensity (WACI, the TCFD metric).
    /// Weights are renormalised over holdings that have data; <see cref="PortfolioEsg.Coverage"/> is the
    /// share of (absolute) weight covered.
    /// </summary>
    public static PortfolioEsg Portfolio(IReadOnlyDictionary<string, double> weights, IReadOnlyDictionary<string, EsgScore> scores)
    {
        double total = weights.Values.Sum(Math.Abs), covered = 0, e = 0, s = 0, g = 0, waci = 0, carbonW = 0;
        foreach (var (sym, w0) in weights)
        {
            if (!scores.TryGetValue(sym, out var sc)) continue;
            double w = Math.Abs(w0);
            covered += w;
            e += w * sc.Environmental;
            s += w * sc.Social;
            g += w * sc.Governance;
            if (!double.IsNaN(sc.CarbonIntensity))
            {
                waci += w * sc.CarbonIntensity;
                carbonW += w;
            }
        }
        if (covered == 0) return new PortfolioEsg(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 0);
        e /= covered; s /= covered; g /= covered;
        return new PortfolioEsg(e, s, g, (e + s + g) / 3, carbonW > 0 ? waci / carbonW : double.NaN, total > 0 ? covered / total : 0);
    }

    /// <summary>Holdings breaching the controversy level or carbon-intensity ceiling.</summary>
    public static IReadOnlyList<EsgFlag> Flags(IReadOnlyDictionary<string, double> weights, IReadOnlyDictionary<string, EsgScore> scores,
        int maxControversy = 3, double maxCarbonIntensity = 500, double minTotal = 30)
    {
        var flags = new List<EsgFlag>();
        foreach (var (sym, w) in weights)
        {
            if (!scores.TryGetValue(sym, out var sc))
            {
                flags.Add(new EsgFlag(sym, "No ESG coverage", w));
                continue;
            }
            if (sc.Controversy > maxControversy) flags.Add(new EsgFlag(sym, $"Controversy level {sc.Controversy}", w));
            if (sc.CarbonIntensity > maxCarbonIntensity) flags.Add(new EsgFlag(sym, $"Carbon intensity {sc.CarbonIntensity:F0} > {maxCarbonIntensity:F0}", w));
            if (sc.Total() < minTotal) flags.Add(new EsgFlag(sym, $"ESG total {sc.Total():F1} < {minTotal:F0}", w));
        }
        return flags;
    }

    /// <summary>
    /// Best-in-class screen: keeps issuers in the top <paramref name="keepFraction"/> of their sector
    /// by total score, and excludes any with controversy above <paramref name="maxControversy"/>.
    /// </summary>
    public static IReadOnlyList<string> BestInClass(IEnumerable<EsgScore> universe, double keepFraction = 0.5, int maxControversy = 4)
    {
        return universe.Where(s => s.Controversy <= maxControversy)
            .GroupBy(s => s.Sector ?? "")
            .SelectMany(g =>
            {
                var ranked = g.OrderByDescending(s => s.Total()).ToList();
                int keep = Math.Max(1, (int)Math.Ceiling(ranked.Count * keepFraction));
                return ranked.Take(keep);
            })
            .Select(s => s.Symbol)
            .ToList();
    }

    /// <summary>
    /// Tilts weights towards better ESG: w_i ∝ w_i · exp(strength · z_i), z = cross-sectional z-score of the total.
    /// The result sums to the same total as the input.
    /// </summary>
    public static Dictionary<string, double> Tilt(IReadOnlyDictionary<string, double> weights, IReadOnlyDictionary<string, EsgScore> scores, double strength = 0.5)
    {
        var totals = weights.Keys.Where(scores.ContainsKey).ToDictionary(k => k, k => scores[k].Total());
        double mean = totals.Count > 0 ? totals.Values.Average() : 0;
        double sd = totals.Count > 1 ? Math.Sqrt(totals.Values.Sum(v => (v - mean) * (v - mean)) / (totals.Count - 1)) : 0;
        var raw = weights.ToDictionary(kv => kv.Key, kv =>
        {
            double z = totals.TryGetValue(kv.Key, out var t) && sd > 0 ? (t - mean) / sd : 0;
            return kv.Value * Math.Exp(strength * z);
        });
        double before = weights.Values.Sum(), after = raw.Values.Sum();
        return raw.ToDictionary(kv => kv.Key, kv => after != 0 ? kv.Value * before / after : kv.Value);
    }
}
