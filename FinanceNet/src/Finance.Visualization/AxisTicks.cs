using System.Globalization;

namespace Finance.Visualization;

/// <summary>"Nice number" axis tick generation (Heckbert's algorithm) and date tick selection.</summary>
public static class AxisTicks
{
    /// <summary>
    /// Rounded tick values covering [min, max] with roughly <paramref name="target"/> ticks.
    /// Returns the expanded axis bounds and the ticks.
    /// </summary>
    public static (double Min, double Max, double[] Ticks) Nice(double min, double max, int target = 6)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max)) return (0, 1, new[] { 0.0, 0.5, 1.0 });
        if (max < min) (min, max) = (max, min);
        if (max - min < 1e-12)
        {
            double pad = Math.Abs(min) > 0 ? Math.Abs(min) * 0.05 : 1;
            min -= pad;
            max += pad;
        }
        double range = NiceNumber(max - min, false);
        double step = NiceNumber(range / Math.Max(1, target - 1), true);
        double lo = Math.Floor(min / step) * step, hi = Math.Ceiling(max / step) * step;
        int count = (int)Math.Round((hi - lo) / step) + 1;
        var ticks = new double[count];
        for (int i = 0; i < count; i++)
        {
            double t = lo + i * step;
            ticks[i] = Math.Abs(t) < step * 1e-9 ? 0 : t;
        }
        return (lo, hi, ticks);
    }

    private static double NiceNumber(double x, bool round)
    {
        double exp = Math.Floor(Math.Log10(x)), f = x / Math.Pow(10, exp), nf;
        if (round) nf = f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10;
        else nf = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
        return nf * Math.Pow(10, exp);
    }

    /// <summary>
    /// Indices of <paramref name="dates"/> to label (about <paramref name="target"/>), snapping to
    /// period boundaries (year/month/day changes) so labels land on meaningful dates.
    /// </summary>
    public static (int[] Indices, string Format) DateTicks(IReadOnlyList<DateTime> dates, int target = 7)
    {
        int n = dates.Count;
        if (n == 0) return (Array.Empty<int>(), "d");
        var span = dates[n - 1] - dates[0];
        string fmt = span.TotalDays > 365 * 3 ? "yyyy" : span.TotalDays > 120 ? "MMM yy" : span.TotalDays > 2 ? "dd MMM" : "HH:mm";
        Func<DateTime, DateTime, bool> boundary = fmt switch
        {
            "yyyy" => (a, b) => a.Year != b.Year,
            "MMM yy" => (a, b) => a.Month != b.Month,
            "dd MMM" => (a, b) => a.Date != b.Date,
            _ => (a, b) => a.Hour != b.Hour,
        };
        var candidates = new List<int>();
        for (int i = 1; i < n; i++) if (boundary(dates[i - 1], dates[i])) candidates.Add(i);
        if (candidates.Count == 0) candidates.Add(0);
        int stride = Math.Max(1, (int)Math.Ceiling(candidates.Count / (double)target));
        var picked = new List<int>();
        for (int i = 0; i < candidates.Count; i += stride) picked.Add(candidates[i]);
        return (picked.ToArray(), fmt);
    }

    /// <summary>Formats a tick value with invariant culture.</summary>
    public static string Format(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);
}
