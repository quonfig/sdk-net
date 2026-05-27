namespace Quonfig.Sdk.Eval;

/// <summary>
/// Wire-level type discriminator for a single config value. Mirrors sdk-java's <c>ValueType</c>
/// enum and sdk-go's value-type constants — the cross-SDK contract is that any value with
/// <c>Type = Int</c> carries a <see cref="long"/> payload, <c>Type = Double</c> a <see cref="double"/>,
/// and so on. Two non-leaf types — <see cref="WeightedValues"/> and <see cref="Provided"/> — wrap
/// payloads that the <see cref="Resolver"/> recurses through before producing a leaf value.
/// </summary>
public enum ValueType
{
    /// <summary>Boolean — payload is <see cref="bool"/>.</summary>
    Bool,
    /// <summary>64-bit integer — payload is <see cref="long"/>.</summary>
    Int,
    /// <summary>IEEE-754 double — payload is <see cref="double"/>.</summary>
    Double,
    /// <summary>String — payload is <see cref="string"/>.</summary>
    String,
    /// <summary>Ordered string list — payload is <see cref="System.Collections.Generic.IReadOnlyList{T}"/> of string.</summary>
    StringList,
    /// <summary>Logger-name → level string (e.g. <c>INFO</c>) — payload is <see cref="string"/>.</summary>
    LogLevel,
    /// <summary>ISO-8601 duration string (e.g. <c>PT1H30M</c>) — payload is <see cref="System.TimeSpan"/>.</summary>
    Duration,
    /// <summary>Parsed JSON — payload is a tree of <see cref="System.Collections.Generic.IDictionary{TKey,TValue}"/>, <see cref="System.Collections.Generic.IReadOnlyList{T}"/>, string, long, double, bool, or null.</summary>
    Json,
    /// <summary>Weighted-variant container — payload is <see cref="WeightedValuesPayload"/>; resolved via Murmur3 bucketing.</summary>
    WeightedValues,
    /// <summary>Provisioned-from-env-var container — payload is <see cref="ProvidedValue"/>.</summary>
    Provided,
}
