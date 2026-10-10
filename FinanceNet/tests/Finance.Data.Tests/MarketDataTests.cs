using System.Text.Json;
using Finance.Core.Market;
using Finance.MarketData;
using Finance.MarketData.Economic;
using Finance.MarketData.Fundamentals;
using Finance.MarketData.Providers;
using Finance.MarketData.Streaming;

namespace Finance.Data.Tests;

public class MarketDataTests
{
    private static readonly DateTime Start = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Yahoo_parses_chart_and_skips_null_rows()
    {
        var h = new FakeHandler().On("/v8/finance/chart/AAPL", Payloads.YahooChart);
        var p = new YahooFinanceProvider(h.Client());
        var bars = await p.GetBarsAsync("AAPL", BarInterval.Day1, Start, End, TestContext.Current.CancellationToken);
        Assert.Equal(4, bars.Length);
        Assert.Equal(333.69, bars[0].Close, 6);
        Assert.Equal(34400900, bars[0].Volume);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791207000).UtcDateTime, bars[0].Time);
        Assert.Contains("interval=1d", h.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public void Yahoo_adjusted_close_scales_ohlc()
    {
        using var doc = JsonDocument.Parse(Payloads.YahooChart);
        var bars = YahooFinanceProvider.ParseChart(doc.RootElement, adjusted: true);
        Assert.Equal(333.0, bars[0].Close, 6);
        Assert.Equal(332.82000732421875 * 333.0 / 333.69, bars[0].Open, 6);
    }

    [Fact]
    public async Task Yahoo_quote_and_error()
    {
        var h = new FakeHandler().On("AAPL", Payloads.YahooChart).On("ZZZZ", Payloads.YahooError);
        var p = new YahooFinanceProvider(h.Client());
        var q = await p.GetQuoteAsync("AAPL", TestContext.Current.CancellationToken);
        Assert.Equal(336.64, q.Last);
        Assert.Equal((336.64 / 333.69 - 1) * 100, q.ChangePercent, 6);
        await Assert.ThrowsAsync<MarketDataException>(() => p.GetQuoteAsync("ZZZZ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Binance_klines_ticker_depth()
    {
        var h = new FakeHandler().On("/klines", Payloads.BinanceKlines).On("/ticker/24hr", Payloads.BinanceTicker).On("/depth", Payloads.BinanceDepth);
        var p = new BinanceProvider(h.Client());
        var ct = TestContext.Current.CancellationToken;
        var bars = await p.GetBarsAsync("BTC-USDT", BarInterval.Day1, Start, End, ct);
        Assert.Equal(2, bars.Length);
        Assert.Equal(82635.55, bars[0].Close);
        Assert.Contains("symbol=BTCUSDT", h.Requests[0].RequestUri!.Query);
        var q = await p.GetQuoteAsync("BTCUSDT", ct);
        Assert.Equal(82775.06, q.Last);
        Assert.Equal(0.341, q.ChangePercent);
        var book = await p.GetOrderBookAsync("BTCUSDT", 2, ct);
        Assert.Equal(82775.05, book.BestBid);
        Assert.Equal(82775.06, book.BestAsk);
        Assert.Equal(2, book.Bids.Count);
    }

    [Fact]
    public async Task Coinbase_candles_are_reordered_and_mapped()
    {
        var h = new FakeHandler().On("/candles", Payloads.CoinbaseCandles).On("/ticker", Payloads.CoinbaseTicker);
        var p = new CoinbaseProvider(h.Client());
        var ct = TestContext.Current.CancellationToken;
        var bars = await p.GetBarsAsync("BTCUSD", BarInterval.Day1, new DateTime(2023, 11, 14, 0, 0, 0, DateTimeKind.Utc), new DateTime(2023, 11, 16, 0, 0, 0, DateTimeKind.Utc), ct);
        Assert.Equal(2, bars.Length);
        Assert.True(bars[0].Time < bars[1].Time);
        Assert.Equal(36100.0, bars[0].Open);
        Assert.Equal(36900.0, bars[0].High);
        Assert.Equal(36000.0, bars[0].Low);
        Assert.Contains("BTC-USD", h.Requests[0].RequestUri!.AbsolutePath);
        var q = await p.GetQuoteAsync("BTC-USD", ct);
        Assert.Equal(37001.0, q.Last);
        Assert.Equal(37000.90, q.Bid);
    }

    [Fact]
    public async Task AlphaVantage_prices_quote_macro_fundamentals()
    {
        var h = new FakeHandler()
            .On("TIME_SERIES_DAILY", Payloads.AlphaDaily).On("GLOBAL_QUOTE", Payloads.AlphaQuote)
            .On("REAL_GDP", Payloads.AlphaGdp).On("OVERVIEW", Payloads.AlphaOverview)
            .On("INCOME_STATEMENT", Payloads.AlphaIncome).On("BALANCE_SHEET", Payloads.AlphaBalance);
        var av = new AlphaVantageProvider("demo", h.Client());
        var ct = TestContext.Current.CancellationToken;
        var bars = await av.GetBarsAsync("IBM", BarInterval.Day1, Start, End, ct);
        Assert.Equal(2, bars.Length);
        Assert.Equal(161.5, bars[0].Close);
        var q = await av.GetQuoteAsync("IBM", ct);
        Assert.Equal(160.1, q.Last);
        Assert.Equal(-0.8669, q.ChangePercent, 6);
        var gdp = await av.GetIndicatorAsync(EconomicIndicator.RealGdp, ct);
        Assert.Equal(2, gdp.Observations.Length);
        Assert.Equal(5765.2, gdp.Latest.Value);
        var ov = await av.GetOverviewAsync("IBM", ct);
        Assert.Equal("TECHNOLOGY", ov.Sector);
        Assert.Equal(22.5, ov.PeRatio);
        var inc = await av.GetIncomeStatementsAsync("IBM", false, ct);
        var bal = await av.GetBalanceSheetsAsync("IBM", false, ct);
        var r = FundamentalRatios.Compute(inc[0], bal[0], inc[1]);
        Assert.Equal(34300.0 / 61860, r.GrossMargin, 9);
        Assert.Equal(32908.0 / 34122, r.CurrentRatio, 9);
        Assert.Equal(61860.0 / 60530 - 1, r.RevenueGrowth, 9);
        Assert.Equal(56548.0 / 22533, r.DebtToEquity, 9);
    }

    [Fact]
    public async Task AlphaVantage_rate_limit_note_becomes_exception()
    {
        var h = new FakeHandler().On("query", """{"Note":"Thank you for using Alpha Vantage! Our standard API call frequency is 5 calls per minute"}""");
        var av = new AlphaVantageProvider("demo", h.Client());
        var ex = await Assert.ThrowsAsync<MarketDataException>(() => av.GetQuoteAsync("IBM", TestContext.Current.CancellationToken));
        Assert.Contains("5 calls", ex.Message);
    }

    [Fact]
    public async Task Polygon_aggregates()
    {
        var h = new FakeHandler().On("/v2/aggs", Payloads.Polygon);
        var p = new PolygonProvider("key", h.Client());
        var bars = await p.GetBarsAsync("AAPL", BarInterval.Day1, Start, End, TestContext.Current.CancellationToken);
        Assert.Equal(2, bars.Length);
        Assert.Equal(130.15, bars[0].Close);
        Assert.Contains("apiKey=key", h.Requests[0].RequestUri!.Query);
        Assert.Contains("/range/1/day/", h.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Csv_round_trip_and_header_detection()
    {
        var dir = Directory.CreateTempSubdirectory("fnet").FullName;
        var bars = SyntheticMarket.GeometricBrownianBars(30, start: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await File.WriteAllTextAsync(Path.Combine(dir, "XYZ.csv"), CsvProvider.Format(bars), TestContext.Current.CancellationToken);
        var p = new CsvProvider(dir);
        var back = await p.GetBarsAsync("XYZ", BarInterval.Day1, DateTime.MinValue, DateTime.MaxValue, TestContext.Current.CancellationToken);
        Assert.Equal(30, back.Length);
        Assert.Equal(bars[5].Close, back[5].Close, 9);

        var semi = CsvProvider.Parse("Date;Open;High;Low;Close;Adj Close;Volume\n2024-01-02;1;2;0.5;1.5;1.4;100\n2024-01-01;1;2;0.5;1.2;1.1;50\n");
        Assert.Equal(2, semi.Length);
        Assert.Equal(1.2, semi[0].Close);
    }

    [Fact]
    public async Task Synthetic_is_deterministic_and_consistent_across_ranges()
    {
        var p = new SyntheticProvider();
        var ct = TestContext.Current.CancellationToken;
        var a = await p.GetBarsAsync("AAPL", BarInterval.Day1, new DateTime(2023, 1, 1), new DateTime(2023, 12, 31), ct);
        var b = await p.GetBarsAsync("AAPL", BarInterval.Day1, new DateTime(2023, 6, 1), new DateTime(2023, 6, 30), ct);
        Assert.True(a.Length > 240);
        Assert.Equal(a.First(x => x.Time == b[0].Time).Close, b[0].Close);
        var c = await p.GetBarsAsync("MSFT", BarInterval.Day1, new DateTime(2023, 1, 1), new DateTime(2023, 12, 31), ct);
        Assert.NotEqual(a[0].Close, c[0].Close);
        var q = await p.GetQuoteAsync("AAPL", ct);
        Assert.True(q.Ask > q.Bid);
        var book = await p.GetOrderBookAsync("AAPL", 10, ct);
        Assert.Equal(10, book.Asks.Count);
        var weekly = await p.GetBarsAsync("AAPL", BarInterval.Week1, new DateTime(2023, 1, 1), new DateTime(2023, 12, 31), ct);
        Assert.InRange(weekly.Length, 50, 54);
    }

    [Fact]
    public void Resample_aggregates_ohlcv()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bars = Enumerable.Range(0, 8).Select(i => new Bar(t0.AddHours(i), 10 + i, 11 + i, 9 + i, 10.5 + i, 100)).ToArray();
        var r = BarAggregator.Resample(bars, TimeSpan.FromHours(4));
        Assert.Equal(2, r.Length);
        Assert.Equal(10, r[0].Open);
        Assert.Equal(14, r[0].High);
        Assert.Equal(9, r[0].Low);
        Assert.Equal(13.5, r[0].Close);
        Assert.Equal(400, r[0].Volume);
    }

    [Fact]
    public async Task Caching_serves_repeat_requests_from_memory_and_disk()
    {
        var h = new FakeHandler().On("/klines", Payloads.BinanceKlines);
        var dir = Directory.CreateTempSubdirectory("fnetcache").FullName;
        var ct = TestContext.Current.CancellationToken;
        var cache = new CachingProvider(new BinanceProvider(h.Client()), dir);
        await cache.GetBarsAsync("BTCUSDT", BarInterval.Day1, Start, End, ct);
        var again = await cache.GetBarsAsync("BTCUSDT", BarInterval.Day1, Start, End, ct);
        Assert.Single(h.Requests);
        Assert.Equal(1, cache.Hits);
        var fresh = new CachingProvider(new BinanceProvider(h.Client()), dir);
        var fromDisk = await fresh.GetBarsAsync("BTCUSDT", BarInterval.Day1, Start, End, ct);
        Assert.Single(h.Requests);
        Assert.Equal(again, fromDisk);
    }

    [Fact]
    public async Task RateLimiter_spaces_calls()
    {
        var limiter = new RateLimiter(2, TimeSpan.FromMilliseconds(200));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 4; i++) await limiter.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(sw.ElapsedMilliseconds >= 150, $"elapsed {sw.ElapsedMilliseconds}");
    }

    [Fact]
    public void Binance_stream_messages_parse()
    {
        var t = BinanceStream.ParseTrade("""{"e":"trade","E":1700000000123,"s":"BTCUSDT","t":12345,"p":"37000.50","q":"0.015","T":1700000000120,"m":true,"M":true}""");
        Assert.NotNull(t);
        Assert.Equal(37000.50, t.Value.Price);
        Assert.Equal(Side.Sell, t.Value.Aggressor);
        var k = BinanceStream.ParseKline("""{"e":"kline","E":1,"s":"BTCUSDT","k":{"t":1700000000000,"T":1700000059999,"s":"BTCUSDT","i":"1m","o":"1","c":"2","h":"3","l":"0.5","v":"10","x":true}}""");
        Assert.True(k!.Value.IsFinal);
        Assert.Equal(3, k.Value.Bar.High);
        Assert.Null(BinanceStream.ParseTrade("""{"e":"kline"}"""));
    }

    [Fact]
    public async Task Synthetic_stream_produces_ticks_bars_and_books()
    {
        var s = new SyntheticStream(100) { TickInterval = TimeSpan.FromMilliseconds(1), SecondsPerTick = 30 };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int ticks = 0;
        await foreach (var t in s.TradesAsync("X", cts.Token))
        {
            Assert.True(t.Price > 0);
            if (++ticks == 20) break;
        }
        int finals = 0, updates = 0;
        await foreach (var b in s.BarsAsync("X", BarInterval.Minute1, cts.Token))
        {
            updates++;
            if (b.IsFinal) finals++;
            Assert.True(b.Bar.High >= b.Bar.Low);
            if (finals == 3) break;
        }
        Assert.True(updates > finals);
        await foreach (var book in s.OrderBooksAsync("X", 5, cts.Token))
        {
            Assert.Equal(5, book.Bids.Count);
            Assert.True(book.BestAsk > book.BestBid);
            break;
        }
    }

    [Fact]
    public async Task Sample_calendar_has_payrolls_and_fomc()
    {
        var cal = new SampleEconomicCalendar();
        var ev = await cal.GetEventsAsync(new DateTime(2025, 1, 1), new DateTime(2025, 12, 31), TestContext.Current.CancellationToken);
        Assert.Equal(12, ev.Count(e => e.Name == "Nonfarm Payrolls"));
        Assert.All(ev.Where(e => e.Name == "Nonfarm Payrolls"), e => Assert.Equal(DayOfWeek.Friday, e.Time.DayOfWeek));
        Assert.Contains(ev, e => e.Name == "FOMC Rate Decision");
        Assert.True(ev.SequenceEqual(ev.OrderBy(e => e.Time)));
    }

    [Fact]
    public async Task Live_yahoo_and_binance()
    {
        Assert.SkipUnless(Live.Enabled, "Set FINANCENET_LIVE=1 to run live tests.");
        var ct = TestContext.Current.CancellationToken;
        var y = await new YahooFinanceProvider().GetRecentBarsAsync("AAPL", BarInterval.Day1, TimeSpan.FromDays(30), ct);
        Assert.True(y.Length > 10);
        var b = new BinanceProvider();
        var k = await b.GetRecentBarsAsync("BTCUSDT", BarInterval.Hour1, TimeSpan.FromDays(2), ct);
        Assert.True(k.Length > 24);
        var book = await b.GetOrderBookAsync("BTCUSDT", 10, ct);
        Assert.True(book.BestAsk > book.BestBid);
        var q = await b.GetQuoteAsync("BTCUSDT", ct);
        Assert.True(q.Last > 0);
    }
}
