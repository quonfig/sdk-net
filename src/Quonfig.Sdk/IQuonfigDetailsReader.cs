using System.Collections.Generic;

namespace Quonfig.Sdk;

/// <summary>
/// Untyped details read (qfg-177f), matching sdk-go's <c>Client.EvaluateDetails</c>. Implemented
/// by <see cref="Quonfig"/> and by the bound view <see cref="BoundQuonfig"/> returned from
/// <see cref="IQuonfig.WithContext"/>. It is a separate interface so that adding it does not add
/// members to <see cref="IQuonfig"/> or <see cref="IBoundQuonfig"/>: code that holds either one
/// reaches it with a type check, for example <c>if (client is IQuonfigDetailsReader reader)</c>.
/// </summary>
public interface IQuonfigDetailsReader
{
    /// <summary>
    /// Untyped details read (qfg-177f), matching sdk-go's <c>Client.EvaluateDetails</c>. Returns
    /// the resolved value in its stored form, after ENV_VAR provisioning and decryption, with the
    /// same reason, variant, metadata and telemetry as the typed <c>Get*Details</c> getters. A
    /// duration is its ISO 8601 text exactly as stored (<c>"PT90S"</c>), a string list is an
    /// <see cref="IReadOnlyList{T}"/> of string, an int is a <see cref="long"/>, a double a
    /// <see cref="double"/>, JSON the same tree <see cref="IQuonfig.GetJson"/> returns. There is no type
    /// check, no coercion and no parse validation: a stored value that a typed getter rejects is
    /// returned as stored with a success reason. <see cref="EvaluationDetails{T}.ValueType"/> names
    /// the stored value type.
    /// Never throws: a missing key returns a null value with <see cref="Reason.Error"/> and
    /// <see cref="ErrorCode.FlagNotFound"/>, and an unresolvable value (unset or malformed
    /// ENV_VAR, decryption failure) returns null with the same error code a typed getter reports.
    /// </summary>
    /// <remarks>
    /// On a bound view, <paramref name="contexts"/> is layered on the bound context the same way
    /// <see cref="IBoundQuonfig.WithContext"/> layers it (a supplied named context replaces the
    /// same-named bound one).
    /// </remarks>
    /// <param name="key">The config key.</param>
    /// <param name="contexts">Per-call contexts, merged like the typed getters' contexts.</param>
    EvaluationDetails<object?> EvaluateDetails(string key, ContextSet? contexts = null);
}
