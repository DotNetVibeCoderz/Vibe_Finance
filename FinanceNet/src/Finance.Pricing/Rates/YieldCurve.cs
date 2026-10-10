using Finance.Mathematics;

namespace Finance.Pricing.Rates;

/// <summary>How a curve interpolates between pillars.</summary>
public enum CurveInterpolation
{
    /// <summary>Linear on continuously compounded zero rates.</summary>
    LinearZero,
    /// <summary>Linear on log discount factors (piecewise-flat forwards).</summary>
    LogLinearDiscount,
    /// <summary>Natural cubic spline on zero rates (smooth forwards).</summary>
    CubicZero,
}

/// <summary>
/// A zero-coupon yield curve keyed by time in years, storing continuously compounded zero rates at pillars.
/// Before the first pillar the zero rate is flat; after the last pillar it is flat as well.
/// </summary>
public sealed class YieldCurve
{
    private readonly double[] _times;
    private readonly double[] _zeros;
    private readonly IInterpolator? _interp;

    public YieldCurve(double[] times, double[] zeroRates, CurveInterpolation interpolation = CurveInterpolation.LinearZero)
    {
        Finance.Core.Guard.SameLength(times.Length, zeroRates.Length, "times and zero rates");
        if (times.Length == 0) throw new ArgumentException("A curve needs at least one pillar.");
        _times = (double[])times.Clone();
        _zeros = (double[])zeroRates.Clone();
        Interpolation = interpolation;
        if (times.Length >= 2)
        {
            _interp = interpolation switch
            {
                CurveInterpolation.LinearZero => new LinearInterpolator(_times, _zeros),
                CurveInterpolation.CubicZero => new CubicSplineInterpolator(_times, _zeros),
                CurveInterpolation.LogLinearDiscount => new LinearInterpolator(_times, _times.Select((t, i) => -_zeros[i] * t).ToArray()),
                _ => throw new ArgumentOutOfRangeException(nameof(interpolation)),
            };
        }
    }

    /// <summary>A flat curve at <paramref name="rate"/> (continuous compounding).</summary>
    public static YieldCurve Flat(double rate) => new(new[] { 1.0 }, new[] { rate });

    public CurveInterpolation Interpolation { get; }
    public IReadOnlyList<double> Times => _times;
    public IReadOnlyList<double> ZeroRates => _zeros;

    /// <summary>Continuously compounded zero rate to time <paramref name="t"/>.</summary>
    public double ZeroRate(double t)
    {
        if (_interp is null || t <= _times[0]) return _zeros[0];
        if (t >= _times[^1]) return _zeros[^1];
        if (Interpolation == CurveInterpolation.LogLinearDiscount) return -_interp.Evaluate(t) / t;
        return _interp.Evaluate(t);
    }

    /// <summary>Discount factor P(0, t).</summary>
    public double DiscountFactor(double t) => t <= 0 ? 1 : Math.Exp(-ZeroRate(t) * t);

    /// <summary>Simply compounded forward rate between t1 and t2.</summary>
    public double ForwardRate(double t1, double t2)
    {
        if (t2 <= t1) throw new ArgumentException("t2 must exceed t1.");
        return (DiscountFactor(t1) / DiscountFactor(t2) - 1) / (t2 - t1);
    }

    /// <summary>Continuously compounded forward rate between t1 and t2.</summary>
    public double ContinuousForwardRate(double t1, double t2)
        => (ZeroRate(t2) * t2 - ZeroRate(t1) * t1) / (t2 - t1);

    /// <summary>Instantaneous forward rate f(t) by central difference.</summary>
    public double InstantaneousForward(double t)
    {
        const double h = 1e-4;
        return ContinuousForwardRate(Math.Max(t - h, 0), t + h);
    }

    /// <summary>Zero rate converted to periodic compounding with <paramref name="frequency"/> periods per year.</summary>
    public double ZeroRate(double t, int frequency) => frequency * (Math.Exp(ZeroRate(t) / frequency) - 1);

    /// <summary>A copy with every zero rate shifted by <paramref name="shift"/> (decimal; 0.0001 = 1bp).</summary>
    public YieldCurve Shifted(double shift) => new(_times, _zeros.Select(z => z + shift).ToArray(), Interpolation);

    /// <summary>A copy with one pillar bumped (key-rate duration).</summary>
    public YieldCurve BumpPillar(int index, double shift)
    {
        var z = (double[])_zeros.Clone();
        z[index] += shift;
        return new YieldCurve(_times, z, Interpolation);
    }

    /// <summary>Par swap rate for a swap paying <paramref name="frequency"/> times a year until <paramref name="maturity"/>.</summary>
    public double ParRate(double maturity, int frequency = 1)
    {
        double annuity = 0, tau = 1.0 / frequency;
        int n = (int)Math.Round(maturity * frequency);
        for (int i = 1; i <= n; i++) annuity += tau * DiscountFactor(i * tau);
        return (1 - DiscountFactor(n * tau)) / annuity;
    }
}

/// <summary>An instrument the bootstrapper can reprice off a curve.</summary>
public interface ICurveInstrument
{
    /// <summary>Last cash-flow time (the pillar this instrument determines).</summary>
    double Maturity { get; }

    /// <summary>Model value minus market value; zero when the curve reprices it.</summary>
    double PricingError(YieldCurve curve);
}

/// <summary>Money-market deposit quoted as a simple rate.</summary>
public sealed record DepositInstrument(double Maturity, double Rate) : ICurveInstrument
{
    public double PricingError(YieldCurve curve) => curve.DiscountFactor(Maturity) - 1 / (1 + Rate * Maturity);
}

/// <summary>Forward-rate agreement from <paramref name="Start"/> to <paramref name="Maturity"/> quoted as a simple rate.</summary>
public sealed record FraInstrument(double Start, double Maturity, double Rate) : ICurveInstrument
{
    public double PricingError(YieldCurve curve) => curve.ForwardRate(Start, Maturity) - Rate;
}

/// <summary>Par interest-rate swap quote (single-curve): fixed leg at <paramref name="Rate"/> paid <paramref name="Frequency"/> times a year.</summary>
public sealed record SwapInstrument(double Maturity, double Rate, int Frequency = 1) : ICurveInstrument
{
    public double PricingError(YieldCurve curve) => curve.ParRate(Maturity, Frequency) - Rate;
}

/// <summary>Bootstraps a zero curve so that each instrument reprices exactly.</summary>
public static class CurveBootstrapper
{
    /// <summary>
    /// Sequential bootstrap: instruments are sorted by maturity, each adds one pillar whose zero rate is
    /// solved with Brent so the instrument reprices given all earlier pillars and the chosen interpolation.
    /// </summary>
    public static YieldCurve Bootstrap(IEnumerable<ICurveInstrument> instruments, CurveInterpolation interpolation = CurveInterpolation.LogLinearDiscount)
    {
        var sorted = instruments.OrderBy(i => i.Maturity).ToList();
        if (sorted.Count == 0) throw new ArgumentException("No instruments.");
        var times = new List<double>();
        var zeros = new List<double>();
        foreach (var inst in sorted)
        {
            times.Add(inst.Maturity);
            zeros.Add(zeros.Count > 0 ? zeros[^1] : 0.03);
            SolvePillar(sorted, times, zeros, zeros.Count - 1, interpolation);
        }

        // A cubic spline is non-local: adding a pillar moves the curve before it, so earlier instruments
        // drift off par. Gauss-Seidel sweeps over all pillars restore an exact fit.
        if (interpolation == CurveInterpolation.CubicZero)
        {
            for (int sweep = 0; sweep < 100; sweep++)
            {
                for (int k = 0; k < sorted.Count; k++) SolvePillar(sorted, times, zeros, k, interpolation);
                var curve = new YieldCurve(times.ToArray(), zeros.ToArray(), interpolation);
                if (sorted.All(i => Math.Abs(i.PricingError(curve)) < 1e-13)) break;
            }
        }
        return new YieldCurve(times.ToArray(), zeros.ToArray(), interpolation);
    }

    private static void SolvePillar(List<ICurveInstrument> instruments, List<double> times, List<double> zeros, int k, CurveInterpolation interpolation)
    {
        var t = times.ToArray();
        var z = zeros.ToArray();
        var inst = instruments[k];
        double Err(double rate)
        {
            z[k] = rate;
            return inst.PricingError(new YieldCurve(t, z, interpolation));
        }
        zeros[k] = RootFinding.Brent(Err, -0.2, 0.5, 1e-15);
    }
}

/// <summary>Nelson-Siegel / Svensson parametric yield curve (continuously compounded zero rates).</summary>
/// <param name="Beta0">Long-term level.</param>
/// <param name="Beta1">Slope (short minus long).</param>
/// <param name="Beta2">First curvature.</param>
/// <param name="Tau1">First decay time.</param>
/// <param name="Beta3">Second curvature (0 for plain Nelson-Siegel).</param>
/// <param name="Tau2">Second decay time (Svensson only).</param>
public sealed record NelsonSiegelSvensson(double Beta0, double Beta1, double Beta2, double Tau1, double Beta3 = 0, double Tau2 = 1)
{
    /// <summary>Zero rate at maturity <paramref name="t"/>.</summary>
    public double ZeroRate(double t)
    {
        if (t <= 1e-9) return Beta0 + Beta1;
        var (l1, c1) = Loadings(t, Tau1);
        var (_, c2) = Loadings(t, Tau2);
        return Beta0 + Beta1 * l1 + Beta2 * c1 + Beta3 * c2;
    }

    /// <summary>Discount factor.</summary>
    public double DiscountFactor(double t) => Math.Exp(-ZeroRate(t) * t);

    /// <summary>Samples the model into a <see cref="YieldCurve"/>.</summary>
    public YieldCurve ToCurve(double maxMaturity = 30, int points = 60)
    {
        var t = Enumerable.Range(1, points).Select(i => maxMaturity * i / points).ToArray();
        return new YieldCurve(t, t.Select(ZeroRate).ToArray(), CurveInterpolation.CubicZero);
    }

    private static (double Slope, double Curvature) Loadings(double t, double tau)
    {
        double x = t / tau, e = Math.Exp(-x), f = (1 - e) / x;
        return (f, f - e);
    }

    /// <summary>
    /// Fits Nelson-Siegel: for each τ the betas are an OLS problem, so τ is found by golden-section search on the SSE.
    /// </summary>
    public static NelsonSiegelSvensson FitNelsonSiegel(double[] maturities, double[] zeroRates)
    {
        Finance.Core.Guard.SameLength(maturities.Length, zeroRates.Length, "maturities and rates");
        double bestSse = double.PositiveInfinity;
        NelsonSiegelSvensson? best = null;
        double Sse(double tau)
        {
            var (model, sse) = FitBetas(maturities, zeroRates, tau, null);
            if (sse < bestSse) { bestSse = sse; best = model; }
            return sse;
        }
        // Coarse grid then refine around the best grid point (the SSE can be multimodal in τ).
        double bestTau = 1, bestVal = double.PositiveInfinity;
        for (double tau = 0.1; tau <= 15; tau *= 1.15)
        {
            double v = Sse(tau);
            if (v < bestVal) { bestVal = v; bestTau = tau; }
        }
        Optimizer.GoldenSection(Sse, bestTau / 1.15, bestTau * 1.15, 1e-8);
        return best!;
    }

    /// <summary>Fits Svensson: (τ1, τ2) by Nelder-Mead in log space, betas by OLS.</summary>
    public static NelsonSiegelSvensson FitSvensson(double[] maturities, double[] zeroRates)
    {
        var ns = FitNelsonSiegel(maturities, zeroRates);
        NelsonSiegelSvensson? best = null;
        double bestSse = double.PositiveInfinity;
        double Obj(double[] x)
        {
            double t1 = Math.Exp(x[0]), t2 = Math.Exp(x[1]);
            if (Math.Abs(t1 - t2) < 1e-3 || t1 > 50 || t2 > 50) return 1e9;
            var (m, sse) = FitBetas(maturities, zeroRates, t1, t2);
            if (sse < bestSse) { bestSse = sse; best = m; }
            return sse;
        }
        Optimizer.NelderMead(Obj, new[] { Math.Log(ns.Tau1), Math.Log(ns.Tau1 * 3 + 1) }, 0.5, 3000, 1e-14);
        var nsSse = FitBetas(maturities, zeroRates, ns.Tau1, null).Sse;
        return best is not null && bestSse <= nsSse ? best : ns;
    }

    private static (NelsonSiegelSvensson Model, double Sse) FitBetas(double[] t, double[] y, double tau1, double? tau2)
    {
        int n = t.Length, k = tau2 is null ? 2 : 3;
        var x = new Matrix(n, k);
        for (int i = 0; i < n; i++)
        {
            var (l1, c1) = Loadings(t[i], tau1);
            x[i, 0] = l1;
            x[i, 1] = c1;
            if (tau2 is { } t2) x[i, 2] = Loadings(t[i], t2).Curvature;
        }
        var fit = Regression.Ols(x, y, intercept: true);
        double sse = 0;
        foreach (var r in fit.Residuals) sse += r * r;
        var c = fit.Coefficients;
        var model = tau2 is { } tt
            ? new NelsonSiegelSvensson(c[0], c[1], c[2], tau1, c[3], tt)
            : new NelsonSiegelSvensson(c[0], c[1], c[2], tau1);
        return (model, sse);
    }
}
