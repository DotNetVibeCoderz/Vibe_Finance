using System.Runtime.InteropServices;

namespace Finance.RustBridge;

/// <summary>Source-generated bindings to the <c>finance_native</c> Rust library.</summary>
internal static unsafe partial class NativeMethods
{
    public const string Library = "finance_native";

    public const int Ok = 0;
    public const int ErrNull = -1;
    public const int ErrArg = -2;
    public const int ErrPanic = -3;

    [LibraryImport(Library, EntryPoint = "fnat_version")]
    public static partial uint Version();

    [LibraryImport(Library, EntryPoint = "fnat_sma")]
    public static partial int Sma(double* x, nuint n, nuint period, double* output);

    [LibraryImport(Library, EntryPoint = "fnat_ema")]
    public static partial int Ema(double* x, nuint n, nuint period, double* output);

    [LibraryImport(Library, EntryPoint = "fnat_rsi")]
    public static partial int Rsi(double* x, nuint n, nuint period, double* output);

    [LibraryImport(Library, EntryPoint = "fnat_rolling_std")]
    public static partial int RollingStd(double* x, nuint n, nuint period, nuint ddof, double* output);

    [LibraryImport(Library, EntryPoint = "fnat_bollinger")]
    public static partial int Bollinger(double* x, nuint n, nuint period, double k, double* middle, double* upper, double* lower);

    [LibraryImport(Library, EntryPoint = "fnat_log_returns")]
    public static partial int LogReturns(double* x, nuint n, double* output);

    [LibraryImport(Library, EntryPoint = "fnat_max_drawdown")]
    public static partial int MaxDrawdown(double* x, nuint n, double* depth, nuint* peak, nuint* trough);

    [LibraryImport(Library, EntryPoint = "fnat_historical_var")]
    public static partial int HistoricalVaR(double* returns, nuint n, double confidence, double* var, double* cvar);

    [LibraryImport(Library, EntryPoint = "fnat_covariance")]
    public static partial int Covariance(double* data, nuint rows, nuint cols, nuint ddof, double* output);

    [LibraryImport(Library, EntryPoint = "fnat_black_scholes_batch")]
    public static partial int BlackScholesBatch(
        double* spot, double* strike, double* time, double* rate, double* dividend, double* vol, byte* isCall, nuint n,
        double* price, double* delta, double* gamma, double* vega, double* theta, double* rho);

    [LibraryImport(Library, EntryPoint = "fnat_mc_european")]
    public static partial int MonteCarloEuropean(
        double spot, double strike, double time, double rate, double dividend, double vol, byte isCall, byte antithetic,
        ulong paths, ulong seed, uint threads, double* price, double* stdError);

    [LibraryImport(Library, EntryPoint = "fnat_simulate_gbm")]
    public static partial int SimulateGbm(double s0, double mu, double sigma, double dt, nuint steps, nuint paths, ulong seed, uint threads, double* output);

    /// <summary>Turns a status code into an exception.</summary>
    public static void Check(int status, string function)
    {
        switch (status)
        {
            case Ok: return;
            case ErrNull: throw new ArgumentNullException(function, "finance_native received a null buffer.");
            case ErrArg: throw new ArgumentException($"finance_native rejected the arguments of {function}.");
            case ErrPanic: throw new InvalidOperationException($"finance_native panicked inside {function}.");
            default: throw new InvalidOperationException($"finance_native returned unknown status {status} from {function}.");
        }
    }
}
