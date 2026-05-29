// AUTO-GENERATED from integration-test-data/tests/eval/delivery_environment.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts
//
// LOCAL PATCH (qfg-pinh, 2026-05-29): case "explicit environment pin ..." was edited in place to
// the decided delivery-mode contract (meta.environment authoritative; pin ignored). The upstream
// YAML still encodes the pre-decision expectation; until the cross-SDK gate regeneration lands
// (qfg-pinh: rewrite delivery_environment.yaml case 2 + regenerate all 6 targets), a plain
// `npm run generate` will revert this case — reconcile with the YAML before regenerating.

using System;
using System.Threading.Tasks;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public sealed class DeliveryEnvironmentTests
{

    [Fact(DisplayName = "singular environment override wins over default when env not pinned")]
    public async Task SingularEnvironmentOverrideWinsOverDefaultWhenEnvNotPinned()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", "\"v1\"")
                .WithBody("{\"meta\":{\"version\":\"v1\",\"environment\":\"development\"},\"configs\":[{\"id\":\"c-env\",\"key\":\"flag.env-scoped\",\"type\":\"bool\",\"valueType\":\"bool\",\"sendToClientSdk\":false,\"default\":{\"rules\":[{\"criteria\":[{\"operator\":\"ALWAYS_TRUE\"}],\"value\":{\"type\":\"bool\",\"value\":true}}]},\"environment\":{\"id\":\"development\",\"rules\":[{\"criteria\":[{\"operator\":\"ALWAYS_TRUE\"}],\"value\":{\"type\":\"bool\",\"value\":false}}]}}]}"));

        await using var client = new Quonfig(new QuonfigOptions
        {
            SdkKey = "sdk-test",
            ApiUrls = new[] { server.Urls[0] },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromSeconds(5),
        });
        await client.InitAsync();

        Assert.Equal(false, client.GetBool("flag.env-scoped"));
    }

    // qfg-pinh (Jeff, 2026-05-29, Option A): in delivery mode meta.environment is AUTHORITATIVE
    // and an explicit pin is datadir-only / ignored. The original generated case asserted the
    // pre-decision "pin wins" behavior (expected false). meta.environment="staging" has no matching
    // env block on the row, so eval now falls to default (true), and the pin="development" is ignored.
    // This case is patched in place to the decided contract pending the cross-SDK gate regeneration
    // tracked in qfg-pinh (delivery_environment.yaml case 2 rewrite + regenerate all 6 targets).
    [Fact(DisplayName = "explicit environment pin is ignored in delivery mode (meta.environment authoritative)")]
    public async Task ExplicitEnvironmentPinIsIgnoredInDeliveryMode()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", "\"v1\"")
                .WithBody("{\"meta\":{\"version\":\"v1\",\"environment\":\"staging\"},\"configs\":[{\"id\":\"c-env\",\"key\":\"flag.env-scoped\",\"type\":\"bool\",\"valueType\":\"bool\",\"sendToClientSdk\":false,\"default\":{\"rules\":[{\"criteria\":[{\"operator\":\"ALWAYS_TRUE\"}],\"value\":{\"type\":\"bool\",\"value\":true}}]},\"environment\":{\"id\":\"development\",\"rules\":[{\"criteria\":[{\"operator\":\"ALWAYS_TRUE\"}],\"value\":{\"type\":\"bool\",\"value\":false}}]}}]}"));

        await using var client = new Quonfig(new QuonfigOptions
        {
            SdkKey = "sdk-test",
            ApiUrls = new[] { server.Urls[0] },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromSeconds(5),
            Environment = "development",
        });
        await client.InitAsync();

        // meta.environment="staging" is authoritative; the row's only env block is "development",
        // which no longer matches, so eval falls to the row default (true). The pin is ignored.
        Assert.Equal(true, client.GetBool("flag.env-scoped"));
    }

    [Fact(DisplayName = "config without environment block falls back to default in delivery mode")]
    public async Task ConfigWithoutEnvironmentBlockFallsBackToDefaultInDeliveryMode()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", "\"v1\"")
                .WithBody("{\"meta\":{\"version\":\"v1\",\"environment\":\"development\"},\"configs\":[{\"id\":\"c-def\",\"key\":\"flag.default-only\",\"type\":\"bool\",\"valueType\":\"bool\",\"sendToClientSdk\":false,\"default\":{\"rules\":[{\"criteria\":[{\"operator\":\"ALWAYS_TRUE\"}],\"value\":{\"type\":\"bool\",\"value\":true}}]}}]}"));

        await using var client = new Quonfig(new QuonfigOptions
        {
            SdkKey = "sdk-test",
            ApiUrls = new[] { server.Urls[0] },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromSeconds(5),
        });
        await client.InitAsync();

        Assert.Equal(true, client.GetBool("flag.default-only"));
    }
}
