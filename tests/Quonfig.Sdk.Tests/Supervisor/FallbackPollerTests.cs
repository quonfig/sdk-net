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
    // --- Timing scaffolding (qfg-7gri) ---------------------------------------
    //
    // Every absolute deadline in this class has to be a generous MULTIPLE of the
    // poller interval/threshold it races, the same "decisive margin" rule the hedge
    // de-flakes landed (b8f5f79, efbbb69). The poller under test arms on the
    // order of milliseconds, but the work that has to happen for the assertion
    // to become true — the Supervisor's Task.Run worker being scheduled at all —
    // is at the mercy of the runner's ThreadPool. On windows/net48 the .NET
    // Framework pool grows only ~1-2 threads/sec past minThreads, so once the
    // suite got heavier (WireMock-backed hedge tests) a worker could sit
    // unscheduled for hundreds of ms and blow a 500ms budget: run 28954129116
    // failed here with "waitFor timed out: poller never engaged".

    /// <summary>
    /// Budget for every <see cref="WaitForAsync"/> gate in this class. This is a
    /// TIMEOUT, not a sleep — WaitForAsync returns the instant its predicate is
    /// true, so a generous value costs a healthy runner nothing — none of these
    /// gates takes more than a few ms when the pool is idle — and only buys
    /// headroom on a starved one. The per-call 500ms/1s budgets it replaces were
    /// the thing that flaked.
    /// </summary>
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Quiet window for the "and then nothing happened" assertions. A longer
    /// window only makes those assertions stronger, so this is safe to be
    /// generous with.
    /// </summary>
    private static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Threshold for <see cref="ReconnectBeforeThresholdCancelsEngagement"/>,
    /// paired with <see cref="ReconnectBefore"/>. That test is the one place a
    /// wall-clock sleep is load-bearing rather than a mere timeout: it has to
    /// reconnect while the armed threshold timer is still pending. The old
    /// pairing was a 100ms threshold against a bare Task.Delay(20) — an 80ms
    /// margin that one starved scheduler slip eats, at which point the poller
    /// legitimately engages and the test fails on a timer that really did fire.
    /// 2s vs 50ms is a 40x ratio (~1.95s of absolute slack), beating the 10x /
    /// ~1.8s margin efbbb69 established as jitter-proof.
    /// </summary>
    private static readonly TimeSpan CancelThreshold = TimeSpan.FromSeconds(2);

    /// <summary>Disconnected dwell before the cancelling reconnect. Long enough
    /// that the worker has observed the disconnect and armed (so the test isn't
    /// vacuous), tiny next to <see cref="CancelThreshold"/>.</summary>
    private static readonly TimeSpan ReconnectBefore = TimeSpan.FromMilliseconds(50);

    private static async Task WaitForAsync(TimeSpan timeout, Func<bool> predicate, string msg)
    {
        var start = DateTime.UtcNow;
        var deadline = start + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(2);
        }
        throw new Xunit.Sdk.XunitException(
            $"waitFor timed out after {(DateTime.UtcNow - start).TotalMilliseconds:F0}ms: {msg}");
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
            await Task.Delay(SettleWindow);
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
            await WaitForAsync(WaitBudget,
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
            threshold: CancelThreshold,
            fetch: _ => { Interlocked.Increment(ref fetches); return Task.CompletedTask; });
        var s = Supervise(p);
        try
        {
            p.SetSseConnected(false);
            // 50ms of disconnected dwell — 40x inside the 2s threshold, so no
            // amount of scheduler slip can push the reconnect past the timer
            // (qfg-7gri; the old 20ms-vs-100ms pairing left only 80ms).
            await Task.Delay(ReconnectBefore);
            p.SetSseConnected(true);
            // Sleep past the point the original (now-cancelled) timer would have
            // fired — the threshold runs from the DISCONNECT, so wait the whole
            // threshold again plus a quiet window.
            await Task.Delay(CancelThreshold + SettleWindow);
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
            await WaitForAsync(WaitBudget, () => p.Active, "poller never engaged");
            p.SetSseConnected(true);
            // Wait on the disengage callback rather than `!p.Active`. The worker
            // flips `_engaged = false` inside the lock and fires `_onDisengage`
            // after the lock is released; on a fast runner the assertion can
            // observe the new Active state before the callback runs. The contract
            // under test is the callback, so we gate on it directly. Active is
            // separately re-checked below.
            await WaitForAsync(WaitBudget,
                () => Volatile.Read(ref disengageCount) >= 1,
                "disengage callback never fired after reconnect");
            disengageCount.Should().Be(1, "expected exactly 1 disengage callback");
            p.Active.Should().BeFalse("poller should be inactive once disengage fired");
            // Anchor the "fetches stopped" window at the DISENGAGE, not at the
            // engage (qfg-7gri). The contract under test is that no fetch happens
            // AFTER disengage; how many 5ms ticks land between engage and
            // reconnect is just a function of how promptly the test thread was
            // scheduled, and on a starved runner that count is unbounded — an
            // engage-anchored budget flakes for a reason the poller isn't
            // responsible for.
            int atDisengage = Volatile.Read(ref fetches);
            await Task.Delay(SettleWindow);
            // Allow one in-flight tick to race with disengage.
            Volatile.Read(ref fetches).Should().BeLessThanOrEqualTo(atDisengage + 1,
                $"fetches kept growing after disengage: had {atDisengage}, now {fetches}");
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
            await WaitForAsync(WaitBudget,
                () => Volatile.Read(ref fetches) >= 3,
                "poller stopped fetching after error");
        }
        finally
        {
            await s.StopAsync();
        }
    }

    // --- Lost-edge regressions (qfg-vov2) ------------------------------------
    //
    // RunAsync decides its wait duration while holding `_lock`, then RELEASES the
    // lock, runs that tick's side effects (onEngage / onDisengage / fetch), and
    // only then enters DelayWithPulseAsync. Before qfg-vov2 that method sampled
    // `_sseConnected` a SECOND time, under its own lock acquisition, to establish
    // the baseline it watches for an edge — so a SetSseConnected() that landed in
    // the gap was already folded into the baseline and could never register as a
    // change. The worker then slept the FULL decided duration: one hour on the
    // connected branch (FallbackPoller.cs:155), one interval on the engaged one.
    //
    // Those side-effect callbacks run on the worker thread from inside that exact
    // gap, which makes them a deterministic injection point for the interleaving —
    // no fake clock, no sleeps, no racing the scheduler for the window.

    // Test 6 — A disconnect delivered inside the disengage window must still engage.
    // This is the severe direction: the wait the worker had just decided on is the
    // connected branch's one hour, so a lost edge here idles Layer 2 for up to an
    // hour during the very SSE outage it exists to cover.
    [Fact]
    public async Task DisconnectDuringDisengageWindowStillEngages()
    {
        int engageCount = 0;
        int disengageCount = 0;
        FallbackPoller p = null!;
        p = new FallbackPoller(
            interval: TimeSpan.FromMilliseconds(10),
            threshold: TimeSpan.FromMilliseconds(20),
            fetch: _ => Task.CompletedTask,
            onEngage: () => Interlocked.Increment(ref engageCount),
            onDisengage: () =>
            {
                // Inside the window. Only inject on the first disengage so the
                // test drives exactly one interleaving.
                if (Interlocked.Increment(ref disengageCount) == 1)
                {
                    p.SetSseConnected(false);
                }
            });
        var s = Supervise(p);
        try
        {
            p.SetSseConnected(false);
            await WaitForAsync(WaitBudget,
                () => Volatile.Read(ref engageCount) >= 1,
                "poller never engaged the first time");

            // Reconnect: the worker takes the disengage branch, decides on the
            // connected branch's 1h wait, and fires onDisengage — which drops SSE
            // again before the worker starts waiting.
            p.SetSseConnected(true);
            await WaitForAsync(WaitBudget,
                () => Volatile.Read(ref disengageCount) >= 1,
                "disengage callback never fired after reconnect");
            await WaitForAsync(WaitBudget,
                () => Volatile.Read(ref engageCount) >= 2,
                "poller never re-engaged — the disconnect delivered inside the disengage window was lost, so the worker sat on its 1h connected-branch wait (qfg-vov2)");
            p.Active.Should().BeTrue("poller should be engaged again after the injected disconnect");
        }
        finally
        {
            await s.StopAsync();
        }
    }

    // Test 7 — A reconnect delivered inside the fetch window must still disengage.
    // Symmetric direction: the decided wait is one INTERVAL, so a lost edge here
    // keeps Layer 2 polling a healthy stream for a full interval. The interval is
    // 30s — 3x WaitBudget — so a lost edge cannot pass by being merely slow.
    [Fact]
    public async Task ReconnectDuringFetchWindowStillDisengages()
    {
        int fetches = 0;
        int disengageCount = 0;
        FallbackPoller p = null!;
        p = new FallbackPoller(
            interval: TimeSpan.FromSeconds(30),
            threshold: TimeSpan.FromMilliseconds(20),
            fetch: _ =>
            {
                // Inside the window: the engaged tick's fetch runs after the lock
                // is released and before the wait's baseline is taken.
                if (Interlocked.Increment(ref fetches) == 1)
                {
                    p.SetSseConnected(true);
                }
                return Task.CompletedTask;
            },
            onDisengage: () => Interlocked.Increment(ref disengageCount));
        var s = Supervise(p);
        try
        {
            p.SetSseConnected(false);
            await WaitForAsync(WaitBudget,
                () => Volatile.Read(ref disengageCount) >= 1,
                "poller never disengaged — the reconnect delivered inside the fetch window was lost, so the worker sat on its full 30s interval (qfg-vov2)");
            p.Active.Should().BeFalse("poller should be idle once the reconnect is observed");
        }
        finally
        {
            await s.StopAsync();
        }
    }
}
