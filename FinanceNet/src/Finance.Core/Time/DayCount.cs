namespace Finance.Core.Time;

/// <summary>Day-count conventions used to turn two dates into a year fraction.</summary>
public enum DayCountConvention
{
    /// <summary>Actual days / 360 (money markets, most floating legs).</summary>
    Actual360,
    /// <summary>Actual days / 365 (GBP, many curve-building contexts).</summary>
    Actual365Fixed,
    /// <summary>Actual/Actual ISDA: each calendar year's days over that year's length.</summary>
    ActualActualIsda,
    /// <summary>30/360 US (bond basis).</summary>
    Thirty360,
    /// <summary>30E/360 (Eurobond basis).</summary>
    ThirtyE360,
}

/// <summary>Year-fraction calculations.</summary>
public static class DayCount
{
    /// <summary>Year fraction between <paramref name="start"/> and <paramref name="end"/>.</summary>
    public static double YearFraction(DateOnly start, DateOnly end, DayCountConvention convention)
    {
        if (end < start) return -YearFraction(end, start, convention);
        return convention switch
        {
            DayCountConvention.Actual360 => Days(start, end) / 360.0,
            DayCountConvention.Actual365Fixed => Days(start, end) / 365.0,
            DayCountConvention.ActualActualIsda => ActActIsda(start, end),
            DayCountConvention.Thirty360 => Thirty360Us(start, end) / 360.0,
            DayCountConvention.ThirtyE360 => Thirty360E(start, end) / 360.0,
            _ => throw new ArgumentOutOfRangeException(nameof(convention)),
        };
    }

    /// <summary>Year fraction between two <see cref="DateTime"/> values (time of day ignored).</summary>
    public static double YearFraction(DateTime start, DateTime end, DayCountConvention convention = DayCountConvention.Actual365Fixed)
        => YearFraction(DateOnly.FromDateTime(start), DateOnly.FromDateTime(end), convention);

    public static int Days(DateOnly start, DateOnly end) => end.DayNumber - start.DayNumber;

    private static double ActActIsda(DateOnly start, DateOnly end)
    {
        if (start.Year == end.Year) return Days(start, end) / (double)DaysInYear(start.Year);
        double frac = Days(start, new DateOnly(start.Year + 1, 1, 1)) / (double)DaysInYear(start.Year);
        frac += end.Year - start.Year - 1;
        frac += Days(new DateOnly(end.Year, 1, 1), end) / (double)DaysInYear(end.Year);
        return frac;
    }

    private static int DaysInYear(int year) => DateTime.IsLeapYear(year) ? 366 : 365;

    private static int Thirty360Us(DateOnly s, DateOnly e)
    {
        int d1 = s.Day, d2 = e.Day;
        if (d1 == 31) d1 = 30;
        if (d2 == 31 && d1 == 30) d2 = 30;
        return 360 * (e.Year - s.Year) + 30 * (e.Month - s.Month) + (d2 - d1);
    }

    private static int Thirty360E(DateOnly s, DateOnly e)
    {
        int d1 = System.Math.Min(s.Day, 30), d2 = System.Math.Min(e.Day, 30);
        return 360 * (e.Year - s.Year) + 30 * (e.Month - s.Month) + (d2 - d1);
    }
}

/// <summary>Payment frequency.</summary>
public enum Frequency
{
    Annual = 1, SemiAnnual = 2, Quarterly = 4, Monthly = 12, Weekly = 52, Daily = 365
}

/// <summary>A period such as 3M, 1Y, 2W, 10D.</summary>
public readonly record struct Tenor(int Length, TenorUnit Unit)
{
    /// <summary>Parses strings like "3M", "10Y", "2W", "1D", "ON".</summary>
    public static Tenor Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var t = text.Trim().ToUpperInvariant();
        if (t is "ON" or "O/N") return new Tenor(1, TenorUnit.Days);
        var unit = t[^1] switch
        {
            'D' => TenorUnit.Days,
            'W' => TenorUnit.Weeks,
            'M' => TenorUnit.Months,
            'Y' => TenorUnit.Years,
            _ => throw new FormatException($"Unknown tenor unit in '{text}'."),
        };
        if (!int.TryParse(t[..^1], out int n)) throw new FormatException($"Invalid tenor '{text}'.");
        return new Tenor(n, unit);
    }

    /// <summary>Adds the tenor to a date.</summary>
    public DateOnly AddTo(DateOnly date) => Unit switch
    {
        TenorUnit.Days => date.AddDays(Length),
        TenorUnit.Weeks => date.AddDays(7 * Length),
        TenorUnit.Months => date.AddMonths(Length),
        TenorUnit.Years => date.AddYears(Length),
        _ => date,
    };

    /// <summary>Approximate length in years (used for quick curve pillars).</summary>
    public double ApproxYears => Unit switch
    {
        TenorUnit.Days => Length / 365.0,
        TenorUnit.Weeks => Length * 7 / 365.0,
        TenorUnit.Months => Length / 12.0,
        TenorUnit.Years => Length,
        _ => 0,
    };

    public override string ToString() => $"{Length}{Unit.ToString()[0]}";
}

public enum TenorUnit { Days, Weeks, Months, Years }
