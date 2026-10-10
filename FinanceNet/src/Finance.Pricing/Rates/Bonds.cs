using Finance.Core.Time;
using Finance.Mathematics;

namespace Finance.Pricing.Rates;

/// <summary>One bond cash flow seen from a settlement date.</summary>
/// <param name="Date">Payment date.</param>
/// <param name="Amount">Payment per bond (coupon, plus redemption on the last date).</param>
/// <param name="Periods">Time to payment in coupon periods (fractional for the first one).</param>
/// <param name="Years">Time to payment in years (Periods / frequency).</param>
public readonly record struct BondCashFlow(DateOnly Date, double Amount, double Periods, double Years);

/// <summary>Yield-based analytics of a bond.</summary>
public sealed record BondAnalytics(double CleanPrice, double DirtyPrice, double AccruedInterest, double YieldToMaturity,
    double MacaulayDuration, double ModifiedDuration, double Convexity, double Dv01);

/// <summary>
/// Fixed-coupon bullet bond. The coupon schedule is generated backwards from maturity (regular periods,
/// short first stub). Yields use street convention: compounding at the coupon frequency, with the first
/// period discounted by the accrual fraction remaining.
/// </summary>
public sealed class FixedRateBond
{
    public FixedRateBond(DateOnly issueDate, DateOnly maturity, double couponRate, int frequency = 2, double faceValue = 100,
        DayCountConvention dayCount = DayCountConvention.ActualActualIsda)
    {
        if (maturity <= issueDate) throw new ArgumentException("Maturity must follow issue.");
        if (frequency is not (1 or 2 or 4 or 12)) throw new ArgumentException("Frequency must be 1, 2, 4 or 12.");
        IssueDate = issueDate;
        Maturity = maturity;
        CouponRate = couponRate;
        Frequency = frequency;
        FaceValue = faceValue;
        DayCount = dayCount;

        var dates = new List<DateOnly>();
        int months = 12 / frequency;
        for (int k = 0; ; k++)
        {
            var d = maturity.AddMonths(-months * k);
            if (d <= issueDate) break;
            dates.Add(d);
        }
        dates.Reverse();
        CouponDates = dates;
    }

    public DateOnly IssueDate { get; }
    public DateOnly Maturity { get; }
    public double CouponRate { get; }
    public int Frequency { get; }
    public double FaceValue { get; }
    public DayCountConvention DayCount { get; }

    /// <summary>Coupon payment dates (ascending, last = maturity).</summary>
    public IReadOnlyList<DateOnly> CouponDates { get; }

    /// <summary>Regular coupon amount per period.</summary>
    public double CouponAmount => FaceValue * CouponRate / Frequency;

    private (DateOnly Previous, DateOnly Next, int NextIndex) Period(DateOnly settlement)
    {
        if (settlement >= Maturity) throw new ArgumentException("Settlement is on or after maturity.");
        int i = 0;
        while (CouponDates[i] <= settlement) i++;
        var prev = i == 0 ? Maturity.AddMonths(-(12 / Frequency) * CouponDates.Count) : CouponDates[i - 1];
        return (prev, CouponDates[i], i);
    }

    /// <summary>Fraction of the current coupon period already elapsed at settlement.</summary>
    public double AccrualFraction(DateOnly settlement)
    {
        var (prev, next, _) = Period(settlement);
        return DayCountFraction(prev, settlement) / DayCountFraction(prev, next);
    }

    private double DayCountFraction(DateOnly a, DateOnly b) => Core.Time.DayCount.YearFraction(a, b, DayCount);

    /// <summary>Accrued interest at settlement.</summary>
    public double AccruedInterest(DateOnly settlement)
    {
        var (prev, _, _) = Period(settlement);
        if (settlement < IssueDate) return 0;
        var start = prev < IssueDate ? IssueDate : prev;
        if (start != prev)
            return CouponAmount * DayCountFraction(start, settlement) / DayCountFraction(prev, Period(settlement).Next);
        return CouponAmount * AccrualFraction(settlement);
    }

    /// <summary>Remaining cash flows after settlement.</summary>
    public IReadOnlyList<BondCashFlow> CashFlows(DateOnly settlement)
    {
        var (prev, next, idx) = Period(settlement);
        double w = 1 - AccrualFraction(settlement);
        var list = new List<BondCashFlow>();
        for (int i = idx, k = 0; i < CouponDates.Count; i++, k++)
        {
            double coupon = CouponAmount;
            if (i == 0 && prev < IssueDate) // short first coupon
                coupon *= DayCountFraction(IssueDate, next) / DayCountFraction(prev, next);
            double amount = coupon + (i == CouponDates.Count - 1 ? FaceValue : 0);
            double periods = w + k;
            list.Add(new BondCashFlow(CouponDates[i], amount, periods, periods / Frequency));
        }
        return list;
    }

    /// <summary>Dirty (full) price from a yield to maturity.</summary>
    public double DirtyPrice(double yield, DateOnly settlement)
    {
        double f = Frequency, pv = 0, baseRate = 1 + yield / f;
        foreach (var cf in CashFlows(settlement)) pv += cf.Amount / Math.Pow(baseRate, cf.Periods);
        return pv;
    }

    /// <summary>Clean price (dirty minus accrued) from a yield.</summary>
    public double CleanPrice(double yield, DateOnly settlement) => DirtyPrice(yield, settlement) - AccruedInterest(settlement);

    /// <summary>Yield to maturity from a clean price (Brent on [-0.99f, 2]).</summary>
    public double YieldToMaturity(double cleanPrice, DateOnly settlement)
    {
        double dirty = cleanPrice + AccruedInterest(settlement);
        return RootFinding.Brent(y => DirtyPrice(y, settlement) - dirty, -0.99 * Frequency + 1e-6, 2.0, 1e-13);
    }

    /// <summary>Dirty price discounting each cash flow on a zero curve (plus an optional z-spread).</summary>
    public double DirtyPrice(YieldCurve curve, DateOnly settlement, double zSpread = 0)
    {
        double pv = 0;
        foreach (var cf in CashFlows(settlement))
            pv += cf.Amount * Math.Exp(-(curve.ZeroRate(cf.Years) + zSpread) * cf.Years);
        return pv;
    }

    /// <summary>Z-spread over <paramref name="curve"/> that reproduces <paramref name="cleanPrice"/>.</summary>
    public double ZSpread(YieldCurve curve, double cleanPrice, DateOnly settlement)
    {
        double dirty = cleanPrice + AccruedInterest(settlement);
        return RootFinding.Brent(s => DirtyPrice(curve, settlement, s) - dirty, -0.5, 1.0, 1e-13);
    }

    /// <summary>Full yield analytics at a given yield.</summary>
    public BondAnalytics Analytics(double yield, DateOnly settlement)
    {
        double f = Frequency, b = 1 + yield / f;
        double dirty = 0, mac = 0, conv = 0;
        foreach (var cf in CashFlows(settlement))
        {
            double pv = cf.Amount / Math.Pow(b, cf.Periods);
            dirty += pv;
            mac += cf.Years * pv;
            conv += cf.Amount * cf.Periods * (cf.Periods + 1) / Math.Pow(b, cf.Periods + 2) / (f * f);
        }
        mac /= dirty;
        conv /= dirty;
        double mod = mac / b;
        double accrued = AccruedInterest(settlement);
        return new BondAnalytics(dirty - accrued, dirty, accrued, yield, mac, mod, conv, mod * dirty * 1e-4);
    }
}

/// <summary>Zero-coupon bond analytics in time-to-maturity terms.</summary>
public static class ZeroCouponBond
{
    /// <summary>Price with an annually-compounded (or <paramref name="frequency"/>) yield.</summary>
    public static double Price(double faceValue, double yield, double maturity, int frequency = 1)
        => faceValue / Math.Pow(1 + yield / frequency, maturity * frequency);

    /// <summary>Price discounting on a zero curve.</summary>
    public static double Price(double faceValue, YieldCurve curve, double maturity) => faceValue * curve.DiscountFactor(maturity);

    /// <summary>Yield implied by a price.</summary>
    public static double Yield(double faceValue, double price, double maturity, int frequency = 1)
        => frequency * (Math.Pow(faceValue / price, 1.0 / (maturity * frequency)) - 1);

    /// <summary>Modified duration.</summary>
    public static double ModifiedDuration(double yield, double maturity, int frequency = 1) => maturity / (1 + yield / frequency);
}

/// <summary>
/// Floating-rate note paying the forward rate plus a quoted margin each period, valued on a (forecast, discount) curve pair.
/// At a reset date with zero margin and the same forecast and discount curve it prices at par.
/// </summary>
public sealed record FloatingRateNote(double Maturity, int Frequency = 4, double QuotedMargin = 0, double FaceValue = 100)
{
    /// <summary>Price (as of a reset date).</summary>
    public double Price(YieldCurve discount, YieldCurve? forecast = null, double discountMargin = 0)
    {
        forecast ??= discount;
        double tau = 1.0 / Frequency, pv = 0;
        int n = (int)Math.Round(Maturity * Frequency);
        for (int i = 1; i <= n; i++)
        {
            double t0 = (i - 1) * tau, t1 = i * tau;
            double fwd = forecast.ForwardRate(Math.Max(t0, 1e-9), t1);
            if (i == 1) fwd = (1 / forecast.DiscountFactor(t1) - 1) / tau;
            double df = discount.DiscountFactor(t1) * Math.Exp(-discountMargin * t1);
            pv += FaceValue * (fwd + QuotedMargin) * tau * df;
            if (i == n) pv += FaceValue * df;
        }
        return pv;
    }
}
