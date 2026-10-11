using Finance.Mathematics;

namespace Finance.Risk;

/// <summary>Correlation scenario of the FRTB sensitivities-based method (MAR21.6).</summary>
public enum FrtbCorrelationScenario { Low, Medium, High }

/// <summary>One general-interest-rate-risk delta sensitivity: PV change for +1bp, divided by 0.0001.</summary>
/// <param name="Currency">The bucket (each currency is its own GIRR bucket).</param>
/// <param name="TenorYears">Vertex: 0.25, 0.5, 1, 2, 3, 5, 10, 15, 20 or 30.</param>
/// <param name="Sensitivity">sₖ = (V(r + 1bp) − V(r)) / 0.0001, in currency.</param>
/// <param name="Curve">Curve name; two curves of one currency correlate at 99.9% of the tenor correlation.</param>
public sealed record GirrSensitivity(string Currency, double TenorYears, double Sensitivity, string Curve = "OIS");

/// <summary>Result of an FRTB delta charge.</summary>
/// <param name="Charge">The capital requirement: the largest of the three correlation scenarios.</param>
/// <param name="Binding">The scenario that produced <paramref name="Charge"/>.</param>
/// <param name="ByScenario">Charge under each scenario.</param>
/// <param name="BucketCapital">Kᵦ per bucket under the binding scenario.</param>
public sealed record FrtbDeltaResult(double Charge, FrtbCorrelationScenario Binding,
    IReadOnlyDictionary<FrtbCorrelationScenario, double> ByScenario, IReadOnlyDictionary<string, double> BucketCapital);

/// <summary>
/// FRTB standardised approach, sensitivities-based method: delta aggregation (Basel MAR21).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Aggregate"/> is the generic engine: weighted sensitivities, the intra-bucket correlation
/// <c>Kᵦ = √max(0, Σ WSₖ² + Σₖ≠ₗ ρₖₗ WSₖ WSₗ)</c>, then across buckets
/// <c>√(Σ Kᵦ² + Σᵦ≠꜀ γᵦ꜀ Sᵦ S꜀)</c> with Sᵦ = Σ WSₖ, or Sᵦ capped to [−Kᵦ, Kᵦ] when that sum would be negative.
/// It is evaluated under the medium, high (min(1.25ρ, 1)) and low (max(2ρ − 1, 0.75ρ)) correlation scenarios,
/// and the largest result is the charge.
/// </para>
/// <para>
/// <see cref="GirrDelta"/> supplies the GIRR delta parameters as written in MAR21 (January 2019 text): risk weights
/// 1.7% (0.25y, 0.5y), 1.6% (1y), 1.3% (2y), 1.2% (3y) and 1.1% (5y and beyond), divided by √2 for specified
/// currencies; ρ = max(exp(−3%·|Tₖ − Tₗ| / min(Tₖ, Tₗ)), 40%); γ = 50% across currencies. Check the
/// parameters against the text your supervisor applies before using the numbers for regulatory capital.
/// </para>
/// </remarks>
public static class FrtbSa
{
    /// <summary>GIRR delta vertices in years.</summary>
    public static IReadOnlyList<double> GirrTenors { get; } = new[] { 0.25, 0.5, 1, 2, 3, 5, 10, 15, 20, 30 };

    /// <summary>Currencies whose GIRR risk weights may be divided by √2 (MAR21.44), besides the domestic currency.</summary>
    public static IReadOnlySet<string> SpecifiedCurrencies { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "EUR", "USD", "GBP", "AUD", "JPY", "SEK", "CAD" };

    /// <summary>GIRR delta risk weight for a vertex (MAR21.42).</summary>
    public static double GirrRiskWeight(double tenorYears) => tenorYears switch
    {
        <= 0.5 => 0.017,
        <= 1 => 0.016,
        <= 2 => 0.013,
        <= 3 => 0.012,
        _ => 0.011,
    };

    /// <summary>GIRR delta correlation between two vertices of the same currency (MAR21.46-47).</summary>
    public static double GirrCorrelation(double tenorK, double tenorL, bool sameCurve = true)
    {
        double rho = Math.Max(Math.Exp(-0.03 * Math.Abs(tenorK - tenorL) / Math.Min(tenorK, tenorL)), 0.40);
        return sameCurve ? rho : 0.999 * rho;
    }

    /// <summary>Correlation under a scenario (MAR21.6).</summary>
    public static double Scenario(double correlation, FrtbCorrelationScenario scenario) => scenario switch
    {
        FrtbCorrelationScenario.High => Math.Min(1.25 * correlation, 1),
        FrtbCorrelationScenario.Low => Math.Max(2 * correlation - 1, 0.75 * correlation),
        _ => correlation,
    };

    /// <summary>
    /// GIRR delta charge for a set of sensitivities. Sensitivities on the same (currency, curve, tenor) are netted
    /// first. Pass <paramref name="domesticCurrency"/> so that it also gets the √2 reduction.
    /// </summary>
    public static FrtbDeltaResult GirrDelta(IEnumerable<GirrSensitivity> sensitivities, string? domesticCurrency = null)
    {
        var netted = sensitivities
            .GroupBy(s => (Ccy: s.Currency.ToUpperInvariant(), s.Curve, s.TenorYears))
            .Select(g => new GirrSensitivity(g.Key.Ccy, g.Key.TenorYears, g.Sum(s => s.Sensitivity), g.Key.Curve))
            .ToList();
        var buckets = netted.GroupBy(s => s.Currency).Select(g =>
        {
            var items = g.ToList();
            bool specified = SpecifiedCurrencies.Contains(g.Key) || string.Equals(g.Key, domesticCurrency, StringComparison.OrdinalIgnoreCase);
            var ws = items.Select(s => GirrRiskWeight(s.TenorYears) / (specified ? Math.Sqrt(2) : 1) * s.Sensitivity).ToArray();
            var rho = new Matrix(items.Count, items.Count);
            for (int k = 0; k < items.Count; k++)
                for (int l = 0; l < items.Count; l++)
                    rho[k, l] = k == l ? 1 : GirrCorrelation(items[k].TenorYears, items[l].TenorYears, items[k].Curve == items[l].Curve);
            return (Name: g.Key, Ws: ws, Rho: rho);
        }).ToList();
        return Aggregate(buckets, (_, _) => 0.50);
    }

    /// <summary>
    /// The generic delta aggregation: weighted sensitivities and intra-bucket correlations per bucket, and
    /// the cross-bucket correlation γ. The result is the maximum over the three correlation scenarios.
    /// </summary>
    public static FrtbDeltaResult Aggregate(IReadOnlyList<(string Name, double[] Ws, Matrix Rho)> buckets, Func<string, string, double> gamma)
    {
        var byScenario = new Dictionary<FrtbCorrelationScenario, double>();
        var capitalByScenario = new Dictionary<FrtbCorrelationScenario, Dictionary<string, double>>();
        foreach (var sc in Enum.GetValues<FrtbCorrelationScenario>())
        {
            var k = new double[buckets.Count];
            var sum = new double[buckets.Count];
            var caps = new Dictionary<string, double>();
            for (int b = 0; b < buckets.Count; b++)
            {
                var (name, ws, rho) = buckets[b];
                double v = 0;
                for (int i = 0; i < ws.Length; i++)
                {
                    v += ws[i] * ws[i];
                    for (int j = 0; j < ws.Length; j++)
                        if (i != j) v += Scenario(rho[i, j], sc) * ws[i] * ws[j];
                }
                k[b] = Math.Sqrt(Math.Max(0, v));
                sum[b] = ws.Sum();
                caps[name] = k[b];
            }

            double total = Across(k, sum, buckets, gamma, sc);
            if (total < 0)
            {
                // MAR21.4(5)(b): the alternative specification caps each Sᵦ to [−Kᵦ, Kᵦ].
                for (int b = 0; b < sum.Length; b++) sum[b] = Math.Max(Math.Min(sum[b], k[b]), -k[b]);
                total = Across(k, sum, buckets, gamma, sc);
            }
            byScenario[sc] = Math.Sqrt(Math.Max(0, total));
            capitalByScenario[sc] = caps;
        }
        var binding = byScenario.MaxBy(p => p.Value).Key;
        return new FrtbDeltaResult(byScenario[binding], binding, byScenario, capitalByScenario[binding]);
    }

    private static double Across(double[] k, double[] s, IReadOnlyList<(string Name, double[] Ws, Matrix Rho)> buckets,
        Func<string, string, double> gamma, FrtbCorrelationScenario sc)
    {
        double total = 0;
        for (int b = 0; b < k.Length; b++)
        {
            total += k[b] * k[b];
            for (int c = 0; c < k.Length; c++)
                if (b != c) total += Scenario(gamma(buckets[b].Name, buckets[c].Name), sc) * s[b] * s[c];
        }
        return total;
    }
}
