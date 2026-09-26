using System;
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
/// qfg-9dxb.3 (audit H2, cross-SDK) coverage at the client level:
/// <list type="bullet">
///   <item><description>Fix B: a 200 whose body is not a config envelope (no <c>meta</c> object
///     with a non-empty <c>version</c> — e.g. <c>{}</c> or <c>{"error":"x"}</c> from a misbehaving
///     proxy/WAF) is a leg error. It must not wipe an established client's keys, must not lower the
///     held generation, must let the hedge fail over to the secondary, and must not pin its ETag
///     so later 304s keep the junk "current".</description></item>
///   <item><description>Fix A: an unversioned install (generation absent/0) still installs (the
///     carve-out), but never lowers a positive held generation — e.g. a <c>qfg serve</c> payload
///     (version + environment, no generation).</description></item>
/// </list>
/// </summary>
public sealed class QuonfigNonEnvelopeTests
{
    private const string SdkKey = "test-sdk-key";

    private static string EnvelopeWithKey(string key, int? generation)
    {
        string gen = generation is null ? string.Empty : $",\"generation\":{generation}";
        return "{\"meta\":{\"version\":\"v1\",\"environment\":\"production\"" + gen + "}," +
            "\"configs\":[{\"id\":\"c-" + key + "\",\"key\":\"" + key + "\",\"type\":\"feature_flag\",\"valueType\":\"bool\"," +
            "\"default\":{\"rules\":[{\"criteria\":[{\"operator\":\"ALWAYS_TRUE\"}],\"value\":{\"type\":\"bool\",\"value\":true}}]}}]}";
    }

    private static void Serve(WireMockServer server, string body, string? etag = null)
    {
        server.Reset();
        var response = Response.Create().WithStatusCode(200)
            .WithHeader("Content-Type", "application/json")
            .WithBody(body);
        if (etag is not null) response = response.WithHeader("ETag", etag);
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(response);
    }

    private static Quonfig NewClient(params WireMockServer[] servers) =>
        new Quonfig(new QuonfigOptions
        {
            SdkKey = SdkKey,
            ApiUrls = servers.Select(s => s.Urls[0]).ToArray(),
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromSeconds(10),
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
        });

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"error\":\"x\"}")]
    [InlineData("{\"meta\":{\"version\":\"\",\"environment\":\"production\"},\"configs\":[]}")]
    [InlineData("{\"meta\":{\"environment\":\"production\",\"generation\":99},\"configs\":[]}")]
    public async Task EstablishedClient_NonEnvelope200_DoesNotWipeKeysOrLowerGeneration(string junk)
    {
        using var server = WireMockServer.Start();
        Serve(server, EnvelopeWithKey("flag.kept", 42));

        await using var client = NewClient(server);
        await client.InitAsync();
        client.Keys().Should().Contain("flag.kept");
        client.HeldGeneration.Should().Be(42);

        Serve(server, junk);
        await client.RefreshAsync();

        client.Keys().Should().Contain("flag.kept", "a non-envelope 200 must not wipe the held config");
        client.HeldGeneration.Should().Be(42, "a non-envelope 200 must not lower the held generation");
        client.NetworkInstallCount.Should().Be(1, "a non-envelope 200 is a leg error, not an install");
    }

    [Fact]
    public async Task Hedge_PrimaryNonEnvelope_FailsOverToSecondary()
    {
        using var primary = WireMockServer.Start();
        using var secondary = WireMockServer.Start();
        Serve(primary, "{}");
        Serve(secondary, EnvelopeWithKey("flag.from.secondary", 7));

        await using var client = NewClient(primary, secondary);
        await client.InitAsync();

        client.Keys().Should().Contain("flag.from.secondary",
            "a junk primary 200 is a leg error, so the hedge proceeds to the secondary");
        client.HeldGeneration.Should().Be(7);
        client.ResolvedFrom.Should().Be("secondary");
    }

    [Fact]
    public async Task NonEnvelope200_ETagIsNotStored_SoLaterPollIsNotPinnedBy304()
    {
        using var server = WireMockServer.Start();
        Serve(server, EnvelopeWithKey("flag.first", 42));

        await using var client = NewClient(server);
        await client.InitAsync();

        // A junk 200 carrying an ETag.
        Serve(server, "{}", etag: "\"junk\"");
        await client.RefreshAsync();

        // The real server now has gen 43. If the junk ETag had been stored, the next request would
        // send If-None-Match: "junk" and get a 304, pinning the client on the junk "version".
        server.Reset();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet()
                .WithHeader("If-None-Match", "\"junk\""))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(304));
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .AtPriority(2)
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(EnvelopeWithKey("flag.second", 43)));
        await client.RefreshAsync();

        client.HeldGeneration.Should().Be(43, "the junk ETag must not have been stored");
        client.Keys().Should().Contain("flag.second");
    }

    [Fact]
    public async Task EstablishedClient_QfgServePayload_InstallsViaCarveOut_KeepsHeldGeneration()
    {
        using var server = WireMockServer.Start();
        Serve(server, EnvelopeWithKey("flag.old", 42));

        await using var client = NewClient(server);
        await client.InitAsync();
        client.HeldGeneration.Should().Be(42);

        // `qfg serve` sends version + environment but no generation.
        Serve(server, EnvelopeWithKey("flag.served", generation: null));
        await client.RefreshAsync();

        client.Keys().Should().Contain("flag.served", "an unversioned envelope still installs (carve-out)");
        client.Keys().Should().NotContain("flag.old");
        client.NetworkInstallCount.Should().Be(2);
        client.HeldGeneration.Should().Be(42,
            "an unversioned install must never lower a positive held generation");

        // And an older positive snapshot is therefore still rejected afterwards.
        Serve(server, EnvelopeWithKey("flag.older", 41));
        await client.RefreshAsync();
        client.Keys().Should().Contain("flag.served");
        client.HeldGeneration.Should().Be(42);
    }
}
