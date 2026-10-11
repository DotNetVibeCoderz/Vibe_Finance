using System.Text;

namespace Finance.Execution.Fix;

/// <summary>
/// Persists what a FIX session must survive a restart with: the next outgoing and expected sequence numbers, and
/// every sent application message (so a counterparty's ResendRequest can be answered after reconnecting).
/// </summary>
public interface IFixMessageStore
{
    int NextSenderSeqNum { get; set; }
    int NextTargetSeqNum { get; set; }

    /// <summary>Stores the encoded message sent with <paramref name="seqNum"/>.</summary>
    void Save(int seqNum, string encodedMessage);

    /// <summary>Stored messages with sequence numbers in [begin, end], in order.</summary>
    IReadOnlyList<(int SeqNum, string Message)> Get(int begin, int end);

    /// <summary>Starts a new sequence (both numbers back to 1, stored messages discarded).</summary>
    void Reset();
}

/// <summary>Volatile store: state lives as long as the process.</summary>
public sealed class MemoryFixMessageStore : IFixMessageStore
{
    private readonly SortedDictionary<int, string> _messages = new();
    private readonly Lock _gate = new();

    public int NextSenderSeqNum { get; set; } = 1;
    public int NextTargetSeqNum { get; set; } = 1;

    public void Save(int seqNum, string encodedMessage)
    {
        lock (_gate) _messages[seqNum] = encodedMessage;
    }

    public IReadOnlyList<(int SeqNum, string Message)> Get(int begin, int end)
    {
        lock (_gate) return _messages.Where(p => p.Key >= begin && p.Key <= end).Select(p => (p.Key, p.Value)).ToList();
    }

    public void Reset()
    {
        lock (_gate) _messages.Clear();
        NextSenderSeqNum = NextTargetSeqNum = 1;
    }
}

/// <summary>
/// File-backed store, one pair of files per session in <c>directory</c>: <c>{session}.seqnums</c> (rewritten
/// atomically through a temporary file on every change) and <c>{session}.messages</c> (append-only, one
/// <c>seq TAB base64</c> line per sent message, flushed before the message goes on the wire). A crash can
/// therefore lose nothing that the counterparty could ask for.
/// </summary>
public sealed class FileFixMessageStore : IFixMessageStore
{
    private readonly string _seqPath, _msgPath;
    private readonly Dictionary<int, string> _messages = new();
    private readonly Lock _gate = new();
    private int _sender = 1, _target = 1;

    /// <param name="directory">Folder for the store files (created if missing).</param>
    /// <param name="sessionId">Unique name per session, e.g. "CLIENT-BROKER".</param>
    public FileFixMessageStore(string directory, string sessionId)
    {
        Directory.CreateDirectory(directory);
        _seqPath = Path.Combine(directory, sessionId + ".seqnums");
        _msgPath = Path.Combine(directory, sessionId + ".messages");
        if (File.Exists(_seqPath))
        {
            var parts = File.ReadAllText(_seqPath).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var s) && int.TryParse(parts[1], out var t)) { _sender = s; _target = t; }
        }
        if (File.Exists(_msgPath))
            foreach (var line in File.ReadLines(_msgPath))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0 && int.TryParse(line.AsSpan(0, tab), out var seq))
                    _messages[seq] = Encoding.Latin1.GetString(Convert.FromBase64String(line[(tab + 1)..]));
            }
    }

    public int NextSenderSeqNum
    {
        get { lock (_gate) return _sender; }
        set { lock (_gate) { _sender = value; WriteSeqNums(); } }
    }

    public int NextTargetSeqNum
    {
        get { lock (_gate) return _target; }
        set { lock (_gate) { _target = value; WriteSeqNums(); } }
    }

    public void Save(int seqNum, string encodedMessage)
    {
        lock (_gate)
        {
            _messages[seqNum] = encodedMessage;
            using var fs = new FileStream(_msgPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            var line = Encoding.ASCII.GetBytes($"{seqNum}\t{Convert.ToBase64String(Encoding.Latin1.GetBytes(encodedMessage))}\n");
            fs.Write(line);
            fs.Flush(flushToDisk: true);
        }
    }

    public IReadOnlyList<(int SeqNum, string Message)> Get(int begin, int end)
    {
        lock (_gate) return _messages.Where(p => p.Key >= begin && p.Key <= end).OrderBy(p => p.Key).Select(p => (p.Key, p.Value)).ToList();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _messages.Clear();
            if (File.Exists(_msgPath)) File.Delete(_msgPath);
            _sender = _target = 1;
            WriteSeqNums();
        }
    }

    private void WriteSeqNums()
    {
        var tmp = _seqPath + ".tmp";
        File.WriteAllText(tmp, $"{_sender} {_target}");
        File.Move(tmp, _seqPath, overwrite: true);
    }
}
