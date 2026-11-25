using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Quonfig.Sdk.Crypto;
using Quonfig.Sdk.Exceptions;

namespace Quonfig.Sdk.Eval;

/// <summary>
/// Post-evaluation value resolution: walks a candidate <see cref="Value"/> through ENV_VAR
/// provisioning → weighted-variant bucketing → type coercion, and decrypts qf1 secrets (the
/// order matches the other SDKs so the .NET SDK produces the same final value for the same stored
/// payload).
///
/// <para>Plain (non-PROVIDED, non-WEIGHTED, non-secret) values pass through unchanged. A value of
/// a <see cref="ConfigType.Secret"/> row is a qf1 string (spec
/// <c>project/plans/secrets-qf1-spec.md</c>) decrypted on every get with private keys read from the
/// <c>QUONFIG_KEYSET*</c> env var it names, through <see cref="EnvLookup"/>. The old
/// <c>decryptWith</c> format is no longer read: such a value fails with
/// <see cref="QuonfigDecryptionException"/>.</para>
/// </summary>
public sealed class Resolver
{
    /// <summary>Callback signature for resolving an AES-GCM decryption key config to its value.</summary>
    [Obsolete("decryptWith values are no longer decrypted (qf1 secrets replace them), so a KeyResolver is never called. It will be removed in a future major version.")]
    public delegate Value? KeyResolver(string configKey, ContextSet contexts);

    /// <summary>Callback signature for env-var lookup. Returning null means the var is unset.</summary>
    public delegate string? EnvLookup(string name);

    private readonly EnvLookup _envLookup;
    private readonly Qf1Reader _qf1;

    /// <summary>
    /// Initializes a new resolver. The default <paramref name="envLookup"/> reads
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>; it serves ENV_VAR provided values
    /// and the <c>QUONFIG_KEYSET*</c> keysets of qf1 secrets. <paramref name="keyResolver"/> is
    /// ignored: <c>decryptWith</c> values are no longer decrypted.
    /// </summary>
#pragma warning disable CS0618 // the public signature keeps the obsolete KeyResolver parameter for compatibility
    public Resolver(EnvLookup? envLookup = null, KeyResolver? keyResolver = null)
#pragma warning restore CS0618
    {
        _ = keyResolver;
        _envLookup = envLookup ?? (name => Environment.GetEnvironmentVariable(name));
        var lookup = _envLookup;
        _qf1 = new Qf1Reader(name => lookup(name));
    }

    /// <summary>Prefix on the redacted telemetry marker for confidential / encrypted values.</summary>
    internal const string ReportableValuePrefix = "*****";

    /// <summary>
    /// Returns the redacted telemetry marker for a resolved <see cref="Value"/> that must NOT have its
    /// plaintext reported — a confidential value or one that was AES-GCM decrypted. The marker is
    /// <c>*****</c> followed by the first five lowercase hex chars of the MD5 of the value as stored (the ciphertext for a decrypted value; 10
    /// chars total), matching the cross-SDK contract (sdk-java <c>reportableValueFor</c>, sdk-node
    /// <c>makeConfidential</c>, sdk-ruby <c>reportable_wrapped_value</c>). Returns <c>null</c> for a
    /// plain value, so telemetry reports the real value; the MD5 here is a non-cryptographic
    /// fingerprint used only to distinguish distinct secrets in a summary, never for security.
    /// </summary>
    internal static string? ReportableValueFor(Value? val)
    {
        if (val is null)
        {
            return null;
        }
        if (val.StoredReportableValue is not null)
        {
            return val.StoredReportableValue;
        }
        if (!val.Confidential && string.IsNullOrEmpty(val.DecryptWith))
        {
            return null;
        }
        return RedactedMarker(StringOf(val.Payload));
    }

    /// <summary>The <c>*****</c> + 5 MD5 hex marker over <paramref name="raw"/>, unconditionally.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "MD5 is a non-security fingerprint used only to redact secrets in telemetry — the cross-SDK reportable-value marker; it never protects data.")]
    internal static string? RedactedMarker(string raw)
    {
        byte[] rawBytes = System.Text.Encoding.UTF8.GetBytes(raw);
#if NET8_0_OR_GREATER
        byte[] sum = System.Security.Cryptography.MD5.HashData(rawBytes);
#else
        byte[] sum;
        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            sum = md5.ComputeHash(rawBytes);
        }
#endif
        var hex = new System.Text.StringBuilder(sum.Length * 2);
        foreach (var b in sum)
        {
            hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        if (hex.Length < 5)
        {
            return null;
        }
        return ReportableValuePrefix + hex.ToString(0, 5);
    }

    private static string StringOf(object? value) => value switch
    {
        null => "",
        string s => s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>
    /// Resolves <paramref name="candidate"/> to its final form. The arguments mirror sdk-java's
    /// <c>resolve(Value, ConfigRow, String envId, ContextSet)</c>, with the config row exploded
    /// into <paramref name="configKey"/> and <paramref name="configValueType"/> since the .NET
    /// SDK doesn't ship a <c>ConfigRow</c> type yet.
    /// </summary>
    /// <exception cref="QuonfigEnvVarNotSetException">ENV_VAR provided value with no matching env var (and no fallback).</exception>
    /// <exception cref="QuonfigCoercionException">Env-var string can't be coerced to <paramref name="configValueType"/>.</exception>
    /// <exception cref="QuonfigDecryptionException">The value still carries <c>decryptWith</c>, which is no longer read.</exception>
    public Value Resolve(Value candidate, string configKey, ValueType configValueType, ContextSet contexts) =>
        Resolve(candidate, configKey, configValueType, contexts, out _);

    /// <summary>
    /// Same as <see cref="Resolve(Value, string, ValueType, ContextSet)"/> but also reports the
    /// 0-based weighted-bucket index chosen when <paramref name="candidate"/> is a weighted-values
    /// value (-1 otherwise). The caller uses this to promote the evaluation reason to
    /// <see cref="Reason.Split"/>.
    /// </summary>
    public Value Resolve(
        Value candidate, string configKey, ValueType configValueType, ContextSet contexts,
        out int weightedValueIndex)
    {
        return Resolve(candidate, configKey, configValueType, contexts, out weightedValueIndex, out _, false);
    }

    /// <summary>
    /// Same as <see cref="Resolve(Value, string, ValueType, ContextSet, out int)"/> but also reports
    /// <paramref name="missingHashPropertyName"/>: the weighted rollout's
    /// <c>hashByPropertyName</c> when that property is missing from <paramref name="contexts"/>
    /// (or its value is null; the rollout then hashes an empty value), or null otherwise (qfg-9dxb.8).
    /// </summary>
    public Value Resolve(
        Value candidate, string configKey, ValueType configValueType, ContextSet contexts,
        out int weightedValueIndex, out string? missingHashPropertyName)
    {
        return Resolve(candidate, configKey, configValueType, contexts, out weightedValueIndex, out missingHashPropertyName, false);
    }

    /// <summary>
    /// Same as the public overload, for a value of a row of type <paramref name="rowType"/>: a
    /// <see cref="ConfigType.Secret"/> row's value is decrypted as qf1. Used by <see cref="Evaluator"/>.
    /// </summary>
    internal Value Resolve(
        Value candidate, string configKey, ValueType configValueType, ContextSet contexts,
        ConfigType rowType, out int weightedValueIndex, out string? missingHashPropertyName)
    {
        return Resolve(candidate, configKey, configValueType, contexts, out weightedValueIndex, out missingHashPropertyName, rowType == ConfigType.Secret);
    }

    private Value Resolve(
        Value candidate, string configKey, ValueType configValueType, ContextSet contexts,
        out int weightedValueIndex, out string? missingHashPropertyName, bool secret)
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

        weightedValueIndex = -1;
        missingHashPropertyName = null;

        // Spec 13.3 R1: this SDK has no decryptWith code. A value that still carries it fails
        // instead of being returned as if its ciphertext were the value.
        if (!string.IsNullOrEmpty(candidate.DecryptWith))
        {
            throw new QuonfigDecryptionException(
                $"config \"{configKey}\": value carries decryptWith, an old secret format this SDK no longer reads; store it in secrets/ as qf1");
        }

        if (secret)
        {
            return ResolveSecret(candidate, configKey);
        }

        if (candidate.Type == ValueType.Provided)
        {
            return ResolveProvided(candidate, configKey, configValueType);
        }

        if (candidate.Type == ValueType.WeightedValues)
        {
            return ResolveWeighted(candidate, configKey, configValueType, contexts, out weightedValueIndex, out missingHashPropertyName);
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

        object coerced = Coerce(raw, configValueType, configKey, pv.Lookup);
        // Keep the ENV_VAR's ISO text for the untyped EvaluateDetails, as sdk-go does (qfg-177f).
        return configValueType == ValueType.Duration && coerced is TimeSpan ts
            ? Value.ParsedDuration(ts, raw, candidate.Confidential, candidate.DecryptWith)
            : new Value(configValueType, coerced, candidate.Confidential, candidate.DecryptWith);
    }

    // ----- Weighted -----

    private Value ResolveWeighted(
        Value candidate, string configKey, ValueType configValueType, ContextSet contexts,
        out int weightedValueIndex, out string? missingHashPropertyName)
    {
        weightedValueIndex = -1;
        missingHashPropertyName = null;
        if (candidate.Payload is not WeightedValuesPayload wv) return candidate;
        if (wv.Variants.Count == 0) return candidate;

        double fraction = UserFraction(configKey, wv.HashByPropertyName, contexts, out bool hashPropertyMissing);

        long total = 0;
        foreach (var v in wv.Variants) total += v.Weight;
        if (total <= 0) return candidate;
        if (hashPropertyMissing) missingHashPropertyName = wv.HashByPropertyName;

        double threshold = fraction * total;

        long running = 0;
        int pickedIndex = -1;
        for (int i = 0; i < wv.Variants.Count; i++)
        {
            running += wv.Variants[i].Weight;
            if (running >= threshold)
            {
                pickedIndex = i;
                break;
            }
        }
        if (pickedIndex < 0) pickedIndex = 0;
        weightedValueIndex = pickedIndex;

        // Recurse: a weighted variant's value can itself be PROVIDED/confidential/etc. The bucket
        // index is the one we just picked — inner resolution doesn't change it.
        return Resolve(wv.Variants[pickedIndex].Value, configKey, configValueType, contexts, out _, out _, false);
    }

    // Random source for rollouts with no hashByPropertyName (qfg-t9wo). System.Random is not
    // thread-safe and Random.Shared is net6+, so one instance is shared behind a lock (the same
    // shape as sdk-go's mutex-guarded rand in evalcore/weighted.go).
    private static readonly Random SharedRandom = new();
    private static readonly object SharedRandomLock = new();

    private static double RandomFraction()
    {
        lock (SharedRandomLock) { return SharedRandom.NextDouble(); }
    }

    // No hashByPropertyName: a random fraction on every evaluation (qfg-t9wo).
    // hashByPropertyName set but its value missing (context, property, or null value) or "":
    // hash configKey + "", so every such caller lands in the same bucket (qfg-9dxb.8).
    // hashPropertyMissing is true only for the missing/null shapes, not for a present "".
    private static double UserFraction(
        string configKey, string? hashByPropertyName, ContextSet contexts, out bool hashPropertyMissing)
    {
        hashPropertyMissing = false;
        if (string.IsNullOrEmpty(hashByPropertyName)) return RandomFraction();
        var lookup = contexts.GetContextValue(hashByPropertyName);
        if (!lookup.Exists || lookup.Value is null or ContextValueString { Value: null })
        {
            hashPropertyMissing = true;
            return Murmur3.HashZeroToOne(configKey);
        }
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

    // ----- qf1 secrets -----

    // Spec 8 and 9.3: a failure fails only this get, with QuonfigDecryptionException. The message
    // is the qf1 class and detail (keyset and fingerprints only) plus the config key; no inner
    // exception, so no library text, key material, payload or plaintext can ride along.
    private Value ResolveSecret(Value candidate, string configKey)
    {
        if (candidate.Type != ValueType.String || candidate.Payload is not string stored)
        {
            throw new QuonfigDecryptionException(
                $"config \"{configKey}\": qf1 {Qf1Error.Malformed}: a secret value must be a qf1 string");
        }

        string? plaintext = _qf1.Decrypt(stored, out var error);
        if (plaintext is null)
        {
            throw new QuonfigDecryptionException($"config \"{configKey}\": {error!.Message}");
        }

        // The plaintext stays confidential, and telemetry reports the marker over the STORED
        // ciphertext, never the plaintext (spec 13.5), even if the stored value lacks
        // confidential: true.
        return Value.Decrypted(plaintext, candidate);
    }

    // ----- Coercion -----

    private static object Coerce(string raw, ValueType target, string configKey, string envVar)
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
                ValueType.Duration => IsoDuration.Parse(raw),
                ValueType.String => raw,
                ValueType.LogLevel => raw,
                _ => raw,
            };
            return result;
        }
        catch (Exception e) when (e is FormatException or OverflowException or JsonException or ArgumentException)
        {
            // Name the variable, the type and the config, never the value: it may be a secret, and
            // e.Message quotes it too on .NET 8. Same wording and wire type name as sdk-go
            // (qfg-goi1.2.4); sdk-go's sanitized parser reason has no safe .NET equivalent, so the
            // parser exception is kept as the inner exception instead (qfg-goi1.2.15).
            throw new QuonfigCoercionException(
                $"environment variable \"{envVar}\" cannot be converted to {WireName(target)} for config \"{configKey}\"", e);
        }
    }

    // The config-file name of a value type ("int", "string_list", ...), as sdk-go prints it.
    private static string WireName(ValueType t) => t switch
    {
        ValueType.Bool => "bool",
        ValueType.Int => "int",
        ValueType.Double => "double",
        ValueType.String => "string",
        ValueType.StringList => "string_list",
        ValueType.LogLevel => "log_level",
        ValueType.Duration => "duration",
        ValueType.Json => "json",
        ValueType.WeightedValues => "weighted_values",
        ValueType.Provided => "provided",
        _ => t.ToString(),
    };

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
