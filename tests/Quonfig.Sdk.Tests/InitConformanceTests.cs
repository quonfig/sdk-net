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
/// qfg-9p4i.17.5: init-failure conformance decisions D1-D4 (2026-10-21).
/// D1 auth fast-fail needs every configured leg (any N) in {401,403,404,410}; D2 timeout iff the last
/// attempt hung; D3 InitTimeout &lt;= 0 runs one attempt then applies OnInitFailure; D4 no URL userinfo
/// in init logs or the init error (including its inner exceptions).
/// </summary>
public sealed class InitConformanceTests
{
    private const string SdkKey = "init-conformance-tests-secret-key";

    private static string EnvelopeJson(int generation) =>
        $"{{\"configs\":[],\"meta\":{{\"version\":\"gen-{generation}\",\"environment\":\"production\",\"generation\":{generation}}}}}";

    private static void ServeStatus(WireMockServer server, int status, TimeSpan? delay = null)
    {
        server.Reset();
        var response = Response.Create().WithStatusCode(status);
        if (delay is { } d) response = response.WithDelay(d);
        server.Given(Request.Create().UsingAnyMethod()).RespondWith(response);
    }

    private static void ServeHealthy(WireMockServer server, int generation, TimeSpan? delay = null)
    {
        server.Reset();
        var response = Response.Create()
            .WithStatusCode(200)
            .WithHeader("Content-Type", "application/json")
            .WithHeader("ETag", $"\"gen-{generation}\"")
            .WithBody(EnvelopeJson(generation));
        if (delay is { } d) response = response.WithDelay(d);
        server.Given(Request.Create().WithPath("/api/v2/configs").UsingGet()).RespondWith(response);
    }

    private static int ConfigRequests(WireMockServer server) =>
        server.LogEntries.Count(e => e.RequestMessage!.Path == "/api/v2/configs");

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

    // ---------------------------------------------------------------- D1

    [Fact]
    public async Task D1_Fail_EveryLegAnswers404Or410_RaisesUnauthorizedAtOnce()
    {
        using var leg0 = WireMockServer.Start();
        using var leg1 = WireMockServer.Start();
        ServeStatus(leg0, 404);
        ServeStatus(leg1, 410);
        await using var client = new Quonfig(Options(new[] { leg0.Urls[0], leg1.Urls[0] }, OnInitFailure.Fail, TimeSpan.FromSeconds(8)));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        sw.Stop();

        raised.Reason.Should().Be(QuonfigInitFailureReason.Unauthorized);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "an auth-class answer from every leg fails fast");
    }

    [Fact]
    public async Task D1_Fail_PrimaryAuthButSecondary503_IsNotUnauthorized()
    {
        using var leg0 = WireMockServer.Start();
        using var leg1 = WireMockServer.Start();
        ServeStatus(leg0, 401);
        ServeStatus(leg1, 503);
        await using var client = new Quonfig(Options(new[] { leg0.Urls[0], leg1.Urls[0] }, OnInitFailure.Fail, TimeSpan.FromMilliseconds(1500)));

        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        raised.Reason.Should().Be(QuonfigInitFailureReason.Unavailable, "never decide unauthorized from the primary alone");
    }

    [Fact]
    public async Task D1_Fail_ThreeLegsAllAuthRejected_RaisesUnauthorizedAtOnce()
    {
        using var leg0 = WireMockServer.Start();
        using var leg1 = WireMockServer.Start();
        using var leg2 = WireMockServer.Start();
        ServeStatus(leg0, 401);
        ServeStatus(leg1, 403);
        ServeStatus(leg2, 401);
        var logger = new CaptureLogger();
        await using var client = new Quonfig(Options(new[] { leg0.Urls[0], leg1.Urls[0], leg2.Urls[0] }, OnInitFailure.Fail, TimeSpan.FromSeconds(8), logger));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        sw.Stop();

        raised.Reason.Should().Be(QuonfigInitFailureReason.Unauthorized);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "the all-legs rule must be reachable with 3+ URLs");
        ConfigRequests(leg2).Should().BeGreaterThan(0, "the third leg is asked before deciding unauthorized");
        logger.LogCount(MelLogLevel.Error, @"^\[quonfig\] init FAILED: SDK key rejected \(401\) by all 3 delivery URLs\.").Should().Be(1);
    }

    [Fact]
    public async Task D1_ThreeLegs_FirstTwoAuthRejected_ThirdHealthy_Installs()
    {
        using var leg0 = WireMockServer.Start();
        using var leg1 = WireMockServer.Start();
        using var leg2 = WireMockServer.Start();
        ServeStatus(leg0, 401);
        ServeStatus(leg1, 401);
        ServeHealthy(leg2, generation: 5);
        await using var client = new Quonfig(Options(new[] { leg0.Urls[0], leg1.Urls[0], leg2.Urls[0] }, OnInitFailure.Fail, TimeSpan.FromSeconds(8)));

        await client.InitAsync();
        client.IsReady.Should().BeTrue();
        client.HeldGeneration.Should().Be(5);
    }

    // ---------------------------------------------------------------- install, then the deadline cuts a late leg

    [Theory]
    [InlineData(OnInitFailure.Fail)]
    [InlineData(OnInitFailure.Continue)]
    public async Task InstalledAttempt_DeadlineCutsLateLeg_IsNotAnInitFailure(OnInitFailure mode)
    {
        // The primary installs at ~400ms; the hedge already fired the secondary, which hangs past the
        // 1s init deadline. The deadline cutting the still-draining secondary must not turn a
        // successful init into an init failure (no "init FAILED" / "STARTING ON CODE DEFAULTS" line).
        using var leg0 = WireMockServer.Start();
        using var leg1 = WireMockServer.Start();
        ServeHealthy(leg0, generation: 3, delay: TimeSpan.FromMilliseconds(400));
        ServeHealthy(leg1, generation: 3, delay: TimeSpan.FromSeconds(5));
        var logger = new CaptureLogger();
        var opts = Options(new[] { leg0.Urls[0], leg1.Urls[0] }, mode, TimeSpan.FromSeconds(1), logger);
        opts.ConfigFetchHedgeDelay = TimeSpan.FromMilliseconds(100);
        await using var client = new Quonfig(opts);

        await client.InitAsync();
        client.IsReady.Should().BeTrue();
        await Task.Delay(TimeSpan.FromMilliseconds(1500)); // past the deadline

        logger.Lines.Should().NotContain(l => l.Message.Contains("init FAILED"));
        logger.Lines.Should().NotContain(l => l.Message.Contains("STARTING ON CODE DEFAULTS"));
        logger.Lines.Should().NotContain(l => l.Message.Contains("no config after"));
        client.HeldGeneration.Should().Be(3);
    }

    // ---------------------------------------------------------------- D3

    [Theory]
    [InlineData(0)]
    [InlineData(-5000)]
    public async Task D3_Fail_NonPositiveInitTimeout_RunsOneAttemptThenRaises(int initTimeoutMs)
    {
        using var server = WireMockServer.Start();
        ServeStatus(server, 503);
        await using var client = new Quonfig(Options(new[] { server.Urls[0] }, OnInitFailure.Fail, TimeSpan.FromMilliseconds(initTimeoutMs)));

        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());

        raised.Reason.Should().Be(QuonfigInitFailureReason.Unavailable, "the one attempt answered 503");
        raised.Message.Should().NotContain("must be greater", "the option must not crash the transport");
        ConfigRequests(server).Should().Be(1, "exactly one attempt");
    }

    [Fact]
    public async Task D3_Fail_InfiniteInitTimeout_RunsOneAttemptThenRaises()
    {
        using var server = WireMockServer.Start();
        ServeStatus(server, 503);
        await using var client = new Quonfig(Options(new[] { server.Urls[0] }, OnInitFailure.Fail, Timeout.InfiniteTimeSpan));

        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        raised.Reason.Should().Be(QuonfigInitFailureReason.Unavailable);
        ConfigRequests(server).Should().Be(1);
    }

    [Fact]
    public async Task D3_Continue_ZeroInitTimeout_StartsOnDefaultsAfterOneAttempt()
    {
        using var server = WireMockServer.Start();
        ServeStatus(server, 503);
        await using var client = new Quonfig(Options(new[] { server.Urls[0] }, OnInitFailure.Continue, TimeSpan.Zero));

        await client.InitAsync();
        client.IsReady.Should().BeFalse();
        client.GetString("any", defaultValue: "D").Should().Be("D");
        ConfigRequests(server).Should().BeGreaterThan(0, "one attempt runs before continuing");
    }

    [Fact]
    public async Task D3_Wait_ZeroInitTimeout_KeepsRetryingUntilConfigArrives()
    {
        using var server = WireMockServer.Start();
        ServeStatus(server, 503);
        await using var client = new Quonfig(Options(new[] { server.Urls[0] }, OnInitFailure.Wait, TimeSpan.Zero));

        var init = client.InitAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        init.IsCompleted.Should().BeFalse("wait keeps blocking");

        ServeHealthy(server, generation: 9);
        (await Task.WhenAny(init, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(init);
        await init;
        client.HeldGeneration.Should().Be(9);
    }

    [Fact]
    public async Task D3_SmallInitTimeout_DoesNotCapLaterFetches()
    {
        // A slow but healthy delivery (800ms) must heal a wait-mode client whose InitTimeout is 300ms:
        // the init budget must not become the per-request ceiling of the shared transport.
        using var server = WireMockServer.Start();
        ServeHealthy(server, generation: 4, delay: TimeSpan.FromMilliseconds(800));
        await using var client = new Quonfig(Options(new[] { server.Urls[0] }, OnInitFailure.Wait, TimeSpan.FromMilliseconds(300)));

        var init = client.InitAsync();
        (await Task.WhenAny(init, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(init, "the 800ms answer lands");
        await init;
        client.HeldGeneration.Should().Be(4);
    }

    // ---------------------------------------------------------------- D4 + timeout message

    [Fact]
    public async Task D4_UserinfoNeverAppearsInInitLogsOrError_BadBody()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().UsingAnyMethod())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("not json at all"));
        var port = new Uri(server.Urls[0]).Port;
        var logger = new CaptureLogger();
        await using var client = new Quonfig(Options(new[] { $"http://alice:hunter2pw@127.0.0.1:{port}" }, OnInitFailure.Fail, TimeSpan.FromMilliseconds(800), logger));

        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());

        raised.ToString().Should().NotContain("hunter2pw").And.NotContain("alice:");
        logger.Lines.Should().NotContain(l => l.Message.Contains("hunter2pw"));
        logger.Lines.Should().Contain(l => l.Message.Contains($"127.0.0.1:{port}"), "hosts are fine");
    }

    [Fact]
    public async Task D4_UserinfoNeverAppearsInInitError_Refused()
    {
        var logger = new CaptureLogger();
        await using var client = new Quonfig(Options(new[] { "http://alice:hunter2pw@127.0.0.1:1" }, OnInitFailure.Fail, TimeSpan.FromMilliseconds(500), logger));

        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());

        raised.ToString().Should().NotContain("hunter2pw");
        logger.Lines.Should().NotContain(l => l.Message.Contains("hunter2pw"));
    }

    [Fact]
    public async Task TimeoutException_MessageNamesTheFailingHosts()
    {
        using var server = WireMockServer.Start();
        ServeStatus(server, 200, delay: TimeSpan.FromSeconds(5));
        var host = new Uri(server.Urls[0]).Authority;
        await using var client = new Quonfig(Options(new[] { server.Urls[0] }, OnInitFailure.Fail, TimeSpan.FromMilliseconds(400)));

        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());

        raised.Should().BeOfType<QuonfigInitTimeoutException>();
        raised.Message.Should().Contain(host).And.Contain("timed out");
    }
}
