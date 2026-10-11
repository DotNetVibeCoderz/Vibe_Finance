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
| `TwsClient` | TWS / IB Gateway socket | account monitoring: positions, account summary, server time, ids, cancels |

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

- `TwsClient` does not place orders: `placeOrder` has a long, version-dependent field list that could not be
  verified against a live TWS here. Place IB orders through `InteractiveBrokersBroker` (Client Portal Web API).
- Live status of the v0.3 venues: none was traded live. Alpaca, OANDA, Bybit and OKX need accounts, and Bybit
  and OKX are also unreachable from the development network (TLS reset, as with Binance's main domain). All
  four are tested against payloads in their documented formats; stop orders on Bybit/OKX (algo/conditional
  endpoints) are not covered.

- The Interactive Brokers adapter targets the **Client Portal Web API** (REST via the IB gateway);
  it is tested against recorded payloads, not a running gateway. The TWS socket API is on the roadmap.
- FIX sessions keep sequence numbers in memory — there is no persistent message store yet, so a
  restart cannot replay a gap.
- Once `PaperBroker` has received an order book for a symbol it matches that symbol against the
  book and ignores quotes.
- Never point `BinanceBroker` at `LiveUrl` with real keys before testing on the testnet.

---

Made by Gravicode Studios, led by Kang Fadhil
