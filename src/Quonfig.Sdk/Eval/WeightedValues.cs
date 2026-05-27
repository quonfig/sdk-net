using System.Collections.Generic;

namespace Quonfig.Sdk.Eval;

/// <summary>
/// Payload for a <see cref="ValueType.WeightedValues"/> value: a list of weighted variants plus
/// optional <see cref="HashByPropertyName"/> selecting which context property feeds Murmur3.
/// Matches sdk-java <c>WeightedValuesData</c> and sdk-go <c>WeightedValuesData</c> on the wire.
/// </summary>
public sealed class WeightedValuesPayload
{
    /// <summary>
    /// Context property whose value is concatenated with the config key and Murmur3-hashed to pick
    /// a bucket deterministically. Null/empty means non-deterministic / fall-back-to-bucket-zero
    /// (matches sdk-java's behavior for missing properties — see <c>Murmur3WeightedValueResolverTest</c>).
    /// </summary>
    public string? HashByPropertyName { get; }

    /// <summary>Ordered list of variants. Order matters — the cumulative-weight walk visits them in order.</summary>
    public IReadOnlyList<WeightedVariant> Variants { get; }

    /// <summary>Initializes a new payload.</summary>
    public WeightedValuesPayload(string? hashByPropertyName, IReadOnlyList<WeightedVariant> variants)
    {
        HashByPropertyName = hashByPropertyName;
        Variants = variants;
    }
}

/// <summary>
/// A single weighted entry: <see cref="Weight"/> is the bucket size (proportional to other
/// variants in the same payload), <see cref="Value"/> is the value to return when that bucket is
/// selected — the resolver re-runs the selected value through itself so a variant can itself be a
/// PROVIDED env-var-sourced value, etc.
/// </summary>
public sealed class WeightedVariant
{
    /// <summary>Bucket size — non-negative. Total weight across variants is the denominator.</summary>
    public int Weight { get; }

    /// <summary>Value returned when this variant is picked. Recursively resolved.</summary>
    public Value Value { get; }

    /// <summary>Initializes a new variant.</summary>
    public WeightedVariant(int weight, Value value)
    {
        Weight = weight;
        Value = value;
    }
}
