namespace Quonfig.Sdk.Eval;

/// <summary>
/// Outcome of running <see cref="Evaluator.Evaluate"/> on a <see cref="ConfigResponse"/>. Maps
/// onto the <see cref="EvaluationDetails{T}"/> a higher-level caller will hand back to user code,
/// but stays raw (the resolved <see cref="Value"/>, the row index, the config id) so the
/// downstream typed-getter layer can build whichever <see cref="Sdk.Reason"/>/variant identifier
/// it needs.
/// </summary>
public sealed class EvaluationMatch
{
    /// <summary>Whether any rule (env-specific or default) matched.</summary>
    public bool IsMatch { get; }

    /// <summary>The resolved value — already passed through <see cref="Resolver"/>. Null when no rule matched.</summary>
    public Value? Value { get; }

    /// <summary>Index of the matched rule within its rule set, or -1 when nothing matched.</summary>
    public int RuleIndex { get; }

    /// <summary>
    /// 0-based index of the resolved entry within a weighted-values bucket, or -1 when the matched
    /// value was not a weighted resolution. A value &gt;= 0 is what promotes the reason to
    /// <see cref="Sdk.Reason.Split"/>.
    /// </summary>
    public int WeightedValueIndex { get; }

    /// <summary>Why this match was produced. <see cref="Sdk.Reason.Default"/> when no rule matched.</summary>
    public Reason Reason { get; }

    /// <summary>Config id (the <c>id</c> field on the wire). Empty when unknown.</summary>
    public string ConfigId { get; }

    /// <summary>Config name (the <c>key</c> field on the wire).</summary>
    public string ConfigKey { get; }

    /// <summary>Wire-level value type of the config row this match came from.</summary>
    public ValueType ValueType { get; }

    private EvaluationMatch(
        bool isMatch,
        Value? value,
        int ruleIndex,
        int weightedValueIndex,
        Reason reason,
        string configId,
        string configKey,
        ValueType valueType)
    {
        IsMatch = isMatch;
        Value = value;
        RuleIndex = ruleIndex;
        WeightedValueIndex = weightedValueIndex;
        Reason = reason;
        ConfigId = configId;
        ConfigKey = configKey;
        ValueType = valueType;
    }

    /// <summary>Builds a successful match. <paramref name="weightedValueIndex"/> is -1 unless the
    /// value came from a resolved weighted-values bucket.</summary>
    public static EvaluationMatch Matched(
        Value value, int ruleIndex, int weightedValueIndex, Reason reason,
        string configId, string configKey, ValueType valueType) =>
        new(true, value, ruleIndex, weightedValueIndex, reason, configId, configKey, valueType);

    /// <summary>Builds a "no rule matched" match; the caller falls back to its own default.</summary>
    public static EvaluationMatch NoMatch(string configId, string configKey, ValueType valueType) =>
        new(false, null, -1, -1, Reason.Default, configId, configKey, valueType);
}
