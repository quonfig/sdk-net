using System;

namespace Quonfig.Sdk;

/// <summary>
/// Result of a <see cref="ContextSet.GetContextValue"/> lookup. Encodes the distinction between
/// "this property was not in the bag" and "this property was in the bag but its value was null"
/// without forcing callers to thread an out-parameter.
/// </summary>
public readonly struct ContextLookup : IEquatable<ContextLookup>
{
    /// <summary>Whether the path existed in the context set.</summary>
    public bool Exists { get; }

    /// <summary>The value at the path, or null when <see cref="Exists"/> is false.</summary>
    public ContextValue? Value { get; }

    /// <summary>Initializes a new lookup result.</summary>
    public ContextLookup(bool exists, ContextValue? value)
    {
        Exists = exists;
        Value = value;
    }

    /// <summary>A singleton absent lookup.</summary>
    public static ContextLookup Absent { get; } = new(false, null);

    /// <summary>Builds a present lookup.</summary>
    public static ContextLookup Present(ContextValue? value) => new(true, value);

    /// <inheritdoc/>
    public bool Equals(ContextLookup other) => Exists == other.Exists && Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ContextLookup other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (Exists.GetHashCode() * 397) ^ (Value?.GetHashCode() ?? 0);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ContextLookup left, ContextLookup right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ContextLookup left, ContextLookup right) => !left.Equals(right);
}
