using Finance.Core.Market;

namespace Finance.TimeSeries.Indicators;

/// <summary>An indicator updated one observation at a time (live feeds, event-driven backtests).</summary>
public interface IStreamingIndicator
{
    /// <summary>Latest value (NaN until <see cref="IsReady"/>).</summary>
    double Value { get; }

    /// <summary>True once the warm-up period has elapsed.</summary>
    bool IsReady { get; }

    /// <summary>Feeds a new bar and returns the updated value.</summary>
    double Update(Bar bar);

    /// <summary>Clears all state.</summary>
    void Reset();
}

/// <summary>Base for indicators driven by a single price (close by default).</summary>
public abstract class StreamingPriceIndicator : IStreamingIndicator
{
    public double Value { get; protected set; } = double.NaN;
    public bool IsReady => !double.IsNaN(Value);

    public double Update(Bar bar) => Update(bar.Close);

    /// <summary>Feeds a new price and returns the updated value.</summary>
    public abstract double Update(double price);

    public abstract void Reset();
}

/// <summary>Streaming SMA over a ring buffer: O(1) per update.</summary>
public sealed class StreamingSma : StreamingPriceIndicator
{
    private readonly double[] _buf;
    private int _count, _pos;
    private double _sum;

    public StreamingSma(int period)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _buf = new double[period];
    }

    public int Period => _buf.Length;

    public override double Update(double price)
    {
        if (_count == _buf.Length) _sum -= _buf[_pos];
        else _count++;
        _buf[_pos] = price;
        _sum += price;
        _pos = (_pos + 1) % _buf.Length;
        if (_count == _buf.Length) Value = _sum / _buf.Length;
        return Value;
    }

    public override void Reset()
    {
        Array.Clear(_buf);
        _count = _pos = 0;
        _sum = 0;
        Value = double.NaN;
    }
}

/// <summary>Streaming EMA, SMA-seeded (matches <see cref="Ta.Ema"/>).</summary>
public sealed class StreamingEma : StreamingPriceIndicator
{
    private readonly int _period;
    private readonly double _alpha;
    private int _count;
    private double _seed;

    public StreamingEma(int period, double? alpha = null)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _alpha = alpha ?? 2.0 / (period + 1);
    }

    public override double Update(double price)
    {
        if (_count < _period)
        {
            _seed += price;
            if (++_count == _period) Value = _seed / _period;
            return Value;
        }
        Value += _alpha * (price - Value);
        return Value;
    }

    public override void Reset()
    {
        _count = 0;
        _seed = 0;
        Value = double.NaN;
    }
}

/// <summary>Streaming Wilder RSI (matches <see cref="Ta.Rsi"/>).</summary>
public sealed class StreamingRsi : StreamingPriceIndicator
{
    private readonly int _period;
    private double _prev = double.NaN, _gain, _loss;
    private int _count;

    public StreamingRsi(int period = 14) => _period = period;

    public override double Update(double price)
    {
        if (double.IsNaN(_prev)) { _prev = price; return Value; }
        double d = price - _prev;
        _prev = price;
        double g = Math.Max(d, 0), l = Math.Max(-d, 0);
        if (_count < _period)
        {
            _gain += g;
            _loss += l;
            if (++_count == _period)
            {
                _gain /= _period;
                _loss /= _period;
                Value = Rsi(_gain, _loss);
            }
            return Value;
        }
        _gain = (_gain * (_period - 1) + g) / _period;
        _loss = (_loss * (_period - 1) + l) / _period;
        Value = Rsi(_gain, _loss);
        return Value;
    }

    private static double Rsi(double g, double l) => l == 0 ? (g == 0 ? 50 : 100) : 100 - 100 / (1 + g / l);

    public override void Reset()
    {
        _prev = double.NaN;
        _gain = _loss = 0;
        _count = 0;
        Value = double.NaN;
    }
}

/// <summary>Streaming Wilder ATR (matches <see cref="Ta.Atr"/>).</summary>
public sealed class StreamingAtr : IStreamingIndicator
{
    private readonly int _period;
    private double _prevClose = double.NaN, _sum;
    private int _count;

    public StreamingAtr(int period = 14) => _period = period;

    public double Value { get; private set; } = double.NaN;
    public bool IsReady => !double.IsNaN(Value);

    public double Update(Bar bar)
    {
        if (double.IsNaN(_prevClose)) { _prevClose = bar.Close; return Value; }
        double tr = Math.Max(bar.High - bar.Low, Math.Max(Math.Abs(bar.High - _prevClose), Math.Abs(bar.Low - _prevClose)));
        _prevClose = bar.Close;
        if (_count < _period)
        {
            _sum += tr;
            if (++_count == _period) Value = _sum / _period;
            return Value;
        }
        Value = (Value * (_period - 1) + tr) / _period;
        return Value;
    }

    public void Reset()
    {
        _prevClose = double.NaN;
        _sum = 0;
        _count = 0;
        Value = double.NaN;
    }
}

/// <summary>Streaming Bollinger Bands (population σ) over a ring buffer.</summary>
public sealed class StreamingBollinger : StreamingPriceIndicator
{
    private readonly double[] _buf;
    private readonly double _k;
    private int _count, _pos;
    private double _sum, _sumSq;

    public StreamingBollinger(int period = 20, double k = 2)
    {
        _buf = new double[period];
        _k = k;
    }

    public double Upper { get; private set; } = double.NaN;
    public double Lower { get; private set; } = double.NaN;

    /// <summary>Updates and returns the middle band.</summary>
    public override double Update(double price)
    {
        if (_count == _buf.Length)
        {
            double old = _buf[_pos];
            _sum -= old;
            _sumSq -= old * old;
        }
        else _count++;
        _buf[_pos] = price;
        _sum += price;
        _sumSq += price * price;
        _pos = (_pos + 1) % _buf.Length;
        if (_count == _buf.Length)
        {
            double mean = _sum / _count;
            double sd = Math.Sqrt(Math.Max(0, _sumSq / _count - mean * mean));
            Value = mean;
            Upper = mean + _k * sd;
            Lower = mean - _k * sd;
        }
        return Value;
    }

    public override void Reset()
    {
        Array.Clear(_buf);
        _count = _pos = 0;
        _sum = _sumSq = 0;
        Value = Upper = Lower = double.NaN;
    }
}

/// <summary>Streaming MACD built from two streaming EMAs and a signal EMA.</summary>
public sealed class StreamingMacd : StreamingPriceIndicator
{
    private readonly StreamingEma _fast, _slow, _signal;

    public StreamingMacd(int fast = 12, int slow = 26, int signal = 9)
    {
        _fast = new StreamingEma(fast);
        _slow = new StreamingEma(slow);
        _signal = new StreamingEma(signal);
    }

    public double Signal => _signal.Value;
    public double Histogram => Value - Signal;

    /// <summary>Updates and returns the MACD line.</summary>
    public override double Update(double price)
    {
        double f = _fast.Update(price), s = _slow.Update(price);
        if (double.IsNaN(s)) return Value;
        Value = f - s;
        _signal.Update(Value);
        return Value;
    }

    public override void Reset()
    {
        _fast.Reset();
        _slow.Reset();
        _signal.Reset();
        Value = double.NaN;
    }
}
