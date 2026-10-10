using System.Collections.Concurrent;
using System.Text.Json;
using Finance.Core.Market;

namespace Finance.MarketData;

/// <summary>
/// Caches bars and quotes of an inner provider in memory and, optionally, on disk as JSON.
/// Bar requests are keyed by (symbol, interval, start, end); quotes expire after <see cref="QuoteTtl"/>.
/// </summary>
public sealed class CachingProvider : IMarketDataProvider
{
    private readonly IMarketDataProvider _inner;
    private readonly ConcurrentDictionary<string, (DateTime Stored, Bar[] Bars)> _bars = new();
    private readonly ConcurrentDictionary<string, MarketSnapshot> _quotes = new();

    public CachingProvider(IMarketDataProvider inner, string? diskDirectory = null)
    {
        _inner = inner;
        DiskDirectory = diskDirectory;
        if (diskDirectory is not null) Directory.CreateDirectory(diskDirectory);
    }

    public string Name => _inner.Name + "+Cache";

    public string? DiskDirectory { get; }

    /// <summary>How long a quote is served from cache.</summary>
    public TimeSpan QuoteTtl { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long bars are served from cache (historical bars rarely change).</summary>
    public TimeSpan BarTtl { get; init; } = TimeSpan.FromHours(12);

    /// <summary>Number of requests answered from cache.</summary>
    public int Hits => _hits;

    private int _hits;

    public async Task<Bar[]> GetBarsAsync(string symbol, BarInterval interval, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        var key = $"{_inner.Name}_{symbol}_{interval}_{start:yyyyMMddHHmm}_{end:yyyyMMddHHmm}";
        if (_bars.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.Stored < BarTtl)
        {
            Interlocked.Increment(ref _hits);
            return hit.Bars;
        }
        var file = DiskDirectory is null ? null : Path.Combine(DiskDirectory, Sanitize(key) + ".json");
        if (file is not null && File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < BarTtl)
        {
            await using var fs = File.OpenRead(file);
            var cached = await JsonSerializer.DeserializeAsync<Bar[]>(fs, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                _bars[key] = (DateTime.UtcNow, cached);
                Interlocked.Increment(ref _hits);
                return cached;
            }
        }
        var bars = await _inner.GetBarsAsync(symbol, interval, start, end, cancellationToken).ConfigureAwait(false);
        _bars[key] = (DateTime.UtcNow, bars);
        if (file is not null)
        {
            await using var fs = File.Create(file);
            await JsonSerializer.SerializeAsync(fs, bars, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return bars;
    }

    public async Task<MarketSnapshot> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        if (_quotes.TryGetValue(symbol, out var q) && DateTime.UtcNow - q.Time < QuoteTtl && q.Time <= DateTime.UtcNow)
        {
            Interlocked.Increment(ref _hits);
            return q;
        }
        var fresh = await _inner.GetQuoteAsync(symbol, cancellationToken).ConfigureAwait(false);
        _quotes[symbol] = fresh with { Time = DateTime.UtcNow };
        return fresh;
    }

    /// <summary>Clears the in-memory cache (disk files are kept).</summary>
    public void Clear()
    {
        _bars.Clear();
        _quotes.Clear();
    }

    private static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' ? '_' : c));
}

/// <summary>
/// Token-bucket limiter: at most <see cref="Capacity"/> calls per <see cref="Period"/>, waiting
/// asynchronously when the bucket is empty.
/// </summary>
public sealed class RateLimiter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;
    private double _tokens;
    private long _last;

    public RateLimiter(int capacity, TimeSpan period, TimeProvider? time = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        Period = period;
        _time = time ?? TimeProvider.System;
        _tokens = capacity;
        _last = _time.GetTimestamp();
    }

    public int Capacity { get; }

    public TimeSpan Period { get; }

    /// <summary>Waits until a call is allowed and consumes one token.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            TimeSpan wait;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Refill();
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return;
                }
                wait = TimeSpan.FromTicks((long)((1 - _tokens) * Period.Ticks / Capacity)) + TimeSpan.FromMilliseconds(1);
            }
            finally
            {
                _gate.Release();
            }
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        long now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_last, now);
        _last = now;
        _tokens = Math.Min(Capacity, _tokens + elapsed.Ticks * (double)Capacity / Period.Ticks);
    }
}

/// <summary>Applies a <see cref="RateLimiter"/> to every call of an inner provider.</summary>
public sealed class RateLimitedProvider : IMarketDataProvider
{
    private readonly IMarketDataProvider _inner;
    private readonly RateLimiter _limiter;

    public RateLimitedProvider(IMarketDataProvider inner, RateLimiter limiter)
    {
        _inner = inner;
        _limiter = limiter;
    }

    /// <summary>Alpha Vantage free tier: 5 calls per minute.</summary>
    public static RateLimitedProvider AlphaVantageFree(IMarketDataProvider inner) => new(inner, new RateLimiter(5, TimeSpan.FromMinutes(1)));

    public string Name => _inner.Name;

    public async Task<Bar[]> GetBarsAsync(string symbol, BarInterval interval, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        await _limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.GetBarsAsync(symbol, interval, start, end, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MarketSnapshot> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        await _limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.GetQuoteAsync(symbol, cancellationToken).ConfigureAwait(false);
    }
}
