using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Finance.Core;
using Finance.Core.Market;
using Finance.MarketData.Internal;

namespace Finance.MarketData.Streaming;

/// <summary>A streaming candle update. <see cref="IsFinal"/> is true once the candle has closed.</summary>
public readonly record struct BarUpdate(string Symbol, Bar Bar, bool IsFinal);

/// <summary>Real-time market data as async streams. Each enumeration owns its own connection.</summary>
public interface IMarketDataStream
{
    /// <summary>Trade prints.</summary>
    IAsyncEnumerable<Tick> TradesAsync(string symbol, CancellationToken cancellationToken = default);

    /// <summary>Candle updates (the forming candle is re-sent until final).</summary>
    IAsyncEnumerable<BarUpdate> BarsAsync(string symbol, BarInterval interval, CancellationToken cancellationToken = default);

    /// <summary>Order book snapshots.</summary>
    IAsyncEnumerable<OrderBook> OrderBooksAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default);
}

/// <summary>
/// Binance public WebSocket streams (trade, kline, partial depth). Reconnects with exponential
/// back-off when the connection drops; the stream ends only when the token is cancelled.
/// </summary>
public sealed class BinanceStream : IMarketDataStream
{
    public BinanceStream(string baseUrl = "wss://data-stream.binance.vision/ws") => BaseUrl = baseUrl.TrimEnd('/');

    public string BaseUrl { get; }

    public async IAsyncEnumerable<Tick> TradesAsync(string symbol, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var msg in Messages($"{Norm(symbol)}@trade", cancellationToken).ConfigureAwait(false))
            if (ParseTrade(msg) is { } t) yield return t;
    }

    public async IAsyncEnumerable<BarUpdate> BarsAsync(string symbol, BarInterval interval, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var code = Providers.BinanceProvider.IntervalCode(interval);
        await foreach (var msg in Messages($"{Norm(symbol)}@kline_{code}", cancellationToken).ConfigureAwait(false))
            if (ParseKline(msg) is { } b) yield return b;
    }

    public async IAsyncEnumerable<OrderBook> OrderBooksAsync(string symbol, int depth = 20, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int levels = depth <= 5 ? 5 : depth <= 10 ? 10 : 20;
        await foreach (var msg in Messages($"{Norm(symbol)}@depth{levels}@100ms", cancellationToken).ConfigureAwait(false))
        {
            using var doc = JsonDocument.Parse(msg);
            if (doc.RootElement.TryGetProperty("bids", out _))
                yield return Providers.BinanceProvider.ParseDepth(doc.RootElement, symbol.ToUpperInvariant(), depth);
        }
    }

    /// <summary>Parses a <c>@trade</c> message; returns null for other event types.</summary>
    public static Tick? ParseTrade(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (Http.Str(r, "e") != "trade") return null;
        // "m" = buyer is the maker, so the aggressor was the seller.
        var aggressor = r.TryGetProperty("m", out var m) && m.GetBoolean() ? Side.Sell : Side.Buy;
        return new Tick(Http.FromUnixMillis(r.GetProperty("T").GetInt64()), Http.Num(r, "p"), Http.Num(r, "q"), aggressor);
    }

    /// <summary>Parses a <c>@kline_*</c> message; returns null for other event types.</summary>
    public static BarUpdate? ParseKline(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (Http.Str(r, "e") != "kline") return null;
        var k = r.GetProperty("k");
        var bar = new Bar(Http.FromUnixMillis(k.GetProperty("t").GetInt64()), Http.Num(k, "o"), Http.Num(k, "h"), Http.Num(k, "l"), Http.Num(k, "c"), Http.Num(k, "v"));
        return new BarUpdate(Http.Str(r, "s") ?? "", bar, k.GetProperty("x").GetBoolean());
    }

    private static string Norm(string s) => Providers.BinanceProvider.NormalizeSymbol(s).ToLowerInvariant();

    private async IAsyncEnumerable<string> Messages(string stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var backoff = TimeSpan.FromMilliseconds(500);
        var buffer = new byte[1 << 16];
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            bool connected = true;
            try
            {
                await ws.ConnectAsync(new Uri($"{BaseUrl}/{stream}"), ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                connected = false;
            }
            if (connected)
            {
                backoff = TimeSpan.FromMilliseconds(500);
                var sb = new StringBuilder();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        yield break;
                    }
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;
                    var text = sb.ToString();
                    sb.Clear();
                    yield return text;
                }
            }
            if (ct.IsCancellationRequested) yield break;
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
        }
    }
}

/// <summary>
/// An offline live feed: a random walk with stochastic trade sizes, a book that re-centres on the
/// last price, and candles built from the generated ticks. Used by the Gallery's live dashboard and
/// order book viewer when no network is available.
/// </summary>
public sealed class SyntheticStream : IMarketDataStream
{
    public SyntheticStream(double startPrice = 100, double annualVolatility = 0.4, ulong seed = 11)
    {
        StartPrice = startPrice;
        AnnualVolatility = annualVolatility;
        Seed = seed;
    }

    public double StartPrice { get; }
    public double AnnualVolatility { get; }
    public ulong Seed { get; }

    /// <summary>Delay between generated events.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Simulated market seconds per generated tick (higher = faster price movement).</summary>
    public double SecondsPerTick { get; init; } = 60;

    public async IAsyncEnumerable<Tick> TradesAsync(string symbol, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rng = new FastRandom(Seed ^ Hash(symbol));
        double price = StartPrice;
        double sigma = AnnualVolatility * Math.Sqrt(SecondsPerTick / (252 * 6.5 * 3600));
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await Delay(cancellationToken).ConfigureAwait(false)) yield break;
            price *= Math.Exp(sigma * rng.NextGaussian());
            double size = Math.Round(Math.Exp(3 + rng.NextGaussian()), 2);
            yield return new Tick(DateTime.UtcNow, Math.Round(price, 4), size, rng.NextDouble() < 0.5 ? Side.Buy : Side.Sell);
        }
    }

    public async IAsyncEnumerable<BarUpdate> BarsAsync(string symbol, BarInterval interval, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var period = interval.ToTimeSpan();
        Bar? current = null;
        long bucket = -1;
        // Simulated clock: each tick advances SecondsPerTick so candles complete at demo speed.
        var clock = new DateTime(DateTime.UtcNow.Ticks / period.Ticks * period.Ticks, DateTimeKind.Utc);
        await foreach (var t in TradesAsync(symbol, cancellationToken).ConfigureAwait(false))
        {
            clock = clock.AddSeconds(SecondsPerTick);
            long b = clock.Ticks / period.Ticks;
            if (current is { } c && b != bucket)
            {
                yield return new BarUpdate(symbol, c, true);
                current = null;
            }
            bucket = b;
            current = current is { } cur
                ? cur with { High = Math.Max(cur.High, t.Price), Low = Math.Min(cur.Low, t.Price), Close = t.Price, Volume = cur.Volume + t.Size }
                : new Bar(new DateTime(b * period.Ticks, DateTimeKind.Utc), t.Price, t.Price, t.Price, t.Price, t.Size);
            yield return new BarUpdate(symbol, current.Value, false);
        }
    }

    public async IAsyncEnumerable<OrderBook> OrderBooksAsync(string symbol, int depth = 20, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ulong n = 0;
        await foreach (var t in TradesAsync(symbol, cancellationToken).ConfigureAwait(false))
        {
            double tick = Math.Pow(10, Math.Floor(Math.Log10(t.Price)) - 3);
            double mid = Math.Round(t.Price / tick) * tick;
            yield return SyntheticMarket.OrderBook(symbol, mid, depth, tick, Seed + n++);
        }
    }

    private async Task<bool> Delay(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TickInterval, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static ulong Hash(string s)
    {
        ulong h = 1469598103934665603UL;
        foreach (char c in s) h = (h ^ c) * 1099511628211UL;
        return h;
    }
}
