using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Finance.Execution.Brokers;

/// <summary>A position reported by TWS / IB Gateway.</summary>
public sealed record TwsPosition(string Account, long ConId, string Symbol, string SecType, string Currency, double Quantity, double AverageCost);

/// <summary>One account-summary value (e.g. NetLiquidation).</summary>
public sealed record TwsAccountValue(string Account, string Tag, string Value, string Currency);

/// <summary>An error or notice from TWS (codes 2100-2199 are informational).</summary>
public sealed record TwsError(int Id, int Code, string Message)
{
    public bool IsInformational => Code is >= 2100 and < 2200;
}

/// <summary>Raised when TWS reports an error for a request the client is waiting on.</summary>
public sealed class TwsException(TwsError error) : Exception($"TWS error {error.Code} (id {error.Id}): {error.Message}")
{
    public TwsError Error { get; } = error;
}

/// <summary>
/// Client for the Interactive Brokers TWS / IB Gateway socket API: the binary protocol (4-byte big-endian length
/// prefix, NUL-terminated ASCII fields), the <c>API\0</c> version handshake and <c>startApi</c>, and the
/// stable request families for monitoring an account (server time, next valid order id, managed accounts,
/// positions, account summary) and for orders: placeOrder, cancelOrder, reqOpenOrders, and the orderStatus,
/// openOrder, execDetails, commissionReport and error messages they produce.
/// </summary>
/// <remarks>
/// placeOrder is encoded by <see cref="TwsPlaceOrder"/> in the field order of the TWS API's <c>EClient.placeOrder</c>
/// for the negotiated server version (reference target 176, the top of the announced range; newer fields are
/// guarded by the server version that introduced them). It is tested against a fake TWS asserting the exact
/// field sequence, not against a live TWS. <see cref="TwsBroker"/> maps it onto <see cref="IBroker"/>.
/// Ports: 7497 TWS paper, 7496 TWS live, 4002/4001 IB Gateway.
/// </remarks>
public sealed class TwsClient : IAsyncDisposable
{
    /// <summary>Client protocol version range announced in the handshake.</summary>
    public const int MinClientVersion = 100, MaxClientVersion = 176;

    private const int ReqCurrentTime = 49, ReqIds = 8, ReqPositions = 61, CancelPositions = 64, ReqAccountSummary = 62,
        CancelAccountSummary = 63, ReqOpenOrders = 5, StartApi = 71;
    private const int InOrderStatus = 3, InError = 4, InOpenOrder = 5, InNextValidId = 9, InExecDetails = 11, InManagedAccounts = 15,
        InCurrentTime = 49, InOpenOrderEnd = 53, InCommissionReport = 59, InPosition = 61, InPositionEnd = 62,
        InAccountSummary = 63, InAccountSummaryEnd = 64;

    // Server versions that change the incoming order messages within 100..176.
    private const int MinServerVerMarketCapPrice = 131, MinServerVerLastLiquidity = 136;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<object>> _waiters = new();
    private readonly List<TwsPosition> _positions = new();
    private readonly ConcurrentDictionary<int, List<TwsAccountValue>> _summaries = new();
    private readonly List<TwsOpenOrder> _openOrders = new();
    private readonly object _idGate = new();
    private Task? _reader;
    private int _nextRequestId = 1;
    private long _nextOrderId;

    private TwsClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    /// <summary>Server protocol version agreed in the handshake.</summary>
    public int ServerVersion { get; private set; }

    /// <summary>Connection time string reported by TWS.</summary>
    public string ConnectionTime { get; private set; } = "";

    /// <summary>Accounts this login can trade (from managedAccounts).</summary>
    public IReadOnlyList<string> ManagedAccounts { get; private set; } = Array.Empty<string>();

    /// <summary>Next order id this client will use (from nextValidId, advanced by <see cref="AllocateOrderId"/>).</summary>
    public long NextValidOrderId
    {
        get { lock (_idGate) return _nextOrderId; }
        private set { lock (_idGate) _nextOrderId = Math.Max(_nextOrderId, value); }
    }

    /// <summary>Every error and notice received.</summary>
    public event Action<TwsError>? Error;

    /// <summary>orderStatus messages.</summary>
    public event Action<TwsOrderStatus>? OrderStatus;

    /// <summary>openOrder messages (after placeOrder and in reply to reqOpenOrders).</summary>
    public event Action<TwsOpenOrder>? OpenOrder;

    /// <summary>execDetails messages: one per (partial) execution.</summary>
    public event Action<TwsExecution>? Execution;

    /// <summary>commissionReport messages, which follow each execution.</summary>
    public event Action<TwsCommissionReport>? CommissionReport;

    /// <summary>Connects, performs the version handshake and startApi, and waits for nextValidId.</summary>
    public static async Task<TwsClient> ConnectAsync(string host = "127.0.0.1", int port = 7497, int clientId = 0, CancellationToken cancellationToken = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var c = new TwsClient(tcp);
        try
        {
            // "API\0" then a length-prefixed version range.
            var range = Encoding.ASCII.GetBytes($"v{MinClientVersion}..{MaxClientVersion}");
            var hello = new byte[4 + 4 + range.Length];
            Encoding.ASCII.GetBytes("API\0").CopyTo(hello, 0);
            BinaryPrimitives.WriteInt32BigEndian(hello.AsSpan(4), range.Length);
            range.CopyTo(hello, 8);
            await c._stream.WriteAsync(hello, cancellationToken).ConfigureAwait(false);

            var first = await c.ReadMessageAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new IOException("TWS closed the connection during the handshake.");
            c.ServerVersion = int.Parse(first[0], CultureInfo.InvariantCulture);
            c.ConnectionTime = first.Length > 1 ? first[1] : "";
            if (c.ServerVersion < MinClientVersion) throw new IOException($"TWS server version {c.ServerVersion} is older than {MinClientVersion}.");

            var nextId = c.Wait("nextValidId");
            c._reader = Task.Run(() => c.ReadLoop(c._cts.Token));
            await c.SendAsync(cancellationToken, StartApi, 2, clientId, "").ConfigureAwait(false);
            c.NextValidOrderId = (long)await nextId.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return c;
        }
        catch
        {
            await c.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>TWS server time.</summary>
    public async Task<DateTimeOffset> GetCurrentTimeAsync(CancellationToken cancellationToken = default)
    {
        var w = Wait("currentTime");
        await SendAsync(cancellationToken, ReqCurrentTime, 1).ConfigureAwait(false);
        return DateTimeOffset.FromUnixTimeSeconds((long)await w.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Asks for a fresh next valid order id.</summary>
    public async Task<long> RequestNextOrderIdAsync(CancellationToken cancellationToken = default)
    {
        var w = Wait("nextValidId");
        await SendAsync(cancellationToken, ReqIds, 1, 1).ConfigureAwait(false);
        NextValidOrderId = (long)await w.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        return NextValidOrderId;
    }

    /// <summary>All positions across managed accounts (reqPositions ... positionEnd).</summary>
    public async Task<IReadOnlyList<TwsPosition>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        lock (_positions) _positions.Clear();
        var w = Wait("positionEnd");
        await SendAsync(cancellationToken, ReqPositions, 1).ConfigureAwait(false);
        await w.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await SendAsync(cancellationToken, CancelPositions, 1).ConfigureAwait(false);
        lock (_positions) return _positions.ToList();
    }

    /// <summary>Account summary values (default tags: NetLiquidation, TotalCashValue, BuyingPower) for <paramref name="group"/>.</summary>
    public async Task<IReadOnlyList<TwsAccountValue>> GetAccountSummaryAsync(string tags = "NetLiquidation,TotalCashValue,BuyingPower",
        string group = "All", CancellationToken cancellationToken = default)
    {
        int reqId = Interlocked.Increment(ref _nextRequestId);
        _summaries[reqId] = new List<TwsAccountValue>();
        var w = Wait($"accountSummaryEnd:{reqId}");
        await SendAsync(cancellationToken, ReqAccountSummary, 1, reqId, group, tags).ConfigureAwait(false);
        await w.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await SendAsync(cancellationToken, CancelAccountSummary, 1, reqId).ConfigureAwait(false);
        _summaries.TryRemove(reqId, out var values);
        return values ?? new List<TwsAccountValue>();
    }

    /// <summary>Cancels an open order by its TWS order id (cancelOrder, outgoing id 4).</summary>
    public Task CancelOrderAsync(long orderId, CancellationToken cancellationToken = default)
        => SendAsync(cancellationToken, TwsPlaceOrder.CancelFields(ServerVersion, orderId));

    /// <summary>Takes the next order id (ids must increase per client; TWS rejects reused ids).</summary>
    public long AllocateOrderId()
    {
        lock (_idGate) return _nextOrderId++;
    }

    /// <summary>Places (or, with an existing id, modifies) an order under <paramref name="orderId"/> (placeOrder, outgoing id 3).</summary>
    public Task PlaceOrderAsync(long orderId, TwsContract contract, TwsOrder order, CancellationToken cancellationToken = default)
        => SendAsync(cancellationToken, TwsPlaceOrder.Fields(ServerVersion, orderId, contract, order));

    /// <summary>Places an order under a freshly allocated id and returns that id.</summary>
    public async Task<long> PlaceOrderAsync(TwsContract contract, TwsOrder order, CancellationToken cancellationToken = default)
    {
        var fields = TwsPlaceOrder.Fields(ServerVersion, 0, contract, order);   // validate before using up an id
        long id = AllocateOrderId();
        fields[ServerVersion < TwsPlaceOrder.OrderContainer ? 2 : 1] = id;
        await SendAsync(cancellationToken, fields).ConfigureAwait(false);
        return id;
    }

    /// <summary>Open orders of this client (reqOpenOrders, outgoing id 5, collected until openOrderEnd).</summary>
    public async Task<IReadOnlyList<TwsOpenOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
    {
        lock (_openOrders) _openOrders.Clear();
        var w = Wait("openOrderEnd");
        await SendAsync(cancellationToken, ReqOpenOrders, 1).ConfigureAwait(false);
        await w.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        lock (_openOrders) return _openOrders.ToList();
    }

    // ------------------------------------------------------------------ wire protocol

    /// <summary>Encodes fields as NUL-terminated strings behind a 4-byte big-endian length.</summary>
    public static byte[] EncodeMessage(params object[] fields)
    {
        var sb = new StringBuilder();
        foreach (var f in fields)
        {
            sb.Append(f switch
            {
                double d => d.ToString("R", CultureInfo.InvariantCulture),
                bool b => b ? "1" : "0",
                IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
                _ => f?.ToString() ?? "",
            });
            sb.Append('\0');
        }
        var body = Encoding.ASCII.GetBytes(sb.ToString());
        var msg = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(msg, body.Length);
        body.CopyTo(msg, 4);
        return msg;
    }

    /// <summary>Splits a message body into its fields.</summary>
    public static string[] DecodeFields(ReadOnlySpan<byte> body)
    {
        var s = Encoding.ASCII.GetString(body);
        var parts = s.Split('\0');
        return s.EndsWith('\0') ? parts[..^1] : parts;
    }

    private async Task SendAsync(CancellationToken ct, params object[] fields)
    {
        var bytes = EncodeMessage(fields);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _stream.WriteAsync(bytes, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    private async Task<string[]?> ReadMessageAsync(CancellationToken ct)
    {
        var len = new byte[4];
        if (!await ReadExactAsync(len, ct).ConfigureAwait(false)) return null;
        int n = BinaryPrimitives.ReadInt32BigEndian(len);
        if (n < 0 || n > 16 * 1024 * 1024) throw new IOException($"Implausible TWS message length {n}.");
        var body = new byte[n];
        if (!await ReadExactAsync(body, ct).ConfigureAwait(false)) return null;
        return DecodeFields(body);
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int r = await _stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (r == 0) return false;
            read += r;
        }
        return true;
    }

    private Task<object> Wait(string key) => _waiters.GetOrAdd(key, _ => new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    private void Complete(string key, object value)
    {
        if (_waiters.TryRemove(key, out var tcs)) tcs.TrySetResult(value);
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var f = await ReadMessageAsync(ct).ConfigureAwait(false);
                if (f is null) break;
                Dispatch(f);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (IOException) { }
        finally
        {
            foreach (var w in _waiters.Values) w.TrySetException(new IOException("TWS connection closed."));
        }
    }

    private static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    private static long L(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Reads a message's fields in order (missing trailing fields read as empty).</summary>
    private sealed class FieldReader(string[] fields, int start)
    {
        private int _i = start;
        public string S() => _i < fields.Length ? fields[_i++] : "";
        public long L() => TwsClient.L(S());
        public int I() => (int)L();
        public double D() => TwsClient.D(S());
        public double? Dn() { var s = S(); return s.Length == 0 || s == "1.7976931348623157E308" ? null : TwsClient.D(s); }
        public bool B() { var s = S(); return s is "1" or "true"; }
    }

    // orderStatus: [3, version (< 131),] orderId, status, filled, remaining, avgFillPrice, permId, parentId,
    //              lastFillPrice, clientId, whyHeld[, mktCapPrice (>= 131)]
    private TwsOrderStatus ParseOrderStatus(string[] f)
    {
        var r = new FieldReader(f, ServerVersion < MinServerVerMarketCapPrice ? 2 : 1);
        return new TwsOrderStatus(r.L(), r.S(), r.D(), r.D(), r.D(), r.L(), r.L(), r.D(), r.I(), r.S(),
            ServerVersion >= MinServerVerMarketCapPrice ? r.D() : 0);
    }

    // execDetails: [11, version (< 136),] reqId (v >= 7), orderId, conId, symbol, secType, lastTradeDate, strike, right,
    //              multiplier (v >= 9), exchange, currency, localSymbol, tradingClass (v >= 10), execId, time, acctNumber,
    //              exchange, side, shares, price, permId, clientId, liquidation, cumQty, avgPrice, orderRef (v >= 8), ...
    private TwsExecution ParseExecution(string[] f)
    {
        int version = ServerVersion < MinServerVerLastLiquidity ? (int)L(f[1]) : ServerVersion;
        var r = new FieldReader(f, ServerVersion < MinServerVerLastLiquidity ? 2 : 1);
        int reqId = version >= 7 ? r.I() : -1;
        long orderId = r.L(), conId = r.L();
        string symbol = r.S(), secType = r.S();
        r.S(); r.S(); r.S();                                   // lastTradeDate, strike, right
        if (version >= 9) r.S();                               // multiplier
        r.S();                                                 // exchange (contract)
        string currency = r.S();
        r.S();                                                 // localSymbol
        if (version >= 10) r.S();                              // tradingClass
        string execId = r.S(), time = r.S(), acct = r.S(), exchange = r.S(), side = r.S();
        double shares = r.D(), price = r.D();
        long permId = r.L();
        int clientId = r.I();
        r.S();                                                 // liquidation
        double cum = version >= 6 ? r.D() : shares, avg = version >= 6 ? r.D() : price;
        string orderRef = version >= 8 ? r.S() : "";
        return new TwsExecution(reqId, orderId, conId, symbol, secType, currency, execId, time, acct, exchange, side, shares, price,
            permId, clientId, cum, avg, orderRef);
    }

    // openOrder: [5, version (< 145),] orderId, conId, symbol, secType, lastTradeDate, strike, right, multiplier, exchange,
    //            currency, localSymbol, tradingClass, action, totalQty, orderType, lmtPrice, auxPrice, tif, ocaGroup,
    //            account, openClose, origin, orderRef, clientId, permId, outsideRth, hidden, ... (rest not read)
    private TwsOpenOrder ParseOpenOrder(string[] f)
    {
        var r = new FieldReader(f, ServerVersion < TwsPlaceOrder.OrderContainer ? 2 : 1);
        long orderId = r.L(), conId = r.L();
        string symbol = r.S(), secType = r.S(), lastTrade = r.S();
        r.S(); r.S();                                          // strike, right
        string multiplier = r.S(), exchange = r.S(), currency = r.S(), localSymbol = r.S(), tradingClass = r.S();
        var contract = new TwsContract(symbol, secType, exchange, currency)
        {
            ConId = conId, LastTradeDateOrContractMonth = lastTrade, Multiplier = multiplier, LocalSymbol = localSymbol, TradingClass = tradingClass,
        };
        string action = r.S();
        double qty = r.D();
        string type = r.S();
        double? lmt = r.Dn(), aux = r.Dn();
        string tif = r.S();
        r.S();                                                 // ocaGroup
        string account = r.S();
        r.S(); r.S(); r.S();                                   // openClose, origin, orderRef
        int clientId = r.I();
        long permId = r.L();
        bool outsideRth = r.B();
        return new TwsOpenOrder(orderId, contract, action, qty, type, lmt, aux, tif, account, clientId, permId, outsideRth);
    }

    internal void Dispatch(string[] f)
    {
        if (f.Length == 0) return;
        switch ((int)L(f[0]))
        {
            case InNextValidId:          // 9, version, orderId
                NextValidOrderId = L(f[2]);
                Complete("nextValidId", NextValidOrderId);
                break;
            case InManagedAccounts:      // 15, version, "DU1,DU2"
                ManagedAccounts = f[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                break;
            case InCurrentTime:          // 49, version, epoch seconds
                Complete("currentTime", L(f[2]));
                break;
            case InPosition:             // 61, version, account, conId, symbol, secType, lastTradeDate, strike, right, multiplier,
                                         //     exchange, currency, localSymbol, tradingClass, position, avgCost
                lock (_positions) _positions.Add(new TwsPosition(f[2], L(f[3]), f[4], f[5], f[11], D(f[14]), D(f[15])));
                break;
            case InPositionEnd:
                Complete("positionEnd", true);
                break;
            case InAccountSummary:       // 63, version, reqId, account, tag, value, currency
                if (_summaries.TryGetValue((int)L(f[2]), out var list)) lock (list) list.Add(new TwsAccountValue(f[3], f[4], f[5], f[6]));
                break;
            case InAccountSummaryEnd:    // 64, version, reqId
                Complete($"accountSummaryEnd:{L(f[2])}", true);
                break;
            case InOrderStatus:
                OrderStatus?.Invoke(ParseOrderStatus(f));
                break;
            case InOpenOrder:
                var oo = ParseOpenOrder(f);
                lock (_openOrders) _openOrders.Add(oo);
                OpenOrder?.Invoke(oo);
                break;
            case InOpenOrderEnd:         // 53, version
                Complete("openOrderEnd", true);
                break;
            case InExecDetails:
                Execution?.Invoke(ParseExecution(f));
                break;
            case InCommissionReport:     // 59, version, execId, commission, currency, realizedPNL, yield, yieldRedemptionDate
                CommissionReport?.Invoke(new TwsCommissionReport(f[2], D(f[3]), f.Length > 4 ? f[4] : "", f.Length > 5 ? D(f[5]) : double.NaN));
                break;
            case InError:                // 4, version, id, code, message[, advancedOrderRejectJson (>= 166)]
                var e = new TwsError((int)L(f[2]), (int)L(f[3]), f.Length > 4 ? f[4] : "");
                Error?.Invoke(e);
                if (!e.IsInformational && e.Id > 0 && _summaries.ContainsKey(e.Id) && _waiters.TryRemove($"accountSummaryEnd:{e.Id}", out var w))
                    w.TrySetException(new TwsException(e));
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _tcp.Dispose();
        if (_reader is not null)
            try { await _reader.ConfigureAwait(false); } catch { /* closing */ }
        _cts.Dispose();
    }
}
