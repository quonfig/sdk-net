using System;

namespace Quonfig.Sdk.Eval;

/// <summary>
/// Universal value wrapper used by the evaluator and the <see cref="Resolver"/>. Mirrors sdk-java's
/// <c>com.quonfig.sdk.eval.Value</c>: a discriminated union over <see cref="ValueType"/> plus two
/// orthogonal flags — <see cref="Confidential"/> (the value is a secret and should be redacted in
/// telemetry) and <see cref="DecryptWith"/> (the payload is AES-GCM ciphertext that needs the
/// referenced key config to decrypt).
///
/// <para>The runtime CLR type of <see cref="Payload"/> depends on <see cref="Type"/> — see the
/// per-enum-value docs on <see cref="ValueType"/>.</para>
/// </summary>
public sealed class Value : IEquatable<Value>
{
    /// <summary>Wire-level type discriminator.</summary>
    public ValueType Type { get; }

    /// <summary>The raw payload. CLR type depends on <see cref="Type"/>.</summary>
    public object? Payload { get; }

    /// <summary>Whether the value is a secret — used by telemetry to decide whether to redact.</summary>
    public bool Confidential { get; }

    /// <summary>
    /// Name of the config that holds the AES-GCM key. Non-null/empty means the <see cref="Payload"/>
    /// is ciphertext and the resolver must look up this config to get the decryption key.
    /// </summary>
    public string? DecryptWith { get; }

    /// <summary>Initializes a plain (non-confidential, non-encrypted) value.</summary>
    public Value(ValueType type, object? payload) : this(type, payload, false, null) { }

    /// <summary>Initializes a value with explicit confidentiality / decryption-pointer flags.</summary>
    public Value(ValueType type, object? payload, bool confidential, string? decryptWith)
    {
        Type = type;
        Payload = payload;
        Confidential = confidential;
        DecryptWith = decryptWith;
    }

    /// <inheritdoc/>
    public bool Equals(Value? other) =>
        other is not null
        && Type == other.Type
        && Confidential == other.Confidential
        && string.Equals(DecryptWith, other.DecryptWith, StringComparison.Ordinal)
        && Equals(Payload, other.Payload);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Value v && Equals(v);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        unchecked
        {
            int h = (int)Type;
            h = (h * 31) ^ (Payload?.GetHashCode() ?? 0);
            h = (h * 31) ^ Confidential.GetHashCode();
#if NETSTANDARD2_0
            h = (h * 31) ^ (DecryptWith?.GetHashCode() ?? 0);
#else
            h = (h * 31) ^ (DecryptWith?.GetHashCode(StringComparison.Ordinal) ?? 0);
#endif
            return h;
        }
    }
}

/// <summary>
/// Wire payload for <see cref="ValueType.Provided"/>. The <see cref="Source"/> field is currently
/// always <c>"ENV_VAR"</c> across SDKs; <see cref="Lookup"/> is the env-var name.
/// </summary>
public sealed record ProvidedValue(string Source, string Lookup);
