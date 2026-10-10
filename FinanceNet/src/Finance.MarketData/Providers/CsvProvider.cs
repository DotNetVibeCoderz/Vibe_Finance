using System.Globalization;
using Finance.Core.Market;

namespace Finance.MarketData.Providers;

/// <summary>
/// Bars from CSV files: one file per symbol named <c>{symbol}.csv</c> inside a directory.
/// The header is required; columns are matched case-insensitively (Date/Time/Timestamp, Open, High,
/// Low, Close or "Adj Close", Volume), so Yahoo, Stooq and most broker exports load unchanged.
/// Unix timestamps (seconds or milliseconds) are accepted as well as ISO dates.
/// </summary>
public sealed class CsvProvider : IMarketDataProvider
{
    public CsvProvider(string directory) => Directory = directory;

    public string Name => "CSV";

    public string Directory { get; }

    public async Task<Bar[]> GetBarsAsync(string symbol, BarInterval interval, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Directory, symbol + ".csv");
        if (!File.Exists(path)) throw new MarketDataException($"CSV file not found: {path}");
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return BarAggregator.Slice(Parse(text), start, end);
    }

    public async Task<MarketSnapshot> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var bars = await GetBarsAsync(symbol, BarInterval.Day1, DateTime.MinValue, DateTime.MaxValue, cancellationToken).ConfigureAwait(false);
        if (bars.Length == 0) throw new MarketDataException($"No rows for {symbol}.");
        var last = bars[^1];
        double change = bars.Length > 1 ? (last.Close / bars[^2].Close - 1) * 100 : double.NaN;
        return new MarketSnapshot(symbol, last.Time, last.Close, Volume: last.Volume, ChangePercent: change);
    }

    /// <summary>Parses CSV text into bars sorted by time. Comma, semicolon and tab delimiters are detected.</summary>
    public static Bar[] Parse(string text)
    {
        using var reader = new StringReader(text);
        var header = reader.ReadLine() ?? throw new MarketDataException("CSV is empty.");
        char sep = header.Contains(';') ? ';' : header.Contains('\t') ? '\t' : ',';
        var cols = header.Split(sep).Select(c => c.Trim().Trim('"').ToLowerInvariant()).ToArray();
        int Find(params string[] names)
        {
            foreach (var n in names)
            {
                int i = Array.IndexOf(cols, n);
                if (i >= 0) return i;
            }
            return -1;
        }
        int iT = Find("date", "time", "timestamp", "datetime", "open time");
        int iO = Find("open"), iH = Find("high"), iL = Find("low");
        int iC = Find("close", "adj close", "adj_close", "price");
        int iV = Find("volume", "vol");
        if (iT < 0 || iC < 0) throw new MarketDataException("CSV needs at least a date and a close column.");

        var bars = new List<Bar>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = line.Split(sep);
            if (f.Length <= Math.Max(iT, iC)) continue;
            double c = Num(f, iC);
            if (double.IsNaN(c)) continue;
            double o = iO >= 0 ? Num(f, iO) : c, h = iH >= 0 ? Num(f, iH) : Math.Max(o, c), l = iL >= 0 ? Num(f, iL) : Math.Min(o, c);
            double v = iV >= 0 ? Num(f, iV) : 0;
            bars.Add(new Bar(ParseTime(f[iT].Trim().Trim('"')), o, h, l, c, double.IsNaN(v) ? 0 : v));
        }
        bars.Sort((a, b) => a.Time.CompareTo(b.Time));
        return bars.ToArray();
    }

    /// <summary>Writes bars as a standard Date,Open,High,Low,Close,Volume CSV.</summary>
    public static string Format(IEnumerable<Bar> bars)
    {
        var sb = new System.Text.StringBuilder("Date,Open,High,Low,Close,Volume\n");
        foreach (var b in bars)
            sb.Append(CultureInfo.InvariantCulture, $"{b.Time:yyyy-MM-ddTHH:mm:ss},{b.Open},{b.High},{b.Low},{b.Close},{b.Volume}\n");
        return sb.ToString();
    }

    private static double Num(string[] f, int i)
        => i < f.Length && double.TryParse(f[i].Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    private static DateTime ParseTime(string s)
    {
        if (long.TryParse(s, out var unix))
            return unix > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(unix).UtcDateTime : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }
}
