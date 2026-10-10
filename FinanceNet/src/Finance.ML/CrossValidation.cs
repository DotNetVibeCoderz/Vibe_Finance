using Finance.Mathematics;

namespace Finance.ML;

/// <summary>A train/test split of row indices.</summary>
public sealed record Split(int[] Train, int[] Test);

/// <summary>Time-series aware cross-validation.</summary>
public static class CrossValidation
{
    /// <summary>
    /// Purged K-fold (AFML ch. 7). Folds are contiguous test blocks; training rows whose label
    /// window [i, i + labelHorizon] overlaps a test block are purged, and an embargo of
    /// <paramref name="embargoFraction"/> × n rows after each test block is also removed.
    /// </summary>
    public static IEnumerable<Split> PurgedKFold(int n, int folds = 5, int labelHorizon = 1, double embargoFraction = 0.01)
    {
        if (folds < 2) throw new ArgumentOutOfRangeException(nameof(folds));
        int embargo = (int)Math.Ceiling(n * embargoFraction);
        for (int f = 0; f < folds; f++)
        {
            int start = (int)((long)n * f / folds), end = (int)((long)n * (f + 1) / folds);
            var test = Enumerable.Range(start, end - start).ToArray();
            var train = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (i >= start && i < end) continue;
                bool overlaps = i < start && i + labelHorizon >= start;
                bool embargoed = i >= end && i < end + embargo;
                if (!overlaps && !embargoed) train.Add(i);
            }
            yield return new Split(train.ToArray(), test);
        }
    }

    /// <summary>
    /// Walk-forward splits: train on a window (expanding or rolling) and test on the next block.
    /// A gap of <paramref name="gap"/> rows separates train and test to avoid label leakage.
    /// </summary>
    public static IEnumerable<Split> WalkForward(int n, int trainSize, int testSize, bool expanding = true, int gap = 0)
    {
        for (int testStart = trainSize + gap; testStart < n; testStart += testSize)
        {
            int trainEnd = testStart - gap;
            int trainStart = expanding ? 0 : trainEnd - trainSize;
            int testEnd = Math.Min(n, testStart + testSize);
            yield return new Split(Enumerable.Range(trainStart, trainEnd - trainStart).ToArray(),
                Enumerable.Range(testStart, testEnd - testStart).ToArray());
        }
    }

    /// <summary>Scores a regressor over splits with a metric (higher or lower is better depending on the metric).</summary>
    public static double[] Evaluate(Func<IRegressor> factory, Matrix x, double[] y, IEnumerable<Split> splits, Func<double[], double[], double>? metric = null)
    {
        metric ??= (a, b) => Metrics.MeanSquaredError(a, b);
        var scores = new List<double>();
        foreach (var s in splits)
        {
            var (xt, yt) = Take(x, y, s.Train);
            var (xv, yv) = Take(x, y, s.Test);
            var model = factory();
            model.Fit(xt, yt);
            scores.Add(metric(yv, model.PredictAll(xv)));
        }
        return scores.ToArray();
    }

    /// <summary>Selects rows of a matrix and target vector.</summary>
    public static (Matrix X, double[] Y) Take(Matrix x, double[] y, IReadOnlyList<int> rows)
    {
        var m = new Matrix(rows.Count, x.Columns);
        var v = new double[rows.Count];
        for (int i = 0; i < rows.Count; i++) { x.Row(rows[i]).CopyTo(m.Row(i)); v[i] = y[rows[i]]; }
        return (m, v);
    }
}
