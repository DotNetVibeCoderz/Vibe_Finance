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

### Sesi persisten dan FIX 5.0 SP2 (v0.3)

```csharp
var settings = FixSessionSettings.Fix50Sp2("CLIENT", "BROKER", isInitiator: true) with { ResetSeqNumOnLogon = false };
var store = new FileFixMessageStore(@"C:\fix\store", "CLIENT-BROKER");
await using var session = new FixSession(settings, await TcpFixTransport.ConnectAsync(host, port), store: store);
await session.StartAsync();
```

`FileFixMessageStore` menyimpan kedua nomor urut dan setiap pesan aplikasi yang dikirim, di-flush ke disk
*sebelum* pesan masuk ke jaringan. Sesi yang dimulai ulang, bahkan di proses baru, logon dengan nomor urut
berikutnya, dan apa pun yang terlewat oleh lawan dipulihkan lewat ResendRequest, diputar ulang dengan
PossDupFlag=Y sementara pesan administratif diisi gap-fill. `FixSessionSettings.Fix50Sp2` memakai FIXT.1.1 dan
mengirim DefaultApplVerID=9 saat logon.

## Venue tambahan (v0.3)

| Broker | Autentikasi | Catatan |
|---|---|---|
| `AlpacaBroker` | header kunci APCA | Saham AS/kripto; `PaperUrl` untuk paper trading |
| `OandaBroker` | Bearer token | FX/CFD; unit bertanda, instrumen "EUR_USD", order market FOK |
| `BybitBroker` | HMAC-SHA256 (X-BAPI-*) | API unified v5, default spot; `TestnetUrl` |
| `OkxBroker` | HMAC-SHA256 Base64 + passphrase | API v5, mode cash; demo trading lewat `demo: true` |
| `TwsClient` / `TwsBroker` | socket TWS / IB Gateway | order (MKT/LMT/STP/STP LMT), status order, eksekusi, order terbuka; posisi, ringkasan akun, waktu server |

Broker REST berbagi `VenueBroker`: venue melaporkan kuantitas kumulatif dan harga rata-rata, dan `RefreshAsync`
mengubahnya menjadi fill inkremental yang tercatat tepat sekali seberapa sering pun order di-poll.

```csharp
IBroker alpaca = new AlpacaBroker(keyId, secret);                      // default paper
var order = await alpaca.SubmitAsync(Order.LimitBuy("AAPL", 10, 190));
await ((VenueBroker)alpaca).RefreshAsync();                            // poll fill / pembatalan

await using var tws = await TwsClient.ConnectAsync("127.0.0.1", 7497, clientId: 1);
var positions = await tws.GetPositionsAsync();
var netLiq = (await tws.GetAccountSummaryAsync()).Single(v => v.Tag == "NetLiquidation");
```

### Order stop dan kondisional

`Order` bersama mengekspresikan order pemicu tanpa tipe order baru: `OrderType.Stop` (stop-market) dan
`OrderType.StopLimit` mengambil harga pemicu dari `StopPrice` (juga tersedia sebagai `TriggerPrice`). Member
opsional: `TriggerBy` (`TriggerPriceType.Last` / `Mark` / `Index`; null = default venue), `TriggerDirection`
(`Rising` / `Falling`; null = naik untuk beli, turun untuk jual, lihat `EffectiveTriggerDirection`), serta
`TakeProfitPrice` / `StopLossPrice` yang dilampirkan. `Order.StopMarket(...)` dan `Order.StopLimit(...)` adalah
pintasan.

```csharp
var stop = new Order("BTCUSDT", Side.Sell, 0.5, OrderType.Stop, stopPrice: 60_000) { TriggerBy = TriggerPriceType.Mark };
var entry = new Order("BTCUSDT", Side.Buy, 0.5, OrderType.Limit, 61_000) { TakeProfitPrice = 66_000, StopLossPrice = 58_000 };

var bybit = new BybitBroker(key, secret, category: "linear");
await bybit.SubmitAsync(stop);              // /v5/order/create: triggerPrice, triggerDirection 2, triggerBy MarkPrice
await bybit.SubmitAsync(entry);             // takeProfit / stopLoss pada order

var okx = new OkxBroker(key, secret, passphrase) { TradeMode = "cross" };
var algo = await okx.SubmitAsync(Order.StopLimit("BTC-USDT-SWAP", Side.Sell, 1, 60_000, 59_900));   // order-algo, ordType=trigger
await okx.SubmitTpSlAsync(new Order("BTC-USDT-SWAP", Side.Sell, 1) { TakeProfitPrice = 66_000, StopLossPrice = 58_000 });   // ordType=oco
await okx.CancelAsync(algo.Id);             // order algo dibatalkan lewat cancel-algos
```

| Venue | Stop / stop-limit | TP/SL terlampir | Pembatalan |
|---|---|---|---|
| Bybit (linear / inverse) | `POST /v5/order/create` + `triggerPrice`, `triggerDirection` (1 naik, 2 turun), `triggerBy` (LastPrice / MarkPrice / IndexPrice) | `takeProfit` / `stopLoss`, `tpTriggerBy` / `slTriggerBy` | `/v5/order/cancel` |
| Bybit (spot) | endpoint yang sama + `triggerPrice` dan `orderFilter` = `StopOrder` (atau `tpslOrder` lewat `SpotConditionalFilter`); hanya harga last | `takeProfit` / `stopLoss` | `/v5/order/cancel` dengan `orderFilter` yang sama |
| OKX | `POST /api/v5/trade/order-algo`, `ordType` = `trigger`, `triggerPx`, `orderPx` (-1 = market), `triggerPxType` (last / index / mark) | `attachAlgoOrds` (`tpTriggerPx`, `tpOrdPx`, `slTriggerPx`, `slOrdPx`); `conditional` / `oco` mandiri lewat `SubmitTpSlAsync` | `POST /api/v5/trade/cancel-algos` (array `{algoId, instId}`); algo yang sudah terpicu dibatalkan lewat order yang ditempatkannya |

`RefreshAsync` tetap berlaku: status Bybit `Untriggered` / `Triggered` tetap terbuka dan `Deactivated` bersifat
final; untuk OKX dibaca `GET /api/v5/trade/order-algo?algoId=…` dan, begitu algo `effective`, `ordIdList` diikuti
ke order yang ditempatkannya lalu fill dicatat dari order itu.

### Order Interactive Brokers TWS

```csharp
await using var tws = await TwsClient.ConnectAsync("127.0.0.1", 7497, clientId: 1);   // 7497 = TWS paper
await using var ib = new TwsBroker(tws) { Account = "DU1234567", OutsideRth = false };
ib.RegisterContract("EURUSD", TwsContract.Forex("EUR.USD"));                       // CASH di IDEALPRO
ib.RegisterContract("ES", TwsContract.Future("ES", "202512", "CME", multiplier: "50"));

Order o = await ib.SubmitAsync(new Order("AAPL", Side.Buy, 100, OrderType.Limit, 185.5) { TimeInForce = TimeInForce.Day });
ib.OrderUpdated += e => Console.WriteLine($"{e.Kind} {e.Fill}");                // fill berasal dari execDetails
await ib.CancelAsync(o.Id);

// Level rendah: order MKT / LMT / STP / STP LMT apa pun, dengan id sendiri
long id = await tws.PlaceOrderAsync(TwsContract.Stock("MSFT"), TwsOrder.StopLimit("SELL", 50, stop: 400, limit: 399.5) with { Tif = "GTC", OutsideRth = true });
IReadOnlyList<TwsOpenOrder> open = await tws.GetOpenOrdersAsync();
tws.OrderStatus += s => Console.WriteLine($"{s.OrderId} {s.Status} {s.Filled}/{s.Remaining}");
tws.Execution += x => Console.WriteLine($"{x.ExecId} {x.Side} {x.Shares} @ {x.Price}");
```

- **Pesan.** Keluar: placeOrder (3), cancelOrder (4), reqOpenOrders (5). Masuk: orderStatus (3), error (4),
  openOrder (5, hanya field awal: kontrak, action, kuantitas, tipe, harga, TIF, akun, id, outsideRth),
  nextValidId (9), execDetails (11), openOrderEnd (53), commissionReport (59).
- **Versi server.** Klien mengumumkan `v100..176`; urutan field placeOrder (`TwsPlaceOrder.Fields`) mengikuti
  `EClient.placeOrder` dari TWS API dengan **176 sebagai target acuan**. Field yang ditambahkan setelah 100
  dijaga oleh versi yang memperkenalkannya (kuantitas pecahan 101, blok conditions/adjusted-order 102,
  modelCode 103, extOperator 105, soft-dollar tier 106, cashQty 111, MiFID II 138/139,
  dontUseAutoPriceForHedge 141, tanpa field VERSION mulai 145, 148, 151, 158, 160, 162, 166, manualOrderTime
  169); faProfile masih dikirim (baru hilang di 177, di luar rentang). cancelOrder menambah manualOrderCancelTime
  mulai 169. orderStatus / execDetails / openOrder yang masuk tidak lagi membawa field versi mulai 131 / 136 / 145.
- **Pemetaan.** Market → MKT, limit → LMT, stop → STP (`auxPrice` = stop), stop-limit → STP LMT; DAY / GTC / IOC
  (FOK ditolak); `TriggerBy = Last` → triggerMethod 2. orderStatus PreSubmitted / Submitted → Accepted,
  Cancelled / ApiCancelled → Cancelled, Inactive → Rejected (Cancelled bila sudah terisi sebagian); error 202 →
  Cancelled, error order lain sebelum konfirmasi → Rejected (peringatan 399, 404, 161, 10147, 10148 diabaikan).
  Setiap execDetails menjadi satu `Fill` (koreksi dengan akar execId yang sama tidak dihitung dua kali); nilai
  commissionReport dijumlahkan di `ReportedCommission(orderId)`, sedangkan `Fill.Commission` memakai model
  `Commission`.

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

- **Penempatan order TWS dan order kondisional Bybit/OKX diuji terhadap fake dan payload terdokumentasi,
  bukan secara live.** Pengujian TWS menjalankan TWS palsu di socket loopback yang memeriksa urutan field
  placeOrder secara persis (dituliskan dari `EClient.placeOrder` untuk versi server 176 dan 130) dan membalas
  dengan frame orderStatus / openOrder / execDetails / commissionReport / error; tidak ada TWS atau IB Gateway
  sungguhan yang dipakai, sehingga pergeseran field pada build TWS tertentu masih mungkin. Pengujian Bybit/OKX
  memeriksa body request terhadap nama parameter v5 yang terdokumentasi dan menghitung ulang setiap tanda tangan
  atas body persisnya; tidak ada bursa yang dipanggil.
- `TwsBroker` hanya mendukung kontrak saham (STK), forex (CASH), dan futures (FUT) dengan MKT / LMT / STP /
  STP LMT; kombo, opsi, algo, trailing, dan conditions tidak dikodekan. TP/SL terlampir tidak dipetakan untuk TWS
  (bangun bracket dengan `TwsOrder.ParentId` / `Transmit` di `TwsClient`). openOrder hanya dibaca sampai
  `outsideRth`; status order berasal dari orderStatus.
- Order kondisional spot Bybit hanya terpicu oleh harga last (`TriggerBy` Mark/Index ditolak). TP/SL tingkat
  posisi Bybit (`/v5/position/trading-stop`) serta algo trailing / iceberg / TWAP OKX belum dicakup.
- Status live venue v0.3: belum ada yang ditransaksikan secara live. Alpaca, OANDA, Bybit, dan OKX memerlukan
  akun, dan Bybit serta OKX juga tidak dapat dijangkau dari jaringan pengembangan (koneksi TLS di-reset, seperti
  domain utama Binance). Keempatnya diuji dengan payload sesuai format dokumentasinya.

- Adapter Interactive Brokers menargetkan **Client Portal Web API** (REST melalui gateway IB); diuji
  dengan payload rekaman, belum dengan gateway yang berjalan. Untuk API socket TWS gunakan `TwsBroker`.
- Sesi FIX menyimpan nomor urut di memori — belum ada penyimpanan pesan persisten, sehingga setelah
  restart celah pesan tidak bisa diputar ulang.
- Begitu `PaperBroker` menerima order book untuk suatu simbol, pencocokan simbol itu memakai book dan
  mengabaikan kuotasi.
- Jangan arahkan `BinanceBroker` ke `LiveUrl` dengan kunci asli sebelum menguji di testnet.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
