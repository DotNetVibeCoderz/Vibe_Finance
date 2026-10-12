using Finance.Core;
using Gravicode.Science.GraviNum;
using Gravicode.Science.GraviNum.Autodiff;

namespace Finance.ML;

/// <summary>Configuration of a <see cref="TemporalFusionTransformer"/>.</summary>
public sealed record TftOptions
{
    /// <summary>Model width d (embeddings, GRNs, LSTM state, attention).</summary>
    public int Hidden { get; init; } = 16;
    public int Heads { get; init; } = 2;
    /// <summary>Quantiles predicted (and trained with the pinball loss).</summary>
    public double[] Quantiles { get; init; } = { 0.1, 0.5, 0.9 };
    public double LearningRate { get; init; } = 3e-3;
    public int BatchSize { get; init; } = 16;
    public int Steps { get; init; } = 300;
    public ulong Seed { get; init; } = 11;
}

/// <summary>A TFT forecast with its interpretability outputs.</summary>
/// <param name="Levels">Quantile levels.</param>
/// <param name="Quantiles">[horizon step, quantile] forecasts in original units.</param>
/// <param name="Attention">[horizon step, position] attention averaged over heads; positions 0..L−1 are the past.</param>
/// <param name="EncoderVariables">Names of the encoder inputs (target, past covariates, known covariates, position).</param>
/// <param name="EncoderImportance">Mean variable-selection weight of each encoder input.</param>
/// <param name="DecoderVariables">Names of the decoder inputs (known covariates, position).</param>
/// <param name="DecoderImportance">Mean variable-selection weight of each decoder input.</param>
public sealed record TftForecast(double[] Levels, double[,] Quantiles, double[,] Attention, string[] EncoderVariables, double[] EncoderImportance,
    string[] DecoderVariables, double[] DecoderImportance)
{
    /// <summary>Forecast at quantile <paramref name="level"/> (must be one of <see cref="Levels"/>).</summary>
    public double[] At(double level)
    {
        int q = Array.IndexOf(Levels, level);
        if (q < 0) throw new ArgumentException($"Quantile {level} was not trained.");
        return Enumerable.Range(0, Quantiles.GetLength(0)).Select(h => Quantiles[h, q]).ToArray();
    }

    /// <summary>The median forecast (or the middle quantile).</summary>
    public double[] Median => At(Levels[Levels.Length / 2]);
}

/// <summary>
/// Temporal Fusion Transformer (Lim, Arık, Loeff and Pfister, 2021) for one target series with past-observed and
/// known-future covariates: per-variable embeddings, variable selection networks, an LSTM encoder-decoder, gated
/// residual connections, interpretable multi-head attention (heads share the value projection, so the averaged
/// attention is meaningful) under a causal mask, and quantile outputs trained with the pinball loss.
/// </summary>
/// <remarks>
/// Built on GraviNum's reverse-mode autodiff with one graph per mini-batch (windows stacked as rows; attention runs over
/// all of them at once under a per-window causal mask), trained with Adam. Two simplifications relative to the paper: no static covariates (so no static enrichment or
/// context vectors), and GELU instead of ELU inside the gated residual networks. A relative-position input is added
/// to both encoder and decoder, so the decoder has an input even without known covariates. Targets and covariates
/// are standardised with training statistics.
/// </remarks>
public sealed class TemporalFusionTransformer
{
    private readonly NeuralParameters _ps = new();
    private readonly int _d;
    private readonly Dense[] _embedEnc, _embedDec;
    private readonly Vsn _vsnEnc, _vsnDec;
    private readonly Dense _lstmEnc, _lstmDec;
    private readonly Glu _gateLstm, _gateAttn, _gateOut;
    private readonly Norm _normLstm, _normAttn, _normOut;
    private readonly Dense[] _wq, _wk;
    private readonly Dense _wv, _wo;
    private readonly Grn _positionwise;
    private readonly Dense _output;
    private double _yMean, _yStd = 1;
    private double[] _pMean = Array.Empty<double>(), _pStd = Array.Empty<double>(), _kMean = Array.Empty<double>(), _kStd = Array.Empty<double>();

    public TemporalFusionTransformer(int lookback, int horizon, int pastCovariates = 0, int knownCovariates = 0, TftOptions? options = null,
        IReadOnlyList<string>? pastNames = null, IReadOnlyList<string>? knownNames = null)
    {
        if (lookback < 2 || horizon < 1) throw new ArgumentException("Need lookback ≥ 2 and horizon ≥ 1.");
        Lookback = lookback;
        Horizon = horizon;
        PastCovariates = pastCovariates;
        KnownCovariates = knownCovariates;
        Options = options ?? new TftOptions();
        _d = Options.Hidden;
        var rng = new FastRandom(Options.Seed);
        var known = Enumerable.Range(0, knownCovariates).Select(i => knownNames?[i] ?? $"known{i + 1}").ToArray();
        EncoderVariables = new[] { "target" }.Concat(Enumerable.Range(0, pastCovariates).Select(i => pastNames?[i] ?? $"past{i + 1}"))
            .Concat(known).Append("position").ToArray();
        DecoderVariables = known.Append("position").ToArray();

        _embedEnc = EncoderVariables.Select(_ => new Dense(_ps, 1, _d, rng)).ToArray();
        _embedDec = DecoderVariables.Select(_ => new Dense(_ps, 1, _d, rng)).ToArray();
        _vsnEnc = new Vsn(_ps, EncoderVariables.Length, _d, rng);
        _vsnDec = new Vsn(_ps, DecoderVariables.Length, _d, rng);
        _lstmEnc = new Dense(_ps, 2 * _d, 4 * _d, rng);
        _lstmDec = new Dense(_ps, 2 * _d, 4 * _d, rng);
        _gateLstm = new Glu(_ps, _d, rng);
        _gateAttn = new Glu(_ps, _d, rng);
        _gateOut = new Glu(_ps, _d, rng);
        _normLstm = new Norm(_ps, _d);
        _normAttn = new Norm(_ps, _d);
        _normOut = new Norm(_ps, _d);
        int dk = Math.Max(1, _d / Options.Heads);
        _wq = Enumerable.Range(0, Options.Heads).Select(_ => new Dense(_ps, _d, dk, rng)).ToArray();
        _wk = Enumerable.Range(0, Options.Heads).Select(_ => new Dense(_ps, _d, dk, rng)).ToArray();
        _wv = new Dense(_ps, _d, dk, rng);
        _wo = new Dense(_ps, dk, _d, rng);
        _positionwise = new Grn(_ps, _d, _d, rng);
        _output = new Dense(_ps, _d, Options.Quantiles.Length, rng);
    }

    public int Lookback { get; }
    public int Horizon { get; }
    public int PastCovariates { get; }
    public int KnownCovariates { get; }
    public TftOptions Options { get; }
    public string[] EncoderVariables { get; }
    public string[] DecoderVariables { get; }
    public int ParameterCount => _ps.Count;
    /// <summary>Mean pinball loss per training step (standardised units), every 10 steps.</summary>
    public List<double> LossHistory { get; } = new();

    // ------------------------------------------------------------------ building blocks

    private static Tensor Gelu(Tensor x) => x * (Tensor.Constant(1.0) + (x * (1 / Math.Sqrt(2))).Erf()) * 0.5;

    private sealed class Glu(NeuralParameters ps, int d, FastRandom rng)
    {
        private readonly Dense _a = new(ps, d, d, rng), _b = new(ps, d, d, rng);
        public Tensor Forward(Tensor x) => _a.Forward(x).Sigmoid() * _b.Forward(x);
    }

    /// <summary>Layer normalisation over the last axis of a [rows, d] matrix.</summary>
    private sealed class Norm(NeuralParameters ps, int d)
    {
        private readonly Tensor _gain = ps.Constant(1, d, 1), _bias = ps.Constant(1, d, 0);

        public Tensor Forward(Tensor x)
        {
            int rows = x.Shape[0], d = x.Shape[1];
            var mean = x.Sum(1).Reshape(rows, 1) * (1.0 / d);
            var centred = x - mean;
            var var = (centred * centred).Sum(1).Reshape(rows, 1) * (1.0 / d);
            return centred / (var + 1e-5).Sqrt() * _gain + _bias;
        }
    }

    /// <summary>Gated residual network: LayerNorm(skip(a) + GLU(W₁·GELU(W₂·a))).</summary>
    private sealed class Grn
    {
        private readonly Dense _w2, _w1;
        private readonly Dense? _skip;
        private readonly Glu _glu;
        private readonly Norm _norm;

        public Grn(NeuralParameters ps, int input, int output, FastRandom rng)
        {
            _w2 = new Dense(ps, input, output, rng);
            _w1 = new Dense(ps, output, output, rng);
            _skip = input == output ? null : new Dense(ps, input, output, rng);
            _glu = new Glu(ps, output, rng);
            _norm = new Norm(ps, output);
        }

        public Tensor Forward(Tensor a) => _norm.Forward((_skip?.Forward(a) ?? a) + _glu.Forward(_w1.Forward(Gelu(_w2.Forward(a)))));
    }

    /// <summary>Variable selection network: softmax weights from a GRN over all embeddings, each embedding refined by its own GRN.</summary>
    private sealed class Vsn
    {
        private readonly Grn _weights;
        private readonly Grn[] _each;

        public Vsn(NeuralParameters ps, int variables, int d, FastRandom rng)
        {
            _weights = new Grn(ps, variables * d, variables, rng);
            _each = Enumerable.Range(0, variables).Select(_ => new Grn(ps, d, d, rng)).ToArray();
        }

        public (Tensor Output, double[] Weights) Forward(Tensor[] embeddings)
        {
            var flat = embeddings[0];
            for (int i = 1; i < embeddings.Length; i++) flat = TensorOps.ConcatColumns(flat, embeddings[i]);
            var w = Softmax(_weights.Forward(flat));
            Tensor? sum = null;
            for (int i = 0; i < embeddings.Length; i++)
            {
                var term = _each[i].Forward(embeddings[i]) * TensorOps.SliceColumns(w, i, 1);
                sum = sum is null ? term : sum + term;
            }
            return (sum!, w.Value.ToArray());
        }
    }

    /// <summary>Row-wise softmax of a [rows, n] matrix, shifted by the row maximum (a constant) for stability.</summary>
    private static Tensor Softmax(Tensor x, Tensor? mask = null)
    {
        int rows = x.Shape[0], n = x.Shape[1];
        var v = x.Value.ToArray();
        var mv = mask?.Value.ToArray();
        var shift = new double[rows * n];
        for (int r = 0; r < rows; r++)
        {
            double m = double.MinValue;
            for (int j = 0; j < n; j++) m = Math.Max(m, v[r * n + j] + (mv is null ? 0 : mv[r * n + j]));
            for (int j = 0; j < n; j++) shift[r * n + j] = m;
        }
        var z = x - Tensor.Constant(new NdArray(shift, rows, n));
        if (mask is not null) z = z + mask;
        var e = z.Exp();
        return e / e.Sum(1).Reshape(rows, 1);
    }

    /// <summary>One LSTM step on a [1, d] input with state (h, c); gates from one dense map of [x, h].</summary>
    private (Tensor H, Tensor C) LstmStep(Dense gates, Tensor x, Tensor h, Tensor c)
    {
        var g = gates.Forward(TensorOps.ConcatColumns(x, h));
        var i = TensorOps.SliceColumns(g, 0, _d).Sigmoid();
        var f = TensorOps.SliceColumns(g, _d, _d).Sigmoid();
        var cand = TensorOps.SliceColumns(g, 2 * _d, _d).Tanh();
        var o = TensorOps.SliceColumns(g, 3 * _d, _d).Sigmoid();
        var cNext = f * c + i * cand;
        return (o * cNext.Tanh(), cNext);
    }

    /// <summary>Stacks [1, d] rows into a [T, d] matrix (via columns of the transpose, the op the tape has).</summary>
    private static Tensor StackRows(IReadOnlyList<Tensor> rows)
    {
        var acc = rows[0].T();
        for (int i = 1; i < rows.Count; i++) acc = TensorOps.ConcatColumns(acc, rows[i].T());
        return acc.T();
    }

    /// <summary>One input variable across the batch as a [batch, 1] column.</summary>
    private static Tensor Column(double[][][] rows, int t, int v)
    {
        var data = new double[rows.Length];
        for (int b = 0; b < rows.Length; b++) data[b] = rows[b][t][v];
        return Tensor.Constant(new NdArray(data, rows.Length, 1));
    }

    /// <summary>
    /// Batched forward pass on standardised inputs: <paramref name="enc"/>[b][t][v] (past steps), <paramref name="dec"/>[b][h][v]
    /// (future steps). Every step runs on [batch, d] matrices; attention runs once over all windows stacked as [T·batch, d]
    /// rows (row = t·batch + b) with a mask that keeps each window to itself and causal. Returns [horizon·batch, quantiles]
    /// (row = h·batch + b) plus the first window's attention and the batch-average variable weights.
    /// </summary>
    private (Tensor Output, double[,] Attention, double[] EncImportance, double[] DecImportance) Forward(double[][][] enc, double[][][] dec)
    {
        int B = enc.Length, L = enc[0].Length, H = dec[0].Length, T = L + H;
        var encImp = new double[EncoderVariables.Length];
        var decImp = new double[DecoderVariables.Length];
        var selected = new List<Tensor>(T);
        for (int t = 0; t < T; t++)
        {
            bool past = t < L;
            var source = past ? enc : dec;
            int step = past ? t : t - L;
            var embed = past ? _embedEnc : _embedDec;
            var embeddings = embed.Select((e, v) => e.Forward(Column(source, step, v))).ToArray();
            var (o, w) = (past ? _vsnEnc : _vsnDec).Forward(embeddings);
            selected.Add(o);
            var imp = past ? encImp : decImp;
            int vars = imp.Length;
            for (int b = 0; b < B; b++)
                for (int v = 0; v < vars; v++) imp[v] += w[b * vars + v] / (B * (past ? L : H));
        }

        Tensor h = Tensor.Constant(NdArray.Zeros(B, _d)), c = Tensor.Constant(NdArray.Zeros(B, _d));
        var phi = new List<Tensor>(T);
        for (int t = 0; t < T; t++)
        {
            (h, c) = LstmStep(t < L ? _lstmEnc : _lstmDec, selected[t], h, c);
            phi.Add(_normLstm.Forward(selected[t] + _gateLstm.Forward(h)));
        }
        var Phi = StackRows(phi);                                              // [T·B, d], row = t·B + b

        int N = T * B;
        var mask = new double[N * N];
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
                if (i % B != j % B || j / B > i / B) mask[i * N + j] = -1e9;   // other windows, or the future
        var maskT = Tensor.Constant(new NdArray(mask, N, N));
        var V = _wv.Forward(Phi);
        double scale = 1 / Math.Sqrt(V.Shape[1]);
        Tensor? mix = null;
        var attention = new double[H, T];
        foreach (var (wq, wk) in _wq.Zip(_wk))
        {
            var a = Softmax(wq.Forward(Phi).MatMul(wk.Forward(Phi).T()) * scale, maskT);
            var av = a.Value.ToArray();
            for (int r = 0; r < H; r++)
                for (int j = 0; j < T; j++) attention[r, j] += av[((L + r) * B) * N + j * B] / _wq.Length;   // window 0
            var head = a.MatMul(V);
            mix = mix is null ? head : mix + head;
        }
        var attn = _wo.Forward(mix! * (1.0 / _wq.Length));

        var delta = _normAttn.Forward(Phi + _gateAttn.Forward(attn));
        var psi = _positionwise.Forward(delta);
        var outRows = _normOut.Forward(Phi + _gateOut.Forward(psi));
        var decoderRows = TensorOps.SliceColumns(outRows.T(), L * B, H * B).T();   // [H·B, d], row = h·B + b
        return (_output.Forward(decoderRows), attention, encImp, decImp);
    }

    /// <summary>
    /// Test hook: the pinball loss on one fixed window and its autodiff gradient with respect to one parameter element,
    /// next to a central finite difference of the same loss.
    /// </summary>
    internal (double Analytic, double Numeric) CheckGradient(double[][] enc, double[][] dec, double[] truth, int parameter, int element, double h = 1e-5)
    {
        var q = Options.Quantiles;
        double Loss(bool backward)
        {
            var (outp, _, _, _) = Forward(new[] { enc }, new[] { dec });
            var t = new double[Horizon * q.Length];
            var lev = new double[Horizon * q.Length];
            for (int i = 0; i < Horizon; i++)
                for (int k = 0; k < q.Length; k++) { t[i * q.Length + k] = truth[i]; lev[i * q.Length + k] = q[k]; }
            var e = Tensor.Constant(new NdArray(t, Horizon, q.Length)) - outp;
            var qs = Tensor.Constant(new NdArray(lev, Horizon, q.Length));
            var loss = (qs * e.Relu() + (Tensor.Constant(1.0) - qs) * (-e).Relu()).Mean();
            if (backward) loss.Backward();
            return loss.Item;
        }
        var p = _ps.Tensors[parameter];
        p.ZeroGradients();
        Loss(true);
        double analytic = p.Gradient!.AsSpan()[element];
        var w = p.Value.AsSpan();
        double orig = w[element];
        w[element] = orig + h;
        double up = Loss(false);
        p.Value.AsSpan()[element] = orig - h;
        double down = Loss(false);
        p.Value.AsSpan()[element] = orig;
        foreach (var t in _ps.Tensors) t.ZeroGradients();
        return (analytic, (up - down) / (2 * h));
    }

    internal int ParameterTensorCount => _ps.Tensors.Count;

    // ------------------------------------------------------------------ data

    private static (double Mean, double Std) Stats(IEnumerable<double> xs)
    {
        var a = xs.ToArray();
        double m = a.Average(), s = Math.Sqrt(a.Sum(v => (v - m) * (v - m)) / a.Length);
        return (m, s > 1e-12 ? s : 1);
    }

    private double[][] EncoderRows(ReadOnlySpan<double> y, double[,]? past, double[,]? known, int start)
    {
        var rows = new double[Lookback][];
        for (int t = 0; t < Lookback; t++)
        {
            int i = start + t;
            var r = new double[EncoderVariables.Length];
            int k = 0;
            r[k++] = (y[i] - _yMean) / _yStd;
            for (int j = 0; j < PastCovariates; j++) r[k++] = (past![i, j] - _pMean[j]) / _pStd[j];
            for (int j = 0; j < KnownCovariates; j++) r[k++] = (known![i, j] - _kMean[j]) / _kStd[j];
            r[k] = (t - Lookback) / (double)Lookback;
            rows[t] = r;
        }
        return rows;
    }

    private double[][] DecoderRows(double[,]? known, int start)
    {
        var rows = new double[Horizon][];
        for (int t = 0; t < Horizon; t++)
        {
            int i = start + t;
            var r = new double[DecoderVariables.Length];
            for (int j = 0; j < KnownCovariates; j++) r[j] = (known![i, j] - _kMean[j]) / _kStd[j];
            r[KnownCovariates] = t / (double)Lookback;
            rows[t] = r;
        }
        return rows;
    }

    private void CheckCovariates(int n, double[,]? past, double[,]? known)
    {
        if (PastCovariates > 0 && (past is null || past.GetLength(0) != n || past.GetLength(1) != PastCovariates))
            throw new ArgumentException($"Past covariates must be {n} × {PastCovariates}.");
        if (KnownCovariates > 0 && (known is null || known.GetLength(1) != KnownCovariates))
            throw new ArgumentException($"Known covariates need {KnownCovariates} columns.");
    }

    /// <summary>
    /// Trains on a target series with aligned covariates (rows = time, oldest first). <paramref name="known"/> must have the
    /// same rows as <paramref name="target"/>. Returns the final batch loss.
    /// </summary>
    public double Fit(ReadOnlySpan<double> target, double[,]? past = null, double[,]? known = null)
    {
        int n = target.Length;
        if (n < Lookback + Horizon + 1) throw new ArgumentException("Series shorter than lookback + horizon.");
        CheckCovariates(n, past, known);
        if (KnownCovariates > 0 && known!.GetLength(0) != n) throw new ArgumentException("Known covariates must align with the target when training.");
        (_yMean, _yStd) = Stats(target.ToArray());
        _pMean = new double[PastCovariates]; _pStd = new double[PastCovariates];
        for (int j = 0; j < PastCovariates; j++) (_pMean[j], _pStd[j]) = Stats(Enumerable.Range(0, n).Select(i => past![i, j]));
        _kMean = new double[KnownCovariates]; _kStd = new double[KnownCovariates];
        for (int j = 0; j < KnownCovariates; j++) (_kMean[j], _kStd[j]) = Stats(Enumerable.Range(0, n).Select(i => known![i, j]));

        var y = target.ToArray();
        var rng = new FastRandom(Options.Seed + 1);
        int windows = n - Lookback - Horizon + 1;
        var q = Options.Quantiles;
        double last = double.NaN;
        for (int step = 0; step < Options.Steps; step++)
        {
            int B = Options.BatchSize;
            var starts = Enumerable.Range(0, B).Select(_ => rng.NextInt(windows)).ToArray();
            var (outp, _, _, _) = Forward(starts.Select(s => EncoderRows(y, past, known, s)).ToArray(),
                starts.Select(s => DecoderRows(known, s + Lookback)).ToArray());
            var truth = new double[Horizon * B * q.Length];
            var lev = new double[Horizon * B * q.Length];
            for (int h = 0; h < Horizon; h++)
                for (int b = 0; b < B; b++)
                    for (int k = 0; k < q.Length; k++)
                    {
                        int i = (h * B + b) * q.Length + k;
                        truth[i] = (y[starts[b] + Lookback + h] - _yMean) / _yStd;
                        lev[i] = q[k];
                    }
            // Pinball loss: q·relu(e) + (1 − q)·relu(−e), e = y − ŷ.
            var e = Tensor.Constant(new NdArray(truth, Horizon * B, q.Length)) - outp;
            var qs = Tensor.Constant(new NdArray(lev, Horizon * B, q.Length));
            var loss = (qs * e.Relu() + (Tensor.Constant(1.0) - qs) * (-e).Relu()).Mean();
            loss.Backward();
            _ps.Step(Options.LearningRate);
            last = loss.Item;
            if (step % 10 == 0) LossHistory.Add(last);
        }
        return last;
    }

    /// <summary>
    /// Forecasts the <see cref="Horizon"/> steps after the end of <paramref name="target"/>. Past covariates align with the
    /// target; known covariates must cover the target's rows plus the horizon.
    /// </summary>
    public TftForecast Predict(ReadOnlySpan<double> target, double[,]? past = null, double[,]? known = null)
    {
        int n = target.Length;
        if (n < Lookback) throw new ArgumentException($"Need at least {Lookback} observations.");
        CheckCovariates(n, past, known);
        if (KnownCovariates > 0 && known!.GetLength(0) < n + Horizon) throw new ArgumentException("Known covariates must extend over the horizon.");
        var (outp, attention, encImp, decImp) = Forward(new[] { EncoderRows(target, past, known, n - Lookback) }, new[] { DecoderRows(known, n) });
        var v = outp.Value.ToArray();
        int nq = Options.Quantiles.Length;
        var quantiles = new double[Horizon, nq];
        for (int h = 0; h < Horizon; h++)
        {
            // Sort each step's quantiles so they never cross.
            var row = Enumerable.Range(0, nq).Select(k => v[h * nq + k] * _yStd + _yMean).Order().ToArray();
            for (int k = 0; k < nq; k++) quantiles[h, k] = row[k];
        }
        return new TftForecast(Options.Quantiles, quantiles, attention, EncoderVariables, encImp, DecoderVariables, decImp);
    }
}
