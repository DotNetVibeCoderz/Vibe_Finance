using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Finance.Core.Market;
using Finance.Execution.Brokers;
using Finance.MarketData;
using Finance.MarketData.Economic;
using Finance.MarketData.Fundamentals;
using Finance.MarketData.Providers;

namespace Finance.Data.Tests;

/// <summary>
/// v0.3 venues against canned payloads in each API's documented shape: order mapping, request signing,
/// incremental fills from cumulative state, pagination, and the IDX / FRED / EDGAR specifics.
/// </summary>
public class VenueTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // ---------------- brokers ----------------

    [Fact]
    public async Task AlpacaSubmitsAndRecordsAnImmediateFill()
    {
        var h = new FakeHandler()
            .On("/v2/orders", """{"id":"b0b6dd9d","status":"filled","filled_qty":"10","filled_avg_price":"190.5"}""")
            .On("/v2/positions", """[{"symbol":"AAPL","qty":"10","avg_entry_price":"190.5"}]""")
            .On("/v2/account", """{"account_number":"PA123","currency":"USD","cash":"8095","equity":"10000","buying_power":"16190"}""");
        var b = new AlpacaBroker("KEY", "SECRET", http: h.Client());
        var o = await b.SubmitAsync(Order.MarketBuy("aapl", 10), Ct);

        Assert.Equal(OrderStatus.Filled, o.Status);
        Assert.Equal(190.5, o.AverageFillPrice, 12);
        var req = h.Requests[0];
        Assert.Equal("KEY", req.Headers.GetValues("APCA-API-KEY-ID").Single());
        var body = JsonNode.Parse(h.Bodies[0]!)!;
        Assert.Equal("AAPL", body["symbol"]!.GetValue<string>());
        Assert.Equal("market", body["type"]!.GetValue<string>());
        Assert.Equal("gtc", body["time_in_force"]!.GetValue<string>());

        Assert.Equal(10, (await b.GetPositionsAsync(Ct)).Single().Quantity);
        Assert.Equal(16190, (await b.GetAccountAsync(Ct)).BuyingPower);
    }

    [Fact]
    public async Task OandaMapsUnitsAndReadsTheFillTransaction()
    {
        var h = new FakeHandler().On("/orders", """
            {"orderCreateTransaction":{"id":"6372","type":"MARKET_ORDER"},
             "orderFillTransaction":{"id":"6373","units":"-1000","price":"1.08502"}}
            """);
        var b = new OandaBroker("TOKEN", "101-004-1", http: h.Client());
        var o = await b.SubmitAsync(Order.MarketSell("EUR/USD", 1000), Ct);
        Assert.Equal(OrderStatus.Filled, o.Status);
        Assert.Equal(1.08502, o.AverageFillPrice, 12);
        var body = JsonNode.Parse(h.Bodies[0]!)!["order"]!;
        Assert.Equal("EUR_USD", body["instrument"]!.GetValue<string>());
        Assert.Equal("-1000", body["units"]!.GetValue<string>());
        Assert.Equal("FOK", body["timeInForce"]!.GetValue<string>());
        Assert.Equal("Bearer TOKEN", h.Requests[0].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task BybitBuildsIncrementalFillsFromCumulativeState()
    {
        var h = new FakeHandler()
            .On("/v5/order/create", """{"retCode":0,"retMsg":"OK","result":{"orderId":"1321003749386327552","orderLinkId":"fnet1"}}""");
        var b = new BybitBroker("APIKEY", "SECRET", http: h.Client());
        var o = await b.SubmitAsync(Order.LimitBuy("BTC-USDT", 1.0, 50_200), Ct);
        Assert.Equal(OrderStatus.New, o.Status);

        h.On("/v5/order/realtime", """{"retCode":0,"retMsg":"OK","result":{"list":[{"orderStatus":"PartiallyFilled","cumExecQty":"0.4","avgPrice":"50000"}]}}""");
        await b.RefreshAsync(Ct);
        Assert.Equal(0.4, o.FilledQuantity, 12);

        // Second poll: cumulative 1.0 at average 50,100 → the new 0.6 traded at (50,100·1.0 − 50,000·0.4)/0.6.
        h.Override("/v5/order/realtime", """{"retCode":0,"retMsg":"OK","result":{"list":[{"orderStatus":"Filled","cumExecQty":"1.0","avgPrice":"50100"}]}}""");
        await b.RefreshAsync(Ct);
        var fills = b.Store.FillsOf(o.Id);
        Assert.Equal(2, fills.Count);
        Assert.Equal((50_100 * 1.0 - 50_000 * 0.4) / 0.6, fills[1].Price, 9);
        Assert.Equal(OrderStatus.Filled, o.Status);
        Assert.Equal(50_100, o.AverageFillPrice, 9);

        // The GET was signed over its query string.
        var get = h.Requests[^1];
        long ts = long.Parse(get.Headers.GetValues("X-BAPI-TIMESTAMP").Single());
        string expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("SECRET"),
            Encoding.UTF8.GetBytes($"{ts}APIKEY5000{get.RequestUri!.Query.TrimStart('?')}")));
        Assert.Equal(expected, get.Headers.GetValues("X-BAPI-SIGN").Single());
    }

    [Fact]
    public async Task OkxSignsAndRejectsOnVenueError()
    {
        var h = new FakeHandler().On("/api/v5/trade/order", """{"code":"1","msg":"","data":[{"ordId":"","sCode":"51008","sMsg":"Order failed. Insufficient balance"}]}""");
        var b = new OkxBroker("K", "S", "P", http: h.Client());
        var o = await b.SubmitAsync(Order.MarketBuy("BTCUSDT", 0.5), Ct);
        Assert.Equal(OrderStatus.Rejected, o.Status);

        var req = h.Requests[0];
        string ts = req.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
        string body = h.Bodies[0]!;
        string expected = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("S"), Encoding.UTF8.GetBytes(ts + "POST/api/v5/trade/order" + body)));
        Assert.Equal(expected, req.Headers.GetValues("OK-ACCESS-SIGN").Single());
        Assert.Equal("1", req.Headers.GetValues("x-simulated-trading").Single());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("BTC-USDT", json["instId"]!.GetValue<string>());
        Assert.Equal("base_ccy", json["tgtCcy"]!.GetValue<string>());
    }

    // ---------------- market data ----------------

    [Fact]
    public async Task AlpacaBarsFollowPageTokens()
    {
        var h = new FakeHandler()
            .On("page_token=P2", """{"bars":[{"t":"2025-01-03T05:00:00Z","o":3,"h":4,"l":2,"c":3.5,"v":30}],"next_page_token":null}""")
            .On("/bars?", """{"bars":[{"t":"2025-01-02T05:00:00Z","o":1,"h":2,"l":0.5,"c":1.5,"v":10}],"next_page_token":"P2"}""");
        var bars = await new AlpacaProvider("K", "S", h.Client()).GetBarsAsync("spy", BarInterval.Day1, new(2025, 1, 1), new(2025, 1, 5), Ct);
        Assert.Equal(new[] { 1.5, 3.5 }, bars.Select(b => b.Close));
        Assert.Contains("timeframe=1Day", h.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public void OandaSkipsTheFormingCandle()
    {
        using var doc = JsonDocument.Parse("""
            {"candles":[{"complete":true,"volume":120,"time":"2025-03-03T10:00:00.000000000Z","mid":{"o":"1.0800","h":"1.0820","l":"1.0790","c":"1.0815"}},
                        {"complete":false,"volume":5,"time":"2025-03-03T11:00:00.000000000Z","mid":{"o":"1.0815","h":"1.0816","l":"1.0814","c":"1.0815"}}]}
            """);
        var bars = OandaProvider.ParseCandles(doc.RootElement);
        Assert.Single(bars);
        Assert.Equal(1.0815, bars[0].Close, 12);
        Assert.Equal("EUR_USD", OandaProvider.Instrument("eurusd"));
        Assert.Equal("XAU_USD", OandaProvider.Instrument("XAU/USD"));
    }

    [Fact]
    public void BybitAndOkxReorderNewestFirstPayloads()
    {
        using var by = JsonDocument.Parse("""{"retCode":0,"retMsg":"OK","result":{"list":[["1735693200000","3","4","2","3.5","100","350"],["1735689600000","1","2","0.5","1.5","50","75"]]}}""");
        var b = BybitProvider.ParseKlines(by.RootElement);
        Assert.True(b[0].Time < b[1].Time);
        Assert.Equal(1.5, b[0].Close);

        using var ok = JsonDocument.Parse("""{"code":"0","msg":"","data":[["1735693200000","3","4","2","3.5","100","350","350","0"],["1735689600000","1","2","0.5","1.5","50","75","75","1"]]}""");
        var k = OkxProvider.ParseCandles(ok.RootElement);
        Assert.Single(k);                                   // the unconfirmed candle is dropped
        Assert.Equal(1.5, k[0].Close);
        Assert.Equal("ETH-USDC", OkxProvider.InstrumentId("ethusdc"));

        using var t = JsonDocument.Parse("""{"retCode":0,"result":{"list":[{"lastPrice":"100","bid1Price":"99.9","ask1Price":"100.1","volume24h":"5","price24hPcnt":"0.0123"}]},"time":1735689600000}""");
        Assert.Equal(1.23, BybitProvider.ParseTicker(t.RootElement, "X").ChangePercent, 12);

        using var err = JsonDocument.Parse("""{"retCode":10001,"retMsg":"params error","result":{}}""");
        Assert.Throws<MarketDataException>(() => BybitProvider.ParseKlines(err.RootElement));
    }

    // ---------------- IDX ----------------

    [Theory]
    [InlineData(150, 1)]
    [InlineData(199, 1)]
    [InlineData(200, 2)]
    [InlineData(499, 2)]
    [InlineData(500, 5)]
    [InlineData(1999, 5)]
    [InlineData(2000, 10)]
    [InlineData(4990, 10)]
    [InlineData(5000, 25)]
    public void IdxTickSizes(double price, int tick) => Assert.Equal(tick, IdxRules.TickSize(price));

    [Fact]
    public void IdxRoundingLotsAndAutoRejection()
    {
        Assert.Equal(1230, IdxRules.RoundToTick(1234, Side.Buy));
        Assert.Equal(1235, IdxRules.RoundToTick(1234, Side.Sell));
        Assert.Equal(500, IdxRules.RoundToTick(499.5));              // crosses into the Rp5 band
        Assert.True(IdxRules.IsValidPrice(2010));
        Assert.False(IdxRules.IsValidPrice(2015));
        Assert.Equal(12, IdxRules.Lots(1299));

        Assert.Equal((750.0, 1250.0), IdxRules.AutoRejection(1000));    // 25% band
        Assert.Equal((65.0, 135.0), IdxRules.AutoRejection(100));       // 35% band
        Assert.Equal((6400.0, 9600.0), IdxRules.AutoRejection(8000));   // 20% band
        Assert.Equal(850, IdxRules.AutoRejection(1000, lowerPercent: 0.15).Lower);
        Assert.Equal(50, IdxRules.AutoRejection(60).Lower);             // Rp50 floor

        Assert.Equal("BBCA.JK", IdxProvider.VendorSymbol("bbca"));
        Assert.Equal("^JKSE", IdxProvider.VendorSymbol("IHSG"));
    }

    // ---------------- FRED and EDGAR ----------------

    [Fact]
    public void FredSkipsMissingValuesAndRanksReleases()
    {
        using var meta = JsonDocument.Parse("""{"seriess":[{"id":"UNRATE","title":"Unemployment Rate","frequency":"Monthly","units":"Percent"}]}""");
        using var obs = JsonDocument.Parse("""{"observations":[{"date":"2025-01-01","value":"4.0"},{"date":"2025-02-01","value":"."},{"date":"2025-03-01","value":"4.2"}]}""");
        var s = FredProvider.ParseSeries(meta.RootElement, obs.RootElement);
        Assert.Equal("Unemployment Rate", s.Name);
        Assert.Equal(2, s.Observations.Length);
        Assert.Equal(4.2, s.Latest.Value);
        Assert.Equal("UNRATE", FredProvider.SeriesId(EconomicIndicator.Unemployment));

        using var rel = JsonDocument.Parse("""{"release_dates":[{"release_id":50,"release_name":"Employment Situation","date":"2025-11-07"},{"release_id":10,"release_name":"Consumer Price Index","date":"2025-11-13"},{"release_id":99,"release_name":"Some Regional Survey","date":"2025-11-01"}]}""");
        var events = FredProvider.ParseReleaseDates(rel.RootElement);
        Assert.Equal("Some Regional Survey", events[0].Name);          // sorted by date
        Assert.Equal(EventImportance.High, events.Single(e => e.Name == "Employment Situation").Importance);
        Assert.Equal(EventImportance.Low, events[0].Importance);
    }

    private const string CompanyFacts = """
    {"cik":320193,"entityName":"Apple Inc.","facts":{
      "dei":{"EntityCommonStockSharesOutstanding":{"units":{"shares":[{"end":"2024-10-18","val":15115823000,"fy":2024,"fp":"FY","form":"10-K","filed":"2024-11-01"}]}}},
      "us-gaap":{
        "Revenues":{"units":{"USD":[
          {"start":"2017-10-01","end":"2018-09-29","val":265595000000,"fy":2018,"fp":"FY","form":"10-K","filed":"2018-11-05"}]}},
        "RevenueFromContractWithCustomerExcludingAssessedTax":{"units":{"USD":[
          {"start":"2022-09-25","end":"2023-09-30","val":383285000000,"fy":2023,"fp":"FY","form":"10-K","filed":"2023-11-03"},
          {"start":"2023-10-01","end":"2024-09-28","val":391035000000,"fy":2024,"fp":"FY","form":"10-K","filed":"2024-11-01"},
          {"start":"2024-06-30","end":"2024-09-28","val":94930000000,"fy":2024,"fp":"FY","form":"10-K","filed":"2024-11-01"},
          {"start":"2024-03-31","end":"2024-06-29","val":85777000000,"fy":2024,"fp":"Q3","form":"10-Q","filed":"2024-08-02"}]}},
        "NetIncomeLoss":{"units":{"USD":[
          {"start":"2022-09-25","end":"2023-09-30","val":96995000000,"fy":2023,"fp":"FY","form":"10-K","filed":"2023-11-03"},
          {"start":"2022-09-25","end":"2023-09-30","val":96999000000,"fy":2024,"fp":"FY","form":"10-K","filed":"2024-11-01"},
          {"start":"2023-10-01","end":"2024-09-28","val":93736000000,"fy":2024,"fp":"FY","form":"10-K","filed":"2024-11-01"}]}},
        "Assets":{"units":{"USD":[{"end":"2024-09-28","val":364980000000,"fy":2024,"fp":"FY","form":"10-K","filed":"2024-11-01"}]}},
        "StockholdersEquity":{"units":{"USD":[{"end":"2024-09-28","val":56950000000,"fy":2024,"fp":"FY","form":"10-K","filed":"2024-11-01"}]}}
      }}}
    """;

    [Fact]
    public void EdgarBuildsStatementsFromXbrlFacts()
    {
        using var doc = JsonDocument.Parse(CompanyFacts);
        var annual = SecEdgarProvider.BuildIncome(doc.RootElement, quarterly: false);
        Assert.Equal(new DateOnly(2024, 9, 28), annual[0].FiscalDateEnding);        // newest first
        Assert.Equal(391035000000, annual[0].TotalRevenue);                          // synonym concept, ~1-year duration only
        Assert.Equal(96999000000, annual[1].NetIncome);                              // restated value from the later filing wins
        Assert.Equal(265595000000, annual.Single(a => a.FiscalDateEnding.Year == 2018).TotalRevenue);   // older synonym merged in

        var quarterly = SecEdgarProvider.BuildIncome(doc.RootElement, quarterly: true);
        Assert.Equal(85777000000, quarterly.Single().TotalRevenue);                  // the 10-Q quarter

        var bs = SecEdgarProvider.BuildBalance(doc.RootElement, quarterly: false).Single();
        Assert.Equal(364980000000 - 56950000000, bs.TotalLiabilities);               // derived when Liabilities is not filed
    }

    [Fact]
    public async Task EdgarResolvesTickerAndPadsTheCik()
    {
        var h = new FakeHandler()
            .On("company_tickers.json", """{"0":{"cik_str":320193,"ticker":"AAPL","title":"Apple Inc."}}""")
            .On("companyfacts/CIK0000320193.json", CompanyFacts);
        var sec = new SecEdgarProvider("FinanceNet tests test@example.com", h.Client());
        var overview = await sec.GetOverviewAsync("aapl", Ct);
        Assert.Equal("Apple Inc.", overview.Name);
        Assert.Equal(15115823000, overview.SharesOutstanding);
        Assert.All(h.Requests, r => Assert.Contains("test@example.com", r.Headers.UserAgent.ToString()));
        Assert.Throws<ArgumentException>(() => new SecEdgarProvider("no contact"));
    }

    /// <summary>Real EDGAR (FINANCENET_LIVE=1 and FINANCENET_SEC_USER_AGENT="Name contact@domain").</summary>
    [Fact]
    public async Task EdgarLiveAppleStatements()
    {
        var ua = Environment.GetEnvironmentVariable("FINANCENET_SEC_USER_AGENT");
        if (!Live.Enabled || string.IsNullOrWhiteSpace(ua)) Assert.Skip("FINANCENET_LIVE=1 and FINANCENET_SEC_USER_AGENT not set.");
        var sec = new SecEdgarProvider(ua);
        var income = await sec.GetIncomeStatementsAsync("AAPL", cancellationToken: TestContext.Current.CancellationToken);
        var latest = income[0];
        Assert.True(latest.FiscalDateEnding >= DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-15)), $"latest annual {latest.FiscalDateEnding}");
        Assert.InRange(latest.TotalRevenue, 300e9, 700e9);
        Assert.InRange(latest.NetIncome / latest.TotalRevenue, 0.15, 0.35);
        var balance = await sec.GetBalanceSheetsAsync("AAPL", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(Math.Abs(balance[0].TotalAssets - balance[0].TotalLiabilities - balance[0].TotalShareholderEquity) < 1e9);   // A = L + E (to $1bn)
    }
}
