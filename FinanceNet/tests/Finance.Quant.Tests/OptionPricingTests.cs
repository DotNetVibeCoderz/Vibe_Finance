using Finance.Pricing.Options;
using Xunit;

namespace Finance.Quant.Tests;

public class OptionPricingTests
{
    [Fact]
    public void BlackScholes_MatchesHullExample()
    {
        // Hull, Options Futures and Other Derivatives, Example 15.6: S=42, K=40, r=10%, σ=20%, T=0.5.
        Assert.Equal(4.76, BlackScholes.Price(OptionType.Call, 42, 40, 0.5, 0.10, 0.20), 2);
        Assert.Equal(0.81, BlackScholes.Price(OptionType.Put, 42, 40, 0.5, 0.10, 0.20), 2);
    }

    [Fact]
    public void BlackScholes_MatchesHaugExamples()
    {
        // Haug (2007) 1.1.1: S=60, K=65, T=0.25, r=8%, σ=30% → call 2.1334.
        Assert.Equal(2.1334, BlackScholes.Price(OptionType.Call, 60, 65, 0.25, 0.08, 0.30), 4);
        // Haug 1.1.6 (Merton): S=100, K=95, T=0.5, r=10%, q=5%, σ=20% → put 2.4648.
        Assert.Equal(2.4648, BlackScholes.Price(OptionType.Put, 100, 95, 0.5, 0.10, 0.05, 0.20), 4);
    }

    [Theory]
    [InlineData(100, 90, 0.75, 0.05, 0.02, 0.3)]
    [InlineData(50, 60, 2.0, 0.01, 0.0, 0.45)]
    public void PutCallParity_Holds(double s, double k, double t, double r, double q, double v)
    {
        double c = BlackScholes.Price(OptionType.Call, s, k, t, r, q, v);
        double p = BlackScholes.Price(OptionType.Put, s, k, t, r, q, v);
        Assert.Equal(s * Math.Exp(-q * t) - k * Math.Exp(-r * t), c - p, 10);
    }

    [Theory]
    [InlineData(OptionType.Call)]
    [InlineData(OptionType.Put)]
    public void Greeks_AgreeWithFiniteDifferences(OptionType type)
    {
        double s = 105, k = 100, t = 0.8, r = 0.04, q = 0.015, v = 0.27;
        var g = BlackScholes.Greeks(type, s, k, t, r, q, v);
        double P(double ss, double tt, double rr, double vv) => BlackScholes.Price(type, ss, k, tt, rr, q, vv);
        double hs = 1e-3, hv = 1e-5, ht = 1e-5, hr = 1e-6;
        Assert.Equal((P(s + hs, t, r, v) - P(s - hs, t, r, v)) / (2 * hs), g.Delta, 6);
        Assert.Equal((P(s + hs, t, r, v) - 2 * P(s, t, r, v) + P(s - hs, t, r, v)) / (hs * hs), g.Gamma, 4);
        Assert.Equal((P(s, t, r, v + hv) - P(s, t, r, v - hv)) / (2 * hv), g.Vega, 4);
        Assert.Equal(-(P(s, t + ht, r, v) - P(s, t - ht, r, v)) / (2 * ht), g.Theta, 4);
        Assert.Equal((P(s, t, r + hr, v) - P(s, t, r - hr, v)) / (2 * hr), g.Rho, 3);

        double Delta(double ss, double tt, double vv) => BlackScholes.Greeks(type, ss, k, tt, r, q, vv).Delta;
        Assert.Equal((Delta(s, t, v + hv) - Delta(s, t, v - hv)) / (2 * hv), g.Vanna, 4);
        Assert.Equal(-(Delta(s, t + ht, v) - Delta(s, t - ht, v)) / (2 * ht), g.Charm, 4);
        double Vega(double vv) => BlackScholes.Greeks(type, s, k, t, r, q, vv).Vega;
        Assert.Equal((Vega(v + hv) - Vega(v - hv)) / (2 * hv), g.Volga, 3);
    }

    [Theory]
    [InlineData(OptionType.Call, 100, 100, 1.0, 0.05, 0.0, 0.2)]
    [InlineData(OptionType.Put, 100, 130, 0.25, 0.03, 0.01, 0.65)]
    [InlineData(OptionType.Call, 100, 60, 2.0, 0.02, 0.0, 0.15)]
    public void ImpliedVolatility_RoundTrips(OptionType type, double s, double k, double t, double r, double q, double v)
    {
        double price = BlackScholes.Price(type, s, k, t, r, q, v);
        Assert.Equal(v, BlackScholes.ImpliedVolatility(type, price, s, k, t, r, q), 7);
    }

    [Fact]
    public void ImpliedVolatility_RejectsArbitragePrices()
    {
        Assert.True(double.IsNaN(BlackScholes.ImpliedVolatility(OptionType.Call, 150, 100, 100, 1, 0.01)));
    }

    [Fact]
    public void Black76_MatchesHaug()
    {
        // Haug 1.1.4: F=19, K=19, T=0.75, r=10%, σ=28% → call = put = 1.7011.
        Assert.Equal(1.7011, Black76.Price(OptionType.Call, 19, 19, 0.75, 0.10, 0.28), 4);
        Assert.Equal(1.7011, Black76.Price(OptionType.Put, 19, 19, 0.75, 0.10, 0.28), 4);
    }

    [Fact]
    public void Bachelier_AtTheMoneyClosedForm_AndImpliedVolRoundTrip()
    {
        double sigma = 0.008, t = 2;
        Assert.Equal(sigma * Math.Sqrt(t / (2 * Math.PI)), Bachelier.Undiscounted(OptionType.Call, 0.03, 0.03, t, sigma), 12);
        double p = Bachelier.Undiscounted(OptionType.Put, 0.03, 0.035, t, sigma);
        Assert.Equal(sigma, Bachelier.ImpliedVolatility(OptionType.Put, p, 0.03, 0.035, t), 9);
    }

    [Fact]
    public void Binomial_ConvergesToBlackScholes_AndAmericanPutMatchesHull()
    {
        double bs = BlackScholes.Price(OptionType.Put, 50, 50, 5.0 / 12, 0.10, 0.40);
        double eu = LatticePricer.Binomial(OptionType.Put, ExerciseStyle.European, 50, 50, 5.0 / 12, 0.10, 0, 0.40, 2000);
        Assert.Equal(bs, eu, 2);
        // Hull Example 21.1: 5-step tree gives 4.49 for the American put.
        Assert.Equal(4.49, LatticePricer.Binomial(OptionType.Put, ExerciseStyle.American, 50, 50, 5.0 / 12, 0.10, 0, 0.40, 5), 2);
        double am = LatticePricer.Binomial(OptionType.Put, ExerciseStyle.American, 50, 50, 5.0 / 12, 0.10, 0, 0.40, 2000);
        Assert.Equal(4.28, am, 1); // converged value ≈ 4.283
        Assert.True(am > eu);
    }

    [Fact]
    public void AmericanCallWithoutDividends_EqualsEuropean()
    {
        double eu = LatticePricer.Binomial(OptionType.Call, ExerciseStyle.European, 100, 95, 1, 0.05, 0, 0.3, 800);
        double am = LatticePricer.Binomial(OptionType.Call, ExerciseStyle.American, 100, 95, 1, 0.05, 0, 0.3, 800);
        Assert.Equal(eu, am, 10);
    }

    [Fact]
    public void Trinomial_AgreesWithBlackScholesAndBinomial()
    {
        double bs = BlackScholes.Price(OptionType.Call, 100, 105, 1, 0.05, 0.02, 0.25);
        Assert.Equal(bs, LatticePricer.Trinomial(OptionType.Call, ExerciseStyle.European, 100, 105, 1, 0.05, 0.02, 0.25, 800), 2);
        double binAm = LatticePricer.Binomial(OptionType.Put, ExerciseStyle.American, 100, 105, 1, 0.05, 0.02, 0.25, 2000);
        double triAm = LatticePricer.Trinomial(OptionType.Put, ExerciseStyle.American, 100, 105, 1, 0.05, 0.02, 0.25, 1000);
        Assert.Equal(binAm, triAm, 2);
    }

    [Fact]
    public void MonteCarloEuropean_WithinThreeStandardErrors()
    {
        var mc = new MonteCarloPricer(100, 0.05, 0.01, 0.2, new MonteCarloSettings(200_000));
        var res = mc.European(OptionType.Call, 100, 1);
        double bs = BlackScholes.Price(OptionType.Call, 100, 100, 1, 0.05, 0.01, 0.2);
        Assert.InRange(res.Price, bs - 3 * res.StandardError, bs + 3 * res.StandardError);
        Assert.True(res.StandardError < 0.02);
    }

    [Fact]
    public void MonteCarlo_IsReproducibleForASeed()
    {
        var a = new MonteCarloPricer(100, 0.05, 0, 0.2, new MonteCarloSettings(50_000, 12, Seed: 9)).AsianArithmetic(OptionType.Call, 100, 1);
        var b = new MonteCarloPricer(100, 0.05, 0, 0.2, new MonteCarloSettings(50_000, 12, Seed: 9)).AsianArithmetic(OptionType.Call, 100, 1);
        Assert.Equal(a.Price, b.Price);
    }

    [Fact]
    public void AsianControlVariate_ReducesErrorAndBoundsGeometric()
    {
        var mc = new MonteCarloPricer(100, 0.05, 0, 0.3, new MonteCarloSettings(40_000, 52));
        var cv = mc.AsianArithmetic(OptionType.Call, 100, 1, true);
        var plain = mc.AsianArithmetic(OptionType.Call, 100, 1, false);
        Assert.True(cv.StandardError < plain.StandardError / 5);
        Assert.Equal(plain.Price, cv.Price, 1);
        double geo = MonteCarloPricer.GeometricAsianPrice(OptionType.Call, 100, 100, 1, 0.05, 0, 0.3, 52);
        Assert.True(cv.Price > geo); // AM-GM: arithmetic average dominates geometric
        Assert.True(cv.Price < BlackScholes.Price(OptionType.Call, 100, 100, 1, 0.05, 0.3));
    }

    [Fact]
    public void GeometricAsian_ConvergesToContinuousFormula()
    {
        // Continuous geometric Asian = BS with σ/√3 and adjusted dividend (Kemna-Vorst).
        double s = 100, k = 95, t = 1, r = 0.05, v = 0.25;
        double vA = v / Math.Sqrt(3), qA = 0.5 * (r + v * v / 6);
        double kv = BlackScholes.Price(OptionType.Call, s, k, t, r, qA, vA);
        Assert.Equal(kv, MonteCarloPricer.GeometricAsianPrice(OptionType.Call, s, k, t, r, 0, v, 100_000), 3);
    }

    [Fact]
    public void BarrierInPlusOut_EqualsVanilla()
    {
        var mc = new MonteCarloPricer(100, 0.03, 0, 0.25, new MonteCarloSettings(40_000, 100, Seed: 5));
        var uo = mc.Barrier(OptionType.Call, BarrierType.UpAndOut, 100, 130, 1);
        var ui = mc.Barrier(OptionType.Call, BarrierType.UpAndIn, 100, 130, 1);
        var path = mc.PathDependent(1, p => Math.Max(p[^1] - 100, 0));
        Assert.Equal(path.Price, uo.Price + ui.Price, 9);
        var dO = mc.Barrier(OptionType.Put, BarrierType.DownAndOut, 100, 80, 1);
        var dI = mc.Barrier(OptionType.Put, BarrierType.DownAndIn, 100, 80, 1);
        var put = mc.PathDependent(1, p => Math.Max(100 - p[^1], 0));
        Assert.Equal(put.Price, dO.Price + dI.Price, 9);
    }

    [Fact]
    public void FloatingLookbackCall_ApproachesGoldmanSosinGatto()
    {
        // Continuous floating-strike lookback call (r ≠ q=0), Haug 4.15.1.
        double s = 100, t = 0.5, r = 0.05, v = 0.3;
        double a1 = (r + 0.5 * v * v) * t / (v * Math.Sqrt(t)), a2 = a1 - v * Math.Sqrt(t);
        double nrm(double x) => Finance.Mathematics.Normal.Cdf(x);
        double exact = s * nrm(a1) - s * Math.Exp(-r * t) * nrm(a2)
                       + s * Math.Exp(-r * t) * v * v / (2 * r) * (nrm(-a1 + 2 * r * Math.Sqrt(t) / v) - Math.Exp(r * t) * nrm(-a1));
        var mc = new MonteCarloPricer(s, r, 0, v, new MonteCarloSettings(40_000, 1000)).LookbackFloatingStrike(OptionType.Call, t);
        // Discrete monitoring underprices the continuous lookback slightly (Broadie-Glasserman shift).
        Assert.InRange(mc.Price, exact - 1.0, exact + 3 * mc.StandardError);
    }

    [Fact]
    public void Heston_MatchesFangOosterleeReference()
    {
        // Fang & Oosterlee (2008) COS paper, Heston test: reference call 5.785155450.
        var p = new HestonParameters(V0: 0.0175, Kappa: 1.5768, Theta: 0.0398, Sigma: 0.5751, Rho: -0.5711);
        double c = HestonModel.Price(OptionType.Call, 100, 100, 1, 0, 0, p);
        Assert.Equal(5.785155450, c, 5);
    }

    [Fact]
    public void Heston_DegeneratesToBlackScholes()
    {
        var p = new HestonParameters(0.04, 2, 0.04, 1e-4, 0);
        double c = HestonModel.Price(OptionType.Call, 100, 110, 1, 0.03, 0.01, p);
        Assert.Equal(BlackScholes.Price(OptionType.Call, 100, 110, 1, 0.03, 0.01, 0.2), c, 4);
        double put = HestonModel.Price(OptionType.Put, 100, 110, 1, 0.03, 0.01, p);
        Assert.Equal(BlackScholes.Price(OptionType.Put, 100, 110, 1, 0.03, 0.01, 0.2), put, 4);
    }

    [Fact]
    public void Sabr_LognormalLimitAndCalibration()
    {
        // β = 1, ν → 0: flat lognormal vol α.
        Assert.Equal(0.25, SabrModel.ImpliedVolatility(0.03, 0.04, 2, new SabrParameters(0.25, 1, 0, 1e-8)), 6);
        var truth = new SabrParameters(0.035, 0.5, -0.3, 0.45);
        double f = 0.025, t = 3;
        double[] strikes = { 0.01, 0.015, 0.02, 0.025, 0.03, 0.035, 0.045 };
        var vols = strikes.Select(k => SabrModel.ImpliedVolatility(f, k, t, truth)).ToArray();
        // Smile continuity across the ATM branch.
        double atm = SabrModel.ImpliedVolatility(f, f, t, truth);
        Assert.Equal(atm, SabrModel.ImpliedVolatility(f, f * 1.000001, t, truth), 6);
        var fit = SabrModel.Calibrate(f, t, strikes, vols, 0.5);
        Assert.Equal(truth.Alpha, fit.Alpha, 3);
        Assert.Equal(truth.Rho, fit.Rho, 2);
        Assert.Equal(truth.Nu, fit.Nu, 2);
    }

    [Fact]
    public void VolatilitySurface_RecoversInputsAndInterpolatesTotalVariance()
    {
        double s = 100, r = 0.02;
        double[] exp = { 0.25, 1.0 };
        double[] ks = { 80, 90, 100, 110, 120 };
        double Vol(double t, double k) => 0.2 + 0.1 * Math.Pow(Math.Log(k / s), 2) + 0.02 * t;
        var quotes = new List<OptionQuote>();
        foreach (var t in exp)
            foreach (var k in ks)
            {
                var type = k < s ? OptionType.Put : OptionType.Call;
                quotes.Add(new OptionQuote(t, k, type, BlackScholes.Price(type, s, k, t, r, 0, Vol(t, k))));
            }
        var surf = VolatilitySurface.FromQuotes(s, r, 0, quotes);
        Assert.Equal(Vol(1, 110), surf.Volatility(1, 110), 6);
        double tv = 0.5 * Math.Pow(Vol(0.25, 100), 2) * 0.25 + 0.5 * Math.Pow(Vol(1, 100), 2) * 1;
        Assert.Equal(Math.Sqrt(tv / 0.625), surf.Volatility(0.625, 100), 6);
        var (sk, sv) = surf.Smile(0.5, 21);
        Assert.Equal(21, sk.Length);
        Assert.True(sv.Min() > 0.19);
    }
}
