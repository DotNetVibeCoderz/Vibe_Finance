using Finance.Core;
using Gravicode.Science.GraviNum;
using Gravicode.Science.GraviNum.Autodiff;

namespace Finance.ML;

/// <summary>Trainable parameters of a neural model and the Adam optimiser that updates them.</summary>
public sealed class NeuralParameters
{
    private readonly List<Tensor> _params = new();
    private readonly List<double[]> _m = new(), _v = new();
    private int _step;

    public IReadOnlyList<Tensor> Tensors => _params;
    public int Count => _params.Sum(p => p.Size);

    /// <summary>A new parameter initialised with Glorot/Xavier uniform scaling.</summary>
    public Tensor Glorot(int rows, int columns, FastRandom rng)
    {
        double limit = Math.Sqrt(6.0 / (rows + columns));
        var data = new double[rows * columns];
        for (int i = 0; i < data.Length; i++) data[i] = (2 * rng.NextDouble() - 1) * limit;
        return Add(new NdArray(data, rows, columns));
    }

    /// <summary>A new parameter filled with <paramref name="value"/>.</summary>
    public Tensor Constant(int rows, int columns, double value = 0) => Add(NdArray.Full(value, rows, columns));

    private Tensor Add(NdArray initial)
    {
        var t = Tensor.Parameter(initial);
        _params.Add(t);
        _m.Add(new double[initial.Size]);
        _v.Add(new double[initial.Size]);
        return t;
    }

    /// <summary>
    /// One Adam step (β₁ = 0.9, β₂ = 0.999) using the gradients accumulated since the last step, scaled by
    /// 1/<paramref name="batchSize"/> and clipped to a global norm of <paramref name="clip"/>; gradients are then cleared.
    /// </summary>
    public void Step(double learningRate, int batchSize = 1, double clip = 1.0)
    {
        _step++;
        double norm = 0, scale = 1.0 / batchSize;
        foreach (var p in _params)
            if (p.Gradient is { } g) foreach (var x in g.AsSpan()) norm += x * x * scale * scale;
        norm = Math.Sqrt(norm);
        if (clip > 0 && norm > clip) scale *= clip / norm;
        double b1 = 0.9, b2 = 0.999, c1 = 1 - Math.Pow(b1, _step), c2 = 1 - Math.Pow(b2, _step);
        for (int k = 0; k < _params.Count; k++)
        {
            var p = _params[k];
            if (p.Gradient is null) continue;
            var g = p.Gradient.AsSpan();
            var w = p.Value.AsSpan();
            var m = _m[k];
            var v = _v[k];
            for (int i = 0; i < w.Length; i++)
            {
                double gi = g[i] * scale;
                m[i] = b1 * m[i] + (1 - b1) * gi;
                v[i] = b2 * v[i] + (1 - b2) * gi * gi;
                w[i] -= learningRate * (m[i] / c1) / (Math.Sqrt(v[i] / c2) + 1e-8);
            }
            p.ZeroGradients();
        }
    }

    /// <summary>All parameter values, flattened (for saving or comparing models).</summary>
    public double[] Export() => _params.SelectMany(p => p.Value.ToArray()).ToArray();
}

/// <summary>A dense layer y = xW + b on row-batched inputs.</summary>
internal sealed class Dense
{
    public Dense(NeuralParameters ps, int inputs, int outputs, FastRandom rng)
    {
        W = ps.Glorot(inputs, outputs, rng);
        B = ps.Constant(1, outputs);
    }

    public Tensor W { get; }
    public Tensor B { get; }
    public Tensor Forward(Tensor x) => x.MatMul(W) + B;
}

/// <summary>Which basis an N-BEATS stack uses.</summary>
public enum NBeatsStackType
{
    /// <summary>Learned basis: fully connected projections of θ (the N-BEATS-G configuration).</summary>
    Generic,
    /// <summary>Polynomial basis in time: the stack's forecast is a low-order trend.</summary>
    Trend,
    /// <summary>Fourier basis: the stack's forecast is a sum of periodic components.</summary>
    Seasonality,
}

/// <summary>Configuration of an <see cref="NBeatsForecaster"/>.</summary>
public sealed record NBeatsOptions
{
    /// <summary>Stacks in order; the default is the interpretable trend + seasonality configuration (N-BEATS-I).</summary>
    public NBeatsStackType[] Stacks { get; init; } = { NBeatsStackType.Trend, NBeatsStackType.Seasonality };
    public int BlocksPerStack { get; init; } = 2;
    public int LayersPerBlock { get; init; } = 3;
    public int Width { get; init; } = 64;
    /// <summary>Polynomial degree of trend blocks.</summary>
    public int TrendDegree { get; init; } = 2;
    /// <summary>Fourier harmonics of seasonality blocks.</summary>
    public int Harmonics { get; init; } = 4;
    /// <summary>θ size of generic blocks.</summary>
    public int GenericThetas { get; init; } = 16;
    public double LearningRate { get; init; } = 1e-3;
    public int BatchSize { get; init; } = 32;
    public int Steps { get; init; } = 1500;
    public ulong Seed { get; init; } = 7;
}

/// <summary>
/// N-BEATS (Oreshkin, Carpov, Chapados and Bengio, ICLR 2020): a deep stack of fully connected blocks with backward and
/// forward residual links. Each block reads what the previous blocks could not explain, removes its own backcast, and
/// adds its partial forecast. With trend (polynomial) and seasonality (Fourier) stacks, the forecast decomposes
/// into interpretable components (<see cref="Decompose"/>).
/// </summary>
/// <remarks>
/// Trained with Adam on mean squared error over windows sampled from the training series, on GraviNum's reverse-mode
/// autodiff. Inputs are standardised with the training mean and standard deviation.
/// </remarks>
public sealed class NBeatsForecaster
{
    private sealed record Block(NBeatsStackType Type, Dense[] Hidden, Dense ThetaBack, Dense ThetaFore, Tensor BasisBack, Tensor BasisFore);

    private readonly List<Block> _blocks = new();
    private readonly NeuralParameters _params = new();
    private double _mean, _std = 1;

    public NBeatsForecaster(int lookback, int horizon, NBeatsOptions? options = null)
    {
        if (lookback < 2 || horizon < 1) throw new ArgumentException("Need lookback ≥ 2 and horizon ≥ 1.");
        Lookback = lookback;
        Horizon = horizon;
        Options = options ?? new NBeatsOptions();
        var rng = new FastRandom(Options.Seed);
        foreach (var type in Options.Stacks)
            for (int b = 0; b < Options.BlocksPerStack; b++)
            {
                var hidden = new Dense[Options.LayersPerBlock];
                for (int l = 0; l < hidden.Length; l++) hidden[l] = new Dense(_params, l == 0 ? lookback : Options.Width, Options.Width, rng);
                int theta = type switch
                {
                    NBeatsStackType.Trend => Options.TrendDegree + 1,
                    NBeatsStackType.Seasonality => 2 * Options.Harmonics + 1,
                    _ => Options.GenericThetas,
                };
                var (bb, bf) = type switch
                {
                    NBeatsStackType.Trend => (Polynomial(theta, lookback, -lookback), Polynomial(theta, horizon, 0)),
                    NBeatsStackType.Seasonality => (Fourier(Options.Harmonics, lookback, -lookback), Fourier(Options.Harmonics, horizon, 0)),
                    _ => ((Tensor?)null, (Tensor?)null),
                };
                // Generic blocks learn their basis: a dense map from θ to the backcast / forecast.
                bb ??= _params.Glorot(theta, lookback, rng);
                bf ??= _params.Glorot(theta, horizon, rng);
                _blocks.Add(new Block(type, hidden, new Dense(_params, Options.Width, theta, rng), new Dense(_params, Options.Width, theta, rng), bb, bf));
            }
    }

    public int Lookback { get; }
    public int Horizon { get; }
    public NBeatsOptions Options { get; }
    public int ParameterCount => _params.Count;
    /// <summary>Training loss (MSE on standardised data) recorded every 50 steps.</summary>
    public List<double> LossHistory { get; } = new();

    /// <summary>Polynomial basis t⁰…tᵖ over the window positions, time measured in horizons (constant tensor).</summary>
    private Tensor Polynomial(int terms, int length, int offset)
    {
        var data = new double[terms * length];
        for (int p = 0; p < terms; p++)
            for (int i = 0; i < length; i++) data[p * length + i] = Math.Pow((offset + i) / (double)Horizon, p);
        return Tensor.Constant(new NdArray(data, terms, length));
    }

    /// <summary>Fourier basis 1, cos(2πkt/L), sin(2πkt/L) with t continuing from the lookback into the horizon.</summary>
    private Tensor Fourier(int harmonics, int length, int offset)
    {
        int terms = 2 * harmonics + 1;
        var data = new double[terms * length];
        for (int i = 0; i < length; i++)
        {
            double t = (offset + i) / (double)Lookback;
            data[i] = 1;
            for (int k = 1; k <= harmonics; k++)
            {
                data[(2 * k - 1) * length + i] = Math.Cos(2 * Math.PI * k * t);
                data[2 * k * length + i] = Math.Sin(2 * Math.PI * k * t);
            }
        }
        return Tensor.Constant(new NdArray(data, terms, length));
    }

    /// <summary>Forward pass on standardised windows [batch, lookback]: total forecast and each stack type's share.</summary>
    private (Tensor Forecast, Dictionary<NBeatsStackType, Tensor> Parts) Forward(Tensor x)
    {
        Tensor residual = x;
        Tensor? total = null;
        var parts = new Dictionary<NBeatsStackType, Tensor>();
        foreach (var block in _blocks)
        {
            var h = residual;
            foreach (var layer in block.Hidden) h = layer.Forward(h).Relu();
            var backcast = block.ThetaBack.Forward(h).MatMul(block.BasisBack);
            var forecast = block.ThetaFore.Forward(h).MatMul(block.BasisFore);
            residual = residual - backcast;
            total = total is null ? forecast : total + forecast;
            parts[block.Type] = parts.TryGetValue(block.Type, out var p) ? p + forecast : forecast;
        }
        return (total!, parts);
    }

    /// <summary>Trains on one series (oldest first). Returns the final training loss.</summary>
    public double Fit(ReadOnlySpan<double> series)
    {
        if (series.Length < Lookback + Horizon + 1) throw new ArgumentException("Series shorter than lookback + horizon.");
        _mean = 0;
        foreach (var v in series) _mean += v;
        _mean /= series.Length;
        double ss = 0;
        foreach (var v in series) ss += (v - _mean) * (v - _mean);
        _std = Math.Max(Math.Sqrt(ss / series.Length), 1e-12);
        var z = new double[series.Length];
        for (int i = 0; i < z.Length; i++) z[i] = (series[i] - _mean) / _std;

        var rng = new FastRandom(Options.Seed + 1);
        int windows = z.Length - Lookback - Horizon + 1, bs = Options.BatchSize;
        double last = double.NaN;
        for (int step = 0; step < Options.Steps; step++)
        {
            var xs = new double[bs * Lookback];
            var ys = new double[bs * Horizon];
            for (int b = 0; b < bs; b++)
            {
                int s = rng.NextInt(windows);
                Array.Copy(z, s, xs, b * Lookback, Lookback);
                Array.Copy(z, s + Lookback, ys, b * Horizon, Horizon);
            }
            var (pred, _) = Forward(Tensor.Constant(new NdArray(xs, bs, Lookback)));
            var err = pred - Tensor.Constant(new NdArray(ys, bs, Horizon));
            var loss = (err * err).Mean();
            loss.Backward();
            _params.Step(Options.LearningRate);
            last = loss.Item;
            if (step % 50 == 0) LossHistory.Add(last);
        }
        return last;
    }

    /// <summary>Forecasts the next <see cref="Horizon"/> values after the last <see cref="Lookback"/> of <paramref name="history"/>.</summary>
    public double[] Forecast(ReadOnlySpan<double> history) => Decompose(history).Total;

    /// <summary>The forecast split by stack type (trend, seasonality, generic), in original units; parts sum to the total.</summary>
    public (double[] Total, IReadOnlyDictionary<NBeatsStackType, double[]> Components) Decompose(ReadOnlySpan<double> history)
    {
        if (history.Length < Lookback) throw new ArgumentException($"Need at least {Lookback} observations.");
        var x = new double[Lookback];
        var window = history[^Lookback..];
        for (int i = 0; i < Lookback; i++) x[i] = (window[i] - _mean) / _std;
        var (total, parts) = Forward(Tensor.Constant(new NdArray(x, 1, Lookback)));
        double[] Unscale(Tensor t, bool withMean) => t.Value.ToArray().Select(v => v * _std + (withMean ? _mean : 0)).ToArray();
        // The level (training mean) is attributed to the trend component when there is one, else to the first stack.
        var first = Options.Stacks.Contains(NBeatsStackType.Trend) ? NBeatsStackType.Trend : Options.Stacks[0];
        var comps = parts.ToDictionary(p => p.Key, p => Unscale(p.Value, p.Key == first));
        return (Unscale(total, true), comps);
    }
}
