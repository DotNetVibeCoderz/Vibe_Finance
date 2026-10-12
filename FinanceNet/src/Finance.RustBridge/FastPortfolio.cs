using Finance.Mathematics;
using Finance.Portfolio;

namespace Finance.RustBridge;

/// <summary>Portfolio construction on the Rust accelerator with managed fallback.</summary>
public static class FastPortfolio
{
    /// <summary>
    /// Hierarchical Risk Parity, identical to <see cref="HierarchicalRiskParity.Optimize"/> (same clustering, tie-breaking,
    /// leaf order and weights); the O(n³) clustering runs natively when the Rust accelerator is available.
    /// </summary>
    public static unsafe HrpResult Hrp(Matrix covariance, IReadOnlyList<string>? assets = null, LinkageMethod linkage = LinkageMethod.Single)
    {
        int n = covariance.Rows;
        if (!RustAccelerator.IsAvailable || !covariance.IsSquare || n == 0)
            return HierarchicalRiskParity.Optimize(covariance, assets, linkage);
        var cov = covariance.Data;
        var w = new double[n];
        var order = new nuint[n];
        int m = Math.Max(1, n - 1);
        var left = new nuint[m];
        var right = new nuint[m];
        var dist = new double[m];
        var size = new nuint[m];
        int status;
        fixed (double* c = cov, pw = w, pd = dist)
        fixed (nuint* po = order, pl = left, pr = right, ps = size)
            status = NativeMethods.Hrp(c, (nuint)n, (uint)linkage, pw, po, pl, pr, pd, ps);
        if (status == NativeMethods.ErrArg) return HierarchicalRiskParity.Optimize(covariance, assets, linkage);
        NativeMethods.Check(status, "fnat_hrp");
        var steps = new LinkageStep[n - 1];
        for (int k = 0; k < n - 1; k++) steps[k] = new LinkageStep((int)left[k], (int)right[k], dist[k], (int)size[k]);
        return new HrpResult(new PortfolioWeights(w, assets), order.Select(o => (int)o).ToArray(), steps);
    }
}
