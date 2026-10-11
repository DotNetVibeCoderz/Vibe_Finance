using System.Globalization;
using System.Text;

namespace Finance.Execution.Fix;

/// <summary>Common FIX 4.4 tag numbers.</summary>
public static class FixTags
{
    public const int Account = 1, AvgPx = 6, BeginSeqNo = 7, BeginString = 8, BodyLength = 9, CheckSum = 10, ClOrdID = 11,
        CumQty = 14, EndSeqNo = 16, ExecID = 17, MsgSeqNum = 34, MsgType = 35, NewSeqNo = 36, OrderID = 37, OrderQty = 38,
        OrdStatus = 39, OrdType = 40, OrigClOrdID = 41, PossDupFlag = 43, Price = 44, RefSeqNum = 45, SenderCompID = 49,
        SendingTime = 52, Side = 54, Symbol = 55, TargetCompID = 56, Text = 58, TimeInForce = 59, TransactTime = 60,
        LastPx = 31, LastQty = 32, EncryptMethod = 98, StopPx = 99, HeartBtInt = 108, TestReqID = 112, OrigSendingTime = 122,
        GapFillFlag = 123, ResetSeqNumFlag = 141, ExecType = 150, LeavesQty = 151, NoMDEntries = 268, MDEntryType = 269,
        MDEntryPx = 270, MDEntrySize = 271, ApplVerID = 1128, DefaultApplVerID = 1137;
}

/// <summary>Session protocol versions and FIX 5.0+ application versions.</summary>
public static class FixVersions
{
    /// <summary>BeginString of FIX 4.4 sessions.</summary>
    public const string Fix44 = "FIX.4.4";

    /// <summary>BeginString of the FIX 5.0+ transport session protocol.</summary>
    public const string Fixt11 = "FIXT.1.1";

    /// <summary>ApplVerID values (tag 1128/1137).</summary>
    public const string ApplFix44 = "6", ApplFix50 = "7", ApplFix50Sp1 = "8", ApplFix50Sp2 = "9";
}

/// <summary>FIX 4.4 message types used by the session and order flow.</summary>
public static class FixMsgType
{
    public const string Heartbeat = "0", TestRequest = "1", ResendRequest = "2", Reject = "3", SequenceReset = "4", Logout = "5",
        Logon = "A", NewOrderSingle = "D", OrderCancelRequest = "F", OrderCancelReplaceRequest = "G", ExecutionReport = "8",
        OrderCancelReject = "9", MarketDataSnapshot = "W";

    /// <summary>Session-level (administrative) messages.</summary>
    public static bool IsAdmin(string type) => type is Heartbeat or TestRequest or ResendRequest or Reject or SequenceReset or Logout or Logon;
}

/// <summary>Raised when a FIX message cannot be parsed or fails validation.</summary>
public sealed class FixParseException : Exception
{
    public FixParseException(string message) : base(message) { }
}

/// <summary>
/// A FIX message as an ordered list of tag=value fields. Field order is preserved, which is what
/// repeating groups depend on. <see cref="Encode"/> computes BodyLength (9) and CheckSum (10).
/// </summary>
public sealed class FixMessage
{
    public const char Soh = '\x01';
    private static readonly Encoding Latin1 = Encoding.Latin1;
    private readonly List<KeyValuePair<int, string>> _fields = new();

    public FixMessage() { }

    public FixMessage(string msgType, string beginString = "FIX.4.4")
    {
        Set(FixTags.BeginString, beginString);
        Set(FixTags.MsgType, msgType);
    }

    public IReadOnlyList<KeyValuePair<int, string>> Fields => _fields;

    public string MsgType => Get(FixTags.MsgType) ?? "";

    public int MsgSeqNum => GetInt(FixTags.MsgSeqNum) ?? 0;

    public bool IsAdmin => FixMsgType.IsAdmin(MsgType);

    /// <summary>First value of a tag, or null.</summary>
    public string? Get(int tag)
    {
        foreach (var f in _fields) if (f.Key == tag) return f.Value;
        return null;
    }

    public string this[int tag] => Get(tag) ?? throw new KeyNotFoundException($"Tag {tag} not present.");

    public bool Has(int tag) => Get(tag) is not null;

    public int? GetInt(int tag) => int.TryParse(Get(tag), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public double? GetDouble(int tag) => double.TryParse(Get(tag), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public bool GetBool(int tag) => Get(tag) == "Y";

    /// <summary>Replaces the first occurrence of a tag (or appends it).</summary>
    public FixMessage Set(int tag, string value)
    {
        if (value.Contains(Soh)) throw new ArgumentException("Values may not contain SOH.");
        for (int i = 0; i < _fields.Count; i++)
        {
            if (_fields[i].Key == tag)
            {
                _fields[i] = new(tag, value);
                return this;
            }
        }
        _fields.Add(new(tag, value));
        return this;
    }

    public FixMessage Set(int tag, int value) => Set(tag, value.ToString(CultureInfo.InvariantCulture));

    public FixMessage Set(int tag, double value) => Set(tag, value.ToString("0.########", CultureInfo.InvariantCulture));

    public FixMessage Set(int tag, bool value) => Set(tag, value ? "Y" : "N");

    public FixMessage Set(int tag, DateTime utc) => Set(tag, FormatTime(utc));

    /// <summary>Appends a field even if the tag already exists (needed inside repeating groups).</summary>
    public FixMessage Add(int tag, string value)
    {
        if (value.Contains(Soh)) throw new ArgumentException("Values may not contain SOH.");
        _fields.Add(new(tag, value));
        return this;
    }

    public FixMessage Remove(int tag)
    {
        _fields.RemoveAll(f => f.Key == tag);
        return this;
    }

    /// <summary>Appends a repeating group: the count tag followed by each entry's fields in order.</summary>
    public FixMessage AddGroup(int countTag, IReadOnlyList<IReadOnlyList<KeyValuePair<int, string>>> entries)
    {
        Add(countTag, entries.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var e in entries)
            foreach (var f in e) Add(f.Key, f.Value);
        return this;
    }

    /// <summary>
    /// Reads a repeating group. <paramref name="memberTags"/> lists the tags that belong to an entry;
    /// the first one is the delimiter that starts every entry. Reading stops at the first tag that is
    /// not a member or once the declared count is reached.
    /// </summary>
    public IReadOnlyList<Dictionary<int, string>> GetGroup(int countTag, params int[] memberTags)
    {
        if (memberTags.Length == 0) throw new ArgumentException("At least the delimiter tag is required.");
        var result = new List<Dictionary<int, string>>();
        int idx = _fields.FindIndex(f => f.Key == countTag);
        if (idx < 0) return result;
        int count = int.Parse(_fields[idx].Value, CultureInfo.InvariantCulture);
        var members = new HashSet<int>(memberTags);
        Dictionary<int, string>? current = null;
        for (int i = idx + 1; i < _fields.Count; i++)
        {
            var f = _fields[i];
            if (!members.Contains(f.Key)) break;
            if (f.Key == memberTags[0])
            {
                if (result.Count == count) break;
                current = new Dictionary<int, string>();
                result.Add(current);
            }
            if (current is null) throw new FixParseException($"Group {countTag} does not start with delimiter {memberTags[0]}.");
            current[f.Key] = f.Value;
        }
        if (result.Count != count) throw new FixParseException($"Group {countTag} declares {count} entries but has {result.Count}.");
        return result;
    }

    /// <summary>
    /// Serialises with header order 8, 9, 35 first, the body in insertion order and a freshly
    /// computed CheckSum last.
    /// </summary>
    public byte[] Encode() => Latin1.GetBytes(EncodeString());

    public string EncodeString()
    {
        var begin = Get(FixTags.BeginString) ?? "FIX.4.4";
        var type = Get(FixTags.MsgType) ?? throw new InvalidOperationException("MsgType (35) is required.");
        var body = new StringBuilder();
        body.Append("35=").Append(type).Append(Soh);
        foreach (var f in _fields)
        {
            if (f.Key is FixTags.BeginString or FixTags.BodyLength or FixTags.MsgType or FixTags.CheckSum) continue;
            body.Append(f.Key.ToString(CultureInfo.InvariantCulture)).Append('=').Append(f.Value).Append(Soh);
        }
        var bodyText = body.ToString();
        var head = $"8={begin}{Soh}9={Latin1.GetByteCount(bodyText)}{Soh}";
        var withoutTrailer = head + bodyText;
        return withoutTrailer + $"10={Checksum(Latin1.GetBytes(withoutTrailer)):D3}{Soh}";
    }

    /// <summary>Sum of bytes mod 256.</summary>
    public static int Checksum(ReadOnlySpan<byte> bytes)
    {
        int s = 0;
        foreach (var b in bytes) s += b;
        return s & 0xFF;
    }

    /// <summary>Parses and validates (BodyLength and CheckSum) one complete message.</summary>
    public static FixMessage Parse(ReadOnlySpan<byte> raw) => Parse(Latin1.GetString(raw));

    /// <summary>Parses a message; '|' is accepted in place of SOH for readability in tests and logs.</summary>
    public static FixMessage Parse(string raw, bool validate = true)
    {
        if (!raw.Contains(Soh) && raw.Contains('|')) raw = raw.Replace('|', Soh);
        if (!raw.StartsWith("8=", StringComparison.Ordinal)) throw new FixParseException("Message must start with 8=.");
        var msg = new FixMessage();
        int pos = 0;
        while (pos < raw.Length)
        {
            int eq = raw.IndexOf('=', pos);
            int end = raw.IndexOf(Soh, pos);
            if (eq < 0 || end < 0 || eq > end) throw new FixParseException($"Malformed field at offset {pos}.");
            if (!int.TryParse(raw.AsSpan(pos, eq - pos), NumberStyles.None, CultureInfo.InvariantCulture, out int tag))
                throw new FixParseException($"Invalid tag at offset {pos}.");
            msg._fields.Add(new(tag, raw.Substring(eq + 1, end - eq - 1)));
            pos = end + 1;
        }
        if (msg._fields.Count < 4 || msg._fields[1].Key != FixTags.BodyLength || msg._fields[2].Key != FixTags.MsgType)
            throw new FixParseException("Header must be 8, 9, 35.");
        if (msg._fields[^1].Key != FixTags.CheckSum) throw new FixParseException("Message must end with 10=.");
        if (validate)
        {
            int bodyStart = raw.IndexOf(Soh) + 1;
            bodyStart = raw.IndexOf(Soh, bodyStart) + 1;
            int trailer = raw.LastIndexOf(Soh + "10=", StringComparison.Ordinal) + 1;
            int declared = int.Parse(msg._fields[1].Value, CultureInfo.InvariantCulture);
            int actual = Latin1.GetByteCount(raw.AsSpan(bodyStart, trailer - bodyStart));
            if (declared != actual) throw new FixParseException($"BodyLength {declared} != actual {actual}.");
            int sum = Checksum(Latin1.GetBytes(raw[..trailer]));
            if (int.Parse(msg._fields[^1].Value, CultureInfo.InvariantCulture) != sum) throw new FixParseException($"CheckSum mismatch (expected {sum:D3}).");
        }
        return msg;
    }

    public static string FormatTime(DateTime utc) => utc.ToUniversalTime().ToString("yyyyMMdd-HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public static DateTime ParseTime(string s) => DateTime.ParseExact(s, new[] { "yyyyMMdd-HH:mm:ss.fff", "yyyyMMdd-HH:mm:ss" },
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public FixMessage Clone()
    {
        var m = new FixMessage();
        m._fields.AddRange(_fields);
        return m;
    }

    /// <summary>Human-readable form with '|' separators.</summary>
    public override string ToString() => string.Join('|', _fields.Select(f => $"{f.Key}={f.Value}"));
}

/// <summary>
/// Splits a byte stream into complete FIX messages using BodyLength, so fragmented or
/// coalesced TCP reads are handled.
/// </summary>
public sealed class FixFramer
{
    private readonly List<byte> _buffer = new();

    /// <summary>Appends received bytes and returns every complete raw message now available.</summary>
    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data.ToArray());
        var result = new List<byte[]>();
        while (TryExtract(out var msg)) result.Add(msg);
        return result;
    }

    private bool TryExtract(out byte[] message)
    {
        message = Array.Empty<byte>();
        int start = IndexOf("8=FIX", 0);
        if (start < 0)
        {
            if (_buffer.Count > 8) _buffer.RemoveRange(0, _buffer.Count - 8);
            return false;
        }
        if (start > 0) _buffer.RemoveRange(0, start);
        int lenTag = IndexOf("\u00019=", 0);
        if (lenTag < 0) return false;
        int lenEnd = _buffer.IndexOf((byte)1, lenTag + 3);
        if (lenEnd < 0) return false;
        var lenText = System.Text.Encoding.ASCII.GetString(_buffer.GetRange(lenTag + 3, lenEnd - lenTag - 3).ToArray());
        if (!int.TryParse(lenText, out int bodyLen))
        {
            _buffer.RemoveRange(0, 2); // skip garbage and resync
            return _buffer.Count > 0 && TryExtract(out message);
        }
        int total = lenEnd + 1 + bodyLen + 7; // "10=xxx" + SOH
        if (_buffer.Count < total) return false;
        message = _buffer.GetRange(0, total).ToArray();
        _buffer.RemoveRange(0, total);
        return true;
    }

    private int IndexOf(string pattern, int from)
    {
        var p = System.Text.Encoding.ASCII.GetBytes(pattern);
        for (int i = from; i <= _buffer.Count - p.Length; i++)
        {
            int k = 0;
            while (k < p.Length && _buffer[i + k] == p[k]) k++;
            if (k == p.Length) return i;
        }
        return -1;
    }
}
