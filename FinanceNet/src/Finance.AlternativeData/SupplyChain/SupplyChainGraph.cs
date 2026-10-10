namespace Finance.AlternativeData.SupplyChain;

/// <summary>A supplier → customer relationship. <see cref="Dependency"/> is the share of the customer's inputs (0-1).</summary>
public readonly record struct SupplyLink(string Supplier, string Customer, double Dependency);

/// <summary>Result of a contagion simulation: the stress each company ends up with (0 = unaffected, 1 = fully disrupted).</summary>
public sealed record ShockResult(IReadOnlyDictionary<string, double> Impact, int Rounds)
{
    /// <summary>Companies sorted by impact, largest first.</summary>
    public IEnumerable<KeyValuePair<string, double>> Ranked => Impact.OrderByDescending(kv => kv.Value);
}

/// <summary>
/// A directed supply-chain network (edges point from supplier to customer) with centrality,
/// exposure propagation and shock contagion.
/// </summary>
public sealed class SupplyChainGraph
{
    private readonly Dictionary<string, Dictionary<string, double>> _customers = new(); // supplier -> customer -> dependency
    private readonly Dictionary<string, Dictionary<string, double>> _suppliers = new(); // customer -> supplier -> dependency

    public IReadOnlyCollection<string> Companies => _customers.Keys;

    public int LinkCount => _customers.Values.Sum(d => d.Count);

    /// <summary>Adds (or replaces) a link.</summary>
    public SupplyChainGraph AddLink(string supplier, string customer, double dependency)
    {
        if (supplier == customer) throw new ArgumentException("A company cannot supply itself.");
        if (dependency < 0 || dependency > 1) throw new ArgumentOutOfRangeException(nameof(dependency), "Dependency must be in [0, 1].");
        Node(supplier);
        Node(customer);
        _customers[supplier][customer] = dependency;
        _suppliers[customer][supplier] = dependency;
        return this;
    }

    public SupplyChainGraph AddLinks(IEnumerable<SupplyLink> links)
    {
        foreach (var l in links) AddLink(l.Supplier, l.Customer, l.Dependency);
        return this;
    }

    /// <summary>Direct suppliers of a company with their dependency share.</summary>
    public IReadOnlyDictionary<string, double> SuppliersOf(string company) => _suppliers.TryGetValue(company, out var d) ? d : new Dictionary<string, double>();

    /// <summary>Direct customers of a company.</summary>
    public IReadOnlyDictionary<string, double> CustomersOf(string company) => _customers.TryGetValue(company, out var d) ? d : new Dictionary<string, double>();

    /// <summary>
    /// Weighted PageRank where importance flows from customers to the suppliers they depend on,
    /// so critical upstream suppliers rank highest. Dangling nodes redistribute uniformly.
    /// </summary>
    public Dictionary<string, double> PageRank(double damping = 0.85, int maxIterations = 200, double tolerance = 1e-12)
    {
        var nodes = _customers.Keys.ToList();
        int n = nodes.Count;
        if (n == 0) return new();
        var rank = nodes.ToDictionary(k => k, _ => 1.0 / n);
        for (int it = 0; it < maxIterations; it++)
        {
            var next = nodes.ToDictionary(k => k, _ => (1 - damping) / n);
            double dangling = 0;
            foreach (var c in nodes)
            {
                var sup = _suppliers[c];
                double total = sup.Values.Sum();
                if (total <= 0)
                {
                    dangling += rank[c];
                    continue;
                }
                foreach (var (s, w) in sup) next[s] += damping * rank[c] * w / total;
            }
            foreach (var k in nodes) next[k] += damping * dangling / n;
            double diff = nodes.Sum(k => Math.Abs(next[k] - rank[k]));
            rank = next;
            if (diff < tolerance) break;
        }
        return rank;
    }

    /// <summary>
    /// Total (direct + indirect) dependency of every company on <paramref name="supplier"/>:
    /// the sum over supply paths of the product of dependencies, damped by <paramref name="decay"/> per extra hop
    /// and capped at 1.
    /// </summary>
    public Dictionary<string, double> ExposureTo(string supplier, double decay = 1.0, int maxDepth = 6)
    {
        var exposure = new Dictionary<string, double>();
        void Walk(string node, double carried, int depth, HashSet<string> path)
        {
            if (depth > maxDepth) return;
            foreach (var (cust, dep) in CustomersOf(node))
            {
                if (path.Contains(cust)) continue;
                double e = carried * dep * (depth == 1 ? 1 : decay);
                exposure[cust] = Math.Min(1, exposure.GetValueOrDefault(cust) + e);
                path.Add(cust);
                Walk(cust, e, depth + 1, path);
                path.Remove(cust);
            }
        }
        Walk(supplier, 1.0, 1, new HashSet<string> { supplier });
        return exposure;
    }

    /// <summary>
    /// Iterative contagion: shocked companies start at their initial shock; each round a company's
    /// stress becomes max(own initial shock, Σ dependency·supplier stress · <paramref name="transmission"/>),
    /// capped at 1. Stops when nothing changes by more than 1e-9.
    /// </summary>
    public ShockResult SimulateShock(IReadOnlyDictionary<string, double> initialShocks, double transmission = 1.0, int maxRounds = 50)
    {
        var stress = _customers.Keys.ToDictionary(k => k, k => initialShocks.GetValueOrDefault(k));
        int round = 0;
        for (; round < maxRounds; round++)
        {
            double change = 0;
            var next = new Dictionary<string, double>(stress);
            foreach (var c in stress.Keys)
            {
                double inherited = 0;
                foreach (var (s, dep) in _suppliers[c]) inherited += dep * stress[s];
                double v = Math.Min(1, Math.Max(initialShocks.GetValueOrDefault(c), transmission * inherited));
                change = Math.Max(change, Math.Abs(v - stress[c]));
                next[c] = v;
            }
            stress = next;
            if (change < 1e-9) break;
        }
        return new ShockResult(stress, round + 1);
    }

    /// <summary>Single-company shock.</summary>
    public ShockResult SimulateShock(string company, double severity = 1.0, double transmission = 1.0)
        => SimulateShock(new Dictionary<string, double> { [company] = severity }, transmission);

    private void Node(string n)
    {
        if (!_customers.ContainsKey(n)) _customers[n] = new();
        if (!_suppliers.ContainsKey(n)) _suppliers[n] = new();
    }
}
