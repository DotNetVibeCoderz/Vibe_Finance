using Finance.Core.Market;
using Finance.DataFrame;

namespace Finance.TimeSeries.Indicators;

/// <summary>Indicator family, used for grouping in UIs.</summary>
public enum IndicatorCategory { Trend, Momentum, Volatility, Volume, Statistics, PriceTransform, Pattern }

/// <summary>A numeric indicator parameter with default and suggested range.</summary>
public sealed record IndicatorParameter(string Name, double Default, double Min, double Max, string Description = "")
{
    /// <summary>True for parameters that must be whole numbers (periods).</summary>
    public bool IsInteger { get; init; } = true;
}

/// <summary>One named output of an indicator, aligned with the input bars.</summary>
public sealed record IndicatorSeries(string Name, double[] Values);

/// <summary>Input given to a catalog compute delegate.</summary>
public sealed class IndicatorInput
{
    private readonly IReadOnlyDictionary<string, double> _values;

    internal IndicatorInput(OhlcvFrame frame, Bar[] bars, IReadOnlyDictionary<string, double> values)
    {
        Frame = frame;
        Bars = bars;
        _values = values;
    }

    public OhlcvFrame Frame { get; }
    public Bar[] Bars { get; }
    public double[] Open => Frame.Open;
    public double[] High => Frame.High;
    public double[] Low => Frame.Low;
    public double[] Close => Frame.Close;
    public double[] Volume => Frame.Volume;

    /// <summary>Parameter value (double).</summary>
    public double P(string name) => _values[name];

    /// <summary>Parameter value rounded to an integer (periods).</summary>
    public int N(string name) => Math.Max(1, (int)Math.Round(_values[name]));
}

/// <summary>Metadata plus compute delegate for a catalogued indicator.</summary>
public sealed class IndicatorDefinition
{
    private readonly Func<IndicatorInput, IndicatorSeries[]> _compute;

    internal IndicatorDefinition(string id, string name, IndicatorCategory category, string description, bool overlay,
        IndicatorParameter[] parameters, Func<IndicatorInput, IndicatorSeries[]> compute)
    {
        Id = id;
        Name = name;
        Category = category;
        Description = description;
        Overlay = overlay;
        Parameters = parameters;
        _compute = compute;
    }

    /// <summary>Short identifier, e.g. "RSI".</summary>
    public string Id { get; }

    /// <summary>Display name.</summary>
    public string Name { get; }
    public IndicatorCategory Category { get; }
    public string Description { get; }

    /// <summary>True when the output is plotted on the price chart (moving averages, bands) rather than in a separate pane.</summary>
    public bool Overlay { get; }
    public IReadOnlyList<IndicatorParameter> Parameters { get; }

    /// <summary>Computes the indicator; missing parameters take their defaults.</summary>
    public IndicatorSeries[] Compute(OhlcvFrame frame, IReadOnlyDictionary<string, double>? parameters = null)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Parameters) values[p.Name] = parameters is not null && parameters.TryGetValue(p.Name, out var v) ? v : p.Default;
        return _compute(new IndicatorInput(frame, frame.ToBars(), values));
    }

    /// <summary>Computes the indicator from bars.</summary>
    public IndicatorSeries[] Compute(Bar[] bars, IReadOnlyDictionary<string, double>? parameters = null)
        => Compute(OhlcvFrame.FromBars(bars), parameters);

    public override string ToString() => $"{Id} - {Name} ({Category})";
}

/// <summary>
/// Registry of every indicator in <see cref="Ta"/> and <see cref="Candlesticks"/> with metadata,
/// for UIs (the Gallery's Indicator Explorer) and scripting. <see cref="Count"/> is the real number of entries.
/// </summary>
public static class IndicatorCatalog
{
    private static readonly List<IndicatorDefinition> _all = new();
    private static readonly Dictionary<string, IndicatorDefinition> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All definitions, in registration order.</summary>
    public static IReadOnlyList<IndicatorDefinition> All => _all;

    /// <summary>Number of catalogued indicators.</summary>
    public static int Count => _all.Count;

    /// <summary>Looks up a definition by id (case-insensitive).</summary>
    public static IndicatorDefinition Get(string id) => _byId.TryGetValue(id, out var d) ? d : throw new KeyNotFoundException($"Unknown indicator '{id}'.");

    public static bool TryGet(string id, out IndicatorDefinition? definition) => _byId.TryGetValue(id, out definition);

    /// <summary>Definitions in one category.</summary>
    public static IEnumerable<IndicatorDefinition> ByCategory(IndicatorCategory category) => _all.Where(d => d.Category == category);

    private static IndicatorParameter Per(string name = "period", double def = 14, double min = 2, double max = 400, string desc = "Look-back period")
        => new(name, def, min, max, desc);

    private static IndicatorParameter Real(string name, double def, double min, double max, string desc = "")
        => new(name, def, min, max, desc) { IsInteger = false };

    private static IndicatorSeries S(string name, double[] values) => new(name, values);

    private static void Add(string id, string name, IndicatorCategory cat, string desc, bool overlay, IndicatorParameter[] ps, Func<IndicatorInput, IndicatorSeries[]> f)
    {
        var d = new IndicatorDefinition(id, name, cat, desc, overlay, ps, f);
        _all.Add(d);
        _byId.Add(id, d);
    }

    private static void One(string id, string name, IndicatorCategory cat, string desc, bool overlay, IndicatorParameter[] ps, Func<IndicatorInput, double[]> f)
        => Add(id, name, cat, desc, overlay, ps, i => new[] { S(id, f(i)) });

    static IndicatorCatalog()
    {
        const IndicatorCategory T = IndicatorCategory.Trend, M = IndicatorCategory.Momentum, V = IndicatorCategory.Volatility,
            Vo = IndicatorCategory.Volume, St = IndicatorCategory.Statistics, Pt = IndicatorCategory.PriceTransform;
        var none = Array.Empty<IndicatorParameter>();

        // Trend / overlap
        One("SMA", "Simple Moving Average", T, "Arithmetic mean of the last n closes.", true, new[] { Per(def: 20) }, i => Ta.Sma(i.Close, i.N("period")));
        One("EMA", "Exponential Moving Average", T, "EMA with α = 2/(n+1), SMA-seeded.", true, new[] { Per(def: 20) }, i => Ta.Ema(i.Close, i.N("period")));
        One("WMA", "Weighted Moving Average", T, "Linearly weighted mean (weights 1..n).", true, new[] { Per(def: 20) }, i => Ta.Wma(i.Close, i.N("period")));
        One("RMA", "Wilder Smoothed MA (SMMA)", T, "Wilder smoothing, α = 1/n.", true, new[] { Per(def: 14) }, i => Ta.Rma(i.Close, i.N("period")));
        One("DEMA", "Double EMA", T, "2·EMA − EMA(EMA): reduced lag.", true, new[] { Per(def: 20) }, i => Ta.Dema(i.Close, i.N("period")));
        One("TEMA", "Triple EMA", T, "3·EMA − 3·EMA² + EMA³.", true, new[] { Per(def: 20) }, i => Ta.Tema(i.Close, i.N("period")));
        One("TRIMA", "Triangular MA", T, "SMA of an SMA: smooth, centre-weighted.", true, new[] { Per(def: 20) }, i => Ta.Trima(i.Close, i.N("period")));
        One("HMA", "Hull Moving Average", T, "WMA(2·WMA(n/2) − WMA(n), √n).", true, new[] { Per(def: 20) }, i => Ta.Hma(i.Close, i.N("period")));
        One("KAMA", "Kaufman Adaptive MA", T, "Adapts its speed to the efficiency ratio.", true, new[] { Per(def: 10), Per("fast", 2, 1, 50, "Fast period"), Per("slow", 30, 2, 200, "Slow period") },
            i => Ta.Kama(i.Close, i.N("period"), i.N("fast"), i.N("slow")));
        One("T3", "Tillson T3", T, "Six chained EMAs with a volume factor.", true, new[] { Per(def: 5), Real("vfactor", 0.7, 0, 1, "Volume factor") }, i => Ta.T3(i.Close, i.N("period"), i.P("vfactor")));
        One("VWMA", "Volume-Weighted MA", T, "Σ(price·volume)/Σvolume over n bars.", true, new[] { Per(def: 20) }, i => Ta.Vwma(i.Close, i.Volume, i.N("period")));
        One("ZLEMA", "Zero-Lag EMA", T, "EMA of a de-lagged price.", true, new[] { Per(def: 20) }, i => Ta.Zlema(i.Close, i.N("period")));
        One("ALMA", "Arnaud Legoux MA", T, "Gaussian-weighted MA with offset.", true, new[] { Per(def: 9), Real("offset", 0.85, 0, 1), Real("sigma", 6, 1, 20) },
            i => Ta.Alma(i.Close, i.N("period"), i.P("offset"), i.P("sigma")));
        One("MCGINLEY", "McGinley Dynamic", T, "Self-adjusting MA that tracks price speed.", true, new[] { Per(def: 14) }, i => Ta.McGinley(i.Close, i.N("period")));
        One("VIDYA", "Variable Index Dynamic Average", T, "EMA scaled by |CMO|.", true, new[] { Per(def: 14), Per("cmo", 9, 2, 100, "CMO period") }, i => Ta.Vidya(i.Close, i.N("period"), i.N("cmo")));
        One("MIDPOINT", "Midpoint", T, "(highest close + lowest close)/2.", true, new[] { Per(def: 14) }, i => Ta.Midpoint(i.Close, i.N("period")));
        One("MIDPRICE", "Mid Price", T, "(highest high + lowest low)/2.", true, new[] { Per(def: 14) }, i => Ta.MidPrice(i.High, i.Low, i.N("period")));
        Add("ENV", "MA Envelopes", T, "SMA ± a fixed percentage.", true, new[] { Per(def: 20), Real("percent", 2.5, 0.1, 20) }, i =>
        {
            var (u, m, l) = Ta.Envelopes(i.Close, i.N("period"), i.P("percent"));
            return new[] { S("Upper", u), S("Middle", m), S("Lower", l) };
        });
        Add("ICHIMOKU", "Ichimoku Cloud", T, "Tenkan, Kijun, Senkou A/B and Chikou lines.", true, new[] { Per("tenkan", 9), Per("kijun", 26), Per("senkouB", 52) }, i =>
        {
            var r = Ta.Ichimoku(i.High, i.Low, i.Close, i.N("tenkan"), i.N("kijun"), i.N("senkouB"));
            return new[] { S("Tenkan", r.Tenkan), S("Kijun", r.Kijun), S("SenkouA", r.SenkouA), S("SenkouB", r.SenkouB), S("Chikou", r.Chikou) };
        });
        One("PSAR", "Parabolic SAR", T, "Wilder's stop-and-reverse trailing stop.", true, new[] { Real("step", 0.02, 0.001, 0.2), Real("max", 0.2, 0.01, 1) }, i => Ta.ParabolicSar(i.High, i.Low, i.P("step"), i.P("max")));
        Add("SUPERTREND", "Supertrend", T, "ATR-band trend follower with direction.", true, new[] { Per(def: 10), Real("multiplier", 3, 0.5, 10) }, i =>
        {
            var (line, dir) = Ta.Supertrend(i.High, i.Low, i.Close, i.N("period"), i.P("multiplier"));
            return new[] { S("Supertrend", line), S("Direction", dir) };
        });
        Add("DMI", "Directional Movement Index", T, "+DI, −DI and ADX (Wilder).", false, new[] { Per(def: 14) }, i =>
        {
            var r = Ta.Dmi(i.High, i.Low, i.Close, i.N("period"));
            return new[] { S("+DI", r.PlusDi), S("-DI", r.MinusDi), S("ADX", r.Adx) };
        });
        One("ADX", "Average Directional Index", T, "Trend strength (0-100).", false, new[] { Per(def: 14) }, i => Ta.Adx(i.High, i.Low, i.Close, i.N("period")));
        One("ADXR", "ADX Rating", T, "Average of ADX now and n−1 bars ago.", false, new[] { Per(def: 14) }, i => Ta.Dmi(i.High, i.Low, i.Close, i.N("period")).Adxr);
        One("DX", "Directional Movement (DX)", T, "Unsmoothed directional index.", false, new[] { Per(def: 14) }, i => Ta.Dmi(i.High, i.Low, i.Close, i.N("period")).Dx);
        Add("AROON", "Aroon", T, "Bars since highest high / lowest low.", false, new[] { Per(def: 25) }, i =>
        {
            var r = Ta.Aroon(i.High, i.Low, i.N("period"));
            return new[] { S("Up", r.Up), S("Down", r.Down) };
        });
        One("AROONOSC", "Aroon Oscillator", T, "Aroon up − Aroon down.", false, new[] { Per(def: 25) }, i => Ta.Aroon(i.High, i.Low, i.N("period")).Oscillator);
        Add("VORTEX", "Vortex Indicator", T, "+VI and −VI trend direction lines.", false, new[] { Per(def: 14) }, i =>
        {
            var (p, m) = Ta.Vortex(i.High, i.Low, i.Close, i.N("period"));
            return new[] { S("+VI", p), S("-VI", m) };
        });
        Add("LINREG", "Linear Regression", T, "End point of a rolling least-squares line.", true, new[] { Per(def: 14) }, i => new[] { S("LinReg", Ta.LinearRegression(i.Close, i.N("period")).Value) });
        One("LINREGSLOPE", "Linear Regression Slope", T, "Slope of the rolling regression line.", false, new[] { Per(def: 14) }, i => Ta.LinearRegression(i.Close, i.N("period")).Slope);
        One("LINREGINTERCEPT", "Linear Regression Intercept", T, "Intercept of the rolling regression line.", true, new[] { Per(def: 14) }, i => Ta.LinearRegression(i.Close, i.N("period")).Intercept);
        One("LINREGANGLE", "Linear Regression Angle", T, "Slope expressed in degrees.", false, new[] { Per(def: 14) }, i => Ta.LinearRegression(i.Close, i.N("period")).Angle);
        One("TSF", "Time Series Forecast", T, "One-step-ahead regression forecast.", true, new[] { Per(def: 14) }, i => Ta.LinearRegression(i.Close, i.N("period")).Forecast);
        Add("PIVOTS", "Pivot Points (Classic)", T, "Floor-trader pivot, supports and resistances.", true, none, i =>
        {
            var r = Ta.PivotPoints(i.High, i.Low, i.Close);
            return new[] { S("Pivot", r.Pivot), S("R1", r.R1), S("S1", r.S1), S("R2", r.R2), S("S2", r.S2) };
        });
        Add("PIVOTSFIB", "Pivot Points (Fibonacci)", T, "Pivot levels at Fibonacci ratios of the prior range.", true, none, i =>
        {
            var r = Ta.PivotPoints(i.High, i.Low, i.Close, PivotMethod.Fibonacci);
            return new[] { S("Pivot", r.Pivot), S("R1", r.R1), S("S1", r.S1), S("R2", r.R2), S("S2", r.S2) };
        });
        Add("PIVOTSCAM", "Pivot Points (Camarilla)", T, "Camarilla levels around the prior close.", true, none, i =>
        {
            var r = Ta.PivotPoints(i.High, i.Low, i.Close, PivotMethod.Camarilla);
            return new[] { S("R1", r.R1), S("S1", r.S1), S("R2", r.R2), S("S2", r.S2), S("R3", r.R3), S("S3", r.S3) };
        });

        // Momentum
        One("RSI", "Relative Strength Index", M, "Wilder RSI (0-100).", false, new[] { Per(def: 14) }, i => Ta.Rsi(i.Close, i.N("period")));
        Add("STOCHRSI", "Stochastic RSI", M, "Stochastic of RSI with %K/%D smoothing.", false, new[] { Per("rsi", 14), Per("stoch", 14), Per("k", 3, 1, 20), Per("d", 3, 1, 20) }, i =>
        {
            var (k, d) = Ta.StochRsi(i.Close, i.N("rsi"), i.N("stoch"), i.N("k"), i.N("d"));
            return new[] { S("%K", k), S("%D", d) };
        });
        Add("STOCH", "Stochastic Oscillator", M, "Slow stochastic %K and %D.", false, new[] { Per("k", 14), Per("smooth", 3, 1, 20), Per("d", 3, 1, 20) }, i =>
        {
            var (k, d) = Ta.Stochastic(i.High, i.Low, i.Close, i.N("k"), i.N("smooth"), i.N("d"));
            return new[] { S("%K", k), S("%D", d) };
        });
        Add("STOCHF", "Fast Stochastic", M, "Unsmoothed %K with %D.", false, new[] { Per("k", 14), Per("d", 3, 1, 20) }, i =>
        {
            var (k, d) = Ta.Stochastic(i.High, i.Low, i.Close, i.N("k"), 1, i.N("d"));
            return new[] { S("%K", k), S("%D", d) };
        });
        One("WILLR", "Williams %R", M, "Close relative to the high-low range (−100..0).", false, new[] { Per(def: 14) }, i => Ta.WilliamsR(i.High, i.Low, i.Close, i.N("period")));
        One("CCI", "Commodity Channel Index", M, "Typical price deviation from its mean.", false, new[] { Per(def: 20) }, i => Ta.Cci(i.High, i.Low, i.Close, i.N("period")));
        One("ROC", "Rate of Change", M, "Percent change over n bars.", false, new[] { Per(def: 10, min: 1) }, i => Ta.Roc(i.Close, i.N("period")));
        One("ROCR", "Rate of Change Ratio", M, "Price ratio over n bars.", false, new[] { Per(def: 10, min: 1) }, i => Ta.RocRatio(i.Close, i.N("period")));
        One("MOM", "Momentum", M, "Price difference over n bars.", false, new[] { Per(def: 10, min: 1) }, i => Ta.Momentum(i.Close, i.N("period")));
        One("TRIX", "TRIX", M, "Rate of change of a triple EMA.", false, new[] { Per(def: 15) }, i => Ta.Trix(i.Close, i.N("period")));
        One("UO", "Ultimate Oscillator", M, "Weighted buying pressure over three windows.", false, new[] { Per("p1", 7), Per("p2", 14), Per("p3", 28) }, i => Ta.UltimateOscillator(i.High, i.Low, i.Close, i.N("p1"), i.N("p2"), i.N("p3")));
        One("AO", "Awesome Oscillator", M, "SMA5 − SMA34 of the median price.", false, new[] { Per("fast", 5), Per("slow", 34) }, i => Ta.AwesomeOscillator(i.High, i.Low, i.N("fast"), i.N("slow")));
        One("AC", "Accelerator Oscillator", M, "AO minus its 5-bar SMA.", false, none, i => Ta.AcceleratorOscillator(i.High, i.Low));
        Add("MACD", "MACD", M, "EMA(fast) − EMA(slow), signal and histogram.", false, new[] { Per("fast", 12), Per("slow", 26), Per("signal", 9) }, i =>
        {
            var r = Ta.Macd(i.Close, i.N("fast"), i.N("slow"), i.N("signal"));
            return new[] { S("MACD", r.Macd), S("Signal", r.Signal), S("Histogram", r.Histogram) };
        });
        Add("PPO", "Percentage Price Oscillator", M, "MACD expressed in percent.", false, new[] { Per("fast", 12), Per("slow", 26), Per("signal", 9) }, i =>
        {
            var r = Ta.Ppo(i.Close, i.N("fast"), i.N("slow"), i.N("signal"));
            return new[] { S("PPO", r.Ppo), S("Signal", r.Signal), S("Histogram", r.Histogram) };
        });
        One("APO", "Absolute Price Oscillator", M, "EMA(fast) − EMA(slow).", false, new[] { Per("fast", 12), Per("slow", 26) }, i => Ta.Apo(i.Close, i.N("fast"), i.N("slow")));
        One("CMO", "Chande Momentum Oscillator", M, "Up minus down momentum (−100..100).", false, new[] { Per(def: 14) }, i => Ta.Cmo(i.Close, i.N("period")));
        One("DPO", "Detrended Price Oscillator", M, "Price minus a displaced SMA.", false, new[] { Per(def: 20) }, i => Ta.Dpo(i.Close, i.N("period")));
        Add("KST", "Know Sure Thing", M, "Weighted sum of smoothed ROCs with signal.", false, none, i =>
        {
            var (k, s) = Ta.Kst(i.Close);
            return new[] { S("KST", k), S("Signal", s) };
        });
        Add("TSI", "True Strength Index", M, "Double-smoothed momentum ratio with signal.", false, new[] { Per("long", 25), Per("short", 13), Per("signal", 7) }, i =>
        {
            var (t, s) = Ta.Tsi(i.Close, i.N("long"), i.N("short"), i.N("signal"));
            return new[] { S("TSI", t), S("Signal", s) };
        });
        One("BOP", "Balance of Power", M, "(close − open)/(high − low).", false, none, i => Ta.BalanceOfPower(i.Open, i.High, i.Low, i.Close));
        One("COPPOCK", "Coppock Curve", M, "WMA of the sum of two ROCs.", false, new[] { Per("wma", 10), Per("long", 14), Per("short", 11) }, i => Ta.Coppock(i.Close, i.N("wma"), i.N("long"), i.N("short")));
        Add("FISHER", "Fisher Transform", M, "Gaussianised price position with trigger.", false, new[] { Per(def: 10) }, i =>
        {
            var (f, t) = Ta.FisherTransform(i.High, i.Low, i.N("period"));
            return new[] { S("Fisher", f), S("Trigger", t) };
        });
        Add("RVGI", "Relative Vigor Index", M, "Close-open vigor relative to range, with signal.", false, new[] { Per(def: 10) }, i =>
        {
            var (r, s) = Ta.RelativeVigor(i.Open, i.High, i.Low, i.Close, i.N("period"));
            return new[] { S("RVI", r), S("Signal", s) };
        });
        Add("ELDERRAY", "Elder Ray", M, "Bull and bear power around an EMA.", false, new[] { Per(def: 13) }, i =>
        {
            var (b, s) = Ta.ElderRay(i.High, i.Low, i.Close, i.N("period"));
            return new[] { S("Bull", b), S("Bear", s) };
        });
        One("STC", "Schaff Trend Cycle", M, "Double stochastic of MACD (0-100).", false, new[] { Per("cycle", 10), Per("fast", 23), Per("slow", 50) }, i => Ta.SchaffTrendCycle(i.Close, i.N("cycle"), i.N("fast"), i.N("slow")));
        One("CRSI", "Connors RSI", M, "Composite of RSI, streak RSI and ROC rank.", false, new[] { Per("rsi", 3), Per("streak", 2), Per("rank", 100) }, i => Ta.ConnorsRsi(i.Close, i.N("rsi"), i.N("streak"), i.N("rank")));
        One("QSTICK", "QStick", M, "Moving average of candle bodies.", false, new[] { Per(def: 8) }, i => Ta.QStick(i.Open, i.Close, i.N("period")));
        One("MASSINDEX", "Mass Index", M, "Range expansion reversal signal.", false, new[] { Per("ema", 9), Per("sum", 25) }, i => Ta.MassIndex(i.High, i.Low, i.N("ema"), i.N("sum")));

        // Volatility
        One("TRANGE", "True Range", V, "Greatest of H−L, |H−Cprev|, |L−Cprev|.", false, none, i => Ta.TrueRange(i.High, i.Low, i.Close));
        One("ATR", "Average True Range", V, "Wilder-smoothed true range.", false, new[] { Per(def: 14) }, i => Ta.Atr(i.High, i.Low, i.Close, i.N("period")));
        One("NATR", "Normalized ATR", V, "ATR as a percent of close.", false, new[] { Per(def: 14) }, i => Ta.Natr(i.High, i.Low, i.Close, i.N("period")));
        Add("BBANDS", "Bollinger Bands", V, "SMA ± k standard deviations.", true, new[] { Per(def: 20), Real("k", 2, 0.5, 5, "Std-dev multiplier") }, i =>
        {
            var r = Ta.Bollinger(i.Close, i.N("period"), i.P("k"));
            return new[] { S("Upper", r.Upper), S("Middle", r.Middle), S("Lower", r.Lower) };
        });
        One("BBPCT", "Bollinger %B", V, "Position of close within the bands.", false, new[] { Per(def: 20), Real("k", 2, 0.5, 5) }, i => Ta.Bollinger(i.Close, i.N("period"), i.P("k")).PercentB);
        One("BBW", "Bollinger Bandwidth", V, "Band width relative to the middle band.", false, new[] { Per(def: 20), Real("k", 2, 0.5, 5) }, i => Ta.Bollinger(i.Close, i.N("period"), i.P("k")).Bandwidth);
        Add("KC", "Keltner Channels", V, "EMA ± multiplier × ATR.", true, new[] { Per(def: 20), Per("atr", 10), Real("multiplier", 2, 0.5, 5) }, i =>
        {
            var (u, m, l) = Ta.Keltner(i.High, i.Low, i.Close, i.N("period"), i.N("atr"), i.P("multiplier"));
            return new[] { S("Upper", u), S("Middle", m), S("Lower", l) };
        });
        Add("DC", "Donchian Channels", V, "Highest high / lowest low channel.", true, new[] { Per(def: 20) }, i =>
        {
            var (u, m, l) = Ta.Donchian(i.High, i.Low, i.N("period"));
            return new[] { S("Upper", u), S("Middle", m), S("Lower", l) };
        });
        Add("CHANDELIER", "Chandelier Exit", V, "ATR trailing exits for longs and shorts.", true, new[] { Per(def: 22), Real("multiplier", 3, 0.5, 10) }, i =>
        {
            var (l, s) = Ta.Chandelier(i.High, i.Low, i.Close, i.N("period"), i.P("multiplier"));
            return new[] { S("LongExit", l), S("ShortExit", s) };
        });
        One("CHAIKINVOL", "Chaikin Volatility", V, "ROC of the EMA of the high-low range.", false, new[] { Per("ema", 10), Per("roc", 10) }, i => Ta.ChaikinVolatility(i.High, i.Low, i.N("ema"), i.N("roc")));
        One("HV", "Historical Volatility", V, "Annualised close-to-close volatility (%).", false, new[] { Per(def: 20) }, i => Ta.HistoricalVolatility(i.Close, i.N("period")));
        One("PARKINSON", "Parkinson Volatility", V, "High-low range volatility estimator (%).", false, new[] { Per(def: 20) }, i => Ta.ParkinsonVolatility(i.High, i.Low, i.N("period")));
        One("GARMANKLASS", "Garman-Klass Volatility", V, "OHLC volatility estimator (%).", false, new[] { Per(def: 20) }, i => Ta.GarmanKlassVolatility(i.Open, i.High, i.Low, i.Close, i.N("period")));
        One("ROGERSSATCHELL", "Rogers-Satchell Volatility", V, "Drift-independent OHLC estimator (%).", false, new[] { Per(def: 20) }, i => Ta.RogersSatchellVolatility(i.Open, i.High, i.Low, i.Close, i.N("period")));
        One("ULCER", "Ulcer Index", V, "RMS percentage drawdown.", false, new[] { Per(def: 14) }, i => Ta.UlcerIndex(i.Close, i.N("period")));
        One("CHOP", "Choppiness Index", V, "Trendiness gauge (high = sideways).", false, new[] { Per(def: 14) }, i => Ta.Choppiness(i.High, i.Low, i.Close, i.N("period")));
        One("STDDEV", "Standard Deviation", V, "Rolling population standard deviation.", false, new[] { Per(def: 20) }, i => Ta.StdDev(i.Close, i.N("period")));

        // Volume
        One("OBV", "On-Balance Volume", Vo, "Cumulative signed volume.", false, none, i => Ta.Obv(i.Close, i.Volume));
        One("VWAP", "VWAP (cumulative)", Vo, "Volume-weighted average typical price.", true, none, i => Ta.Vwap(i.High, i.Low, i.Close, i.Volume));
        One("SVWAP", "VWAP (session)", Vo, "VWAP that resets each calendar day.", true, none, i => Ta.SessionVwap(i.Frame.Time, i.High, i.Low, i.Close, i.Volume));
        One("RVWAP", "Rolling VWAP", Vo, "VWAP over the last n bars.", true, new[] { Per(def: 20) }, i => Ta.RollingVwap(i.High, i.Low, i.Close, i.Volume, i.N("period")));
        One("MFI", "Money Flow Index", Vo, "Volume-weighted RSI (0-100).", false, new[] { Per(def: 14) }, i => Ta.Mfi(i.High, i.Low, i.Close, i.Volume, i.N("period")));
        One("AD", "Accumulation/Distribution", Vo, "Chaikin A/D line.", false, none, i => Ta.AccumulationDistribution(i.High, i.Low, i.Close, i.Volume));
        One("CMF", "Chaikin Money Flow", Vo, "Average money-flow multiplier.", false, new[] { Per(def: 20) }, i => Ta.Cmf(i.High, i.Low, i.Close, i.Volume, i.N("period")));
        One("ADOSC", "Chaikin Oscillator", Vo, "EMA3 − EMA10 of the A/D line.", false, new[] { Per("fast", 3), Per("slow", 10) }, i => Ta.ChaikinOscillator(i.High, i.Low, i.Close, i.Volume, i.N("fast"), i.N("slow")));
        One("FI", "Force Index", Vo, "EMA of price change × volume.", false, new[] { Per(def: 13, min: 1) }, i => Ta.ForceIndex(i.Close, i.Volume, i.N("period")));
        One("EOM", "Ease of Movement", Vo, "Price movement per unit volume.", false, new[] { Per(def: 14) }, i => Ta.EaseOfMovement(i.High, i.Low, i.Volume, i.N("period")));
        One("VPT", "Volume-Price Trend", Vo, "Cumulative volume × percent change.", false, none, i => Ta.Vpt(i.Close, i.Volume));
        One("NVI", "Negative Volume Index", Vo, "Tracks price on falling-volume days.", false, none, i => Ta.Nvi(i.Close, i.Volume));
        One("PVI", "Positive Volume Index", Vo, "Tracks price on rising-volume days.", false, none, i => Ta.Pvi(i.Close, i.Volume));
        One("VO", "Volume Oscillator (PVO)", Vo, "Percent gap between volume EMAs.", false, new[] { Per("fast", 12), Per("slow", 26) }, i => Ta.VolumeOscillator(i.Volume, i.N("fast"), i.N("slow")));
        Add("KVO", "Klinger Volume Oscillator", Vo, "Volume-force EMA spread with signal.", false, new[] { Per("fast", 34), Per("slow", 55), Per("signal", 13) }, i =>
        {
            var (k, s) = Ta.Klinger(i.High, i.Low, i.Close, i.Volume, i.N("fast"), i.N("slow"), i.N("signal"));
            return new[] { S("KVO", k), S("Signal", s) };
        });
        One("VROC", "Volume Rate of Change", Vo, "Percent change of volume over n bars.", false, new[] { Per(def: 14, min: 1) }, i => Ta.Roc(i.Volume, i.N("period")));

        // Statistics
        One("ZSCORE", "Z-Score", St, "Standardised distance from the rolling mean.", false, new[] { Per(def: 20) }, i => Ta.ZScore(i.Close, i.N("period")));
        One("VAR", "Variance", St, "Rolling population variance.", false, new[] { Per(def: 20) }, i => Ta.Variance(i.Close, i.N("period")));
        One("SKEW", "Rolling Skewness", St, "Skewness of log returns.", false, new[] { Per(def: 60, min: 4) }, i => Ta.RollingSkewness(Log(i.Close), i.N("period")));
        One("KURT", "Rolling Kurtosis", St, "Excess kurtosis of log returns.", false, new[] { Per(def: 60, min: 5) }, i => Ta.RollingKurtosis(Log(i.Close), i.N("period")));
        One("MEDIAN", "Rolling Median", St, "Median close over n bars.", true, new[] { Per(def: 20) }, i => Ta.RollingMedian(i.Close, i.N("period")));
        One("PCTRANK", "Percent Rank", St, "Percentile of the close within its window.", false, new[] { Per(def: 20) }, i => Ta.PercentRank(i.Close, i.N("period")));
        One("CORREL", "Trend Correlation", St, "Correlation of price with time.", false, new[] { Per(def: 20) }, i => Ta.TrendCorrelation(i.Close, i.N("period")));
        One("HURST", "Hurst Exponent", St, "Rescaled-range persistence (0.5 = random walk).", false, new[] { Per(def: 100, min: 32, max: 1000) }, i => Ta.Hurst(i.Close, i.N("period")));

        // Price transforms
        One("TYPPRICE", "Typical Price", Pt, "(H + L + C)/3.", true, none, i => Ta.TypicalPrice(i.High, i.Low, i.Close));
        One("MEDPRICE", "Median Price", Pt, "(H + L)/2.", true, none, i => Ta.MedianPrice(i.High, i.Low));
        One("WCLPRICE", "Weighted Close", Pt, "(H + L + 2C)/4.", true, none, i => Ta.WeightedClose(i.High, i.Low, i.Close));
        One("AVGPRICE", "Average Price", Pt, "(O + H + L + C)/4.", true, none, i => Ta.AveragePrice(i.Open, i.High, i.Low, i.Close));
        Add("HEIKINASHI", "Heikin-Ashi", Pt, "Smoothed candles (open/high/low/close).", true, none, i =>
        {
            var ha = Ta.HeikinAshi(i.Bars);
            return new[] { S("Open", ha.Select(b => b.Open).ToArray()), S("High", ha.Select(b => b.High).ToArray()), S("Low", ha.Select(b => b.Low).ToArray()), S("Close", ha.Select(b => b.Close).ToArray()) };
        });

        // Candlestick patterns
        foreach (var p in Candlesticks.All)
        {
            var pattern = p;
            string id = "CDL_" + pattern.ToString().ToUpperInvariant();
            Add(id, Spaced(pattern.ToString()), IndicatorCategory.Pattern, "Candlestick pattern: +100 bullish, −100 bearish, 0 none.", false, none,
                i => new[] { S(id, Array.ConvertAll(Candlesticks.Detect(i.Bars, pattern), v => (double)v)) });
        }
    }

    private static double[] Log(double[] close)
    {
        var r = Rolling.NaNs(close.Length);
        for (int i = 1; i < close.Length; i++) r[i] = Math.Log(close[i] / close[i - 1]);
        return r;
    }

    private static string Spaced(string pascal)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i])) sb.Append(' ');
            sb.Append(pascal[i]);
        }
        return sb.ToString();
    }
}
