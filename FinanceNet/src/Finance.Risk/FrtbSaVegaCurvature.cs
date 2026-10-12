using Finance.Mathematics;

namespace Finance.Risk;

/// <summary>
/// Vega risk-weight class of MAR21.92: the regulatory liquidity horizon LH that scales the vega risk weight.
/// </summary>
public enum FrtbVegaClass
{
    /// <summary>General interest rate risk, LH = 60.</summary>
    Girr,
    /// <summary>Credit spread risk, non-securitisations, LH = 120.</summary>
    CsrNonSecuritisation,
    /// <summary>Credit spread risk, securitisations in the correlation trading portfolio, LH = 120.</summary>
    CsrSecuritisationCtp,
    /// <summary>Credit spread risk, securitisations outside the correlation trading portfolio, LH = 120.</summary>
    CsrSecuritisationNonCtp,
    /// <summary>Equity, large cap and indices, LH = 20.</summary>
    EquityLargeCap,
    /// <summary>Equity, small cap and other, LH = 60.</summary>
    EquitySmallCap,
    /// <summary>Commodity, LH = 120.</summary>
    Commodity,
    /// <summary>Foreign exchange, LH = 40.</summary>
    Fx,
}

/// <summary>Curvature shock direction selected for a bucket (MAR21.5(4)).</summary>
public enum FrtbCurvatureShock { Up, Down }

/// <summary>One GIRR vega sensitivity (MAR21.25): vega × implied volatility, VRₖ = ∂V/∂σ · σ.</summary>
/// <param name="Currency">The bucket (each currency is its own GIRR bucket).</param>
/// <param name="OptionMaturityYears">Option-maturity vertex: 0.5, 1, 3, 5 or 10.</param>
/// <param name="UnderlyingMaturityYears">Residual maturity of the underlying at the option's expiry: 0.5, 1, 3, 5 or 10.</param>
/// <param name="Sensitivity">VRₖ = vega × implied volatility, in currency.</param>
public sealed record GirrVegaSensitivity(string Currency, double OptionMaturityYears, double UnderlyingMaturityYears, double Sensitivity);

/// <summary>
/// One GIRR curvature input (MAR21.5): the full revaluation of an instrument (or book) under a parallel shift of
/// all curves of one currency by ±RW, and its delta to that parallel shift.
/// </summary>
/// <param name="Currency">The bucket and the single curvature risk factor of that currency (MAR21.8).</param>
/// <param name="UpChange">V(x + RW) − V(x): PV change for the upward parallel shift.</param>
/// <param name="DownChange">V(x − RW) − V(x): PV change for the downward parallel shift.</param>
/// <param name="Delta">Σ of the GIRR delta sensitivities of the instrument to the curves of that currency
/// (the same convention as <see cref="GirrSensitivity.Sensitivity"/>: PV change per unit rate move).</param>
public sealed record GirrCurvatureSensitivity(string Currency, double UpChange, double DownChange, double Delta);

/// <summary>Result of an FRTB curvature charge.</summary>
/// <param name="Charge">The largest of the three correlation scenarios.</param>
/// <param name="Binding">The scenario that produced <paramref name="Charge"/>.</param>
/// <param name="ByScenario">Charge under each scenario.</param>
/// <param name="BucketCapital">Kᵦ = max(Kᵦ⁺, Kᵦ⁻) per bucket under the binding scenario.</param>
/// <param name="Selected">The shock direction chosen for each bucket under the binding scenario.</param>
public sealed record FrtbCurvatureResult(double Charge, FrtbCorrelationScenario Binding,
    IReadOnlyDictionary<FrtbCorrelationScenario, double> ByScenario, IReadOnlyDictionary<string, double> BucketCapital,
    IReadOnlyDictionary<string, FrtbCurvatureShock> Selected);

/// <summary>Sensitivities-based capital (MAR21.7): delta + vega + curvature summed per scenario, then the maximum.</summary>
/// <param name="Charge">max over scenarios of the summed components.</param>
/// <param name="Binding">The scenario that produced <paramref name="Charge"/>.</param>
/// <param name="ByScenario">Sum of all components under each scenario.</param>
public sealed record FrtbSbmResult(double Charge, FrtbCorrelationScenario Binding, IReadOnlyDictionary<FrtbCorrelationScenario, double> ByScenario);

// Vega (MAR21.4, MAR21.25, MAR21.92-95) and curvature (MAR21.5, MAR21.96-101).
public static partial class FrtbSa
{
    /// <summary>Vega option-maturity vertices (and, for GIRR, underlying residual-maturity vertices) in years (MAR21.25).</summary>
    public static IReadOnlyList<double> VegaTenors { get; } = new[] { 0.5, 1, 3, 5, 10 };

    /// <summary>RWσ of MAR21.92: 55%.</summary>
    public const double VegaBaseRiskWeight = 0.55;

    /// <summary>Regulatory liquidity horizon LH of a vega class, in days (MAR21.92).</summary>
    public static int VegaLiquidityHorizon(FrtbVegaClass vegaClass) => vegaClass switch
    {
        FrtbVegaClass.Girr => 60,
        FrtbVegaClass.EquityLargeCap => 20,
        FrtbVegaClass.EquitySmallCap => 60,
        FrtbVegaClass.Fx => 40,
        _ => 120,
    };

    /// <summary>Vega risk weight min(RWσ · √(LH / 10), 100%) (MAR21.92). For GIRR 0.55·√6 ≈ 1.347, capped to 100%.</summary>
    public static double VegaRiskWeight(FrtbVegaClass vegaClass)
        => Math.Min(VegaBaseRiskWeight * Math.Sqrt(VegaLiquidityHorizon(vegaClass) / 10.0), 1.0);

    /// <summary>Vega maturity correlation exp(−1%·|Tₖ − Tₗ| / min(Tₖ, Tₗ)) (MAR21.93-94).</summary>
    public static double VegaMaturityCorrelation(double tenorK, double tenorL)
        => Math.Exp(-0.01 * Math.Abs(tenorK - tenorL) / Math.Min(tenorK, tenorL));

    /// <summary>
    /// GIRR vega intra-bucket correlation (MAR21.93): min(ρ(option maturity) · ρ(underlying maturity), 1),
    /// each factor <see cref="VegaMaturityCorrelation"/>.
    /// </summary>
    public static double GirrVegaCorrelation(double optionK, double optionL, double underlyingK, double underlyingL)
        => Math.Min(VegaMaturityCorrelation(optionK, optionL) * VegaMaturityCorrelation(underlyingK, underlyingL), 1.0);

    /// <summary>
    /// Vega intra-bucket correlation for the other risk classes (MAR21.94): min(ρ(delta) · ρ(option maturity), 1),
    /// where ρ(delta) is the delta correlation of the two underlyings. Use it to build buckets for <see cref="Aggregate"/>.
    /// </summary>
    public static double VegaCorrelation(double deltaCorrelation, double optionK, double optionL)
        => Math.Min(deltaCorrelation * VegaMaturityCorrelation(optionK, optionL), 1.0);

    /// <summary>
    /// GIRR vega charge. Sensitivities on the same (currency, option maturity, underlying maturity) are netted first;
    /// RW = <see cref="VegaRiskWeight"/>(Girr) = 100% (no √2 reduction, which applies to delta only); γ = 50% (MAR21.95).
    /// </summary>
    public static FrtbDeltaResult GirrVega(IEnumerable<GirrVegaSensitivity> sensitivities)
    {
        double rw = VegaRiskWeight(FrtbVegaClass.Girr);
        var buckets = sensitivities
            .GroupBy(s => (Ccy: s.Currency.ToUpperInvariant(), s.OptionMaturityYears, s.UnderlyingMaturityYears))
            .Select(g => new GirrVegaSensitivity(g.Key.Ccy, g.Key.OptionMaturityYears, g.Key.UnderlyingMaturityYears, g.Sum(s => s.Sensitivity)))
            .GroupBy(s => s.Currency)
            .Select(g =>
            {
                var items = g.ToList();
                var ws = items.Select(s => rw * s.Sensitivity).ToArray();
                var rho = new Matrix(items.Count, items.Count);
                for (int k = 0; k < items.Count; k++)
                    for (int l = 0; l < items.Count; l++)
                        rho[k, l] = k == l ? 1 : GirrVegaCorrelation(items[k].OptionMaturityYears, items[l].OptionMaturityYears,
                            items[k].UnderlyingMaturityYears, items[l].UnderlyingMaturityYears);
                return (Name: g.Key, Ws: ws, Rho: rho);
            }).ToList();
        return Aggregate(buckets, (_, _) => 0.50);
    }

    /// <summary>GIRR curvature risk weight: the parallel shift by the highest GIRR delta risk weight, 1.7% (MAR21.98).</summary>
    public const double GirrCurvatureRiskWeight = 0.017;

    /// <summary>
    /// Curvature risk exposures of one risk factor (MAR21.5(2)):
    /// CVR⁺ = −(V(x + RW) − V(x) − RW·s) and CVR⁻ = −(V(x − RW) − V(x) + RW·s).
    /// </summary>
    /// <param name="upChange">V(x + RW) − V(x).</param>
    /// <param name="downChange">V(x − RW) − V(x).</param>
    /// <param name="riskWeight">The curvature shock size RW.</param>
    /// <param name="delta">The delta sensitivity s to the risk factor.</param>
    public static (double Up, double Down) CurvatureExposure(double upChange, double downChange, double riskWeight, double delta)
        => (-(upChange - riskWeight * delta), -(downChange + riskWeight * delta));

    /// <summary>
    /// GIRR curvature charge. One risk factor per currency (all curves shifted in parallel, MAR21.8); inputs of one
    /// currency are summed into one CVR⁺ / CVR⁻ pair; γ = 50%² = 25% (MAR21.100-101).
    /// </summary>
    /// <param name="inputs">Shocked PV changes and deltas.</param>
    /// <param name="riskWeight">Shock size used for <see cref="GirrCurvatureSensitivity.UpChange"/> /
    /// <see cref="GirrCurvatureSensitivity.DownChange"/>; defaults to <see cref="GirrCurvatureRiskWeight"/>.</param>
    public static FrtbCurvatureResult GirrCurvature(IEnumerable<GirrCurvatureSensitivity> inputs, double riskWeight = GirrCurvatureRiskWeight)
    {
        var buckets = inputs.GroupBy(s => s.Currency.ToUpperInvariant()).Select(g =>
        {
            double up = 0, down = 0;
            foreach (var s in g)
            {
                var (u, d) = CurvatureExposure(s.UpChange, s.DownChange, riskWeight, s.Delta);
                up += u;
                down += d;
            }
            return (Name: g.Key, Up: new[] { up }, Down: new[] { down }, Rho: Matrix.Identity(1));
        }).ToList();
        return AggregateCurvature(buckets, (_, _) => 0.50 * 0.50);
    }

    /// <summary>
    /// The generic curvature aggregation (MAR21.5(4)-(5)). Pass the curvature correlations, i.e. the squared delta
    /// correlations ρ² and γ² (MAR21.100). Per bucket and scenario
    /// <c>Kᵦ± = √max(0, Σ max(CVRₖ±, 0)² + Σₖ≠ₗ ρₖₗ CVRₖ± CVRₗ± ψ(CVRₖ±, CVRₗ±))</c> with ψ = 0 when both CVRs are
    /// negative; the larger of Kᵦ⁺ and Kᵦ⁻ is selected (on a tie, the direction with the larger Σ CVR). Across buckets
    /// <c>√max(0, Σ Kᵦ² + Σᵦ≠꜀ γᵦ꜀ Sᵦ S꜀ ψ(Sᵦ, S꜀))</c> with Sᵦ = Σ CVRₖ of the selected direction; as for delta, when
    /// the inner sum is negative Sᵦ is capped to [−Kᵦ, Kᵦ]. The result is the maximum over the three scenarios.
    /// </summary>
    public static FrtbCurvatureResult AggregateCurvature(
        IReadOnlyList<(string Name, double[] Up, double[] Down, Matrix Rho)> buckets, Func<string, string, double> gamma)
    {
        var byScenario = new Dictionary<FrtbCorrelationScenario, double>();
        var capital = new Dictionary<FrtbCorrelationScenario, Dictionary<string, double>>();
        var selected = new Dictionary<FrtbCorrelationScenario, Dictionary<string, FrtbCurvatureShock>>();
        foreach (var sc in Enum.GetValues<FrtbCorrelationScenario>())
        {
            var k = new double[buckets.Count];
            var s = new double[buckets.Count];
            var caps = new Dictionary<string, double>();
            var dirs = new Dictionary<string, FrtbCurvatureShock>();
            for (int b = 0; b < buckets.Count; b++)
            {
                var (name, up, down, rho) = buckets[b];
                double kUp = CurvatureBucket(up, rho, sc), kDown = CurvatureBucket(down, rho, sc);
                bool chooseUp = kUp > kDown || (kUp == kDown && up.Sum() > down.Sum());
                k[b] = chooseUp ? kUp : kDown;
                s[b] = chooseUp ? up.Sum() : down.Sum();
                caps[name] = k[b];
                dirs[name] = chooseUp ? FrtbCurvatureShock.Up : FrtbCurvatureShock.Down;
            }

            double total = CurvatureAcross(k, s, buckets, gamma, sc);
            if (total < 0)
            {
                for (int b = 0; b < s.Length; b++) s[b] = Math.Max(Math.Min(s[b], k[b]), -k[b]);
                total = CurvatureAcross(k, s, buckets, gamma, sc);
            }
            byScenario[sc] = Math.Sqrt(Math.Max(0, total));
            capital[sc] = caps;
            selected[sc] = dirs;
        }
        var binding = byScenario.MaxBy(p => p.Value).Key;
        return new FrtbCurvatureResult(byScenario[binding], binding, byScenario, capital[binding], selected[binding]);
    }

    /// <summary>
    /// Total sensitivities-based capital (MAR21.7): the <c>ByScenario</c> maps of every delta, vega and curvature
    /// charge (any risk class) are summed scenario by scenario, and the largest sum is the capital requirement.
    /// </summary>
    public static FrtbSbmResult SensitivitiesBasedCharge(params IReadOnlyDictionary<FrtbCorrelationScenario, double>[] components)
    {
        var byScenario = Enum.GetValues<FrtbCorrelationScenario>()
            .ToDictionary(sc => sc, sc => components.Sum(c => c[sc]));
        var binding = byScenario.MaxBy(p => p.Value).Key;
        return new FrtbSbmResult(byScenario[binding], binding, byScenario);
    }

    private static double Psi(double a, double b) => a < 0 && b < 0 ? 0 : 1;

    private static double CurvatureBucket(double[] cvr, Matrix rho, FrtbCorrelationScenario sc)
    {
        double v = 0;
        for (int i = 0; i < cvr.Length; i++)
        {
            double pos = Math.Max(cvr[i], 0);
            v += pos * pos;
            for (int j = 0; j < cvr.Length; j++)
                if (i != j) v += Scenario(rho[i, j], sc) * cvr[i] * cvr[j] * Psi(cvr[i], cvr[j]);
        }
        return Math.Sqrt(Math.Max(0, v));
    }

    private static double CurvatureAcross(double[] k, double[] s, IReadOnlyList<(string Name, double[] Up, double[] Down, Matrix Rho)> buckets,
        Func<string, string, double> gamma, FrtbCorrelationScenario sc)
    {
        double total = 0;
        for (int b = 0; b < k.Length; b++)
        {
            total += k[b] * k[b];
            for (int c = 0; c < k.Length; c++)
                if (b != c) total += Scenario(gamma(buckets[b].Name, buckets[c].Name), sc) * s[b] * s[c] * Psi(s[b], s[c]);
        }
        return total;
    }
}
