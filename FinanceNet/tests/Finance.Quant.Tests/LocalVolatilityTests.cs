using Finance.Pricing.Options;
using Xunit;

namespace Finance.Quant.Tests;

/// <summary>
/// Dupire local volatility. A flat surface must give back the same flat vol; a pure term structure gives the
/// forward variance d(σ²T)/dT; and the defining property, that Europeans under the local-vol diffusion reprice
/// to the input implied vols, is checked by Monte Carlo against Black-Scholes.
/// </summary>
public class LocalVolatilityTests
{
    private static VolatilitySurface Surface(double[] expiries, double[] strikes, Func<double, double, double> vol)
    {
        var v = new double[expiries.Length, strikes.Length];
        for (int i = 0; i < expiries.Length; i++)
            for (int j = 0; j < strikes.Length; j++) v[i, j] = vol(expiries[i], strikes[j]);
        return new VolatilitySurface(expiries, strikes, v);
    }

    private static readonly double[] Strikes = Enumerable.Range(0, 17).Select(i => 60.0 + 5 * i).ToArray();

    [Fact]
    public void FlatImpliedGivesFlatLocal()
    {
        var lv = new LocalVolatility(Surface(new[] { 0.5, 1, 2 }, Strikes, (_, _) => 0.25), 100, 0.03, 0.01);
        foreach (var (t, s) in new[] { (0.3, 100.0), (1.2, 80.0), (1.7, 125.0) })
            Assert.Equal(0.25, lv.Volatility(t, s), 6);
        Assert.Equal(0, lv.Floored);
    }

    [Fact]
    public void TermStructureGivesForwardVariance()
    {
        // Total variance is linear in T between pillars, so σ²_loc = (w₁ − w₀)/(T₁ − T₀) there.
        var lv = new LocalVolatility(Surface(new[] { 0.5, 1, 2 }, Strikes, (t, _) => t switch { 0.5 => 0.30, 1 => 0.25, _ => 0.22 }), 100, 0.02);
        double expected = Math.Sqrt((0.25 * 0.25 * 1 - 0.30 * 0.30 * 0.5) / 0.5);
        Assert.Equal(expected, lv.Volatility(0.75, 100), 5);
        Assert.Equal(expected, lv.Volatility(0.75, 90), 5);
    }

    [Theory]
    [InlineData(85)]
    [InlineData(100)]
    [InlineData(115)]
    public void EuropeansRepriceToTheirImpliedVolatility(double strike)
    {
        static double Smile(double t, double k) => 0.20 + 0.15 * Math.Pow(Math.Log(k / 100), 2) - 0.05 * Math.Log(k / 100);
        var surface = Surface(new[] { 0.25, 0.5, 1, 1.5, 2 }, Strikes, Smile);
        var lv = new LocalVolatility(surface, 100, 0.03, 0.0);
        var type = strike < 100 ? OptionType.Put : OptionType.Call;   // out of the money: most smile-sensitive
        var mc = lv.PriceEuropean(type, strike, 1.0, paths: 120_000, steps: 100, seed: 7);
        double bs = BlackScholes.Price(type, 100, strike, 1.0, 0.03, 0.0, surface.Volatility(1.0, strike));
        Assert.True(Math.Abs(mc.Price - bs) < 4 * mc.StandardError + 0.01 * bs,
            $"K={strike}: local-vol MC {mc.Price:F4} ± {mc.StandardError:F4} vs Black-Scholes {bs:F4}");
        Assert.Equal(0, lv.Floored);
    }
}
