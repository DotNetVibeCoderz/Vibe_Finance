using System.Collections.Concurrent;
using Finance.Core.Market;
using Finance.Execution.Oms;

namespace Finance.Execution.Brokers;

/// <summary>
/// <see cref="IBroker"/> over the TWS / IB Gateway socket API (<see cref="TwsClient"/>). Orders map to placeOrder:
/// market → MKT, limit → LMT, stop → STP, stop-limit → STP LMT; TIF DAY / GTC / IOC; <see cref="Account"/> and
/// <see cref="OutsideRth"/> apply to every order. Lifecycle comes from TWS's push messages: orderStatus
/// (PreSubmitted / Submitted → accepted, Cancelled / ApiCancelled → cancelled, Inactive → rejected or cancelled),
/// execDetails (one <see cref="Fill"/> per execution, deduplicated by execution id) and error (code 202 → cancelled,
/// other order errors before acknowledgement → rejected).
/// </summary>
/// <remarks>
/// Contracts default to SMART-routed USD stocks; register forex / futures with <see cref="RegisterContract"/>.
/// Attached take-profit / stop-loss and FOK are not mapped (build a bracket with <see cref="TwsOrder.ParentId"/> /
/// <see cref="TwsOrder.Transmit"/> on <see cref="Client"/> instead). Tested against a fake TWS, not a live one.
/// </remarks>
public sealed class TwsBroker : IBroker, IAsyncDisposable
{
    private static readonly HashSet<int> WarningCodes = new() { 161, 399, 404, 10147, 10148 };

    private readonly bool _ownsClient;
    private readonly ConcurrentDictionary<long, long> _byTwsId = new(), _twsIds = new();
    private readonly ConcurrentDictionary<string, long> _execOrders = new();
    private readonly ConcurrentDictionary<long, double> _commissions = new();
    private readonly ConcurrentDictionary<long, byte> _accepted = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _acks = new(), _done = new();
    private readonly ConcurrentDictionary<string, TwsContract> _contracts = new(StringComparer.OrdinalIgnoreCase);

    public TwsBroker(TwsClient client, bool ownsClient = false)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
        Store.OrderEvent += OnStoreEvent;
        Client.OrderStatus += OnOrderStatus;
        Client.Execution += OnExecution;
        Client.CommissionReport += OnCommission;
        Client.Error += OnError;
    }

    /// <summary>Connects a <see cref="TwsClient"/> that the broker owns (and disposes).</summary>
    public static async Task<TwsBroker> ConnectAsync(string host = "127.0.0.1", int port = 7497, int clientId = 0, CancellationToken cancellationToken = default)
        => new(await TwsClient.ConnectAsync(host, port, clientId, cancellationToken).ConfigureAwait(false), ownsClient: true);

    public string Name => "IB-TWS";
    public TwsClient Client { get; }
    public OrderStore Store { get; } = new();

    /// <summary>Account sent with every order (and used to filter positions / account summary); empty = TWS default.</summary>
    public string Account { get; init; } = "";

    /// <summary>Lets orders trigger and fill outside regular trading hours.</summary>
    public bool OutsideRth { get; init; }

    /// <summary>Commission estimate attached to fills; actual commissions arrive later (<see cref="ReportedCommission"/>).</summary>
    public CommissionModel Commission { get; init; } = CommissionModel.Zero;

    /// <summary>How long Submit / Cancel wait for TWS's first answer before returning.</summary>
    public TimeSpan AckTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public event Action<OrderEvent>? OrderUpdated;

    /// <summary>The contract used for <paramref name="symbol"/> (default: SMART-routed USD stock).</summary>
    public TwsContract ContractFor(string symbol) => _contracts.TryGetValue(symbol, out var c) ? c : TwsContract.Stock(symbol);

    /// <summary>Maps a symbol to a contract (forex, futures, a specific exchange or conId).</summary>
    public void RegisterContract(string symbol, TwsContract contract) => _contracts[symbol] = contract;

    /// <summary>The TWS order id of one of our orders.</summary>
    public long? TwsOrderId(long orderId) => _twsIds.TryGetValue(orderId, out var t) ? t : null;

    /// <summary>Sum of commissions TWS reported (commissionReport) for an order's executions.</summary>
    public double ReportedCommission(long orderId) => _commissions.TryGetValue(orderId, out var c) ? c : 0;

    /// <summary>The placeOrder order fields for <paramref name="order"/>.</summary>
    public static TwsOrder ToTwsOrder(Order order, string account = "", bool outsideRth = false)
    {
        if (order.TakeProfitPrice is not null || order.StopLossPrice is not null)
            throw new BrokerException("TWS attached TP/SL is not mapped; build a bracket (ParentId / Transmit) through TwsClient.");
        string action = order.Side == Side.Buy ? "BUY" : "SELL";
        var o = order.Type switch
        {
            OrderType.Market => TwsOrder.Market(action, order.Quantity),
            OrderType.Limit => TwsOrder.Limit(action, order.Quantity, order.LimitPrice!.Value),
            OrderType.Stop => TwsOrder.Stop(action, order.Quantity, order.StopPrice!.Value),
            _ => TwsOrder.StopLimit(action, order.Quantity, order.StopPrice!.Value, order.LimitPrice!.Value),
        };
        return o with
        {
            Tif = order.TimeInForce switch
            {
                TimeInForce.Day => "DAY",
                TimeInForce.Gtc => "GTC",
                TimeInForce.Ioc => "IOC",
                _ => throw new BrokerException($"TWS time in force {order.TimeInForce} is not mapped (DAY, GTC, IOC)."),
            },
            TriggerMethod = order.TriggerBy switch
            {
                null => 0,
                TriggerPriceType.Last => 2,
                _ => throw new BrokerException($"TWS stops cannot trigger on the {order.TriggerBy} price."),
            },
            Account = account,
            OutsideRth = outsideRth,
            OrderRef = order.Tag ?? $"fnet{order.Id}",
        };
    }

    public async Task<Order> SubmitAsync(Order order, CancellationToken cancellationToken = default)
    {
        Store.Add(order, Now);
        try
        {
            var contract = ContractFor(order.Symbol);
            var tws = ToTwsOrder(order, Account, OutsideRth);
            TwsPlaceOrder.Fields(Client.ServerVersion, 0, contract, tws);       // validate before using up an id
            long id = Client.AllocateOrderId();
            var ack = _acks.GetOrAdd(order.Id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _twsIds[order.Id] = id;
            _byTwsId[id] = order.Id;                                            // before sending: replies can arrive at once
            await Client.PlaceOrderAsync(id, contract, tws, cancellationToken).ConfigureAwait(false);
            try { await ack.Task.WaitAsync(AckTimeout, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { /* still New; later messages update it */ }
        }
        catch (Exception ex) when (ex is BrokerException or NotSupportedException or ArgumentException or IOException)
        {
            if (order.IsActive && order.FilledQuantity == 0) Store.Reject(order, Now, ex.Message);
        }
        finally
        {
            _acks.TryRemove(order.Id, out _);
        }
        return order;
    }

    public async Task<bool> CancelAsync(long orderId, CancellationToken cancellationToken = default)
    {
        var order = Store.Get(orderId);
        if (order is null || !order.IsActive || !_twsIds.TryGetValue(orderId, out var twsId)) return false;
        var done = _done.GetOrAdd(orderId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await Client.CancelOrderAsync(twsId, cancellationToken).ConfigureAwait(false);
            if (order.IsActive) await done.Task.WaitAsync(AckTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        catch (IOException) { return false; }
        finally { _done.TryRemove(orderId, out _); }
        return order.Status == OrderStatus.Cancelled;
    }

    public async Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<Position>();
        foreach (var p in await Client.GetPositionsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Account.Length > 0 && p.Account != Account || Math.Abs(p.Quantity) < 1e-12) continue;
            var pos = new Position(p.Symbol);
            pos.Apply(new Fill(0, p.Symbol, p.Quantity > 0 ? Side.Buy : Side.Sell, Math.Abs(p.Quantity), p.AverageCost, Now));
            list.Add(pos);
        }
        return list;
    }

    public async Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        var values = await Client.GetAccountSummaryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        string acct = Account.Length > 0 ? Account : values.FirstOrDefault()?.Account ?? "";
        var mine = values.Where(v => v.Account == acct).ToList();
        double Val(string tag) => mine.FirstOrDefault(v => v.Tag == tag) is { } v && double.TryParse(v.Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        string ccy = mine.FirstOrDefault(v => v.Tag == "NetLiquidation")?.Currency ?? "USD";
        return new AccountInfo(acct, ccy, Val("TotalCashValue"), Val("NetLiquidation"), Val("BuyingPower"));
    }

    private static DateTime Now => DateTime.UtcNow;

    private Order? Tracked(long twsId) => _byTwsId.TryGetValue(twsId, out var id) ? Store.Get(id) : null;

    private void EnsureAccepted(Order order)
    {
        if (order.IsActive && _accepted.TryAdd(order.Id, 0)) Store.Accept(order, Now);
    }

    private void OnStoreEvent(OrderEvent e)
    {
        if (e.Kind is OrderEventKind.Accepted or OrderEventKind.Rejected or OrderEventKind.Cancelled or OrderEventKind.Filled
            && _acks.TryGetValue(e.Order.Id, out var ack)) ack.TrySetResult();
        if (e.Kind is OrderEventKind.Rejected or OrderEventKind.Cancelled or OrderEventKind.Filled
            && _done.TryGetValue(e.Order.Id, out var done)) done.TrySetResult();
        OrderUpdated?.Invoke(e);
    }

    private void OnOrderStatus(TwsOrderStatus s)
    {
        var order = Tracked(s.OrderId);
        if (order is null || !order.IsActive) return;
        switch (s.Status)
        {
            case "PreSubmitted" or "Submitted" or "Filled":
                EnsureAccepted(order);                       // fills themselves come from execDetails
                break;
            case "Cancelled" or "ApiCancelled":
                Store.Cancel(order.Id, Now, $"TWS: {s.Status}");
                break;
            case "Inactive":
                if (order.FilledQuantity == 0) Store.Reject(order, Now, "TWS: Inactive" + (s.WhyHeld.Length > 0 ? $" ({s.WhyHeld})" : ""));
                else Store.Cancel(order.Id, Now, "TWS: Inactive");
                break;
        }
    }

    private void OnExecution(TwsExecution x)
    {
        var order = Tracked(x.OrderId);
        if (order is null) return;
        // Corrections reuse the execId up to its last '.', so the first report of an execution is the one recorded.
        int dot = x.ExecId.LastIndexOf('.');
        string key = dot > 0 ? x.ExecId[..dot] : x.ExecId;
        if (!_execOrders.TryAdd(key, order.Id) || !order.IsActive) return;
        _execOrders.TryAdd(x.ExecId, order.Id);
        double qty = Math.Min(x.Shares, order.RemainingQuantity);
        if (!(qty > 1e-12)) return;
        EnsureAccepted(order);
        Store.RecordFill(new Fill(order.Id, order.Symbol, order.Side, qty, x.Price, Now, Commission.Compute(qty, x.Price)));
    }

    private void OnCommission(TwsCommissionReport c)
    {
        if (_execOrders.TryGetValue(c.ExecId, out var id) && double.IsFinite(c.Commission) && c.Commission < 1e300)
            _commissions.AddOrUpdate(id, c.Commission, (_, v) => v + c.Commission);
    }

    private void OnError(TwsError e)
    {
        if (e.IsInformational || e.Id <= 0) return;
        var order = Tracked(e.Id);
        if (order is null || !order.IsActive) return;
        if (e.Code == 202) Store.Cancel(order.Id, Now, $"TWS 202: {e.Message}");
        else if (!WarningCodes.Contains(e.Code) && !_accepted.ContainsKey(order.Id) && order.FilledQuantity == 0)
            Store.Reject(order, Now, $"TWS {e.Code}: {e.Message}");
    }

    public async ValueTask DisposeAsync()
    {
        Client.OrderStatus -= OnOrderStatus;
        Client.Execution -= OnExecution;
        Client.CommissionReport -= OnCommission;
        Client.Error -= OnError;
        if (_ownsClient) await Client.DisposeAsync().ConfigureAwait(false);
    }
}
