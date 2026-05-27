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

    /// <summary>Initializes a new instance from the three fields. All are optional on the wire.</summary>
    [JsonConstructor]
    public Meta(string? version, string? environment, string? workspaceId)
    {
        Version = version;
        Environment = environment;
        WorkspaceId = workspaceId;
    }
}
