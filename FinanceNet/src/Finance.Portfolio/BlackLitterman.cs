using Finance.Mathematics;

namespace Finance.Portfolio;

/// <summary>How the view-uncertainty matrix Ω is built.</summary>
public enum OmegaMethod
{
    /// <summary>He &amp; Litterman (1999): Ω = diag(τ P Σ Pᵀ).</summary>
    HeLitterman,
    /// <summary>Idzorek (2005) confidences: Ωₖ = τ (1 − cₖ)/cₖ · pₖ Σ pₖᵀ.</summary>
    Idzorek,
}

/// <summary>
/// A set of investor views, built against a fixed asset universe.
/// Each view is a row of the pick matrix P with an expected (annual) return Q and a confidence in (0, 1].
/// </summary>
public sealed class BlackLittermanViews
{
    private readonly Dictionary<string, int> _index;
    private readonly List<double[]> _rows = new();
    private readonly List<double> _q = new();
    private readonly List<double> _confidence = new();

    public BlackLittermanViews(IReadOnlyList<string> assets)
    {
        Assets = assets;
        _index = assets.Select((a, i) => (a, i)).ToDictionary(t => t.a, t => t.i, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> Assets { get; }
    public int Count => _q.Count;

    /// <summary>"<paramref name="asset"/> will return <paramref name="expectedReturn"/>".</summary>
    public BlackLittermanViews Absolute(string asset, double expectedReturn, double confidence = 0.5)
    {
        var row = new double[Assets.Count];
        row[Index(asset)] = 1;
        return Add(row, expectedReturn, confidence);
    }

    /// <summary>"<paramref name="outperformer"/> will beat <paramref name="underperformer"/> by <paramref name="spread"/>".</summary>
    public BlackLittermanViews Relative(string outperformer, string underperformer, double spread, double confidence = 0.5)
    {
        var row = new double[Assets.Count];
        row[Index(outperformer)] = 1;
        row[Index(underperformer)] = -1;
        return Add(row, spread, confidence);
    }

    /// <summary>A general view: Σ pickᵢ·Rᵢ = <paramref name="expectedReturn"/>.</summary>
    public BlackLittermanViews Add(double[] pick, double expectedReturn, double confidence = 0.5)
    {
        if (pick.Length != Assets.Count) throw new ArgumentException("Pick vector length must match the asset count.");
        Finance.Core.Guard.InRange(confidence, 0, 1);
        _rows.Add((double[])pick.Clone());
        _q.Add(expectedReturn);
        _confidence.Add(confidence);
        return this;
    }

    /// <summary>The K x N pick matrix.</summary>
    public Matrix P => Matrix.FromRows(_rows.Count == 0 ? new List<double[]>() : _rows);

    public double[] Q => _q.ToArray();
    public double[] Confidences => _confidence.ToArray();

    private int Index(string asset) => _index.TryGetValue(asset, out int i) ? i : throw new KeyNotFoundException($"Unknown asset '{asset}'.");
}

/// <summary>
/// The Black-Litterman model: blends an equilibrium prior π with investor views.
/// Posterior: μ = π + τΣPᵀ(PτΣPᵀ + Ω)⁻¹(Q − Pπ), Σ_post = Σ + τΣ − τΣPᵀ(PτΣPᵀ + Ω)⁻¹PτΣ.
/// This form never inverts Ω, so 100%-confidence views (Ω = 0) are exact.
/// </summary>
public sealed class BlackLittermanModel
{
    public BlackLittermanModel(Matrix covariance, double[] priorReturns, Matrix? pick = null, double[]? viewReturns = null, Matrix? omega = null, double tau = 0.05, IReadOnlyList<string>? assets = null)
    {
        int n = priorReturns.Length;
        if (covariance.Rows != n || !covariance.IsSquare) throw new ArgumentException("Covariance must be n x n.");
        Finance.Core.Guard.Positive(tau);
        Covariance = covariance;
        Prior = (double[])priorReturns.Clone();
        Tau = tau;
        Assets = assets ?? Enumerable.Range(0, n).Select(i => $"Asset{i + 1}").ToArray();

        int k = viewReturns?.Length ?? 0;
        if (k == 0 || pick is null)
        {
            Posterior = (double[])Prior.Clone();
            PosteriorCovariance = covariance + tau * covariance;
            Omega = new Matrix(0, 0);
            return;
        }
        if (pick.Rows != k || pick.Columns != n) throw new ArgumentException("P must be K x N.");
        Omega = omega ?? HeLittermanOmega(pick, covariance, tau);

        var tauSigma = tau * covariance;
        var tsPt = tauSigma * pick.Transpose();            // N x K
        var middle = pick * tsPt + Omega;                   // K x K
        var middleInv = middle.Inverse();
        var gain = tsPt * middleInv;                        // N x K

        var residual = new double[k];
        var pPi = pick.Multiply(Prior);
        for (int i = 0; i < k; i++) residual[i] = viewReturns![i] - pPi[i];
        var adj = gain.Multiply(residual);
        Posterior = new double[n];
        for (int i = 0; i < n; i++) Posterior[i] = Prior[i] + adj[i];

        var m = tauSigma - gain * tsPt.Transpose();
        PosteriorCovariance = covariance + m;
    }

    /// <summary>Builds the model from a <see cref="BlackLittermanViews"/> set.</summary>
    public static BlackLittermanModel Create(Matrix covariance, double[] priorReturns, BlackLittermanViews views, double tau = 0.05, OmegaMethod omegaMethod = OmegaMethod.HeLitterman)
    {
        if (views.Count == 0) return new BlackLittermanModel(covariance, priorReturns, tau: tau, assets: views.Assets);
        var p = views.P;
        var omega = omegaMethod == OmegaMethod.Idzorek
            ? IdzorekOmega(views.Confidences, p, covariance, tau)
            : HeLittermanOmega(p, covariance, tau);
        return new BlackLittermanModel(covariance, priorReturns, p, views.Q, omega, tau, views.Assets);
    }

    public IReadOnlyList<string> Assets { get; }
    public Matrix Covariance { get; }
    public double[] Prior { get; }
    public double Tau { get; }
    public Matrix Omega { get; }

    /// <summary>Posterior expected returns.</summary>
    public double[] Posterior { get; }

    /// <summary>Posterior covariance of returns (Σ + M).</summary>
    public Matrix PosteriorCovariance { get; }

    /// <summary>
    /// Unconstrained optimal weights w ∝ (δΣ)⁻¹ μ_post, normalised to sum to one.
    /// </summary>
    public PortfolioWeights Weights(double riskAversion = 1)
    {
        var raw = (riskAversion * Covariance).Solve(Posterior);
        double s = raw.Sum();
        for (int i = 0; i < raw.Length; i++) raw[i] /= s;
        return new PortfolioWeights(raw, Assets);
    }

    /// <summary>Market-implied prior returns π = δ Σ w_mkt + rf, with w_mkt proportional to market caps.</summary>
    public static double[] MarketImpliedPriorReturns(double[] marketCaps, Matrix covariance, double riskAversion = 1, double riskFreeRate = 0)
    {
        double total = marketCaps.Sum();
        var w = marketCaps.Select(c => c / total).ToArray();
        var pi = covariance.Multiply(w);
        for (int i = 0; i < pi.Length; i++) pi[i] = riskAversion * pi[i] + riskFreeRate;
        return pi;
    }

    /// <summary>Market-implied risk aversion δ = (E[R_m] − rf) / Var(R_m), annualised from market prices.</summary>
    public static double MarketImpliedRiskAversion(ReadOnlySpan<double> marketPrices, double periodsPerYear = 252, double riskFreeRate = 0)
    {
        if (marketPrices.Length < 3) throw new ArgumentException("Need at least three prices.");
        var r = new double[marketPrices.Length - 1];
        for (int i = 1; i < marketPrices.Length; i++) r[i - 1] = marketPrices[i] / marketPrices[i - 1] - 1;
        double mean = Stats.Mean(r) * periodsPerYear;
        double variance = Stats.Variance(r) * periodsPerYear;
        return (mean - riskFreeRate) / variance;
    }

    /// <summary>Ω = diag(τ P Σ Pᵀ).</summary>
    public static Matrix HeLittermanOmega(Matrix pick, Matrix covariance, double tau)
    {
        var full = pick * covariance * pick.Transpose();
        var omega = new Matrix(pick.Rows, pick.Rows);
        for (int i = 0; i < pick.Rows; i++) omega[i, i] = tau * full[i, i];
        return omega;
    }

    /// <summary>
    /// Idzorek's method in its closed form: Ωₖ = τ · (1 − cₖ)/cₖ · pₖΣpₖᵀ. A confidence of 1 gives Ωₖ = 0 (the view
    /// is matched exactly); 0 gives a very large variance (the view is ignored).
    /// </summary>
    public static Matrix IdzorekOmega(double[] confidences, Matrix pick, Matrix covariance, double tau)
    {
        int k = pick.Rows;
        if (confidences.Length != k) throw new ArgumentException("One confidence per view is required.");
        var omega = new Matrix(k, k);
        var full = pick * covariance * pick.Transpose();
        for (int i = 0; i < k; i++)
        {
            double c = confidences[i];
            omega[i, i] = c <= 1e-12 ? 1e6 : tau * (1 - c) / c * full[i, i];
        }
        return omega;
    }
}
