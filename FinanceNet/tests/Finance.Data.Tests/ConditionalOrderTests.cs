using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Finance.Core.Market;
using Finance.Execution.Brokers;

namespace Finance.Data.Tests;

/// <summary>
/// Bybit v5 and OKX v5 conditional orders against fake HTTP handlers: request bodies are checked field by field
/// against the documented parameter names (Bybit <c>/v5/order/create</c>: triggerPrice, triggerDirection, triggerBy,
/// orderFilter, takeProfit/stopLoss; OKX <c>/api/v5/trade/order-algo</c>: ordType trigger/conditional/oco,
/// triggerPx, orderPx, tp/sl*; <c>cancel-algos</c>), and each signature is recomputed over the exact body sent.
/// </summary>
public class ConditionalOrderTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static string BybitSign(HttpRequestMessage r, string payload, string key = "APIKEY", string secret = "SECRET")
    {
        long ts = long.Parse(r.Headers.GetValues("X-BAPI-TIMESTAMP").Single());
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{ts}{key}5000{payload}")));
    }

    private static string OkxSign(HttpRequestMessage r, string body, string secret = "S")
    {
        string ts = r.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();
        return Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(ts + r.Method.Method + r.RequestUri!.PathAndQuery + body)));
    }

    private static string Str(JsonNode n, string key) => n[key]!.GetValue<string>();

    [Fact]
    public void OrderModelExpressesTriggerOrders()
    {
        var stop = Order.StopMarket("BTCUSDT", Side.Sell, 1, 60_000);
        Assert.True(stop.IsConditional);
        Assert.Equal(60_000, stop.TriggerPrice);
        Assert.Equal(TriggerDirection.Falling, stop.EffectiveTriggerDirection);
        var sl = Order.StopLimit("BTCUSDT", Side.Buy, 1, 70_000, 70_100);
        Assert.Equal(TriggerDirection.Rising, sl.EffectiveTriggerDirection);
        Assert.Equal(70_100, sl.LimitPrice);
        var over = new Order("X", Side.Buy, 1, OrderType.Stop, stopPrice: 5) { TriggerDirection = TriggerDirection.Falling };
        Assert.Equal(TriggerDirection.Falling, over.EffectiveTriggerDirection);
        Assert.Null(Order.LimitBuy("X", 1, 5).TriggerPrice);
        Assert.False(Order.MarketBuy("X", 1).IsConditional);
    }

    [Fact]
    public async Task BybitLinearStopMarketCarriesTriggerDirectionTriggerByAndTpSl()
    {
        var h = new FakeHandler().On("/v5/order/create", """{"retCode":0,"retMsg":"OK","result":{"orderId":"c-1","orderLinkId":"x"}}""");
        var b = new BybitBroker("APIKEY", "SECRET", category: "linear", http: h.Client());
        var order = new Order("BTC/USDT", Side.Buy, 0.01, OrderType.Stop, stopPrice: 65_000)
        {
            TriggerBy = TriggerPriceType.Mark, TakeProfitPrice = 70_000, StopLossPrice = 62_000,
        };
        var o = await b.SubmitAsync(order, Ct);
        Assert.Equal(OrderStatus.New, o.Status);
        Assert.Equal("c-1", b.VenueOrderId(o.Id));

        var body = JsonNode.Parse(h.Bodies[0]!)!;
        Assert.Equal("linear", Str(body, "category"));
        Assert.Equal("BTCUSDT", Str(body, "symbol"));
        Assert.Equal("Buy", Str(body, "side"));
        Assert.Equal("Market", Str(body, "orderType"));
        Assert.Equal("0.01", Str(body, "qty"));
        Assert.Equal("65000", Str(body, "triggerPrice"));
        Assert.Equal(1, body["triggerDirection"]!.GetValue<int>());     // 1 = rises to triggerPrice
        Assert.Equal("MarkPrice", Str(body, "triggerBy"));
        Assert.Equal("70000", Str(body, "takeProfit"));
        Assert.Equal("62000", Str(body, "stopLoss"));
        Assert.Equal("MarkPrice", Str(body, "tpTriggerBy"));
        Assert.Equal("MarkPrice", Str(body, "slTriggerBy"));
        Assert.Null(body["orderFilter"]);                                // spot-only
        Assert.Null(body["marketUnit"]);                                 // spot-only
        Assert.Null(body["price"]);

        var req = h.Requests[0];
        Assert.Equal(BybitSign(req, h.Bodies[0]!), req.Headers.GetValues("X-BAPI-SIGN").Single());
    }

    [Fact]
    public async Task BybitSpotStopLimitUsesOrderFilterAndCancelsWithIt()
    {
        var h = new FakeHandler()
            .On("/v5/order/create", """{"retCode":0,"retMsg":"OK","result":{"orderId":"s-9","orderLinkId":"x"}}""")
            .On("/v5/order/realtime", """{"retCode":0,"retMsg":"OK","result":{"list":[{"orderStatus":"Untriggered","cumExecQty":"0","avgPrice":""}]}}""")
            .On("/v5/order/cancel", """{"retCode":0,"retMsg":"OK","result":{"orderId":"s-9","orderLinkId":"x"}}""");
        var b = new BybitBroker("APIKEY", "SECRET", http: h.Client());
        var o = await b.SubmitAsync(Order.StopLimit("ETHUSDT", Side.Sell, 0.5, 2_900, 2_890), Ct);

        var body = JsonNode.Parse(h.Bodies[0]!)!;
        Assert.Equal("spot", Str(body, "category"));
        Assert.Equal("Limit", Str(body, "orderType"));
        Assert.Equal("2890", Str(body, "price"));
        Assert.Equal("2900", Str(body, "triggerPrice"));
        Assert.Equal("StopOrder", Str(body, "orderFilter"));
        Assert.Null(body["triggerDirection"]);                           // linear / inverse only
        Assert.Null(body["triggerBy"]);

        await b.RefreshAsync(Ct);                                        // Untriggered: still working
        Assert.Equal(OrderStatus.New, o.Status);
        Assert.Contains("orderFilter=StopOrder", h.Requests[1].RequestUri!.Query);
        Assert.Equal(BybitSign(h.Requests[1], h.Requests[1].RequestUri!.Query.TrimStart('?')), h.Requests[1].Headers.GetValues("X-BAPI-SIGN").Single());

        Assert.True(await b.CancelAsync(o.Id, Ct));
        Assert.Equal(OrderStatus.Cancelled, o.Status);
        var cancel = JsonNode.Parse(h.Bodies[2]!)!;
        Assert.Equal("s-9", Str(cancel, "orderId"));
        Assert.Equal("StopOrder", Str(cancel, "orderFilter"));
        Assert.Equal("ETHUSDT", Str(cancel, "symbol"));
        Assert.Equal(BybitSign(h.Requests[2], h.Bodies[2]!), h.Requests[2].Headers.GetValues("X-BAPI-SIGN").Single());
    }

    [Fact]
    public async Task BybitSpotTpSlOrderFilterAndRejections()
    {
        var h = new FakeHandler().On("/v5/order/create", """{"retCode":0,"retMsg":"OK","result":{"orderId":"t-1","orderLinkId":"x"}}""");
        var b = new BybitBroker("APIKEY", "SECRET", http: h.Client()) { SpotConditionalFilter = "tpslOrder" };
        await b.SubmitAsync(Order.StopMarket("BTCUSDT", Side.Sell, 0.1, 60_000), Ct);
        var body = JsonNode.Parse(h.Bodies[0]!)!;
        Assert.Equal("tpslOrder", Str(body, "orderFilter"));
        Assert.Equal("Market", Str(body, "orderType"));
        Assert.Equal("baseCoin", Str(body, "marketUnit"));

        // Spot conditionals only trigger on the last price: rejected locally, nothing sent.
        var mark = await b.SubmitAsync(new Order("BTCUSDT", Side.Sell, 0.1, OrderType.Stop, stopPrice: 60_000) { TriggerBy = TriggerPriceType.Mark }, Ct);
        Assert.Equal(OrderStatus.Rejected, mark.Status);
        Assert.Single(h.Requests);

        // Attached TP/SL on a plain spot limit order.
        var lim = new Order("BTCUSDT", Side.Buy, 0.1, OrderType.Limit, 61_000) { TakeProfitPrice = 65_000, StopLossPrice = 59_000 };
        await b.SubmitAsync(lim, Ct);
        var lb = JsonNode.Parse(h.Bodies[1]!)!;
        Assert.Equal("65000", Str(lb, "takeProfit"));
        Assert.Equal("59000", Str(lb, "stopLoss"));
        Assert.Null(lb["triggerPrice"]);
        Assert.Null(lb["orderFilter"]);
        Assert.Null(lb["tpTriggerBy"]);
    }

    [Fact]
    public async Task OkxStopLimitIsATriggerAlgoAndCancelsThroughCancelAlgos()
    {
        var h = new FakeHandler()
            .On("/api/v5/trade/order-algo", """{"code":"0","msg":"","data":[{"algoId":"681096944655273984","clOrdId":"","algoClOrdId":"x","sCode":"0","sMsg":""}]}""")
            .On("/api/v5/trade/cancel-algos", """{"code":"0","msg":"","data":[{"algoId":"681096944655273984","sCode":"0","sMsg":""}]}""");
        var b = new OkxBroker("K", "S", "P", http: h.Client());
        var order = new Order("BTCUSDT", Side.Sell, 0.2, OrderType.StopLimit, 59_900, 60_000) { TriggerBy = TriggerPriceType.Mark };
        var o = await b.SubmitAsync(order, Ct);
        Assert.Equal(OrderStatus.New, o.Status);
        Assert.True(b.IsAlgoOrder(o.Id));
        Assert.Equal("681096944655273984", b.VenueOrderId(o.Id));

        var req = h.Requests[0];
        Assert.Equal("/api/v5/trade/order-algo", req.RequestUri!.AbsolutePath);
        var body = JsonNode.Parse(h.Bodies[0]!)!;
        Assert.Equal("BTC-USDT", Str(body, "instId"));
        Assert.Equal("cash", Str(body, "tdMode"));
        Assert.Equal("sell", Str(body, "side"));
        Assert.Equal("trigger", Str(body, "ordType"));
        Assert.Equal("0.2", Str(body, "sz"));
        Assert.Equal("60000", Str(body, "triggerPx"));
        Assert.Equal("59900", Str(body, "orderPx"));
        Assert.Equal("mark", Str(body, "triggerPxType"));
        Assert.Equal($"fnet{o.Id}", Str(body, "algoClOrdId"));
        Assert.Null(body["tgtCcy"]);                                     // limit leg: size is base already
        Assert.Equal(OkxSign(req, h.Bodies[0]!), req.Headers.GetValues("OK-ACCESS-SIGN").Single());

        Assert.True(await b.CancelAsync(o.Id, Ct));
        Assert.Equal(OrderStatus.Cancelled, o.Status);
        var cancelReq = h.Requests[1];
        Assert.Equal("/api/v5/trade/cancel-algos", cancelReq.RequestUri!.AbsolutePath);
        var arr = JsonNode.Parse(h.Bodies[1]!)!.AsArray();               // documented as an array of {algoId, instId}
        Assert.Single(arr);
        Assert.Equal("681096944655273984", Str(arr[0]!, "algoId"));
        Assert.Equal("BTC-USDT", Str(arr[0]!, "instId"));
        Assert.Equal(OkxSign(cancelReq, h.Bodies[1]!), cancelReq.Headers.GetValues("OK-ACCESS-SIGN").Single());
    }

    [Fact]
    public async Task OkxStopMarketTriggersAtMinusOneAndFillsThroughTheTriggeredOrder()
    {
        var h = new FakeHandler()
            .On("/api/v5/trade/order-algo?algoId=", """{"code":"0","msg":"","data":[{"algoId":"A1","state":"live","ordIdList":[]}]}""")
            .On("/api/v5/trade/order-algo", """{"code":"0","msg":"","data":[{"algoId":"A1","sCode":"0","sMsg":""}]}""");
        var b = new OkxBroker("K", "S", "P", http: h.Client());
        var o = await b.SubmitAsync(Order.StopMarket("ETH-USDT", Side.Buy, 1.5, 3_100), Ct);
        var body = JsonNode.Parse(h.Bodies[0]!)!;
        Assert.Equal("trigger", Str(body, "ordType"));
        Assert.Equal("-1", Str(body, "orderPx"));                       // -1 = market on trigger
        Assert.Equal("3100", Str(body, "triggerPx"));
        Assert.Equal("base_ccy", Str(body, "tgtCcy"));
        Assert.Null(body["triggerPxType"]);                              // venue default (last)

        await b.RefreshAsync(Ct);                                        // live, not triggered
        Assert.Equal(OrderStatus.New, o.Status);
        Assert.Equal("/api/v5/trade/order-algo", h.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal(OkxSign(h.Requests[1], ""), h.Requests[1].Headers.GetValues("OK-ACCESS-SIGN").Single());

        h.Override("/api/v5/trade/order-algo?algoId=", """{"code":"0","msg":"","data":[{"algoId":"A1","state":"effective","ordIdList":["590908157585625111"]}]}""")
         .On("/api/v5/trade/order?instId=", """{"code":"0","msg":"","data":[{"ordId":"590908157585625111","state":"filled","accFillSz":"1.5","avgPx":"3101.2"}]}""");
        await b.RefreshAsync(Ct);
        Assert.Equal(OrderStatus.Filled, o.Status);
        Assert.Equal(3101.2, o.AverageFillPrice, 9);
        Assert.Contains("ordId=590908157585625111", h.Requests[^1].RequestUri!.Query);
    }

    [Fact]
    public async Task OkxAttachesTpSlAndPlacesConditionalAndOcoAlgos()
    {
        var h = new FakeHandler()
            .On("/api/v5/trade/order-algo", """{"code":"0","msg":"","data":[{"algoId":"T1","sCode":"0","sMsg":""}]}""")
            .On("/api/v5/trade/order", """{"code":"0","msg":"","data":[{"ordId":"O1","sCode":"0","sMsg":""}]}""");
        var b = new OkxBroker("K", "S", "P", http: h.Client()) { TradeMode = "cross" };

        var entry = new Order("BTC-USDT-SWAP", Side.Buy, 2, OrderType.Limit, 61_000) { TakeProfitPrice = 66_000, StopLossPrice = 58_000, TriggerBy = TriggerPriceType.Last };
        await b.SubmitAsync(entry, Ct);
        Assert.Equal("/api/v5/trade/order", h.Requests[0].RequestUri!.AbsolutePath);
        var body = JsonNode.Parse(h.Bodies[0]!)!;
        Assert.Equal("limit", Str(body, "ordType"));
        Assert.Equal("61000", Str(body, "px"));
        var att = body["attachAlgoOrds"]!.AsArray().Single()!;
        Assert.Equal("66000", Str(att, "tpTriggerPx"));
        Assert.Equal("-1", Str(att, "tpOrdPx"));
        Assert.Equal("58000", Str(att, "slTriggerPx"));
        Assert.Equal("-1", Str(att, "slOrdPx"));
        Assert.Equal("last", Str(att, "tpTriggerPxType"));
        Assert.Equal("last", Str(att, "slTriggerPxType"));
        Assert.Equal(OkxSign(h.Requests[0], h.Bodies[0]!), h.Requests[0].Headers.GetValues("OK-ACCESS-SIGN").Single());

        var oco = await b.SubmitTpSlAsync(new Order("BTC-USDT-SWAP", Side.Sell, 2) { TakeProfitPrice = 66_000, StopLossPrice = 58_000 }, Ct);
        Assert.True(b.IsAlgoOrder(oco.Id));
        var ob = JsonNode.Parse(h.Bodies[1]!)!;
        Assert.Equal("/api/v5/trade/order-algo", h.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("oco", Str(ob, "ordType"));
        Assert.Equal("cross", Str(ob, "tdMode"));
        Assert.Equal("sell", Str(ob, "side"));
        Assert.Equal("2", Str(ob, "sz"));
        Assert.Equal("66000", Str(ob, "tpTriggerPx"));
        Assert.Equal("-1", Str(ob, "tpOrdPx"));
        Assert.Equal("58000", Str(ob, "slTriggerPx"));
        Assert.Equal("-1", Str(ob, "slOrdPx"));
        Assert.Null(ob["triggerPx"]);
        Assert.Equal(OkxSign(h.Requests[1], h.Bodies[1]!), h.Requests[1].Headers.GetValues("OK-ACCESS-SIGN").Single());

        await b.SubmitTpSlAsync(new Order("BTC-USDT-SWAP", Side.Sell, 2, OrderType.Limit, 57_900) { StopLossPrice = 58_000 }, Ct);
        var cb = JsonNode.Parse(h.Bodies[2]!)!;
        Assert.Equal("conditional", Str(cb, "ordType"));
        Assert.Equal("58000", Str(cb, "slTriggerPx"));
        Assert.Equal("57900", Str(cb, "slOrdPx"));                       // limit leg
        Assert.Null(cb["tpTriggerPx"]);

        Assert.Throws<ArgumentException>(() => { _ = b.SubmitTpSlAsync(Order.MarketSell("BTC-USDT", 1), Ct); });
    }

    [Fact]
    public async Task OkxAlgoErrorsReject()
    {
        var h = new FakeHandler().On("/api/v5/trade/order-algo", """{"code":"1","msg":"","data":[{"algoId":"","sCode":"51277","sMsg":"TP trigger price cannot be higher than the last price"}]}""");
        var b = new OkxBroker("K", "S", "P", http: h.Client());
        var o = await b.SubmitAsync(Order.StopMarket("BTCUSDT", Side.Sell, 1, 60_000), Ct);
        Assert.Equal(OrderStatus.Rejected, o.Status);
        Assert.False(b.IsAlgoOrder(o.Id));
        var (state, triggered) = OkxBroker.ParseAlgoOrder(JsonNode.Parse("""{"state":"order_failed","ordId":""}""")!);
        Assert.True(state.IsRejected && state.IsFinal);
        Assert.Null(triggered);
    }
}
