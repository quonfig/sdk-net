using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Quonfig.Sdk.Telemetry;

/// <summary>Shipped defaults of the telemetry transport policy (server SDK class, qfg-y8je.10).</summary>
internal static class TelemetryDefaults
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxRetainedAge = TimeSpan.FromMinutes(5);
    public const int MaxRetainedBatches = 5;
    public const int MaxRetainedBytes = 2 * 1024 * 1024;
    public const int MaxEvaluationSummaries = 10_000;
    public const int MaxContextShapeFields = 10_000;
    public const int MaxExampleContexts = 10_000;

    /// <summary>No send sooner than this after a failed POST (P4).</summary>
    public const long ResendFloorMs = 30_000;

    /// <summary>Retry-After is honored up to this (P4).</summary>
    public const long RetryAfterCapMs = 600_000;

    /// <summary>At most one drop WARN per this interval while dropping continues (P7).</summary>
    public const long DropWarnIntervalMs = 600_000;

    /// <summary>close() gives the live window one POST with this deadline (P8).</summary>
    public static readonly TimeSpan ShutdownFlushDeadline = TimeSpan.FromSeconds(5);

    /// <summary>Bound on the example-context rate-limit map (P6).</summary>
    public const int ExampleContextSeenCap = 100_000;

    public static TimeSpan PositiveOr(TimeSpan value, TimeSpan fallback) => value > TimeSpan.Zero ? value : fallback;

    public static int PositiveOr(int value, int fallback) => value > 0 ? value : fallback;
}

/// <summary>Resolved transport settings (see the <c>Telemetry*</c> properties on <see cref="QuonfigOptions"/>).</summary>
internal sealed class TelemetryReporterSettings
{
    public TelemetryReporterSettings(
        TimeSpan flushInterval = default,
        TimeSpan timeout = default,
        TimeSpan connectTimeout = default,
        int maxRetainedBatches = 0,
        int maxRetainedBytes = 0,
        TimeSpan maxRetainedAge = default)
    {
        FlushInterval = TelemetryDefaults.PositiveOr(flushInterval, TelemetryDefaults.FlushInterval);
        Timeout = TelemetryDefaults.PositiveOr(timeout, TelemetryDefaults.Timeout);
        ConnectTimeout = TelemetryDefaults.PositiveOr(connectTimeout, TelemetryDefaults.ConnectTimeout);
        MaxRetainedBatches = TelemetryDefaults.PositiveOr(maxRetainedBatches, TelemetryDefaults.MaxRetainedBatches);
        MaxRetainedBytes = TelemetryDefaults.PositiveOr(maxRetainedBytes, TelemetryDefaults.MaxRetainedBytes);
        MaxRetainedAge = TelemetryDefaults.PositiveOr(maxRetainedAge, TelemetryDefaults.MaxRetainedAge);
    }

    public TimeSpan FlushInterval { get; }
    public TimeSpan Timeout { get; }
    public TimeSpan ConnectTimeout { get; }
    public int MaxRetainedBatches { get; }
    public int MaxRetainedBytes { get; }
    public TimeSpan MaxRetainedAge { get; }

    public static TelemetryReporterSettings From(QuonfigOptions o) => new(
        o.TelemetryFlushInterval,
        o.TelemetryTimeout,
        o.TelemetryConnectTimeout,
        o.TelemetryMaxRetainedBatches,
        o.TelemetryMaxRetainedBytes,
        o.TelemetryMaxRetainedAge);
}

/// <summary>
/// One serialized telemetry window. <see cref="Body"/> is the only serialization of the window: it is
/// stored and resent byte-for-byte (P5, P9). <see cref="Payload"/> is the drained envelope object,
/// kept only for a user-supplied <see cref="ITelemetrySender"/>, which takes the object form.
/// </summary>
internal sealed class TelemetryBatch
{
    public TelemetryBatch(byte[] body, IDictionary<string, object?>? payload)
    {
        Body = body;
        Payload = payload;
    }

    public byte[] Body { get; }
    public IDictionary<string, object?>? Payload { get; }
}

/// <summary>Outcome of one telemetry POST that got an HTTP response.</summary>
internal sealed class TelemetryHttpResult
{
    public TelemetryHttpResult(int status, string? retryAfter, string bodySnippet)
    {
        Status = status;
        RetryAfter = retryAfter;
        BodySnippet = bodySnippet;
    }

    public int Status { get; }
    public string? RetryAfter { get; }
    public string BodySnippet { get; }
}

/// <summary>
/// Byte-level telemetry transport used by <see cref="TelemetryReporter"/>: returns the HTTP status and
/// <c>Retry-After</c> instead of throwing on non-2xx. Throws only when no response arrived (network
/// error or cancellation).
/// </summary>
internal interface ITelemetryTransport
{
    /// <summary>Endpoint named in the auth-failure log line.</summary>
    string Url { get; }

    Task<TelemetryHttpResult> SendAsync(TelemetryBatch batch, CancellationToken cancellationToken);
}

/// <summary>
/// Adapts a user-supplied <see cref="ITelemetrySender"/> (object payload, throws on failure) to the
/// byte-level transport: returning normally is a 200; any exception is a retryable failure.
/// </summary>
internal sealed class TelemetrySenderTransport : ITelemetryTransport
{
    private readonly ITelemetrySender _sender;

    public TelemetrySenderTransport(ITelemetrySender sender)
    {
        _sender = sender;
    }

    public string Url => "the configured ITelemetrySender";

    public async Task<TelemetryHttpResult> SendAsync(TelemetryBatch batch, CancellationToken cancellationToken)
    {
        await _sender.SendAsync(batch.Payload ?? new Dictionary<string, object?>(), cancellationToken).ConfigureAwait(false);
        return new TelemetryHttpResult(200, null, string.Empty);
    }
}
