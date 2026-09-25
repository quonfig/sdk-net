using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Telemetry;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests.Telemetry;

/// <summary>
/// Reporter-level coverage: backoff growth on failure, reset on success, empty no-op, and the
/// final flush on shutdown.
/// </summary>
public sealed class TelemetryReporterTests
{
    private sealed class CapturingSender : ITelemetrySender
    {
        public List<IDictionary<string, object?>> Sent { get; } = new();
        public Func<Task>? OnSend { get; set; }

        public async Task SendAsync(IDictionary<string, object?> payload, CancellationToken cancellationToken)
        {
            if (OnSend is not null) await OnSend().ConfigureAwait(false);
            Sent.Add(payload);
        }
    }

    private static (TelemetryReporter, CapturingSender, EvaluationSummaryCollector, ContextShapeCollector, ExampleContextCollector) MakeReporter(
        TimeSpan baseInterval, TimeSpan maxInterval, CapturingSender? sender = null)
    {
        sender ??= new CapturingSender();
        var summaries = new EvaluationSummaryCollector(enabled: true);
        var shapes = new ContextShapeCollector(ContextUploadMode.ShapesOnly);
        var examples = new ExampleContextCollector(ContextUploadMode.PeriodicExample);
        var reporter = new TelemetryReporter(
            sender, "instance-hash", summaries, shapes, examples,
            initialDelay: TimeSpan.Zero, baseInterval: baseInterval, maxInterval: maxInterval);
        return (reporter, sender, summaries, shapes, examples);
    }

    private static EvaluationStat OneStat() =>
        new("cfg-1", "feature.foo", "CONFIG", ruleIndex: 0, weightedValueIndex: -1,
            selectedValue: "value-a", reportableValue: null, reason: 1);

    [Fact]
    public async Task flush_with_no_pending_events_does_not_send()
    {
        var (reporter, sender, _, _, _) = MakeReporter(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));
        await using var r = reporter;

        await r.FlushAsync(CancellationToken.None);

        sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task flush_sends_envelope_with_instance_hash_and_events()
    {
        var (reporter, sender, summaries, _, _) = MakeReporter(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));
        await using var r = reporter;
        summaries.Push(OneStat());

        await r.FlushAsync(CancellationToken.None);

        sender.Sent.Should().HaveCount(1);
        sender.Sent[0]["instanceHash"].Should().Be("instance-hash");
        sender.Sent[0]["events"].Should().NotBeNull();
    }

    [Fact]
    public async Task flush_and_apply_backoff_grows_interval_on_sender_failure()
    {
        var sender = new CapturingSender { OnSend = () => throw new InvalidOperationException("boom") };
        var (reporter, _, summaries, _, _) = MakeReporter(
            baseInterval: TimeSpan.FromMilliseconds(100),
            maxInterval: TimeSpan.FromMinutes(10),
            sender: sender);
        await using var r = reporter;
        summaries.Push(OneStat());

        bool ok = await r.FlushAndApplyBackoffAsync(CancellationToken.None);

        ok.Should().BeFalse();
        r.CurrentInterval.Should().BeGreaterThan(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task flush_and_apply_backoff_resets_interval_on_success()
    {
        var sender = new CapturingSender { OnSend = () => throw new InvalidOperationException("boom") };
        var (reporter, _, summaries, _, _) = MakeReporter(
            baseInterval: TimeSpan.FromMilliseconds(100),
            maxInterval: TimeSpan.FromMinutes(10),
            sender: sender);
        await using var r = reporter;
        summaries.Push(OneStat());

        // First push fails — interval grows
        await r.FlushAndApplyBackoffAsync(CancellationToken.None);
        var grown = r.CurrentInterval;
        grown.Should().BeGreaterThan(TimeSpan.FromMilliseconds(100));

        // Second push: clear the throw and re-push so an envelope is built; should succeed and reset
        sender.OnSend = null;
        summaries.Push(OneStat());
        bool ok = await r.FlushAndApplyBackoffAsync(CancellationToken.None);

        ok.Should().BeTrue();
        r.CurrentInterval.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task flush_and_apply_backoff_caps_at_max_interval()
    {
        var sender = new CapturingSender { OnSend = () => throw new InvalidOperationException("boom") };
        var (reporter, _, summaries, _, _) = MakeReporter(
            baseInterval: TimeSpan.FromMilliseconds(100),
            maxInterval: TimeSpan.FromMilliseconds(300),
            sender: sender);
        await using var r = reporter;

        for (int i = 0; i < 20; i++)
        {
            summaries.Push(OneStat());
            await r.FlushAndApplyBackoffAsync(CancellationToken.None);
        }

        r.CurrentInterval.Should().Be(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task dispose_async_runs_final_flush()
    {
        var sender = new CapturingSender();
        var (reporter, _, summaries, _, _) = MakeReporter(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), sender);
        summaries.Push(OneStat());

        await reporter.DisposeAsync();

        sender.Sent.Should().HaveCount(1, "DisposeAsync must perform one final flush before exit");
        reporter.IsClosed.Should().BeTrue();
    }

    [Fact]
    public async Task start_then_dispose_drains_pending_events()
    {
        var sender = new CapturingSender();
        var (reporter, _, summaries, _, _) = MakeReporter(
            baseInterval: TimeSpan.FromSeconds(30),
            maxInterval: TimeSpan.FromMinutes(10),
            sender: sender);
        reporter.Start();
        reporter.Start(); // idempotent
        summaries.Push(OneStat());

        await reporter.DisposeAsync();

        sender.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task flush_emits_failover_event_with_exact_camelcase_fields()
    {
        var sender = new CapturingSender();
        var summaries = new EvaluationSummaryCollector(enabled: true);
        var shapes = new ContextShapeCollector(ContextUploadMode.ShapesOnly);
        var examples = new ExampleContextCollector(ContextUploadMode.PeriodicExample);
        var failover = new FailoverCollector();
        await using var reporter = new TelemetryReporter(
            sender, "instance-hash", summaries, shapes, examples,
            initialDelay: TimeSpan.Zero, baseInterval: TimeSpan.FromSeconds(30),
            maxInterval: TimeSpan.FromMinutes(10), failover: failover);

        failover.RecordHedgeFired();
        failover.RecordGuardRejected();
        failover.RecordResolvedFrom(0); // primary
        failover.RecordResolvedFrom(1); // secondary

        await reporter.FlushAsync(CancellationToken.None);

        sender.Sent.Should().HaveCount(1);
        // The failover event must ride the same envelope as the other collectors, with the exact
        // camelCase field names api-telemetry's Zod schema + ClickHouse MV parse (qfg-41nh.18).
        string json = System.Text.Json.JsonSerializer.Serialize(sender.Sent[0]);
        json.Should().Contain("\"failover\":");
        json.Should().Contain("\"hedgeFired\":1");
        json.Should().Contain("\"guardRejected\":1");
        json.Should().Contain("\"resolvedFromPrimary\":1");
        json.Should().Contain("\"resolvedFromSecondary\":1");
        json.Should().Contain("\"resolvedFromLkg\":0");
    }

    [Fact]
    public async Task flush_with_no_failover_activity_emits_no_failover_event()
    {
        var sender = new CapturingSender();
        var summaries = new EvaluationSummaryCollector(enabled: true);
        var shapes = new ContextShapeCollector(ContextUploadMode.ShapesOnly);
        var examples = new ExampleContextCollector(ContextUploadMode.PeriodicExample);
        var failover = new FailoverCollector();
        await using var reporter = new TelemetryReporter(
            sender, "instance-hash", summaries, shapes, examples,
            initialDelay: TimeSpan.Zero, baseInterval: TimeSpan.FromSeconds(30),
            maxInterval: TimeSpan.FromMinutes(10), failover: failover);

        // Only an eval summary is pending; a healthy client records no failover activity.
        summaries.Push(OneStat());

        await reporter.FlushAsync(CancellationToken.None);

        sender.Sent.Should().HaveCount(1);
        string json = System.Text.Json.JsonSerializer.Serialize(sender.Sent[0]);
        json.Should().NotContain("failover", "a healthy client must emit no failover event");
    }

    [Fact]
    public async Task flush_and_apply_backoff_treats_request_timeout_as_failure_not_shutdown()
    {
        // HttpClient.Timeout surfaces as TaskCanceledException while the caller's token is NOT
        // cancelled. That is a failed POST (back off, keep going), not a shutdown (qfg-y8je.1).
        var sender = new CapturingSender
        {
            OnSend = () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
        };
        var (reporter, _, summaries, _, _) = MakeReporter(
            baseInterval: TimeSpan.FromMilliseconds(100),
            maxInterval: TimeSpan.FromMinutes(10),
            sender: sender);
        await using var r = reporter;
        summaries.Push(OneStat());

        bool ok = await r.FlushAndApplyBackoffAsync(CancellationToken.None);

        ok.Should().BeFalse();
        r.CurrentInterval.Should().BeGreaterThan(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task flush_and_apply_backoff_propagates_cancellation_when_shutdown_token_cancelled()
    {
        var shutdown = new CancellationToken(canceled: true);
        var sender = new CapturingSender
        {
            OnSend = () => throw new OperationCanceledException(shutdown),
        };
        var (reporter, _, summaries, _, _) = MakeReporter(
            baseInterval: TimeSpan.FromMilliseconds(100),
            maxInterval: TimeSpan.FromMinutes(10),
            sender: sender);
        await using var r = reporter;
        summaries.Push(OneStat());

        Func<Task> act = () => r.FlushAndApplyBackoffAsync(shutdown);

        await act.Should().ThrowAsync<OperationCanceledException>();
        r.CurrentInterval.Should().Be(TimeSpan.FromMilliseconds(100), "shutdown is not a transport failure");
    }

    [Fact]
    public async Task loop_keeps_ticking_after_a_timed_out_post()
    {
        // Repro for qfg-y8je.1: one slow response (longer than the sender's HttpClient timeout)
        // used to exit the flush loop for the life of the process.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/v1/telemetry/").UsingPost())
            .InScenario("slow-then-fast")
            .WillSetStateTo("fast")
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(3)));
        server.Given(Request.Create().WithPath("/api/v1/telemetry/").UsingPost())
            .InScenario("slow-then-fast")
            .WhenStateIs("fast")
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sender = new HttpTelemetrySender(
            new Uri(server.Urls[0]), "test-sdk-key", TimeSpan.FromMilliseconds(250), messageHandler: null);
        var summaries = new EvaluationSummaryCollector(enabled: true);
        var shapes = new ContextShapeCollector(ContextUploadMode.ShapesOnly);
        var examples = new ExampleContextCollector(ContextUploadMode.PeriodicExample);
        await using var reporter = new TelemetryReporter(
            sender, "instance-hash", summaries, shapes, examples,
            initialDelay: TimeSpan.Zero, baseInterval: TimeSpan.FromMilliseconds(50),
            maxInterval: TimeSpan.FromMilliseconds(100));

        summaries.Push(OneStat());
        reporter.Start();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (server.LogEntries.Count() < 2 && DateTime.UtcNow < deadline)
        {
            summaries.Push(OneStat());
            await Task.Delay(50);
        }

        server.LogEntries.Count().Should().BeGreaterThanOrEqualTo(
            2, "the loop must survive a timed-out POST and keep posting on later ticks");
    }
}
