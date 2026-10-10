using Finance.Core.Market;

namespace Finance.Portfolio;

/// <summary>Whole-share allocation of a budget.</summary>
/// <param name="Shares">Shares to buy per asset.</param>
/// <param name="Leftover">Cash left unspent.</param>
/// <param name="AchievedWeights">Resulting weights relative to the budget.</param>
public sealed record AllocationResult(IReadOnlyDictionary<string, int> Shares, double Leftover, PortfolioWeights AchievedWeights);

/// <summary>
/// Converts continuous weights into integer share counts (long-only).
/// </summary>
public static class DiscreteAllocation
{
    /// <summary>
    /// Greedy allocation: buy the floor of each target, then repeatedly buy one share of the affordable asset
    /// furthest below its target weight until nothing more can be bought.
    /// </summary>
    public static AllocationResult Greedy(PortfolioWeights weights, ReadOnlySpan<double> prices, double budget)
    {
        var (shares, leftover) = Floor(weights, prices, budget);
        int n = weights.Count;
        var p = prices.ToArray();
        while (true)
        {
            int best = -1;
            double bestDeficit = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                if (p[i] > leftover || weights[i] <= 0) continue;
                double deficit = weights[i] - shares[i] * p[i] / budget;
                if (deficit > bestDeficit) { bestDeficit = deficit; best = i; }
            }
            if (best < 0) break;
            shares[best]++;
            leftover -= p[best];
        }
        return Build(weights, p, shares, leftover, budget);
    }

    /// <summary>
    /// Largest-remainder rounding: floor every target, then spend the remainder on the assets with the largest
    /// fractional share counts first. Never buys more than one extra share per asset.
    /// </summary>
    public static AllocationResult LargestRemainder(PortfolioWeights weights, ReadOnlySpan<double> prices, double budget)
    {
        var (shares, leftover) = Floor(weights, prices, budget);
        var p = prices.ToArray();
        var order = Enumerable.Range(0, weights.Count)
            .Where(i => weights[i] > 0)
            .OrderByDescending(i =>
            {
                double exact = weights[i] * budget / p[i];
                return exact - Math.Floor(exact);
            });
        foreach (int i in order)
        {
            if (p[i] <= leftover)
            {
                shares[i]++;
                leftover -= p[i];
            }
        }
        return Build(weights, p, shares, leftover, budget);
    }

    private static (int[] Shares, double Leftover) Floor(PortfolioWeights weights, ReadOnlySpan<double> prices, double budget)
    {
        if (prices.Length != weights.Count) throw new ArgumentException("One price per asset is required.");
        Finance.Core.Guard.Positive(budget);
        var shares = new int[weights.Count];
        double spent = 0;
        for (int i = 0; i < shares.Length; i++)
        {
            if (weights[i] < 0) throw new ArgumentException("Discrete allocation is long-only.");
            if (!(prices[i] > 0)) throw new ArgumentException("Prices must be positive.");
            shares[i] = (int)Math.Floor(weights[i] * budget / prices[i] + 1e-9);
            spent += shares[i] * prices[i];
        }
        return (shares, budget - spent);
    }

    private static AllocationResult Build(PortfolioWeights weights, double[] prices, int[] shares, double leftover, double budget)
    {
        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        var achieved = new double[shares.Length];
        for (int i = 0; i < shares.Length; i++)
        {
            if (shares[i] > 0) dict[weights.Assets[i]] = shares[i];
            achieved[i] = shares[i] * prices[i] / budget;
        }
        return new AllocationResult(dict, leftover, new PortfolioWeights(achieved, weights.Assets));
    }
}

/// <summary>A trade needed to move from current holdings to target weights.</summary>
public readonly record struct RebalanceTrade(string Asset, Side Side, double Quantity, double Price)
{
    public double Value => Quantity * Price;
}

/// <summary>Computes rebalancing trade lists.</summary>
public static class Rebalancer
{
    /// <summary>
    /// Trades that move <paramref name="currentQuantities"/> to <paramref name="target"/> weights.
    /// Sells are listed before buys so the cash they raise funds the purchases.
    /// </summary>
    /// <param name="portfolioValue">Total value to allocate; defaults to the current holdings' market value.</param>
    /// <param name="minTradeValue">Trades smaller than this (in currency) are skipped.</param>
    /// <param name="wholeShares">Round quantities towards zero to whole shares.</param>
    public static IReadOnlyList<RebalanceTrade> Trades(
        IReadOnlyDictionary<string, double> currentQuantities,
        PortfolioWeights target,
        IReadOnlyDictionary<string, double> prices,
        double? portfolioValue = null,
        double minTradeValue = 0,
        bool wholeShares = true)
    {
        double value = portfolioValue ?? currentQuantities.Sum(kv => kv.Value * prices[kv.Key]);
        var assets = new HashSet<string>(target.Assets, StringComparer.Ordinal);
        assets.UnionWith(currentQuantities.Keys);
        var trades = new List<RebalanceTrade>();
        foreach (var asset in assets.OrderBy(a => a, StringComparer.Ordinal))
        {
            double price = prices.TryGetValue(asset, out var p) ? p : throw new KeyNotFoundException($"No price for '{asset}'.");
            double w = target.Assets.Contains(asset) ? target[asset] : 0;
            double desired = w * value / price;
            double current = currentQuantities.TryGetValue(asset, out var q) ? q : 0;
            double delta = desired - current;
            if (wholeShares) delta = Math.Truncate(delta);
            if (delta == 0 || Math.Abs(delta) * price < minTradeValue) continue;
            trades.Add(new RebalanceTrade(asset, delta > 0 ? Side.Buy : Side.Sell, Math.Abs(delta), price));
        }
        return trades.OrderBy(t => t.Side == Side.Buy ? 1 : 0).ToList();
    }
}
