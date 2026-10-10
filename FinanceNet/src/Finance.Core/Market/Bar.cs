namespace Finance.Core.Market;

/// <summary>One OHLCV candle. A value type so arrays of bars stay contiguous in memory.</summary>
public readonly record struct Bar(DateTime Time, double Open, double High, double Low, double Close, double Volume)
{
    /// <summary>(High + Low + Close) / 3.</summary>
    public double Typical => (High + Low + Close) / 3.0;

    /// <summary>High - Low.</summary>
    public double Range => High - Low;

    /// <summary>True when the bar closed at or above its open.</summary>
    public bool IsBullish => Close >= Open;
}

/// <summary>A single trade print.</summary>
public readonly record struct Tick(DateTime Time, double Price, double Size, Side Aggressor = Side.Buy);

/// <summary>Top-of-book quote.</summary>
public readonly record struct Quote(DateTime Time, double Bid, double BidSize, double Ask, double AskSize)
{
    public double Mid => (Bid + Ask) * 0.5;
    public double Spread => Ask - Bid;

    /// <summary>Size-weighted mid ("microprice").</summary>
    public double MicroPrice => BidSize + AskSize > 0 ? (Bid * AskSize + Ask * BidSize) / (BidSize + AskSize) : Mid;
}

/// <summary>Candle interval.</summary>
public enum BarInterval
{
    Minute1, Minute5, Minute15, Minute30, Hour1, Hour4, Day1, Week1, Month1
}

public static class BarIntervalExtensions
{
    public static TimeSpan ToTimeSpan(this BarInterval interval) => interval switch
    {
        BarInterval.Minute1 => TimeSpan.FromMinutes(1),
        BarInterval.Minute5 => TimeSpan.FromMinutes(5),
        BarInterval.Minute15 => TimeSpan.FromMinutes(15),
        BarInterval.Minute30 => TimeSpan.FromMinutes(30),
        BarInterval.Hour1 => TimeSpan.FromHours(1),
        BarInterval.Hour4 => TimeSpan.FromHours(4),
        BarInterval.Day1 => TimeSpan.FromDays(1),
        BarInterval.Week1 => TimeSpan.FromDays(7),
        BarInterval.Month1 => TimeSpan.FromDays(30),
        _ => throw new ArgumentOutOfRangeException(nameof(interval)),
    };

    /// <summary>Approximate number of bars per year, used to annualise statistics.</summary>
    public static double PeriodsPerYear(this BarInterval interval) => interval switch
    {
        BarInterval.Day1 => 252,
        BarInterval.Week1 => 52,
        BarInterval.Month1 => 12,
        BarInterval.Hour4 => 252 * 6.5 / 4,
        BarInterval.Hour1 => 252 * 6.5,
        BarInterval.Minute30 => 252 * 13,
        BarInterval.Minute15 => 252 * 26,
        BarInterval.Minute5 => 252 * 78,
        BarInterval.Minute1 => 252 * 390,
        _ => 252,
    };
}
