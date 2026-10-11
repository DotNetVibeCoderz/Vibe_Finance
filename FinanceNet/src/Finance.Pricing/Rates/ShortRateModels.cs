using Finance.Core;
using Finance.Mathematics;
using Finance.Pricing.Options;

namespace Finance.Pricing.Rates;

/// <summary>One Gaussian factor of a short-rate model: dxᵢ = −kᵢ xᵢ dt + σᵢ dWᵢ.</summary>
/// <param name="MeanReversion">kᵢ &gt; 0.</param>
/// <param name="Volatility">σᵢ (absolute, rate units per √year).</param>
public readonly record struct GaussianFactor(double MeanReversion, double Volatility);

/// <summary>
/// Gaussian additive short-rate model (Brigo and Mercurio's Gn++): r(t) = Σ xᵢ(t) + φ(t), with φ fitted
/// to the initial curve exactly. One factor is Hull-White; two factors are G2++.
/// </summary>
/// <remarks>
/// Everything follows from one function: the covariance of the factors and of their time integrals over a
/// step. That gives the zero-coupon bond <c>P(t,T) = P(0,T)/P(0,t)·exp(½[V(t,T) − V(0,T) + V(0,t)] − Σ Bᵢ(t,T) xᵢ)</c>,
/// closed-form bond options, and an exact simulation. The simulation steps the factors and ∫r dt jointly,
/// so path discount factors carry no discretisation bias at any step size.
/// </remarks>
public sealed class GaussianShortRateModel
{
    private readonly GaussianFactor[] _f;
    private readonly double[,] _rho;

    /// <param name="curve">Initial discount curve the model reprices exactly.</param>
    /// <param name="factors">The Gaussian factors.</param>
    /// <param name="correlation">Factor correlation matrix (identity when null).</param>
    public GaussianShortRateModel(YieldCurve curve, IReadOnlyList<GaussianFactor> factors, double[,]? correlation = null)
    {
        if (factors.Count == 0) throw new ArgumentException("At least one factor is required.");
        foreach (var f in factors)
            if (f.MeanReversion <= 0 || f.Volatility < 0) throw new ArgumentException("Mean reversion must be > 0 and volatility >= 0.");
        Curve = curve;
        _f = factors.ToArray();
        int n = _f.Length;
        _rho = correlation ?? Identity(n);
        if (_rho.GetLength(0) != n || _rho.GetLength(1) != n) throw new ArgumentException("Correlation matrix shape mismatch.");
    }

    /// <summary>Hull-White one-factor model: dr = (θ(t) − a·r)dt + σ dW.</summary>
    public static GaussianShortRateModel HullWhite(YieldCurve curve, double meanReversion, double volatility)
        => new(curve, new[] { new GaussianFactor(meanReversion, volatility) });

    /// <summary>G2++: two correlated Gaussian factors.</summary>
    public static GaussianShortRateModel G2(YieldCurve curve, double a, double sigma, double b, double eta, double rho)
        => new(curve, new[] { new GaussianFactor(a, sigma), new GaussianFactor(b, eta) }, new[,] { { 1, rho }, { rho, 1 } });

    public YieldCurve Curve { get; }
    public int Factors => _f.Length;
    public IReadOnlyList<GaussianFactor> FactorParameters => _f;

    private static double[,] Identity(int n)
    {
        var m = new double[n, n];
        for (int i = 0; i < n; i++) m[i, i] = 1;
        return m;
    }

    private static double E1(double k, double dt) => k * dt < 1e-8 ? dt : (1 - Math.Exp(-k * dt)) / k;

    /// <summary>Bᵢ(t, T) = (1 − e^{−kᵢ(T − t)}) / kᵢ.</summary>
    public double B(int factor, double t, double T) => E1(_f[factor].MeanReversion, T - t);

    private double CovXX(int i, int j, double dt) => _rho[i, j] * _f[i].Volatility * _f[j].Volatility * E1(_f[i].MeanReversion + _f[j].MeanReversion, dt);

    private double CovXI(int i, int j, double dt)
    {
        double ki = _f[i].MeanReversion, kj = _f[j].MeanReversion;
        return _rho[i, j] * _f[i].Volatility * _f[j].Volatility / kj * (E1(ki, dt) - E1(ki + kj, dt));
    }

    private double CovII(int i, int j, double dt)
    {
        double ki = _f[i].MeanReversion, kj = _f[j].MeanReversion;
        return _rho[i, j] * _f[i].Volatility * _f[j].Volatility / (ki * kj) * (dt - E1(ki, dt) - E1(kj, dt) + E1(ki + kj, dt));
    }

    /// <summary>V(t, T): variance of ∫ₜᵀ Σxᵢ(u) du given the state at t.</summary>
    public double IntegratedVariance(double t, double T)
    {
        double v = 0, dt = T - t;
        for (int i = 0; i < Factors; i++)
            for (int j = 0; j < Factors; j++) v += CovII(i, j, dt);
        return v;
    }

    /// <summary>Zero-coupon bond price P(t, T) given the factor state at t.</summary>
    public double ZeroBond(double t, double T, ReadOnlySpan<double> state)
    {
        if (T <= t) return 1;
        double lnA = Math.Log(Curve.DiscountFactor(T) / Curve.DiscountFactor(t))
                     + 0.5 * (IntegratedVariance(t, T) - IntegratedVariance(0, T) + IntegratedVariance(0, t));
        double bx = 0;
        for (int i = 0; i < Factors; i++) bx += B(i, t, T) * state[i];
        return Math.Exp(lnA - bx);
    }

    /// <summary>
    /// European option expiring at <paramref name="expiry"/> on the zero bond maturing at <paramref name="maturity"/>.
    /// ln P(T, S) is Gaussian, so this is Black's formula on the forward bond with variance Σᵢⱼ BᵢBⱼ·Cov(xᵢ(T), xⱼ(T)).
    /// </summary>
    public double ZeroBondOption(OptionType type, double expiry, double maturity, double strike)
    {
        double var = 0;
        for (int i = 0; i < Factors; i++)
            for (int j = 0; j < Factors; j++) var += B(i, expiry, maturity) * B(j, expiry, maturity) * CovXX(i, j, expiry);
        double sp = Math.Sqrt(var);
        double pT = Curve.DiscountFactor(expiry), pS = Curve.DiscountFactor(maturity);
        if (sp < 1e-15) return Math.Max(type == OptionType.Call ? pS - strike * pT : strike * pT - pS, 0);
        double h = Math.Log(pS / (strike * pT)) / sp + 0.5 * sp;
        return type == OptionType.Call
            ? pS * Normal.Cdf(h) - strike * pT * Normal.Cdf(h - sp)
            : strike * pT * Normal.Cdf(-h + sp) - pS * Normal.Cdf(-h);
    }

    /// <summary>Caplet on the simple rate over [start, end] with strike K, per unit notional: (1 + Kτ)·put on P(start, end).</summary>
    public double Caplet(double start, double end, double strike)
    {
        double tau = end - start, k = 1 + strike * tau;
        return k * ZeroBondOption(OptionType.Put, start, end, 1 / k);
    }

    /// <summary>Floorlet: (1 + Kτ)·call on P(start, end).</summary>
    public double Floorlet(double start, double end, double strike)
    {
        double tau = end - start, k = 1 + strike * tau;
        return k * ZeroBondOption(OptionType.Call, start, end, 1 / k);
    }

    /// <summary>Cap (sum of caplets) on a strip starting at <paramref name="start"/> with <paramref name="frequency"/> periods a year.</summary>
    public double Cap(double start, double maturity, double strike, int frequency = 4)
    {
        double tau = 1.0 / frequency, v = 0;
        int n = (int)Math.Round((maturity - start) * frequency);
        for (int i = 1; i <= n; i++) v += Caplet(start + (i - 1) * tau, start + i * tau, strike);
        return v;
    }

    /// <summary>
    /// European swaption by Jamshidian's decomposition (one-factor models only). The option on the coupon bond
    /// becomes a portfolio of zero-bond options struck at the bond prices for the critical rate.
    /// </summary>
    public double JamshidianSwaption(Swaption swaption)
    {
        if (Factors != 1) throw new NotSupportedException("Jamshidian's decomposition needs a one-factor model; use BermudanSwaption with one exercise date for G2++.");
        var (times, coupons) = CouponSchedule(swaption.Expiry, swaption.Expiry + swaption.Tenor, swaption.Strike, swaption.Frequency);
        double T = swaption.Expiry;
        double Bond(double x)
        {
            double v = 0;
            for (int i = 0; i < times.Length; i++) v += coupons[i] * ZeroBond(T, times[i], new[] { x });
            return v;
        }
        double xStar = RootFinding.Brent(x => Bond(x) - 1, -2.0, 2.0);
        double price = 0;
        // A payer swaption is a put on the coupon bond with strike 1; a receiver is a call.
        var type = swaption.Payer ? OptionType.Put : OptionType.Call;
        for (int i = 0; i < times.Length; i++)
            price += coupons[i] * ZeroBondOption(type, T, times[i], ZeroBond(T, times[i], new[] { xStar }));
        return swaption.Notional * price;
    }

    /// <summary>Fixed-leg payment times after <paramref name="start"/> and the coupon-bond cash flows (Kτ, last one + 1).</summary>
    internal static (double[] Times, double[] Coupons) CouponSchedule(double start, double end, double strike, int frequency)
    {
        double tau = 1.0 / frequency;
        int n = (int)Math.Round((end - start) * frequency);
        var t = new double[n];
        var c = new double[n];
        for (int i = 0; i < n; i++) { t[i] = start + (i + 1) * tau; c[i] = strike * tau; }
        c[^1] += 1;
        return (t, c);
    }

    /// <summary>
    /// Exact joint simulation of the factors at <paramref name="times"/> (ascending, &gt; 0). Returns states[path][k][factor]
    /// and the path discount factors D(0, times[k]) = exp(−∫r).
    /// </summary>
    public (double[][][] States, double[][] Discount) Simulate(double[] times, int paths, ulong seed = 42)
    {
        int n = Factors, m = times.Length;
        var states = new double[paths][][];
        var disc = new double[paths][];
        // Per-step Cholesky factor of the 2n-dimensional covariance of (x(t+Δ), ∫x) given x(t).
        var chol = new Matrix[m];
        double prev = 0;
        for (int k = 0; k < m; k++)
        {
            double dt = times[k] - prev;
            if (dt <= 0) throw new ArgumentException("Simulation times must be ascending and positive.");
            var cov = new Matrix(2 * n, 2 * n);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    cov[i, j] = CovXX(i, j, dt);
                    cov[i, n + j] = CovXI(i, j, dt);
                    cov[n + j, i] = cov[i, n + j];
                    cov[n + i, n + j] = CovII(i, j, dt);
                }
            for (int i = 0; i < 2 * n; i++) cov[i, i] += 1e-18; // degenerate zero-volatility factors stay factorable
            chol[k] = cov.Cholesky();
            prev = times[k];
        }

        var rng = new FastRandom(seed);
        var z = new double[2 * n];
        for (int p = 0; p < paths; p++)
        {
            var x = new double[n];
            double integral = 0;
            prev = 0;
            states[p] = new double[m][];
            disc[p] = new double[m];
            for (int k = 0; k < m; k++)
            {
                double dt = times[k] - prev;
                for (int i = 0; i < z.Length; i++) z[i] = rng.NextGaussian();
                var next = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double ki = _f[i].MeanReversion, shockX = 0, shockI = 0;
                    for (int c = 0; c <= i; c++) shockX += chol[k][i, c] * z[c];
                    for (int c = 0; c <= n + i; c++) shockI += chol[k][n + i, c] * z[c];
                    next[i] = x[i] * Math.Exp(-ki * dt) + shockX;
                    integral += x[i] * E1(ki, dt) + shockI;
                }
                x = next;
                states[p][k] = x;
                // D(0, t) = P(0, t)·exp(−½V(0, t) − ∫Σx): E[D] = P(0, t) exactly.
                disc[p][k] = Curve.DiscountFactor(times[k]) * Math.Exp(-0.5 * IntegratedVariance(0, times[k]) - integral);
                prev = times[k];
            }
        }
        return (states, disc);
    }

    /// <summary>
    /// Bermudan swaption by Longstaff-Schwartz least-squares Monte Carlo. The holder may enter the swap
    /// (<see cref="Swaption.Payer"/> decides the side) on every fixed-leg date from <see cref="Swaption.Expiry"/> up to but excluding
    /// maturity. The exercise value at each date is the exact model swap value; continuation values are regressed on
    /// {1, s, s², x₁…xₙ} over in-the-money paths, s being the swap value.
    /// </summary>
    public MonteCarloResult BermudanSwaption(Swaption swaption, int paths = 20_000, ulong seed = 42, int? exerciseDates = null)
    {
        double tau = 1.0 / swaption.Frequency, maturity = swaption.Expiry + swaption.Tenor;
        int nEx = exerciseDates ?? (int)Math.Round(swaption.Tenor * swaption.Frequency);
        var ex = Enumerable.Range(0, nEx).Select(i => swaption.Expiry + i * tau).Where(t => t < maturity - 1e-9).ToArray();
        var (states, disc) = Simulate(ex, paths, seed);

        double SwapValue(int k, double[] x)
        {
            double t0 = ex[k], annuity = 0;
            int n = (int)Math.Round((maturity - t0) * swaption.Frequency);
            for (int i = 1; i <= n; i++) annuity += tau * ZeroBond(t0, t0 + i * tau, x);
            double payer = 1 - ZeroBond(t0, maturity, x) - swaption.Strike * annuity;
            return swaption.Payer ? payer : -payer;
        }

        // cash[p] holds the exercise value discounted to time 0 along the path (0 if never exercised so far).
        var cash = new double[paths];
        int last = ex.Length - 1;
        for (int p = 0; p < paths; p++) cash[p] = Math.Max(SwapValue(last, states[p][last]), 0) * disc[p][last];

        for (int k = last - 1; k >= 0; k--)
        {
            var itm = new List<int>();
            var exercise = new double[paths];
            for (int p = 0; p < paths; p++)
            {
                exercise[p] = SwapValue(k, states[p][k]);
                if (exercise[p] > 0) itm.Add(p);
            }
            if (itm.Count < 3 + Factors) continue;
            int cols = 2 + Factors;
            var X = new Matrix(itm.Count, cols);
            var y = new double[itm.Count];
            for (int r = 0; r < itm.Count; r++)
            {
                int p = itm[r];
                double s = exercise[p];
                X[r, 0] = s;
                X[r, 1] = s * s;
                for (int i = 0; i < Factors; i++) X[r, 2 + i] = states[p][k][i];
                y[r] = cash[p] / disc[p][k]; // continuation value in time-t_k money
            }
            var fit = Regression.Ols(X, y);
            for (int r = 0; r < itm.Count; r++)
            {
                int p = itm[r];
                var row = new double[cols];
                for (int c = 0; c < cols; c++) row[c] = X[r, c];
                if (exercise[p] > fit.Predict(row)) cash[p] = exercise[p] * disc[p][k];
            }
        }

        double mean = cash.Average();
        double sd = Math.Sqrt(cash.Sum(c => (c - mean) * (c - mean)) / (paths - 1));
        return new MonteCarloResult(swaption.Notional * mean, swaption.Notional * sd / Math.Sqrt(paths), paths);
    }
}
