// Finance.Net quickstart: a short tour of every module, offline and deterministic.
// Usage: dotnet run --project samples/Finance.Quickstart [-- <output-directory>]
using System.Diagnostics;
using System.Text;
using Finance.Backtesting;
using Finance.Backtesting.Strategies;
using Finance.Core;
using Finance.Core.Market;
using Finance.GPU;
using Finance.Mathematics;
using Finance.ML;
using Finance.NewsAnalytics;
using Finance.Portfolio;
using Finance.Pricing.Options;
using Finance.Pricing.Rates;
using Finance.Risk;
using Finance.RustBridge;
using Finance.TimeSeries.Indicators;
using Finance.Visualization;

Console.OutputEncoding = Encoding.UTF8;
string outDir = args.Length > 0 ? args[0] : Path.Combine(Environment.CurrentDirectory, "output");
Directory.CreateDirectory(outDir);
var clock = Stopwatch.StartNew();

Banner();

// ------------------------------------------------------------------ market data + indicators
Section("1", "Market data & indicators", "Finance.Core · Finance.TimeSeries");
var bars = SyntheticMarket.GeometricBrownianBars(756, startPrice: 120, annualDrift: 0.11, annualVolatility: 0.27, seed: 2026);
var close = bars.Select(b => b.Close).ToArray();
var rsi = Ta.Rsi(close, 14);
var macd = Ta.Macd(close);
var bands = Ta.Bollinger(close, 20, 2);
Row("Bars", $"{bars.Length} daily, {bars[0].Time:yyyy-MM-dd} → {bars[^1].Time:yyyy-MM-dd}");
Row("Last close", close[^1].ToString("F2"));
Row("RSI(14) / MACD hist", $"{rsi[^1]:F1} / {macd.Histogram[^1]:+0.000;-0.000}");
Row("Bollinger %B", bands.PercentB[^1].ToString("F2"));
Row("Indicator catalogue", $"{IndicatorCatalog.Count} entries");
Row("Candlestick signals", $"{Candlesticks.Scan(bars).Count} in the history");

// ------------------------------------------------------------------ options
Section("2", "Derivatives pricing", "Finance.Pricing");
var g = BlackScholes.Greeks(OptionType.Call, 100, 105, 0.5, 0.04, 0.01, 0.25);
Row("BSM call 105, 6m, 25%", $"{g.Price:F4}  Δ {g.Delta:F3}  Γ {g.Gamma:F4}  vega/pt {g.VegaPerPoint:F3}");
double american = LatticePricer.Binomial(OptionType.Put, ExerciseStyle.American, 100, 105, 0.5, 0.04, 0.01, 0.25, 800);
Row("American put (CRR 800)", american.ToString("F4"));
var asian = new MonteCarloPricer(100, 0.04, 0.01, 0.25, new MonteCarloSettings(50_000, 126, 7)).AsianArithmetic(OptionType.Call, 100, 0.5);
Row("Asian call (MC + CV)", $"{asian.Price:F4} ± {asian.StandardError:F4}");
var curve = CurveBootstrapper.Bootstrap(new ICurveInstrument[]
{
    new DepositInstrument(0.5, 0.050), new SwapInstrument(2, 0.045), new SwapInstrument(5, 0.042), new SwapInstrument(10, 0.043),
});
var bond = new FixedRateBond(new DateOnly(2025, 1, 15), new DateOnly(2035, 1, 15), 0.045);
var ba = bond.Analytics(0.043, new DateOnly(2025, 3, 17));
Row("10y 4.5% bond @ 4.3%", $"clean {ba.CleanPrice:F3}  mod.dur {ba.ModifiedDuration:F2}  DV01 {ba.Dv01:F4}");
Row("5y par swap rate", curve.ParRate(5).ToString("P3"));

// ------------------------------------------------------------------ portfolio
Section("3", "Portfolio construction", "Finance.Portfolio");
string[] assets = { "Equity", "Tech", "Bonds", "Gold" };
var prices = Matrix.FromRows(SyntheticMarket.CorrelatedPrices(1000, new[] { 0.08, 0.13, 0.035, 0.05 }, new[] { 0.16, 0.28, 0.06, 0.15 },
    new double[,] { { 1, 0.8, -0.2, 0.05 }, { 0.8, 1, -0.25, 0 }, { -0.2, -0.25, 1, 0.3 }, { 0.05, 0, 0.3, 1 } }, seed: 11));
var returns = ExpectedReturns.ReturnsFromPrices(prices);
var mu = ExpectedReturns.MeanHistorical(returns);
var cov = RiskModels.LedoitWolf(returns);
var mvo = new MeanVarianceOptimizer(mu, cov, 0, 0.6, assets);
var maxSharpe = mvo.MaxSharpe(0.02);
Row("Max Sharpe", maxSharpe.Clean().ToString());
Row("  performance", mvo.Performance(maxSharpe, 0.02).ToString());
Row("Risk parity", RiskParity.EqualRiskContribution(cov, assets).Clean().ToString());
Row("HRP", HierarchicalRiskParity.Optimize(cov, assets).Weights.Clean().ToString());

// ------------------------------------------------------------------ risk
Section("4", "Risk", "Finance.Risk");
var daily = close.Skip(1).Select((c, i) => c / close[i] - 1).ToArray();
foreach (var method in new[] { VarMethod.Historical, VarMethod.ParametricNormal, VarMethod.CornishFisher })
{
    var e = ValueAtRisk.Estimate(daily, method, 0.99);
    Row($"VaR 99% {method}", $"{e.ValueAtRisk:P2}  (ES {e.ExpectedShortfall:P2})");
}
var garch = VolatilityModels.FitGarch11(daily);
Row("GARCH(1,1)", $"α {garch.Alpha:F3}  β {garch.Beta:F3}  long-run vol {garch.LongRunVolatility():P1}");
var worst = StressTester.RunAll(new[] { StressPosition.Equity("Equities", 1_000_000), StressPosition.Bond("Bonds", 500_000, 6.5) })[0];
Row("Worst stress scenario", $"{worst.Scenario}: {worst.TotalPnl:N0}");

// ------------------------------------------------------------------ backtest
Section("5", "Backtesting", "Finance.Backtesting");
var engine = new BacktestEngine(new BacktestSettings { Commission = CommissionModels.PerShare(0.005, 1), Slippage = SlippageModels.FixedBps(2) });
var sma = engine.Run(new SmaCrossoverStrategy(20, 100), "DEMO", bars);
var hold = engine.Run(new BuyAndHoldStrategy(), "DEMO", bars);
Row("SMA 20/100", Short(sma.Statistics));
Row("Buy & hold", Short(hold.Statistics));

// ------------------------------------------------------------------ ML
Section("6", "Machine learning", "Finance.ML");
var features = FeatureEngineering.FromBars(bars);
var fwd = FeatureEngineering.ForwardReturns(bars, features);
var rows = Enumerable.Range(0, features.Rows).Where(i => double.IsFinite(fwd[i])).ToArray();
var x = features.SelectRows(rows).Values;
var y = rows.Select(i => fwd[i] > 0 ? 1.0 : 0.0).ToArray();
var auc = CrossValidation.Evaluate(() => new RandomForestClassifier(40, new TreeOptions { MaxDepth = 4, MinSamplesLeaf = 20 }).AsProbabilityRegressor(), x, y,
    CrossValidation.PurgedKFold(x.Rows, 5), (t, p) => Metrics.RocAuc(t, p));
Row("Features", $"{x.Rows} × {x.Columns} ({string.Join(", ", features.Names.Take(5))}, …)");
Row("Random forest, purged CV AUC", $"{auc.Average():F3} (≈0.5 on a random walk, as it should be)");

// ------------------------------------------------------------------ NLP
Section("7", "News sentiment", "Finance.NewsAnalytics");
var lexicon = new LexiconSentimentModel();
foreach (var h in new[] { "Nvidia beats estimates and raises full-year guidance", "Regulators sue the bank over alleged fraud", "Company completes routine board meeting" })
{
    var s = lexicon.Score(h);
    Row($"{s.Score:+0.00;-0.00; 0.00} {s.Label}", $"{h}  [{EventClassifier.Classify(h)}]");
}

// ------------------------------------------------------------------ acceleration
Section("8", "Acceleration", "Finance.RustBridge · Finance.GPU");
Row("Rust", RustAccelerator.Describe());
try
{
    using var gpu = GpuContext.CreateBest();
    var mc = new GpuOptionPricer(gpu).MonteCarloEuropean(100, 105, 0.5, 0.04, 0.01, 0.25, true, 1_000_000);
    Row("GPU", $"{gpu.Device.Name} [{gpu.Backend}] MC call {mc.Price:F4} ± {mc.HalfWidth95:F4} in {mc.Elapsed.TotalMilliseconds:F0} ms (BSM {g.Price:F4})");
}
catch (Exception ex)
{
    Row("GPU", "unavailable: " + ex.Message);
}

// ------------------------------------------------------------------ report
Section("9", "Tearsheet", "Finance.Visualization");
var report = Tearsheet.Build("Finance.Net Quickstart — SMA 20/100", sma.Times[1..], sma.Returns(), hold.Returns());
string reportPath = Path.Combine(outDir, "quickstart_tearsheet.html");
report.Save(reportPath);
Charts.Save(Charts.Candlestick(bars[^120..], new ChartOptions { Title = "DEMO — last 120 sessions" },
    new[] { new Finance.Visualization.LineSeries("Upper", bands.Upper[^120..]), new Finance.Visualization.LineSeries("Lower", bands.Lower[^120..]) }), Path.Combine(outDir, "quickstart_candles.svg"));
Row("HTML tearsheet", reportPath);
Row("SVG chart", Path.Combine(outDir, "quickstart_candles.svg"));

Console.WriteLine();
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine($"  Done in {clock.Elapsed.TotalSeconds:F1}s · {FinanceInfo.CreditIndonesian} · {FinanceInfo.CreditEnglish}");
Console.ResetColor();
return 0;

static string Short(PerformanceStatistics s) => $"return {s.TotalReturn:P1}  Sharpe {s.SharpeRatio:F2}  maxDD {s.MaxDrawdown:P1}  trades {s.NumberOfTrades}";

static void Banner()
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine();
    Console.WriteLine("  ╔══════════════════════════════════════════════════════════════╗");
    Console.WriteLine($"  ║  {FinanceInfo.Product} {FinanceInfo.Version} — quantitative finance for .NET 10".PadRight(65) + "║");
    Console.WriteLine($"  ║  {FinanceInfo.CreditIndonesian}".PadRight(65) + "║");
    Console.WriteLine("  ╚══════════════════════════════════════════════════════════════╝");
    Console.ResetColor();
}

static void Section(string number, string title, string modules)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.Write($"  {number}. {title}");
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"   ({modules})");
    Console.ResetColor();
}

static void Row(string label, string value)
{
    Console.ForegroundColor = ConsoleColor.Gray;
    Console.Write($"     {label,-30}");
    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine(value);
    Console.ResetColor();
}
