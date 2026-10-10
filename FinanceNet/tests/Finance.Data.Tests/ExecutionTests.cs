using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Finance.Core.Market;
using Finance.Execution.Algorithms;
using Finance.Execution.Analytics;
using Finance.Execution.Brokers;
using Finance.Execution.Fix;
using Finance.Execution.Oms;
using Finance.Execution.Risk;
using Finance.Execution.Routing;

namespace Finance.Data.Tests;

public class OmsAndPaperBrokerTests
{
    private static OrderBook Book(string sym = "XYZ")
    {
        var b = new OrderBook(sym);
        b.SetBids(new[] { new BookLevel(99.9, 100), new BookLevel(99.8, 200) });
        b.SetAsks(new[] { new BookLevel(100.1, 100), new BookLevel(100.2, 200), new BookLevel(100.3, 300) });
        return b;
    }

    [Fact]
    public void State_machine_rejects_illegal_transitions()
    {
        Assert.True(OrderStateMachine.CanTransition(OrderStatus.New, OrderStatus.PartiallyFilled));
        Assert.False(OrderStateMachine.CanTransition(OrderStatus.Filled, OrderStatus.Cancelled));
        var o = Order.MarketBuy("X", 1);
        o.Status = OrderStatus.Filled;
        Assert.Throws<InvalidOperationException>(() => OrderStateMachine.Transition(o, OrderStatus.Cancelled));
    }

    [Fact]
    public async Task Market_order_walks_the_book()
    {
        var broker = new PaperBroker(100_000, commission: new CommissionModel(0.01));
        broker.UpdateOrderBook(Book());
        var events = new List<OrderEvent>();
        broker.OrderUpdated += events.Add;
        var o = await broker.SubmitAsync(Order.MarketBuy("XYZ", 250), TestContext.Current.CancellationToken);
        Assert.Equal(OrderStatus.Filled, o.Status);
        Assert.Equal((100 * 100.1 + 150 * 100.2) / 250, o.AverageFillPrice, 9);
        Assert.Equal(2, broker.Store.FillsOf(o.Id).Count);
        Assert.Equal(100_000 - (100 * 100.1 + 150 * 100.2) - 2.5, broker.Cash, 6);
        Assert.Equal(new[] { OrderEventKind.Submitted, OrderEventKind.Accepted, OrderEventKind.PartiallyFilled, OrderEventKind.Filled }, events.Select(e => e.Kind));

        // Liquidity already consumed is not reused until the next book update.
        var o2 = await broker.SubmitAsync(Order.MarketBuy("XYZ", 100), TestContext.Current.CancellationToken);
        Assert.Equal(100.25, o2.AverageFillPrice, 9);
        var pos = await broker.GetPositionsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(350, Assert.Single(pos).Quantity);
    }

    [Fact]
    public async Task Limit_order_rests_then_fills_on_new_quote()
    {
        var broker = new PaperBroker();
        broker.UpdateQuote("ABC", new Quote(DateTime.UtcNow, 9.9, 0, 10.1, 0));
        var o = await broker.SubmitAsync(Order.LimitBuy("ABC", 10, 10.0), TestContext.Current.CancellationToken);
        Assert.Equal(OrderStatus.New, o.Status);
        broker.UpdateQuote("ABC", new Quote(DateTime.UtcNow, 9.8, 0, 9.95, 0));
        Assert.Equal(OrderStatus.Filled, o.Status);
        Assert.Equal(9.95, o.AverageFillPrice);
    }

    [Fact]
    public async Task Stop_triggers_and_ioc_fok_behave()
    {
        var broker = new PaperBroker();
        var ct = TestContext.Current.CancellationToken;
        broker.UpdatePrice("S", 100);
        var stop = await broker.SubmitAsync(new Order("S", Side.Sell, 5, OrderType.Stop, stopPrice: 95), ct);
        Assert.Equal(OrderStatus.New, stop.Status);
        broker.UpdatePrice("S", 94);
        Assert.Equal(OrderStatus.Filled, stop.Status);

        broker.UpdateOrderBook(Book("T"));
        var ioc = await broker.SubmitAsync(new Order("T", Side.Buy, 150, OrderType.Limit, 100.1) { TimeInForce = TimeInForce.Ioc }, ct);
        Assert.Equal(OrderStatus.Cancelled, ioc.Status);
        Assert.Equal(100, ioc.FilledQuantity);
        var fok = await broker.SubmitAsync(new Order("T", Side.Buy, 1000, OrderType.Market) { TimeInForce = TimeInForce.Fok }, ct);
        Assert.Equal(OrderStatus.Cancelled, fok.Status);
        Assert.Equal(0, fok.FilledQuantity);
    }

    [Fact]
    public async Task Risk_checks_reject_and_cancel_works()
    {
        var risk = new PreTradeRiskChecker(new RiskLimits { MaxOrderNotional = 50_000, MaxPriceDeviation = 0.05, MaxPosition = 300 });
        var broker = new PaperBroker(risk: risk);
        var ct = TestContext.Current.CancellationToken;
        broker.UpdatePrice("R", 100);
        var big = await broker.SubmitAsync(Order.MarketBuy("R", 1000), ct);
        Assert.Equal(OrderStatus.Rejected, big.Status);
        var fat = await broker.SubmitAsync(Order.LimitBuy("R", 1, 150), ct);
        Assert.Equal(OrderStatus.Rejected, fat.Status);
        var r = risk.Check(Order.MarketBuy("R", 400), 100);
        Assert.False(r.Passed);
        Assert.Contains(r.Violations, v => v.Contains("Position"));

        var rest = await broker.SubmitAsync(Order.LimitBuy("R", 10, 99), ct);
        Assert.True(await broker.CancelAsync(rest.Id, ct));
        Assert.False(await broker.CancelAsync(rest.Id, ct));
        var acct = await broker.GetAccountAsync(ct);
        Assert.Equal(100_000, acct.Equity);
    }

    [Fact]
    public async Task Short_selling_can_be_disabled_and_latency_applies()
    {
        var broker = new PaperBroker(1000) { AllowShort = false, Latency = TimeSpan.FromMilliseconds(30) };
        broker.UpdatePrice("Q", 10);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var o = await broker.SubmitAsync(Order.MarketSell("Q", 1), TestContext.Current.CancellationToken);
        Assert.True(sw.ElapsedMilliseconds >= 25);
        Assert.Equal(OrderStatus.Rejected, o.Status);
    }
}

public class FixTests
{
    [Fact]
    public void Encode_parse_round_trip_with_checksum()
    {
        var m = new FixMessage(FixMsgType.NewOrderSingle).Set(FixTags.ClOrdID, "1").Set(FixTags.Symbol, "AAPL").Set(FixTags.OrderQty, 100.0);
        var raw = m.EncodeString();
        Assert.StartsWith("8=FIX.4.4\u00019=", raw);
        Assert.Matches(@"10=\d{3}\u0001$", raw);
        var back = FixMessage.Parse(raw);
        Assert.Equal("AAPL", back[FixTags.Symbol]);
        Assert.Equal(100, back.GetDouble(FixTags.OrderQty));

        var corrupted = raw.Replace("AAPL", "AAPM");
        Assert.Throws<FixParseException>(() => FixMessage.Parse(corrupted));
    }

    [Fact]
    public void Parses_known_message_with_pipes()
    {
        // Canonical heartbeat from the FIX spec examples, checksum recomputed.
        var body = "35=0|49=SENDER|56=TARGET|34=2|52=20240101-12:00:00.000|";
        var head = $"8=FIX.4.4|9={body.Length}|";
        var sum = Encoding.Latin1.GetBytes((head + body).Replace('|', '\u0001')).Sum(b => b) % 256;
        var m = FixMessage.Parse(head + body + $"10={sum:D3}|");
        Assert.Equal(FixMsgType.Heartbeat, m.MsgType);
        Assert.Equal(2, m.MsgSeqNum);
        Assert.Throws<FixParseException>(() => FixMessage.Parse(head.Replace($"9={body.Length}", "9=999") + body + $"10={sum:D3}|"));
    }

    [Fact]
    public void Repeating_groups()
    {
        var m = new FixMessage(FixMsgType.MarketDataSnapshot).Set(FixTags.Symbol, "EURUSD");
        m.AddGroup(FixTags.NoMDEntries, new[]
        {
            new[] { new KeyValuePair<int, string>(FixTags.MDEntryType, "0"), new(FixTags.MDEntryPx, "1.1"), new(FixTags.MDEntrySize, "5") },
            new[] { new KeyValuePair<int, string>(FixTags.MDEntryType, "1"), new(FixTags.MDEntryPx, "1.2"), new(FixTags.MDEntrySize, "7") },
        });
        var back = FixMessage.Parse(m.EncodeString());
        var g = back.GetGroup(FixTags.NoMDEntries, FixTags.MDEntryType, FixTags.MDEntryPx, FixTags.MDEntrySize);
        Assert.Equal(2, g.Count);
        Assert.Equal("1.2", g[1][FixTags.MDEntryPx]);
    }

    [Fact]
    public void Framer_handles_fragmentation_and_coalescing()
    {
        var a = new FixMessage(FixMsgType.Heartbeat).Set(FixTags.MsgSeqNum, 1).Encode();
        var b = new FixMessage(FixMsgType.TestRequest).Set(FixTags.MsgSeqNum, 2).Set(FixTags.TestReqID, "x").Encode();
        var stream = a.Concat(b).ToArray();
        var f = new FixFramer();
        var got = new List<byte[]>();
        foreach (var chunk in stream.Chunk(7)) got.AddRange(f.Push(chunk));
        Assert.Equal(2, got.Count);
        Assert.Equal(FixMsgType.TestRequest, FixMessage.Parse(got[1]).MsgType);
    }

    [Fact]
    public void Order_mapping_round_trip()
    {
        var o = new Order("MSFT", Side.Sell, 50, OrderType.Limit, 410.5) { TimeInForce = TimeInForce.Ioc };
        var nos = FixMessage.Parse(FixMapping.NewOrderSingle(o, "C1", "ACC").EncodeString());
        var (back, cl) = FixMapping.ParseNewOrderSingle(nos);
        Assert.Equal("C1", cl);
        Assert.Equal(Side.Sell, back.Side);
        Assert.Equal(410.5, back.LimitPrice);
        Assert.Equal(TimeInForce.Ioc, back.TimeInForce);

        back.ApplyFill(20, 410.6);
        var er = FixMapping.ParseExecutionReport(FixMessage.Parse(FixMapping.ExecutionReport(back, "C1", "X1", "E1", 'F', 20, 410.6).EncodeString()));
        Assert.Equal(OrderStatus.PartiallyFilled, er.Status);
        Assert.Equal(30, er.LeavesQty);
        var fill = er.ToFill(o.Id);
        Assert.Equal(20, fill!.Value.Quantity);
        Assert.Equal("F", FixMapping.OrderCancelRequest(o, "C2", "C1").MsgType);
    }

    private static (FixSession Init, FixSession Acc) Pair(int hb = 30)
    {
        var (a, b) = InMemoryFixTransport.CreatePair();
        a.FragmentSize = 13;
        var init = new FixSession(new FixSessionSettings("CLIENT", "BROKER", true, hb), a);
        var acc = new FixSession(new FixSessionSettings("BROKER", "CLIENT", false, hb), b);
        return (init, acc);
    }

    [Fact]
    public async Task Session_logon_order_flow_and_logout()
    {
        var (init, acc) = Pair();
        var ct = TestContext.Current.CancellationToken;
        var received = new TaskCompletionSource<FixMessage>();
        var reports = new TaskCompletionSource<FixMessage>();
        acc.MessageReceived += m => received.TrySetResult(m);
        init.MessageReceived += m => reports.TrySetResult(m);
        await acc.StartAsync(ct);
        await init.StartAsync(ct);
        await Task.WhenAll(init.LoggedOn, acc.LoggedOn).WaitAsync(TimeSpan.FromSeconds(5), ct);

        await init.SendAsync(FixMapping.NewOrderSingle(Order.MarketBuy("AAPL", 10), "C1"), ct);
        var nos = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var (order, cl) = FixMapping.ParseNewOrderSingle(nos);
        order.ApplyFill(10, 190.0);
        await acc.SendAsync(FixMapping.ExecutionReport(order, cl, "O1", "E1", 'F', 10, 190.0), ct);
        var er = FixMapping.ParseExecutionReport(await reports.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Equal(OrderStatus.Filled, er.Status);

        Assert.Equal(3, init.NextOutgoingSeqNum); // logon + order
        await init.LogoutAsync("bye", TimeSpan.FromSeconds(5));
        await acc.Closed.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(FixSessionState.Closed, init.State);
        await init.DisposeAsync();
        await acc.DisposeAsync();
    }

    [Fact]
    public async Task Test_request_gets_heartbeat()
    {
        var (init, acc) = Pair();
        var ct = TestContext.Current.CancellationToken;
        var hb = new TaskCompletionSource<FixMessage>();
        init.MessageLogged += (m, sent) => { if (!sent && m.MsgType == FixMsgType.Heartbeat) hb.TrySetResult(m); };
        await acc.StartAsync(ct);
        await init.StartAsync(ct);
        await init.LoggedOn.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await init.SendTestRequestAsync("PING1", ct);
        var m = await hb.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal("PING1", m.Get(FixTags.TestReqID));
        await init.DisposeAsync();
        await acc.DisposeAsync();
    }

    [Fact]
    public async Task Gap_triggers_resend_with_possdup_and_gapfill()
    {
        var (init, acc) = Pair();
        var ct = TestContext.Current.CancellationToken;
        var got = new List<string>();
        var all = new TaskCompletionSource();
        acc.MessageReceived += m =>
        {
            lock (got) got.Add(m.Get(FixTags.ClOrdID)!);
            if (got.Count == 3) all.TrySetResult();
        };
        await acc.StartAsync(ct);
        await init.StartAsync(ct);
        await Task.WhenAll(init.LoggedOn, acc.LoggedOn).WaitAsync(TimeSpan.FromSeconds(5), ct);

        await init.SendAsync(FixMapping.NewOrderSingle(Order.MarketBuy("A", 1), "C1"), ct);
        // Simulate a lost message: an admin heartbeat number is skipped, then two orders follow.
        init.SkipOutgoingSequence(1);
        await init.SendAsync(FixMapping.NewOrderSingle(Order.MarketBuy("A", 1), "C2"), ct);
        await init.SendAsync(FixMapping.NewOrderSingle(Order.MarketBuy("A", 1), "C3"), ct);
        await all.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(new[] { "C1", "C2", "C3" }, got);
        Assert.Equal(init.NextOutgoingSeqNum, acc.NextExpectedSeqNum);
        await init.DisposeAsync();
        await acc.DisposeAsync();
    }

    [Fact]
    public async Task Heartbeats_flow_when_idle()
    {
        var (init, acc) = Pair(hb: 1);
        var ct = TestContext.Current.CancellationToken;
        int beats = 0;
        acc.MessageLogged += (m, sent) => { if (!sent && m.MsgType == FixMsgType.Heartbeat) Interlocked.Increment(ref beats); };
        await acc.StartAsync(ct);
        await init.StartAsync(ct);
        await init.LoggedOn.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await Task.Delay(2600, ct);
        Assert.True(beats >= 1);
        Assert.Equal(FixSessionState.LoggedOn, acc.State);
        await init.DisposeAsync();
        await acc.DisposeAsync();
    }

    [Fact]
    public async Task Tcp_transport_carries_session()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var ct = TestContext.Current.CancellationToken;
        var acceptTask = TcpFixTransport.AcceptAsync(listener, ct);
        var client = await TcpFixTransport.ConnectAsync("127.0.0.1", port, ct);
        var server = await acceptTask;
        var acc = new FixSession(new FixSessionSettings("BROKER", "CLIENT", false), server);
        var init = new FixSession(new FixSessionSettings("CLIENT", "BROKER", true), client);
        await acc.StartAsync(ct);
        await init.StartAsync(ct);
        await Task.WhenAll(init.LoggedOn, acc.LoggedOn).WaitAsync(TimeSpan.FromSeconds(5), ct);
        await init.LogoutAsync(timeout: TimeSpan.FromSeconds(3));
        await init.DisposeAsync();
        await acc.DisposeAsync();
        listener.Stop();
    }
}

public class BrokerAdapterTests
{
    [Fact]
    public void Binance_signature_matches_documented_example()
    {
        // Example from the Binance API documentation (SIGNED endpoint security).
        const string secret = "NhqPtmdSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fATj0j";
        const string query = "symbol=LTCBTC&side=BUY&type=LIMIT&timeInForce=GTC&quantity=1&price=0.1&recvWindow=5000&timestamp=1499827319559";
        Assert.Equal("c8db56825ae71d6d79447849e617115f4a920fa2acdcab2b053c4b2838bd6b71", BinanceBroker.Sign(query, Encoding.UTF8.GetBytes(secret)));
    }

    [Fact]
    public async Task Binance_broker_submits_signed_order_and_records_fills()
    {
        const string resp = """{"symbol":"BTCUSDT","orderId":28,"clientOrderId":"x","transactTime":1507725176595,"price":"0.00000000","origQty":"0.02","executedQty":"0.02","cummulativeQuoteQty":"740.0","status":"FILLED","type":"MARKET","side":"BUY","fills":[{"price":"37000.00","qty":"0.01","commission":"0.37","commissionAsset":"USDT"},{"price":"37000.00","qty":"0.01","commission":"0.37","commissionAsset":"USDT"}]}""";
        var h = new FakeHandler().On("/api/v3/order", resp).On("/api/v3/account", """{"balances":[{"asset":"BTC","free":"0.02","locked":"0"},{"asset":"USDT","free":"1000.5","locked":"0"},{"asset":"ETH","free":"0","locked":"0"}]}""");
        var b = new BinanceBroker("key", "secret", http: h.Client());
        var ct = TestContext.Current.CancellationToken;
        var o = await b.SubmitAsync(Order.MarketBuy("BTC/USDT", 0.02), ct);
        Assert.Equal(OrderStatus.Filled, o.Status);
        Assert.Equal(37000, o.AverageFillPrice);
        var req = h.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("signature=", req.RequestUri!.Query);
        Assert.Contains("symbol=BTCUSDT", req.RequestUri.Query);
        Assert.Equal("key", req.Headers.GetValues("X-MBX-APIKEY").Single());
        var pos = await b.GetPositionsAsync(ct);
        Assert.Equal(2, pos.Count);
        Assert.Equal(1000.5, (await b.GetAccountAsync(ct)).Cash);
    }

    [Fact]
    public async Task Binance_error_rejects_order()
    {
        var h = new FakeHandler().On("/api/v3/order", """{"code":-2010,"msg":"Account has insufficient balance for requested action."}""", System.Net.HttpStatusCode.BadRequest);
        var b = new BinanceBroker("key", "secret", http: h.Client());
        var o = await b.SubmitAsync(Order.MarketBuy("BTCUSDT", 1), TestContext.Current.CancellationToken);
        Assert.Equal(OrderStatus.Rejected, o.Status);
        Assert.Contains("insufficient", b.Store.History.Last().Message);
    }

    [Fact]
    public async Task Interactive_brokers_confirms_prompt_and_reads_portfolio()
    {
        var h = new FakeHandler()
            .On("/iserver/accounts", """{"accounts":["DU123"],"selectedAccount":"DU123"}""")
            .On("/iserver/secdef/search", """[{"conid":"265598","companyHeader":"APPLE INC - NASDAQ","symbol":"AAPL"}]""")
            .On("/iserver/reply/", """[{"order_id":"987654","order_status":"Submitted"}]""")
            .On("/iserver/account/DU123/orders", """[{"id":"07a13a5a-4a48-44a5-bb25-5ab37b79186c","message":["You are submitting an order without market data."]}]""")
            .On("/portfolio/DU123/positions/0", """[{"acctId":"DU123","conid":265598,"contractDesc":"AAPL","ticker":"AAPL","position":15.0,"mktPrice":190.1,"avgCost":180.5}]""")
            .On("/portfolio/DU123/summary", """{"netliquidation":{"amount":105000.5,"currency":"USD"},"totalcashvalue":{"amount":50000.0},"buyingpower":{"amount":200000.0}}""");
        var ib = new InteractiveBrokersBroker(h.Client());
        var ct = TestContext.Current.CancellationToken;
        var o = await ib.SubmitAsync(Order.LimitBuy("AAPL", 15, 180), ct);
        Assert.Equal(OrderStatus.New, o.Status);
        Assert.Contains(ib.Store.History, e => e.Kind == OrderEventKind.Accepted);
        var orderReq = h.Requests.First(r => r.RequestUri!.AbsolutePath.EndsWith("/orders"));
        var body = JsonNode.Parse(await orderReq.Content!.ReadAsStringAsync(ct))!;
        Assert.Equal(265598, (long)body["orders"]![0]!["conid"]!);
        Assert.Equal("LMT", (string)body["orders"]![0]!["orderType"]!);
        var pos = Assert.Single(await ib.GetPositionsAsync(ct));
        Assert.Equal(15, pos.Quantity);
        Assert.Equal(180.5, pos.AveragePrice);
        var acct = await ib.GetAccountAsync(ct);
        Assert.Equal(105000.5, acct.Equity);
        Assert.Equal(200000.0, acct.BuyingPower);
    }
}

public class AlgoRoutingTcaTests
{
    private static readonly DateTime T0 = new(2024, 1, 2, 14, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Twap_preserves_total_with_lots()
    {
        var s = ExecutionAlgorithms.Twap(1003, T0, T0.AddHours(1), 10, lotSize: 10);
        Assert.Equal(1003, s.Sum(c => c.Quantity), 9);
        Assert.All(s.Take(s.Count - 1), c => Assert.Equal(0, c.Quantity % 10, 9));
        Assert.Equal(T0.AddMinutes(6), s[1].Time);
    }

    [Fact]
    public void Vwap_follows_profile()
    {
        var prof = ExecutionAlgorithms.UShapedProfile(13);
        Assert.Equal(1, prof.Sum(), 9);
        Assert.True(prof[0] > prof[6] && prof[12] > prof[6]);
        var s = ExecutionAlgorithms.Vwap(10_000, T0, T0.AddHours(6.5), prof);
        Assert.Equal(10_000, s.Sum(c => c.Quantity), 9);
        Assert.True(s[0].Quantity > s[6].Quantity);
    }

    [Fact]
    public void Pov_and_iceberg()
    {
        var pov = ExecutionAlgorithms.Pov(1000, 0.1, T0, TimeSpan.FromMinutes(5), new double[] { 3000, 4000, 5000, 6000 });
        Assert.Equal(new double[] { 300, 400, 300 }, pov.Select(c => c.Quantity));
        var ice = ExecutionAlgorithms.Iceberg(1050, 200, 99.5, T0);
        Assert.Equal(6, ice.Count);
        Assert.Equal(50, ice[^1].Quantity);
        Assert.All(ice, c => Assert.Equal(99.5, c.LimitPrice));
    }

    [Fact]
    public void Volume_profile_from_bars()
    {
        var bars = new List<Bar>();
        for (int d = 0; d < 2; d++)
            for (int h = 0; h < 4; h++)
                bars.Add(new Bar(T0.Date.AddDays(d).AddHours(14 + h), 1, 1, 1, 1, h == 0 ? 400 : 200));
        var p = ExecutionAlgorithms.ProfileFromBars(bars, TimeSpan.FromHours(1));
        Assert.Equal(new[] { 0.4, 0.2, 0.2, 0.2 }, p.Select(v => Math.Round(v, 9)));
    }

    [Fact]
    public void Smart_router_beats_single_venue_after_fees()
    {
        var a = new OrderBook("BTC");
        a.SetAsks(new[] { new BookLevel(100.0, 5), new BookLevel(100.5, 10) });
        var b = new OrderBook("BTC");
        b.SetAsks(new[] { new BookLevel(100.05, 8), new BookLevel(100.4, 10) });
        var venues = new[] { new Venue("A", a, 10), new Venue("B", b, 2) };
        var plan = SmartOrderRouter.Route(Side.Buy, 12, venues);
        Assert.True(plan.IsComplete);
        Assert.Equal(12, plan.FilledQuantity, 9);
        // B's 100.05 with 2bps (≈100.07) beats A's 100.0 with 10bps (100.10), so B is used first.
        Assert.Equal(8, plan.Allocations.First(x => x.Venue == "B").Quantity, 9);
        var single = SmartOrderRouter.SingleVenue(Side.Buy, 12, venues[0]);
        Assert.True(plan.AllInPrice < single.AllInPrice);
    }

    [Fact]
    public void Implementation_shortfall_decomposition()
    {
        var fills = new[] { new Fill(1, "X", Side.Buy, 600, 101, T0, 6), new Fill(1, "X", Side.Buy, 200, 102, T0, 2) };
        var r = TransactionCostAnalysis.ImplementationShortfall(Side.Buy, 1000, 100, fills, 104);
        Assert.Equal((101.25 - 100) * 800, r.ExecutionCost, 9);
        Assert.Equal(4 * 200, r.OpportunityCost, 9);
        Assert.Equal(8, r.Fees, 9);
        Assert.Equal((1000 + 800 + 8) / 100_000.0 * 1e4, r.TotalBps, 9);
        Assert.Equal(0.8, r.FillRate);
    }

    [Fact]
    public void Slippage_vs_arrival_and_vwap()
    {
        var fills = new[] { new Fill(1, "X", Side.Sell, 100, 99, T0) };
        var bars = new[] { new Bar(T0, 100, 100, 100, 100, 1000) };
        var s = TransactionCostAnalysis.Slippage(Side.Sell, fills, 100, bars);
        Assert.Equal(100, s.VsArrivalBps, 9);
        Assert.Equal(100, s.VsVwapBps, 9);
        Assert.Equal(0.02 * Math.Sqrt(0.01), TransactionCostAnalysis.SquareRootImpact(1000, 100_000, 0.02), 12);
    }
}
