using Finance.Core.Time;

namespace Finance.Pricing.Credit;

/// <summary>How standard CDS maturities roll.</summary>
public enum CdsRollConvention
{
    /// <summary>
    /// The convention in force since 20 Dec 2015: the on-the-run maturity rolls only on 20 Mar and 20 Sep, so
    /// trades from 20 Mar to 19 Sep mature on 20 Jun and trades from 20 Sep to 19 Mar mature on 20 Dec.
    /// </summary>
    SemiAnnual2015,
    /// <summary>The pre-2015 convention: maturities roll on every IMM date (20 Mar/Jun/Sep/Dec).</summary>
    Quarterly,
}

/// <summary>Standard CDS (IMM) dates: the 20th of March, June, September and December.</summary>
public static class CdsDates
{
    /// <summary>True for an unadjusted CDS roll date (20 Mar/Jun/Sep/Dec).</summary>
    public static bool IsImmDate(DateOnly date) => date.Day == 20 && date.Month % 3 == 0;

    /// <summary>The first unadjusted IMM date strictly after <paramref name="date"/>.</summary>
    public static DateOnly NextImmDate(DateOnly date)
    {
        int idx = date.Year * 12 + date.Month - 1;
        if (date.Day >= 20) idx++;
        while ((idx % 12 + 1) % 3 != 0) idx++;
        return new DateOnly(idx / 12, idx % 12 + 1, 20);
    }

    /// <summary>The last unadjusted IMM date on or before <paramref name="date"/>.</summary>
    public static DateOnly PreviousImmDate(DateOnly date)
    {
        int idx = date.Year * 12 + date.Month - 1;
        if (date.Day < 20) idx--;
        while ((idx % 12 + 1) % 3 != 0) idx--;
        return new DateOnly(idx / 12, idx % 12 + 1, 20);
    }

    /// <summary>
    /// Standard (unadjusted) maturity of a CDS traded on <paramref name="tradeDate"/> with the given tenor, using
    /// the QuantLib <c>cdsMaturity</c> rule: anchor on the last IMM date on or before the trade date (for
    /// <see cref="CdsRollConvention.SemiAnnual2015"/>, a June or December anchor is moved back to March or
    /// September), then add the tenor plus three months. Rolls take effect on the 20th itself.
    /// </summary>
    /// <param name="tradeDate">Trade date.</param>
    /// <param name="tenor">Tenor; a positive whole number of quarters (3M, 6M, 1Y, 5Y, …).</param>
    /// <param name="roll">Roll convention.</param>
    public static DateOnly StandardMaturity(DateOnly tradeDate, Tenor tenor, CdsRollConvention roll = CdsRollConvention.SemiAnnual2015)
    {
        int months = tenor.Unit switch
        {
            TenorUnit.Months => tenor.Length,
            TenorUnit.Years => 12 * tenor.Length,
            _ => throw new ArgumentException("A CDS tenor must be in months or years.", nameof(tenor)),
        };
        if (months <= 0 || months % 3 != 0)
            throw new ArgumentException("A CDS tenor must be a positive whole number of quarters.", nameof(tenor));
        var anchor = PreviousImmDate(tradeDate);
        if (roll == CdsRollConvention.SemiAnnual2015 && anchor.Month is 6 or 12) anchor = anchor.AddMonths(-3);
        return anchor.AddMonths(months + 3);
    }
}

/// <summary>One premium period of a dated CDS.</summary>
/// <param name="AccrualStart">First accrual day (business-day adjusted).</param>
/// <param name="AccrualEnd">Accrual end, exclusive: the next adjusted IMM date, or maturity + 1 day for the last period.</param>
/// <param name="PaymentDate">Coupon payment date (adjusted Following).</param>
/// <param name="AccrualFraction">Act/360 accrual: days(AccrualStart, AccrualEnd) / 360.</param>
public sealed record CdsPeriod(DateOnly AccrualStart, DateOnly AccrualEnd, DateOnly PaymentDate, double AccrualFraction)
{
    /// <summary>Number of accrual days.</summary>
    public int Days => AccrualEnd.DayNumber - AccrualStart.DayNumber;
}

/// <summary>
/// Premium schedule of a standard (post-Big-Bang) CDS, built the way the ISDA CDS Standard Model builds it:
/// step-in at T+1 (calendar), cash settlement at T+3 business days, accrual from the latest adjusted IMM date on or
/// before step-in (full first coupon, accrued rebated at settlement), quarterly IMM coupon dates adjusted
/// Following, Act/360 accrual, and a final period that accrues to maturity inclusive (accrual end = unadjusted
/// maturity + 1 day) and pays on the adjusted maturity date.
/// </summary>
/// <remarks>
/// Model times are Act/365F from the trade date (<see cref="TimeOf"/>), the ISDA curve convention. If the
/// adjusted IMM date on or before step-in falls after step-in (an IMM date on a weekend with a Friday trade), the
/// previous IMM date is used, so accrued days are never negative.
/// </remarks>
public sealed class CdsSchedule
{
    private CdsSchedule(DateOnly tradeDate, DateOnly stepIn, DateOnly cashSettlement, DateOnly maturity, CdsPeriod[] periods)
    {
        TradeDate = tradeDate;
        StepInDate = stepIn;
        CashSettlementDate = cashSettlement;
        MaturityDate = maturity;
        Periods = periods;
    }

    /// <summary>Trade date T (model time 0).</summary>
    public DateOnly TradeDate { get; }

    /// <summary>Step-in date T+1 (calendar day).</summary>
    public DateOnly StepInDate { get; }

    /// <summary>Cash settlement date T+3 business days.</summary>
    public DateOnly CashSettlementDate { get; }

    /// <summary>Unadjusted maturity (protection end, inclusive).</summary>
    public DateOnly MaturityDate { get; }

    /// <summary>Premium periods, first to last.</summary>
    public IReadOnlyList<CdsPeriod> Periods { get; }

    /// <summary>Accrual start of the first period.</summary>
    public DateOnly AccrualStartDate => Periods[0].AccrualStart;

    /// <summary>Accrued days at step-in: StepIn − AccrualStart (accrual start to T inclusive).</summary>
    public int AccruedDays => StepInDate.DayNumber - AccrualStartDate.DayNumber;

    /// <summary>Accrued Act/360 fraction at step-in.</summary>
    public double AccruedFraction => AccruedDays / 360.0;

    /// <summary>Model time of a date: Act/365F years from the trade date.</summary>
    public double TimeOf(DateOnly date) => (date.DayNumber - TradeDate.DayNumber) / 365.0;

    /// <summary>Builds the schedule of a CDS traded on <paramref name="tradeDate"/> maturing on <paramref name="maturity"/>.</summary>
    /// <param name="tradeDate">Trade date.</param>
    /// <param name="maturity">Unadjusted maturity (normally an IMM date).</param>
    /// <param name="calendar">Business-day calendar; weekends only when null.</param>
    public static CdsSchedule Build(DateOnly tradeDate, DateOnly maturity, BusinessCalendar? calendar = null)
    {
        calendar ??= BusinessCalendar.WeekendsOnly;
        var stepIn = tradeDate.AddDays(1);
        if (maturity <= stepIn) throw new ArgumentException("Maturity must be after the step-in date.", nameof(maturity));
        DateOnly Adj(DateOnly d) => calendar.Adjust(d, BusinessDayConvention.Following);

        var first = CdsDates.PreviousImmDate(stepIn);
        if (Adj(first) > stepIn) first = CdsDates.PreviousImmDate(first.AddDays(-1));
        var dates = new List<DateOnly> { first };
        for (var d = CdsDates.NextImmDate(first); ; d = CdsDates.NextImmDate(d))
        {
            if (d >= maturity) { dates.Add(maturity); break; }
            dates.Add(d);
        }

        var periods = new CdsPeriod[dates.Count - 1];
        for (int i = 1; i < dates.Count; i++)
        {
            bool last = i == dates.Count - 1;
            var start = Adj(dates[i - 1]);
            var end = last ? maturity.AddDays(1) : Adj(dates[i]);
            periods[i - 1] = new CdsPeriod(start, end, Adj(dates[i]), (end.DayNumber - start.DayNumber) / 360.0);
        }
        return new CdsSchedule(tradeDate, stepIn, calendar.AddBusinessDays(tradeDate, 3), maturity, periods);
    }

    /// <summary>Schedule of the standard CDS traded on <paramref name="tradeDate"/> with the given tenor.</summary>
    public static CdsSchedule Standard(DateOnly tradeDate, Tenor tenor, BusinessCalendar? calendar = null,
        CdsRollConvention roll = CdsRollConvention.SemiAnnual2015)
        => Build(tradeDate, CdsDates.StandardMaturity(tradeDate, tenor, roll), calendar);
}
