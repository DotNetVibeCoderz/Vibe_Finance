using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Finance.Core.Market;
using Finance.Execution.Oms;

namespace Finance.Execution.Brokers;

/// <summary>An order's state as reported by a venue: cumulative fill and its average price.</summary>
public readonly record struct VenueOrderState(string Status, double CumulativeQuantity, double AveragePrice, bool IsFinal, bool IsRejected = false);

/// <summary>
/// Shared plumbing for REST brokers that report orders as cumulative quantity plus average price: the order store,
/// our-id ↔ venue-id mapping, and turning cumulative state into incremental <see cref="Fill"/>s (so a fill is
/// recorded exactly once however often the order is polled). Venues implement signing and payload mapping.
/// </summary>
public abstract class VenueBroker : IBroker
{
    protected readonly HttpClient Http;
    protected readonly TimeProvider Time;
    private readonly Dictionary<long, string> _venueIds = new();

    protected VenueBroker(string baseUrl, HttpClient? http, TimeProvider? time)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        Http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        Time = time ?? TimeProvider.System;
        Store.OrderEvent += e => OrderUpdated?.Invoke(e);
    }

    public abstract string Name { get; }
    public string BaseUrl { get; }
    public OrderStore Store { get; } = new();
    public CommissionModel Commission { get; init; } = CommissionModel.Zero;
    public event Action<OrderEvent>? OrderUpdated;

    protected DateTime Now => Time.GetUtcNow().UtcDateTime;

    /// <summary>The venue's id for one of our orders.</summary>
    public string? VenueOrderId(long orderId) => _venueIds.TryGetValue(orderId, out var v) ? v : null;

    public async Task<Order> SubmitAsync(Order order, CancellationToken cancellationToken = default)
    {
        Store.Add(order, Now);
        try
        {
            var (venueId, state) = await PlaceAsync(order, cancellationToken).ConfigureAwait(false);
            _venueIds[order.Id] = venueId;
            Store.Accept(order, Now);
            if (state is { } s) Apply(order, s);
        }
        catch (BrokerException ex)
        {
            if (order.IsActive) Store.Reject(order, Now, ex.Message);
        }
        return order;
    }

    public async Task<bool> CancelAsync(long orderId, CancellationToken cancellationToken = default)
    {
        var order = Store.Get(orderId);
        if (order is null || !order.IsActive || !_venueIds.TryGetValue(orderId, out var venueId)) return false;
        try
        {
            await CancelAtVenueAsync(order, venueId, cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerException)
        {
            return false;
        }
        return Store.Cancel(orderId, Now, $"Cancelled on {Name}");
    }

    /// <summary>Polls every open order and records new fills, cancellations and rejections.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        foreach (var order in Store.ActiveOrders.ToList())
            if (_venueIds.TryGetValue(order.Id, out var venueId))
                Apply(order, await QueryAsync(order, venueId, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Records the part of <paramref name="state"/> not yet seen as a fill, then terminal status.</summary>
    protected void Apply(Order order, VenueOrderState state)
    {
        double newQty = Math.Min(state.CumulativeQuantity - order.FilledQuantity, order.RemainingQuantity);
        if (newQty > 1e-12 && state.AveragePrice > 0)
        {
            // Price of the increment from the change in cumulative notional.
            double px = (state.AveragePrice * state.CumulativeQuantity - order.AverageFillPrice * order.FilledQuantity) / newQty;
            Store.RecordFill(new Fill(order.Id, order.Symbol, order.Side, newQty, px, Now, Commission.Compute(newQty, px)));
        }
        if (!order.IsActive) return;
        if (state.IsRejected) Store.Reject(order, Now, $"{Name}: {state.Status}");
        else if (state.IsFinal) Store.Cancel(order.Id, Now, $"{Name}: {state.Status}");
    }

    protected abstract Task<(string VenueId, VenueOrderState? State)> PlaceAsync(Order order, CancellationToken ct);
    protected abstract Task CancelAtVenueAsync(Order order, string venueId, CancellationToken ct);
    protected abstract Task<VenueOrderState> QueryAsync(Order order, string venueId, CancellationToken ct);
    public abstract Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken cancellationToken = default);
    public abstract Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a request and returns the parsed body; non-2xx responses become <see cref="BrokerException"/>.</summary>
    protected async Task<JsonNode> SendAsync(HttpMethod method, string pathAndQuery, string? jsonBody, Action<HttpRequestMessage, string> sign, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, BaseUrl + pathAndQuery);
        if (jsonBody is not null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        sign(req, jsonBody ?? "");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new BrokerException($"{Name} {(int)resp.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");
        return string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body)!;
    }

    protected static string F(double v) => v.ToString("0.##########", CultureInfo.InvariantCulture);

    protected static double N(JsonNode? n) => n is null ? 0
        : double.TryParse(n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    protected static Position MakePosition(string symbol, double quantity, double averagePrice)
    {
        var p = new Position(symbol);
        if (Math.Abs(quantity) > 0) p.Apply(new Fill(0, symbol, quantity > 0 ? Side.Buy : Side.Sell, Math.Abs(quantity), averagePrice, DateTime.UtcNow));
        return p;
    }

    protected static string HmacHex(byte[] key, string payload) => Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
    protected static string HmacBase64(byte[] key, string payload) => Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
}

/// <summary>
/// Alpaca Trading API v2 (US equities and crypto). <see cref="PaperUrl"/> for paper trading. Authenticated with the
/// APCA-API-KEY-ID / APCA-API-SECRET-KEY headers.
/// </summary>
public sealed class AlpacaBroker : VenueBroker
{
    public const string PaperUrl = "https://paper-api.alpaca.markets", LiveUrl = "https://api.alpaca.markets";
    private readonly string _keyId, _secret;

    public AlpacaBroker(string keyId, string secretKey, string baseUrl = PaperUrl, HttpClient? http = null, TimeProvider? time = null)
        : base(baseUrl, http, time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        _keyId = keyId;
        _secret = secretKey;
    }

    public override string Name => "Alpaca";

    private void Sign(HttpRequestMessage r, string _)
    {
        r.Headers.TryAddWithoutValidation("APCA-API-KEY-ID", _keyId);
        r.Headers.TryAddWithoutValidation("APCA-API-SECRET-KEY", _secret);
    }

    /// <summary>The JSON body of POST /v2/orders for an order.</summary>
    public static JsonObject OrderBody(Order order)
    {
        var o = new JsonObject
        {
            ["symbol"] = order.Symbol.ToUpperInvariant(),
            ["qty"] = F(order.Quantity),
            ["side"] = order.Side == Side.Buy ? "buy" : "sell",
            ["type"] = order.Type switch { OrderType.Limit => "limit", OrderType.Stop => "stop", OrderType.StopLimit => "stop_limit", _ => "market" },
            ["time_in_force"] = order.TimeInForce switch { TimeInForce.Day => "day", TimeInForce.Ioc => "ioc", TimeInForce.Fok => "fok", _ => "gtc" },
            ["client_order_id"] = $"fnet{order.Id}",
        };
        if (order.LimitPrice is { } lp) o["limit_price"] = F(lp);
        if (order.StopPrice is { } sp) o["stop_price"] = F(sp);
        return o;
    }

    public static VenueOrderState ParseOrder(JsonNode o)
    {
        string status = o["status"]?.GetValue<string>() ?? "";
        return new VenueOrderState(status, N(o["filled_qty"]), N(o["filled_avg_price"]),
            status is "canceled" or "expired" or "done_for_day" or "replaced", status == "rejected");
    }

    protected override async Task<(string, VenueOrderState?)> PlaceAsync(Order order, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Post, "/v2/orders", OrderBody(order).ToJsonString(), Sign, ct).ConfigureAwait(false);
        return (r["id"]!.GetValue<string>(), ParseOrder(r));
    }

    protected override Task CancelAtVenueAsync(Order order, string venueId, CancellationToken ct)
        => SendAsync(HttpMethod.Delete, $"/v2/orders/{venueId}", null, Sign, ct);

    protected override async Task<VenueOrderState> QueryAsync(Order order, string venueId, CancellationToken ct)
        => ParseOrder(await SendAsync(HttpMethod.Get, $"/v2/orders/{venueId}", null, Sign, ct).ConfigureAwait(false));

    public override async Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        var r = await SendAsync(HttpMethod.Get, "/v2/positions", null, Sign, cancellationToken).ConfigureAwait(false);
        return r.AsArray().Select(p => MakePosition(p!["symbol"]!.GetValue<string>(), N(p["qty"]), N(p["avg_entry_price"]))).ToList();
    }

    public override async Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        var a = await SendAsync(HttpMethod.Get, "/v2/account", null, Sign, cancellationToken).ConfigureAwait(false);
        return new AccountInfo(a["account_number"]?.GetValue<string>() ?? "", a["currency"]?.GetValue<string>() ?? "USD", N(a["cash"]), N(a["equity"]), N(a["buying_power"]));
    }
}

/// <summary>
/// OANDA v20 REST for FX/CFDs. Units are signed (negative = sell); instruments like "EUR_USD". Market orders are
/// fill-or-kill and come back already filled (or cancelled) in the response.
/// </summary>
public sealed class OandaBroker : VenueBroker
{
    public const string PracticeUrl = "https://api-fxpractice.oanda.com", LiveUrl = "https://api-fxtrade.oanda.com";
    private readonly string _token, _accountId;

    public OandaBroker(string token, string accountId, string baseUrl = PracticeUrl, HttpClient? http = null, TimeProvider? time = null)
        : base(baseUrl, http, time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        _token = token;
        _accountId = accountId;
    }

    public override string Name => "OANDA";

    private void Sign(HttpRequestMessage r, string _) => r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);

    public static string Instrument(string symbol)
    {
        var s = symbol.Replace("/", "_").Replace("-", "_").ToUpperInvariant();
        return s.Length == 6 && !s.Contains('_') ? s[..3] + "_" + s[3..] : s;
    }

    public static JsonObject OrderBody(Order order)
    {
        double units = order.Side == Side.Buy ? order.Quantity : -order.Quantity;
        var o = new JsonObject
        {
            ["instrument"] = Instrument(order.Symbol),
            ["units"] = F(units),
            ["positionFill"] = "DEFAULT",
            ["clientExtensions"] = new JsonObject { ["id"] = $"fnet{order.Id}" },
        };
        switch (order.Type)
        {
            case OrderType.Market:
                o["type"] = "MARKET";
                o["timeInForce"] = order.TimeInForce == TimeInForce.Ioc ? "IOC" : "FOK";
                break;
            case OrderType.Limit:
                o["type"] = "LIMIT";
                o["price"] = F(order.LimitPrice!.Value);
                o["timeInForce"] = order.TimeInForce switch { TimeInForce.Ioc => "IOC", TimeInForce.Fok => "FOK", TimeInForce.Day => "GFD", _ => "GTC" };
                break;
            case OrderType.Stop:
                o["type"] = "STOP";
                o["price"] = F(order.StopPrice!.Value);
                o["timeInForce"] = "GTC";
                break;
            default:
                throw new BrokerException("OANDA stop-limit orders are not supported; use STOP with priceBound instead.");
        }
        return new JsonObject { ["order"] = o };
    }

    /// <summary>Reads the create/fill/cancel transactions of an order response.</summary>
    public static (string Id, VenueOrderState State) ParseCreate(JsonNode r)
    {
        string id = r["orderCreateTransaction"]?["id"]?.GetValue<string>() ?? "";
        if (r["orderFillTransaction"] is { } fill)
            return (id, new VenueOrderState("FILLED", Math.Abs(N(fill["units"])), N(fill["price"]), true));
        if (r["orderCancelTransaction"] is { } c)
            return (id, new VenueOrderState(c["reason"]?.GetValue<string>() ?? "CANCELLED", 0, 0, true));
        if (r["orderRejectTransaction"] is { } rej)
            return (id, new VenueOrderState(rej["rejectReason"]?.GetValue<string>() ?? "REJECTED", 0, 0, true, true));
        return (id, new VenueOrderState("PENDING", 0, 0, false));
    }

    protected override async Task<(string, VenueOrderState?)> PlaceAsync(Order order, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Post, $"/v3/accounts/{_accountId}/orders", OrderBody(order).ToJsonString(), Sign, ct).ConfigureAwait(false);
        var (id, state) = ParseCreate(r);
        return (id, state);
    }

    protected override Task CancelAtVenueAsync(Order order, string venueId, CancellationToken ct)
        => SendAsync(HttpMethod.Put, $"/v3/accounts/{_accountId}/orders/{venueId}/cancel", null, Sign, ct);

    protected override async Task<VenueOrderState> QueryAsync(Order order, string venueId, CancellationToken ct)
    {
        var o = (await SendAsync(HttpMethod.Get, $"/v3/accounts/{_accountId}/orders/{venueId}", null, Sign, ct).ConfigureAwait(false))["order"]!;
        string state = o["state"]?.GetValue<string>() ?? "";
        if (state == "FILLED")
        {
            // The fill price lives on the fill transaction.
            var txId = o["fillingTransactionID"]?.GetValue<string>();
            var tx = txId is null ? null : (await SendAsync(HttpMethod.Get, $"/v3/accounts/{_accountId}/transactions/{txId}", null, Sign, ct).ConfigureAwait(false))["transaction"];
            return new VenueOrderState(state, order.Quantity, N(tx?["price"]), true);
        }
        return new VenueOrderState(state, 0, 0, state is "CANCELLED", false);
    }

    public override async Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        var r = await SendAsync(HttpMethod.Get, $"/v3/accounts/{_accountId}/openPositions", null, Sign, cancellationToken).ConfigureAwait(false);
        var list = new List<Position>();
        foreach (var p in r["positions"]!.AsArray())
        {
            string inst = p!["instrument"]!.GetValue<string>();
            double longUnits = N(p["long"]?["units"]), shortUnits = N(p["short"]?["units"]);
            if (longUnits != 0) list.Add(MakePosition(inst, longUnits, N(p["long"]?["averagePrice"])));
            if (shortUnits != 0) list.Add(MakePosition(inst, shortUnits, N(p["short"]?["averagePrice"])));
        }
        return list;
    }

    public override async Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        var a = (await SendAsync(HttpMethod.Get, $"/v3/accounts/{_accountId}/summary", null, Sign, cancellationToken).ConfigureAwait(false))["account"]!;
        return new AccountInfo(a["id"]?.GetValue<string>() ?? _accountId, a["currency"]?.GetValue<string>() ?? "", N(a["balance"]), N(a["NAV"]), N(a["marginAvailable"]));
    }
}

/// <summary>
/// Bybit v5 unified trading (spot by default). Requests are signed with HMAC-SHA256 over
/// <c>timestamp + apiKey + recvWindow + (query string | JSON body)</c>, hex-encoded in X-BAPI-SIGN.
/// <see cref="TestnetUrl"/> for paper trading.
/// </summary>
/// <remarks>
/// Conditional orders (<see cref="OrderType.Stop"/> = stop-market, <see cref="OrderType.StopLimit"/> = stop-limit) go
/// through <c>POST /v5/order/create</c> with <c>triggerPrice</c>. For linear/inverse they also carry
/// <c>triggerDirection</c> (1 rising, 2 falling) and <c>triggerBy</c> (LastPrice / MarkPrice / IndexPrice); spot
/// conditional orders instead carry <c>orderFilter</c> (<see cref="SpotConditionalFilter"/>: <c>StopOrder</c> or
/// <c>tpslOrder</c>) and only trigger on the last price. <see cref="Order.TakeProfitPrice"/> /
/// <see cref="Order.StopLossPrice"/> become <c>takeProfit</c> / <c>stopLoss</c> (with <c>tpTriggerBy</c> /
/// <c>slTriggerBy</c> for derivatives). Built from the documented v5 field names; not exercised against the live API.
/// </remarks>
public sealed class BybitBroker : VenueBroker
{
    public const string LiveUrl = "https://api.bybit.com", TestnetUrl = "https://api-testnet.bybit.com";
    private readonly string _apiKey;
    private readonly byte[] _secret;

    public BybitBroker(string apiKey, string apiSecret, string baseUrl = TestnetUrl, string category = "spot", HttpClient? http = null, TimeProvider? time = null)
        : base(baseUrl, http, time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiSecret);
        _apiKey = apiKey;
        _secret = Encoding.UTF8.GetBytes(apiSecret);
        Category = category;
    }

    public override string Name => "Bybit";
    public string Category { get; }
    public int RecvWindow { get; init; } = 5000;

    /// <summary>X-BAPI-SIGN for a request: HMAC-SHA256(secret, timestamp + key + recvWindow + payload), lower-case hex.</summary>
    public static string Signature(byte[] secret, long timestamp, string apiKey, int recvWindow, string payload)
        => HmacHex(secret, $"{timestamp}{apiKey}{recvWindow}{payload}");

    private void Sign(HttpRequestMessage r, string body)
    {
        long ts = Time.GetUtcNow().ToUnixTimeMilliseconds();
        string payload = r.Method == HttpMethod.Get ? r.RequestUri!.Query.TrimStart('?') : body;
        r.Headers.TryAddWithoutValidation("X-BAPI-API-KEY", _apiKey);
        r.Headers.TryAddWithoutValidation("X-BAPI-TIMESTAMP", ts.ToString(CultureInfo.InvariantCulture));
        r.Headers.TryAddWithoutValidation("X-BAPI-RECV-WINDOW", RecvWindow.ToString(CultureInfo.InvariantCulture));
        r.Headers.TryAddWithoutValidation("X-BAPI-SIGN", Signature(_secret, ts, _apiKey, RecvWindow, payload));
    }

    private static JsonNode Result(JsonNode r)
    {
        if (N(r["retCode"]) != 0) throw new BrokerException($"Bybit {r["retCode"]}: {r["retMsg"]}");
        return r["result"]!;
    }

    /// <summary><c>orderFilter</c> sent with spot conditional orders: <c>StopOrder</c> (default) or <c>tpslOrder</c>.</summary>
    public string SpotConditionalFilter { get; init; } = "StopOrder";

    private bool IsSpot => Category == "spot";

    private static string Symbol(Order order) => order.Symbol.Replace("-", "").Replace("/", "").ToUpperInvariant();

    /// <summary>Bybit's name for a trigger reference: LastPrice, MarkPrice or IndexPrice.</summary>
    public static string TriggerByName(TriggerPriceType t) => t switch
    {
        TriggerPriceType.Mark => "MarkPrice",
        TriggerPriceType.Index => "IndexPrice",
        _ => "LastPrice",
    };

    /// <summary>The <c>orderFilter</c> an order is created / cancelled with on spot (null for derivatives and plain orders).</summary>
    public string? OrderFilter(Order order) => IsSpot && order.IsConditional ? SpotConditionalFilter : null;

    public JsonObject OrderBody(Order order)
    {
        var o = new JsonObject
        {
            ["category"] = Category,
            ["symbol"] = Symbol(order),
            ["side"] = order.Side == Side.Buy ? "Buy" : "Sell",
            ["orderType"] = order.Type is OrderType.Limit or OrderType.StopLimit ? "Limit" : "Market",
            ["qty"] = F(order.Quantity),
            ["orderLinkId"] = $"fnet{order.Id}",
            ["timeInForce"] = order.TimeInForce switch { TimeInForce.Ioc => "IOC", TimeInForce.Fok => "FOK", _ => "GTC" },
        };
        if (order.Type is OrderType.Market or OrderType.Stop && IsSpot) o["marketUnit"] = "baseCoin";   // qty in base currency
        if (order.LimitPrice is { } lp) o["price"] = F(lp);
        if (order.TriggerPrice is { } tp)
        {
            o["triggerPrice"] = F(tp);
            if (IsSpot)
            {
                if (order.TriggerBy is { } t && t != TriggerPriceType.Last)
                    throw new BrokerException("Bybit spot conditional orders trigger on the last price only.");
                o["orderFilter"] = SpotConditionalFilter;
            }
            else
            {
                o["triggerDirection"] = order.EffectiveTriggerDirection == TriggerDirection.Rising ? 1 : 2;
                o["triggerBy"] = TriggerByName(order.TriggerBy ?? TriggerPriceType.Last);
            }
        }
        if (order.TakeProfitPrice is { } take)
        {
            o["takeProfit"] = F(take);
            if (!IsSpot && order.TriggerBy is { } t) o["tpTriggerBy"] = TriggerByName(t);
        }
        if (order.StopLossPrice is { } loss)
        {
            o["stopLoss"] = F(loss);
            if (!IsSpot && order.TriggerBy is { } t) o["slTriggerBy"] = TriggerByName(t);
        }
        return o;
    }

    /// <summary>The JSON body of <c>POST /v5/order/cancel</c> (spot conditional orders need their <c>orderFilter</c>).</summary>
    public JsonObject CancelBody(Order order, string venueId)
    {
        var body = new JsonObject { ["category"] = Category, ["symbol"] = Symbol(order), ["orderId"] = venueId };
        if (OrderFilter(order) is { } filter) body["orderFilter"] = filter;
        return body;
    }

    public static VenueOrderState ParseOrder(JsonNode o)
    {
        string status = o["orderStatus"]?.GetValue<string>() ?? "";
        return new VenueOrderState(status, N(o["cumExecQty"]), N(o["avgPrice"]),
            status is "Cancelled" or "PartiallyFilledCanceled" or "Deactivated", status == "Rejected");   // Untriggered / Triggered stay open
    }

    protected override async Task<(string, VenueOrderState?)> PlaceAsync(Order order, CancellationToken ct)
    {
        var r = Result(await SendAsync(HttpMethod.Post, "/v5/order/create", OrderBody(order).ToJsonString(), Sign, ct).ConfigureAwait(false));
        return (r["orderId"]!.GetValue<string>(), null);
    }

    protected override async Task CancelAtVenueAsync(Order order, string venueId, CancellationToken ct)
        => Result(await SendAsync(HttpMethod.Post, "/v5/order/cancel", CancelBody(order, venueId).ToJsonString(), Sign, ct).ConfigureAwait(false));

    protected override async Task<VenueOrderState> QueryAsync(Order order, string venueId, CancellationToken ct)
    {
        string filter = OrderFilter(order) is { } f ? "&orderFilter=" + f : "";
        var r = Result(await SendAsync(HttpMethod.Get, $"/v5/order/realtime?category={Category}&orderId={venueId}{filter}", null, Sign, ct).ConfigureAwait(false));
        var list = r["list"]!.AsArray();
        return list.Count == 0 ? new VenueOrderState("Unknown", order.FilledQuantity, order.AverageFillPrice, false) : ParseOrder(list[0]!);
    }

    public override async Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        var w = Result(await SendAsync(HttpMethod.Get, "/v5/account/wallet-balance?accountType=UNIFIED", null, Sign, cancellationToken).ConfigureAwait(false));
        var coins = w["list"]![0]!["coin"]!.AsArray();
        return coins.Where(c => N(c!["walletBalance"]) != 0).Select(c => MakePosition(c!["coin"]!.GetValue<string>(), N(c["walletBalance"]), 0)).ToList();
    }

    public override async Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        var a = Result(await SendAsync(HttpMethod.Get, "/v5/account/wallet-balance?accountType=UNIFIED", null, Sign, cancellationToken).ConfigureAwait(false))["list"]![0]!;
        return new AccountInfo("UNIFIED", "USD", N(a["totalWalletBalance"]), N(a["totalEquity"]), N(a["totalAvailableBalance"]));
    }
}

/// <summary>
/// OKX v5 trading (spot cash mode by default). Signed with Base64(HMAC-SHA256(secret, timestamp + METHOD +
/// requestPath + body)), plus the API passphrase; <see cref="Demo"/> adds the x-simulated-trading header for the
/// demo-trading environment.
/// </summary>
/// <remarks>
/// Stop orders (<see cref="OrderType.Stop"/> / <see cref="OrderType.StopLimit"/>) are placed as algo orders through
/// <c>POST /api/v5/trade/order-algo</c> with <c>ordType</c> = <c>trigger</c> (<c>triggerPx</c>, <c>orderPx</c> = limit or -1
/// for market, <c>triggerPxType</c> last / index / mark) and cancelled with <c>POST /api/v5/trade/cancel-algos</c>.
/// <see cref="Order.TakeProfitPrice"/> / <see cref="Order.StopLossPrice"/> on an order are sent as
/// <c>attachAlgoOrds</c>; <see cref="SubmitTpSlAsync"/> places a standalone <c>conditional</c> (one leg) or <c>oco</c>
/// (both legs) TP/SL algo. Built from the documented v5 field names; not exercised against the live API.
/// </remarks>
public sealed class OkxBroker : VenueBroker
{
    private readonly string _apiKey, _passphrase;
    private readonly byte[] _secret;
    private readonly ConcurrentDictionary<long, string> _algoTriggeredOrdIds = new();   // our id -> triggered ordId ("" until triggered)
    private readonly ConcurrentDictionary<long, byte> _tpslOnly = new();

    public OkxBroker(string apiKey, string apiSecret, string passphrase, bool demo = true, string baseUrl = "https://www.okx.com",
        HttpClient? http = null, TimeProvider? time = null) : base(baseUrl, http, time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);
        _apiKey = apiKey;
        _secret = Encoding.UTF8.GetBytes(apiSecret);
        _passphrase = passphrase;
        Demo = demo;
    }

    public override string Name => "OKX";
    public bool Demo { get; }
    /// <summary>Trade mode: "cash" for spot, "cross"/"isolated" for margin and derivatives.</summary>
    public string TradeMode { get; init; } = "cash";

    /// <summary>OK-ACCESS-SIGN: Base64(HMAC-SHA256(secret, timestamp + method + requestPath + body)).</summary>
    public static string Signature(byte[] secret, string timestamp, string method, string requestPath, string body)
        => HmacBase64(secret, timestamp + method.ToUpperInvariant() + requestPath + body);

    private void Sign(HttpRequestMessage r, string body)
    {
        string ts = Time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        r.Headers.TryAddWithoutValidation("OK-ACCESS-KEY", _apiKey);
        r.Headers.TryAddWithoutValidation("OK-ACCESS-PASSPHRASE", _passphrase);
        r.Headers.TryAddWithoutValidation("OK-ACCESS-TIMESTAMP", ts);
        r.Headers.TryAddWithoutValidation("OK-ACCESS-SIGN", Signature(_secret, ts, r.Method.Method, r.RequestUri!.PathAndQuery, body));
        if (Demo) r.Headers.TryAddWithoutValidation("x-simulated-trading", "1");
    }

    private static JsonNode Data(JsonNode r)
    {
        if (r["code"]?.GetValue<string>() != "0")
        {
            var first = r["data"] is JsonArray a && a.Count > 0 ? a[0] : null;
            throw new BrokerException($"OKX {r["code"]}: {first?["sMsg"] ?? r["msg"]}");
        }
        return r["data"]!;
    }

    /// <summary>True when the order lives on OKX as an algo order (its venue id is an algoId).</summary>
    public bool IsAlgoOrder(long orderId) => _algoTriggeredOrdIds.ContainsKey(orderId);

    /// <summary>
    /// Places a standalone take-profit / stop-loss algo for <paramref name="closingOrder"/> (side and size of the
    /// closing trade; a limit price, if any, becomes the leg order price, otherwise -1 = market): <c>conditional</c>
    /// with one leg, <c>oco</c> with both <see cref="Order.TakeProfitPrice"/> and <see cref="Order.StopLossPrice"/>.
    /// </summary>
    public Task<Order> SubmitTpSlAsync(Order closingOrder, CancellationToken cancellationToken = default)
    {
        if (closingOrder.TakeProfitPrice is null && closingOrder.StopLossPrice is null)
            throw new ArgumentException("A TP/SL algo needs TakeProfitPrice and/or StopLossPrice.", nameof(closingOrder));
        if (closingOrder.IsConditional) throw new ArgumentException("The closing order of a TP/SL algo must be market or limit.", nameof(closingOrder));
        _tpslOnly[closingOrder.Id] = 0;
        return SubmitAsync(closingOrder, cancellationToken);
    }

    /// <summary>OKX's name for a trigger reference: last, index or mark.</summary>
    public static string TriggerPxTypeName(TriggerPriceType t) => t switch
    {
        TriggerPriceType.Mark => "mark",
        TriggerPriceType.Index => "index",
        _ => "last",
    };

    private static JsonObject? AttachedTpSl(Order order)
    {
        if (order.TakeProfitPrice is null && order.StopLossPrice is null) return null;
        var a = new JsonObject { ["attachAlgoClOrdId"] = $"fnet{order.Id}tpsl" };
        AddTpSlLegs(a, order, legPx: "-1");
        return a;
    }

    private static void AddTpSlLegs(JsonObject o, Order order, string legPx)
    {
        if (order.TakeProfitPrice is { } tp)
        {
            o["tpTriggerPx"] = F(tp);
            o["tpOrdPx"] = legPx;
            if (order.TriggerBy is { } t) o["tpTriggerPxType"] = TriggerPxTypeName(t);
        }
        if (order.StopLossPrice is { } sl)
        {
            o["slTriggerPx"] = F(sl);
            o["slOrdPx"] = legPx;
            if (order.TriggerBy is { } t) o["slTriggerPxType"] = TriggerPxTypeName(t);
        }
    }

    /// <summary>The JSON body of <c>POST /api/v5/trade/order-algo</c> (<c>ordType</c> = <c>trigger</c>) for a stop / stop-limit order.</summary>
    public JsonObject AlgoBody(Order order)
    {
        if (!order.IsConditional) throw new BrokerException("OKX trigger algo orders need a Stop or StopLimit order.");
        var o = new JsonObject
        {
            ["instId"] = InstId(order.Symbol),
            ["tdMode"] = TradeMode,
            ["side"] = order.Side == Side.Buy ? "buy" : "sell",
            ["ordType"] = "trigger",
            ["sz"] = F(order.Quantity),
            ["algoClOrdId"] = $"fnet{order.Id}",
            ["triggerPx"] = F(order.TriggerPrice!.Value),
            ["orderPx"] = order.Type == OrderType.StopLimit ? F(order.LimitPrice!.Value) : "-1",
        };
        if (order.TriggerBy is { } t) o["triggerPxType"] = TriggerPxTypeName(t);
        if (order.Type == OrderType.Stop && TradeMode == "cash") o["tgtCcy"] = "base_ccy";
        if (AttachedTpSl(order) is { } a) o["attachAlgoOrds"] = new JsonArray(a);
        return o;
    }

    /// <summary>The JSON body of <c>POST /api/v5/trade/order-algo</c> for a standalone TP/SL: <c>conditional</c> or <c>oco</c>.</summary>
    public JsonObject TpSlBody(Order closingOrder)
    {
        bool both = closingOrder.TakeProfitPrice is not null && closingOrder.StopLossPrice is not null;
        var o = new JsonObject
        {
            ["instId"] = InstId(closingOrder.Symbol),
            ["tdMode"] = TradeMode,
            ["side"] = closingOrder.Side == Side.Buy ? "buy" : "sell",
            ["ordType"] = both ? "oco" : "conditional",
            ["sz"] = F(closingOrder.Quantity),
            ["algoClOrdId"] = $"fnet{closingOrder.Id}",
        };
        AddTpSlLegs(o, closingOrder, closingOrder.LimitPrice is { } lp ? F(lp) : "-1");
        return o;
    }

    /// <summary>The JSON body of <c>POST /api/v5/trade/cancel-algos</c>: an array of algoId / instId pairs.</summary>
    public static JsonArray CancelAlgosBody(Order order, string algoId)
        => new(new JsonObject { ["algoId"] = algoId, ["instId"] = InstId(order.Symbol) });

    public JsonObject OrderBody(Order order)
    {
        if (order.Type is OrderType.Stop or OrderType.StopLimit) throw new BrokerException("OKX stop orders are algo orders: use AlgoBody (order-algo).");
        var o = new JsonObject
        {
            ["instId"] = InstId(order.Symbol),
            ["tdMode"] = TradeMode,
            ["side"] = order.Side == Side.Buy ? "buy" : "sell",
            ["ordType"] = order.Type == OrderType.Market ? "market"
                : order.TimeInForce switch { TimeInForce.Ioc => "ioc", TimeInForce.Fok => "fok", _ => "limit" },
            ["sz"] = F(order.Quantity),
            ["clOrdId"] = $"fnet{order.Id}",
        };
        if (order.Type == OrderType.Market && TradeMode == "cash") o["tgtCcy"] = "base_ccy";      // size in base currency
        if (order.LimitPrice is { } lp) o["px"] = F(lp);
        if (AttachedTpSl(order) is { } a) o["attachAlgoOrds"] = new JsonArray(a);
        return o;
    }

    public static string InstId(string symbol)
    {
        var s = symbol.Replace("/", "-").Replace("_", "-").ToUpperInvariant();
        if (s.Contains('-')) return s;
        foreach (var q in new[] { "USDT", "USDC", "USD", "BTC", "ETH" })
            if (s.EndsWith(q, StringComparison.Ordinal) && s.Length > q.Length) return s[..^q.Length] + "-" + q;
        return s;
    }

    public static VenueOrderState ParseOrder(JsonNode o)
    {
        string state = o["state"]?.GetValue<string>() ?? "";
        return new VenueOrderState(state, N(o["accFillSz"]), N(o["avgPx"]), state is "canceled" or "mmp_canceled", false);
    }

    /// <summary>
    /// Reads an algo order from <c>GET /api/v5/trade/order-algo</c>: its state (live, effective, canceled,
    /// order_failed, ...) and, once triggered, the id of the order it placed (<c>ordIdList</c>, or legacy <c>ordId</c>).
    /// </summary>
    public static (VenueOrderState State, string? TriggeredOrdId) ParseAlgoOrder(JsonNode o)
    {
        string state = o["state"]?.GetValue<string>() ?? "";
        string? ordId = o["ordIdList"] is JsonArray ids && ids.Count > 0 ? ids[0]?.GetValue<string>() : o["ordId"]?.GetValue<string>();
        if (string.IsNullOrEmpty(ordId)) ordId = null;
        return (new VenueOrderState(state, 0, 0, state is "canceled" or "order_failed", state == "order_failed"), ordId);
    }

    protected override async Task<(string, VenueOrderState?)> PlaceAsync(Order order, CancellationToken ct)
    {
        bool tpsl = _tpslOnly.TryRemove(order.Id, out _);
        if (tpsl || order.IsConditional)
        {
            var body = tpsl ? TpSlBody(order) : AlgoBody(order);
            var a = Data(await SendAsync(HttpMethod.Post, "/api/v5/trade/order-algo", body.ToJsonString(), Sign, ct).ConfigureAwait(false))[0]!;
            if (a["sCode"]?.GetValue<string>() is { } c && c != "0") throw new BrokerException($"OKX {c}: {a["sMsg"]}");
            _algoTriggeredOrdIds[order.Id] = "";
            return (a["algoId"]!.GetValue<string>(), null);
        }
        var d = Data(await SendAsync(HttpMethod.Post, "/api/v5/trade/order", OrderBody(order).ToJsonString(), Sign, ct).ConfigureAwait(false))[0]!;
        if (d["sCode"]?.GetValue<string>() is { } code && code != "0") throw new BrokerException($"OKX {code}: {d["sMsg"]}");
        return (d["ordId"]!.GetValue<string>(), null);
    }

    protected override async Task CancelAtVenueAsync(Order order, string venueId, CancellationToken ct)
    {
        if (_algoTriggeredOrdIds.TryGetValue(order.Id, out var triggered))
        {
            if (triggered.Length == 0)
            {
                Data(await SendAsync(HttpMethod.Post, "/api/v5/trade/cancel-algos", CancelAlgosBody(order, venueId).ToJsonString(), Sign, ct).ConfigureAwait(false));
                return;
            }
            venueId = triggered;            // already triggered: cancel the order it placed
        }
        var body = new JsonObject { ["instId"] = InstId(order.Symbol), ["ordId"] = venueId };
        Data(await SendAsync(HttpMethod.Post, "/api/v5/trade/cancel-order", body.ToJsonString(), Sign, ct).ConfigureAwait(false));
    }

    protected override async Task<VenueOrderState> QueryAsync(Order order, string venueId, CancellationToken ct)
    {
        if (_algoTriggeredOrdIds.TryGetValue(order.Id, out var triggered))
        {
            if (triggered.Length == 0)
            {
                var (state, ordId) = ParseAlgoOrder(Data(await SendAsync(HttpMethod.Get, $"/api/v5/trade/order-algo?algoId={venueId}", null, Sign, ct).ConfigureAwait(false))[0]!);
                if (ordId is null) return state;
                _algoTriggeredOrdIds[order.Id] = triggered = ordId;
            }
            venueId = triggered;
        }
        return ParseOrder(Data(await SendAsync(HttpMethod.Get, $"/api/v5/trade/order?instId={InstId(order.Symbol)}&ordId={venueId}", null, Sign, ct).ConfigureAwait(false))[0]!);
    }

    public override async Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        var b = Data(await SendAsync(HttpMethod.Get, "/api/v5/account/balance", null, Sign, cancellationToken).ConfigureAwait(false))[0]!;
        return b["details"]!.AsArray().Where(d => N(d!["cashBal"]) != 0)
            .Select(d => MakePosition(d!["ccy"]!.GetValue<string>(), N(d["cashBal"]), N(d["openAvgPx"]))).ToList();
    }

    public override async Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        var b = Data(await SendAsync(HttpMethod.Get, "/api/v5/account/balance", null, Sign, cancellationToken).ConfigureAwait(false))[0]!;
        double usdt = b["details"]!.AsArray().Where(d => d!["ccy"]?.GetValue<string>() == "USDT").Sum(d => N(d!["cashBal"]));
        return new AccountInfo("OKX", "USD", usdt, N(b["totalEq"]), N(b["adjEq"]) is var adj && adj > 0 ? adj : N(b["totalEq"]));
    }
}
