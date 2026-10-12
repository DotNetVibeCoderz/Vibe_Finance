# Finance.Execution

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/execution.md) · Package: `Gravicode.Finance.Execution`

Getting orders to market: an order-management system, broker adapters (paper, Binance,
Interactive Brokers), a FIX 4.4 engine, execution algorithms, smart order routing, pre-trade risk
checks and transaction-cost analysis.

Namespaces: `Finance.Execution.Oms`, `.Brokers`, `.Fix`, `.Algorithms`, `.Routing`, `.Risk`,
`.Analytics`.

## Key concepts

- **OMS** — the system of record for orders. Each order moves through a state machine
  (New → PartiallyFilled → Filled / Cancelled / Rejected); every transition is an event.
- **FIX** — the industry protocol for orders and executions: `tag=value` fields separated by SOH,
  with a body length and a checksum. A *session* layer (Logon, Heartbeat, TestRequest,
  ResendRequest, SequenceReset, Logout) guarantees ordered delivery by sequence number.
- **Execution algorithms** — slice a large *parent* order into *child* orders: **TWAP** (equal
  slices over time), **VWAP** (slices follow the expected volume curve), **POV** (a fixed share of
  market volume), **Iceberg** (show only a small part of a limit order).
- **Smart order routing** — split an order across venues to get the best *all-in* price (price
  plus taker fees) by walking each venue's book.
- **Implementation shortfall** — the cost of trading relative to the price when the decision was
  made: market impact, timing and opportunity cost.

## Brokers

```csharp
using Finance.Core.Market;
using Finance.Execution.Brokers;
using Finance.Execution.Risk;

var risk = new PreTradeRiskChecker(new RiskLimits { MaxOrderNotional = 250_000, MaxPosition = 5_000, MaxPriceDeviation = 0.05 });
IBroker paper = new PaperBroker(initialCash: 100_000, currency: "USD", commission: CommissionModel.InteractiveBrokers, risk: risk);
paper.OrderUpdated += e => Console.WriteLine($"{e.Kind}: {e.Order}");

var pb = (PaperBroker)paper;
pb.UpdateQuote("AAPL", new Quote(DateTime.UtcNow, 189.95, 300, 190.05, 200));
Order filled = await paper.SubmitAsync(Order.MarketBuy("AAPL", 100));
Order resting = await paper.SubmitAsync(Order.LimitBuy("AAPL", 100, 185));
await paper.CancelAsync(resting.Id);

AccountInfo acct = await paper.GetAccountAsync();               // AccountId, Currency, Cash, Equity, BuyingPower
IReadOnlyList<Position> positions = await paper.GetPositionsAsync();
```

`PaperBroker` fills against quotes, order books (`UpdateOrderBook`) or last prices
(`UpdatePrice`), supports partial fills, latency and day-order expiry.

```csharp
// Binance: signed REST (HMAC-SHA256). Testnet by default.
IBroker binance = new BinanceBroker(apiKey, apiSecret, BinanceBroker.TestnetUrl);

// Interactive Brokers: Client Portal Web API through a locally running gateway
var ib = new InteractiveBrokersBroker(InteractiveBrokersBroker.CreateGatewayClient());   // https://localhost:5000/v1/api
long conid = await ib.ResolveConidAsync("AAPL");
```

## FIX 4.4

```csharp
using Finance.Execution.Fix;

var (clientSide, serverSide) = InMemoryFixTransport.CreatePair();      // or TcpFixTransport.ConnectAsync(host, port)
var session = new FixSession(new FixSessionSettings("CLIENT", "BROKER", IsInitiator: true, HeartBtIntSeconds: 30), clientSide);
session.MessageReceived += m => Console.WriteLine(m);    // fields printed as tag=value|...
session.StateChanged += st => Console.WriteLine(st);     // LogonSent, LoggedOn, ...
await session.StartAsync();                               // sends Logon, then heartbeats

var order = Order.LimitBuy("MSFT", 200, 410.25);
FixMessage nos = FixMapping.NewOrderSingle(order, clOrdId: "ORD-1");
await session.SendAsync(nos);

FixMessage parsed = FixMessage.Parse(nos.EncodeString());            // validates BodyLength and CheckSum
ExecutionReportInfo er = FixMapping.ParseExecutionReport(someExecutionReport);
Fill? fill = er.ToFill(order.Id);
await session.LogoutAsync();
```

`FixMessage` supports typed getters/setters, repeating groups (`AddGroup` / `GetGroup`) and
`FixFramer` splits a TCP byte stream into messages.

### Persistent sessions and FIX 5.0 SP2 (v0.3)

```csharp
var settings = FixSessionSettings.Fix50Sp2("CLIENT", "BROKER", isInitiator: true) with { ResetSeqNumOnLogon = false };
var store = new FileFixMessageStore(@"C:\fix\store", "CLIENT-BROKER");
await using var session = new FixSession(settings, await TcpFixTransport.ConnectAsync(host, port), store: store);
await session.StartAsync();
```

`FileFixMessageStore` persists both sequence numbers and every sent application message, flushed to disk
*before* the message reaches the wire. A session restarted, even in a new process, logs on with its next
sequence number, and anything the counterparty missed is recovered through ResendRequest, replayed with
PossDupFlag=Y while administrative messages are gap-filled. `FixSessionSettings.Fix50Sp2` speaks FIXT.1.1 and
sends DefaultApplVerID=9 at logon.

## More venues (v0.3)

| Broker | Auth | Notes |
|---|---|---|
| `AlpacaBroker` | APCA key headers | US equities/crypto; `PaperUrl` for paper trading |
| `OandaBroker` | Bearer token | FX/CFDs; signed units, "EUR_USD" instruments, FOK market orders |
| `BybitBroker` | HMAC-SHA256 (X-BAPI-*) | v5 unified API, spot by default; `TestnetUrl` |
| `OkxBroker` | HMAC-SHA256 Base64 + passphrase | v5 API, cash mode; demo trading via `demo: true` |
| `TwsClient` / `TwsBroker` | TWS / IB Gateway socket | orders (MKT/LMT/STP/STP LMT), order status, executions, open orders; positions, account summary, server time |

The REST brokers share `VenueBroker`: venues report cumulative quantity and average price, and `RefreshAsync`
turns that into incremental fills, recorded exactly once however often an order is polled.

```csharp
IBroker alpaca = new AlpacaBroker(keyId, secret);                      // paper by default
var order = await alpaca.SubmitAsync(Order.LimitBuy("AAPL", 10, 190));
await ((VenueBroker)alpaca).RefreshAsync();                            // poll fills / cancellations

await using var tws = await TwsClient.ConnectAsync("127.0.0.1", 7497, clientId: 1);
var positions = await tws.GetPositionsAsync();
var netLiq = (await tws.GetAccountSummaryAsync()).Single(v => v.Tag == "NetLiquidation");
```

### Stop and conditional orders

The shared `Order` expresses trigger orders without new order types: `OrderType.Stop` (stop-market) and
`OrderType.StopLimit` take their trigger from `StopPrice` (also exposed as `TriggerPrice`). Optional members:
`TriggerBy` (`TriggerPriceType.Last` / `Mark` / `Index`; null = venue default), `TriggerDirection` (`Rising` /
`Falling`; null = rising for buys, falling for sells, see `EffectiveTriggerDirection`), and attached
`TakeProfitPrice` / `StopLossPrice`. `Order.StopMarket(...)` and `Order.StopLimit(...)` are shortcuts.

```csharp
var stop = new Order("BTCUSDT", Side.Sell, 0.5, OrderType.Stop, stopPrice: 60_000) { TriggerBy = TriggerPriceType.Mark };
var entry = new Order("BTCUSDT", Side.Buy, 0.5, OrderType.Limit, 61_000) { TakeProfitPrice = 66_000, StopLossPrice = 58_000 };

var bybit = new BybitBroker(key, secret, category: "linear");
await bybit.SubmitAsync(stop);              // /v5/order/create: triggerPrice, triggerDirection 2, triggerBy MarkPrice
await bybit.SubmitAsync(entry);             // takeProfit / stopLoss on the order

var okx = new OkxBroker(key, secret, passphrase) { TradeMode = "cross" };
var algo = await okx.SubmitAsync(Order.StopLimit("BTC-USDT-SWAP", Side.Sell, 1, 60_000, 59_900));   // order-algo, ordType=trigger
await okx.SubmitTpSlAsync(new Order("BTC-USDT-SWAP", Side.Sell, 1) { TakeProfitPrice = 66_000, StopLossPrice = 58_000 });   // ordType=oco
await okx.CancelAsync(algo.Id);             // algo orders cancel through cancel-algos
```

| Venue | Stop / stop-limit | Attached TP/SL | Cancel |
|---|---|---|---|
| Bybit (linear / inverse) | `POST /v5/order/create` + `triggerPrice`, `triggerDirection` (1 rising, 2 falling), `triggerBy` (LastPrice / MarkPrice / IndexPrice) | `takeProfit` / `stopLoss`, `tpTriggerBy` / `slTriggerBy` | `/v5/order/cancel` |
| Bybit (spot) | same endpoint + `triggerPrice` and `orderFilter` = `StopOrder` (or `tpslOrder` via `SpotConditionalFilter`); last price only | `takeProfit` / `stopLoss` | `/v5/order/cancel` with the same `orderFilter` |
| OKX | `POST /api/v5/trade/order-algo`, `ordType` = `trigger`, `triggerPx`, `orderPx` (-1 = market), `triggerPxType` (last / index / mark) | `attachAlgoOrds` (`tpTriggerPx`, `tpOrdPx`, `slTriggerPx`, `slOrdPx`); standalone `conditional` / `oco` via `SubmitTpSlAsync` | `POST /api/v5/trade/cancel-algos` (array of `{algoId, instId}`); a triggered algo is cancelled through its placed order |

`RefreshAsync` keeps working: Bybit's `Untriggered` / `Triggered` stay open and `Deactivated` is final; for OKX it
reads `GET /api/v5/trade/order-algo?algoId=…` and, once the algo is `effective`, follows `ordIdList` to the order
it placed and records fills from that order.

### Interactive Brokers TWS orders

```csharp
await using var tws = await TwsClient.ConnectAsync("127.0.0.1", 7497, clientId: 1);   // 7497 = paper TWS
await using var ib = new TwsBroker(tws) { Account = "DU1234567", OutsideRth = false };
ib.RegisterContract("EURUSD", TwsContract.Forex("EUR.USD"));                       // CASH on IDEALPRO
ib.RegisterContract("ES", TwsContract.Future("ES", "202512", "CME", multiplier: "50"));

Order o = await ib.SubmitAsync(new Order("AAPL", Side.Buy, 100, OrderType.Limit, 185.5) { TimeInForce = TimeInForce.Day });
ib.OrderUpdated += e => Console.WriteLine($"{e.Kind} {e.Fill}");                // fills come from execDetails
await ib.CancelAsync(o.Id);

// Low level: any MKT / LMT / STP / STP LMT order, with your own ids
long id = await tws.PlaceOrderAsync(TwsContract.Stock("MSFT"), TwsOrder.StopLimit("SELL", 50, stop: 400, limit: 399.5) with { Tif = "GTC", OutsideRth = true });
IReadOnlyList<TwsOpenOrder> open = await tws.GetOpenOrdersAsync();
tws.OrderStatus += s => Console.WriteLine($"{s.OrderId} {s.Status} {s.Filled}/{s.Remaining}");
tws.Execution += x => Console.WriteLine($"{x.ExecId} {x.Side} {x.Shares} @ {x.Price}");
```

- **Messages.** Outgoing: placeOrder (3), cancelOrder (4), reqOpenOrders (5). Incoming: orderStatus (3),
  error (4), openOrder (5, leading fields only: contract, action, quantity, type, prices, TIF, account, ids,
  outsideRth), nextValidId (9), execDetails (11), openOrderEnd (53), commissionReport (59).
- **Server version.** The client announces `v100..176`; the placeOrder field order (`TwsPlaceOrder.Fields`)
  follows the TWS API's `EClient.placeOrder` with **176 as the reference target**. Fields added after 100 are
  guarded by the version that introduced them (fractional quantity 101, conditions/adjusted-order block 102,
  modelCode 103, extOperator 105, soft-dollar tier 106, cashQty 111, MiFID II 138/139,
  dontUseAutoPriceForHedge 141, no VERSION field from 145, 148, 151, 158, 160, 162, 166, manualOrderTime 169);
  faProfile is still sent (it disappears at 177, beyond the range). cancelOrder adds manualOrderCancelTime from
  169. Incoming orderStatus / execDetails / openOrder drop their version field from 131 / 136 / 145.
- **Mapping.** Market → MKT, limit → LMT, stop → STP (`auxPrice` = stop), stop-limit → STP LMT; DAY / GTC / IOC
  (FOK is rejected); `TriggerBy = Last` → triggerMethod 2. orderStatus PreSubmitted / Submitted → Accepted,
  Cancelled / ApiCancelled → Cancelled, Inactive → Rejected (Cancelled after a partial fill); error 202 → Cancelled,
  other order errors before acknowledgement → Rejected (warnings 399, 404, 161, 10147, 10148 are ignored).
  Each execDetails becomes one `Fill` (corrections with the same execId root are not double-counted);
  commissionReport amounts are summed in `ReportedCommission(orderId)`, while `Fill.Commission` uses the
  `Commission` model.

## Algorithms, routing, TCA

```csharp
using Finance.Execution.Algorithms;
using Finance.Execution.Routing;
using Finance.Execution.Analytics;

var start = DateTime.Today.AddHours(9.5); var end = DateTime.Today.AddHours(16);
IReadOnlyList<ChildOrder> twap = ExecutionAlgorithms.Twap(50_000, start, end, slices: 26, lotSize: 100);
IReadOnlyList<ChildOrder> vwap = ExecutionAlgorithms.Vwap(50_000, start, end, ExecutionAlgorithms.UShapedProfile(26), lotSize: 100);
IReadOnlyList<ChildOrder> ice = ExecutionAlgorithms.Iceberg(20_000, displayQuantity: 500, limitPrice: 99.5, start);
Order child = twap[0].ToOrder("AAPL", Side.Buy);

RoutingPlan plan = SmartOrderRouter.Route(Side.Buy, 12.5, new[]
{
    new Venue("Binance", binanceBook, TakerFeeBps: 10),
    new Venue("Coinbase", coinbaseBook, TakerFeeBps: 40),
});
Console.WriteLine($"filled {plan.FilledQuantity} at all-in {plan.AllInPrice:F2}, fees {plan.TotalFees:F2}, complete {plan.IsComplete}");

ShortfallReport shortfall = TransactionCostAnalysis.ImplementationShortfall(Side.Buy, parentQuantity: 50_000, decisionPrice: 100, fills, closePrice: 101.2);
Console.WriteLine($"shortfall {shortfall.TotalBps:F1} bps (execution {shortfall.ExecutionBps:F1}, opportunity {shortfall.OpportunityBps:F1})");
double impact = TransactionCostAnalysis.SquareRootImpact(quantity: 50_000, averageDailyVolume: 2_000_000, dailyVolatility: 0.02);
```

## Notes and limitations

- **TWS order placement and the Bybit/OKX conditional orders are tested against fakes and documented
  payloads, not live.** The TWS tests run a fake TWS on a loopback socket that asserts the exact placeOrder field
  sequence (written out from `EClient.placeOrder` for server versions 176 and 130) and replies with
  orderStatus / openOrder / execDetails / commissionReport / error frames; no real TWS or IB Gateway was used,
  so field-level drift in a TWS build is possible. The Bybit/OKX tests check request bodies against the
  documented v5 parameter names and recompute each signature over the exact body; no exchange was called.
- `TwsBroker` supports stock (STK), forex (CASH) and future (FUT) contracts with MKT / LMT / STP / STP LMT only;
  combos, options, algos, trailing and conditions are not encoded. Attached TP/SL is not mapped for TWS (build a
  bracket with `TwsOrder.ParentId` / `Transmit` on `TwsClient`). openOrder is only read up to `outsideRth`; order
  state comes from orderStatus.
- Bybit spot conditional orders trigger on the last price only (`TriggerBy` Mark/Index is rejected). Bybit
  position-level TP/SL (`/v5/position/trading-stop`) and OKX trailing / iceberg / TWAP algos are not covered.
- Live status of the v0.3 venues: none was traded live. Alpaca, OANDA, Bybit and OKX need accounts, and Bybit
  and OKX are also unreachable from the development network (TLS reset, as with Binance's main domain). All
  four are tested against payloads in their documented formats.

- The Interactive Brokers adapter targets the **Client Portal Web API** (REST via the IB gateway);
  it is tested against recorded payloads, not a running gateway. For the TWS socket API use `TwsBroker`.
- FIX sessions keep sequence numbers in memory — there is no persistent message store yet, so a
  restart cannot replay a gap.
- Once `PaperBroker` has received an order book for a symbol it matches that symbol against the
  book and ignores quotes.
- Never point `BinanceBroker` at `LiveUrl` with real keys before testing on the testnet.

---

Made by Gravicode Studios, led by Kang Fadhil
