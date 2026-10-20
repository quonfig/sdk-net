using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Exceptions;
using Quonfig.Sdk.Tests.Telemetry.Helpers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests;

using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

/// <summary>
/// qfg-9p4i.10 (net minor): the fail / wait / continue surface, the readiness API, the
/// <see cref="QuonfigInitException"/> family and the section C log contract.
/// Spec: project/plans/2026-10-05-init-failure-design.md (Decisions, B.1-B.4, C).
/// </summary>
public sealed class InitFailureModeTests
{
    private const string SdkKey = "init-failure-mode-tests-secret-key";

    private static string EnvelopeJson(int generation) =>
        $"{{\"configs\":[],\"meta\":{{\"version\":\"gen-{generation}\",\"environment\":\"production\",\"generation\":{generation}}}}}";

    private static void ServeStatus(WireMockServer server, int status)
    {
        server.Reset();
        server.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithStatusCode(status));
    }

    private static void ServeHealthy(WireMockServer server, int generation)
    {
        server.Reset();
        server.Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", $"\"gen-{generation}\"")
                .WithBody(EnvelopeJson(generation)));
        server.Given(Request.Create().WithPath("/api/v2/sse/config").UsingAnyMethod())
            .RespondWith(Response.Create().WithStatusCode(503));
    }

    private static QuonfigOptions Options(string[] apiUrls, OnInitFailure mode, TimeSpan initTimeout, CaptureLogger? logger = null) => new()
    {
        SdkKey = SdkKey,
        ApiUrls = apiUrls,
        StreamUrls = Array.Empty<string>(),
        FallbackPollEnabled = false,
        InitTimeout = initTimeout,
        OnInitFailure = mode,
        OnNoDefault = OnNoDefault.Ignore,
        Logger = logger,
        CollectEvaluationSummaries = false,
        ContextUploadMode = ContextUploadMode.None,
    };

    [Fact]
    public void DeprecatedValues_AreAliasesOfTheNewNames()
    {
#pragma warning disable CS0618
        OnInitFailure.Throw.Should().Be(OnInitFailure.Fail);
        OnInitFailure.ReturnDefaults.Should().Be(OnInitFailure.Continue);
#pragma warning restore CS0618
        new QuonfigOptions().OnInitFailure.Should().Be(OnInitFailure.Fail, "fail is the default");
        new QuonfigOptions().InitTimeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void TimeoutException_IsAnInitException()
    {
        var ex = new QuonfigInitTimeoutException("t");
        ex.Should().BeAssignableTo<QuonfigInitException>();
        ex.Should().BeAssignableTo<QuonfigException>();
        ex.Reason.Should().Be(QuonfigInitFailureReason.Timeout);
    }

    [Fact]
    public async Task Fail_RetriesThroughErrorsUntilTheTimeout_ThenRaisesUnavailable()
    {
        using var server = WireMockServer.Start();
        ServeStatus(server, 503);
        var logger = new CaptureLogger();
        await using var client = new Quonfig(Options(new[] { server.Urls[0] }, OnInitFailure.Fail, TimeSpan.FromSeconds(2), logger));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        sw.Stop();

        raised.Should().NotBeOfType<QuonfigInitTimeoutException>();
        raised.Reason.Should().Be(QuonfigInitFailureReason.Unavailable);
        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(1500), "fail retries until initTimeout");
        server.LogEntries.Count(e => e.RequestMessage.Path == "/api/v2/configs").Should().BeGreaterThan(1, "the client retries through blips");
        client.IsReady.Should().BeFalse();

        var host = new Uri(server.Urls[0]).Authority;
        logger.LogCount(MelLogLevel.Warning, @"^\[quonfig\] init: config not loaded yet, retrying until initTimeout \(2s\)\. primary=" + System.Text.RegularExpressions.Regex.Escape(host) + ": HTTP 503").Should().Be(1);
        logger.LogCount(MelLogLevel.Error, @"^\[quonfig\] init FAILED: no config after 2\.\ds\. primary=.*onInitFailure=fail: raising QuonfigInitException\. Options: onInitFailure=wait to keep waiting, onInitFailure=continue to start on code defaults, or raise initTimeout\.").Should().Be(1);
        logger.Lines.Should().NotContain(l => l.Message.Contains(SdkKey), "the SDK key is never logged");
    }

    [Fact]
    public async Task Fail_AuthRejectedOnEveryLeg_RaisesUnauthorizedAtOnce()
    {
        using var leg0 = WireMockServer.Start();
        using var leg1 = WireMockServer.Start();
        ServeStatus(leg0, 401);
        ServeStatus(leg1, 403);
        var logger = new CaptureLogger();
        await using var client = new Quonfig(Options(new[] { leg0.Urls[0], leg1.Urls[0] }, OnInitFailure.Fail, TimeSpan.FromSeconds(8), logger));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        sw.Stop();

        raised.Reason.Should().Be(QuonfigInitFailureReason.Unauthorized);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
        logger.LogCount(MelLogLevel.Error, @"^\[quonfig\] init FAILED: SDK key rejected \(401\) by primary and secondary\. Check the key and environment\. onInitFailure=fail: raising now\.").Should().Be(1);
    }

    [Fact]
    public async Task Continue_AuthRejectedOnEveryLeg_ContinuesAtOnce()
    {
        using var leg0 = WireMockServer.Start();
        ServeStatus(leg0, 401);
        await using var client = new Quonfig(Options(new[] { leg0.Urls[0] }, OnInitFailure.Continue, TimeSpan.FromSeconds(8)));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await client.InitAsync();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
        client.IsReady.Should().BeFalse();
        client.GetString("any", defaultValue: "D").Should().Be("D");
    }

    [Fact]
    public async Task Continue_ReturnsAtTimeout_LogsErrorAndHealsLater()
    {
        using var server = WireMockServer.Start();
        ServeStatus(server, 503);
        var logger = new CaptureLogger();
        var opts = Options(new[] { server.Urls[0] }, OnInitFailure.Continue, TimeSpan.FromMilliseconds(500), logger);
        opts.StreamUrls = new[] { server.Urls[0] };
        opts.FallbackPollEnabled = true;
        opts.FallbackPollThreshold = TimeSpan.FromMilliseconds(200);
        opts.FallbackPollInterval = TimeSpan.FromMilliseconds(200);
        await using var client = new Quonfig(opts);

        await client.InitAsync();
        client.IsReady.Should().BeFalse();
        logger.LogCount(MelLogLevel.Error, @"^\[quonfig\] init: no config after 0\.\ds\. onInitFailure=continue: STARTING ON CODE DEFAULTS\. This instance may disagree with the rest of your fleet\. Retrying in background\.").Should().Be(1);

        ServeHealthy(server, generation: 7);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.WaitForReadyAsync(cts.Token);
        client.IsReady.Should().BeTrue();
        client.HeldGeneration.Should().Be(7);
        logger.LogCount(MelLogLevel.Warning, @"^\[quonfig\] init recovered: config loaded after \d+\.\ds via primary \(version gen-7\)\. Now serving live config\.").Should().Be(1);
    }

    [Fact]
    public async Task Wait_BlocksPastTheTimeout_AndReturnsWhenConfigArrives()
    {
        using var server = WireMockServer.Start();
        ServeStatus(server, 503);
        var logger = new CaptureLogger();
        await using var client = new Quonfig(Options(new[] { server.Urls[0] }, OnInitFailure.Wait, TimeSpan.FromMilliseconds(300), logger));

        var init = client.InitAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        init.IsCompleted.Should().BeFalse("wait keeps blocking past initTimeout");
        logger.LogCount(MelLogLevel.Warning, @"^\[quonfig\] init: no config after 0\.\ds\. onInitFailure=wait: still blocking startup and retrying indefinitely\. primary=.*Use onInitFailure=fail or continue to bound startup\.").Should().Be(1);

        ServeHealthy(server, generation: 3);
        var done = await Task.WhenAny(init, Task.Delay(TimeSpan.FromSeconds(20)));
        done.Should().BeSameAs(init, "init returns once the first config lands");
        await init;
        client.IsReady.Should().BeTrue();
        client.HeldGeneration.Should().Be(3);
    }

    [Fact]
    public async Task Wait_HonorsTheCallerCancellationToken()
    {
        using var server = WireMockServer.Start();
        ServeStatus(server, 503);
        await using var client = new Quonfig(Options(new[] { server.Urls[0] }, OnInitFailure.Wait, TimeSpan.FromMilliseconds(200)));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        Func<Task> init = () => client.InitAsync(cts.Token);
        await init.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Wait_AuthRejected_LogsErrorAndKeepsRetrying()
    {
        using var server = WireMockServer.Start();
        ServeStatus(server, 401);
        var logger = new CaptureLogger();
        await using var client = new Quonfig(Options(new[] { server.Urls[0] }, OnInitFailure.Wait, TimeSpan.FromSeconds(5), logger));

        var init = client.InitAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        init.IsCompleted.Should().BeFalse();
        logger.LogCount(MelLogLevel.Error, @"^\[quonfig\] init: SDK key rejected \(401\) by primary\. Check the key and environment\. onInitFailure=wait: retrying").Should().Be(1);

        ServeHealthy(server, generation: 2);
        (await Task.WhenAny(init, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(init);
        client.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task Readiness_DatadirClientIsReadyAtConstruction()
    {
        await using var client = new Quonfig(new QuonfigOptions
        {
            DatafileEnvelope = System.Text.Json.JsonSerializer.Deserialize<global::Quonfig.Sdk.Wire.ConfigEnvelope>(EnvelopeJson(1))!,
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
        });
        client.IsReady.Should().BeTrue();
        await client.WaitForReadyAsync();
    }

    [Fact]
    public async Task Healthy_BootLogsNothingAboveDebug()
    {
        using var server = WireMockServer.Start();
        ServeHealthy(server, generation: 4);
        var logger = new CaptureLogger();
        await using var client = new Quonfig(Options(new[] { server.Urls[0] }, OnInitFailure.Fail, TimeSpan.FromSeconds(5), logger));

        await client.InitAsync();
        client.IsReady.Should().BeTrue();
        logger.LogCount(MelLogLevel.Debug, @"^\[quonfig\] init: fetching config \(initTimeout=5s, onInitFailure=fail\)").Should().Be(1);
        logger.Lines.Where(l => l.Message.StartsWith("[quonfig] init", StringComparison.Ordinal))
            .Should().OnlyContain(l => l.Level == MelLogLevel.Debug);
    }
}
