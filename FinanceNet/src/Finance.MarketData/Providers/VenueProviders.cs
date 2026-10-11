using System.Globalization;
using System.Text.Json;
using Finance.Core.Market;
using Finance.MarketData.Internal;

namespace Finance.MarketData.Providers;

/// <summary>
/// Alpaca Market Data API v2 for US stocks (<c>data.alpaca.markets</c>). Needs an API key pair (free tier: IEX feed).
/// Bars are paged with <c>next_page_token</c>.
/// </summary>
public sealed class AlpacaProvider : IMarketDataProvider
{
    private readonly HttpClient _http;
    private readonly string _keyId, _secret;

    public AlpacaProvider(string keyId, string secretKey, HttpClient? http = null, string baseUrl = "https://data.alpaca.markets", string feed = "iex")
    {
        _keyId = keyId;
        _secret = secretKey;
        _http = http ?? Http.Default;
        BaseUrl = baseUrl.TrimEnd('/');
        Feed = feed;
    }

    public string Name => "Alpaca";
    public string BaseUrl { get; }
    /// <summary>"iex" (free) or "sip" (paid consolidated feed).</summary>
    public string Feed { get; }

    private void Auth(HttpRequestMessage r)
    {
        r.Headers.TryAddWithoutValidation("APCA-API-KEY-ID", _keyId);
        r.Headers.TryAddWithoutValidation("APCA-API-SECRET-KEY", _secret);
    }

    public async Task<Bar[]> GetBarsAsync(string symbol, BarInterval interval, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        var bars = new List<Bar>();
        string? token = null;
        do
        {
            var url = $"{BaseUrl}/v2/stocks/{Http.Esc(symbol.ToUpperInvariant())}/bars?timeframe={Timeframe(interval)}&start={Rfc3339(start)}&end={Rfc3339(end)}" +
                      $"&limit=10000&adjustment=raw&feed={Feed}" + (token is null ? "" : $"&page_token={Http.Esc(token)}");
            using var doc = await Http.GetJsonAsync(_http, url, Name, cancellationToken, Auth).ConfigureAwait(false);
            bars.AddRange(ParseBars(doc.RootElement));
            token = Http.Str(doc.RootElement, "next_page_token");
        } while (!string.IsNullOrEmpty(token));
        return bars.ToArray();
    }

    public async Task<MarketSnapshot> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var sym = Http.Esc(symbol.ToUpperInvariant());
        using var q = await Http.GetJsonAsync(_http, $"{BaseUrl}/v2/stocks/{sym}/quotes/latest?feed={Feed}", Name, cancellationToken, Auth).ConfigureAwait(false);
        using var t = await Http.GetJsonAsync(_http, $"{BaseUrl}/v2/stocks/{sym}/trades/latest?feed={Feed}", Name, cancellationToken, Auth).ConfigureAwait(false);
        return ParseLatest(q.RootElement, t.RootElement, symbol);
    }

    public static Bar[] ParseBars(JsonElement root)
    {
        if (!root.TryGetProperty("bars", out var arr) || arr.ValueKind != JsonValueKind.Array) return Array.Empty<Bar>();
        return arr.EnumerateArray().Select(b => new Bar(DateTime.Parse(b.GetProperty("t").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
            Http.Num(b, "o"), Http.Num(b, "h"), Http.Num(b, "l"), Http.Num(b, "c"), Http.Num(b, "v"))).ToArray();
    }

    public static MarketSnapshot ParseLatest(JsonElement quote, JsonElement trade, string symbol)
    {
        var q = quote.GetProperty("quote");
        var t = trade.GetProperty("trade");
        var time = DateTime.Parse(t.GetProperty("t").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        return new MarketSnapshot(symbol, time, Http.Num(t, "p"), Http.Num(q, "bp"), Http.Num(q, "ap"));
    }

    internal static string Timeframe(BarInterval i) => i switch
    {
        BarInterval.Minute1 => "1Min", BarInterval.Minute5 => "5Min", BarInterval.Minute15 => "15Min", BarInterval.Minute30 => "30Min",
        BarInterval.Hour1 => "1Hour", BarInterval.Hour4 => "4Hour", BarInterval.Day1 => "1Day", BarInterval.Week1 => "1Week", BarInterval.Month1 => "1Month",
        _ => throw new ArgumentOutOfRangeException(nameof(i)),
    };

    internal static string Rfc3339(DateTime t) => Http.Esc(DateTime.SpecifyKind(t, t.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : t.Kind)
        .ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}

/// <summary>
/// OANDA v20 REST for FX and CFDs (<c>api-fxpractice.oanda.com</c> practice, <c>api-fxtrade.oanda.com</c> live).
/// Instruments use OANDA's form "EUR_USD"; "EUR/USD" and "EURUSD" are accepted. Candles are mid prices,
/// fetched in pages of up to 5,000.
/// </summary>
public sealed class OandaProvider : IMarketDataProvider
{
    public const string PracticeUrl = "https://api-fxpractice.oanda.com", LiveUrl = "https://api-fxtrade.oanda.com";
    private readonly HttpClient _http;
    private readonly string _token, _accountId;

    public OandaProvider(string token, string accountId, HttpClient? http = null, string baseUrl = PracticeUrl)
    {
        _token = token;
        _accountId = accountId;
        _http = http ?? Http.Default;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string Name => "OANDA";
    public string BaseUrl { get; }

    private void Auth(HttpRequestMessage r) => r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);

    /// <summary>"EUR/USD", "eurusd", "EUR_USD" → "EUR_USD" (six-letter pairs; other names pass through upper-cased).</summary>
    public static string Instrument(string symbol)
    {
        var s = symbol.Replace("/", "_").Replace("-", "_").ToUpperInvariant();
        return s.Length == 6 && !s.Contains('_') ? s[..3] + "_" + s[3..] : s;
    }

    public async Task<Bar[]> GetBarsAsync(string symbol, BarInterval interval, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        var bars = new List<Bar>();
        var from = start;
        while (from < end)
        {
            var url = $"{BaseUrl}/v3/instruments/{Instrument(symbol)}/candles?granularity={Granularity(interval)}&price=M" +
                      $"&from={AlpacaProvider.Rfc3339(from)}&count=5000";
            using var doc = await Http.GetJsonAsync(_http, url, Name, cancellationToken, Auth).ConfigureAwait(false);
            var page = ParseCandles(doc.RootElement).Where(b => b.Time <= end).ToArray();
            if (page.Length == 0) break;
            bars.AddRange(page.Where(b => bars.Count == 0 || b.Time > bars[^1].Time));
            if (page.Length < 4999) break;
            from = page[^1].Time.AddSeconds(1);
        }
        return bars.ToArray();
    }

    public async Task<MarketSnapshot> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        using var doc = await Http.GetJsonAsync(_http, $"{BaseUrl}/v3/accounts/{_accountId}/pricing?instruments={Instrument(symbol)}", Name, cancellationToken, Auth)
            .ConfigureAwait(false);
        return ParsePricing(doc.RootElement, symbol);
    }

    /// <summary>Completed mid-price candles (the still-forming last candle is skipped).</summary>
    public static Bar[] ParseCandles(JsonElement root)
        => root.GetProperty("candles").EnumerateArray()
            .Where(c => !c.TryGetProperty("complete", out var done) || done.GetBoolean())
            .Select(c =>
            {
                var m = c.GetProperty("mid");
                var t = DateTime.Parse(c.GetProperty("time").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                return new Bar(t, Http.Num(m, "o"), Http.Num(m, "h"), Http.Num(m, "l"), Http.Num(m, "c"), Http.Num(c, "volume"));
            }).ToArray();

    public static MarketSnapshot ParsePricing(JsonElement root, string symbol)
    {
        var p = root.GetProperty("prices")[0];
        double bid = Http.Num(p.GetProperty("bids")[0], "price"), ask = Http.Num(p.GetProperty("asks")[0], "price");
        var t = DateTime.Parse(p.GetProperty("time").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        return new MarketSnapshot(symbol, t, (bid + ask) / 2, bid, ask);
    }

    internal static string Granularity(BarInterval i) => i switch
    {
        BarInterval.Minute1 => "M1", BarInterval.Minute5 => "M5", BarInterval.Minute15 => "M15", BarInterval.Minute30 => "M30",
        BarInterval.Hour1 => "H1", BarInterval.Hour4 => "H4", BarInterval.Day1 => "D", BarInterval.Week1 => "W", BarInterval.Month1 => "M",
        _ => throw new ArgumentOutOfRangeException(nameof(i)),
    };
}

/// <summary>
/// Bybit v5 public market data (no key) for spot, linear or inverse contracts. Klines come newest first and are
/// re-ordered; pages of 1,000 are walked backwards from the end time.
/// </summary>
public sealed class BybitProvider : IMarketDataProvider, IOrderBookProvider
{
    private readonly HttpClient _http;

    public BybitProvider(HttpClient? http = null, string baseUrl = "https://api.bybit.com", string category = "spot")
    {
        _http = http ?? Http.Default;
        BaseUrl = baseUrl.TrimEnd('/');
        Category = category;
    }

    public string Name => "Bybit";
    public string BaseUrl { get; }
    /// <summary>"spot", "linear" or "inverse".</summary>
    public string Category { get; }

    public async Task<Bar[]> GetBarsAsync(string symbol, BarInterval interval, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        var sym = BinanceProvider.NormalizeSymbol(symbol);
        var bars = new List<Bar>();
        long from = Http.UnixMillis(start), to = Http.UnixMillis(end);
        while (to >= from)
        {
            var url = $"{BaseUrl}/v5/market/kline?category={Category}&symbol={sym}&interval={IntervalCode(interval)}&start={from}&end={to}&limit=1000";
            using var doc = await Http.GetJsonAsync(_http, url, Name, cancellationToken).ConfigureAwait(false);
            var page = ParseKlines(doc.RootElement);
            if (page.Length == 0) break;
            bars.InsertRange(0, page.Where(b => bars.Count == 0 || b.Time < bars[0].Time));
            if (page.Length < 1000) break;
            to = Http.UnixMillis(page[0].Time) - 1;
        }
        return bars.ToArray();
    }

    public async Task<MarketSnapshot> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        using var doc = await Http.GetJsonAsync(_http, $"{BaseUrl}/v5/market/tickers?category={Category}&symbol={BinanceProvider.NormalizeSymbol(symbol)}",
            Name, cancellationToken).ConfigureAwait(false);
        return ParseTicker(doc.RootElement, symbol);
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        using var doc = await Http.GetJsonAsync(_http, $"{BaseUrl}/v5/market/orderbook?category={Category}&symbol={BinanceProvider.NormalizeSymbol(symbol)}&limit={Math.Clamp(depth, 1, 200)}",
            Name, cancellationToken).ConfigureAwait(false);
        return ParseOrderBook(doc.RootElement, symbol);
    }

    private static JsonElement Result(JsonElement root)
    {
        if (Http.Num(root, "retCode") != 0) throw new MarketDataException($"Bybit error {Http.Str(root, "retCode")}: {Http.Str(root, "retMsg")}");
        return root.GetProperty("result");
    }

    /// <summary>Parses <c>/v5/market/kline</c> (newest first on the wire) into oldest-first bars.</summary>
    public static Bar[] ParseKlines(JsonElement root)
        => Result(root).GetProperty("list").EnumerateArray()
            .Select(k => new Bar(Http.FromUnixMillis(long.Parse(k[0].GetString()!, CultureInfo.InvariantCulture)),
                Http.Num(k[1]), Http.Num(k[2]), Http.Num(k[3]), Http.Num(k[4]), Http.Num(k[5])))
            .OrderBy(b => b.Time).ToArray();

    public static MarketSnapshot ParseTicker(JsonElement root, string symbol)
    {
        var r = Result(root);
        var t = r.GetProperty("list")[0];
        var time = root.TryGetProperty("time", out var ts) ? Http.FromUnixMillis(ts.GetInt64()) : DateTime.UtcNow;
        return new MarketSnapshot(symbol, time, Http.Num(t, "lastPrice"), Http.Num(t, "bid1Price"), Http.Num(t, "ask1Price"),
            Http.Num(t, "volume24h"), 100 * Http.Num(t, "price24hPcnt"));
    }

    public static OrderBook ParseOrderBook(JsonElement root, string symbol)
    {
        var r = Result(root);
        var book = new OrderBook(symbol, Http.FromUnixMillis(r.GetProperty("ts").GetInt64()));
        book.SetBids(BinanceProvider.Levels(r.GetProperty("b"), int.MaxValue));
        book.SetAsks(BinanceProvider.Levels(r.GetProperty("a"), int.MaxValue));
        return book;
    }

    internal static string IntervalCode(BarInterval i) => i switch
    {
        BarInterval.Minute1 => "1", BarInterval.Minute5 => "5", BarInterval.Minute15 => "15", BarInterval.Minute30 => "30",
        BarInterval.Hour1 => "60", BarInterval.Hour4 => "240", BarInterval.Day1 => "D", BarInterval.Week1 => "W", BarInterval.Month1 => "M",
        _ => throw new ArgumentOutOfRangeException(nameof(i)),
    };
}

/// <summary>
/// OKX v5 public market data (no key). Instruments use OKX's "BTC-USDT" form ("BTCUSDT" and "BTC/USDT" are
/// accepted for USDT/USDC/USD quotes). History is read from <c>history-candles</c>, 100 per page, newest first.
/// </summary>
public sealed class OkxProvider : IMarketDataProvider, IOrderBookProvider
{
    private readonly HttpClient _http;

    public OkxProvider(HttpClient? http = null, string baseUrl = "https://www.okx.com")
    {
        _http = http ?? Http.Default;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string Name => "OKX";
    public string BaseUrl { get; }

    public static string InstrumentId(string symbol)
    {
        var s = symbol.Replace("/", "-").Replace("_", "-").ToUpperInvariant();
        if (s.Contains('-')) return s;
        foreach (var quote in new[] { "USDT", "USDC", "USD", "BTC", "ETH" })
            if (s.EndsWith(quote, StringComparison.Ordinal) && s.Length > quote.Length) return s[..^quote.Length] + "-" + quote;
        return s;
    }

    public async Task<Bar[]> GetBarsAsync(string symbol, BarInterval interval, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        var bars = new List<Bar>();
        long startMs = Http.UnixMillis(start), after = Http.UnixMillis(end) + 1;
        while (true)
        {
            // "after" = return records earlier than this timestamp.
            var url = $"{BaseUrl}/api/v5/market/history-candles?instId={InstrumentId(symbol)}&bar={BarCode(interval)}&after={after}&limit=100";
            using var doc = await Http.GetJsonAsync(_http, url, Name, cancellationToken).ConfigureAwait(false);
            var page = ParseCandles(doc.RootElement);
            if (page.Length == 0) break;
            bars.InsertRange(0, page.Where(b => Http.UnixMillis(b.Time) >= startMs && (bars.Count == 0 || b.Time < bars[0].Time)));
            if (page.Length < 100 || Http.UnixMillis(page[0].Time) <= startMs) break;
            after = Http.UnixMillis(page[0].Time);
        }
        return bars.ToArray();
    }

    public async Task<MarketSnapshot> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        using var doc = await Http.GetJsonAsync(_http, $"{BaseUrl}/api/v5/market/ticker?instId={InstrumentId(symbol)}", Name, cancellationToken).ConfigureAwait(false);
        return ParseTicker(doc.RootElement, symbol);
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        using var doc = await Http.GetJsonAsync(_http, $"{BaseUrl}/api/v5/market/books?instId={InstrumentId(symbol)}&sz={Math.Clamp(depth, 1, 400)}",
            Name, cancellationToken).ConfigureAwait(false);
        return ParseOrderBook(doc.RootElement, symbol);
    }

    private static JsonElement Data(JsonElement root)
    {
        if (Http.Str(root, "code") != "0") throw new MarketDataException($"OKX error {Http.Str(root, "code")}: {Http.Str(root, "msg")}");
        return root.GetProperty("data");
    }

    /// <summary>Parses candles (newest first on the wire) into confirmed, oldest-first bars.</summary>
    public static Bar[] ParseCandles(JsonElement root)
        => Data(root).EnumerateArray()
            .Where(k => k.GetArrayLength() < 9 || k[8].GetString() != "0")       // confirm = 0: still forming
            .Select(k => new Bar(Http.FromUnixMillis(long.Parse(k[0].GetString()!, CultureInfo.InvariantCulture)),
                Http.Num(k[1]), Http.Num(k[2]), Http.Num(k[3]), Http.Num(k[4]), Http.Num(k[5])))
            .OrderBy(b => b.Time).ToArray();

    public static MarketSnapshot ParseTicker(JsonElement root, string symbol)
    {
        var t = Data(root)[0];
        double last = Http.Num(t, "last"), open = Http.Num(t, "open24h");
        return new MarketSnapshot(symbol, Http.FromUnixMillis(long.Parse(Http.Str(t, "ts")!, CultureInfo.InvariantCulture)), last,
            Http.Num(t, "bidPx"), Http.Num(t, "askPx"), Http.Num(t, "vol24h"), open > 0 ? 100 * (last / open - 1) : double.NaN);
    }

    public static OrderBook ParseOrderBook(JsonElement root, string symbol)
    {
        var d = Data(root)[0];
        var book = new OrderBook(symbol, Http.FromUnixMillis(long.Parse(Http.Str(d, "ts")!, CultureInfo.InvariantCulture)));
        book.SetBids(BinanceProvider.Levels(d.GetProperty("bids"), int.MaxValue));
        book.SetAsks(BinanceProvider.Levels(d.GetProperty("asks"), int.MaxValue));
        return book;
    }

    internal static string BarCode(BarInterval i) => i switch
    {
        BarInterval.Minute1 => "1m", BarInterval.Minute5 => "5m", BarInterval.Minute15 => "15m", BarInterval.Minute30 => "30m",
        BarInterval.Hour1 => "1H", BarInterval.Hour4 => "4H", BarInterval.Day1 => "1Dutc", BarInterval.Week1 => "1Wutc", BarInterval.Month1 => "1Mutc",
        _ => throw new ArgumentOutOfRangeException(nameof(i)),
    };
}
