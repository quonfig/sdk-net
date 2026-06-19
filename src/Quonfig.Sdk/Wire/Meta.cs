using System.Text.Json.Serialization;

namespace Quonfig.Sdk.Wire;

/// <summary>
/// Response metadata that accompanies a <see cref="ConfigEnvelope"/>. Maps 1:1 to sdk-go
/// <c>Meta</c> and sdk-java <c>com.quonfig.sdk.wire.Meta</c> so an envelope serialized by any
/// SDK is consumable by the others.
/// </summary>
public sealed class Meta
{
    /// <summary>Wire-format version tag — opaque string, e.g. <c>"v1"</c>.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; }

    /// <summary>Environment slug evaluated against, e.g. <c>"production"</c>.</summary>
    [JsonPropertyName("environment")]
    public string? Environment { get; }

    /// <summary>Workspace identifier, when known. Omitted from the wire payload when null.</summary>
    [JsonPropertyName("workspaceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkspaceId { get; }

    /// <summary>
    /// Monotonic, per-branch commit counter (<c>git rev-list --count HEAD</c>) served by
    /// api-delivery alongside <see cref="Version"/>. Unlike the SHA in <see cref="Version"/> — which
    /// is unordered — a higher <see cref="Generation"/> is strictly newer, so the SDK can order two
    /// snapshots and reject an older one (the canonical reject-older install guard). Purely additive:
    /// servers that predate the watermark omit it and it decodes to <c>0</c>. Maps 1:1 to sdk-go's
    /// <c>Meta.Generation</c> and sdk-java's <c>Meta.generation</c>.
    /// </summary>
    [JsonPropertyName("generation")]
    public int Generation { get; }

    /// <summary>Initializes a new instance from the four fields. All are optional on the wire.</summary>
    [JsonConstructor]
    public Meta(string? version, string? environment, string? workspaceId, int generation = 0)
    {
        Version = version;
        Environment = environment;
        WorkspaceId = workspaceId;
        Generation = generation;
    }
}
