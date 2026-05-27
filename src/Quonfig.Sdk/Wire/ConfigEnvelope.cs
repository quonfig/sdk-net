using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quonfig.Sdk.Wire;

/// <summary>
/// Wire-format wrapper for an HTTP config-download response from <c>api-delivery</c>. Mirrors
/// sdk-go <c>ConfigEnvelope</c> and sdk-java <c>com.quonfig.sdk.wire.ConfigEnvelope</c>: a list
/// of per-config wire payloads plus response metadata (version, environment, workspace id).
///
/// <para>Each entry in <see cref="Configs"/> is the same JSON shape that the datadir loader
/// reads from a workspace file, so consumers can route each <see cref="JsonElement"/> through
/// the existing parser. Held as a <see cref="JsonElement"/> rather than an exploded POCO so the
/// envelope is forward-compatible with any field additions to the per-config payload — the wire
/// spec evolves on the per-config schema, not on this wrapper.</para>
/// </summary>
public sealed class ConfigEnvelope
{
    /// <summary>Per-config wire payloads, in the order returned by the server.</summary>
    [JsonPropertyName("configs")]
    public IReadOnlyList<JsonElement> Configs { get; }

    /// <summary>Response metadata (envelope version, environment, workspace id). May be null.</summary>
    [JsonPropertyName("meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Meta? Meta { get; }

    /// <summary>
    /// Initializes a new envelope. A null <paramref name="configs"/> is normalized to an empty
    /// list so callers can iterate without a null check. Defensive copy taken so the envelope is
    /// effectively immutable after construction.
    /// </summary>
    [JsonConstructor]
    public ConfigEnvelope(IReadOnlyList<JsonElement>? configs, Meta? meta)
    {
        if (configs is null)
        {
            Configs = System.Array.Empty<JsonElement>();
        }
        else
        {
            var copy = new JsonElement[configs.Count];
            for (int i = 0; i < configs.Count; i++) copy[i] = configs[i];
            Configs = copy;
        }
        Meta = meta;
    }
}
