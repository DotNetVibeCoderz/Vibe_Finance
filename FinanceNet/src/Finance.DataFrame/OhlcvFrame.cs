using System.Globalization;
using System.Text;
using Finance.Core;
using Finance.Core.Market;

namespace Finance.DataFrame;

/// <summary>Target period for OHLCV resampling.</summary>
public enum ResamplePeriod { Week, Month, Quarter, Year }

/// <summary>
/// Columnar OHLCV table: one array per field, all of equal length, sorted by time.
/// Columnar storage keeps indicator loops on contiguous <see cref="double"/> spans.
/// </summary>
public sealed class OhlcvFrame
{
    public OhlcvFrame(DateTime[] time, double[] open, double[] high, double[] low, double[] close, double[] volume, string symbol = "")
    {
        int n = time.Length;
        if (open.Length != n || high.Length != n || low.Length != n || close.Length != n || volume.Length != n)
            throw new ArgumentException("All OHLCV columns must have the same length.");
        Time = time;
        Open = open;
        High = high;
        Low = low;
        Close = close;
        Volume = volume;
        Symbol = symbol;
    }

    public string Symbol { get; }
    public DateTime[] Time { get; }
    public double[] Open { get; }
    public double[] High { get; }
    public double[] Low { get; }
    public double[] Close { get; }
    public double[] Volume { get; }

    /// <summary>Number of rows.</summary>
    public int Count => Time.Length;

    /// <summary>Row <paramref name="i"/> as a <see cref="Bar"/>.</summary>
    public Bar this[int i] => new(Time[i], Open[i], High[i], Low[i], Close[i], Volume[i]);

    /// <summary>Builds a frame from bars (copied into columns).</summary>
    public static OhlcvFrame FromBars(ReadOnlySpan<Bar> bars, string symbol = "")
    {
        int n = bars.Length;
        var t = new DateTime[n];
        var o = new double[n];
        var h = new double[n];
        var l = new double[n];
        var c = new double[n];
        var v = new double[n];
        for (int i = 0; i < n; i++)
        {
            var b = bars[i];
            t[i] = b.Time; o[i] = b.Open; h[i] = b.High; l[i] = b.Low; c[i] = b.Close; v[i] = b.Volume;
        }
        return new OhlcvFrame(t, o, h, l, c, v, symbol);
    }

    /// <summary>Converts back to an array of bars.</summary>
    public Bar[] ToBars()
    {
        var bars = new Bar[Count];
        for (int i = 0; i < bars.Length; i++) bars[i] = this[i];
        return bars;
    }

    /// <summary>Rows with <paramref name="from"/> ≤ time ≤ <paramref name="to"/> (binary search; data must be sorted).</summary>
    public OhlcvFrame Slice(DateTime from, DateTime to)
    {
        int start = LowerBound(Time, from);
        int end = LowerBound(Time, to.AddTicks(1));
        return Rows(start, Math.Max(0, end - start));
    }

    /// <summary>Rows [start, start + count).</summary>
    public OhlcvFrame Rows(int start, int count)
    {
        if (start < 0 || count < 0 || start + count > Count) throw new ArgumentOutOfRangeException(nameof(start));
        return new OhlcvFrame(Time[start..(start + count)], Open[start..(start + count)], High[start..(start + count)],
            Low[start..(start + count)], Close[start..(start + count)], Volume[start..(start + count)], Symbol);
    }

    /// <summary>The last <paramref name="count"/> rows.</summary>
    public OhlcvFrame Tail(int count) => Rows(Math.Max(0, Count - count), Math.Min(count, Count));

    /// <summary>
    /// Aggregates into weekly/monthly/quarterly/yearly bars: first open, max high, min low, last close,
    /// summed volume. The bar is stamped with the period's last observed timestamp.
    /// </summary>
    public OhlcvFrame Resample(ResamplePeriod period)
    {
        var t = new List<DateTime>();
        var o = new List<double>();
        var h = new List<double>();
        var l = new List<double>();
        var c = new List<double>();
        var v = new List<double>();
        int i = 0;
        while (i < Count)
        {
            long key = PeriodKey(Time[i], period);
            double open = Open[i], high = High[i], low = Low[i], vol = 0;
            int j = i;
            while (j < Count && PeriodKey(Time[j], period) == key)
            {
                if (High[j] > high) high = High[j];
                if (Low[j] < low) low = Low[j];
                vol += Volume[j];
                j++;
            }
            t.Add(Time[j - 1]); o.Add(open); h.Add(high); l.Add(low); c.Add(Close[j - 1]); v.Add(vol);
            i = j;
        }
        return new OhlcvFrame(t.ToArray(), o.ToArray(), h.ToArray(), l.ToArray(), c.ToArray(), v.ToArray(), Symbol);
    }

    private static long PeriodKey(DateTime d, ResamplePeriod p) => p switch
    {
        // ISO-like week: Monday-based week index since epoch.
        ResamplePeriod.Week => (d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7)).Ticks),
        ResamplePeriod.Month => d.Year * 12L + d.Month,
        ResamplePeriod.Quarter => d.Year * 4L + (d.Month - 1) / 3,
        ResamplePeriod.Year => d.Year,
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    internal static int LowerBound(DateTime[] a, DateTime x)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (a[mid] < x) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// Reads CSV with a header containing Date/Time, Open, High, Low, Close and optionally Volume
    /// (case-insensitive; "Adj Close" is ignored unless no Close column exists). Invariant culture.
    /// </summary>
    public static OhlcvFrame ReadCsv(string path, string symbol = "") => ParseCsv(File.ReadAllText(path), symbol);

    /// <summary>Parses CSV text; see <see cref="ReadCsv"/>.</summary>
    public static OhlcvFrame ParseCsv(string text, string symbol = "")
    {
        using var reader = new StringReader(text);
        var header = reader.ReadLine() ?? throw new FormatException("Empty CSV.");
        var cols = SplitCsv(header).Select(s => s.Trim().Trim('"').ToLowerInvariant()).ToArray();
        int Find(params string[] names)
        {
            foreach (var n in names)
            {
                int k = Array.IndexOf(cols, n);
                if (k >= 0) return k;
            }
            return -1;
        }
        int it = Find("date", "time", "datetime", "timestamp"), io = Find("open"), ih = Find("high"),
            il = Find("low"), ic = Find("close", "adj close", "adj_close"), iv = Find("volume");
        if (it < 0 || io < 0 || ih < 0 || il < 0 || ic < 0)
            throw new FormatException("CSV must contain Date, Open, High, Low and Close columns.");

        var bars = new List<Bar>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = SplitCsv(line);
            if (!TryNum(f[io], out var o) || !TryNum(f[ih], out var h) || !TryNum(f[il], out var l) || !TryNum(f[ic], out var c))
                continue; // skip rows with "null" fields (Yahoo exports them)
            double v = iv >= 0 && iv < f.Length && TryNum(f[iv], out var vv) ? vv : 0;
            bars.Add(new Bar(ParseTime(f[it]), o, h, l, c, v));
        }
        bars.Sort((a, b) => a.Time.CompareTo(b.Time));
        return FromBars(bars.ToArray(), symbol);
    }

    /// <summary>Writes "Date,Open,High,Low,Close,Volume" CSV (ISO-8601 times, invariant numbers).</summary>
    public void WriteCsv(string path) => File.WriteAllText(path, ToCsv());

    /// <summary>CSV text of this frame.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder(Count * 64);
        sb.Append("Date,Open,High,Low,Close,Volume\n");
        var inv = CultureInfo.InvariantCulture;
        for (int i = 0; i < Count; i++)
        {
            var t = Time[i];
            sb.Append(t.TimeOfDay == TimeSpan.Zero ? t.ToString("yyyy-MM-dd", inv) : t.ToString("yyyy-MM-ddTHH:mm:ss", inv)).Append(',')
              .Append(Open[i].ToString("R", inv)).Append(',')
              .Append(High[i].ToString("R", inv)).Append(',')
              .Append(Low[i].ToString("R", inv)).Append(',')
              .Append(Close[i].ToString("R", inv)).Append(',')
              .Append(Volume[i].ToString("R", inv)).Append('\n');
        }
        return sb.ToString();
    }

    private static bool TryNum(string s, out double v)
        => double.TryParse(s.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static DateTime ParseTime(string s)
    {
        s = s.Trim().Trim('"');
        if (long.TryParse(s, out long epoch))
            return epoch > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime : DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
        return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    internal static string[] SplitCsv(string line)
    {
        if (!line.Contains('"')) return line.Split(',');
        var parts = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (char ch in line)
        {
            if (ch == '"') quoted = !quoted;
            else if (ch == ',' && !quoted) { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        parts.Add(sb.ToString());
        return parts.ToArray();
    }

    /// <summary>A synthetic GBM frame (see <see cref="SyntheticMarket.GeometricBrownianBars"/>).</summary>
    public static OhlcvFrame Synthetic(int count, string symbol = "SYN", ulong seed = 42, double startPrice = 100, double drift = 0.08, double vol = 0.25)
        => FromBars(SyntheticMarket.GeometricBrownianBars(count, startPrice, drift, vol, seed), symbol);
}
