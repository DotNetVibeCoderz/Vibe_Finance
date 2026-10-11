using System.Net;
using System.Text;

namespace Finance.Data.Tests;

/// <summary>Routes requests to canned responses by URL substring and records what was asked.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly List<(string Match, HttpStatusCode Code, string Body)> _routes = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>Request bodies, captured before the request is disposed (null for requests without content).</summary>
    public List<string?> Bodies { get; } = new();

    public FakeHandler On(string urlContains, string body, HttpStatusCode code = HttpStatusCode.OK)
    {
        _routes.Add((urlContains, code, body));
        return this;
    }

    /// <summary>Like <see cref="On"/>, but takes precedence over earlier routes (for responses that change over time).</summary>
    public FakeHandler Override(string urlContains, string body, HttpStatusCode code = HttpStatusCode.OK)
    {
        _routes.Insert(0, (urlContains, code, body));
        return this;
    }

    public HttpClient Client() => new(this);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
        var url = request.RequestUri!.ToString();
        foreach (var (m, code, body) in _routes)
            if (url.Contains(m, StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"no route\"}") });
    }
}

/// <summary>Live tests run only when FINANCENET_LIVE=1.</summary>
public static class Live
{
    public static bool Enabled => Environment.GetEnvironmentVariable("FINANCENET_LIVE") == "1";
}

/// <summary>Payloads recorded from the real APIs (trimmed).</summary>
public static class Payloads
{
    public const string YahooChart = """
    {"chart":{"result":[{"meta":{"currency":"USD","symbol":"AAPL","exchangeName":"NMS","regularMarketTime":1791576000,"regularMarketPrice":336.64,"regularMarketVolume":37821157,"chartPreviousClose":333.69,"dataGranularity":"1d","range":"5d"},
    "timestamp":[1791207000,1791293400,1791379800,1791466200,1791552600],
    "indicators":{"quote":[{"low":[331.6499938964844,330.6199951171875,332.7799987792969,335.8999938964844,330.70001220703125],
    "open":[332.82000732421875,332.2799987792969,336.9599914550781,336.82000732421875,331.70001220703125],
    "volume":[34400900,30449000,34147900,35332400,37855000],
    "high":[336.2099914550781,334.3800048828125,338.6700134277344,341.57000732421875,338.6099853515625],
    "close":[333.69,333.1,null,340.98,336.64]}],
    "adjclose":[{"adjclose":[333.0,333.1,null,340.98,336.64]}]}}],"error":null}}
    """;

    public const string YahooError = """{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found, symbol may be delisted"}}}""";

    public const string BinanceKlines = """
    [[1791504000000,"81754.46000000","83528.98000000","81603.52000000","82635.55000000","12301.61263000",1791590399999,"1016094089.65125780",2142080,"6834.92850000","564632935.49658460","0"],
     [1791590400000,"82635.56000000","82794.43000000","82545.82000000","82775.06000000","1532.67287000",1791676799999,"126657503.86577410",125814,"755.71465000","62454252.88366250","0"]]
    """;

    public const string BinanceDepth = """{"lastUpdateId":101262622089,"bids":[["82775.05000000","9.77439000"],["82775.04000000","0.30207000"]],"asks":[["82775.06000000","1.04327000"],["82775.07000000","0.00062000"]]}""";

    public const string BinanceTicker = """{"symbol":"BTCUSDT","priceChange":"281.50000000","priceChangePercent":"0.341","lastPrice":"82775.06000000","bidPrice":"82775.05000000","bidQty":"9.77439000","askPrice":"82775.06000000","askQty":"1.04327000","volume":"10970.00907000","openTime":1791524783012,"closeTime":1791611183012}""";

    public const string CoinbaseCandles = """[[1700006400,36500.1,37200.5,36700.0,37000.2,1234.5],[1699920000,36000.0,36900.0,36100.0,36700.0,1500.25]]""";

    public const string CoinbaseTicker = """{"ask":"37001.10","bid":"37000.90","volume":"15234.12","trade_id":580123,"price":"37001.00","size":"0.01","time":"2023-11-15T12:00:00.123456Z"}""";

    public const string AlphaDaily = """
    {"Meta Data":{"1. Information":"Daily Prices (open, high, low, close) and Volumes","2. Symbol":"IBM"},
     "Time Series (Daily)":{"2024-01-03":{"1. open":"161.0000","2. high":"161.7300","3. low":"160.0800","4. close":"160.1000","5. volume":"4086131"},
                            "2024-01-02":{"1. open":"162.8300","2. high":"163.2900","3. low":"160.7100","4. close":"161.5000","5. volume":"3778063"}}}
    """;

    public const string AlphaQuote = """{"Global Quote":{"01. symbol":"IBM","02. open":"161.0000","03. high":"161.7300","04. low":"160.0800","05. price":"160.1000","06. volume":"4086131","07. latest trading day":"2024-01-03","08. previous close":"161.5000","09. change":"-1.4000","10. change percent":"-0.8669%"}}""";

    public const string AlphaGdp = """{"name":"Real Gross Domestic Product","interval":"quarterly","unit":"billions of dollars","data":[{"date":"2024-04-01","value":"5765.2"},{"date":"2024-01-01","value":"5667.0"},{"date":"2023-10-01","value":"."}]}""";

    public const string AlphaOverview = """{"Symbol":"IBM","Name":"International Business Machines","Exchange":"NYSE","Currency":"USD","Sector":"TECHNOLOGY","Industry":"COMPUTER & OFFICE EQUIPMENT","MarketCapitalization":"152000000000","PERatio":"22.5","PEGRatio":"4.1","BookValue":"24.95","DividendYield":"0.04","EPS":"7.4","ProfitMargin":"0.12","ReturnOnEquityTTM":"0.36","Beta":"0.71","52WeekHigh":"170.0","52WeekLow":"120.0","PriceToBookRatio":"6.7","SharesOutstanding":"916000000"}""";

    public const string AlphaIncome = """{"symbol":"IBM","annualReports":[{"fiscalDateEnding":"2023-12-31","totalRevenue":"61860000000","costOfRevenue":"27560000000","grossProfit":"34300000000","operatingIncome":"8900000000","netIncome":"7502000000","ebitda":"14000000000","interestExpense":"1600000000"},{"fiscalDateEnding":"2022-12-31","totalRevenue":"60530000000","grossProfit":"32690000000","operatingIncome":"7800000000","netIncome":"1640000000","interestExpense":"1216000000"}],"quarterlyReports":[]}""";

    public const string AlphaBalance = """{"symbol":"IBM","annualReports":[{"fiscalDateEnding":"2023-12-31","totalAssets":"135241000000","totalLiabilities":"112628000000","totalShareholderEquity":"22533000000","totalCurrentAssets":"32908000000","totalCurrentLiabilities":"34122000000","cashAndCashEquivalentsAtCarryingValue":"13068000000","inventory":"1161000000","shortLongTermDebtTotal":"56548000000"}],"quarterlyReports":[]}""";

    public const string Polygon = """{"ticker":"AAPL","queryCount":2,"resultsCount":2,"adjusted":true,"results":[{"v":70790813,"vw":131.6292,"o":130.465,"c":130.15,"h":133.41,"l":129.89,"t":1673240400000,"n":645365},{"v":63896155,"vw":129.822,"o":130.26,"c":130.73,"h":131.2636,"l":128.12,"t":1673326800000,"n":554940}],"status":"OK","request_id":"abc","count":2}""";

    public const string Reddit = """
    {"kind":"Listing","data":{"after":"t3_x","children":[
     {"kind":"t3","data":{"id":"a1","subreddit":"stocks","author":"alice","title":"$NVDA breakout, buying calls 🚀","selftext":"","created_utc":1700000000.0,"score":120,"num_comments":45}},
     {"kind":"t3","data":{"id":"a2","subreddit":"stocks","author":"bob","title":"TSLA looks overvalued","selftext":"Expecting a dump after earnings miss","created_utc":1700003600.0,"score":10,"num_comments":3}}]}}
    """;

    public const string OpenMeteo = """{"latitude":40.710335,"longitude":-73.99308,"daily_units":{"time":"iso8601"},"daily":{"time":["2026-10-08","2026-10-09","2026-10-10"],"temperature_2m_max":[24.9,23.5,21.7],"temperature_2m_min":[13.3,11.0,9.8],"temperature_2m_mean":[17.7,16.9,null],"precipitation_sum":[0.0,1.2,null]}}""";
}
