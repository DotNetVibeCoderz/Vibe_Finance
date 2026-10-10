using Finance.Mathematics;

namespace Finance.ML;

/// <summary>A model that maps a feature row to a real value.</summary>
public interface IRegressor
{
    /// <summary>Fits the model on rows of <paramref name="x"/> and targets <paramref name="y"/>.</summary>
    void Fit(Matrix x, ReadOnlySpan<double> y);

    /// <summary>Predicts one row.</summary>
    double Predict(ReadOnlySpan<double> row);
}

/// <summary>A binary classifier with labels 0/1.</summary>
public interface IClassifier
{
    /// <summary>Fits on rows of <paramref name="x"/> with labels 0 or 1 in <paramref name="y"/>.</summary>
    void Fit(Matrix x, ReadOnlySpan<double> y);

    /// <summary>Probability that the row belongs to class 1.</summary>
    double PredictProba(ReadOnlySpan<double> row);

    /// <summary>Predicted label (0 or 1) at a 0.5 threshold.</summary>
    double Predict(ReadOnlySpan<double> row) => PredictProba(row) >= 0.5 ? 1 : 0;
}

/// <summary>Batch helpers over models.</summary>
public static class ModelExtensions
{
    /// <summary>Predicts every row of <paramref name="x"/>.</summary>
    public static double[] PredictAll(this IRegressor model, Matrix x)
    {
        var p = new double[x.Rows];
        for (int i = 0; i < x.Rows; i++) p[i] = model.Predict(x.Row(i));
        return p;
    }

    /// <summary>Class-1 probabilities for every row of <paramref name="x"/>.</summary>
    public static double[] PredictProbaAll(this IClassifier model, Matrix x)
    {
        var p = new double[x.Rows];
        for (int i = 0; i < x.Rows; i++) p[i] = model.PredictProba(x.Row(i));
        return p;
    }

    /// <summary>Adapts a classifier to a regressor that outputs the class-1 probability (for SHAP/PDP).</summary>
    public static IRegressor AsProbabilityRegressor(this IClassifier model) => new ProbaAdapter(model);

    private sealed class ProbaAdapter(IClassifier inner) : IRegressor
    {
        public void Fit(Matrix x, ReadOnlySpan<double> y) => inner.Fit(x, y);
        public double Predict(ReadOnlySpan<double> row) => inner.PredictProba(row);
    }
}

/// <summary>A feature matrix with column names and the time stamp of each row.</summary>
public sealed class FeatureMatrix
{
    public FeatureMatrix(Matrix values, string[] names, DateTime[] times)
    {
        if (names.Length != values.Columns) throw new ArgumentException("One name per column is required.");
        if (times.Length != values.Rows) throw new ArgumentException("One time per row is required.");
        Values = values;
        Names = names;
        Times = times;
    }

    public Matrix Values { get; }
    public string[] Names { get; }
    public DateTime[] Times { get; }
    public int Rows => Values.Rows;
    public int Columns => Values.Columns;

    /// <summary>Column index for a feature name, or -1.</summary>
    public int IndexOf(string name) => Array.IndexOf(Names, name);

    /// <summary>A copy of the named column.</summary>
    public double[] Column(string name)
    {
        int i = IndexOf(name);
        if (i < 0) throw new KeyNotFoundException(name);
        return Values.Column(i);
    }

    /// <summary>Selects a subset of rows.</summary>
    public FeatureMatrix SelectRows(IReadOnlyList<int> rows)
    {
        var m = new Matrix(rows.Count, Columns);
        var t = new DateTime[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            Values.Row(rows[i]).CopyTo(m.Row(i));
            t[i] = Times[rows[i]];
        }
        return new FeatureMatrix(m, Names, t);
    }

    /// <summary>Column-wise z-score standardisation; returns the scaler so test data can reuse it.</summary>
    public Standardizer FitStandardizer() => Standardizer.Fit(Values);
}

/// <summary>Column-wise (x - mean) / std scaler.</summary>
public sealed class Standardizer
{
    private Standardizer(double[] mean, double[] std) { Mean = mean; Std = std; }

    public double[] Mean { get; }
    public double[] Std { get; }

    public static Standardizer Fit(Matrix x)
    {
        var mean = new double[x.Columns];
        var std = new double[x.Columns];
        for (int j = 0; j < x.Columns; j++)
        {
            var c = x.Column(j);
            mean[j] = Stats.Mean(c);
            double s = Stats.StdDev(c);
            std[j] = s > 1e-15 ? s : 1;
        }
        return new Standardizer(mean, std);
    }

    public Matrix Transform(Matrix x)
    {
        var r = new Matrix(x.Rows, x.Columns);
        for (int i = 0; i < x.Rows; i++)
            for (int j = 0; j < x.Columns; j++) r[i, j] = (x[i, j] - Mean[j]) / Std[j];
        return r;
    }

    public double[] Transform(ReadOnlySpan<double> row)
    {
        var r = new double[row.Length];
        for (int j = 0; j < row.Length; j++) r[j] = (row[j] - Mean[j]) / Std[j];
        return r;
    }
}

/// <summary>Classification and regression scoring.</summary>
public static class Metrics
{
    public static double MeanSquaredError(ReadOnlySpan<double> y, ReadOnlySpan<double> p)
    {
        double s = 0;
        for (int i = 0; i < y.Length; i++) { double d = y[i] - p[i]; s += d * d; }
        return s / y.Length;
    }

    public static double RSquared(ReadOnlySpan<double> y, ReadOnlySpan<double> p)
    {
        double m = Stats.Mean(y), ss = 0, sr = 0;
        for (int i = 0; i < y.Length; i++) { ss += (y[i] - m) * (y[i] - m); sr += (y[i] - p[i]) * (y[i] - p[i]); }
        return ss > 0 ? 1 - sr / ss : 0;
    }

    public static double Accuracy(ReadOnlySpan<double> y, ReadOnlySpan<double> predictedLabels)
    {
        int ok = 0;
        for (int i = 0; i < y.Length; i++) if (y[i] == predictedLabels[i]) ok++;
        return (double)ok / y.Length;
    }

    /// <summary>Binary log loss for probabilities <paramref name="p"/>.</summary>
    public static double LogLoss(ReadOnlySpan<double> y, ReadOnlySpan<double> p)
    {
        double s = 0;
        for (int i = 0; i < y.Length; i++)
        {
            double q = Math.Clamp(p[i], 1e-15, 1 - 1e-15);
            s -= y[i] * Math.Log(q) + (1 - y[i]) * Math.Log(1 - q);
        }
        return s / y.Length;
    }

    /// <summary>Area under the ROC curve (Mann-Whitney rank formulation, ties averaged).</summary>
    public static double RocAuc(ReadOnlySpan<double> y, ReadOnlySpan<double> score)
    {
        var ranks = Stats.Rank(score);
        double pos = 0, sumPos = 0;
        for (int i = 0; i < y.Length; i++) if (y[i] == 1) { pos++; sumPos += ranks[i]; }
        double neg = y.Length - pos;
        if (pos == 0 || neg == 0) return double.NaN;
        return (sumPos - pos * (pos + 1) / 2) / (pos * neg);
    }
}
