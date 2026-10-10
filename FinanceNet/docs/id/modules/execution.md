# Finance.Execution

[← Indeks](../index.md) · [English](../../en/modules/execution.md) · Paket: `Gravicode.Finance.Execution`

Mengantar order ke pasar: sistem manajemen order (OMS), adapter broker (paper, Binance, Interactive
Brokers), engine FIX 4.4, algoritma eksekusi, smart order routing, pemeriksaan risiko pra-transaksi,
dan analisis biaya transaksi.

Namespace: `Finance.Execution.Oms`, `.Brokers`, `.Fix`, `.Algorithms`, `.Routing`, `.Risk`,
`.Analytics`.

## Konsep utama

- **OMS** — sumber kebenaran untuk order. Setiap order bergerak melalui state machine
  (New → PartiallyFilled → Filled / Cancelled / Rejected); setiap transisi adalah sebuah event.
- **FIX** — protokol industri untuk order dan eksekusi: field `tag=value` yang dipisahkan SOH, lengkap
  dengan body length dan checksum. Lapisan *session* (Logon, Heartbeat, TestRequest, ResendRequest,
  SequenceReset, Logout) menjamin urutan pengiriman lewat nomor urut.
- **Algoritma eksekusi** — memecah order *induk* besar menjadi order *anak*: **TWAP** (potongan sama
  rata sepanjang waktu), **VWAP** (potongan mengikuti kurva volume yang diharapkan), **POV** (porsi
  tetap dari volume pasar), **Iceberg** (hanya menampilkan sebagian kecil limit order).
- **Smart order routing** — membagi order ke beberapa venue untuk mendapat harga *all-in* terbaik
  (harga ditambah taker fee) dengan menyapu order book tiap venue.
- **Implementation shortfall** — biaya trading relatif terhadap harga saat keputusan diambil: dampak
  pasar, timing, dan opportunity cost.

## Broker

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

`PaperBroker` mengisi order berdasarkan kuotasi, order book (`UpdateOrderBook`), atau harga terakhir
(`UpdatePrice`), mendukung partial fill, latensi, dan kedaluwarsa day order.

```csharp
// Binance: REST bertanda tangan (HMAC-SHA256). Bawaan memakai testnet.
IBroker binance = new BinanceBroker(apiKey, apiSecret, BinanceBroker.TestnetUrl);

// Interactive Brokers: Client Portal Web API melalui gateway yang berjalan lokal
var ib = new InteractiveBrokersBroker(InteractiveBrokersBroker.CreateGatewayClient());   // https://localhost:5000/v1/api
long conid = await ib.ResolveConidAsync("AAPL");
```

## FIX 4.4

```csharp
using Finance.Execution.Fix;

var (clientSide, serverSide) = InMemoryFixTransport.CreatePair();      // atau TcpFixTransport.ConnectAsync(host, port)
var session = new FixSession(new FixSessionSettings("CLIENT", "BROKER", IsInitiator: true, HeartBtIntSeconds: 30), clientSide);
session.MessageReceived += m => Console.WriteLine(m);    // field dicetak sebagai tag=value|...
session.StateChanged += st => Console.WriteLine(st);     // LogonSent, LoggedOn, ...
await session.StartAsync();                               // mengirim Logon, lalu heartbeat

var order = Order.LimitBuy("MSFT", 200, 410.25);
FixMessage nos = FixMapping.NewOrderSingle(order, clOrdId: "ORD-1");
await session.SendAsync(nos);

FixMessage parsed = FixMessage.Parse(nos.EncodeString());            // memvalidasi BodyLength dan CheckSum
ExecutionReportInfo er = FixMapping.ParseExecutionReport(someExecutionReport);
Fill? fill = er.ToFill(order.Id);
await session.LogoutAsync();
```

`FixMessage` mendukung getter/setter bertipe, repeating group (`AddGroup` / `GetGroup`), dan
`FixFramer` memecah aliran byte TCP menjadi pesan-pesan.

## Algoritma, routing, TCA

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
Console.WriteLine($"terisi {plan.FilledQuantity} pada all-in {plan.AllInPrice:F2}, fee {plan.TotalFees:F2}, lengkap {plan.IsComplete}");

ShortfallReport shortfall = TransactionCostAnalysis.ImplementationShortfall(Side.Buy, parentQuantity: 50_000, decisionPrice: 100, fills, closePrice: 101.2);
Console.WriteLine($"shortfall {shortfall.TotalBps:F1} bps (eksekusi {shortfall.ExecutionBps:F1}, opportunity {shortfall.OpportunityBps:F1})");
double impact = TransactionCostAnalysis.SquareRootImpact(quantity: 50_000, averageDailyVolume: 2_000_000, dailyVolatility: 0.02);
```

## Catatan dan batasan

- Adapter Interactive Brokers menargetkan **Client Portal Web API** (REST melalui gateway IB); diuji
  dengan payload rekaman, belum dengan gateway yang berjalan. API socket TWS ada di roadmap.
- Sesi FIX menyimpan nomor urut di memori — belum ada penyimpanan pesan persisten, sehingga setelah
  restart celah pesan tidak bisa diputar ulang.
- Begitu `PaperBroker` menerima order book untuk suatu simbol, pencocokan simbol itu memakai book dan
  mengabaikan kuotasi.
- Jangan arahkan `BinanceBroker` ke `LiveUrl` dengan kunci asli sebelum menguji di testnet.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
