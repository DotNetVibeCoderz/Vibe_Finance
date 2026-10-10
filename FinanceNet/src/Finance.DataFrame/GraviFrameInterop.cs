using GFrame = Gravicode.Science.GraviFrame.DataFrame;
using Gravicode.Science.GraviFrame;

namespace Finance.DataFrame;

/// <summary>
/// Conversions between Finance.Net columnar types and Gravicode.Science GraviFrame data frames,
/// so GraviFrame's group-by, joins, Parquet/Excel IO and statistics can be used on market data.
/// </summary>
public static class GraviFrameInterop
{
    /// <summary>Converts to a GraviFrame with columns Date, Open, High, Low, Close, Volume.</summary>
    public static GFrame ToGraviFrame(this OhlcvFrame frame) => new(new Series[]
    {
        new DateTimeSeries("Date", (DateTime[])frame.Time.Clone()),
        new NumericSeries("Open", (double[])frame.Open.Clone()),
        new NumericSeries("High", (double[])frame.High.Clone()),
        new NumericSeries("Low", (double[])frame.Low.Clone()),
        new NumericSeries("Close", (double[])frame.Close.Clone()),
        new NumericSeries("Volume", (double[])frame.Volume.Clone()),
    });

    /// <summary>
    /// Reads an OHLCV frame from a GraviFrame. Column names are matched case-insensitively;
    /// a missing Volume column becomes zeros.
    /// </summary>
    public static OhlcvFrame ToOhlcvFrame(this GFrame df, string symbol = "")
    {
        string Name(params string[] candidates)
        {
            foreach (var c in candidates)
            {
                var hit = df.ColumnNames.FirstOrDefault(n => string.Equals(n, c, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) return hit;
            }
            return "";
        }
        string dateCol = Name("Date", "Time", "Datetime", "Timestamp");
        if (dateCol.Length == 0) throw new ArgumentException("GraviFrame has no Date/Time column.");
        var dates = df.DateTimes(dateCol);
        int n = df.RowCount;
        var time = new DateTime[n];
        for (int i = 0; i < n; i++) time[i] = (DateTime)dates.GetValue(i)!;

        double[] Num(string col, bool optional = false)
        {
            string name = Name(col);
            if (name.Length == 0)
            {
                if (optional) return new double[n];
                throw new ArgumentException($"GraviFrame has no {col} column.");
            }
            return (double[])df.Numeric(name).Values.ToArray().Clone();
        }

        return new OhlcvFrame(time, Num("Open"), Num("High"), Num("Low"), Num("Close"), Num("Volume", optional: true), symbol);
    }

    /// <summary>Converts a price panel to a GraviFrame (Date column + one numeric column per symbol).</summary>
    public static GFrame ToGraviFrame(this PricePanel panel)
    {
        var series = new List<Series> { new DateTimeSeries("Date", (DateTime[])panel.Dates.Clone()) };
        for (int j = 0; j < panel.ColumnCount; j++)
            series.Add(new NumericSeries(panel.Symbols[j], (double[])panel.Column(j).Clone()));
        return new GFrame(series);
    }
}
