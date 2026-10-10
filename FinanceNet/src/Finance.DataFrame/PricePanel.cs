using System.Globalization;
using System.Text;
using Finance.Mathematics;

namespace Finance.DataFrame;

/// <summary>
/// Aligned multi-asset price table: one row per date, one column per symbol.
/// Built by inner-joining per-symbol series on their timestamps.
/// </summary>
public sealed class PricePanel
{
    private readonly double[][] _columns;

    public PricePanel(DateTime[] dates, IReadOnlyList<string> symbols, double[][] columns)
    {
        if (symbols.Count != columns.Length) throw new ArgumentException("One column per symbol is required.");
        foreach (var c in columns)
            if (c.Length != dates.Length) throw new ArgumentException("Every column must have one value per date.");
        Dates = dates;
        Symbols = symbols.ToArray();
        _columns = columns;
    }

    public DateTime[] Dates { get; }
    public string[] Symbols { get; }
    public int RowCount => Dates.Length;
    public int ColumnCount => Symbols.Length;

    /// <summary>Price column of a symbol.</summary>
    public double[] this[string symbol]
    {
        get
        {
            int i = Array.IndexOf(Symbols, symbol);
            if (i < 0) throw new KeyNotFoundException(symbol);
            return _columns[i];
        }
    }

    /// <summary>Price column by index.</summary>
    public double[] Column(int index) => _columns[index];

    /// <summary>Inner-join alignment of close prices from several frames.</summary>
    public static PricePanel FromFrames(IEnumerable<OhlcvFrame> frames)
    {
        var list = frames.ToList();
        return Align(list.Select(f => (f.Symbol, f.Time, f.Close)).ToList());
    }

    /// <summary>
    /// Inner-join alignment: keeps only timestamps present in every series. Each input must be sorted by time.
    /// </summary>
    public static PricePanel Align(IReadOnlyList<(string Symbol, DateTime[] Time, double[] Values)> series)
    {
        if (series.Count == 0) return new PricePanel(Array.Empty<DateTime>(), Array.Empty<string>(), Array.Empty<double[]>());
        HashSet<DateTime>? common = null;
        foreach (var s in series)
        {
            if (common is null) common = new HashSet<DateTime>(s.Time);
            else common.IntersectWith(s.Time);
        }
        var dates = common!.ToArray();
        Array.Sort(dates);
        var cols = new double[series.Count][];
        for (int k = 0; k < series.Count; k++)
        {
            var (_, time, values) = series[k];
            var col = new double[dates.Length];
            int j = 0;
            for (int i = 0; i < time.Length && j < dates.Length; i++)
            {
                if (time[i] == dates[j]) col[j++] = values[i];
            }
            cols[k] = col;
        }
        return new PricePanel(dates, series.Select(s => s.Symbol).ToArray(), cols);
    }

    /// <summary>Builds a panel from rows of prices (e.g. <c>SyntheticMarket.CorrelatedPrices</c>).</summary>
    public static PricePanel FromRows(DateTime[] dates, IReadOnlyList<string> symbols, double[][] rows)
    {
        var cols = new double[symbols.Count][];
        for (int k = 0; k < cols.Length; k++)
        {
            cols[k] = new double[rows.Length];
            for (int i = 0; i < rows.Length; i++) cols[k][i] = rows[i][k];
        }
        return new PricePanel(dates, symbols, cols);
    }

    /// <summary>Prices as a (dates × symbols) matrix.</summary>
    public Matrix ToMatrix()
    {
        var m = new Matrix(RowCount, ColumnCount);
        for (int j = 0; j < ColumnCount; j++)
            for (int i = 0; i < RowCount; i++) m[i, j] = _columns[j][i];
        return m;
    }

    /// <summary>Simple (or log) returns as a ((dates-1) × symbols) matrix.</summary>
    public Matrix ToReturnsMatrix(bool logReturns = false)
    {
        int n = Math.Max(0, RowCount - 1);
        var m = new Matrix(n, ColumnCount);
        for (int j = 0; j < ColumnCount; j++)
        {
            var c = _columns[j];
            for (int i = 0; i < n; i++)
                m[i, j] = logReturns ? Math.Log(c[i + 1] / c[i]) : c[i + 1] / c[i] - 1;
        }
        return m;
    }

    /// <summary>Selects a subset of symbols.</summary>
    public PricePanel Select(params string[] symbols)
        => new(Dates, symbols, symbols.Select(s => this[s]).ToArray());

    /// <summary>Wide CSV: Date,SYM1,SYM2,...</summary>
    public string ToCsv()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("Date");
        foreach (var s in Symbols) sb.Append(',').Append(s);
        sb.Append('\n');
        for (int i = 0; i < RowCount; i++)
        {
            sb.Append(Dates[i].ToString("yyyy-MM-dd", inv));
            for (int j = 0; j < ColumnCount; j++) sb.Append(',').Append(_columns[j][i].ToString("R", inv));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Parses wide CSV (Date,SYM1,SYM2,...); rows with any missing value are dropped.</summary>
    public static PricePanel ParseCsv(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) throw new FormatException("Empty CSV.");
        var header = OhlcvFrame.SplitCsv(lines[0]);
        var symbols = header.Skip(1).Select(s => s.Trim()).ToArray();
        var dates = new List<DateTime>();
        var cols = symbols.Select(_ => new List<double>()).ToArray();
        var inv = CultureInfo.InvariantCulture;
        var row = new double[symbols.Length];
        for (int i = 1; i < lines.Length; i++)
        {
            var f = OhlcvFrame.SplitCsv(lines[i]);
            if (f.Length < symbols.Length + 1) continue;
            bool ok = true;
            for (int j = 0; j < symbols.Length && ok; j++)
                ok = double.TryParse(f[j + 1], NumberStyles.Float, inv, out row[j]);
            if (!ok) continue;
            dates.Add(DateTime.Parse(f[0], inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
            for (int j = 0; j < symbols.Length; j++) cols[j].Add(row[j]);
        }
        return new PricePanel(dates.ToArray(), symbols, cols.Select(c => c.ToArray()).ToArray());
    }
}
