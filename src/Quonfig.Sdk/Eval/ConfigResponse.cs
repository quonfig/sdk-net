using System.Text.Json;

namespace Quonfig.Sdk.Eval;

/// <summary>
/// One per-config entry parsed out of a <see cref="Wire.ConfigEnvelope"/>. Keyed on the
/// envelope's <c>"key"</c> field so the evaluator can look up rows by name. The raw
/// <see cref="JsonElement"/> is retained verbatim — downstream consumers (the future evaluator)
/// project the fields they need; the wire spec evolves on the per-config schema, not this wrapper.
/// </summary>
public sealed class ConfigResponse
{
    /// <summary>Config name (the <c>"key"</c> field on the wire payload).</summary>
    public string Key { get; }

    /// <summary>Raw per-config JSON node — the same shape the datadir loader reads from disk.</summary>
    public JsonElement Raw { get; }

    /// <summary>Initializes a new instance from a key and the raw wire payload.</summary>
    public ConfigResponse(string key, JsonElement raw)
    {
        Key = key;
        Raw = raw;
    }
}
