using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Finance.Core.Market;
using Finance.Execution.Oms;

namespace Finance.Execution.Brokers;

/// <summary>
/// Interactive Brokers through the Client Portal Web API (the local gateway, by default
/// <c>https://localhost:5000/v1/api</c>). The gateway must be running and authenticated in a browser.
/// It uses a self-signed certificate: pass an <see cref="HttpClient"/> built with
/// <see cref="CreateGatewayClient"/> to accept it. Order "reply" prompts (precautionary warnings)
/// are confirmed automatically when <see cref="AutoConfirmWarnings"/> is true.
/// </summary>
public sealed class InteractiveBrokersBroker : IBroker
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, long> _conids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, string> _ibOrderIds = new();
    private string? _accountId;

    public InteractiveBrokersBroker(HttpClient? http = null, string baseUrl = "https://localhost:5000/v1/api", string? accountId = null)
    {
        _http = http ?? CreateGatewayClient();
        BaseUrl = baseUrl.TrimEnd('/');
        _accountId = accountId;
        Store.OrderEvent += e => OrderUpdated?.Invoke(e);
    }

    public string Name => "InteractiveBrokers";

    public string BaseUrl { get; }

    public OrderStore Store { get; } = new();

    public bool AutoConfirmWarnings { get; init; } = true;

    public event Action<OrderEvent>? OrderUpdated;

    /// <summary>An HttpClient that accepts the gateway's self-signed localhost certificate.</summary>
    public static HttpClient CreateGatewayClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (req, _, _, _) => req.RequestUri?.IsLoopback == true,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("FinanceNet/0.1");
        return c;
    }

    /// <summary>The first account of the logged-in user (cached).</summary>
    public async Task<string> GetAccountIdAsync(CancellationToken ct = default)
    {
        if (_accountId is not null) return _accountId;
        var r = await GetAsync("/iserver/accounts", ct).ConfigureAwait(false);
        _accountId = r["accounts"]?.AsArray().FirstOrDefault()?.GetValue<string>() ?? throw new BrokerException("No IB account available (is the gateway authenticated?).");
        return _accountId;
    }

    /// <summary>Resolves a ticker to an IB contract id via secdef search (cached).</summary>
    public async Task<long> ResolveConidAsync(string symbol, CancellationToken ct = default)
    {
        if (_conids.TryGetValue(symbol, out var id)) return id;
        var r = await GetAsync($"/iserver/secdef/search?symbol={Uri.EscapeDataString(symbol)}", ct).ConfigureAwait(false);
        var first = r.AsArray().FirstOrDefault() ?? throw new BrokerException($"IB has no contract for {symbol}.");
        id = long.Parse(first["conid"]!.ToString(), CultureInfo.InvariantCulture);
        _conids[symbol] = id;
        return id;
    }

    /// <summary>Pre-registers a symbol → conid mapping to skip the lookup.</summary>
    public void RegisterConid(string symbol, long conid) => _conids[symbol] = conid;

    public async Task<Order> SubmitAsync(Order order, CancellationToken cancellationToken = default)
    {
        var acct = await GetAccountIdAsync(cancellationToken).ConfigureAwait(false);
        long conid = await ResolveConidAsync(order.Symbol, cancellationToken).ConfigureAwait(false);
        var o = new JsonObject
        {
            ["conid"] = conid,
            ["cOID"] = $"fnet-{order.Id}",
            ["orderType"] = order.Type switch { OrderType.Market => "MKT", OrderType.Limit => "LMT", OrderType.Stop => "STP", _ => "STOP_LIMIT" },
            ["side"] = order.Side == Side.Buy ? "BUY" : "SELL",
            ["quantity"] = order.Quantity,
            ["tif"] = order.TimeInForce switch { TimeInForce.Gtc => "GTC", TimeInForce.Ioc => "IOC", _ => "DAY" },
        };
        if (order.LimitPrice is { } lp) o["price"] = lp;
        if (order.StopPrice is { } sp)
        {
            if (order.Type == OrderType.Stop) o["price"] = sp;
            else o["auxPrice"] = sp;
        }
        Store.Add(order, DateTime.UtcNow);
        JsonNode reply;
        try
        {
            reply = await PostAsync($"/iserver/account/{acct}/orders", new JsonObject { ["orders"] = new JsonArray(o) }, cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < 5 && IsPrompt(reply); i++)
            {
                if (!AutoConfirmWarnings) throw new BrokerException("IB requires confirmation: " + reply.AsArray()[0]!["message"]);
                var replyId = reply.AsArray()[0]!["id"]!.ToString();
                reply = await PostAsync($"/iserver/reply/{replyId}", new JsonObject { ["confirmed"] = true }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (BrokerException ex)
        {
            Store.Reject(order, DateTime.UtcNow, ex.Message);
            return order;
        }
        var first = reply.AsArray().FirstOrDefault();
        var ibId = first?["order_id"]?.ToString();
        if (ibId is null)
        {
            Store.Reject(order, DateTime.UtcNow, first?["error"]?.ToString() ?? reply.ToJsonString());
            return order;
        }
        _ibOrderIds[order.Id] = ibId;
        Store.Accept(order, DateTime.UtcNow);
        return order;
    }

    public async Task<bool> CancelAsync(long orderId, CancellationToken cancellationToken = default)
    {
        if (!_ibOrderIds.TryGetValue(orderId, out var ibId)) return false;
        var acct = await GetAccountIdAsync(cancellationToken).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/iserver/account/{acct}/order/{ibId}");
        using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return false;
        return Store.Cancel(orderId, DateTime.UtcNow, "Cancelled at IB");
    }

    /// <summary>Polls <c>/iserver/account/orders</c> and records fills and cancellations of tracked orders.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var r = await GetAsync("/iserver/account/orders", cancellationToken).ConfigureAwait(false);
        var byIb = _ibOrderIds.ToDictionary(kv => kv.Value, kv => kv.Key);
        foreach (var o in r["orders"]?.AsArray() ?? new JsonArray())
        {
            var ibId = o?["orderId"]?.ToString();
            if (ibId is null || !byIb.TryGetValue(ibId, out var id)) continue;
            var order = Store.Get(id);
            if (order is null || !order.IsActive) continue;
            double filled = Dbl(o!["filledQuantity"]);
            double avg = Dbl(o["avgPrice"]);
            double inc = filled - order.FilledQuantity;
            if (inc > 1e-9)
            {
                double px = (avg * filled - order.AverageFillPrice * order.FilledQuantity) / inc;
                Store.RecordFill(new Fill(id, order.Symbol, order.Side, inc, px, DateTime.UtcNow));
            }
            var status = o["status"]?.ToString();
            if (status is "Cancelled" or "Inactive" && order.IsActive) Store.Cancel(id, DateTime.UtcNow, status);
        }
    }

    public async Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        var acct = await GetAccountIdAsync(cancellationToken).ConfigureAwait(false);
        var r = await GetAsync($"/portfolio/{acct}/positions/0", cancellationToken).ConfigureAwait(false);
        return ParsePositions(r);
    }

    public async Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        var acct = await GetAccountIdAsync(cancellationToken).ConfigureAwait(false);
        var r = await GetAsync($"/portfolio/{acct}/summary", cancellationToken).ConfigureAwait(false);
        return ParseSummary(acct, r);
    }

    /// <summary>Parses <c>/portfolio/{acct}/positions/0</c>.</summary>
    public static IReadOnlyList<Position> ParsePositions(JsonNode positions)
    {
        var list = new List<Position>();
        foreach (var p in positions.AsArray())
        {
            if (p is null) continue;
            double qty = Dbl(p["position"]);
            if (Math.Abs(qty) < 1e-12) continue;
            var sym = p["ticker"]?.ToString() ?? p["contractDesc"]?.ToString() ?? p["conid"]?.ToString() ?? "?";
            var pos = new Position(sym);
            pos.Apply(new Fill(0, sym, qty > 0 ? Side.Buy : Side.Sell, Math.Abs(qty), Dbl(p["avgCost"]), DateTime.UtcNow));
            list.Add(pos);
        }
        return list;
    }

    /// <summary>Parses <c>/portfolio/{acct}/summary</c>.</summary>
    public static AccountInfo ParseSummary(string accountId, JsonNode summary)
    {
        double Amt(string key) => Dbl(summary[key]?["amount"]);
        var cur = summary["netliquidation"]?["currency"]?.ToString() ?? "USD";
        return new AccountInfo(accountId, cur, Amt("totalcashvalue"), Amt("netliquidation"), Amt("buyingpower"));
    }

    private static bool IsPrompt(JsonNode reply)
        => reply is JsonArray a && a.Count > 0 && a[0]?["id"] is not null && a[0]?["message"] is not null && a[0]?["order_id"] is null;

    private async Task<JsonNode> GetAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(BaseUrl + path, ct).ConfigureAwait(false);
        return await Read(resp, ct).ConfigureAwait(false);
    }

    private async Task<JsonNode> PostAsync(string path, JsonNode body, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(BaseUrl + path, body, ct).ConfigureAwait(false);
        return await Read(resp, ct).ConfigureAwait(false);
    }

    private static async Task<JsonNode> Read(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new BrokerException($"IB gateway {(int)resp.StatusCode}: {text}");
        try
        {
            return JsonNode.Parse(text) ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new BrokerException("IB gateway returned invalid JSON.", ex);
        }
    }

    private static double Dbl(JsonNode? n)
    {
        if (n is null) return 0;
        var s = n.ToString();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
