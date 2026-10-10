namespace Finance.MarketData.Fundamentals;

/// <summary>Company profile and headline valuation metrics.</summary>
public sealed record CompanyOverview
{
    public required string Symbol { get; init; }
    public string? Name { get; init; }
    public string? Exchange { get; init; }
    public string? Currency { get; init; }
    public string? Sector { get; init; }
    public string? Industry { get; init; }
    public string? Description { get; init; }
    public double MarketCapitalization { get; init; } = double.NaN;
    public double PeRatio { get; init; } = double.NaN;
    public double PegRatio { get; init; } = double.NaN;
    public double PriceToBook { get; init; } = double.NaN;
    public double BookValuePerShare { get; init; } = double.NaN;
    public double Eps { get; init; } = double.NaN;
    public double DividendYield { get; init; } = double.NaN;
    public double ProfitMargin { get; init; } = double.NaN;
    public double ReturnOnEquity { get; init; } = double.NaN;
    public double ReturnOnAssets { get; init; } = double.NaN;
    public double Beta { get; init; } = double.NaN;
    public double SharesOutstanding { get; init; } = double.NaN;
    public double FiftyTwoWeekHigh { get; init; } = double.NaN;
    public double FiftyTwoWeekLow { get; init; } = double.NaN;
}

/// <summary>One income statement period.</summary>
public sealed record IncomeStatement
{
    public required DateOnly FiscalDateEnding { get; init; }
    public double TotalRevenue { get; init; } = double.NaN;
    public double CostOfRevenue { get; init; } = double.NaN;
    public double GrossProfit { get; init; } = double.NaN;
    public double OperatingIncome { get; init; } = double.NaN;
    public double NetIncome { get; init; } = double.NaN;
    public double Ebitda { get; init; } = double.NaN;
    public double InterestExpense { get; init; } = double.NaN;
}

/// <summary>One balance sheet period.</summary>
public sealed record BalanceSheet
{
    public required DateOnly FiscalDateEnding { get; init; }
    public double TotalAssets { get; init; } = double.NaN;
    public double TotalLiabilities { get; init; } = double.NaN;
    public double TotalShareholderEquity { get; init; } = double.NaN;
    public double TotalCurrentAssets { get; init; } = double.NaN;
    public double TotalCurrentLiabilities { get; init; } = double.NaN;
    public double CashAndEquivalents { get; init; } = double.NaN;
    public double Inventory { get; init; } = double.NaN;
    public double TotalDebt { get; init; } = double.NaN;
}

/// <summary>Standard ratios computed from statements (NaN when an input is missing).</summary>
public sealed record FundamentalRatios(
    double GrossMargin,
    double OperatingMargin,
    double NetMargin,
    double ReturnOnEquity,
    double ReturnOnAssets,
    double CurrentRatio,
    double QuickRatio,
    double DebtToEquity,
    double InterestCoverage,
    double RevenueGrowth)
{
    /// <summary>
    /// Ratios for the latest period. <paramref name="previousIncome"/> enables year-over-year revenue growth.
    /// </summary>
    public static FundamentalRatios Compute(IncomeStatement income, BalanceSheet balance, IncomeStatement? previousIncome = null)
    {
        static double Div(double a, double b) => double.IsNaN(a) || double.IsNaN(b) || b == 0 ? double.NaN : a / b;
        double inventory = double.IsNaN(balance.Inventory) ? 0 : balance.Inventory;
        return new FundamentalRatios(
            Div(income.GrossProfit, income.TotalRevenue),
            Div(income.OperatingIncome, income.TotalRevenue),
            Div(income.NetIncome, income.TotalRevenue),
            Div(income.NetIncome, balance.TotalShareholderEquity),
            Div(income.NetIncome, balance.TotalAssets),
            Div(balance.TotalCurrentAssets, balance.TotalCurrentLiabilities),
            Div(balance.TotalCurrentAssets - inventory, balance.TotalCurrentLiabilities),
            Div(balance.TotalDebt, balance.TotalShareholderEquity),
            Div(income.OperatingIncome, income.InterestExpense),
            previousIncome is null ? double.NaN : Div(income.TotalRevenue, previousIncome.TotalRevenue) - 1);
    }
}

/// <summary>A source of company fundamentals.</summary>
public interface IFundamentalDataProvider
{
    Task<CompanyOverview> GetOverviewAsync(string symbol, CancellationToken cancellationToken = default);

    /// <summary>Income statements, newest first.</summary>
    Task<IncomeStatement[]> GetIncomeStatementsAsync(string symbol, bool quarterly = false, CancellationToken cancellationToken = default);

    /// <summary>Balance sheets, newest first.</summary>
    Task<BalanceSheet[]> GetBalanceSheetsAsync(string symbol, bool quarterly = false, CancellationToken cancellationToken = default);
}
