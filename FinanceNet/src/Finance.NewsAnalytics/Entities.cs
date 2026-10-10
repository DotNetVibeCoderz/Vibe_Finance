using System.Text.RegularExpressions;

namespace Finance.NewsAnalytics;

/// <summary>Kinds of corporate events detected in news.</summary>
[Flags]
public enum NewsEventType
{
    None = 0,
    Earnings = 1,
    MergerAcquisition = 2,
    Guidance = 4,
    AnalystUpgrade = 8,
    AnalystDowngrade = 16,
    LegalRegulatory = 32,
    ManagementChange = 64,
    Dividend = 128,
    Buyback = 256,
    ProductLaunch = 512,
    Macro = 1024,
}

/// <summary>
/// Extracts tickers from text: cashtags ($AAPL), exchange-prefixed symbols (NASDAQ: AAPL), and
/// company names from a dictionary (case-insensitive, word-bounded).
/// </summary>
public sealed partial class EntityExtractor
{
    private readonly List<(Regex Pattern, string Ticker)> _names = new();

    /// <summary>A starter dictionary of large-cap company names (US and Indonesia).</summary>
    public static IReadOnlyDictionary<string, string> DefaultCompanies { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Apple"] = "AAPL", ["Microsoft"] = "MSFT", ["Alphabet"] = "GOOGL", ["Google"] = "GOOGL", ["Amazon"] = "AMZN", ["Meta Platforms"] = "META",
        ["Facebook"] = "META", ["Nvidia"] = "NVDA", ["Tesla"] = "TSLA", ["Berkshire Hathaway"] = "BRK-B", ["JPMorgan"] = "JPM", ["JP Morgan"] = "JPM",
        ["Goldman Sachs"] = "GS", ["Morgan Stanley"] = "MS", ["Bank of America"] = "BAC", ["Wells Fargo"] = "WFC", ["Citigroup"] = "C",
        ["Visa"] = "V", ["Mastercard"] = "MA", ["Netflix"] = "NFLX", ["Intel"] = "INTC", ["AMD"] = "AMD", ["Advanced Micro Devices"] = "AMD",
        ["Broadcom"] = "AVGO", ["Oracle"] = "ORCL", ["Salesforce"] = "CRM", ["Adobe"] = "ADBE", ["IBM"] = "IBM", ["Qualcomm"] = "QCOM",
        ["Exxon"] = "XOM", ["ExxonMobil"] = "XOM", ["Chevron"] = "CVX", ["Pfizer"] = "PFE", ["Johnson & Johnson"] = "JNJ", ["Eli Lilly"] = "LLY",
        ["Walmart"] = "WMT", ["Costco"] = "COST", ["Coca-Cola"] = "KO", ["PepsiCo"] = "PEP", ["McDonald's"] = "MCD", ["Disney"] = "DIS",
        ["Boeing"] = "BA", ["Nike"] = "NKE", ["Starbucks"] = "SBUX", ["Uber"] = "UBER", ["PayPal"] = "PYPL", ["Coinbase"] = "COIN",
        ["Bank Central Asia"] = "BBCA.JK", ["Bank Rakyat Indonesia"] = "BBRI.JK", ["Bank Mandiri"] = "BMRI.JK", ["Telkom Indonesia"] = "TLKM.JK",
        ["Astra International"] = "ASII.JK", ["GoTo"] = "GOTO.JK", ["Bitcoin"] = "BTC-USD", ["Ethereum"] = "ETH-USD",
    };

    public EntityExtractor(IReadOnlyDictionary<string, string>? companies = null)
    {
        foreach (var (name, ticker) in companies ?? DefaultCompanies) AddCompany(name, ticker);
    }

    /// <summary>Adds a company-name → ticker mapping.</summary>
    public void AddCompany(string name, string ticker)
        => _names.Add((new Regex($@"(?<![\w]){Regex.Escape(name)}(?![\w])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), ticker.ToUpperInvariant()));

    /// <summary>Distinct tickers mentioned in <paramref name="text"/>, in order of first appearance.</summary>
    public IReadOnlyList<string> ExtractTickers(string text)
    {
        var found = new List<(int Pos, string Ticker)>();
        foreach (Match m in Cashtag().Matches(text)) found.Add((m.Index, m.Groups[1].Value.ToUpperInvariant()));
        foreach (Match m in ExchangeSymbol().Matches(text)) found.Add((m.Index, m.Groups[2].Value.ToUpperInvariant()));
        foreach (var (pattern, ticker) in _names)
        {
            var m = pattern.Match(text);
            if (m.Success) found.Add((m.Index, ticker));
        }
        return found.OrderBy(f => f.Pos).Select(f => f.Ticker).Distinct().ToList();
    }

    /// <summary>Returns a copy of the item with tickers filled in (merged with any it already has).</summary>
    public NewsItem Tag(NewsItem item) => item with { Tickers = item.Tickers.Concat(ExtractTickers(item.Text)).Distinct().ToList() };

    [GeneratedRegex(@"\$([A-Za-z]{1,6}(?:\.[A-Za-z]{1,3})?)\b")]
    private static partial Regex Cashtag();

    [GeneratedRegex(@"\b(NASDAQ|NYSE|AMEX|IDX|LSE|TSX)\s*:\s*([A-Z]{1,6}(?:\.[A-Z]{1,3})?)\b")]
    private static partial Regex ExchangeSymbol();
}

/// <summary>Rule-based corporate event classifier.</summary>
public static partial class EventClassifier
{
    private static readonly (NewsEventType Type, Regex Pattern)[] Rules =
    {
        (NewsEventType.Earnings, R(@"\b(earnings|quarterly results|q[1-4]\b|eps|revenue|profit|net income|beats? estimates|miss(es|ed)? estimates|results)\b")),
        (NewsEventType.MergerAcquisition, R(@"\b(acquir\w*|acquisition|merger|merge[sd]?|takeover|buyout|to buy|deal to buy|bid for|stake in)\b")),
        (NewsEventType.Guidance, R(@"\b(guidance|outlook|forecast|raises? (its )?(full[- ]year )?(forecast|outlook)|cuts? (its )?(forecast|outlook)|sees .* (revenue|earnings))\b")),
        (NewsEventType.AnalystUpgrade, R(@"\b(upgrad\w*|raises? (price )?target|price target (raised|hike)|initiat\w* .* buy|to (buy|outperform|overweight))\b")),
        (NewsEventType.AnalystDowngrade, R(@"\b(downgrad\w*|cuts? (price )?target|price target (cut|lowered)|to (sell|underperform|underweight))\b")),
        (NewsEventType.LegalRegulatory, R(@"\b(lawsuit|sued|sues|court|settlement|antitrust|regulator\w*|sec\b|doj|probe|investigation|fine[sd]?|penalt\w*|ban(ned)?)\b")),
        (NewsEventType.ManagementChange, R(@"\b(ceo|cfo|chief executive|chairman|resign\w*|steps? down|appoint\w*|names? new|successor|ousted)\b")),
        (NewsEventType.Dividend, R(@"\b(dividend\w*|payout)\b")),
        (NewsEventType.Buyback, R(@"\b(buyback|share repurchase|repurchase program|buy back)\b")),
        (NewsEventType.ProductLaunch, R(@"\b(launch\w*|unveil\w*|introduc\w* new|new product|rolls? out|release[sd]? new)\b")),
        (NewsEventType.Macro, R(@"\b(fed|federal reserve|interest rates?|inflation|cpi|gdp|jobs report|payrolls|central bank|bank indonesia|tariff\w*)\b")),
    };

    /// <summary>All event types whose rules match.</summary>
    public static NewsEventType Classify(string text)
    {
        var t = NewsEventType.None;
        foreach (var (type, pattern) in Rules) if (pattern.IsMatch(text)) t |= type;
        return t;
    }

    /// <summary>Individual flags set in <paramref name="value"/>.</summary>
    public static IEnumerable<NewsEventType> Flags(NewsEventType value)
        => Enum.GetValues<NewsEventType>().Where(f => f != NewsEventType.None && value.HasFlag(f));

    private static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
