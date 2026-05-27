using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Xml;
using Quonfig.Sdk.Crypto;
using Quonfig.Sdk.Exceptions;

namespace Quonfig.Sdk.Eval;

/// <summary>
/// Post-evaluation value resolution: walks a candidate <see cref="Value"/> through ENV_VAR
/// provisioning → weighted-variant bucketing → AES-GCM decryption → type coercion in that order
/// (the order matches sdk-java / sdk-go so the .NET SDK produces the same final value for the same
/// stored payload).
///
/// <para>Plain (non-PROVIDED, non-WEIGHTED, non-confidential) values pass through unchanged. The
/// resolver is decoupled from the not-yet-implemented evaluator via two pluggable callbacks:
/// <see cref="EnvLookup"/> reads env vars, and <see cref="KeyResolver"/> resolves the
/// <see cref="Value.DecryptWith"/>-pointed key config to its plaintext hex key (the future
/// <c>Evaluator</c> wires this to its own <c>Evaluate</c> entry point).</para>
/// </summary>
public sealed class Resolver
{
    /// <summary>Callback signature for resolving an AES-GCM decryption key config to its value.</summary>
    public delegate Value? KeyResolver(string configKey, ContextSet contexts);

    /// <summary>Callback signature for env-var lookup. Returning null means the var is unset.</summary>
    public delegate string? EnvLookup(string name);

    private readonly EnvLookup _envLookup;
    private readonly KeyResolver _keyResolver;

    /// <summary>
    /// Initializes a new resolver. Both callbacks are optional; the default
    /// <paramref name="envLookup"/> reads <see cref="Environment.GetEnvironmentVariable(string)"/>
    /// and the default <paramref name="keyResolver"/> always returns null (so decryption requests
    /// will fail with <see cref="QuonfigDecryptionException"/> until the evaluator is wired in).
    /// </summary>
    public Resolver(EnvLookup? envLookup = null, KeyResolver? keyResolver = null)
    {
        _envLookup = envLookup ?? (name => Environment.GetEnvironmentVariable(name));
        _keyResolver = keyResolver ?? ((_, _) => null);
    }

    /// <summary>
    /// Resolves <paramref name="candidate"/> to its final form. The arguments mirror sdk-java's
    /// <c>resolve(Value, ConfigRow, String envId, ContextSet)</c>, with the config row exploded
    /// into <paramref name="configKey"/> and <paramref name="configValueType"/> since the .NET
    /// SDK doesn't ship a <c>ConfigRow</c> type yet.
    /// </summary>
    /// <exception cref="QuonfigEnvVarNotSetException">ENV_VAR provided value with no matching env var (and no fallback).</exception>
    /// <exception cref="QuonfigCoercionException">Env-var string can't be coerced to <paramref name="configValueType"/>.</exception>
    /// <exception cref="QuonfigDecryptionException">Decrypt key config is missing/unresolvable, or AES-GCM fails.</exception>
    public Value Resolve(Value candidate, string configKey, ValueType configValueType, ContextSet contexts)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(configKey);
        ArgumentNullException.ThrowIfNull(contexts);
#else
        if (candidate is null) throw new ArgumentNullException(nameof(candidate));
        if (configKey is null) throw new ArgumentNullException(nameof(configKey));
        if (contexts is null) throw new ArgumentNullException(nameof(contexts));
#endif

        if (candidate.Type == ValueType.Provided)
        {
            return ResolveProvided(candidate, configKey, configValueType);
        }

        if (candidate.Type == ValueType.WeightedValues)
        {
            return ResolveWeighted(candidate, configKey, configValueType, contexts);
        }

        if (candidate.Confidential && !string.IsNullOrEmpty(candidate.DecryptWith))
        {
            return ResolveDecryption(candidate, configKey, contexts);
        }

        return candidate;
    }

    // ----- ENV_VAR -----

    private Value ResolveProvided(Value candidate, string configKey, ValueType configValueType)
    {
        if (candidate.Payload is not ProvidedValue pv) return candidate;
        if (!string.Equals(pv.Source, "ENV_VAR", StringComparison.Ordinal)) return candidate;

        string? raw = _envLookup(pv.Lookup);
        if (raw is null)
        {
            throw new QuonfigEnvVarNotSetException(
                $"environment variable \"{pv.Lookup}\" not set for config \"{configKey}\"");
        }

        object coerced = Coerce(raw, configValueType, configKey);
        return new Value(configValueType, coerced, candidate.Confidential, candidate.DecryptWith);
    }

    // ----- Weighted -----

    private Value ResolveWeighted(Value candidate, string configKey, ValueType configValueType, ContextSet contexts)
    {
        if (candidate.Payload is not WeightedValuesPayload wv) return candidate;
        if (wv.Variants.Count == 0) return candidate;

        double fraction = UserFraction(configKey, wv.HashByPropertyName, contexts);

        long total = 0;
        foreach (var v in wv.Variants) total += v.Weight;
        if (total <= 0) return candidate;

        double threshold = fraction * total;

        long running = 0;
        WeightedVariant? picked = null;
        foreach (var v in wv.Variants)
        {
            running += v.Weight;
            if (running >= threshold)
            {
                picked = v;
                break;
            }
        }
        picked ??= wv.Variants[0];

        // Recurse: a weighted variant's value can itself be PROVIDED/confidential/etc.
        return Resolve(picked.Value, configKey, configValueType, contexts);
    }

    private static double UserFraction(string configKey, string? hashByPropertyName, ContextSet contexts)
    {
        if (string.IsNullOrEmpty(hashByPropertyName)) return 0.0;
        var lookup = contexts.GetContextValue(hashByPropertyName);
        if (!lookup.Exists) return 0.0;
        string valueRendered = RenderContextValue(lookup.Value);
        return Murmur3.HashZeroToOne(configKey + valueRendered);
    }

    private static string RenderContextValue(ContextValue? cv)
    {
        if (cv is null) return "";
        // Match sdk-go's `fmt.Sprintf("%s%v", configKey, value)` which renders the underlying CLR value.
        return cv switch
        {
            ContextValueString s => s.Value,
            ContextValueInt i => i.Value.ToString(CultureInfo.InvariantCulture),
            ContextValueLong l => l.Value.ToString(CultureInfo.InvariantCulture),
            ContextValueDouble d => d.Value.ToString("R", CultureInfo.InvariantCulture),
            ContextValueBool b => b.Value ? "true" : "false",
            _ => cv.ToObject()?.ToString() ?? "",
        };
    }

    // ----- Decryption -----

    private Value ResolveDecryption(Value candidate, string configKey, ContextSet contexts)
    {
        Value? keyValue = _keyResolver(candidate.DecryptWith!, contexts);
        if (keyValue is null)
        {
            throw new QuonfigDecryptionException(
                $"decryption key config \"{candidate.DecryptWith}\" not found");
        }

        Value resolvedKey;
        try
        {
            // The key config can itself be PROVIDED — recurse so the env-var lookup happens.
            resolvedKey = Resolve(keyValue, candidate.DecryptWith!, ValueType.String, contexts);
        }
        catch (QuonfigException e)
        {
            throw new QuonfigDecryptionException(
                $"failed to resolve decryption key from \"{candidate.DecryptWith}\": {e.Message}", e);
        }

        string? secretKeyHex = resolvedKey.Payload?.ToString();
        if (string.IsNullOrEmpty(secretKeyHex))
        {
            throw new QuonfigDecryptionException(
                $"decryption key from \"{candidate.DecryptWith}\" is empty");
        }

        string ciphertext = candidate.Payload?.ToString() ?? "";
        string plaintext;
        try
        {
            plaintext = AesGcmCompat.Decrypt(secretKeyHex!, ciphertext);
        }
        catch (QuonfigDecryptionException e)
        {
            throw new QuonfigDecryptionException(
                $"decryption failed for config \"{configKey}\": {e.Message}", e);
        }

        // Plaintext remains confidential (for telemetry redaction); decryptWith is cleared since
        // the value is no longer ciphertext.
        return new Value(ValueType.String, plaintext, true, null);
    }

    // ----- Coercion -----

    private static object Coerce(string raw, ValueType target, string configKey)
    {
        try
        {
            object result = target switch
            {
                ValueType.Bool => CoerceBool(raw),
                ValueType.Int => long.Parse(raw, CultureInfo.InvariantCulture),
                ValueType.Double => double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture),
                ValueType.StringList => SplitStringList(raw),
                ValueType.Json => ParseJson(raw) ?? new Dictionary<string, object?>(StringComparer.Ordinal),
                ValueType.Duration => XmlConvert.ToTimeSpan(raw),
                ValueType.String => raw,
                ValueType.LogLevel => raw,
                _ => raw,
            };
            return result;
        }
        catch (Exception e) when (e is FormatException or OverflowException or JsonException or ArgumentException)
        {
            throw new QuonfigCoercionException(
                $"cannot convert \"{raw}\" to {target} for config \"{configKey}\": {e.Message}", e);
        }
    }

    private static bool CoerceBool(string raw)
    {
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || raw == "1") return true;
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) || raw == "0") return false;
        throw new FormatException("not a boolean");
    }

    private static IReadOnlyList<string> SplitStringList(string raw)
    {
        if (raw.Length == 0) return Array.Empty<string>();
        var parts = raw.Split(',');
        var result = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++) result[i] = parts[i].Trim();
        return result;
    }

    private static object? ParseJson(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return ConvertJsonElement(doc.RootElement);
    }

    private static object? ConvertJsonElement(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var prop in el.EnumerateObject())
                {
                    obj[prop.Name] = ConvertJsonElement(prop.Value);
                }
                return obj;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in el.EnumerateArray())
                {
                    list.Add(ConvertJsonElement(item));
                }
                return list;
            case JsonValueKind.String:
                return el.GetString();
            case JsonValueKind.Number:
                if (el.TryGetInt64(out long l)) return l;
                return el.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                return null;
        }
    }
}
