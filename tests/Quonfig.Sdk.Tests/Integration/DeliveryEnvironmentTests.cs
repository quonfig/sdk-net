// AUTO-GENERATED from integration-test-data/tests/eval/delivery_environment.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

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

    [Fact(DisplayName = "explicit environment pin wins over meta.environment")]
    public async Task ExplicitEnvironmentPinWinsOverMetaEnvironment()
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

        Assert.Equal(false, client.GetBool("flag.env-scoped"));
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
