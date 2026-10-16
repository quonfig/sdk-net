using System;
using Quonfig.Sdk.Wire;

namespace Quonfig.Sdk.Transport;

/// <summary>
/// One hedged leg's outcome from <see cref="HttpTransport.FetchHedgedAsync"/>. Exactly one
/// <see cref="LegResult"/> is emitted per FIRED leg; <see cref="LegIndex"/> identifies the leg so
/// the caller can set <c>resolvedFrom</c> atomically with the install (no shared
/// <c>LastResolvedIndex</c> race between concurrent legs). Mirrors sdk-go's <c>legResult</c>.
/// </summary>
internal readonly struct LegResult : IEquatable<LegResult>
{
    /// <summary>Zero-based <see cref="HttpTransport.ApiUrls"/> index of the leg this result came from.</summary>
    public int LegIndex { get; }

    /// <summary>The installed envelope, or <c>null</c> when <see cref="NotModified"/> or <see cref="Error"/> is set.</summary>
    public ConfigEnvelope? Envelope { get; }

    /// <summary>True when the leg returned HTTP 304 (no change) — nothing to install, not an error.</summary>
    public bool NotModified { get; }

    /// <summary>The leg's failure, or <c>null</c> on success / 304.</summary>
    public Exception? Error { get; }

    /// <summary>The ETag this leg stored from its 200 response, or <c>null</c> (qfg-9dxb.9 follow-up).</summary>
    public string? ETag { get; }

    /// <summary>
    /// The HTTP status the leg answered with, or <c>0</c> when there was no response (timeout,
    /// transport error, cancellation). Carried so a fetch cycle can classify the PRIMARY leg's
    /// status for the auth-failure rule (qfg-goi1.10.28, R2).
    /// </summary>
    public int Status { get; }

    private LegResult(int legIndex, ConfigEnvelope? envelope, bool notModified, Exception? error, string? etag, int status)
    {
        LegIndex = legIndex;
        Envelope = envelope;
        NotModified = notModified;
        Error = error;
        ETag = etag;
        Status = status;
    }

    internal static LegResult Ok(int leg, ConfigEnvelope env, string? etag = null, int status = 200) => new(leg, env, false, null, etag, status);
    internal static LegResult Unchanged(int leg) => new(leg, null, true, null, null, 304);
    internal static LegResult Fail(int leg, Exception err, int status = 0) => new(leg, null, false, err, null, status);

    /// <inheritdoc/>
    public bool Equals(LegResult other) =>
        LegIndex == other.LegIndex
        && ReferenceEquals(Envelope, other.Envelope)
        && NotModified == other.NotModified
        && ReferenceEquals(Error, other.Error)
        && string.Equals(ETag, other.ETag, StringComparison.Ordinal)
        && Status == other.Status;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LegResult other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        (LegIndex, Envelope, NotModified, Error, ETag, Status).GetHashCode();

    public static bool operator ==(LegResult left, LegResult right) => left.Equals(right);

    public static bool operator !=(LegResult left, LegResult right) => !left.Equals(right);
}
