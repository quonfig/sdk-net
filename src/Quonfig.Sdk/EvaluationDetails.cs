using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Quonfig.Sdk;

/// <summary>
/// The full outcome of a single typed evaluation: the value the caller will use, the reason it
/// was chosen, and metadata that lets a downstream consumer (e.g. a future <c>openfeature-net</c>
/// provider) build a complete <c>ResolutionDetails</c> of its own.
///
/// <para>Field semantics:</para>
/// <list type="bullet">
/// <item><description><see cref="Value"/> — the typed value returned to the caller. On
/// <see cref="Reason.Error"/> this is the caller's default; on <see cref="Reason.Default"/>
/// this is also the caller's default; on Static / TargetingMatch / Split this is the resolved
/// config value.</description></item>
/// <item><description><see cref="Reason"/> — never <see cref="Sdk.Reason.Unknown"/> unless the
/// engine genuinely could not classify the outcome.</description></item>
/// <item><description><see cref="Variant"/> — never null. Synthetic OpenFeature-style identifier
/// derived from <see cref="Reason"/> and the matched indexes
/// (see <c>project/plans/openfeature-resolution-details.md</c> §2):
/// <c>"static"</c>, <c>"targeting:&lt;ruleIndex&gt;"</c>, <c>"split:&lt;weightedValueIndex&gt;"</c>,
/// or <c>"default"</c>.</description></item>
/// <item><description><see cref="VariantIndex"/> — populated only when <see cref="Reason"/> is
/// <see cref="Sdk.Reason.Split"/>; null otherwise. Same integer that appears in
/// <see cref="Variant"/> for Split.</description></item>
/// <item><description><see cref="ErrorCode"/> — populated only when <see cref="Reason"/> is
/// <see cref="Sdk.Reason.Error"/>; null otherwise.</description></item>
/// <item><description><see cref="ErrorMessage"/> — companion to <see cref="ErrorCode"/>; may be
/// null. Per the spec, successful evaluations never set this to a non-null value.</description></item>
/// <item><description><see cref="Metadata"/> — never null; immutable. Standard keys (camelCase,
/// matching sdk-java): <c>configId</c>, <c>configKey</c>, <c>configType</c>, <c>ruleIndex</c>
/// (only on <see cref="Sdk.Reason.TargetingMatch"/> or <see cref="Sdk.Reason.Split"/>),
/// <c>weightedValueIndex</c> (only on Split), <c>environment</c> (omitted when not known).</description></item>
/// </list>
/// </summary>
/// <typeparam name="T">The typed value's CLR type (string, bool, long, double, IReadOnlyList&lt;string&gt;, …).</typeparam>
public sealed class EvaluationDetails<T>
{
    /// <summary>
    /// Constructs a fully populated EvaluationDetails. <paramref name="metadata"/> may be null;
    /// it is copied into an immutable view to prevent caller mutation.
    /// </summary>
    public EvaluationDetails(
        T value,
        Reason reason,
        string variant,
        int? variantIndex,
        ErrorCode? errorCode,
        string? errorMessage,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        Value = value;
        Reason = reason;
        Variant = variant ?? throw new System.ArgumentNullException(nameof(variant));
        VariantIndex = variantIndex;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        if (metadata is null || metadata.Count == 0)
        {
            Metadata = EmptyMetadata;
        }
        else
        {
            var copy = new Dictionary<string, object?>(metadata.Count);
            foreach (var kv in metadata) copy[kv.Key] = kv.Value;
            Metadata = new ReadOnlyDictionary<string, object?>(copy);
        }
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyMetadata =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    /// <summary>The typed value returned to the caller.</summary>
    public T Value { get; }

    /// <summary>Reason for the outcome. Never <see cref="Sdk.Reason.Unknown"/> in normal paths.</summary>
    public Reason Reason { get; }

    /// <summary>Synthetic OpenFeature-style variant identifier. Never null.</summary>
    public string Variant { get; }

    /// <summary>Weighted-bucket index when <see cref="Reason"/> is <see cref="Sdk.Reason.Split"/>.</summary>
    public int? VariantIndex { get; }

    /// <summary>Error code when <see cref="Reason"/> is <see cref="Sdk.Reason.Error"/>.</summary>
    public ErrorCode? ErrorCode { get; }

    /// <summary>Optional human-readable error description; companion to <see cref="ErrorCode"/>.</summary>
    public string? ErrorMessage { get; }

    /// <summary>Immutable metadata bag. Never null; empty when no metadata is available.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"EvaluationDetails{{value={Value}, reason={Reason}, variant={Variant}, variantIndex={VariantIndex}, errorCode={ErrorCode}, errorMessage={ErrorMessage}, metadata=[{Metadata.Count} entries]}}";
}
