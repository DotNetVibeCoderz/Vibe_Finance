using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Finance.Core.Market;
using Finance.Execution.Oms;

namespace Finance.Execution.Brokers;

/// <summary>
/// Binance spot trading over the signed REST API (HMAC-SHA256 of the query string, key in the
/// <c>X-MBX-APIKEY</c> header). Use <see cref="TestnetUrl"/> for paper trading against the official
/// testnet. Fills reported in the order response are recorded immediately; poll
/// <see cref="RefreshAsync"/> for later status changes of resting orders.
/// </summary>
public sealed class BinanceBroker : IBroker
{
    public const string LiveUrl = "https://api.binance.com";
    public const string TestnetUrl = "https://testnet.binance.vision";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly byte[] _secret;
    private readonly TimeProvider _time;
    private readonly Dictionary<long, (string Symbol, long ExchangeId, string ClientId)> _map = new();

    public BinanceBroker(string apiKey, string apiSecret, string baseUrl = TestnetUrl, HttpClient? http = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiSecret);
        _apiKey = apiKey;
        _secret = Encoding.UTF8.GetBytes(apiSecret);
        BaseUrl = baseUrl.TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _time = time ?? TimeProvider.System;
        Store.OrderEvent += e => OrderUpdated?.Invoke(e);
    }

    public string Name => "Binance";

    public string BaseUrl { get; }

    public OrderStore Store { get; } = new();

    public CommissionModel Commission { get; init; } = CommissionModel.BinanceSpot;

    /// <summary>recvWindow in milliseconds.</summary>
    public int RecvWindow { get; init; } = 5000;

    public event Action<OrderEvent>? OrderUpdated;

    /// <summary>HMAC-SHA256 signature of a query string, lower-case hex (as Binance expects).</summary>
    public static string Sign(string query, byte[] secret) => Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(query)));

    public async Task<Order> SubmitAsync(Order order, CancellationToken cancellationToken = default)
    {
        var symbol = order.Symbol.Replace("-", "").Replace("/", "").ToUpperInvariant();
        var clientId = $"fnet{order.Id}";
        var p = new List<(string, string)>
        {
            ("symbol", symbol),
            ("side", order.Side == Side.Buy ? "BUY" : "SELL"),
            ("newClientOrderId", clientId),
            ("newOrderRespType", "FULL"),
        };
        switch (order.Type)
        {
            case OrderType.Market:
                p.Add(("type", "MARKET"));
                break;
            case OrderType.Limit:
                p.Add(("type", "LIMIT"));
                p.Add(("timeInForce", Tif(order.TimeInForce)));
                p.Add(("price", F(order.LimitPrice!.Value)));
                break;
            case OrderType.Stop:
                p.Add(("type", "STOP_LOSS"));
                p.Add(("stopPrice", F(order.StopPrice!.Value)));
                break;
            case OrderType.StopLimit:
                p.Add(("type", "STOP_LOSS_LIMIT"));
                p.Add(("timeInForce", Tif(order.TimeInForce)));
                p.Add(("price", F(order.LimitPrice!.Value)));
                p.Add(("stopPrice", F(order.StopPrice!.Value)));
                break;
        }
        p.Add(("quantity", F(order.Quantity)));

        Store.Add(order, Now);
        JsonDocument doc;
        try
        {
            doc = await SignedAsync(HttpMethod.Post, "/api/v3/order", p, cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerException ex)
        {
            Store.Reject(order, Now, ex.Message);
            return order;
        }
        using (doc)
        {
            var r = doc.RootElement;
            _map[order.Id] = (symbol, r.GetProperty("orderId").GetInt64(), clientId);
            Store.Accept(order, Now);
            Apply(order, r);
        }
        return order;
    }

    public async Task<bool> CancelAsync(long orderId, CancellationToken cancellationToken = default)
    {
        if (!_map.TryGetValue(orderId, out var ids)) return false;
        var order = Store.Get(orderId);
        if (order is null || !order.IsActive) return false;
        try
        {
            using var doc = await SignedAsync(HttpMethod.Delete, "/api/v3/order",
                new() { ("symbol", ids.Symbol), ("orderId", ids.ExchangeId.ToString(CultureInfo.InvariantCulture)) }, cancellationToken).ConfigureAwait(false);
            return Store.Cancel(orderId, Now, "Cancelled on Binance");
        }
        catch (BrokerException)
        {
            return false;
        }
    }

    /// <summary>Polls the status of every open order and records new fills/cancellations.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        foreach (var order in Store.ActiveOrders.ToList())
        {
            if (!_map.TryGetValue(order.Id, out var ids)) continue;
            using var doc = await SignedAsync(HttpMethod.Get, "/api/v3/order",
                new() { ("symbol", ids.Symbol), ("orderId", ids.ExchangeId.ToString(CultureInfo.InvariantCulture)) }, cancellationToken).ConfigureAwait(false);
            var r = doc.RootElement;
            double executed = Num(r, "executedQty");
            double quote = Num(r, "cummulativeQuoteQty");
            double newQty = executed - order.FilledQuantity;
            if (newQty > 1e-12)
            {
                // Price of the incremental fill from the change in cumulative quote quantity.
                double px = (quote - order.AverageFillPrice * order.FilledQuantity) / newQty;
                Store.RecordFill(new Fill(order.Id, order.Symbol, order.Side, newQty, px, Now, Commission.Compute(newQty, px)));
            }
            var status = r.GetProperty("status").GetString();
            if (status is "CANCELED" or "EXPIRED" or "EXPIRED_IN_MATCH" && order.IsActive) Store.Cancel(order.Id, Now, status);
        }
    }

    public async Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        using var doc = await SignedAsync(HttpMethod.Get, "/api/v3/account", new(), cancellationToken).ConfigureAwait(false);
        return ParseBalances(doc.RootElement);
    }

    public async Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        using var doc = await SignedAsync(HttpMethod.Get, "/api/v3/account", new(), cancellationToken).ConfigureAwait(false);
        double usdt = 0;
        foreach (var b in doc.RootElement.GetProperty("balances").EnumerateArray())
            if (b.GetProperty("asset").GetString() == "USDT") usdt = Num(b, "free") + Num(b, "locked");
        return new AccountInfo("BINANCE", "USDT", usdt, usdt, usdt);
    }

    /// <summary>Non-zero asset balances as positions (asset name as symbol, average price unknown = 0).</summary>
    public static IReadOnlyList<Position> ParseBalances(JsonElement account)
    {
        var list = new List<Position>();
        foreach (var b in account.GetProperty("balances").EnumerateArray())
        {
            double qty = Num(b, "free") + Num(b, "locked");
            if (qty <= 0) continue;
            var p = new Position(b.GetProperty("asset").GetString()!);
            p.Apply(new Fill(0, p.Symbol, Side.Buy, qty, 0, DateTime.UtcNow));
            list.Add(p);
        }
        return list;
    }

    private void Apply(Order order, JsonElement r)
    {
        if (r.TryGetProperty("fills", out var fills))
        {
            foreach (var f in fills.EnumerateArray())
            {
                double q = Num(f, "qty"), px = Num(f, "price"), fee = Num(f, "commission");
                if (q > 0 && order.IsActive) Store.RecordFill(new Fill(order.Id, order.Symbol, order.Side, Math.Min(q, order.RemainingQuantity), px, Now, fee));
            }
        }
        var status = r.TryGetProperty("status", out var s) ? s.GetString() : null;
        if (status is "CANCELED" or "EXPIRED" or "EXPIRED_IN_MATCH" && order.IsActive) Store.Cancel(order.Id, Now, status);
        if (status == "REJECTED" && order.IsActive) Store.Reject(order, Now, "Rejected by Binance");
    }

    private async Task<JsonDocument> SignedAsync(HttpMethod method, string path, List<(string Key, string Value)> parameters, CancellationToken ct)
    {
        parameters.Add(("recvWindow", RecvWindow.ToString(CultureInfo.InvariantCulture)));
        parameters.Add(("timestamp", _time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
        var query = string.Join('&', parameters.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        query += "&signature=" + Sign(query, _secret);
        using var req = new HttpRequestMessage(method, $"{BaseUrl}{path}?{query}");
        req.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _apiKey);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string msg = body;
            try
            {
                using var e = JsonDocument.Parse(body);
                msg = $"{e.RootElement.GetProperty("code")}: {e.RootElement.GetProperty("msg").GetString()}";
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
            throw new BrokerException($"Binance {(int)resp.StatusCode}: {msg}");
        }
        return JsonDocument.Parse(body);
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static string Tif(TimeInForce t) => t switch { TimeInForce.Ioc => "IOC", TimeInForce.Fok => "FOK", _ => "GTC" };

    private static string F(double v) => v.ToString("0.########", CultureInfo.InvariantCulture);

    private static double Num(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && double.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
}
