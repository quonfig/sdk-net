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

    [Fact]
    public async Task EstablishedClient_InstallsUnversionedSnapshot_CarveOut()
    {
        using var server = WireMockServer.Start();
        ServeGeneration(server, 42);

        await using var client = NewClient(server);
        await client.InitAsync();

        client.HeldGeneration.Should().Be(42, "the initial fetch established generation 42");

        // Server now serves an UNVERSIONED (generation 0) snapshot — a server that predates the
        // watermark, or one whose rev-count failed. It carries no ordering information, so the
        // carve-out must install it rather than freeze the established client on 42.
        ServeGeneration(server, 0);
        await client.RefreshAsync();

        client.HeldGeneration.Should().Be(0, "gen-0 carve-out: an unversioned snapshot must install, not freeze");
        client.NetworkInstallCount.Should().Be(2, "the carve-out install advances the count");
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
