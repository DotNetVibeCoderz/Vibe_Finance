using System.Globalization;
using System.Text.Json;
using Finance.Core.Market;
using Finance.MarketData.Economic;
using Finance.MarketData.Fundamentals;
using Finance.MarketData.Internal;

namespace Finance.MarketData.Providers;

/// <summary>
/// Alpha Vantage (https://www.alphavantage.co): prices, macro indicators and fundamentals.
/// The free tier is heavily rate limited, so wrap it in <see cref="RateLimitedProvider"/> or
/// <see cref="CachingProvider"/>. Vendor errors arrive as HTTP 200 with an "Error Message",
/// "Note" or "Information" field; those are turned into <see cref="MarketDataException"/>.
/// </summary>
public sealed class AlphaVantageProvider : IMarketDataProvider, IEconomicDataProvider, IFundamentalDataProvider
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public AlphaVantageProvider(string apiKey, HttpClient? http = null, string baseUrl = "https://www.alphavantage.co")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey;
        _http = http ?? Http.Default;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string Name => "AlphaVantage";

    public string BaseUrl { get; }

    public async Task<Bar[]> GetBarsAsync(string symbol, BarInterval interval, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        string query = interval switch
        {
            BarInterval.Day1 => $"function=TIME_SERIES_DAILY&symbol={Http.Esc(symbol)}&outputsize=full",
            BarInterval.Week1 => $"function=TIME_SERIES_WEEKLY&symbol={Http.Esc(symbol)}",
            BarInterval.Month1 => $"function=TIME_SERIES_MONTHLY&symbol={Http.Esc(symbol)}",
            BarInterval.Hour4 => $"function=TIME_SERIES_INTRADAY&symbol={Http.Esc(symbol)}&interval=60min&outputsize=full",
            _ => $"function=TIME_SERIES_INTRADAY&symbol={Http.Esc(symbol)}&interval={IntradayCode(interval)}&outputsize=full",
        };
        using var doc = await Query(query, cancellationToken).ConfigureAwait(false);
        var bars = BarAggregator.Slice(ParseTimeSeries(doc.RootElement), start, end);
        return interval == BarInterval.Hour4 ? BarAggregator.Resample(bars, TimeSpan.FromHours(4)) : bars;
    }

    public async Task<MarketSnapshot> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        using var doc = await Query($"function=GLOBAL_QUOTE&symbol={Http.Esc(symbol)}", cancellationToken).ConfigureAwait(false);
        return ParseGlobalQuote(doc.RootElement, symbol);
    }

    public async Task<EconomicSeries> GetIndicatorAsync(EconomicIndicator indicator, CancellationToken cancellationToken = default)
    {
        string fn = indicator switch
        {
            EconomicIndicator.RealGdp => "REAL_GDP&interval=quarterly",
            EconomicIndicator.RealGdpPerCapita => "REAL_GDP_PER_CAPITA",
            EconomicIndicator.TreasuryYield => "TREASURY_YIELD&interval=monthly&maturity=10year",
            EconomicIndicator.FederalFundsRate => "FEDERAL_FUNDS_RATE&interval=monthly",
            EconomicIndicator.Cpi => "CPI&interval=monthly",
            EconomicIndicator.Inflation => "INFLATION",
            EconomicIndicator.RetailSales => "RETAIL_SALES",
            EconomicIndicator.DurableGoods => "DURABLES",
            EconomicIndicator.Unemployment => "UNEMPLOYMENT",
            EconomicIndicator.NonfarmPayroll => "NONFARM_PAYROLL",
            _ => throw new ArgumentOutOfRangeException(nameof(indicator)),
        };
        using var doc = await Query("function=" + fn, cancellationToken).ConfigureAwait(false);
        return ParseEconomic(doc.RootElement);
    }

    public async Task<CompanyOverview> GetOverviewAsync(string symbol, CancellationToken cancellationToken = default)
    {
        using var doc = await Query($"function=OVERVIEW&symbol={Http.Esc(symbol)}", cancellationToken).ConfigureAwait(false);
        return ParseOverview(doc.RootElement);
    }

    public async Task<IncomeStatement[]> GetIncomeStatementsAsync(string symbol, bool quarterly = false, CancellationToken cancellationToken = default)
    {
        using var doc = await Query($"function=INCOME_STATEMENT&symbol={Http.Esc(symbol)}", cancellationToken).ConfigureAwait(false);
        return ParseIncomeStatements(doc.RootElement, quarterly);
    }

    public async Task<BalanceSheet[]> GetBalanceSheetsAsync(string symbol, bool quarterly = false, CancellationToken cancellationToken = default)
    {
        using var doc = await Query($"function=BALANCE_SHEET&symbol={Http.Esc(symbol)}", cancellationToken).ConfigureAwait(false);
        return ParseBalanceSheets(doc.RootElement, quarterly);
    }

    private async Task<JsonDocument> Query(string query, CancellationToken ct)
    {
        var doc = await Http.GetJsonAsync(_http, $"{BaseUrl}/query?{query}&apikey={Http.Esc(_apiKey)}", Name, ct).ConfigureAwait(false);
        try
        {
            CheckError(doc.RootElement);
        }
        catch
        {
            doc.Dispose();
            throw;
        }
        return doc;
    }

    internal static void CheckError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var key in new[] { "Error Message", "Note", "Information" })
            if (root.TryGetProperty(key, out var msg))
                throw new MarketDataException($"Alpha Vantage: {msg.GetString()}");
    }

    /// <summary>Parses any TIME_SERIES_* payload (the series key is detected), oldest first.</summary>
    public static Bar[] ParseTimeSeries(JsonElement root)
    {
        CheckError(root);
        JsonElement series = default;
        bool found = false;
        foreach (var p in root.EnumerateObject())
        {
            if (p.Name.Contains("Time Series", StringComparison.OrdinalIgnoreCase))
            {
                series = p.Value;
                found = true;
                break;
            }
        }
        if (!found) throw new MarketDataException("Alpha Vantage payload has no time series.");
        var bars = new List<Bar>();
        foreach (var p in series.EnumerateObject())
        {
            var t = DateTime.Parse(p.Name, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            var v = p.Value;
            bars.Add(new Bar(t, Http.Num(v, "1. open"), Http.Num(v, "2. high"), Http.Num(v, "3. low"), Http.Num(v, "4. close"),
                Field(v, "5. volume", "6. volume")));
        }
        bars.Sort((a, b) => a.Time.CompareTo(b.Time));
        return bars.ToArray();
    }

    /// <summary>Parses a GLOBAL_QUOTE payload.</summary>
    public static MarketSnapshot ParseGlobalQuote(JsonElement root, string symbol)
    {
        CheckError(root);
        var q = root.GetProperty("Global Quote");
        var pct = Http.Str(q, "10. change percent")?.TrimEnd('%');
        double change = double.TryParse(pct, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c : double.NaN;
        var day = Http.Str(q, "07. latest trading day");
        var time = day is null ? DateTime.UtcNow : DateTime.SpecifyKind(DateTime.Parse(day, CultureInfo.InvariantCulture), DateTimeKind.Utc);
        return new MarketSnapshot(symbol, time, Http.Num(q, "05. price"), Volume: Http.Num(q, "06. volume"), ChangePercent: change);
    }

    /// <summary>Parses an economic indicator payload ({name, interval, unit, data[]}); "." values are dropped.</summary>
    public static EconomicSeries ParseEconomic(JsonElement root)
    {
        CheckError(root);
        var obs = new List<EconomicObservation>();
        foreach (var d in root.GetProperty("data").EnumerateArray())
        {
            double v = Http.Num(d, "value");
            if (double.IsNaN(v)) continue;
            obs.Add(new EconomicObservation(DateOnly.Parse(Http.Str(d, "date")!, CultureInfo.InvariantCulture), v));
        }
        obs.Sort((a, b) => a.Date.CompareTo(b.Date));
        return new EconomicSeries(Http.Str(root, "name") ?? "", Http.Str(root, "interval") ?? "", Http.Str(root, "unit") ?? "", obs.ToArray());
    }

    /// <summary>Parses an OVERVIEW payload.</summary>
    public static CompanyOverview ParseOverview(JsonElement o)
    {
        CheckError(o);
        if (!o.TryGetProperty("Symbol", out _)) throw new MarketDataException("Alpha Vantage returned an empty overview.");
        return new CompanyOverview
        {
            Symbol = Http.Str(o, "Symbol")!,
            Name = Http.Str(o, "Name"),
            Exchange = Http.Str(o, "Exchange"),
            Currency = Http.Str(o, "Currency"),
            Sector = Http.Str(o, "Sector"),
            Industry = Http.Str(o, "Industry"),
            Description = Http.Str(o, "Description"),
            MarketCapitalization = Http.Num(o, "MarketCapitalization"),
            PeRatio = Http.Num(o, "PERatio"),
            PegRatio = Http.Num(o, "PEGRatio"),
            PriceToBook = Http.Num(o, "PriceToBookRatio"),
            BookValuePerShare = Http.Num(o, "BookValue"),
            Eps = Http.Num(o, "EPS"),
            DividendYield = Http.Num(o, "DividendYield"),
            ProfitMargin = Http.Num(o, "ProfitMargin"),
            ReturnOnEquity = Http.Num(o, "ReturnOnEquityTTM"),
            ReturnOnAssets = Http.Num(o, "ReturnOnAssetsTTM"),
            Beta = Http.Num(o, "Beta"),
            SharesOutstanding = Http.Num(o, "SharesOutstanding"),
            FiftyTwoWeekHigh = Http.Num(o, "52WeekHigh"),
            FiftyTwoWeekLow = Http.Num(o, "52WeekLow"),
        };
    }

    /// <summary>Parses INCOME_STATEMENT reports, newest first.</summary>
    public static IncomeStatement[] ParseIncomeStatements(JsonElement root, bool quarterly)
    {
        CheckError(root);
        return Reports(root, quarterly).Select(r => new IncomeStatement
        {
            FiscalDateEnding = DateOnly.Parse(Http.Str(r, "fiscalDateEnding")!, CultureInfo.InvariantCulture),
            TotalRevenue = Http.Num(r, "totalRevenue"),
            CostOfRevenue = Http.Num(r, "costOfRevenue"),
            GrossProfit = Http.Num(r, "grossProfit"),
            OperatingIncome = Http.Num(r, "operatingIncome"),
            NetIncome = Http.Num(r, "netIncome"),
            Ebitda = Http.Num(r, "ebitda"),
            InterestExpense = Http.Num(r, "interestExpense"),
        }).OrderByDescending(s => s.FiscalDateEnding).ToArray();
    }

    /// <summary>Parses BALANCE_SHEET reports, newest first.</summary>
    public static BalanceSheet[] ParseBalanceSheets(JsonElement root, bool quarterly)
    {
        CheckError(root);
        return Reports(root, quarterly).Select(r =>
        {
            double debt = Http.Num(r, "shortLongTermDebtTotal");
            if (double.IsNaN(debt))
            {
                double lt = Http.Num(r, "longTermDebt"), st = Http.Num(r, "shortTermDebt");
                debt = (double.IsNaN(lt) ? 0 : lt) + (double.IsNaN(st) ? 0 : st);
            }
            return new BalanceSheet
            {
                FiscalDateEnding = DateOnly.Parse(Http.Str(r, "fiscalDateEnding")!, CultureInfo.InvariantCulture),
                TotalAssets = Http.Num(r, "totalAssets"),
                TotalLiabilities = Http.Num(r, "totalLiabilities"),
                TotalShareholderEquity = Http.Num(r, "totalShareholderEquity"),
                TotalCurrentAssets = Http.Num(r, "totalCurrentAssets"),
                TotalCurrentLiabilities = Http.Num(r, "totalCurrentLiabilities"),
                CashAndEquivalents = Http.Num(r, "cashAndCashEquivalentsAtCarryingValue"),
                Inventory = Http.Num(r, "inventory"),
                TotalDebt = debt,
            };
        }).OrderByDescending(s => s.FiscalDateEnding).ToArray();
    }

    private static IEnumerable<JsonElement> Reports(JsonElement root, bool quarterly)
        => root.TryGetProperty(quarterly ? "quarterlyReports" : "annualReports", out var arr) ? arr.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static double Field(JsonElement v, params string[] names)
    {
        foreach (var n in names)
        {
            double x = Http.Num(v, n);
            if (!double.IsNaN(x)) return x;
        }
        return 0;
    }

    private static string IntradayCode(BarInterval interval) => interval switch
    {
        BarInterval.Minute1 => "1min",
        BarInterval.Minute5 => "5min",
        BarInterval.Minute15 => "15min",
        BarInterval.Minute30 => "30min",
        BarInterval.Hour1 => "60min",
        _ => throw new ArgumentOutOfRangeException(nameof(interval)),
    };
}
