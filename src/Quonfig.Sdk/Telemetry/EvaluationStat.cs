using System;

namespace Quonfig.Sdk.Telemetry;

/// <summary>
/// One evaluation observation pushed into the telemetry pipeline.
///
/// <para><see cref="ReportableValue"/> is non-null when the underlying value is confidential or
/// AES-GCM encrypted; in that case the wire payload sends the redacted form instead of the
/// plaintext. Mirrors sdk-java's <c>EvaluationStat</c>.</para>
/// </summary>
public sealed class EvaluationStat
{
    /// <summary>Initializes a new evaluation observation.</summary>
    public EvaluationStat(
        string configId,
        string configKey,
        string configType,
        int ruleIndex,
        int weightedValueIndex,
        object? selectedValue,
        string? reportableValue,
        int reason)
    {
        ConfigId = configId ?? throw new ArgumentNullException(nameof(configId));
        ConfigKey = configKey ?? throw new ArgumentNullException(nameof(configKey));
        ConfigType = configType ?? throw new ArgumentNullException(nameof(configType));
        RuleIndex = ruleIndex;
        WeightedValueIndex = weightedValueIndex;
        SelectedValue = selectedValue;
        ReportableValue = reportableValue;
        Reason = reason;
    }

    /// <summary>Convenience overload accepting the typed <see cref="Sdk.Reason"/> enum.</summary>
    public EvaluationStat(
        string configId,
        string configKey,
        string configType,
        int ruleIndex,
        int weightedValueIndex,
        object? selectedValue,
        string? reportableValue,
        Reason reason)
        : this(configId, configKey, configType, ruleIndex, weightedValueIndex, selectedValue, reportableValue, (int)reason)
    {
    }

    /// <summary>Wire config id (UUID string).</summary>
    public string ConfigId { get; }

    /// <summary>Human-readable config key (e.g. <c>"feature.foo"</c>).</summary>
    public string ConfigKey { get; }

    /// <summary>Config type code (<c>CONFIG</c>, <c>FEATURE_FLAG</c>, <c>LOG_LEVEL</c>, …).</summary>
    public string ConfigType { get; }

    /// <summary>Conditional value index that matched. -1 when the match was the static fallback row.</summary>
    public int RuleIndex { get; }

    /// <summary>Weighted-bucket index when the matched row was a <c>WeightedValues</c>; -1 otherwise.</summary>
    public int WeightedValueIndex { get; }

    /// <summary>Resolved value (unboxed CLR primitive: bool, int, long, double, string, IReadOnlyList&lt;string&gt;).</summary>
    public object? SelectedValue { get; }

    /// <summary>Non-null when the value is redacted (confidential / encrypted); sent in place of <see cref="SelectedValue"/>.</summary>
    public string? ReportableValue { get; }

    /// <summary>Wire-int code for the evaluation reason (matches <see cref="Sdk.Reason"/> ordinals).</summary>
    public int Reason { get; }
}
