namespace Quonfig.Sdk.Eval;

/// <summary>
/// Cross-SDK config-row category. Matches the <c>type</c> field on the wire and sdk-java's
/// <c>ConfigType</c> enum.
/// </summary>
public enum ConfigType
{
    /// <summary>Plain config value (loggers, JSON, etc.).</summary>
    Config,
    /// <summary>Boolean feature flag.</summary>
    FeatureFlag,
    /// <summary>Reusable named segment referenced by <c>IN_SEG</c>/<c>NOT_IN_SEG</c>.</summary>
    Segment,
    /// <summary>Per-logger level row — excluded from per-evaluation telemetry summaries.</summary>
    LogLevel,
    /// <summary>JSON-schema validation row. Currently treated like <see cref="Config"/> at eval time.</summary>
    Schema,
    /// <summary>Anything we don't recognize — the evaluator treats unknowns the same as Config.</summary>
    Unknown,
}
