namespace Finance.Backtesting;

/// <summary>
/// The few indicators the built-in strategies need, kept here so Backtesting has no dependency on
/// Finance.TimeSeries. Leading values that cannot be computed are NaN.
/// </summary>
public static class Indicators
{
    /// <summary>Simple moving average (O(n) rolling sum).</summary>
    public static double[] Sma(ReadOnlySpan<double> x, int period)
    {
        Finance.Core.Guard.Positive(period);
        var r = new double[x.Length];
        double sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            sum += x[i];
            if (i >= period) sum -= x[i - period];
            r[i] = i >= period - 1 ? sum / period : double.NaN;
        }
        return r;
    }

    /// <summary>Wilder's RSI.</summary>
    public static double[] Rsi(ReadOnlySpan<double> x, int period = 14)
    {
        var r = new double[x.Length];
        if (x.Length == 0) return r;
        var rsi = new WilderRsi(period);
        r[0] = double.NaN;
        rsi.Update(x[0]);
        for (int i = 1; i < x.Length; i++) r[i] = rsi.Update(x[i]);
        return r;
    }

    /// <summary>Mean and population standard deviation of a window.</summary>
    public static (double Mean, double StdDev) MeanStd(ReadOnlySpan<double> window)
    {
        double m = 0;
        foreach (var v in window) m += v;
        m /= window.Length;
        double s = 0;
        foreach (var v in window) s += (v - m) * (v - m);
        return (m, Math.Sqrt(s / window.Length));
    }
}

/// <summary>Incremental Wilder RSI: feed closes one at a time.</summary>
public sealed class WilderRsi
{
    private readonly int _period;
    private double _prev = double.NaN, _avgGain, _avgLoss;
    private int _count;

    public WilderRsi(int period = 14)
    {
        Finance.Core.Guard.Positive(period);
        _period = period;
    }

    /// <summary>Latest value (NaN until <c>period</c> changes have been seen).</summary>
    public double Value { get; private set; } = double.NaN;

    public bool IsReady => _count >= _period;

    public double Update(double close)
    {
        if (double.IsNaN(_prev))
        {
            _prev = close;
            return Value;
        }
        double change = close - _prev;
        _prev = close;
        double gain = Math.Max(change, 0), loss = Math.Max(-change, 0);
        _count++;
        if (_count <= _period)
        {
            _avgGain += gain / _period;
            _avgLoss += loss / _period;
            if (_count < _period) return Value;
        }
        else
        {
            _avgGain = (_avgGain * (_period - 1) + gain) / _period;
            _avgLoss = (_avgLoss * (_period - 1) + loss) / _period;
        }
        Value = _avgLoss == 0 ? (_avgGain == 0 ? 50 : 100) : 100 - 100 / (1 + _avgGain / _avgLoss);
        return Value;
    }
}
