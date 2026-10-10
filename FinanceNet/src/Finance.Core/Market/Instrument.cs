namespace Finance.Core.Market;

/// <summary>Broad asset class.</summary>
public enum AssetClass
{
    Equity, Etf, Index, Bond, Commodity, Forex, Crypto, Future, Option, Swap, Cds, Structured
}

/// <summary>An ISO-4217 style currency code.</summary>
public readonly record struct Currency(string Code)
{
    public static readonly Currency USD = new("USD");
    public static readonly Currency EUR = new("EUR");
    public static readonly Currency JPY = new("JPY");
    public static readonly Currency GBP = new("GBP");
    public static readonly Currency IDR = new("IDR");
    public static readonly Currency USDT = new("USDT");

    public override string ToString() => Code;
}

/// <summary>An amount in a currency. Arithmetic refuses to mix currencies.</summary>
public readonly record struct Money(decimal Amount, Currency Currency)
{
    public static Money operator +(Money a, Money b) { Same(a, b); return new(a.Amount + b.Amount, a.Currency); }
    public static Money operator -(Money a, Money b) { Same(a, b); return new(a.Amount - b.Amount, a.Currency); }
    public static Money operator *(Money a, decimal k) => new(a.Amount * k, a.Currency);

    private static void Same(Money a, Money b)
    {
        if (a.Currency != b.Currency) throw new InvalidOperationException($"Cannot combine {a.Currency} with {b.Currency}.");
    }

    public override string ToString() => $"{Amount:N2} {Currency}";
}

/// <summary>A tradable instrument.</summary>
public sealed record Instrument(string Symbol, AssetClass AssetClass = AssetClass.Equity, string Currency = "USD", string? Exchange = null)
{
    /// <summary>Minimum price increment.</summary>
    public double TickSize { get; init; } = 0.01;

    /// <summary>Contract multiplier (1 for cash instruments).</summary>
    public double Multiplier { get; init; } = 1.0;

    /// <summary>Minimum order quantity step.</summary>
    public double LotSize { get; init; } = 1.0;

    public override string ToString() => Exchange is null ? Symbol : $"{Symbol}.{Exchange}";
}
