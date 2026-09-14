using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk;
using Quonfig.Sdk.Telemetry;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// Pins WHICH guard rejections are counted as <c>guardRejected</c> failover telemetry (qfg-rr5b).
/// <list type="bullet">
///   <item><description>Only a STRICTLY OLDER payload (incoming generation &lt; held) counts — that is
///     the thing worth alerting on: a leg tried to move the client backwards.</description></item>
///   <item><description>An EQUAL-generation re-delivery (SSE reconnect resend, cold-ETag poll,
///     fallback-poller engage fetch) is a silent no-op: still not installed, still advances the
///     liveness stamp exactly where it did before, but NOT counted. The server re-sending config the
///     client already holds is normal steady-state traffic, not a failover signal.</description></item>
///   <item><description>The unversioned (generation &lt;= 0) carve-out is untouched: such a snapshot
///     is INSTALLED, never rejected, so it can never be counted.</description></item>
/// </list>
/// Drives the real network install paths (HTTP hedged fetch and SSE push) through the live client and
/// drains the real <see cref="FailoverCollector"/>, so deleting the narrowing makes these fail.
/// </summary>
public sealed class QuonfigGuardRejectedCountingTests
{
    private const string SdkKey = "test-sdk-key";
    private const string ConfigsPath = "/api/v2/configs";
    private const string SsePath = "/api/v2/sse/config";

    /// <summary>An SSE body that pushes no envelope, so only the HTTP path can touch the guard.</summary>
    private const string SseHeartbeatOnly = ": heartbeat\n\n";

    private static string EnvelopeJson(int generation) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{{\"configs\":[],\"meta\":{{\"version\":\"v1\",\"environment\":\"production\",\"generation\":{0}}}}}",
            generation);

    private static string SseEnvelope(int generation) => "data: " + EnvelopeJson(generation) + "\n\n";

    /// <summary>
    /// (Re)wires both endpoints from scratch: the config endpoint answers 200 with
    /// <paramref name="generation"/>, the SSE endpoint serves <paramref name="sseBody"/>. Called again
    /// mid-test to change what the server serves.
    /// </summary>
    private static void Serve(WireMockServer server, int generation, string sseBody)
    {
        server.Reset();
        server
            .Given(Request.Create().WithPath(ConfigsPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(EnvelopeJson(generation)));
        server
            .Given(Request.Create().WithPath(SsePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithBody(sseBody));
    }

    private static int SseRequestCount(WireMockServer server) =>
        server.LogEntries.Count(e => e.RequestMessage.Path == SsePath);

    private static async Task WaitForSseReconnectAsync(WireMockServer server)
    {
        // The reconnect only happens after the first stream was fully parsed, so once a SECOND connect
        // is logged the first stream's envelope has definitely been processed.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (SseRequestCount(server) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        SseRequestCount(server).Should().BeGreaterThanOrEqualTo(2, "the SSE stream must have reconnected");
    }

    /// <summary>
    /// Telemetry stays ENABLED (these tests drain <c>client.Failover</c>) but a no-op sender keeps it
    /// off the network and a far-out initial delay stops the reporter's background loop from draining
    /// the failover collector out from under the test's own <c>Drain()</c>. The fallback poller is off
    /// so the only fetches are the ones the test drives. SSE is always wired: the freshness stamp is
    /// only observable through the supervisor, and <c>StartSse</c> (and with it the supervisor) is
    /// skipped entirely when <c>StreamUrls</c> is empty — the SSE stream BODY decides whether any
    /// envelope is actually pushed.
    /// </summary>
    private static Quonfig NewClient(WireMockServer server)
    {
        return new Quonfig(new QuonfigOptions
        {
            SdkKey = SdkKey,
            ApiUrls = new[] { server.Urls[0] },
            StreamUrls = new[] { server.Urls[0] },
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromSeconds(5),
            TelemetrySender = new NoopSender(),
            TelemetryInitialDelay = TimeSpan.FromMinutes(5),
        });
    }

    private sealed class NoopSender : ITelemetrySender
    {
        public Task SendAsync(IDictionary<string, object?> payload, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private static long GuardRejected(Quonfig client)
    {
        client.Failover.Should().NotBeNull("telemetry is enabled, so the failover collector exists");
        var ev = client.Failover!.Drain();
        ev.Should().NotBeNull(
            "the init install records resolvedFrom, so the window always has at least one non-zero counter");
        var f = (IDictionary<string, object?>)ev!["failover"]!;
        return (long)f["guardRejected"]!;
    }

    /// <summary>
    /// HTTP fetch path: an established client polls a server that answers 200 with the SAME generation
    /// it already holds (the cold-ETag / fallback-poller-engage shape). Nothing installs, the poll is
    /// still a successful refresh (liveness advances, unchanged from before), and nothing is counted.
    /// </summary>
    [Fact]
    public async Task SameGenerationHttpRedelivery_IsNotCounted_AndStillAdvancesLiveness()
    {
        using var server = WireMockServer.Start();
        Serve(server, generation: 42, sseBody: SseHeartbeatOnly);

        await using var client = NewClient(server);
        await client.InitAsync();
        client.HeldGeneration.Should().Be(42, "the initial fetch established generation 42");

        // Wait for the SSE worker (and with it the supervisor that owns the stamp) to spin up.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (SseRequestCount(server) < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        var stampAfterInit = client.LastSuccessfulRefresh;
        stampAfterInit.Should().NotBeNull("the init fetch stamped liveness");

        // Two equal-generation re-deliveries on the HTTP path.
        await Task.Delay(100); // past timer resolution so the stamp comparison is meaningful
        await client.RefreshAsync();
        await client.RefreshAsync();

        client.HeldGeneration.Should().Be(42, "an equal-generation payload is still not installed");
        client.NetworkInstallCount.Should().Be(1, "only the init fetch installed");

        client.LastSuccessfulRefresh!.Value.Should().BeAfter(stampAfterInit!.Value,
            "an answered poll with nothing newer is still a successful refresh — liveness must advance "
            + "exactly as it did before the counting change");

        GuardRejected(client).Should().Be(0L,
            "an equal-generation re-delivery is a silent no-op, NOT a guardRejected failover signal (qfg-rr5b)");
    }

    /// <summary>
    /// SSE path: the stream re-delivers the envelope the client already holds on every connect
    /// (api-delivery's <c>sendInitialConfig</c> shape — it resends the current envelope regardless of
    /// <c>Last-Event-Id</c>). Nothing installs, nothing is counted, and the liveness stamp behavior is
    /// unchanged: a guard-rejected SSE push deliberately does NOT stamp (qfg-41nh.8).
    /// </summary>
    [Fact]
    public async Task SameGenerationSseRedelivery_IsNotCounted_AndLivenessUnchanged()
    {
        using var server = WireMockServer.Start();
        // Each stream EOFs after the event, so the client reconnects and the equal-generation
        // re-delivery repeats — one per connect, which is exactly the bead's measured shape.
        Serve(server, generation: 42, sseBody: SseEnvelope(42));

        await using var client = NewClient(server);
        await client.InitAsync();
        client.HeldGeneration.Should().Be(42, "the initial fetch established generation 42");
        var stampAfterInit = client.LastSuccessfulRefresh;
        stampAfterInit.Should().NotBeNull("the init fetch stamped liveness");

        await WaitForSseReconnectAsync(server);

        client.HeldGeneration.Should().Be(42, "an equal-generation SSE push is still not installed");
        client.NetworkInstallCount.Should().Be(1, "only the init fetch installed");
        client.LastSuccessfulRefresh.Should().Be(stampAfterInit,
            "a guard-rejected SSE push must not advance the freshness stamp — unchanged by qfg-rr5b");

        GuardRejected(client).Should().Be(0L,
            "an equal-generation SSE re-delivery is a silent no-op, NOT a guardRejected signal (qfg-rr5b)");
    }

    /// <summary>
    /// HTTP fetch path: a STRICTLY older payload (a failover to a stale secondary) is the thing
    /// guardRejected exists to report. Exactly one rejection, exactly one count.
    /// </summary>
    [Fact]
    public async Task StrictlyOlderHttpPayload_IsCountedAsGuardRejected()
    {
        using var server = WireMockServer.Start();
        Serve(server, generation: 42, sseBody: SseHeartbeatOnly);

        await using var client = NewClient(server);
        await client.InitAsync();
        client.HeldGeneration.Should().Be(42);

        // The server now serves the OLDER generation 41 — a regression attempt.
        Serve(server, generation: 41, sseBody: SseHeartbeatOnly);
        await client.RefreshAsync();

        client.HeldGeneration.Should().Be(42, "an older generation must be rejected — no regression");
        client.NetworkInstallCount.Should().Be(1, "the older payload was not installed");

        GuardRejected(client).Should().Be(1L,
            "a STRICTLY older payload is exactly what guardRejected must report (qfg-rr5b)");
    }

    /// <summary>
    /// SSE path: a strictly older pushed envelope is counted too. The stream EOFs and reconnects, so
    /// the replay can repeat — assert at least one, which is what distinguishes counted from silent.
    /// </summary>
    [Fact]
    public async Task StrictlyOlderSseEnvelope_IsCountedAsGuardRejected()
    {
        using var server = WireMockServer.Start();
        Serve(server, generation: 42, sseBody: SseEnvelope(41));

        await using var client = NewClient(server);
        await client.InitAsync();
        client.HeldGeneration.Should().Be(42);

        await WaitForSseReconnectAsync(server);

        client.HeldGeneration.Should().Be(42, "the older SSE envelope is guard-rejected");
        client.NetworkInstallCount.Should().Be(1, "only the init fetch installed");

        GuardRejected(client).Should().BeGreaterThanOrEqualTo(1L,
            "a STRICTLY older SSE push is a real backwards-move attempt and must be counted (qfg-rr5b)");
    }

    /// <summary>
    /// The unversioned (generation &lt;= 0) carve-out is untouched by qfg-rr5b: such a snapshot carries
    /// no ordering information, so it INSTALLS rather than being rejected, and therefore can never be
    /// counted as a guard rejection.
    /// </summary>
    [Fact]
    public async Task UnversionedSnapshot_IsInstalled_AndNotCounted()
    {
        using var server = WireMockServer.Start();
        Serve(server, generation: 42, sseBody: SseHeartbeatOnly);

        await using var client = NewClient(server);
        await client.InitAsync();
        client.HeldGeneration.Should().Be(42);

        // An UNVERSIONED (generation 0) snapshot — a server that predates the watermark, or one whose
        // rev-count failed.
        Serve(server, generation: 0, sseBody: SseHeartbeatOnly);
        await client.RefreshAsync();

        client.HeldGeneration.Should().Be(0, "gen<=0 carve-out: an unversioned snapshot installs, not freezes");
        client.NetworkInstallCount.Should().Be(2, "the carve-out install advances the count");

        GuardRejected(client).Should().Be(0L,
            "the unversioned carve-out installs, so there is no rejection to count");
    }
}
