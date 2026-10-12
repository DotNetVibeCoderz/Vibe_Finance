using Finance.Core.Market;
using Finance.DataFrame;
using Finance.RustBridge;
using Finance.TimeSeries.Indicators;
using Xunit;

namespace Finance.Native.Tests;

/// <summary>
/// Every <see cref="IndicatorCatalog"/> entry computed by the Rust catalogue must match the managed definition: same
/// output names and order, NaN in the same places, values equal to 1e-9 relative. Checked with default parameters
/// and with a second, randomised parameter set (catches parameter-order mistakes).
/// </summary>
public class CatalogParityTests
{
    private static readonly OhlcvFrame Bars = OhlcvFrame.FromBars(SyntheticMarket.GeometricBrownianBars(700, seed: 11));
    private static readonly OhlcvFrame Choppy = OhlcvFrame.FromBars(SyntheticMarket.GeometricBrownianBars(400, annualVolatility: 0.6, seed: 5));

    public static TheoryData<string> Ids() => new(IndicatorCatalog.All.Select(d => d.Id));

    [Theory]
    [MemberData(nameof(Ids))]
    public void NativeMatchesManaged(string id)
    {
        Assert.True(RustAccelerator.IsLoaded, RustAccelerator.LoadError);
        if (!FastCatalog.NativeIds.Contains(id)) Assert.Skip($"{id} is not in the native catalogue yet.");
        var def = IndicatorCatalog.Get(id);

        var defaults = def.Parameters.Select(p => p.Default).ToArray();
        Compare(id, "defaults", def.Compute(Bars), FastCatalog.ComputeNative(id, Bars, defaults));

        // A second parameter set inside each parameter's range (integers rounded), on more volatile bars.
        var rng = new Random(id.GetHashCode() & 0x7fffffff);
        var dict = new Dictionary<string, double>();
        foreach (var p in def.Parameters)
        {
            double lo = p.Min, hi = Math.Min(p.Max, p.IsInteger ? Math.Max(p.Min, p.Default * 2) : p.Max);
            double v = lo + rng.NextDouble() * (hi - lo);
            dict[p.Name] = p.IsInteger ? Math.Round(v) : v;
        }
        string label = string.Join(",", dict.Select(kv => $"{kv.Key}={kv.Value:G4}"));
        var native = FastCatalog.ComputeNative(id, Choppy, def.Parameters.Select(p => dict[p.Name]).ToArray());
        IndicatorSeries[] managed;
        try { managed = def.Compute(Choppy, dict); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Assert.True(native is null, $"{id} [{label}]: managed threw {e.GetType().Name} but native returned values");
            return;
        }
        Compare(id, label, managed, native);
    }

    [Fact]
    public void EveryCatalogueEntryIsNative()
    {
        Assert.True(RustAccelerator.IsLoaded, RustAccelerator.LoadError);
        var missing = IndicatorCatalog.All.Select(d => d.Id).Where(id => !FastCatalog.NativeIds.Contains(id)).ToArray();
        Assert.True(missing.Length == 0, $"{missing.Length} of {IndicatorCatalog.Count} not native: {string.Join(", ", missing)}");
        Assert.Equal(IndicatorCatalog.Count, FastCatalog.NativeIds.Count);
    }

    [Fact]
    public void SessionVwapResetsOnIntradayBars()
    {
        // 7 hourly bars per day: the session VWAP must reset at each date change, natively as in managed code.
        var start = new DateTime(2026, 3, 2, 9, 0, 0);
        var bars = SyntheticMarket.GeometricBrownianBars(70, seed: 3)
            .Select((b, i) => b with { Time = start.AddDays(i / 7).AddHours(i % 7) }).ToArray();
        var frame = OhlcvFrame.FromBars(bars);
        var native = FastCatalog.ComputeNative("SVWAP", frame, Array.Empty<double>());
        Compare("SVWAP", "intraday", IndicatorCatalog.Get("SVWAP").Compute(frame), native);
        double typical = (bars[7].High + bars[7].Low + bars[7].Close) / 3;
        Assert.Equal(typical, native![0].Values[7], 12);           // first bar of day 2 starts afresh
        Assert.NotEqual((bars[6].High + bars[6].Low + bars[6].Close) / 3, native[0].Values[6], 12);
    }

    [Fact]
    public void ComputeFallsBackToManagedWhenForced()
    {
        var before = RustAccelerator.ForceManaged;
        try
        {
            RustAccelerator.ForceManaged = true;
            var r = FastCatalog.Compute("SMA", Bars, new Dictionary<string, double> { ["period"] = 10 });
            Assert.Equal(IndicatorCatalog.Get("SMA").Compute(Bars, new Dictionary<string, double> { ["period"] = 10 })[0].Values, r[0].Values);
        }
        finally { RustAccelerator.ForceManaged = before; }
    }

    private static void Compare(string id, string label, IndicatorSeries[] managed, IndicatorSeries[]? native)
    {
        Assert.True(native is not null, $"{id} [{label}]: native rejected the arguments");
        Assert.Equal(managed.Select(s => s.Name), native!.Select(s => s.Name));
        for (int k = 0; k < managed.Length; k++)
        {
            var (m, r) = (managed[k].Values, native[k].Values);
            Assert.Equal(m.Length, r.Length);
            for (int i = 0; i < m.Length; i++)
            {
                if (double.IsNaN(m[i]) || double.IsNaN(r[i]))
                {
                    Assert.True(double.IsNaN(m[i]) && double.IsNaN(r[i]), $"{id}/{managed[k].Name} [{label}] at {i}: managed {m[i]}, native {r[i]}");
                    continue;
                }
                if (double.IsInfinity(m[i]) || double.IsInfinity(r[i]))
                {
                    Assert.True(m[i] == r[i], $"{id}/{managed[k].Name} [{label}] at {i}: managed {m[i]}, native {r[i]}");
                    continue;
                }
                double tol = 1e-9 * Math.Max(1, Math.Abs(m[i]));
                Assert.True(Math.Abs(m[i] - r[i]) <= tol, $"{id}/{managed[k].Name} [{label}] at {i}: managed {m[i]:R}, native {r[i]:R}");
            }
        }
    }
}
