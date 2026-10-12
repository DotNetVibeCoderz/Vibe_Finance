using System.Text;
using Finance.DataFrame;
using Finance.TimeSeries.Indicators;

namespace Finance.RustBridge;

/// <summary>
/// The whole <see cref="IndicatorCatalog"/> on the Rust accelerator: same ids, parameters, output names and values
/// (to floating-point rounding) as the managed definitions, which remain the fallback when the native library is
/// missing, disabled, or does not implement an id.
/// </summary>
public static class FastCatalog
{
    private const int MaxOutputs = 16;
    private static readonly Lazy<HashSet<string>> NativeSet = new(LoadIds);

    /// <summary>Ids the loaded native library implements (empty when it is unavailable).</summary>
    public static IReadOnlyCollection<string> NativeIds => RustAccelerator.IsLoaded ? NativeSet.Value : Array.Empty<string>();

    /// <summary>True when <paramref name="id"/> will run natively.</summary>
    public static bool IsNative(string id) => RustAccelerator.IsAvailable && NativeSet.Value.Contains(id);

    /// <summary>Computes catalogue entry <paramref name="id"/>; missing parameters take their defaults.</summary>
    public static IndicatorSeries[] Compute(string id, OhlcvFrame frame, IReadOnlyDictionary<string, double>? parameters = null)
    {
        var def = IndicatorCatalog.Get(id);
        if (!IsNative(def.Id)) return def.Compute(frame, parameters);
        var values = def.Parameters.Select(p => parameters is not null && TryGet(parameters, p.Name, out var v) ? v : p.Default).ToArray();
        return ComputeNative(def.Id, frame, values) ?? def.Compute(frame, parameters);
    }

    /// <summary>Native computation only (no fallback); null when the library rejects the arguments.</summary>
    internal static unsafe IndicatorSeries[]? ComputeNative(string id, OhlcvFrame frame, double[] parameters)
    {
        int n = frame.Count;
        var output = new double[MaxOutputs * Math.Max(n, 1)];
        var names = new byte[4096];
        var idBytes = Encoding.UTF8.GetBytes(id);
        int status;
        fixed (byte* pid = idBytes)
        fixed (double* o = frame.Open, h = frame.High, l = frame.Low, c = frame.Close, v = frame.Volume, ps = parameters, outp = output)
        fixed (byte* pn = names)
        {
            status = NativeMethods.Indicator(pid, (nuint)idBytes.Length, o, h, l, c, v, (nuint)n, ps, (nuint)parameters.Length,
                outp, MaxOutputs, pn, (nuint)names.Length);
        }
        if (status == NativeMethods.ErrArg || status == NativeMethods.ErrUnknown) return null;
        if (status < 0) NativeMethods.Check(status, "fnat_indicator");
        var labels = Encoding.UTF8.GetString(names).TrimEnd('\0').Split('\n');
        var result = new IndicatorSeries[status];
        for (int k = 0; k < status; k++) result[k] = new IndicatorSeries(labels[k], output.AsSpan(k * n, n).ToArray());
        return result;
    }

    private static bool TryGet(IReadOnlyDictionary<string, double> d, string name, out double value)
    {
        if (d.TryGetValue(name, out value)) return true;
        foreach (var (k, v) in d)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) { value = v; return true; }
        return false;
    }

    private static unsafe HashSet<string> LoadIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!RustAccelerator.IsLoaded) return set;
        try
        {
            int len = NativeMethods.IndicatorIds(null, 0);
            if (len <= 0) return set;
            var buf = new byte[len];
            fixed (byte* b = buf) NativeMethods.IndicatorIds(b, (nuint)len);
            foreach (var id in Encoding.UTF8.GetString(buf).Split('\n', StringSplitOptions.RemoveEmptyEntries)) set.Add(id);
        }
        catch (EntryPointNotFoundException)
        {
            // An older native library without the catalogue: everything falls back to managed code.
        }
        return set;
    }
}
