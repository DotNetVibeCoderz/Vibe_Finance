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
/// stable request families for monitoring an account: server time, next valid order id, managed accounts,
/// positions, account summary, order cancellation, and error/notice messages.
/// </summary>
/// <remarks>
/// Order placement over the socket is deliberately not implemented: <c>placeOrder</c>'s field list runs to over a
/// hundred version-dependent fields and could not be verified against a live TWS here. Place orders through
/// <see cref="InteractiveBrokersBroker"/> (Client Portal Web API) and use this client for low-latency account
/// and position monitoring alongside it. Ports: 7497 TWS paper, 7496 TWS live, 4002/4001 IB Gateway.
/// </remarks>
public sealed class TwsClient : IAsyncDisposable
{
    /// <summary>Client protocol version range announced in the handshake.</summary>
    public const int MinClientVersion = 100, MaxClientVersion = 176;

    private const int ReqCurrentTime = 49, ReqIds = 8, ReqPositions = 61, CancelPositions = 64, ReqAccountSummary = 62,
        CancelAccountSummary = 63, CancelOrderMsg = 4, StartApi = 71;
    private const int InError = 4, InNextValidId = 9, InManagedAccounts = 15, InCurrentTime = 49, InPosition = 61, InPositionEnd = 62,
        InAccountSummary = 63, InAccountSummaryEnd = 64;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<object>> _waiters = new();
    private readonly List<TwsPosition> _positions = new();
    private readonly ConcurrentDictionary<int, List<TwsAccountValue>> _summaries = new();
    private Task? _reader;
    private int _nextRequestId = 1;

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

    /// <summary>Next valid order id (from nextValidId).</summary>
    public long NextValidOrderId { get; private set; }

    /// <summary>Every error and notice received.</summary>
    public event Action<TwsError>? Error;

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

    /// <summary>Cancels an open order by its TWS order id.</summary>
    public Task CancelOrderAsync(long orderId, CancellationToken cancellationToken = default)
        => SendAsync(cancellationToken, CancelOrderMsg, 1, orderId);

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
            case InError:                // 4, version, id, code, message[, ...]
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
