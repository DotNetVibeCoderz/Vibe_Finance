using Finance.Core;
using Finance.Mathematics;

namespace Finance.ML;

/// <summary>Hyper-parameters shared by the tree learners.</summary>
public sealed record TreeOptions
{
    public int MaxDepth { get; init; } = 6;
    public int MinSamplesLeaf { get; init; } = 5;
    /// <summary>Features tried per split; 0 means all (random forests use √p for classification, p/3 for regression).</summary>
    public int MaxFeatures { get; init; }
    public ulong Seed { get; init; } = 17;
}

/// <summary>
/// CART decision tree. Splits are found by sorting each candidate feature once per node and
/// sweeping running sums, so a node costs O(n log n) per feature rather than O(n²).
/// Regression minimises squared error; classification (labels 0/1) minimises Gini impurity and
/// leaves hold the class-1 frequency.
/// </summary>
public class DecisionTree
{
    private readonly List<Node> _nodes = new();
    private readonly bool _classification;
    private readonly TreeOptions _options;
    private FastRandom _rng;

    protected DecisionTree(bool classification, TreeOptions? options)
    {
        _classification = classification;
        _options = options ?? new TreeOptions();
        _rng = new FastRandom(_options.Seed);
    }

    private struct Node
    {
        public int Feature;     // -1 for a leaf
        public double Threshold;
        public int Left, Right;
        public double Value;
    }

    /// <summary>Total impurity decrease attributed to each feature (sums to the root impurity reduction).</summary>
    public double[] FeatureImportances { get; private set; } = Array.Empty<double>();

    /// <summary>Number of nodes in the fitted tree.</summary>
    public int NodeCount => _nodes.Count;

    internal void FitIndices(Matrix x, ReadOnlySpan<double> y, int[] indices)
    {
        _nodes.Clear();
        _rng = new FastRandom(_options.Seed);
        FeatureImportances = new double[x.Columns];
        var yArr = y.ToArray();
        Build(x, yArr, indices, 0, indices.Length, 0);
    }

    protected void FitCore(Matrix x, ReadOnlySpan<double> y)
    {
        var idx = new int[x.Rows];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        FitIndices(x, y, idx);
    }

    protected double Evaluate(ReadOnlySpan<double> row)
    {
        if (_nodes.Count == 0) throw new InvalidOperationException("Tree is not fitted.");
        int n = 0;
        while (_nodes[n].Feature >= 0)
            n = row[_nodes[n].Feature] <= _nodes[n].Threshold ? _nodes[n].Left : _nodes[n].Right;
        return _nodes[n].Value;
    }

    private int Build(Matrix x, double[] y, int[] idx, int start, int end, int depth)
    {
        int count = end - start;
        double sum = 0, sumSq = 0;
        for (int i = start; i < end; i++) { double v = y[idx[i]]; sum += v; sumSq += v * v; }
        double mean = sum / count;
        int self = _nodes.Count;
        _nodes.Add(new Node { Feature = -1, Value = mean });

        if (depth >= _options.MaxDepth || count < 2 * _options.MinSamplesLeaf) return self;
        double parentImpurity = Impurity(sum, sumSq, count);
        if (parentImpurity <= 1e-14) return self;

        int p = x.Columns;
        int tries = _options.MaxFeatures <= 0 ? p : Math.Min(p, _options.MaxFeatures);
        var features = Enumerable.Range(0, p).ToArray();
        for (int i = 0; i < tries; i++)
        {
            int j = i + _rng.NextInt(p - i);
            (features[i], features[j]) = (features[j], features[i]);
        }

        int bestFeature = -1;
        double bestThreshold = 0, bestScore = parentImpurity * count;
        var order = new int[count];
        var keys = new double[count];
        for (int f = 0; f < tries; f++)
        {
            int feat = features[f];
            for (int i = 0; i < count; i++) { order[i] = idx[start + i]; keys[i] = x[order[i], feat]; }
            Array.Sort(keys, order);
            double ls = 0, lss = 0;
            for (int i = 0; i < count - 1; i++)
            {
                double v = y[order[i]];
                ls += v;
                lss += v * v;
                int nl = i + 1, nr = count - nl;
                if (nl < _options.MinSamplesLeaf || nr < _options.MinSamplesLeaf) continue;
                if (keys[i] == keys[i + 1]) continue;
                double score = Impurity(ls, lss, nl) * nl + Impurity(sum - ls, sumSq - lss, nr) * nr;
                if (score < bestScore - 1e-12)
                {
                    bestScore = score;
                    bestFeature = feat;
                    bestThreshold = 0.5 * (keys[i] + keys[i + 1]);
                }
            }
        }
        if (bestFeature < 0) return self;

        FeatureImportances[bestFeature] += parentImpurity * count - bestScore;
        int mid = start;
        for (int i = start; i < end; i++)
            if (x[idx[i], bestFeature] <= bestThreshold) { (idx[i], idx[mid]) = (idx[mid], idx[i]); mid++; }

        int left = Build(x, y, idx, start, mid, depth + 1);
        int right = Build(x, y, idx, mid, end, depth + 1);
        var node = _nodes[self];
        node.Feature = bestFeature;
        node.Threshold = bestThreshold;
        node.Left = left;
        node.Right = right;
        _nodes[self] = node;
        return self;
    }

    private double Impurity(double sum, double sumSq, int n)
    {
        if (n == 0) return 0;
        if (_classification)
        {
            double p = sum / n; // labels are 0/1
            return 2 * p * (1 - p);
        }
        double m = sum / n;
        return Math.Max(0, sumSq / n - m * m);
    }
}

/// <summary>CART regression tree.</summary>
public sealed class DecisionTreeRegressor(TreeOptions? options = null) : DecisionTree(false, options), IRegressor
{
    public void Fit(Matrix x, ReadOnlySpan<double> y) => FitCore(x, y);
    public double Predict(ReadOnlySpan<double> row) => Evaluate(row);
}

/// <summary>CART classification tree for labels 0/1.</summary>
public sealed class DecisionTreeClassifier(TreeOptions? options = null) : DecisionTree(true, options), IClassifier
{
    public void Fit(Matrix x, ReadOnlySpan<double> y) => FitCore(x, y);
    public double PredictProba(ReadOnlySpan<double> row) => Evaluate(row);
}

/// <summary>Bagged ensemble of randomised CART trees, trained in parallel.</summary>
public abstract class RandomForestBase
{
    protected DecisionTree[] Trees = Array.Empty<DecisionTree>();
    private readonly int _treeCount;
    private readonly TreeOptions _options;
    private readonly bool _classification;

    protected RandomForestBase(int trees, TreeOptions? options, bool classification)
    {
        _treeCount = trees;
        _options = options ?? new TreeOptions { MaxDepth = 8, MinSamplesLeaf = 3 };
        _classification = classification;
    }

    /// <summary>Mean impurity-based importance across trees, normalised to sum to 1.</summary>
    public double[] FeatureImportances { get; private set; } = Array.Empty<double>();

    protected void FitForest(Matrix x, ReadOnlySpan<double> y)
    {
        int p = x.Columns, n = x.Rows;
        int maxF = _options.MaxFeatures > 0 ? _options.MaxFeatures
            : Math.Max(1, _classification ? (int)Math.Sqrt(p) : p / 3);
        var yArr = y.ToArray();
        Trees = new DecisionTree[_treeCount];
        Parallel.For(0, _treeCount, t =>
        {
            var rng = new FastRandom(_options.Seed + (ulong)t * 7919);
            var boot = new int[n];
            for (int i = 0; i < n; i++) boot[i] = rng.NextInt(n);
            var opts = _options with { MaxFeatures = maxF, Seed = _options.Seed + (ulong)t * 104729 };
            DecisionTree tree = _classification ? new DecisionTreeClassifier(opts) : new DecisionTreeRegressor(opts);
            tree.FitIndices(x, yArr, boot);
            Trees[t] = tree;
        });
        var imp = new double[p];
        foreach (var tree in Trees)
            for (int j = 0; j < p; j++) imp[j] += tree.FeatureImportances[j];
        double total = imp.Sum();
        if (total > 0) for (int j = 0; j < p; j++) imp[j] /= total;
        FeatureImportances = imp;
    }
}

/// <summary>Random forest regressor.</summary>
public sealed class RandomForestRegressor(int trees = 100, TreeOptions? options = null) : RandomForestBase(trees, options, false), IRegressor
{
    public void Fit(Matrix x, ReadOnlySpan<double> y) => FitForest(x, y);

    public double Predict(ReadOnlySpan<double> row)
    {
        double s = 0;
        foreach (var t in Trees) s += ((DecisionTreeRegressor)t).Predict(row);
        return s / Trees.Length;
    }
}

/// <summary>Random forest classifier (labels 0/1); probability is the mean leaf frequency.</summary>
public sealed class RandomForestClassifier(int trees = 100, TreeOptions? options = null) : RandomForestBase(trees, options, true), IClassifier
{
    public void Fit(Matrix x, ReadOnlySpan<double> y) => FitForest(x, y);

    public double PredictProba(ReadOnlySpan<double> row)
    {
        double s = 0;
        foreach (var t in Trees) s += ((DecisionTreeClassifier)t).PredictProba(row);
        return s / Trees.Length;
    }
}

/// <summary>Gradient boosting with squared loss: each tree fits the current residuals.</summary>
public sealed class GradientBoostingRegressor(int estimators = 200, double learningRate = 0.05, TreeOptions? options = null) : IRegressor
{
    private readonly TreeOptions _options = options ?? new TreeOptions { MaxDepth = 3, MinSamplesLeaf = 5 };
    private readonly List<DecisionTreeRegressor> _trees = new();
    private double _base;

    /// <summary>Training MSE after each stage.</summary>
    public List<double> TrainingLoss { get; } = new();

    public void Fit(Matrix x, ReadOnlySpan<double> y)
    {
        _trees.Clear();
        TrainingLoss.Clear();
        int n = x.Rows;
        _base = Stats.Mean(y);
        var f = Enumerable.Repeat(_base, n).ToArray();
        var r = new double[n];
        for (int m = 0; m < estimators; m++)
        {
            for (int i = 0; i < n; i++) r[i] = y[i] - f[i];
            var tree = new DecisionTreeRegressor(_options with { Seed = _options.Seed + (ulong)m });
            tree.Fit(x, r);
            _trees.Add(tree);
            double loss = 0;
            for (int i = 0; i < n; i++)
            {
                f[i] += learningRate * tree.Predict(x.Row(i));
                loss += (y[i] - f[i]) * (y[i] - f[i]);
            }
            TrainingLoss.Add(loss / n);
        }
    }

    public double Predict(ReadOnlySpan<double> row)
    {
        double s = _base;
        foreach (var t in _trees) s += learningRate * t.Predict(row);
        return s;
    }
}
