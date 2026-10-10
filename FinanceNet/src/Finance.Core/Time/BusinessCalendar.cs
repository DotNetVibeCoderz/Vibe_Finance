namespace Finance.Core.Time;

/// <summary>Business-day adjustment rules.</summary>
public enum BusinessDayConvention
{
    Unadjusted, Following, ModifiedFollowing, Preceding, ModifiedPreceding
}

/// <summary>
/// A trading calendar: weekends plus an explicit holiday set.
/// <see cref="NewYorkStockExchange"/> and <see cref="IndonesiaStockExchange"/> carry fixed-rule holidays only
/// (moveable feasts must be added with <see cref="AddHoliday"/>).
/// </summary>
public sealed class BusinessCalendar
{
    private readonly HashSet<DateOnly> _holidays = new();
    private readonly Func<int, IEnumerable<DateOnly>>? _ruleHolidays;
    private readonly HashSet<int> _expandedYears = new();
    private readonly object _gate = new();

    public BusinessCalendar(string name, Func<int, IEnumerable<DateOnly>>? ruleHolidays = null)
    {
        Name = name;
        _ruleHolidays = ruleHolidays;
    }

    public string Name { get; }

    /// <summary>Weekends only.</summary>
    public static BusinessCalendar WeekendsOnly { get; } = new("WeekendsOnly");

    /// <summary>NYSE fixed-rule holidays (New Year, Juneteenth, Independence Day, Christmas, MLK, Presidents, Memorial, Labor, Thanksgiving).</summary>
    public static BusinessCalendar NewYorkStockExchange { get; } = new("NYSE", UsHolidays);

    /// <summary>IDX fixed-date holidays (New Year, Labour Day, Pancasila, Independence Day, Christmas).</summary>
    public static BusinessCalendar IndonesiaStockExchange { get; } = new("IDX", year => new[]
    {
        new DateOnly(year, 1, 1), new DateOnly(year, 5, 1), new DateOnly(year, 6, 1),
        new DateOnly(year, 8, 17), new DateOnly(year, 12, 25),
    });

    public void AddHoliday(DateOnly date)
    {
        lock (_gate) _holidays.Add(date);
    }

    public bool IsHoliday(DateOnly date)
    {
        EnsureYear(date.Year);
        lock (_gate) return _holidays.Contains(date);
    }

    public bool IsBusinessDay(DateOnly date)
        => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !IsHoliday(date);

    /// <summary>Moves a date onto a business day according to <paramref name="convention"/>.</summary>
    public DateOnly Adjust(DateOnly date, BusinessDayConvention convention = BusinessDayConvention.Following)
    {
        switch (convention)
        {
            case BusinessDayConvention.Unadjusted:
                return date;
            case BusinessDayConvention.Following:
                while (!IsBusinessDay(date)) date = date.AddDays(1);
                return date;
            case BusinessDayConvention.Preceding:
                while (!IsBusinessDay(date)) date = date.AddDays(-1);
                return date;
            case BusinessDayConvention.ModifiedFollowing:
            {
                var d = Adjust(date, BusinessDayConvention.Following);
                return d.Month == date.Month ? d : Adjust(date, BusinessDayConvention.Preceding);
            }
            case BusinessDayConvention.ModifiedPreceding:
            {
                var d = Adjust(date, BusinessDayConvention.Preceding);
                return d.Month == date.Month ? d : Adjust(date, BusinessDayConvention.Following);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(convention));
        }
    }

    /// <summary>Adds (or subtracts) business days.</summary>
    public DateOnly AddBusinessDays(DateOnly date, int days)
    {
        int step = System.Math.Sign(days);
        while (days != 0)
        {
            date = date.AddDays(step);
            if (IsBusinessDay(date)) days -= step;
        }
        return date;
    }

    /// <summary>Number of business days in (start, end].</summary>
    public int BusinessDaysBetween(DateOnly start, DateOnly end)
    {
        int n = 0;
        for (var d = start.AddDays(1); d <= end; d = d.AddDays(1))
            if (IsBusinessDay(d)) n++;
        return n;
    }

    /// <summary>All business days in [start, end].</summary>
    public IEnumerable<DateOnly> BusinessDays(DateOnly start, DateOnly end)
    {
        for (var d = start; d <= end; d = d.AddDays(1))
            if (IsBusinessDay(d)) yield return d;
    }

    private void EnsureYear(int year)
    {
        if (_ruleHolidays is null) return;
        lock (_gate)
        {
            if (!_expandedYears.Add(year)) return;
            foreach (var h in _ruleHolidays(year)) _holidays.Add(h);
        }
    }

    private static IEnumerable<DateOnly> UsHolidays(int year)
    {
        yield return Observed(new DateOnly(year, 1, 1));
        yield return NthWeekday(year, 1, DayOfWeek.Monday, 3);   // MLK
        yield return NthWeekday(year, 2, DayOfWeek.Monday, 3);   // Presidents
        yield return LastWeekday(year, 5, DayOfWeek.Monday);     // Memorial
        if (year >= 2022) yield return Observed(new DateOnly(year, 6, 19));
        yield return Observed(new DateOnly(year, 7, 4));
        yield return NthWeekday(year, 9, DayOfWeek.Monday, 1);   // Labor
        yield return NthWeekday(year, 11, DayOfWeek.Thursday, 4); // Thanksgiving
        yield return Observed(new DateOnly(year, 12, 25));
    }

    private static DateOnly Observed(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Saturday => d.AddDays(-1),
        DayOfWeek.Sunday => d.AddDays(1),
        _ => d,
    };

    private static DateOnly NthWeekday(int year, int month, DayOfWeek dow, int n)
    {
        var d = new DateOnly(year, month, 1);
        while (d.DayOfWeek != dow) d = d.AddDays(1);
        return d.AddDays(7 * (n - 1));
    }

    private static DateOnly LastWeekday(int year, int month, DayOfWeek dow)
    {
        var d = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        while (d.DayOfWeek != dow) d = d.AddDays(-1);
        return d;
    }
}
