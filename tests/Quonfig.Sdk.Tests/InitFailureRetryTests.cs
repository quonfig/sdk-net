using System;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Exceptions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// qfg-9p4i.3: under <see cref="OnInitFailure.Fail"/> a caller that catches the
/// <see cref="Quonfig.InitAsync"/> exception and keeps the client must still get a client that
/// heals. Before the fix only <see cref="OnInitFailure.Continue"/> started the update
/// channel (SSE + fallback poller), so a caught Throw left a client serving defaults forever.
/// Spec: project/plans/2026-10-05-init-failure-design.md, decision 4 and section E step 1
/// ("no permanently dead clients").
/// </summary>
public sealed class InitFailureRetryTests
{
    private const string SdkKey = "init-failure-retry-tests";

    private static string EnvelopeJson(int generation) =>
        $"{{\"configs\":[],\"meta\":{{\"version\":\"gen-{generation}\",\"environment\":\"production\",\"generation\":{generation}}}}}";

    private static void ServeDown(WireMockServer server) =>
        server.Given(Request.Create().UsingAnyMethod())
            .RespondWith(Response.Create().WithStatusCode(503));

    private static void ServeHealthy(WireMockServer server, int generation)
    {
        server.Reset();
        server.Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", $"\"gen-{generation}\"")
                .WithBody(EnvelopeJson(generation)));
        // The SSE endpoint stays down, so the heal must come through the fallback poller.
        server.Given(Request.Create().WithPath("/api/v2/sse/config").UsingAnyMethod())
            .RespondWith(Response.Create().WithStatusCode(503));
    }

    [Fact]
    public async Task CaughtThrow_ClientKeepsRetryingAndHeals()
    {
        using var server = WireMockServer.Start();
        ServeDown(server);

        await using var client = new Quonfig(new QuonfigOptions
        {
            SdkKey = SdkKey,
            ApiUrls = new[] { server.Urls[0] },
            StreamUrls = new[] { server.Urls[0] },
            InitTimeout = TimeSpan.FromSeconds(5),
            OnInitFailure = OnInitFailure.Fail,
            OnNoDefault = OnNoDefault.Ignore,
            FallbackPollThreshold = TimeSpan.FromMilliseconds(200),
            FallbackPollInterval = TimeSpan.FromMilliseconds(200),
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
        });

        Func<Task> init = () => client.InitAsync();
        await init.Should().ThrowAsync<QuonfigException>("both legs are down and the policy is Throw");
        client.HeldGeneration.Should().BeLessThan(1, "nothing has been installed yet");

        // The network comes back. A client object that survived a caught Throw must heal.
        ServeHealthy(server, generation: 7);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (client.HeldGeneration != 7 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        client.HeldGeneration.Should().Be(7,
            "after a caught OnInitFailure.Fail the update channel must stay alive and install the first envelope");
    }
}
