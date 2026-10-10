using Finance.Core;
using Finance.Core.Market;

namespace Finance.MarketData.Providers;

/// <summary>
/// Deterministic offline data: each symbol gets its own geometric Brownian motion whose seed,
/// start price, drift and volatility are derived from the symbol name. The same symbol and range
/// always return the same bars, so samples and tests are reproducible without a network.
/// </summary>
public sealed class SyntheticProvider : IMarketDataProvider, IOrderBookProvider
{
    public string Name => "Synthetic";

    /// <summary>Anchor date of every synthetic history.</summary>
    public DateTime Origin { get; init; } = new(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public Task<Bar[]> GetBarsAsync(string symbol, BarInterval interval, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        var p = Params(symbol);
        var step = interval.ToTimeSpan();
        if (end < start) return Task.FromResult(Array.Empty<Bar>());
        if (interval is BarInterval.Day1 or BarInterval.Week1 or BarInterval.Month1)
        {
            // Generate the full daily path from the origin so overlapping requests agree.
            int days = (int)Math.Ceiling((end - Origin).TotalDays * 5 / 7) + 10;
            if (days <= 0) return Task.FromResult(Array.Empty<Bar>());
            var daily = SyntheticMarket.GeometricBrownianBars(days, p.Price, p.Drift, p.Vol, p.Seed, Origin);
            var range = BarAggregator.Slice(daily, start, end);
            return Task.FromResult(interval == BarInterval.Day1 ? range : BarAggregator.Resample(range, step));
        }

        var rng = new FastRandom(p.Seed ^ (ulong)start.Ticks);
        double perYear = interval.PeriodsPerYear();
        double dt = 1 / perYear, price = p.Price;
        var bars = new List<Bar>();
        for (var t = new DateTime(start.Ticks / step.Ticks * step.Ticks, DateTimeKind.Utc); t <= end; t += step)
        {
            double open = price;
            double close = open * Math.Exp((p.Drift - 0.5 * p.Vol * p.Vol) * dt + p.Vol * Math.Sqrt(dt) * rng.NextGaussian());
            double wig = p.Vol * Math.Sqrt(dt) * 0.5;
            bars.Add(new Bar(t, open, Math.Max(open, close) * (1 + Math.Abs(rng.NextGaussian()) * wig),
                Math.Min(open, close) * (1 - Math.Abs(rng.NextGaussian()) * wig), close, Math.Round(5000 * Math.Exp(0.4 * rng.NextGaussian()))));
            price = close;
        }
        return Task.FromResult(bars.ToArray());
    }

    public async Task<MarketSnapshot> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var bars = await GetBarsAsync(symbol, BarInterval.Day1, now.AddDays(-10), now, cancellationToken).ConfigureAwait(false);
        var last = bars[^1];
        double spread = last.Close * 0.0002;
        return new MarketSnapshot(symbol, last.Time, last.Close, last.Close - spread / 2, last.Close + spread / 2, last.Volume, (last.Close / bars[^2].Close - 1) * 100);
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        var q = await GetQuoteAsync(symbol, cancellationToken).ConfigureAwait(false);
        double tick = Math.Pow(10, Math.Floor(Math.Log10(q.Last)) - 4);
        return SyntheticMarket.OrderBook(symbol, Math.Round(q.Last / tick) * tick, depth, tick, Params(symbol).Seed);
    }

    internal static (ulong Seed, double Price, double Drift, double Vol) Params(string symbol)
    {
        ulong h = 1469598103934665603UL;
        foreach (char c in symbol.ToUpperInvariant()) h = (h ^ c) * 1099511628211UL;
        var r = new FastRandom(h);
        return (h, Math.Round(20 + r.NextDouble() * 380, 2), -0.02 + r.NextDouble() * 0.2, 0.15 + r.NextDouble() * 0.35);
    }
}
