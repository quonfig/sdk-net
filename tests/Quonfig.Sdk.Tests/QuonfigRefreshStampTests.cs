using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// Pins the <see cref="Quonfig.LastSuccessfulRefresh"/> stamping semantics (qfg-41nh.8):
/// <list type="bullet">
///   <item><description>An SSE envelope REJECTED by the reject-older guard must NOT advance the
///     stamp — otherwise a stale-replay loop reports a frozen client as fresh (sdk-java parity:
///     stamp only on accepted installs).</description></item>
///   <item><description>An HTTP poll the server ANSWERS — even when the payload is guard-rejected
///     or a 304, i.e. nothing newer to install — IS a successful refresh and must advance the
///     stamp (sdk-go post-qfg-41nh.11 semantics: freshness means "config is reachable").</description></item>
/// </list>
/// </summary>
public sealed class QuonfigRefreshStampTests
{
    private const string SdkKey = "test-sdk-key";
    private const string ConfigsPath = "/api/v2/configs";
    private const string SsePath = "/api/v2/sse/config";

    private static string EnvelopeJson(int generation) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{{\"configs\":[],\"meta\":{{\"version\":\"v1\",\"environment\":\"production\",\"generation\":{0}}}}}",
            generation);

    private static void ServeConfigs(WireMockServer server, int generation)
    {
        server
            .Given(Request.Create().WithPath(ConfigsPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(EnvelopeJson(generation)));
    }

    private static void ServeSse(WireMockServer server, string body)
    {
        server
            .Given(Request.Create().WithPath(SsePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithBody(body));
    }

    private static int SseRequestCount(WireMockServer server) =>
        server.LogEntries.Count(e => e.RequestMessage.Path == SsePath);

    [Fact]
    public async Task GuardRejectedSseEnvelope_DoesNotAdvanceLastSuccessfulRefresh()
    {
        using var server = WireMockServer.Start();
        ServeConfigs(server, 42);
        // The SSE stream (re)delivers an OLDER envelope (gen 41) on every connect — the
        // stale-replay outage shape. Each stream EOFs after the event, so the client
        // reconnects and the replay repeats.
        ServeSse(server, "data: " + EnvelopeJson(41) + "\n\n");

        await using var client = new Quonfig(new QuonfigOptions
        {
            SdkKey = SdkKey,
            ApiUrls = new[] { server.Urls[0] },
            StreamUrls = new[] { server.Urls[0] },
            InitTimeout = TimeSpan.FromSeconds(5),
        });
        await client.InitAsync();
        var stampAfterInit = client.LastSuccessfulRefresh;
        stampAfterInit.Should().NotBeNull("init installed generation 42");

        // Wait for a SECOND SSE connect: the reconnect only happens after the first stream was
        // fully parsed, so by then the rejected gen-41 envelope has definitely been processed.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (SseRequestCount(server) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        SseRequestCount(server).Should().BeGreaterThanOrEqualTo(2, "the SSE stream must have reconnected");

        client.HeldGeneration.Should().Be(42, "the older SSE envelope is guard-rejected");
        client.NetworkInstallCount.Should().Be(1, "only the init fetch installed");
        client.LastSuccessfulRefresh.Should().Be(stampAfterInit,
            "a guard-REJECTED SSE envelope must not advance the freshness stamp — the client is frozen, not fresh");
    }

    [Fact]
    public async Task AnsweredHttpPollWithNothingNewer_AdvancesLastSuccessfulRefresh()
    {
        using var server = WireMockServer.Start();
        ServeConfigs(server, 42);
        // SSE endpoint serves only a comment (no envelope) so the supervisor exists and the
        // stamp is observable through it, without SSE installs interfering.
        ServeSse(server, ": heartbeat\n\n");

        await using var client = new Quonfig(new QuonfigOptions
        {
            SdkKey = SdkKey,
            ApiUrls = new[] { server.Urls[0] },
            StreamUrls = new[] { server.Urls[0] },
            InitTimeout = TimeSpan.FromSeconds(5),
        });
        await client.InitAsync();

        // Wait for the SSE worker (and with it the supervisor) to spin up.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (SseRequestCount(server) < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        var stampAfterInit = client.LastSuccessfulRefresh;
        stampAfterInit.Should().NotBeNull();

        // Let the clock move past timer resolution, then poll: the server answers 200 with the
        // SAME generation — guard-rejected, nothing installed, but the poll SUCCEEDED.
        await Task.Delay(100);
        await client.RefreshAsync();

        client.NetworkInstallCount.Should().Be(1, "same-generation payload is a no-op install");
        client.LastSuccessfulRefresh.Should().NotBeNull();
        client.LastSuccessfulRefresh!.Value.Should().BeAfter(stampAfterInit!.Value,
            "an answered poll with nothing newer IS a successful refresh (sdk-go post-qfg-41nh.11 semantics)");
    }
}
