using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// Unit coverage for the canonical reject-older install guard (qfg-7h5d.1.11) — the o02 mechanism.
/// An established client installs a network envelope only if its <c>Meta.generation</c> strictly
/// advances the held watermark: an older payload is rejected (no regression), an equal one is a
/// no-op (no flap), a newer one heals forward. Drives the real network install path via the
/// transport-backed <c>RefreshAsync</c>, so deleting the guard makes these assertions fail.
/// </summary>
public sealed class QuonfigRejectOlderTests
{
    private const string SdkKey = "test-sdk-key";

    private static string EnvelopeJson(int generation, string env = "production") =>
        $"{{\"configs\":[],\"meta\":{{\"version\":\"v1\",\"environment\":\"{env}\",\"generation\":{generation}}}}}";

    private static void ServeGeneration(WireMockServer server, int generation)
    {
        server.Reset();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(EnvelopeJson(generation)));
    }

    private static Quonfig NewClient(WireMockServer server) =>
        new Quonfig(new QuonfigOptions
        {
            SdkKey = SdkKey,
            ApiUrls = new[] { server.Urls[0] },
            // No SSE — keep the only installs the initial fetch + explicit RefreshAsync calls, so
            // the guard's behavior is observed deterministically.
            StreamUrls = Array.Empty<string>(),
            InitTimeout = TimeSpan.FromSeconds(5),
            // This test exercises the reject-older guard, not telemetry; opt out so the now-live
            // reporter (qfg-gxm6) does not post to the default telemetry endpoint.
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
        });

    [Fact]
    public async Task EstablishedClient_RejectsOlderGeneration_DoesNotRegress()
    {
        using var server = WireMockServer.Start();
        ServeGeneration(server, 42);

        await using var client = NewClient(server);
        await client.InitAsync();

        client.HeldGeneration.Should().Be(42, "the initial fetch established generation 42");
        client.NetworkInstallCount.Should().Be(1);

        // Server now serves the OLDER generation 41 (models a failover to a stale secondary).
        ServeGeneration(server, 41);
        await client.RefreshAsync();

        client.HeldGeneration.Should().Be(42, "an older generation must be rejected — no regression");
        client.NetworkInstallCount.Should().Be(1, "the rejected-older payload was not installed");
    }

    private static string MarkerEnvelopeJson(int generation, string marker) =>
        "{\"meta\":{\"version\":\"v1\",\"environment\":\"production\",\"generation\":" + generation + "}," +
        "\"configs\":[{\"id\":\"c-marker\",\"key\":\"marker\",\"type\":\"config\",\"valueType\":\"string\"," +
        "\"default\":{\"rules\":[{\"criteria\":[],\"value\":{\"type\":\"string\",\"value\":\"" + marker + "\"}}]}}]}";

    private static void ServeMarker(WireMockServer server, int generation, string marker)
    {
        server.Reset();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(MarkerEnvelopeJson(generation, marker)));
    }

    /// <summary>
    /// qfg-9dxb.9: an unversioned (generation &lt;= 0) payload must NOT install over a held positive
    /// generation. Gen 0 today only comes from a server whose git object store is damaged (rev-count
    /// failed) — the least trustworthy source — so installing it would move the client backward to
    /// OLD content, and (since the held generation is not lowered, qfg-9dxb.3) the healthy gen-N
    /// re-delivery would then be rejected as same-generation, sticking the client on OLD.
    /// </summary>
    [Fact]
    public async Task EstablishedClient_RejectsUnversionedSnapshot_OverHeldPositiveGeneration()
    {
        using var server = WireMockServer.Start();
        ServeMarker(server, 42, "NEW");

        await using var client = NewClient(server);
        await client.InitAsync();

        client.HeldGeneration.Should().Be(42, "the initial fetch established generation 42");
        client.GetString("marker").Should().Be("NEW");

        // A damaged-store server answers with generation 0 and OLD content.
        ServeMarker(server, 0, "OLD");
        await client.RefreshAsync();

        client.GetString("marker").Should().Be("NEW", "a gen-0 payload must not install over held generation 42");
        client.HeldGeneration.Should().Be(42);
        client.NetworkInstallCount.Should().Be(1, "the gen-0 payload was not installed");

        // The healthy server re-delivers generation 42 — the client still holds NEW.
        ServeMarker(server, 42, "NEW");
        await client.RefreshAsync();

        client.GetString("marker").Should().Be("NEW", "the client never went backward, so gen-42 re-delivery leaves NEW in place");
        client.HeldGeneration.Should().Be(42);

        // And an older positive snapshot is still rejected.
        ServeMarker(server, 41, "OLDER");
        await client.RefreshAsync();
        client.GetString("marker").Should().Be("NEW");
        client.NetworkInstallCount.Should().Be(1);
    }

    /// <summary>
    /// qfg-9dxb.9 follow-up: an IGNORED gen-0 200 must not leave its ETag remembered. api-delivery's
    /// ETag is the git sha, and it can repair the generation for the SAME sha, so if the ignored
    /// response's ETag stuck, every later poll would 304 and the client would stay on the old config
    /// until the next commit.
    /// </summary>
    [Fact]
    public async Task IgnoredGenZeroSnapshot_DoesNotPinItsETag()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", "\"shaA\"")
                .WithBody(MarkerEnvelopeJson(5, "A")));

        await using var client = NewClient(server);
        await client.InitAsync();
        client.GetString("marker").Should().Be("A");
        client.HeldGeneration.Should().Be(5);

        // Damaged-store server: sha B at generation 0 — ignored.
        server.Reset();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", "\"shaB\"")
                .WithBody(MarkerEnvelopeJson(0, "B")));
        await client.RefreshAsync();
        client.GetString("marker").Should().Be("A", "the gen-0 payload is ignored while generation 5 is held");

        // Repaired server: the SAME sha B, now at generation 6. It answers 304 to If-None-Match "shaB".
        server.Reset();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet()
                .WithHeader("If-None-Match", "\"shaB\""))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(304).WithHeader("ETag", "\"shaB\""));
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .AtPriority(10)
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", "\"shaB\"")
                .WithBody(MarkerEnvelopeJson(6, "B")));
        await client.RefreshAsync();

        client.GetString("marker").Should().Be("B", "the ignored gen-0 response's ETag must not turn the repaired gen-6 payload into a 304");
        client.HeldGeneration.Should().Be(6);
    }

    /// <summary>
    /// qfg-9dxb.9: a client that has only ever seen generation 0 (held == 0, never a real generation)
    /// keeps installing each gen-0 payload, so it never freezes on stale config.
    /// </summary>
    [Fact]
    public async Task GenZeroOnlyClient_KeepsInstallingEachGenZeroPayload()
    {
        using var server = WireMockServer.Start();
        ServeMarker(server, 0, "A");

        await using var client = NewClient(server);
        await client.InitAsync();

        client.GetString("marker").Should().Be("A");
        client.HeldGeneration.Should().Be(0);

        ServeMarker(server, 0, "B");
        await client.RefreshAsync();
        client.GetString("marker").Should().Be("B", "held generation is 0, so a gen-0 payload installs");

        ServeMarker(server, 0, "C");
        await client.RefreshAsync();
        client.GetString("marker").Should().Be("C");

        client.HeldGeneration.Should().Be(0);
        client.NetworkInstallCount.Should().Be(3, "every gen-0 payload installed while no real generation was ever held");

        // Once a real generation arrives it is held, and gen 0 no longer overrides it.
        ServeMarker(server, 5, "REAL");
        await client.RefreshAsync();
        client.GetString("marker").Should().Be("REAL");
        client.HeldGeneration.Should().Be(5);

        ServeMarker(server, 0, "D");
        await client.RefreshAsync();
        client.GetString("marker").Should().Be("REAL", "gen 0 must not override a held real generation");
        client.NetworkInstallCount.Should().Be(4);
    }

    [Fact]
    public async Task EstablishedClient_HealsForwardToNewerGeneration()
    {
        using var server = WireMockServer.Start();
        ServeGeneration(server, 42);

        await using var client = NewClient(server);
        await client.InitAsync();

        ServeGeneration(server, 41); // older — rejected
        await client.RefreshAsync();
        client.HeldGeneration.Should().Be(42);

        ServeGeneration(server, 43); // newer — heals forward
        await client.RefreshAsync();
        client.HeldGeneration.Should().Be(43, "a newer generation heals forward — reject-older only blocks going backward");
        client.NetworkInstallCount.Should().Be(2, "init (42) + heal-forward (43); the rejected 41 did not count");
    }

    [Fact]
    public async Task EstablishedClient_SameGeneration_IsNoOp()
    {
        using var server = WireMockServer.Start();
        ServeGeneration(server, 42);

        await using var client = NewClient(server);
        await client.InitAsync();

        // Re-serving the same generation must not re-install (o04: no flap from the equal leg).
        await client.RefreshAsync();
        await client.RefreshAsync();

        client.HeldGeneration.Should().Be(42);
        client.NetworkInstallCount.Should().Be(1, "a same-generation snapshot is a no-op");
    }

    [Fact]
    public async Task FreshClient_AcceptsFirstSnapshot_EvenAtGenerationZero()
    {
        using var server = WireMockServer.Start();
        ServeGeneration(server, 0);

        await using var client = NewClient(server);
        await client.InitAsync();

        client.HeldGeneration.Should().Be(0, "a fresh client always accepts its first snapshot, even at generation 0");
        client.NetworkInstallCount.Should().Be(1);
        client.ResolvedFrom.Should().Be("primary");
    }
}
