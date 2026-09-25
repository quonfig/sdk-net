using System;
using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Quonfig.Sdk.Telemetry;
using Quonfig.Sdk.Tests.Telemetry.Helpers;
using Xunit;

namespace Quonfig.Sdk.Tests.Telemetry;

/// <summary>Unit coverage for the transport-policy helpers behind the T1-T8 contract (qfg-y8je.10).</summary>
public sealed class TelemetryTransportUnitTests
{
    [Theory]
    [InlineData(200, "Ok")]
    [InlineData(204, "Ok")]
    [InlineData(301, "Rejected")]
    [InlineData(400, "Rejected")]
    [InlineData(401, "Auth")]
    [InlineData(403, "Auth")]
    [InlineData(404, "Auth")]
    [InlineData(408, "Retryable")]
    [InlineData(413, "Rejected")]
    [InlineData(422, "Rejected")]
    [InlineData(429, "Retryable")]
    [InlineData(500, "Retryable")]
    [InlineData(503, "Retryable")]
    public void ClassifyStatus(int status, string expected)
    {
        TelemetryTransportQueue.ClassifyStatus(status).ToString().Should().Be(expected);
    }

    [Fact]
    public void ParseRetryAfter()
    {
        const long now = 1_790_294_400_000;
        TelemetryTransportQueue.ParseRetryAfterMs("120", now).Should().Be(120_000);
        TelemetryTransportQueue.ParseRetryAfterMs(" 5 ", now).Should().Be(5_000);
        TelemetryTransportQueue.ParseRetryAfterMs("3600", now).Should().Be(600_000);
        TelemetryTransportQueue.ParseRetryAfterMs("99999999999999999999", now).Should().Be(600_000);
        string future = DateTimeOffset.FromUnixTimeMilliseconds(now + 90_000).ToString("r", CultureInfo.InvariantCulture);
        TelemetryTransportQueue.ParseRetryAfterMs(future, now).Should().Be(90_000);
        string past = DateTimeOffset.FromUnixTimeMilliseconds(now - 90_000).ToString("r", CultureInfo.InvariantCulture);
        TelemetryTransportQueue.ParseRetryAfterMs(past, now).Should().Be(0);
        TelemetryTransportQueue.ParseRetryAfterMs("soon", now).Should().BeNull();
        TelemetryTransportQueue.ParseRetryAfterMs(null, now).Should().BeNull();
        TelemetryTransportQueue.ParseRetryAfterMs("", now).Should().BeNull();
    }

    [Fact]
    public void Expire_IsStrictlyGreaterThanMaxAge()
    {
        var clock = new ManualTelemetryClock();
        var queue = new TelemetryTransportQueue(
            new NeverTransport(), NullLogger.Instance, clock, TimeSpan.FromSeconds(15),
            maxRetainedBatches: 5, maxRetainedBytes: 1024, maxRetainedAge: TimeSpan.FromMinutes(5), onDisabled: () => { });
        queue.Append(Batch(10));
        clock.Advance(TimeSpan.FromMilliseconds(300_000));
        queue.Expire();
        queue.RetainedCount.Should().Be(1, "a batch aged exactly the max age survives");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        queue.Expire();
        queue.RetainedCount.Should().Be(0);
    }

    [Fact]
    public void Append_EvictsOldestByCountAndBytes_OversizeNotCounted()
    {
        var clock = new ManualTelemetryClock();
        var queue = new TelemetryTransportQueue(
            new NeverTransport(), NullLogger.Instance, clock, TimeSpan.FromSeconds(15),
            maxRetainedBatches: 2, maxRetainedBytes: 100, maxRetainedAge: TimeSpan.FromMinutes(5), onDisabled: () => { });
        queue.Append(Batch(40));
        queue.Append(Batch(40));
        queue.Append(Batch(10));
        queue.RetainedCount.Should().Be(2);
        queue.RetainedBytes.Should().Be(50);
        queue.Append(Batch(90)); // 10 + 90 = 100 fits; 40 was evicted by count, then bytes
        queue.RetainedBytes.Should().Be(100);
        queue.Append(Batch(500)); // oversize: kept only until its one send, never counted against the caps
        queue.RetainedCount.Should().Be(3);
        queue.RetainedBytes.Should().Be(600);
    }

    private static TelemetryBatch Batch(int bytes) => new(new byte[bytes], payload: null);

    private sealed class NeverTransport : ITelemetryTransport
    {
        public string Url => "http://unused";

        public System.Threading.Tasks.Task<TelemetryHttpResult> SendAsync(TelemetryBatch batch, System.Threading.CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
