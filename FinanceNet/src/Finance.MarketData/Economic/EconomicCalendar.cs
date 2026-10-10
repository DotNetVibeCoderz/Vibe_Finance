namespace Finance.MarketData.Economic;

/// <summary>Expected market impact of a release.</summary>
public enum EventImportance { Low, Medium, High }

/// <summary>A scheduled macro-economic release.</summary>
public sealed record EconomicEvent(
    DateTime Time,
    string Country,
    string Name,
    EventImportance Importance,
    double? Actual = null,
    double? Forecast = null,
    double? Previous = null,
    string? Unit = null)
{
    /// <summary>Actual minus forecast, when both are known.</summary>
    public double? Surprise => Actual is { } a && Forecast is { } f ? a - f : null;
}

/// <summary>A source of calendar events.</summary>
public interface IEconomicCalendar
{
    Task<IReadOnlyList<EconomicEvent>> GetEventsAsync(DateTime start, DateTime end, CancellationToken cancellationToken = default);
}

/// <summary>A dated observation of a macro indicator.</summary>
public readonly record struct EconomicObservation(DateOnly Date, double Value);

/// <summary>A macro indicator time series (GDP, CPI, rates...).</summary>
public sealed record EconomicSeries(string Name, string Interval, string Unit, EconomicObservation[] Observations)
{
    /// <summary>Most recent observation.</summary>
    public EconomicObservation Latest => Observations[^1];

    /// <summary>Period-over-period percentage changes.</summary>
    public double[] PercentChanges()
    {
        var r = new double[Math.Max(0, Observations.Length - 1)];
        for (int i = 1; i < Observations.Length; i++) r[i - 1] = (Observations[i].Value / Observations[i - 1].Value - 1) * 100;
        return r;
    }
}

/// <summary>Well-known macro indicators.</summary>
public enum EconomicIndicator
{
    RealGdp, RealGdpPerCapita, TreasuryYield, FederalFundsRate, Cpi, Inflation, RetailSales, DurableGoods, Unemployment, NonfarmPayroll
}

/// <summary>A source of macro indicator series.</summary>
public interface IEconomicDataProvider
{
    /// <summary>The series, oldest first.</summary>
    Task<EconomicSeries> GetIndicatorAsync(EconomicIndicator indicator, CancellationToken cancellationToken = default);
}

/// <summary>
/// An offline calendar of recurring US/EU/ID releases generated from their usual schedules
/// (first-Friday payrolls, mid-month CPI, FOMC-style meetings every six weeks, ...). Values are not
/// included; it is meant for demos, tests and blackout-window logic.
/// </summary>
public sealed class SampleEconomicCalendar : IEconomicCalendar
{
    public Task<IReadOnlyList<EconomicEvent>> GetEventsAsync(DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        var events = new List<EconomicEvent>();
        for (var month = new DateTime(start.Year, start.Month, 1); month <= end; month = month.AddMonths(1))
        {
            var firstFriday = Nth(month, DayOfWeek.Friday, 1);
            events.Add(new(firstFriday.AddHours(12.5), "US", "Nonfarm Payrolls", EventImportance.High, Unit: "K"));
            events.Add(new(firstFriday.AddHours(12.5), "US", "Unemployment Rate", EventImportance.High, Unit: "%"));
            events.Add(new(Weekday(month.AddDays(11)).AddHours(12.5), "US", "CPI YoY", EventImportance.High, Unit: "%"));
            events.Add(new(Weekday(month.AddDays(14)).AddHours(12.5), "US", "Retail Sales MoM", EventImportance.Medium, Unit: "%"));
            events.Add(new(Weekday(month.AddDays(0)).AddHours(14), "US", "ISM Manufacturing PMI", EventImportance.Medium));
            events.Add(new(Weekday(month.AddDays(16)).AddHours(9), "EU", "Eurozone CPI YoY", EventImportance.Medium, Unit: "%"));
            events.Add(new(Weekday(month.AddDays(0)).AddHours(2), "ID", "Indonesia Inflation YoY", EventImportance.Medium, Unit: "%"));
            if ((month.Month - 1) % 3 == 0)
                events.Add(new(Weekday(month.AddDays(27)).AddHours(12.5), "US", "GDP QoQ (Advance)", EventImportance.High, Unit: "%"));
            if (month.Month % 2 == 1 || month.Month == 12)
                events.Add(new(Nth(month, DayOfWeek.Wednesday, 3).AddHours(18), "US", "FOMC Rate Decision", EventImportance.High, Unit: "%"));
            events.Add(new(Nth(month, DayOfWeek.Thursday, 3).AddHours(7), "ID", "Bank Indonesia Rate Decision", EventImportance.High, Unit: "%"));
        }
        IReadOnlyList<EconomicEvent> result = events.Where(e => e.Time >= start && e.Time <= end).OrderBy(e => e.Time).ToList();
        return Task.FromResult(result);
    }

    private static DateTime Nth(DateTime month, DayOfWeek dow, int n)
    {
        var d = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        while (d.DayOfWeek != dow) d = d.AddDays(1);
        return d.AddDays(7 * (n - 1));
    }

    private static DateTime Weekday(DateTime d)
    {
        d = DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
        return d;
    }
}
