using Finance.Mathematics;

namespace Finance.ML;

/// <summary>A univariate time-series forecaster.</summary>
public interface IForecaster
{
    void Fit(ReadOnlySpan<double> series);

    /// <summary>Point forecasts for the next <paramref name="horizon"/> steps.</summary>
    double[] Forecast(int horizon);
}

/// <summary>Autoregressive AR(p) model y_t = c + Σ φ_i y_{t-i} + ε, fitted by OLS.</summary>
public sealed class AutoRegressiveModel(int order) : IForecaster
{
    private double[] _history = Array.Empty<double>();

    public int Order { get; } = order >= 1 ? order : throw new ArgumentOutOfRangeException(nameof(order));
    public double Intercept { get; private set; }
    /// <summary>φ_1 … φ_p.</summary>
    public double[] Coefficients { get; private set; } = Array.Empty<double>();
    public double ResidualStdError { get; private set; }

    public void Fit(ReadOnlySpan<double> series)
    {
        int p = Order, n = series.Length - p;
        if (n <= p + 1) throw new ArgumentException("Series too short for this order.");
        var x = new Matrix(n, p);
        var y = new double[n];
        for (int t = 0; t < n; t++)
        {
            y[t] = series[t + p];
            for (int i = 0; i < p; i++) x[t, i] = series[t + p - 1 - i];
        }
        var r = Regression.Ols(x, y);
        Intercept = r.Intercept;
        Coefficients = r.Slopes.ToArray();
        ResidualStdError = r.ResidualStdError;
        _history = series[^p..].ToArray();
    }

    public double[] Forecast(int horizon)
    {
        var buf = new List<double>(_history);
        var f = new double[horizon];
        for (int h = 0; h < horizon; h++)
        {
            double v = Intercept;
            for (int i = 0; i < Order; i++) v += Coefficients[i] * buf[buf.Count - 1 - i];
            f[h] = v;
            buf.Add(v);
        }
        return f;
    }
}

/// <summary>
/// ARIMA(p, d, 0): AR(p) on the d-times differenced series, forecasts integrated back to levels.
/// </summary>
public sealed class ArimaModel(int p, int d) : IForecaster
{
    private readonly AutoRegressiveModel _ar = new(p);
    private double[] _lastLevels = Array.Empty<double>();

    public int P { get; } = p;
    public int D { get; } = d >= 0 ? d : throw new ArgumentOutOfRangeException(nameof(d));
    public AutoRegressiveModel Ar => _ar;

    public void Fit(ReadOnlySpan<double> series)
    {
        var s = series.ToArray();
        // Last value at each differencing level, needed to integrate forecasts back.
        _lastLevels = new double[D];
        for (int k = 0; k < D; k++)
        {
            _lastLevels[k] = s[^1];
            var diff = new double[s.Length - 1];
            for (int i = 1; i < s.Length; i++) diff[i - 1] = s[i] - s[i - 1];
            s = diff;
        }
        _ar.Fit(s);
    }

    public double[] Forecast(int horizon)
    {
        var f = _ar.Forecast(horizon);
        for (int k = D - 1; k >= 0; k--)
        {
            double level = _lastLevels[k];
            for (int h = 0; h < horizon; h++) { level += f[h]; f[h] = level; }
        }
        return f;
    }
}

/// <summary>
/// Additive Holt-Winters (level, trend, seasonality). With <c>seasonLength = 0</c> it reduces to
/// Holt's linear trend method. Smoothing parameters can be fitted by minimising one-step SSE.
/// </summary>
public sealed class HoltWintersModel : IForecaster
{
    private double _level, _trend;
    private double[] _season = Array.Empty<double>();
    private int _n;

    public HoltWintersModel(int seasonLength = 0, double alpha = double.NaN, double beta = double.NaN, double gamma = double.NaN)
    {
        SeasonLength = seasonLength;
        Alpha = alpha;
        Beta = beta;
        Gamma = gamma;
    }

    public int SeasonLength { get; }
    public double Alpha { get; private set; }
    public double Beta { get; private set; }
    public double Gamma { get; private set; }
    /// <summary>In-sample one-step-ahead fitted values.</summary>
    public double[] Fitted { get; private set; } = Array.Empty<double>();

    public void Fit(ReadOnlySpan<double> series)
    {
        var s = series.ToArray();
        int m = SeasonLength;
        if (s.Length < Math.Max(4, 2 * m)) throw new ArgumentException("Series too short.");
        if (double.IsNaN(Alpha) || double.IsNaN(Beta) || (m > 0 && double.IsNaN(Gamma)))
        {
            double Logit(double v) => Math.Log(v / (1 - v));
            double Inv(double v) => 1 / (1 + Math.Exp(-v));
            var start = new[] { Logit(0.3), Logit(0.05), Logit(0.1) };
            var res = Optimizer.NelderMead(z => Run(s, Inv(z[0]), Inv(z[1]), m > 0 ? Inv(z[2]) : 0, false), start, 0.5, 2000);
            Alpha = double.IsNaN(Alpha) ? Inv(res.X[0]) : Alpha;
            Beta = double.IsNaN(Beta) ? Inv(res.X[1]) : Beta;
            Gamma = m > 0 ? (double.IsNaN(Gamma) ? Inv(res.X[2]) : Gamma) : 0;
        }
        Run(s, Alpha, Beta, Gamma, true);
    }

    private double Run(double[] s, double a, double b, double g, bool keep)
    {
        int m = SeasonLength, n = s.Length;
        double level, trend;
        var season = new double[Math.Max(m, 1)];
        if (m > 0)
        {
            double first = 0, second = 0;
            for (int i = 0; i < m; i++) { first += s[i]; second += s[m + i]; }
            first /= m;
            second /= m;
            level = first;
            trend = (second - first) / m;
            for (int i = 0; i < m; i++) season[i] = s[i] - first;
        }
        else
        {
            level = s[0];
            trend = s[1] - s[0];
        }
        double sse = 0;
        var fitted = keep ? new double[n] : null;
        for (int t = 0; t < n; t++)
        {
            double sea = m > 0 ? season[t % m] : 0;
            double forecast = level + trend + sea;
            if (t == 0) forecast = s[0];
            if (fitted != null) fitted[t] = forecast;
            double e = s[t] - forecast;
            if (t > 0) sse += e * e;
            double prevLevel = level;
            level = a * (s[t] - sea) + (1 - a) * (level + trend);
            trend = b * (level - prevLevel) + (1 - b) * trend;
            if (m > 0) season[t % m] = g * (s[t] - level) + (1 - g) * sea;
        }
        if (keep)
        {
            _level = level;
            _trend = trend;
            _season = season;
            _n = n;
            Fitted = fitted!;
        }
        return sse;
    }

    public double[] Forecast(int horizon)
    {
        var f = new double[horizon];
        for (int h = 1; h <= horizon; h++)
            f[h - 1] = _level + h * _trend + (SeasonLength > 0 ? _season[(_n + h - 1) % SeasonLength] : 0);
        return f;
    }
}
