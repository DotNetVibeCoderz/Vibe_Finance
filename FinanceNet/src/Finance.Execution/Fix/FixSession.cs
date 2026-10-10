using System.Collections.Concurrent;

namespace Finance.Execution.Fix;

/// <summary>Session configuration.</summary>
public sealed record FixSessionSettings(string SenderCompId, string TargetCompId, bool IsInitiator, int HeartBtIntSeconds = 30)
{
    public string BeginString { get; init; } = "FIX.4.4";

    /// <summary>Send 141=Y on logon and restart both sequence numbers at 1.</summary>
    public bool ResetSeqNumOnLogon { get; init; } = true;
}

public enum FixSessionState { Disconnected, LogonSent, LoggedOn, LogoutSent, Closed }

/// <summary>
/// A FIX 4.4 session: logon/logout handshake, sequence numbers, heartbeats and test requests,
/// gap detection with ResendRequest, and replay of stored application messages (PossDupFlag=Y)
/// with SequenceReset-GapFill over administrative messages.
/// </summary>
public sealed class FixSession : IAsyncDisposable
{
    private readonly IFixTransport _transport;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<int, FixMessage> _sent = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource _loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop, _heartbeatLoop;
    private long _lastSentTicks, _lastReceivedTicks;
    private bool _testRequestPending;
    private int _resendRequestedUpTo;
    private int _testReqCounter;

    public FixSession(FixSessionSettings settings, IFixTransport transport, TimeProvider? time = null)
    {
        Settings = settings;
        _transport = transport;
        _time = time ?? TimeProvider.System;
    }

    public FixSessionSettings Settings { get; }

    public FixSessionState State { get; private set; } = FixSessionState.Disconnected;

    /// <summary>Sequence number of the next outgoing message.</summary>
    public int NextOutgoingSeqNum { get; private set; } = 1;

    /// <summary>Sequence number expected on the next incoming message.</summary>
    public int NextExpectedSeqNum { get; private set; } = 1;

    /// <summary>Application (non-session) messages, and session Rejects.</summary>
    public event Action<FixMessage>? MessageReceived;

    public event Action<FixSessionState>? StateChanged;

    /// <summary>Every message sent (true) or received (false), for audit logs.</summary>
    public event Action<FixMessage, bool>? MessageLogged;

    /// <summary>Completes when the logon handshake succeeds.</summary>
    public Task LoggedOn => _loggedOn.Task;

    /// <summary>Completes when the session has ended.</summary>
    public Task Closed => _closed.Task;

    /// <summary>Starts processing; an initiator sends Logon immediately.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _lastReceivedTicks = _lastSentTicks = _time.GetTimestamp();
        _receiveLoop = Task.Run(() => ReceiveLoop(_cts.Token));
        _heartbeatLoop = Task.Run(() => HeartbeatLoop(_cts.Token));
        if (Settings.IsInitiator)
        {
            var logon = new FixMessage(FixMsgType.Logon, Settings.BeginString).Set(FixTags.EncryptMethod, 0).Set(FixTags.HeartBtInt, Settings.HeartBtIntSeconds);
            if (Settings.ResetSeqNumOnLogon)
            {
                NextOutgoingSeqNum = 1;
                logon.Set(FixTags.ResetSeqNumFlag, true);
            }
            await SendRawAsync(logon, cancellationToken).ConfigureAwait(false);
            SetState(FixSessionState.LogonSent);
        }
    }

    /// <summary>Sends an application message (header fields are filled in). It is stored for possible resend.</summary>
    public Task SendAsync(FixMessage message, CancellationToken cancellationToken = default)
    {
        if (State != FixSessionState.LoggedOn) throw new InvalidOperationException($"Session is {State}.");
        return SendRawAsync(message, cancellationToken);
    }

    /// <summary>Initiates a graceful logout and waits (up to <paramref name="timeout"/>) for the peer's confirmation.</summary>
    public async Task LogoutAsync(string? text = null, TimeSpan? timeout = null)
    {
        if (State is FixSessionState.Closed or FixSessionState.Disconnected) return;
        var m = new FixMessage(FixMsgType.Logout, Settings.BeginString);
        if (text is not null) m.Set(FixTags.Text, text);
        await SendRawAsync(m, CancellationToken.None).ConfigureAwait(false);
        SetState(FixSessionState.LogoutSent);
        await Task.WhenAny(_closed.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        Shutdown();
    }

    /// <summary>Sends a TestRequest; the peer must answer with a Heartbeat carrying the same id.</summary>
    public Task SendTestRequestAsync(string? id = null, CancellationToken cancellationToken = default)
    {
        _testRequestPending = true;
        return SendRawAsync(new FixMessage(FixMsgType.TestRequest, Settings.BeginString).Set(FixTags.TestReqID, id ?? $"TEST{Interlocked.Increment(ref _testReqCounter)}"), cancellationToken);
    }

    /// <summary>For tests: skips outgoing sequence numbers so the peer detects a gap.</summary>
    public void SkipOutgoingSequence(int count) => NextOutgoingSeqNum += count;

    private async Task SendRawAsync(FixMessage message, CancellationToken ct, bool possDup = false)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var m = message.Clone();
            m.Set(FixTags.BeginString, Settings.BeginString);
            if (!possDup || !m.Has(FixTags.MsgSeqNum)) m.Set(FixTags.MsgSeqNum, NextOutgoingSeqNum);
            m.Set(FixTags.SenderCompID, Settings.SenderCompId).Set(FixTags.TargetCompID, Settings.TargetCompId);
            var now = _time.GetUtcNow().UtcDateTime;
            if (possDup)
            {
                m.Set(FixTags.OrigSendingTime, m.Get(FixTags.SendingTime) ?? FixMessage.FormatTime(now));
                m.Set(FixTags.PossDupFlag, true);
            }
            m.Set(FixTags.SendingTime, now);
            if (!possDup)
            {
                if (!m.IsAdmin) _sent[NextOutgoingSeqNum] = m;
                NextOutgoingSeqNum++;
            }
            await _transport.SendAsync(m.Encode(), ct).ConfigureAwait(false);
            _lastSentTicks = _time.GetTimestamp();
            MessageLogged?.Invoke(m, true);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _transport.ReceiveAsync(ct).ConfigureAwait(false))
            {
                _lastReceivedTicks = _time.GetTimestamp();
                MessageLogged?.Invoke(msg, false);
                await HandleAsync(msg, ct).ConfigureAwait(false);
                if (State == FixSessionState.Closed) break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Shutdown();
        }
    }

    private async Task HandleAsync(FixMessage msg, CancellationToken ct)
    {
        if (msg.Get(FixTags.SenderCompID) != Settings.TargetCompId || msg.Get(FixTags.TargetCompID) != Settings.SenderCompId)
        {
            await SendRawAsync(new FixMessage(FixMsgType.Logout, Settings.BeginString).Set(FixTags.Text, "CompID problem"), ct).ConfigureAwait(false);
            Shutdown();
            return;
        }

        string type = msg.MsgType;
        int seq = msg.MsgSeqNum;

        if (type == FixMsgType.Logon && msg.GetBool(FixTags.ResetSeqNumFlag)) NextExpectedSeqNum = seq;

        // SequenceReset in Reset mode ignores the sequence number check.
        if (type == FixMsgType.SequenceReset && !msg.GetBool(FixTags.GapFillFlag))
        {
            int newSeq = msg.GetInt(FixTags.NewSeqNo) ?? NextExpectedSeqNum;
            if (newSeq > NextExpectedSeqNum) NextExpectedSeqNum = newSeq;
            return;
        }

        if (seq > NextExpectedSeqNum)
        {
            if (type == FixMsgType.Logon) await OnLogonAsync(msg, ct).ConfigureAwait(false);
            if (type == FixMsgType.Logout)
            {
                await OnLogoutAsync(ct).ConfigureAwait(false);
                return;
            }
            // Only one ResendRequest in flight: it asks for everything up to infinity.
            if (NextExpectedSeqNum > _resendRequestedUpTo)
            {
                _resendRequestedUpTo = seq;
                await SendRawAsync(new FixMessage(FixMsgType.ResendRequest, Settings.BeginString)
                    .Set(FixTags.BeginSeqNo, NextExpectedSeqNum).Set(FixTags.EndSeqNo, 0), ct).ConfigureAwait(false);
            }
            return; // the gap will be replayed; this message comes again
        }

        if (seq < NextExpectedSeqNum)
        {
            if (msg.GetBool(FixTags.PossDupFlag)) return; // duplicate, already processed
            await SendRawAsync(new FixMessage(FixMsgType.Logout, Settings.BeginString)
                .Set(FixTags.Text, $"MsgSeqNum too low, expecting {NextExpectedSeqNum} but received {seq}"), ct).ConfigureAwait(false);
            Shutdown();
            return;
        }

        NextExpectedSeqNum++;
        switch (type)
        {
            case FixMsgType.Logon:
                await OnLogonAsync(msg, ct).ConfigureAwait(false);
                break;
            case FixMsgType.Heartbeat:
                _testRequestPending = false;
                break;
            case FixMsgType.TestRequest:
                await SendRawAsync(new FixMessage(FixMsgType.Heartbeat, Settings.BeginString).Set(FixTags.TestReqID, msg.Get(FixTags.TestReqID) ?? ""), ct).ConfigureAwait(false);
                break;
            case FixMsgType.ResendRequest:
                await ResendAsync(msg.GetInt(FixTags.BeginSeqNo) ?? 1, msg.GetInt(FixTags.EndSeqNo) ?? 0, ct).ConfigureAwait(false);
                break;
            case FixMsgType.SequenceReset:
                int newSeq = msg.GetInt(FixTags.NewSeqNo) ?? NextExpectedSeqNum;
                if (newSeq > NextExpectedSeqNum) NextExpectedSeqNum = newSeq;
                break;
            case FixMsgType.Logout:
                await OnLogoutAsync(ct).ConfigureAwait(false);
                break;
            default:
                MessageReceived?.Invoke(msg);
                break;
        }
    }

    private async Task OnLogonAsync(FixMessage msg, CancellationToken ct)
    {
        if (State == FixSessionState.LoggedOn) return;
        if (!Settings.IsInitiator)
        {
            var reply = new FixMessage(FixMsgType.Logon, Settings.BeginString).Set(FixTags.EncryptMethod, 0)
                .Set(FixTags.HeartBtInt, msg.GetInt(FixTags.HeartBtInt) ?? Settings.HeartBtIntSeconds);
            if (msg.GetBool(FixTags.ResetSeqNumFlag))
            {
                NextOutgoingSeqNum = 1;
                _sent.Clear();
                reply.Set(FixTags.ResetSeqNumFlag, true);
            }
            await SendRawAsync(reply, ct).ConfigureAwait(false);
        }
        SetState(FixSessionState.LoggedOn);
        _loggedOn.TrySetResult();
    }

    private async Task OnLogoutAsync(CancellationToken ct)
    {
        if (State != FixSessionState.LogoutSent)
            await SendRawAsync(new FixMessage(FixMsgType.Logout, Settings.BeginString), ct).ConfigureAwait(false);
        Shutdown();
    }

    private async Task ResendAsync(int begin, int end, CancellationToken ct)
    {
        int last = NextOutgoingSeqNum - 1;
        if (end == 0 || end > last) end = last;
        int? gapStart = null;
        for (int s = begin; s <= end; s++)
        {
            if (_sent.TryGetValue(s, out var stored))
            {
                if (gapStart is { } g)
                {
                    await GapFillAsync(g, s, ct).ConfigureAwait(false);
                    gapStart = null;
                }
                await SendRawAsync(stored, ct, possDup: true).ConfigureAwait(false);
            }
            else gapStart ??= s;
        }
        if (gapStart is { } gs) await GapFillAsync(gs, end + 1, ct).ConfigureAwait(false);
    }

    private Task GapFillAsync(int seq, int newSeq, CancellationToken ct)
    {
        var m = new FixMessage(FixMsgType.SequenceReset, Settings.BeginString)
            .Set(FixTags.MsgSeqNum, seq).Set(FixTags.GapFillFlag, true).Set(FixTags.NewSeqNo, newSeq);
        return SendRawAsync(m, ct, possDup: true);
    }

    private async Task HeartbeatLoop(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Settings.HeartBtIntSeconds);
        var tick = TimeSpan.FromMilliseconds(Math.Clamp(interval.TotalMilliseconds / 4, 50, 1000));
        try
        {
            while (!ct.IsCancellationRequested && State != FixSessionState.Closed)
            {
                await Task.Delay(tick, _time, ct).ConfigureAwait(false);
                if (State != FixSessionState.LoggedOn) continue;
                var sinceSend = _time.GetElapsedTime(_lastSentTicks);
                var sinceRecv = _time.GetElapsedTime(_lastReceivedTicks);
                if (sinceRecv > interval * 2.4 && _testRequestPending)
                {
                    Shutdown(); // peer is gone
                    return;
                }
                if (sinceRecv > interval * 1.2 && !_testRequestPending)
                    await SendTestRequestAsync(null, ct).ConfigureAwait(false);
                else if (sinceSend >= interval)
                    await SendRawAsync(new FixMessage(FixMsgType.Heartbeat, Settings.BeginString), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (State == FixSessionState.Closed) { }
    }

    private void SetState(FixSessionState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    private void Shutdown()
    {
        if (State == FixSessionState.Closed) return;
        SetState(FixSessionState.Closed);
        _transport.Close();
        _cts?.Cancel();
        _closed.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        Shutdown();
        try
        {
            if (_receiveLoop is not null) await _receiveLoop.ConfigureAwait(false);
            if (_heartbeatLoop is not null) await _heartbeatLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        await _transport.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
