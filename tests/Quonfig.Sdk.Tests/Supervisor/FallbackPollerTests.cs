using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Supervisor;
using Xunit;

namespace Quonfig.Sdk.Tests.Supervisor;

/// <summary>
/// Tier-1 fallback-poller contract tests — port of <c>FallbackPollerTest.java</c>
/// (qfg-47c2.21). Behavior: idle while SSE is connected; on disconnect arm a
/// threshold timer; if SSE reconnects before the threshold the timer is
/// cancelled; if the threshold elapses the poller engages and ticks at the
/// configured interval; on reconnect the poller disengages.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA2007",
    Justification = "Test code; ConfigureAwait(false) not required.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1031",
    Justification = "Tests assert on counters; broad catch is fine.")]
public sealed class FallbackPollerTests
{
    private static async Task WaitForAsync(TimeSpan timeout, Func<bool> predicate, string msg)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(2);
        }
        throw new Xunit.Sdk.XunitException("waitFor timed out: " + msg);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2000",
        Justification = "Caller assumes ownership; tests await s.StopAsync() before exiting.")]
    private static global::Quonfig.Sdk.Supervisor.Supervisor Supervise(FallbackPoller p)
    {
        var s = new global::Quonfig.Sdk.Supervisor.Supervisor(
            initialDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(5),
            workers: new[] { new WorkerSpec("2", p.Worker) });
        s.Start();
        return s;
    }

    // Test 1 — Poller stays idle while SSE is connected.
    [Fact]
    public async Task IdleWhileConnected()
    {
        int fetches = 0;
        var p = new FallbackPoller(
            interval: TimeSpan.FromMilliseconds(10),
            threshold: TimeSpan.FromMilliseconds(10),
            fetch: _ => { Interlocked.Increment(ref fetches); return Task.CompletedTask; });
        var s = Supervise(p);
        try
        {
            p.SetSseConnected(true);
            await Task.Delay(60);
            fetches.Should().Be(0, "expected 0 fetches while connected");
            p.Active.Should().BeFalse("expected active=false while connected");
        }
        finally
        {
            await s.StopAsync();
        }
    }

    // Test 2 — After threshold elapses while disconnected, poller engages and fetches.
    [Fact]
    public async Task EngagesAfterThreshold()
    {
        int fetches = 0;
        int engageCount = 0;
        var p = new FallbackPoller(
            interval: TimeSpan.FromMilliseconds(10),
            threshold: TimeSpan.FromMilliseconds(20),
            fetch: _ => { Interlocked.Increment(ref fetches); return Task.CompletedTask; },
            onEngage: () => Interlocked.Increment(ref engageCount));
        var s = Supervise(p);
        try
        {
            p.SetSseConnected(false);
            await WaitForAsync(TimeSpan.FromSeconds(1),
                () => Volatile.Read(ref fetches) >= 2,
                "poller never engaged and fetched after threshold");
            p.Active.Should().BeTrue("expected active=true while engaged");
            engageCount.Should().Be(1, "expected exactly 1 engage callback");
        }
        finally
        {
            await s.StopAsync();
        }
    }

    // Test 3 — Reconnect before threshold elapses cancels engagement.
    [Fact]
    public async Task ReconnectBeforeThresholdCancelsEngagement()
    {
        int fetches = 0;
        var p = new FallbackPoller(
            interval: TimeSpan.FromMilliseconds(5),
            threshold: TimeSpan.FromMilliseconds(100),
            fetch: _ => { Interlocked.Increment(ref fetches); return Task.CompletedTask; });
        var s = Supervise(p);
        try
        {
            p.SetSseConnected(false);
            await Task.Delay(20); // well below 100ms threshold
            p.SetSseConnected(true);
            await Task.Delay(150); // past original threshold
            fetches.Should().Be(0, "expected 0 fetches when reconnect beats threshold");
            p.Active.Should().BeFalse("poller should never have engaged");
        }
        finally
        {
            await s.StopAsync();
        }
    }

    // Test 4 — Reconnect after engagement disengages and stops fetches.
    [Fact]
    public async Task ReconnectAfterEngagementDisengages()
    {
        int fetches = 0;
        int disengageCount = 0;
        var p = new FallbackPoller(
            interval: TimeSpan.FromMilliseconds(5),
            threshold: TimeSpan.FromMilliseconds(5),
            fetch: _ => { Interlocked.Increment(ref fetches); return Task.CompletedTask; },
            onDisengage: () => Interlocked.Increment(ref disengageCount));
        var s = Supervise(p);
        try
        {
            p.SetSseConnected(false);
            await WaitForAsync(TimeSpan.FromMilliseconds(500), () => p.Active, "poller never engaged");
            int atEngage = Volatile.Read(ref fetches);
            p.SetSseConnected(true);
            // Wait on the disengage callback rather than `!p.Active`. The worker
            // flips `_engaged = false` inside the lock and fires `_onDisengage`
            // after the lock is released; on a fast runner the assertion can
            // observe the new Active state before the callback runs. The contract
            // under test is the callback, so we gate on it directly. Active is
            // separately re-checked below.
            await WaitForAsync(TimeSpan.FromMilliseconds(500),
                () => Volatile.Read(ref disengageCount) >= 1,
                "disengage callback never fired after reconnect");
            disengageCount.Should().Be(1, "expected exactly 1 disengage callback");
            p.Active.Should().BeFalse("poller should be inactive once disengage fired");
            await Task.Delay(50);
            // Allow one in-flight tick to race with disengage.
            Volatile.Read(ref fetches).Should().BeLessThanOrEqualTo(atEngage + 1,
                $"fetches kept growing after disengage: had {atEngage}, now {fetches}");
        }
        finally
        {
            await s.StopAsync();
        }
    }

    // Test 5 — Fetch exception must not crash the poller; ticks keep firing.
    [Fact]
    public async Task SurvivesFetchErrors()
    {
        int fetches = 0;
        var p = new FallbackPoller(
            interval: TimeSpan.FromMilliseconds(5),
            threshold: TimeSpan.FromMilliseconds(5),
            fetch: _ =>
            {
                Interlocked.Increment(ref fetches);
                throw new InvalidOperationException("simulated");
            });
        var s = Supervise(p);
        try
        {
            p.SetSseConnected(false);
            await WaitForAsync(TimeSpan.FromMilliseconds(500),
                () => Volatile.Read(ref fetches) >= 3,
                "poller stopped fetching after error");
        }
        finally
        {
            await s.StopAsync();
        }
    }
}
