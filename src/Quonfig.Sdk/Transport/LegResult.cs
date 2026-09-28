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

    private LegResult(int legIndex, ConfigEnvelope? envelope, bool notModified, Exception? error, string? etag = null)
    {
        LegIndex = legIndex;
        Envelope = envelope;
        NotModified = notModified;
        Error = error;
        ETag = etag;
    }

    internal static LegResult Ok(int leg, ConfigEnvelope env, string? etag = null) => new(leg, env, false, null, etag);
    internal static LegResult Unchanged(int leg) => new(leg, null, true, null);
    internal static LegResult Fail(int leg, Exception err) => new(leg, null, false, err);

    /// <inheritdoc/>
    public bool Equals(LegResult other) =>
        LegIndex == other.LegIndex
        && ReferenceEquals(Envelope, other.Envelope)
        && NotModified == other.NotModified
        && ReferenceEquals(Error, other.Error)
        && string.Equals(ETag, other.ETag, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LegResult other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        (LegIndex, Envelope, NotModified, Error, ETag).GetHashCode();

    public static bool operator ==(LegResult left, LegResult right) => left.Equals(right);

    public static bool operator !=(LegResult left, LegResult right) => !left.Equals(right);
}
