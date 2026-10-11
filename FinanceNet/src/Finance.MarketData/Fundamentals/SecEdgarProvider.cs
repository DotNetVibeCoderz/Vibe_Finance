using System.Globalization;
using System.Text.Json;
using Finance.MarketData.Internal;

namespace Finance.MarketData.Fundamentals;

/// <summary>One XBRL fact as filed with the SEC.</summary>
/// <param name="Start">Period start (null for instant facts such as balance-sheet items).</param>
/// <param name="End">Period end or instant.</param>
/// <param name="Value">Reported value.</param>
/// <param name="Form">10-K, 10-Q, ...</param>
/// <param name="FiscalPeriod">FY, Q1, Q2, Q3.</param>
/// <param name="FiscalYear">Fiscal year of the filing that reported the fact.</param>
/// <param name="Filed">Filing date.</param>
public sealed record XbrlFact(DateOnly? Start, DateOnly End, double Value, string Form, string FiscalPeriod, int? FiscalYear, DateOnly Filed);

/// <summary>
/// Company fundamentals from SEC EDGAR's XBRL APIs (<c>data.sec.gov</c>, free, no key). The SEC requires a
/// descriptive User-Agent with a contact address, and allows at most 10 requests per second.
/// </summary>
/// <remarks>
/// Statements are assembled from us-gaap concepts, merging each list of synonyms per period (filers differ, and
/// change concept over time: Revenues vs RevenueFromContractWithCustomerExcludingAssessedTax, ...). Annual figures come from
/// 10-K facts covering about a year; quarterly figures from 10-Q facts covering about a quarter. Q4 is not filed
/// separately and is therefore absent from quarterly results. When a figure was restated, the latest filing wins.
/// </remarks>
public sealed class SecEdgarProvider : IFundamentalDataProvider
{
    private readonly HttpClient _http;
    private readonly string _userAgent;
    private Dictionary<string, (long Cik, string Title)>? _tickers;
    private readonly Dictionary<long, JsonDocument> _facts = new();

    /// <param name="userAgent">"Your Company you@example.com" — required by the SEC.</param>
    /// <param name="http">HTTP client (a dedicated one is created when null).</param>
    /// <param name="dataUrl">Base of the XBRL APIs.</param>
    /// <param name="wwwUrl">Base of www.sec.gov (ticker list).</param>
    public SecEdgarProvider(string userAgent, HttpClient? http = null, string dataUrl = "https://data.sec.gov", string wwwUrl = "https://www.sec.gov")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        if (!userAgent.Contains('@')) throw new ArgumentException("The SEC requires a contact e-mail address in the User-Agent.", nameof(userAgent));
        _userAgent = userAgent;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        DataUrl = dataUrl.TrimEnd('/');
        WwwUrl = wwwUrl.TrimEnd('/');
    }

    public string Name => "SEC EDGAR";
    public string DataUrl { get; }
    public string WwwUrl { get; }

    internal static readonly string[] Revenue = { "Revenues", "RevenueFromContractWithCustomerExcludingAssessedTax", "SalesRevenueNet", "RevenueFromContractWithCustomerIncludingAssessedTax" };
    internal static readonly string[] CostOfRevenue = { "CostOfRevenue", "CostOfGoodsAndServicesSold", "CostOfGoodsSold" };

    private void Ua(HttpRequestMessage r) => r.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

    /// <summary>SEC central index key for a ticker.</summary>
    public async Task<long> GetCikAsync(string ticker, CancellationToken cancellationToken = default)
    {
        if (_tickers is null)
        {
            using var doc = await Http.GetJsonAsync(_http, $"{WwwUrl}/files/company_tickers.json", Name, cancellationToken, Ua).ConfigureAwait(false);
            _tickers = ParseTickers(doc.RootElement);
        }
        return _tickers.TryGetValue(ticker.ToUpperInvariant(), out var e) ? e.Cik : throw new MarketDataException($"Ticker '{ticker}' is not in the SEC ticker list.");
    }

    public static Dictionary<string, (long Cik, string Title)> ParseTickers(JsonElement root)
    {
        var map = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in root.EnumerateObject())
            map[p.Value.GetProperty("ticker").GetString()!] = (p.Value.GetProperty("cik_str").GetInt64(), Http.Str(p.Value, "title") ?? "");
        return map;
    }

    private async Task<JsonElement> FactsAsync(string ticker, CancellationToken ct)
    {
        long cik = await GetCikAsync(ticker, ct).ConfigureAwait(false);
        if (!_facts.TryGetValue(cik, out var doc))
        {
            doc = await Http.GetJsonAsync(_http, $"{DataUrl}/api/xbrl/companyfacts/CIK{cik:D10}.json", Name, ct, Ua).ConfigureAwait(false);
            _facts[cik] = doc;
        }
        return doc.RootElement;
    }

    /// <summary>All facts of one concept (e.g. "us-gaap", "Revenues") in a unit (default USD).</summary>
    public async Task<IReadOnlyList<XbrlFact>> GetFactsAsync(string ticker, string taxonomy, string concept, string unit = "USD", CancellationToken cancellationToken = default)
        => ReadFacts(await FactsAsync(ticker, cancellationToken).ConfigureAwait(false), taxonomy, concept, unit);

    public static IReadOnlyList<XbrlFact> ReadFacts(JsonElement companyFacts, string taxonomy, string concept, string unit = "USD")
    {
        if (!companyFacts.GetProperty("facts").TryGetProperty(taxonomy, out var tax) || !tax.TryGetProperty(concept, out var c)
            || !c.GetProperty("units").TryGetProperty(unit, out var arr)) return Array.Empty<XbrlFact>();
        static DateOnly D(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return arr.EnumerateArray().Select(f => new XbrlFact(
            Http.Str(f, "start") is { } s ? D(s) : null, D(Http.Str(f, "end")!), Http.Num(f, "val"), Http.Str(f, "form") ?? "",
            Http.Str(f, "fp") ?? "", f.TryGetProperty("fy", out var fy) && fy.ValueKind == JsonValueKind.Number ? fy.GetInt32() : null,
            D(Http.Str(f, "filed")!))).ToList();
    }

    /// <summary>
    /// Period values across the synonym <paramref name="concepts"/>: duration facts of about a year (annual) or a quarter
    /// (quarterly), or instant facts when <paramref name="instant"/>; keyed by period end. Filers switch concepts over
    /// time (Apple reported "Revenues" until 2018, then RevenueFromContractWithCustomerExcludingAssessedTax), so the
    /// synonyms are merged per period, the latest filing winning (which also picks up restatements).
    /// </summary>
    public static Dictionary<DateOnly, double> Periods(JsonElement companyFacts, IEnumerable<string> concepts, bool quarterly, bool instant)
        => concepts.SelectMany(c => ReadFacts(companyFacts, "us-gaap", c))
            .Where(f => quarterly ? f.Form.StartsWith("10-Q", StringComparison.Ordinal) : f.Form.StartsWith("10-K", StringComparison.Ordinal))
            .Where(f => instant ? f.Start is null : f.Start is { } s && IsPeriod(s, f.End, quarterly))
            .GroupBy(f => f.End)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.Filed).First().Value);

    private static bool IsPeriod(DateOnly start, DateOnly end, bool quarterly)
    {
        int days = end.DayNumber - start.DayNumber;
        return quarterly ? days is >= 80 and <= 100 : days is >= 350 and <= 380;
    }

    public async Task<CompanyOverview> GetOverviewAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var facts = await FactsAsync(symbol, cancellationToken).ConfigureAwait(false);
        var shares = ReadFacts(facts, "dei", "EntityCommonStockSharesOutstanding", "shares").OrderByDescending(f => f.End).FirstOrDefault();
        return new CompanyOverview
        {
            Symbol = symbol.ToUpperInvariant(),
            Name = Http.Str(facts, "entityName"),
            Currency = "USD",
            SharesOutstanding = shares?.Value ?? double.NaN,
        };
    }

    public async Task<IncomeStatement[]> GetIncomeStatementsAsync(string symbol, bool quarterly = false, CancellationToken cancellationToken = default)
        => BuildIncome(await FactsAsync(symbol, cancellationToken).ConfigureAwait(false), quarterly);

    public async Task<BalanceSheet[]> GetBalanceSheetsAsync(string symbol, bool quarterly = false, CancellationToken cancellationToken = default)
        => BuildBalance(await FactsAsync(symbol, cancellationToken).ConfigureAwait(false), quarterly);

    /// <summary>Income statements, newest first, from company facts.</summary>
    public static IncomeStatement[] BuildIncome(JsonElement facts, bool quarterly)
    {
        var revenue = Periods(facts, Revenue, quarterly, false);
        var cost = Periods(facts, CostOfRevenue, quarterly, false);
        var gross = Periods(facts, new[] { "GrossProfit" }, quarterly, false);
        var operating = Periods(facts, new[] { "OperatingIncomeLoss" }, quarterly, false);
        var net = Periods(facts, new[] { "NetIncomeLoss", "ProfitLoss" }, quarterly, false);
        var interest = Periods(facts, new[] { "InterestExpense", "InterestExpenseNonoperating" }, quarterly, false);
        static double V(Dictionary<DateOnly, double> d, DateOnly k) => d.TryGetValue(k, out var v) ? v : double.NaN;
        return revenue.Keys.Union(net.Keys).OrderByDescending(d => d).Select(end => new IncomeStatement
        {
            FiscalDateEnding = end,
            TotalRevenue = V(revenue, end),
            CostOfRevenue = V(cost, end),
            GrossProfit = gross.ContainsKey(end) ? gross[end] : V(revenue, end) - V(cost, end),
            OperatingIncome = V(operating, end),
            NetIncome = V(net, end),
            InterestExpense = V(interest, end),
        }).ToArray();
    }

    /// <summary>Balance sheets, newest first, from company facts.</summary>
    public static BalanceSheet[] BuildBalance(JsonElement facts, bool quarterly)
    {
        var assets = Periods(facts, new[] { "Assets" }, quarterly, true);
        var liabilities = Periods(facts, new[] { "Liabilities" }, quarterly, true);
        var equity = Periods(facts, new[] { "StockholdersEquity", "StockholdersEquityIncludingPortionAttributableToNoncontrollingInterest" }, quarterly, true);
        var currentAssets = Periods(facts, new[] { "AssetsCurrent" }, quarterly, true);
        var currentLiab = Periods(facts, new[] { "LiabilitiesCurrent" }, quarterly, true);
        var cash = Periods(facts, new[] { "CashAndCashEquivalentsAtCarryingValue", "CashCashEquivalentsRestrictedCashAndRestrictedCashEquivalents" }, quarterly, true);
        var inventory = Periods(facts, new[] { "InventoryNet" }, quarterly, true);
        var debt = Periods(facts, new[] { "LongTermDebt", "LongTermDebtNoncurrent" }, quarterly, true);
        static double V(Dictionary<DateOnly, double> d, DateOnly k) => d.TryGetValue(k, out var v) ? v : double.NaN;
        return assets.Keys.OrderByDescending(d => d).Select(end => new BalanceSheet
        {
            FiscalDateEnding = end,
            TotalAssets = V(assets, end),
            TotalLiabilities = liabilities.ContainsKey(end) ? liabilities[end] : V(assets, end) - V(equity, end),
            TotalShareholderEquity = V(equity, end),
            TotalCurrentAssets = V(currentAssets, end),
            TotalCurrentLiabilities = V(currentLiab, end),
            CashAndEquivalents = V(cash, end),
            Inventory = V(inventory, end),
            TotalDebt = V(debt, end),
        }).ToArray();
    }
}
