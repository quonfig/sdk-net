namespace Quonfig.Sdk;

/// <summary>
/// Granularity of context data uploaded to api-telemetry. Mirrors sdk-java
/// <c>ContextUploadMode</c> and sdk-node <c>contextUploadMode</c>. Consumed by
/// <see cref="Telemetry.ContextShapeCollector"/> and <see cref="Telemetry.ExampleContextCollector"/>.
/// </summary>
public enum ContextUploadMode
{
    /// <summary>No context data uploaded.</summary>
    None,

    /// <summary>Only the per-context property-name shapes (no values).</summary>
    ShapesOnly,

    /// <summary>Shapes plus periodic anonymized example contexts.</summary>
    PeriodicExample,
}
