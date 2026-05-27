namespace Quonfig.Sdk;

/// <summary>
/// Granularity of context data uploaded to api-telemetry. Mirrors sdk-java
/// <c>ContextUploadMode</c> and sdk-node <c>contextUploadMode</c>. The telemetry pipeline
/// arrives in a follow-on bead (qfg-zp7i.12); this enum lives here so the public
/// <see cref="QuonfigOptions"/> surface is complete at v0.0.1.
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
