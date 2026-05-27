using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Telemetry;
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
}
