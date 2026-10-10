using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Finance.Execution.Fix;

/// <summary>A byte pipe carrying FIX messages. Receiving yields complete, validated messages.</summary>
public interface IFixTransport : IAsyncDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Messages until the connection closes. Messages failing validation are skipped and reported via <see cref="Garbled"/>.</summary>
    IAsyncEnumerable<FixMessage> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised with the reason when an incoming message fails parsing (the message is dropped, as FIX requires).</summary>
    event Action<string>? Garbled;

    void Close();
}

/// <summary>
/// An in-process transport pair for tests and simulations. <see cref="FragmentSize"/> splits writes
/// into small chunks to exercise framing.
/// </summary>
public sealed class InMemoryFixTransport : IFixTransport
{
    private readonly Channel<byte[]> _inbound;
    private readonly Channel<byte[]> _outbound;

    private InMemoryFixTransport(Channel<byte[]> inbound, Channel<byte[]> outbound)
    {
        _inbound = inbound;
        _outbound = outbound;
    }

    /// <summary>When positive, every send is split into chunks of this many bytes.</summary>
    public int FragmentSize { get; set; }

    public event Action<string>? Garbled;

    /// <summary>Two connected endpoints.</summary>
    public static (InMemoryFixTransport A, InMemoryFixTransport B) CreatePair()
    {
        var ab = Channel.CreateUnbounded<byte[]>();
        var ba = Channel.CreateUnbounded<byte[]>();
        return (new InMemoryFixTransport(ba, ab), new InMemoryFixTransport(ab, ba));
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (FragmentSize <= 0)
        {
            await _outbound.Writer.WriteAsync(data.ToArray(), cancellationToken).ConfigureAwait(false);
            return;
        }
        for (int i = 0; i < data.Length; i += FragmentSize)
            await _outbound.Writer.WriteAsync(data.Slice(i, Math.Min(FragmentSize, data.Length - i)).ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<FixMessage> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var framer = new FixFramer();
        while (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_inbound.Reader.TryRead(out var chunk))
            {
                foreach (var raw in framer.Push(chunk))
                {
                    FixMessage? msg = null;
                    try { msg = FixMessage.Parse(raw); }
                    catch (FixParseException ex) { Garbled?.Invoke(ex.Message); }
                    if (msg is not null) yield return msg;
                }
            }
        }
    }

    /// <summary>Injects raw bytes as if the peer had sent them (for testing garbled input).</summary>
    public ValueTask InjectAsync(byte[] raw) => _inbound.Writer.WriteAsync(raw);

    public void Close() => _outbound.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}

/// <summary>FIX over a TCP socket (no TLS; wrap the stream for TLS venues).</summary>
public sealed class TcpFixTransport : IFixTransport
{
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public TcpFixTransport(TcpClient client, Stream? stream = null)
    {
        _client = client;
        _stream = stream ?? client.GetStream();
    }

    public event Action<string>? Garbled;

    /// <summary>Connects to a FIX acceptor.</summary>
    public static async Task<TcpFixTransport> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        return new TcpFixTransport(client);
    }

    /// <summary>Accepts one inbound connection on a listener.</summary>
    public static async Task<TcpFixTransport> AcceptAsync(TcpListener listener, CancellationToken cancellationToken = default)
    {
        var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        client.NoDelay = true;
        return new TcpFixTransport(client);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<FixMessage> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var framer = new FixFramer();
        var buffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                yield break;
            }
            if (n == 0) yield break;
            foreach (var raw in framer.Push(buffer.AsSpan(0, n)))
            {
                FixMessage? msg = null;
                try { msg = FixMessage.Parse(raw); }
                catch (FixParseException ex) { Garbled?.Invoke(ex.Message); }
                if (msg is not null) yield return msg;
            }
        }
    }

    private int _closed;

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        try { _client.Client?.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
        _client.Close();
    }

    public ValueTask DisposeAsync()
    {
        Close();
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
