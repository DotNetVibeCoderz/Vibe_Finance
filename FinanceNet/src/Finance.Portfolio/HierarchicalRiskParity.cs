using Finance.Mathematics;

namespace Finance.Portfolio;

/// <summary>Agglomerative clustering linkage rule.</summary>
public enum LinkageMethod { Single, Complete, Average }

/// <summary>
/// One merge of the dendrogram, in SciPy's linkage-matrix convention: clusters 0..n−1 are the leaves and the
/// cluster created by step k gets id n + k.
/// </summary>
public readonly record struct LinkageStep(int Left, int Right, double Distance, int Size);

/// <summary>Result of Hierarchical Risk Parity.</summary>
/// <param name="Weights">Final weights.</param>
/// <param name="Order">Leaf order after quasi-diagonalisation (use it to sort a correlation heatmap).</param>
/// <param name="Linkage">Dendrogram merges, for plotting.</param>
public sealed record HrpResult(PortfolioWeights Weights, int[] Order, LinkageStep[] Linkage);

/// <summary>
/// Hierarchical Risk Parity (López de Prado, 2016): cluster assets on the correlation distance
/// √(½(1 − ρ)), reorder the covariance so similar assets are adjacent, then split capital top-down by
/// recursive bisection using the inverse-variance risk of each half.
/// </summary>
public static class HierarchicalRiskParity
{
    /// <summary>HRP weights from a covariance matrix.</summary>
    public static HrpResult Optimize(Matrix covariance, IReadOnlyList<string>? assets = null, LinkageMethod linkage = LinkageMethod.Single)
    {
        int n = covariance.Rows;
        if (!covariance.IsSquare || n == 0) throw new ArgumentException("Covariance must be a non-empty square matrix.");
        var corr = Stats.CovarianceToCorrelation(covariance);
        var dist = new Matrix(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                dist[i, j] = i == j ? 0 : Math.Sqrt(Math.Max(0, 0.5 * (1 - corr[i, j])));

        var steps = Cluster(dist, linkage);
        var order = QuasiDiagonal(steps, n);
        var w = RecursiveBisection(covariance, order);
        return new HrpResult(new PortfolioWeights(w, assets), order, steps);
    }

    /// <summary>HRP weights from a period-returns matrix (rows = periods).</summary>
    public static HrpResult OptimizeFromReturns(Matrix returns, IReadOnlyList<string>? assets = null, LinkageMethod linkage = LinkageMethod.Single)
        => Optimize(Stats.CovarianceMatrix(returns), assets, linkage);

    /// <summary>
    /// Agglomerative clustering on a distance matrix with Lance-Williams updates. O(n³), adequate for asset universes.
    /// </summary>
    public static LinkageStep[] Cluster(Matrix distance, LinkageMethod method = LinkageMethod.Single)
    {
        int n = distance.Rows;
        int total = 2 * n - 1;
        var d = new double[total, total];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) d[i, j] = distance[i, j];
        var size = new int[total];
        var active = new List<int>(Enumerable.Range(0, n));
        for (int i = 0; i < n; i++) size[i] = 1;
        var steps = new LinkageStep[n - 1];

        for (int k = 0; k < n - 1; k++)
        {
            double best = double.PositiveInfinity;
            int ba = -1, bb = -1;
            for (int x = 0; x < active.Count; x++)
                for (int y = x + 1; y < active.Count; y++)
                {
                    double v = d[active[x], active[y]];
                    if (v < best) { best = v; ba = active[x]; bb = active[y]; }
                }

            int id = n + k;
            size[id] = size[ba] + size[bb];
            steps[k] = new LinkageStep(Math.Min(ba, bb), Math.Max(ba, bb), best, size[id]);
            active.Remove(ba);
            active.Remove(bb);
            foreach (int c in active)
            {
                double v = method switch
                {
                    LinkageMethod.Single => Math.Min(d[ba, c], d[bb, c]),
                    LinkageMethod.Complete => Math.Max(d[ba, c], d[bb, c]),
                    _ => (size[ba] * d[ba, c] + size[bb] * d[bb, c]) / (size[ba] + size[bb]),
                };
                d[id, c] = v;
                d[c, id] = v;
            }
            active.Add(id);
        }
        return steps;
    }

    /// <summary>Leaf order of the dendrogram (left subtree before right), which quasi-diagonalises Σ.</summary>
    public static int[] QuasiDiagonal(LinkageStep[] steps, int leaves)
    {
        if (leaves == 1) return new[] { 0 };
        var order = new List<int>(leaves);
        var stack = new Stack<int>();
        stack.Push(leaves + steps.Length - 1);
        while (stack.Count > 0)
        {
            int c = stack.Pop();
            if (c < leaves) { order.Add(c); continue; }
            var s = steps[c - leaves];
            stack.Push(s.Right);
            stack.Push(s.Left);
        }
        return order.ToArray();
    }

    private static double[] RecursiveBisection(Matrix cov, int[] order)
    {
        int n = order.Length;
        var w = Enumerable.Repeat(1.0, n).ToArray();
        var clusters = new Queue<int[]>();
        clusters.Enqueue(order);
        while (clusters.Count > 0)
        {
            var c = clusters.Dequeue();
            if (c.Length < 2) continue;
            int half = c.Length / 2;
            var left = c[..half];
            var right = c[half..];
            double vl = ClusterVariance(cov, left), vr = ClusterVariance(cov, right);
            double alpha = 1 - vl / (vl + vr);
            foreach (int i in left) w[i] *= alpha;
            foreach (int i in right) w[i] *= 1 - alpha;
            clusters.Enqueue(left);
            clusters.Enqueue(right);
        }
        return w;
    }

    private static double ClusterVariance(Matrix cov, int[] members)
    {
        var ivp = new double[members.Length];
        double s = 0;
        for (int i = 0; i < members.Length; i++)
        {
            ivp[i] = 1 / cov[members[i], members[i]];
            s += ivp[i];
        }
        double v = 0;
        for (int i = 0; i < members.Length; i++)
            for (int j = 0; j < members.Length; j++)
                v += ivp[i] / s * cov[members[i], members[j]] * ivp[j] / s;
        return v;
    }
}
