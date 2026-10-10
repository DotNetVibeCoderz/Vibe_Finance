using Finance.Core;
using Finance.Mathematics;
using Finance.Pricing.Options;

namespace Finance.Pricing.Structured;

/// <summary>
/// Reverse convertible: pays a fixed coupon; at maturity the holder receives par, or
/// (face / strike) shares if the underlying finishes below the strike. Equivalent to a bond minus
/// face/strike European puts.
/// </summary>
public sealed record ReverseConvertible(double FaceValue, double CouponRate, double Maturity, double Strike, int CouponFrequency = 4)
{
    /// <summary>Fair value under Black-Scholes.</summary>
    public double Price(double spot, double rate, double dividendYield, double volatility)
        => BondValue(rate) - FaceValue / Strike * BlackScholes.Price(OptionType.Put, spot, Strike, Maturity, rate, dividendYield, volatility);

    /// <summary>Value of the coupon-bearing note without the embedded put.</summary>
    public double BondValue(double rate)
    {
        double tau = 1.0 / CouponFrequency, pv = 0;
        int n = (int)Math.Round(Maturity * CouponFrequency);
        for (int i = 1; i <= n; i++) pv += FaceValue * CouponRate * tau * Math.Exp(-rate * i * tau);
        return pv + FaceValue * Math.Exp(-rate * Maturity);
    }

    /// <summary>Coupon rate that makes the note price at par.</summary>
    public static double FairCoupon(double faceValue, double maturity, double strike, double spot, double rate, double dividendYield, double volatility, int frequency = 4)
        => RootFinding.Brent(c => new ReverseConvertible(faceValue, c, maturity, strike, frequency).Price(spot, rate, dividendYield, volatility) - faceValue, -0.5, 5, 1e-12);
}

/// <summary>
/// Capital-protected note: returns <paramref name="ProtectionLevel"/> × face at maturity plus
/// <paramref name="Participation"/> in the upside of the underlying above <paramref name="StrikeLevel"/> (as fraction of initial spot).
/// </summary>
public sealed record CapitalProtectedNote(double FaceValue, double Maturity, double ProtectionLevel = 1.0, double Participation = 1.0, double StrikeLevel = 1.0)
{
    /// <summary>Fair value under Black-Scholes.</summary>
    public double Price(double spot, double rate, double dividendYield, double volatility)
    {
        double zero = FaceValue * ProtectionLevel * Math.Exp(-rate * Maturity);
        double call = BlackScholes.Price(OptionType.Call, spot, StrikeLevel * spot, Maturity, rate, dividendYield, volatility);
        return zero + Participation * FaceValue / spot * call;
    }

    /// <summary>Participation rate that makes the note price at par.</summary>
    public static double FairParticipation(double faceValue, double maturity, double protectionLevel, double spot, double rate, double dividendYield, double volatility, double strikeLevel = 1.0)
    {
        double zero = faceValue * protectionLevel * Math.Exp(-rate * maturity);
        double call = BlackScholes.Price(OptionType.Call, spot, strikeLevel * spot, maturity, rate, dividendYield, volatility);
        return (faceValue - zero) / (faceValue / spot * call);
    }
}

/// <summary>Result of an autocallable valuation.</summary>
/// <param name="Price">Fair value.</param>
/// <param name="StandardError">Monte Carlo standard error.</param>
/// <param name="CallProbabilities">Probability of redemption at each observation date.</param>
/// <param name="CapitalLossProbability">Probability that the protection barrier is breached at maturity.</param>
/// <param name="ExpectedLife">Expected life in years.</param>
public sealed record AutocallableResult(double Price, double StandardError, double[] CallProbabilities, double CapitalLossProbability, double ExpectedLife);

/// <summary>
/// Autocallable note: on each observation date, if the underlying is at or above <paramref name="AutocallLevel"/>
/// (fraction of initial spot) the note redeems at face plus accumulated coupons. If never called, at maturity
/// the holder receives face if the underlying is above <paramref name="ProtectionBarrier"/>, else face × S_T/S_0.
/// </summary>
public sealed record AutocallableNote(double FaceValue, double[] ObservationTimes, double CouponPerPeriod,
    double AutocallLevel = 1.0, double ProtectionBarrier = 0.6)
{
    /// <summary>Monte Carlo valuation under GBM (exact steps between observation dates).</summary>
    public AutocallableResult Price(double spot, double rate, double dividendYield, double volatility, int paths = 100_000, ulong seed = 42)
    {
        int m = ObservationTimes.Length;
        if (m == 0) throw new ArgumentException("Need observation dates.");
        const int block = 4096;
        int blocks = (paths + block - 1) / block;
        var root = new FastRandom(seed);
        var seeds = new ulong[blocks];
        for (int b = 0; b < blocks; b++) seeds[b] = root.NextUInt64();
        var sums = new double[blocks];
        var sqs = new double[blocks];
        var callCounts = new long[blocks, m];
        var lossCounts = new long[blocks];
        var lifeSums = new double[blocks];

        Parallel.For(0, blocks, b =>
        {
            var rng = new FastRandom(seeds[b]);
            int count = Math.Min(block, paths - b * block);
            double s = 0, ss = 0, life = 0;
            for (int p = 0; p < count; p++)
            {
                double logRel = 0, prevT = 0, value = 0;
                bool called = false;
                for (int k = 0; k < m; k++)
                {
                    double t = ObservationTimes[k], dt = t - prevT;
                    logRel += (rate - dividendYield - 0.5 * volatility * volatility) * dt + volatility * Math.Sqrt(dt) * rng.NextGaussian();
                    prevT = t;
                    if (Math.Exp(logRel) >= AutocallLevel)
                    {
                        value = FaceValue * (1 + CouponPerPeriod * (k + 1)) * Math.Exp(-rate * t);
                        callCounts[b, k]++;
                        life += t;
                        called = true;
                        break;
                    }
                }
                if (!called)
                {
                    double T = ObservationTimes[^1], rel = Math.Exp(logRel);
                    double payoff = rel >= ProtectionBarrier ? FaceValue : FaceValue * rel;
                    if (rel < ProtectionBarrier) lossCounts[b]++;
                    value = payoff * Math.Exp(-rate * T);
                    life += T;
                }
                s += value;
                ss += value * value;
            }
            sums[b] = s;
            sqs[b] = ss;
            lifeSums[b] = life;
        });

        double sum = sums.Sum(), sq = sqs.Sum();
        double mean = sum / paths;
        double se = Math.Sqrt(Math.Max(sq / paths - mean * mean, 0) / (paths - 1));
        var probs = new double[m];
        for (int k = 0; k < m; k++)
        {
            long c = 0;
            for (int b = 0; b < blocks; b++) c += callCounts[b, k];
            probs[k] = (double)c / paths;
        }
        return new AutocallableResult(mean, se, probs, (double)lossCounts.Sum() / paths, lifeSums.Sum() / paths);
    }
}
