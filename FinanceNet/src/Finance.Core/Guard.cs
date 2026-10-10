using System.Runtime.CompilerServices;

namespace Finance.Core;

/// <summary>Argument validation helpers shared by every Finance.Net module.</summary>
public static class Guard
{
    public static void Positive(double value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (!(value > 0)) throw new ArgumentOutOfRangeException(name, value, "Value must be positive.");
    }

    public static void NonNegative(double value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (!(value >= 0)) throw new ArgumentOutOfRangeException(name, value, "Value must be non-negative.");
    }

    public static void Positive(int value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(name, value, "Value must be positive.");
    }

    public static void InRange(double value, double min, double max, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (!(value >= min && value <= max))
            throw new ArgumentOutOfRangeException(name, value, $"Value must be in [{min}, {max}].");
    }

    public static void SameLength(int a, int b, string what = "inputs")
    {
        if (a != b) throw new ArgumentException($"Length mismatch between {what}: {a} vs {b}.");
    }

    public static void MinLength(int length, int min, [CallerArgumentExpression(nameof(length))] string? name = null)
    {
        if (length < min) throw new ArgumentException($"{name} needs at least {min} elements, got {length}.");
    }
}
