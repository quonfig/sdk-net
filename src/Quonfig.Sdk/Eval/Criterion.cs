namespace Quonfig.Sdk.Eval;

/// <summary>
/// A single condition inside a rule. Mirrors sdk-java's <c>Criterion</c> — a
/// dotted-property name (may be null for property-less operators like <c>ALWAYS_TRUE</c>,
/// <c>IN_SEG</c>), an operator string (see <see cref="Operators"/>), and the
/// <see cref="Value"/> against which the criterion is matched.
/// </summary>
public sealed class Criterion
{
    /// <summary>Dotted context-property name, or null for property-less operators.</summary>
    public string? PropertyName { get; }

    /// <summary>Operator string — one of the constants on <see cref="Operators"/>.</summary>
    public string Operator { get; }

    /// <summary>The right-hand side of the comparison; null for operators that don't need one.</summary>
    public Value? ValueToMatch { get; }

    /// <summary>Initializes a new criterion.</summary>
    public Criterion(string? propertyName, string @operator, Value? valueToMatch)
    {
        PropertyName = propertyName;
#if NET8_0_OR_GREATER
        System.ArgumentNullException.ThrowIfNull(@operator);
#else
        if (@operator is null) throw new System.ArgumentNullException(nameof(@operator));
#endif
        Operator = @operator;
        ValueToMatch = valueToMatch;
    }
}
