using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Quonfig.Sdk;
using Xunit;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// qfg-6vno.4: every value the chaos grammar asserts on comes from the SDK, the probe never
/// reports a state the supervisor contract forbids, and an inject or process step the rig does not
/// implement fails the run instead of running as a no-op.
/// </summary>
public class ChaosProbeHonestyTests
{
    [Fact]
    public void Disconnected_IsReportedAsDisconnected_NotInventedReconnecting()
    {
        var (probe, fake) = FakeClientView.Probe();
        fake.State = Sdk.ConnectionState.Disconnected;
        Assert.Equal("disconnected", probe.ConnectionState());
        foreach (Sdk.ConnectionState st in Enum.GetValues(typeof(Sdk.ConnectionState)))
        {
            fake.State = st;
            Assert.Contains(probe.ConnectionState(), new[] { "initializing", "connected", "disconnected", "falling_back" });
        }
    }

    [Fact]
    public void SdkLog_MatchesExactlyTheRequestedLevel()
    {
        // ITD chaos README: sdkLog(level, re) counts lines at exactly that level.
        var probe = new ChaosProbe();
        probe.Log("warning", "quonfig: OnConfigChange handler threw: simulated user-callback panic");
        var r = new ExpressionEvaluator(probe).Evaluate("client.sdkLog('error', /callback/i) == 0");
        Assert.True(r.Passed, r.Reason);
    }

    [Fact]
    public void SdkLog_WithoutIFlag_ParsesAndIsCaseSensitive()
    {
        // f01/f04 (ITD v2026.10.21.1) write /re/ with no i flag.
        var probe = new ChaosProbe();
        probe.Log("warning", "[quonfig] init: config not loaded yet, retrying");
        var ev = new ExpressionEvaluator(probe);
        var r = ev.Evaluate("client.sdkLog('warn', /\\[quonfig\\] init/) == 1 AND client.sdkLog('error', /\\[quonfig\\] init/) == 0");
        Assert.True(r.Passed, r.Reason);
        Assert.True(ev.Evaluate("client.sdkLog('warn', /\\[QUONFIG\\] init/) == 0").Passed);
        Assert.True(ev.Evaluate("client.sdkLog('warn', /\\[QUONFIG\\] init/i) == 1").Passed);
    }

    [Fact]
    public void ProbeWithoutClient_FailsLoudly_InsteadOfReportingADefault()
    {
        var probe = new ChaosProbe();
        Assert.Throws<InvalidOperationException>(() => probe.ConnectionState());
        Assert.Throws<InvalidOperationException>(() => probe.SdkMetric("quonfig_sse_connect_attempts_total", null));
    }

    [Fact]
    public async Task FailedSseAttempts_AreVisibleToTheAttemptsAndRestartMetrics()
    {
        // The real 07 condition: every SSE attempt answers 200 headers and closes before a frame.
        // The storm guard (09: attempts < 100) and the restart rows must see these attempts,
        // though no state edge fires for them (first-frame rule).
        var probe = new ChaosProbe();
        await using var client = HeadersOnlyClient();
        probe.SetClient(client);
        await client.InitAsync();
        await Task.Delay(1500);
        var attempts = probe.SdkMetric("quonfig_sse_connect_attempts_total", null).Value;
        var restarts = probe.SdkMetric("quonfig_sdk_worker_restart_total", "1").Value;
        Assert.True(attempts >= 3, "attempts=" + attempts);
        Assert.True(restarts >= 3, "restarts=" + restarts);
        Assert.NotEqual("connected", probe.ConnectionState());
        var ev = new ExpressionEvaluator(probe);
        Assert.False(ev.Evaluate("client.sdkMetric('quonfig_sse_connect_attempts_total') < 3").Passed);
    }

    [Fact]
    public void InitState_ReadsTheInitTask()
    {
        var (probe, _) = FakeClientView.Probe();
        var ev = new ExpressionEvaluator(probe);
        var tcs = new TaskCompletionSource<bool>();
        probe.SetInitTask(tcs.Task);
        Assert.True(ev.Evaluate("client.initState() == 'pending'").Passed);
        tcs.SetException(new InvalidOperationException("boom"));
        Assert.True(ev.Evaluate("client.initState() == 'raised'").Passed);

        var (probe2, _) = FakeClientView.Probe();
        probe2.SetInitTask(Task.CompletedTask);
        Assert.True(new ExpressionEvaluator(probe2).Evaluate("client.initState() == 'returned'").Passed);
    }

    [Fact]
    public void ReadyEventCount_CountsOnlyEventsAfterInitReturned()
    {
        var (probe, fake) = FakeClientView.Probe();
        var tcs = new TaskCompletionSource<bool>();
        probe.SetInitTask(tcs.Task);
        probe.OnConfigChange();
        Assert.Equal(0, probe.ReadyEventCount());
        tcs.SetResult(true);
        probe.OnConfigChange();
        fake.Ready = true;
        fake.Values["k"] = "v";
        var r = new ExpressionEvaluator(probe).Evaluate(
            "client.ready() == true AND client.readyEventCount() >= 1 AND client.getString('k', 'CODE-DEFAULT') == 'v'");
        Assert.True(r.Passed, r.Reason);
        Assert.True(new ExpressionEvaluator(probe).Evaluate("client.getString('missing', 'CODE-DEFAULT') == 'CODE-DEFAULT'").Passed);
    }

    [Fact]
    public async Task UnknownInject_FailsInsteadOfNoOp()
    {
        using var tp = new ToxiproxyClient("http://127.0.0.1:1");
        var rig = new ChaosTests.InjectContext(tp, 1, "k");
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            ChaosTests.ApplyInjectAsync(rig, new ChaosScenario.Inject { Name = "x" }));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            ChaosTests.ApplyInjectAsync(rig, new ChaosScenario.Inject { Name = "x", SseHttpStatus = 401 }));
    }

    [Fact]
    public async Task UnknownProcessAction_FailsInsteadOfNoOp()
    {
        using var tp = new ToxiproxyClient("http://127.0.0.1:1");
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            ChaosTests.ApplyProcessAsync(tp, new ChaosScenario.Process { Action = "restart_sse_proxy", Count = 1 }, CancellationToken.None));
    }

    [Fact]
    public void Loader_RejectsUnknownInjectKey()
    {
        const string yaml = "function: x\ntests:\n  - name: t\n    setup: {}\n    chaos:\n      - at_ms: 0\n        inject:\n          name: a\n          sse_typo_ms: 5\n    expectations: []\n";
        var ex = Assert.Throws<InvalidDataException>(() => ChaosYamlLoader.LoadString(yaml));
        Assert.Contains("sse_typo_ms", ex.Message);
    }

    [Fact]
    public void Deviations_MatchTheirRowsExactly_AndStaleOnesFail()
    {
        const string row = "client.sdkLog('error', /callback|onConfigUpdate/i) >= 1";
        Assert.NotNull(ChaosDeviations.Find("10 callback throw — user code throws inside onConfigUpdate", row));
        Assert.Null(ChaosDeviations.Find("01 baseline", row));
        Assert.Throws<InvalidOperationException>(() => ChaosDeviations.AssertAllMatched(
            "10 callback throw", new[] { new ChaosScenario.Expectation { AssertExpr = "client.processStillAlive() == true" } }));
    }

    internal static Sdk.Quonfig HeadersOnlyClient() => new(new QuonfigOptions
    {
        SdkKey = "test-backend-key",
        ApiUrls = new[] { "http://127.0.0.1:1" },
        StreamUrls = new[] { "http://127.0.0.1:2" },
        InitTimeout = TimeSpan.FromMilliseconds(300),
        OnInitFailure = OnInitFailure.Continue,
        HttpMessageHandler = new HeadersOnlyHandler(),
        TestWaitHook = (_, d) => d > TimeSpan.FromMilliseconds(20) ? TimeSpan.FromMilliseconds(20) : d,
        CollectEvaluationSummaries = false,
        ContextUploadMode = ContextUploadMode.None,
    });

    /// <summary>Config fetch answers 503; every SSE attempt answers 200 headers and an empty body.</summary>
    internal sealed class HeadersOnlyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Port == 2)
            {
                var ok = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                };
                ok.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
                return Task.FromResult(ok);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("down", Encoding.UTF8),
            });
        }
    }
}
