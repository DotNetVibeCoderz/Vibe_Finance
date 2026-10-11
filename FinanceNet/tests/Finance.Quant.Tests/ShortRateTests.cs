using Finance.Mathematics;
using Finance.Pricing.Options;
using Finance.Pricing.Rates;
using Xunit;

namespace Finance.Quant.Tests;

/// <summary>
/// Hull-White and G2++. References are closed forms from Hull's and Brigo-Mercurio's texts, no-arbitrage
/// parities that hold exactly, and martingale properties of the simulation checked against standard errors.
/// </summary>
public class ShortRateTests
{
    private static readonly YieldCurve Curve = new(new[] { 0.5, 1, 2, 5, 10, 30 }, new[] { 0.030, 0.032, 0.035, 0.038, 0.040, 0.041 });
    private static readonly GaussianShortRateModel Hw = GaussianShortRateModel.HullWhite(Curve, 0.10, 0.01);

    [Fact]
    public void ModelRepricesTheInitialCurve()
    {
        foreach (var t in new[] { 0.25, 1.0, 3.7, 10.0, 25.0 })
            Assert.Equal(Curve.DiscountFactor(t), Hw.ZeroBond(0, t, new[] { 0.0 }), 13);
    }

    [Fact]
    public void ZeroBondOptionMatchesHullClosedForm()
    {
        // Hull, Options Futures and Other Derivatives: σP = σ/a·(1 − e^{−a(S−T)})·√((1 − e^{−2aT})/(2a)).
        const double a = 0.1, s = 0.01, T = 2, S = 7, K = 0.83;
        double sp = s / a * (1 - Math.Exp(-a * (S - T))) * Math.Sqrt((1 - Math.Exp(-2 * a * T)) / (2 * a));
        double pT = Curve.DiscountFactor(T), pS = Curve.DiscountFactor(S);
        double h = Math.Log(pS / (K * pT)) / sp + sp / 2;
        double call = pS * Normal.Cdf(h) - K * pT * Normal.Cdf(h - sp);
        double put = K * pT * Normal.Cdf(-h + sp) - pS * Normal.Cdf(-h);
        Assert.Equal(call, Hw.ZeroBondOption(OptionType.Call, T, S, K), 13);
        Assert.Equal(put, Hw.ZeroBondOption(OptionType.Put, T, S, K), 13);
        Assert.Equal(pS - K * pT, call - put, 13); // put-call parity
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SimulationIsArbitrageFree(int factors)
    {
        // E[D(0,T)] = P(0,T) and E[D(0,T)·P(T,S)] = P(0,S): the discount factor and the bond formula agree.
        var model = factors == 1 ? Hw : GaussianShortRateModel.G2(Curve, 0.5, 0.012, 0.05, 0.008, -0.6);
        double[] times = { 0.5, 1.5, 3.0 };
        var (states, disc) = model.Simulate(times, 40_000, seed: 3);
        int last = times.Length - 1;
        var d = disc.Select(x => x[last]).ToArray();
        var dp = disc.Select((x, p) => x[last] * model.ZeroBond(3.0, 8.0, states[p][last])).ToArray();
        AssertMean(Curve.DiscountFactor(3.0), d);
        AssertMean(Curve.DiscountFactor(8.0), dp);
    }

    private static void AssertMean(double expected, double[] samples)
    {
        double m = samples.Average(), se = Math.Sqrt(samples.Sum(v => (v - m) * (v - m)) / (samples.Length - 1) / samples.Length);
        Assert.True(Math.Abs(m - expected) < 4 * se + 1e-12, $"mean {m:F6} vs {expected:F6} (se {se:E2})");
    }

    [Fact]
    public void MonteCarloZeroBondCallMatchesClosedForm()
    {
        var (states, disc) = Hw.Simulate(new[] { 2.0 }, 60_000, seed: 9);
        var payoff = states.Select((s, p) => disc[p][0] * Math.Max(Hw.ZeroBond(2, 7, s[0]) - 0.83, 0)).ToArray();
        AssertMean(Hw.ZeroBondOption(OptionType.Call, 2, 7, 0.83), payoff);
    }

    [Fact]
    public void CapFloorParityIsExact()
    {
        const double K = 0.037;
        double cap = Hw.Cap(1, 6, K, 4), floor = 0;
        double fras = 0, tau = 0.25;
        for (int i = 1; i <= 20; i++)
        {
            double t0 = 1 + (i - 1) * tau, t1 = 1 + i * tau;
            floor += Hw.Floorlet(t0, t1, K);
            fras += Curve.DiscountFactor(t0) - (1 + K * tau) * Curve.DiscountFactor(t1);
        }
        Assert.Equal(fras, cap - floor, 12);
    }

    [Fact]
    public void JamshidianPayerReceiverParityAndMonteCarlo()
    {
        var payer = new Swaption(Expiry: 2, Tenor: 5, Strike: 0.04, Payer: true);
        var receiver = payer with { Payer = false };
        double p = Hw.JamshidianSwaption(payer), r = Hw.JamshidianSwaption(receiver);
        // Payer − receiver = forward-starting payer swap = P(0,T₀) − P(0,Tₙ) − K·Annuity.
        double swap = Curve.DiscountFactor(2) - Curve.DiscountFactor(7) - 0.04 * payer.Annuity(Curve);
        Assert.Equal(swap, p - r, 12);

        // A "Bermudan" with a single exercise date is the European: LSMC has nothing to regress.
        var mc = Hw.BermudanSwaption(payer, paths: 40_000, seed: 5, exerciseDates: 1);
        Assert.True(Math.Abs(mc.Price - p) < 4 * mc.StandardError, $"MC {mc.Price:F6} ± {mc.StandardError:F6} vs Jamshidian {p:F6}");
    }

    [Fact]
    public void G2WithOneSilentFactorIsHullWhite()
    {
        var g2 = GaussianShortRateModel.G2(Curve, 0.10, 0.01, 0.5, 0.0, 0.3);
        Assert.Equal(Hw.ZeroBond(2, 9, new[] { 0.004 }), g2.ZeroBond(2, 9, new[] { 0.004, 0.0 }), 13);
        Assert.Equal(Hw.ZeroBondOption(OptionType.Put, 3, 8, 0.85), g2.ZeroBondOption(OptionType.Put, 3, 8, 0.85), 13);
        Assert.Equal(Hw.Caplet(2, 2.5, 0.04), g2.Caplet(2, 2.5, 0.04), 13);
    }

    [Fact]
    public void BermudanIsWorthAtLeastEveryCoTerminalEuropean()
    {
        var swpn = new Swaption(Expiry: 1, Tenor: 5, Strike: 0.04, Payer: false);
        var berm = Hw.BermudanSwaption(swpn, paths: 30_000, seed: 21);
        double maxEuro = 0;
        for (int k = 0; k < 5; k++)
            maxEuro = Math.Max(maxEuro, Hw.JamshidianSwaption(new Swaption(1 + k, 5 - k, 0.04, Payer: false)));
        Assert.True(berm.Price > maxEuro - 3 * berm.StandardError, $"Bermudan {berm.Price:F6} vs best European {maxEuro:F6}");
        // ... and the early-exercise premium is real but bounded: well below the sum of the Europeans.
        Assert.True(berm.Price < 1.5 * maxEuro);
    }

    [Fact]
    public void G2BermudanRunsAndExceedsItsEuropean()
    {
        var g2 = GaussianShortRateModel.G2(Curve, 0.5, 0.012, 0.05, 0.008, -0.6);
        var swpn = new Swaption(Expiry: 1, Tenor: 4, Strike: 0.038, Payer: true);
        var euro = g2.BermudanSwaption(swpn, paths: 20_000, seed: 2, exerciseDates: 1);
        var berm = g2.BermudanSwaption(swpn, paths: 20_000, seed: 2);
        Assert.True(berm.Price >= euro.Price - 1e-12);
    }
}
