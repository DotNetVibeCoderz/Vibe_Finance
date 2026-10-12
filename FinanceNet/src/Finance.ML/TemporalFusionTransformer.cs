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
    /// <summary>Names of the static covariates (empty when the model has none).</summary>
    public string[] StaticVariables { get; init; } = Array.Empty<string>();
    /// <summary>Static variable-selection weight of each static covariate for the forecast series (sums to 1).</summary>
    public double[] StaticImportance { get; init; } = Array.Empty<double>();

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
/// One training series for multi-series fitting: the target, its aligned covariates (rows = time, oldest first) and its
/// static covariates (one value per static input, constant over time).
/// </summary>
public sealed record TftSeries(double[] Target, double[,]? Past = null, double[,]? Known = null, double[]? Static = null);

/// <summary>
/// Temporal Fusion Transformer (Lim, Arık, Loeff and Pfister, 2021) for one or several target series with static,
/// past-observed and known-future covariates: per-variable embeddings, variable selection networks, static covariate
/// encoders, an LSTM encoder-decoder, static enrichment, gated residual connections, interpretable multi-head attention
/// (heads share the value projection, so the averaged attention is meaningful) under a causal mask, and quantile outputs
/// trained with the pinball loss.
/// </summary>
/// <remarks>
/// Built on GraviNum's reverse-mode autodiff with one graph per mini-batch (windows stacked as rows; attention runs over
/// all of them at once under a per-window causal mask), trained with Adam. Static covariates go through their own
/// variable selection network; four GRNs turn the selected static embedding into the paper's context vectors: c_s (context
/// of the encoder/decoder selection weights), c_e (context of the static-enrichment GRN after the LSTM), and c_c, c_h (initial
/// LSTM cell and hidden state). Without static covariates the model has no context vectors and no enrichment layer.
/// GELU replaces ELU inside the gated residual networks. A relative-position input is added to both encoder and decoder,
/// so the decoder has an input even without known covariates. Each series' target is standardised by its own training
/// mean and standard deviation (forecasts are un-scaled with the same statistics); covariates and statics are
/// standardised with statistics pooled over all training series.
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
    private readonly Dense[] _embedStatic = Array.Empty<Dense>();
    private readonly Vsn? _vsnStatic;
    private readonly Grn? _ctxSelection, _ctxEnrichment, _ctxCell, _ctxHidden, _enrichment;
    private double[] _yMean = { 0 }, _yStd = { 1 };
    private double[][] _static = { Array.Empty<double>() };
    private double[] _pMean = Array.Empty<double>(), _pStd = Array.Empty<double>(), _kMean = Array.Empty<double>(), _kStd = Array.Empty<double>();
    private double[] _sMean = Array.Empty<double>(), _sStd = Array.Empty<double>();

    public TemporalFusionTransformer(int lookback, int horizon, int pastCovariates = 0, int knownCovariates = 0, TftOptions? options = null,
        IReadOnlyList<string>? pastNames = null, IReadOnlyList<string>? knownNames = null, int staticCovariates = 0,
        IReadOnlyList<string>? staticNames = null)
    {
        if (lookback < 2 || horizon < 1) throw new ArgumentException("Need lookback ≥ 2 and horizon ≥ 1.");
        if (staticCovariates < 0) throw new ArgumentException("Static covariate count cannot be negative.");
        Lookback = lookback;
        Horizon = horizon;
        PastCovariates = pastCovariates;
        KnownCovariates = knownCovariates;
        StaticCovariates = staticCovariates;
        Options = options ?? new TftOptions();
        _d = Options.Hidden;
        var rng = new FastRandom(Options.Seed);
        var known = Enumerable.Range(0, knownCovariates).Select(i => knownNames?[i] ?? $"known{i + 1}").ToArray();
        EncoderVariables = new[] { "target" }.Concat(Enumerable.Range(0, pastCovariates).Select(i => pastNames?[i] ?? $"past{i + 1}"))
            .Concat(known).Append("position").ToArray();
        DecoderVariables = known.Append("position").ToArray();
        StaticVariables = Enumerable.Range(0, staticCovariates).Select(i => staticNames?[i] ?? $"static{i + 1}").ToArray();

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

        // Static path last, so a model without statics is parameter-for-parameter the same as before, and the static
        // parameters are a contiguous tail of the parameter list.
        StaticParameterStart = _ps.Tensors.Count;
        if (staticCovariates > 0)
        {
            _embedStatic = StaticVariables.Select(_ => new Dense(_ps, 1, _d, rng)).ToArray();
            _vsnStatic = new Vsn(_ps, staticCovariates, _d, rng);
            _ctxSelection = new Grn(_ps, _d, _d, rng);
            _ctxEnrichment = new Grn(_ps, _d, _d, rng);
            _ctxCell = new Grn(_ps, _d, _d, rng);
            _ctxHidden = new Grn(_ps, _d, _d, rng);
            _vsnEnc.AttachContext(_ps, _d, rng);
            _vsnDec.AttachContext(_ps, _d, rng);
            _enrichment = new Grn(_ps, _d, _d, rng);
            _enrichment.AttachContext(_ps, _d, rng);
        }
    }

    public int Lookback { get; }
    public int Horizon { get; }
    public int PastCovariates { get; }
    public int KnownCovariates { get; }
    public int StaticCovariates { get; }
    public TftOptions Options { get; }
    public string[] EncoderVariables { get; }
    public string[] DecoderVariables { get; }
    public string[] StaticVariables { get; }
    /// <summary>Number of series the model was last trained on (1 after the single-series <see cref="Fit(ReadOnlySpan{double}, double[,], double[,])"/>).</summary>
    public int SeriesCount => _yMean.Length;
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

    /// <summary>
    /// Gated residual network: LayerNorm(skip(a) + GLU(W₁·GELU(W₂·a + W₃·c))); the context term W₃·c (no bias) exists
    /// once <see cref="AttachContext"/> has been called.
    /// </summary>
    private sealed class Grn
    {
        private readonly Dense _w2, _w1;
        private readonly Dense? _skip;
        private readonly Glu _glu;
        private readonly Norm _norm;
        private readonly int _output;
        private Tensor? _w3;

        public Grn(NeuralParameters ps, int input, int output, FastRandom rng)
        {
            _output = output;
            _w2 = new Dense(ps, input, output, rng);
            _w1 = new Dense(ps, output, output, rng);
            _skip = input == output ? null : new Dense(ps, input, output, rng);
            _glu = new Glu(ps, output, rng);
            _norm = new Norm(ps, output);
        }

        public void AttachContext(NeuralParameters ps, int context, FastRandom rng) => _w3 = ps.Glorot(context, _output, rng);

        public Tensor Forward(Tensor a, Tensor? context = null)
        {
            var pre = _w2.Forward(a);
            if (_w3 is not null && context is not null) pre = pre + context.MatMul(_w3);
            return _norm.Forward((_skip?.Forward(a) ?? a) + _glu.Forward(_w1.Forward(Gelu(pre))));
        }
    }

    /// <summary>
    /// Variable selection network: softmax weights from a GRN over all embeddings (optionally conditioned on a static
    /// context), each embedding refined by its own GRN.
    /// </summary>
    private sealed class Vsn
    {
        private readonly Grn _weights;
        private readonly Grn[] _each;

        public Vsn(NeuralParameters ps, int variables, int d, FastRandom rng)
        {
            _weights = new Grn(ps, variables * d, variables, rng);
            _each = Enumerable.Range(0, variables).Select(_ => new Grn(ps, d, d, rng)).ToArray();
        }

        public void AttachContext(NeuralParameters ps, int context, FastRandom rng) => _weights.AttachContext(ps, context, rng);

        public (Tensor Output, double[] Weights) Forward(Tensor[] embeddings, Tensor? context = null)
        {
            var flat = embeddings[0];
            for (int i = 1; i < embeddings.Length; i++) flat = TensorOps.ConcatColumns(flat, embeddings[i]);
            var w = Softmax(_weights.Forward(flat, context));
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

    /// <summary>Stacks [r, d] blocks into a [Σr, d] matrix (via columns of the transpose, the op the tape has).</summary>
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

    /// <summary>One static variable across the batch as a [batch, 1] column.</summary>
    private static Tensor Column(double[][] statics, int v)
    {
        var data = new double[statics.Length];
        for (int b = 0; b < statics.Length; b++) data[b] = statics[b][v];
        return Tensor.Constant(new NdArray(data, statics.Length, 1));
    }

    /// <summary>
    /// Batched forward pass on standardised inputs: <paramref name="enc"/>[b][t][v] (past steps), <paramref name="dec"/>[b][h][v]
    /// (future steps), <paramref name="statics"/>[b][s] (static covariates; ignored when the model has none). Every step
    /// runs on [batch, d] matrices; attention runs once over all windows stacked as [T·batch, d] rows (row = t·batch + b)
    /// with a mask that keeps each window to itself and causal. Returns [horizon·batch, quantiles] (row = h·batch + b) plus
    /// the first window's attention and the batch-average variable weights.
    /// </summary>
    private (Tensor Output, double[,] Attention, double[] EncImportance, double[] DecImportance, double[] StaticImportance) Forward(
        double[][][] enc, double[][][] dec, double[][]? statics = null)
    {
        int B = enc.Length, L = enc[0].Length, H = dec[0].Length, T = L + H;

        // Static covariate encoders: selected static embedding ζ → context vectors c_s, c_e, c_c, c_h ([B, d] each).
        Tensor? cs = null, ce = null;
        Tensor h = Tensor.Constant(NdArray.Zeros(B, _d)), c = Tensor.Constant(NdArray.Zeros(B, _d));
        var statImp = new double[StaticCovariates];
        if (StaticCovariates > 0)
        {
            if (statics is null || statics.Length != B) throw new ArgumentException("Static covariates are required for every window.");
            var embeddings = _embedStatic.Select((e, v) => e.Forward(Column(statics, v))).ToArray();
            var (zeta, w) = _vsnStatic!.Forward(embeddings);
            for (int b = 0; b < B; b++)
                for (int v = 0; v < StaticCovariates; v++) statImp[v] += w[b * StaticCovariates + v] / B;
            cs = _ctxSelection!.Forward(zeta);
            ce = _ctxEnrichment!.Forward(zeta);
            c = _ctxCell!.Forward(zeta);
            h = _ctxHidden!.Forward(zeta);
        }

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
            var (o, w) = (past ? _vsnEnc : _vsnDec).Forward(embeddings, cs);
            selected.Add(o);
            var imp = past ? encImp : decImp;
            int vars = imp.Length;
            for (int b = 0; b < B; b++)
                for (int v = 0; v < vars; v++) imp[v] += w[b * vars + v] / (B * (past ? L : H));
        }

        var phi = new List<Tensor>(T);
        for (int t = 0; t < T; t++)
        {
            (h, c) = LstmStep(t < L ? _lstmEnc : _lstmDec, selected[t], h, c);
            phi.Add(_normLstm.Forward(selected[t] + _gateLstm.Forward(h)));
        }
        var Phi = StackRows(phi);                                              // [T·B, d], row = t·B + b
        // Static enrichment θ = GRN(φ, c_e), with c_e repeated for every time step.
        var Theta = ce is null ? Phi : _enrichment!.Forward(Phi, StackRows(Enumerable.Repeat(ce, T).ToArray()));

        int N = T * B;
        var mask = new double[N * N];
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
                if (i % B != j % B || j / B > i / B) mask[i * N + j] = -1e9;   // other windows, or the future
        var maskT = Tensor.Constant(new NdArray(mask, N, N));
        var V = _wv.Forward(Theta);
        double scale = 1 / Math.Sqrt(V.Shape[1]);
        Tensor? mix = null;
        var attention = new double[H, T];
        foreach (var (wq, wk) in _wq.Zip(_wk))
        {
            var a = Softmax(wq.Forward(Theta).MatMul(wk.Forward(Theta).T()) * scale, maskT);
            var av = a.Value.ToArray();
            for (int r = 0; r < H; r++)
                for (int j = 0; j < T; j++) attention[r, j] += av[((L + r) * B) * N + j * B] / _wq.Length;   // window 0
            var head = a.MatMul(V);
            mix = mix is null ? head : mix + head;
        }
        var attn = _wo.Forward(mix! * (1.0 / _wq.Length));

        var delta = _normAttn.Forward(Theta + _gateAttn.Forward(attn));
        var psi = _positionwise.Forward(delta);
        var outRows = _normOut.Forward(Phi + _gateOut.Forward(psi));
        var decoderRows = TensorOps.SliceColumns(outRows.T(), L * B, H * B).T();   // [H·B, d], row = h·B + b
        return (_output.Forward(decoderRows), attention, encImp, decImp, statImp);
    }

    /// <summary>
    /// Test hook: the pinball loss on one fixed window and its autodiff gradient with respect to one parameter element,
    /// next to a central finite difference of the same loss. <paramref name="statics"/> are standardised static inputs.
    /// </summary>
    internal (double Analytic, double Numeric) CheckGradient(double[][] enc, double[][] dec, double[] truth, int parameter, int element,
        double h = 1e-5, double[]? statics = null)
    {
        var q = Options.Quantiles;
        double Loss(bool backward)
        {
            var (outp, _, _, _, _) = Forward(new[] { enc }, new[] { dec }, statics is null ? null : new[] { statics });
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
    /// <summary>Index of the first static-path parameter tensor (static embeddings, static VSN, context GRNs, context weights, enrichment).</summary>
    internal int StaticParameterStart { get; }
    internal int ParameterTensorSize(int parameter) => _ps.Tensors[parameter].Size;

    // ------------------------------------------------------------------ data

    private static (double Mean, double Std) Stats(IEnumerable<double> xs)
    {
        var a = xs.ToArray();
        double m = a.Average(), s = Math.Sqrt(a.Sum(v => (v - m) * (v - m)) / a.Length);
        return (m, s > 1e-12 ? s : 1);
    }

    private double[][] EncoderRows(ReadOnlySpan<double> y, double[,]? past, double[,]? known, int start, int series)
    {
        double yMean = _yMean[series], yStd = _yStd[series];
        var rows = new double[Lookback][];
        for (int t = 0; t < Lookback; t++)
        {
            int i = start + t;
            var r = new double[EncoderVariables.Length];
            int k = 0;
            r[k++] = (y[i] - yMean) / yStd;
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

    private double[] StaticRow(int series) => Enumerable.Range(0, StaticCovariates).Select(j => (_static[series][j] - _sMean[j]) / _sStd[j]).ToArray();

    private void CheckCovariates(int n, double[,]? past, double[,]? known)
    {
        if (PastCovariates > 0 && (past is null || past.GetLength(0) != n || past.GetLength(1) != PastCovariates))
            throw new ArgumentException($"Past covariates must be {n} × {PastCovariates}.");
        if (KnownCovariates > 0 && (known is null || known.GetLength(1) != KnownCovariates))
            throw new ArgumentException($"Known covariates need {KnownCovariates} columns.");
    }

    /// <summary>
    /// Trains on a target series with aligned covariates (rows = time, oldest first). <paramref name="known"/> must have the
    /// same rows as <paramref name="target"/>. Returns the final batch loss. Not available for models with static
    /// covariates (a single series' statics are constant; use <see cref="Fit(IReadOnlyList{TftSeries})"/>).
    /// </summary>
    public double Fit(ReadOnlySpan<double> target, double[,]? past = null, double[,]? known = null)
    {
        if (StaticCovariates > 0) throw new InvalidOperationException("A model with static covariates is trained on a list of TftSeries.");
        return Fit(new[] { new TftSeries(target.ToArray(), past, known) });
    }

    /// <summary>
    /// Trains one model on several series. Each mini-batch samples windows uniformly over all windows of all series. Each
    /// series' target is standardised by its own mean and standard deviation; covariates and static covariates use
    /// statistics pooled over the series. Known covariates must have the same rows as their target. Returns the final batch loss.
    /// </summary>
    public double Fit(IReadOnlyList<TftSeries> series)
    {
        if (series.Count == 0) throw new ArgumentException("Need at least one series.");
        foreach (var s in series)
        {
            int n = s.Target.Length;
            if (n < Lookback + Horizon + 1) throw new ArgumentException("Series shorter than lookback + horizon.");
            CheckCovariates(n, s.Past, s.Known);
            if (KnownCovariates > 0 && s.Known!.GetLength(0) != n) throw new ArgumentException("Known covariates must align with the target when training.");
            if (StaticCovariates > 0 && (s.Static is null || s.Static.Length != StaticCovariates))
                throw new ArgumentException($"Every series needs {StaticCovariates} static covariates.");
        }
        int S = series.Count;
        _yMean = new double[S]; _yStd = new double[S];
        for (int i = 0; i < S; i++) (_yMean[i], _yStd[i]) = Stats(series[i].Target);
        _pMean = new double[PastCovariates]; _pStd = new double[PastCovariates];
        for (int j = 0; j < PastCovariates; j++)
            (_pMean[j], _pStd[j]) = Stats(series.SelectMany(s => Enumerable.Range(0, s.Target.Length).Select(i => s.Past![i, j])));
        _kMean = new double[KnownCovariates]; _kStd = new double[KnownCovariates];
        for (int j = 0; j < KnownCovariates; j++)
            (_kMean[j], _kStd[j]) = Stats(series.SelectMany(s => Enumerable.Range(0, s.Target.Length).Select(i => s.Known![i, j])));
        _static = series.Select(s => s.Static?.ToArray() ?? Array.Empty<double>()).ToArray();
        _sMean = new double[StaticCovariates]; _sStd = new double[StaticCovariates];
        for (int j = 0; j < StaticCovariates; j++) (_sMean[j], _sStd[j]) = Stats(_static.Select(s => s[j]));
        var statics = Enumerable.Range(0, S).Select(StaticRow).ToArray();

        // Window w of the pooled index belongs to the series whose cumulative count first exceeds it.
        var offsets = new int[S + 1];
        for (int i = 0; i < S; i++) offsets[i + 1] = offsets[i] + series[i].Target.Length - Lookback - Horizon + 1;
        var rng = new FastRandom(Options.Seed + 1);
        var q = Options.Quantiles;
        double last = double.NaN;
        for (int step = 0; step < Options.Steps; step++)
        {
            int B = Options.BatchSize;
            var picks = Enumerable.Range(0, B).Select(_ =>
            {
                int w = rng.NextInt(offsets[S]);
                int s = Array.BinarySearch(offsets, w);
                s = s >= 0 ? s : ~s - 1;
                return (Series: s, Start: w - offsets[s]);
            }).ToArray();
            var (outp, _, _, _, _) = Forward(
                picks.Select(p => EncoderRows(series[p.Series].Target, series[p.Series].Past, series[p.Series].Known, p.Start, p.Series)).ToArray(),
                picks.Select(p => DecoderRows(series[p.Series].Known, p.Start + Lookback)).ToArray(),
                StaticCovariates > 0 ? picks.Select(p => statics[p.Series]).ToArray() : null);
            var truth = new double[Horizon * B * q.Length];
            var lev = new double[Horizon * B * q.Length];
            for (int h = 0; h < Horizon; h++)
                for (int b = 0; b < B; b++)
                    for (int k = 0; k < q.Length; k++)
                    {
                        var (s, start) = picks[b];
                        int i = (h * B + b) * q.Length + k;
                        truth[i] = (series[s].Target[start + Lookback + h] - _yMean[s]) / _yStd[s];
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
    /// target; known covariates must cover the target's rows plus the horizon. For a model trained on one series without
    /// static covariates; otherwise use <see cref="Predict(int, ReadOnlySpan{double}, double[,], double[,])"/>.
    /// </summary>
    public TftForecast Predict(ReadOnlySpan<double> target, double[,]? past = null, double[,]? known = null)
    {
        if (StaticCovariates > 0 || SeriesCount > 1)
            throw new InvalidOperationException("This model was trained on several series or with static covariates; pass the series index.");
        return Predict(0, target, past, known);
    }

    /// <summary>
    /// Forecasts the <see cref="Horizon"/> steps after <paramref name="target"/> (the recent history of training series
    /// <paramref name="series"/>), using that series' static covariates and target scaling.
    /// </summary>
    public TftForecast Predict(int series, ReadOnlySpan<double> target, double[,]? past = null, double[,]? known = null)
    {
        if (series < 0 || series >= SeriesCount) throw new ArgumentOutOfRangeException(nameof(series), $"The model knows {SeriesCount} series.");
        if (StaticCovariates > 0 && _static[series].Length != StaticCovariates) throw new InvalidOperationException("The model has not been trained.");
        int n = target.Length;
        if (n < Lookback) throw new ArgumentException($"Need at least {Lookback} observations.");
        CheckCovariates(n, past, known);
        if (KnownCovariates > 0 && known!.GetLength(0) < n + Horizon) throw new ArgumentException("Known covariates must extend over the horizon.");
        var (outp, attention, encImp, decImp, statImp) = Forward(new[] { EncoderRows(target, past, known, n - Lookback, series) },
            new[] { DecoderRows(known, n) }, StaticCovariates > 0 ? new[] { StaticRow(series) } : null);
        var v = outp.Value.ToArray();
        int nq = Options.Quantiles.Length;
        var quantiles = new double[Horizon, nq];
        for (int h = 0; h < Horizon; h++)
        {
            // Sort each step's quantiles so they never cross.
            var row = Enumerable.Range(0, nq).Select(k => v[h * nq + k] * _yStd[series] + _yMean[series]).Order().ToArray();
            for (int k = 0; k < nq; k++) quantiles[h, k] = row[k];
        }
        return new TftForecast(Options.Quantiles, quantiles, attention, EncoderVariables, encImp, DecoderVariables, decImp)
        {
            StaticVariables = StaticVariables,
            StaticImportance = statImp,
        };
    }
}
