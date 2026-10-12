namespace Finance.Risk;

/// <summary>Default-risk-charge bucket for non-securitisations (MAR22.22).</summary>
public enum FrtbDrcBucket { Corporate, Sovereign, LocalGovernment }

/// <summary>Credit quality category of an obligor (MAR22.24).</summary>
public enum FrtbCreditQuality { AAA, AA, A, BBB, BB, B, CCC, Unrated, Defaulted }

/// <summary>
/// Seniority of an exposure, from most junior to most senior. It sets the LGD (MAR22.15) and which shorts may offset
/// which longs of the same obligor (MAR22.20).
/// </summary>
public enum FrtbSeniority { Equity, NonSeniorDebt, SeniorDebt, CoveredBond }

/// <summary>One non-securitisation position for the default risk charge.</summary>
/// <param name="Obligor">The issuer; positions of one obligor are netted.</param>
/// <param name="Bucket">Corporates, sovereigns or local governments / municipalities.</param>
/// <param name="CreditQuality">The obligor's credit quality category.</param>
/// <param name="Seniority">The seniority of the instrument.</param>
/// <param name="Notional">Bond-equivalent notional, signed: positive for a long credit exposure, negative for a short.
/// For equity, use the market value.</param>
/// <param name="MarketValue">Market value, with the same sign convention.</param>
/// <param name="MaturityYears">Residual maturity; for equity 1y or 0.25y at the bank's discretion (MAR22.18).</param>
/// <param name="Lgd">Overrides the seniority LGD, e.g. 0 for instruments whose value is not linked to the recovery rate.</param>
public sealed record DrcPosition(string Obligor, FrtbDrcBucket Bucket, FrtbCreditQuality CreditQuality, FrtbSeniority Seniority,
    double Notional, double MarketValue, double MaturityYears, double? Lgd = null);

/// <summary>Default risk charge of one bucket.</summary>
/// <param name="Hbr">Hedge benefit ratio Σ netJTD_long / (Σ netJTD_long + Σ |netJTD_short|).</param>
/// <param name="WeightedLong">Σ RW · netJTD_long.</param>
/// <param name="WeightedShort">Σ RW · |netJTD_short|.</param>
/// <param name="Charge">DRCᵦ = max(WeightedLong − HBR · WeightedShort, 0).</param>
public sealed record FrtbDrcBucketResult(double Hbr, double WeightedLong, double WeightedShort, double Charge);

/// <summary>Result of the non-securitisation default risk charge.</summary>
/// <param name="Charge">Σᵦ DRCᵦ (no hedging recognised across buckets).</param>
/// <param name="Buckets">Detail per bucket.</param>
/// <param name="NetJtd">Net long (≥ 0) and net short (≤ 0) JTD per obligor after maturity scaling and offsetting.</param>
public sealed record FrtbDrcResult(double Charge, IReadOnlyDictionary<FrtbDrcBucket, FrtbDrcBucketResult> Buckets,
    IReadOnlyDictionary<string, (double Long, double Short)> NetJtd);

/// <summary>
/// FRTB standardised approach: default risk charge for non-securitisations (Basel MAR22, January 2019 text).
/// </summary>
/// <remarks>
/// Gross JTD = LGD · notional + (market value − notional), floored at 0 for longs and capped at 0 for shorts
/// (MAR22.11-14); LGD 100% for equity and non-senior debt, 75% senior debt, 25% covered bonds (MAR22.15). Each JTD
/// is scaled by max(min(M, 1), 0.25) (MAR22.17-19). Per obligor, shorts offset longs of the same or higher seniority
/// (MAR22.20). In each bucket HBR = Σ long / (Σ long + Σ |short|) and DRCᵦ = max(Σ RW·long − HBR·Σ RW·|short|, 0)
/// with RW by credit quality: AAA 0.5%, AA 2%, A 3%, BBB 6%, BB 15%, B 30%, CCC 50%, unrated 15%, defaulted 100%
/// (MAR22.24). The charge is the simple sum over buckets.
/// </remarks>
public static class FrtbDrc
{
    /// <summary>LGD by seniority (MAR22.15).</summary>
    public static double Lgd(FrtbSeniority seniority) => seniority switch
    {
        FrtbSeniority.SeniorDebt => 0.75,
        FrtbSeniority.CoveredBond => 0.25,
        _ => 1.0,
    };

    /// <summary>Default risk weight by credit quality category (MAR22.24).</summary>
    public static double RiskWeight(FrtbCreditQuality quality) => quality switch
    {
        FrtbCreditQuality.AAA => 0.005,
        FrtbCreditQuality.AA => 0.02,
        FrtbCreditQuality.A => 0.03,
        FrtbCreditQuality.BBB => 0.06,
        FrtbCreditQuality.BB => 0.15,
        FrtbCreditQuality.B => 0.30,
        FrtbCreditQuality.CCC => 0.50,
        FrtbCreditQuality.Unrated => 0.15,
        _ => 1.0,
    };

    /// <summary>Maturity scaling max(min(M, 1), 0.25) (MAR22.17-19).</summary>
    public static double MaturityScale(double maturityYears) => Math.Max(Math.Min(maturityYears, 1), 0.25);

    /// <summary>Gross jump-to-default LGD · notional + P&amp;L with P&amp;L = market value − notional (MAR22.11-14),
    /// floored at 0 for a long (notional ≥ 0) and capped at 0 for a short.</summary>
    public static double JumpToDefault(double notional, double marketValue, double lgd)
    {
        double jtd = lgd * notional + (marketValue - notional);
        return notional >= 0 ? Math.Max(jtd, 0) : Math.Min(jtd, 0);
    }

    /// <summary>The non-securitisation default risk charge for a set of positions.</summary>
    /// <exception cref="ArgumentException">An obligor appears with two credit qualities or in two buckets.</exception>
    public static FrtbDrcResult Charge(IEnumerable<DrcPosition> positions)
    {
        var netJtd = new Dictionary<string, (double Long, double Short)>();
        var perBucket = new Dictionary<FrtbDrcBucket, List<(double Long, double Short, double Rw)>>();
        foreach (var obligor in positions.GroupBy(p => p.Obligor))
        {
            var first = obligor.First();
            if (obligor.Any(p => p.CreditQuality != first.CreditQuality || p.Bucket != first.Bucket))
                throw new ArgumentException($"Obligor '{obligor.Key}' has inconsistent credit quality or bucket.", nameof(positions));

            // Scaled gross JTD summed per seniority, longs and shorts separately.
            int levels = Enum.GetValues<FrtbSeniority>().Length;
            var longs = new double[levels];
            var shorts = new double[levels];   // stored as positive magnitudes
            foreach (var p in obligor)
            {
                double jtd = JumpToDefault(p.Notional, p.MarketValue, p.Lgd ?? Lgd(p.Seniority)) * MaturityScale(p.MaturityYears);
                if (jtd >= 0) longs[(int)p.Seniority] += jtd;
                else shorts[(int)p.Seniority] -= jtd;
            }

            // MAR22.20: a short offsets longs of the same or higher seniority. Shorts are processed from the most senior
            // down; each consumes the eligible longs, most senior first. Eligible sets are nested, so this maximises netting.
            for (int s = levels - 1; s >= 0; s--)
                for (int l = levels - 1; l >= s && shorts[s] > 0; l--)
                {
                    double off = Math.Min(shorts[s], longs[l]);
                    shorts[s] -= off;
                    longs[l] -= off;
                }

            double netLong = longs.Sum(), netShort = -shorts.Sum();
            netJtd[obligor.Key] = (netLong, netShort);
            if (!perBucket.TryGetValue(first.Bucket, out var list)) perBucket[first.Bucket] = list = new();
            list.Add((netLong, netShort, RiskWeight(first.CreditQuality)));
        }

        var buckets = new Dictionary<FrtbDrcBucket, FrtbDrcBucketResult>();
        foreach (var (bucket, items) in perBucket)
        {
            double sumLong = items.Sum(i => i.Long), sumShort = items.Sum(i => -i.Short);
            double hbr = sumLong + sumShort > 0 ? sumLong / (sumLong + sumShort) : 0;
            double wLong = items.Sum(i => i.Rw * i.Long), wShort = items.Sum(i => i.Rw * -i.Short);
            buckets[bucket] = new FrtbDrcBucketResult(hbr, wLong, wShort, Math.Max(wLong - hbr * wShort, 0));
        }
        return new FrtbDrcResult(buckets.Values.Sum(b => b.Charge), buckets, netJtd);
    }
}
