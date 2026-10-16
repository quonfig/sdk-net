using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Supervisor;
using Xunit;
using static Quonfig.Sdk.Tests.Supervisor.SupervisorAuthRig;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Quonfig.Sdk.Tests.Supervisor;

// ---------------------------------------------------------------------------------------------
// Test 8 — Auth-class response (contract v2026.10.16, plan
// project/plans/2026-10-07-sdk-auth-failure-rule.md R1-R6, qfg-goi1.10.28). Rig:
// SupervisorAuthRig.cs. Every sub-test runs over 401/403/404/410.
//
// sdk-net has no polling mode: without stream URLs there is no background refresh at all
// (Quonfig.LogStartupMode, mode=no-background-refresh), so the contract's polling-mode clause
// (wait = max(T, poll interval)) has nothing to apply to here.
// ---------------------------------------------------------------------------------------------
public sealed partial class SupervisorTests
{
    private static readonly TimeSpan ProbeMin = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan ProbeMax = TimeSpan.FromSeconds(300);

    public static IEnumerable<object[]> AuthStatusData => AuthStatuses.Select(s => new object[] { s });

    private static string S(int status) => status.ToString(CultureInfo.InvariantCulture);

    /// <summary>The engage wait sdk-net requests is the time left of the 120s threshold.</summary>
    private static void AssertSingleEngageWait(List<TimeSpan> engage, string label)
    {
        engage.Should().HaveCount(1, $"{label}: one engage wait on the existing 120s rule");
        engage[0].Should().BeCloseTo(FallbackPoller.DefaultThreshold, TimeSpan.FromSeconds(1), label);
    }

    // Test 8 (a)-(c): SSE gets auth-class, then the fallback poller's cycle gets auth-class on
    // every leg -> auth-failed: 6 probes each requested at a wait in [150s, 300s], SSE and the
    // poller off, disconnected, one ERROR, no WARN.
    [Theory]
    [MemberData(nameof(AuthStatusData))]
    public async Task AuthClassEntersAuthFailedAndProbesSlowly(int status)
    {
        await using var r = new SupervisorAuthRig { SseStatus = status };
        r.Leg = (leg, cycle) => cycle == 1 ? new Resp(200, 1) : new Resp(status);
        await r.StartAsync(2);

        // Entering cycle (2) + 6 probes (3..8), then the 7th probe wait.
        var ev = await r.AwaitAsync("6 probes", e => r.Leg0Calls >= 8 && Count(e, "wait:probe") >= 7);

        int enter = EnteringCycle(ev);
        var pre = Slice(ev, 0, enter);
        // (a) before the entering cycle: SSE on its normal ladder, the poller engaged on its
        // existing 120s rule, no other fetch cycle.
        Count(pre, "hit:sse").Should().BeGreaterThanOrEqualTo(3, Dump(ev));
        CheckSseLadder(Waits(pre, "sse")).Should().BeEmpty("pre-entry SSE sleeps follow the normal ladder");
        AssertSingleEngageWait(Waits(pre, "engage"), "pre-entry");
        Count(Slice(ev, Index(ev, 0, "hit:sse"), enter), "hit:1").Should().Be(0,
            "no fetch request between SSE start and the entering cycle");

        int errIdx = LogIndex(ev, enter, MelLogLevel.Error);
        errIdx.Should().BeGreaterThan(0, "an ERROR is logged by the entering cycle;\n" + Dump(ev));
        var post = Slice(ev, errIdx, -1);

        // (a) the next 6 probes (and the 7th wait): requested at waits in [150s, 300s].
        var probes = Waits(post, "probe");
        probes.Count.Should().BeGreaterThanOrEqualTo(7);
        foreach (var w in probes.Take(7))
        {
            w.Should().BeGreaterThanOrEqualTo(ProbeMin).And.BeLessThanOrEqualTo(ProbeMax);
        }

        // (b) while auth-failed: no SSE attempt, the poller never engages or ticks, each probe
        // is one cycle across both legs, disconnected throughout.
        int last = Index(ev, errIdx, "wait:probe", 7);
        var window = Slice(ev, errIdx, last);
        foreach (var k in new[] { "hit:sse", "wait:sse", "wait:engage", "wait:poll" })
        {
            Count(window, k).Should().Be(0, $"no {k} while auth-failed");
        }
        for (int p = 1; p <= 6; p++)
        {
            var seg = Slice(ev, Index(ev, errIdx, "wait:probe", p), Index(ev, errIdx, "wait:probe", p + 1));
            Count(seg, "hit:0").Should().Be(1, $"probe {p} asks the primary once");
            Count(seg, "hit:1").Should().Be(1, $"probe {p} asks the secondary once");
        }
        foreach (var e in window.Where(e => e.Kind == "hit:0" || e.Kind == "hit:1"))
        {
            e.State.Should().Be(ConnectionState.Disconnected, "ConnectionState during a probe");
            e.Active.Should().BeFalse("the fallback poller is not engaged during a probe");
        }

        // (c) from entry (the log capture marks it with the ERROR) through the 6 probes: one
        // ERROR naming the status (never the key), no other ERROR in the whole run, no WARN.
        var counted = Slice(ev, errIdx, last);
        Logs(Slice(ev, 0, last), MelLogLevel.Error).Should().ContainSingle().Which.Should().Contain(S(status));
        Logs(counted, MelLogLevel.Warning).Should().BeEmpty("no WARN from entry through the probes");
        Logs(counted, MelLogLevel.Debug).Should().HaveCountGreaterThanOrEqualTo(6, "each rejected probe logs DEBUG");
        ev.Where(e => e.Kind == "log" && HasText(e.Message, Key)).Should().BeEmpty("logs never carry the SDK key");
    }

    // Test 8 (d): a probe that gets a valid 200 on the secondary leg only, or a 304 on any leg,
    // exits: install, one INFO, SSE restarts from its base backoff, the poller returns to its
    // normal schedule.
    public static IEnumerable<object[]> ExitData =>
        from s in AuthStatuses
        from c in new[] { "secondary-200", "primary-304", "secondary-304" }
        select new object[] { s, c };

    [Theory]
    [MemberData(nameof(ExitData))]
    public async Task AuthFailedExitsOnFirstValidAnswer(int status, string name)
    {
        var (primary, second, wantGen) = name switch
        {
            "secondary-200" => (new Resp(status), new Resp(200, 2, exit: true), 2),
            "primary-304" => (new Resp(304, exit: true), new Resp(status), 1),
            _ => (new Resp(status), new Resp(304, exit: true), 1),
        };
        await using var r = new SupervisorAuthRig { SseStatus = status, SseAfter = 503 };
        // cycle 2 enters, cycle 3 is a rejected probe, cycle 4 exits.
        r.Leg = (leg, cycle) => cycle switch
        {
            1 => new Resp(200, 1),
            4 => leg == 0 ? primary : second,
            _ => new Resp(status),
        };
        await r.StartAsync(2);
        var c = r.Client;
        var ev = await r.AwaitAsync("exit and an SSE restart", e =>
        {
            int i = Index(e, 0, "wait:probe", 2);
            if (!r.Exited || i < 0) return false;
            int h = Index(e, i, "hit:sse");
            return h >= 0 && Index(e, h, "wait:sse") >= 0 && Index(e, i, "wait:engage") >= 0;
        });

        c.HeldGeneration.Should().Be(wantGen);
        int enter = EnteringCycle(ev);
        int errIdx = LogIndex(ev, enter, MelLogLevel.Error);
        int exitIdx = LogIndex(ev, errIdx, MelLogLevel.Information);
        if (errIdx < 0 || exitIdx < 0)
        {
            throw new Xunit.Sdk.XunitException("want an entry ERROR then an exit INFO; events:\n" + Dump(ev));
        }
        int restart = Index(ev, exitIdx, "hit:sse");
        Count(Slice(ev, errIdx, exitIdx), "hit:sse").Should().Be(0, "no SSE attempt while auth-failed");
        Count(Slice(ev, errIdx, exitIdx), "wait:probe").Should().Be(2, "probe waits for cycles 3 and 4");
        Logs(Slice(ev, enter, restart), MelLogLevel.Information).Should().HaveCount(1, "one INFO on exit");
        Logs(Slice(ev, enter, restart), MelLogLevel.Error).Should().HaveCount(1, "one ERROR on entry");

        // SSE restarts from its normal base: the next ordinary SSE failure requests the first
        // backoff of a fresh client.
        var afterExit = Waits(Slice(ev, restart, -1), "sse");
        afterExit.Should().NotBeEmpty("an SSE sleep after the restart");
        CheckSseLadder(afterExit.Take(1).ToList()).Should().BeEmpty("post-exit SSE starts at its base backoff");
        // The poller is back on its normal rule (engage after 120s).
        AssertSingleEngageWait(Waits(Slice(ev, exitIdx, -1), "engage"), "post-exit");
        Count(Slice(ev, exitIdx, -1), "wait:probe").Should().Be(0, "no probe after exit");
        if (c.ConnectionState == ConnectionState.FallingBack)
        {
            c.FallbackPollerActive.Should().BeTrue("falling_back only while the poller is engaged");
        }
    }

    /// <summary>
    /// A run that never entered auth-failed: no ERROR, no probe, the SSE ladder continues across
    /// the poller's cycles, and the poller keeps its normal interval.
    /// </summary>
    private static async Task CheckNormalRegimeAsync(SupervisorAuthRig r)
    {
        var ev = await r.AwaitAsync("3 poller cycles", _ => r.Leg0Calls >= 4);
        Logs(ev, MelLogLevel.Error).Should().BeEmpty("never auth-failed;\n" + Dump(ev));
        Count(ev, "wait:probe").Should().Be(0, "never auth-failed");
        CheckSseLadder(Waits(ev, "sse")).Should().BeEmpty("SSE stays on the normal ladder");
        int firstPoll = EnteringCycle(ev);
        // Let the ladder carry on past the poller's cycle before asserting it did.
        ev = await r.AwaitAsync("an SSE attempt after the first poll", e => Index(e, firstPoll, "hit:sse") >= 0);
        var polls = Waits(ev, "poll");
        polls.Should().NotBeEmpty();
        polls.Should().OnlyContain(p => p == FallbackPoller.DefaultInterval, "the poller keeps its normal interval");
        foreach (var e in Slice(ev, firstPoll, -1).Where(e => e.Kind == "hit:0"))
        {
            e.State.Should().Be(ConnectionState.FallingBack, "ConnectionState during a poll");
        }
    }

    // Test 8 (e): SSE auth-class, then a poller cycle with a 200 on any leg: no auth-failed, no
    // ERROR, SSE and the poller stay on their normal schedule.
    public static IEnumerable<object[]> TransientData =>
        from s in AuthStatuses
        from c in new[] { "secondary-200", "primary-200" }
        select new object[] { s, c };

    [Theory]
    [MemberData(nameof(TransientData))]
    public async Task TransientPrimaryRejectionStaysNormal(int status, string name)
    {
        await using var r = new SupervisorAuthRig { SseStatus = status };
        r.Leg = (leg, cycle) =>
            cycle == 1 || name == "primary-200" || leg == 1 ? new Resp(200, 1) : new Resp(status);
        await r.StartAsync(2);
        await CheckNormalRegimeAsync(r);
    }

    // Test 8 (f): primary auth-class + secondary 503 or timeout enters; primary 503 + secondary
    // auth-class does not.
    public static IEnumerable<object[]> MixedEnterData =>
        from s in AuthStatuses
        from second in new[] { "503", "timeout" }
        select new object[] { s, second };

    [Theory]
    [MemberData(nameof(MixedEnterData))]
    public async Task MixedLegsPrimaryAuthEnters(int status, string second)
    {
        await using var r = new SupervisorAuthRig { SseStatus = status };
        r.Leg = (leg, cycle) =>
        {
            if (cycle == 1) return new Resp(200, 1);
            if (leg == 0) return new Resp(status);
            return second == "timeout"
                // A 30s hang against a 100ms per-leg abort: a decisive margin even on a starved
                // windows/net48 runner (a 400ms hang let the 200 land there once).
                ? new Resp(200, 9, delay: TimeSpan.FromSeconds(30))
                : new Resp(503);
        };
        await r.StartAsync(2, o => o.ConfigFetchHedgeAbort = TimeSpan.FromMilliseconds(100));
        var ev = await r.AwaitAsync("a probe after entry", e => Count(e, "wait:probe") >= 2);
        Logs(ev, MelLogLevel.Error).Should().ContainSingle().Which.Should().Contain(S(status));
        foreach (var w in Waits(ev, "probe"))
        {
            w.Should().BeGreaterThanOrEqualTo(ProbeMin).And.BeLessThanOrEqualTo(ProbeMax);
        }
        r.Client.HeldGeneration.Should().Be(1, "the timed-out secondary never installed");
    }

    [Theory]
    [MemberData(nameof(AuthStatusData))]
    public async Task MixedLegsPrimary503SecondaryAuthStaysNormal(int status)
    {
        await using var r = new SupervisorAuthRig { SseStatus = status };
        r.Leg = (leg, cycle) =>
            cycle == 1 ? new Resp(200, 1) : leg == 0 ? new Resp(503) : new Resp(status);
        await r.StartAsync(2);
        await CheckNormalRegimeAsync(r);
    }

    // Test 8 (g): a 503 on every leg (and on SSE) stays on the normal ladder and never enters
    // auth-failed.
    [Fact]
    public async Task NonAuthStatusStaysOnLadder()
    {
        await using var r = new SupervisorAuthRig { SseStatus = 503 };
        r.Leg = (leg, cycle) => cycle == 1 ? new Resp(200, 1) : new Resp(503);
        await r.StartAsync(2);
        await CheckNormalRegimeAsync(r);
    }

    // R2: a single-leg client's only leg is the primary.
    [Theory]
    [MemberData(nameof(AuthStatusData))]
    public async Task SingleLegAuthClassEnters(int status)
    {
        await using var r = new SupervisorAuthRig { SseStatus = status };
        r.Leg = (leg, cycle) => cycle == 1 ? new Resp(200, 1) : new Resp(status);
        await r.StartAsync(1);
        var ev = await r.AwaitAsync("two probes", e => Count(e, "wait:probe") >= 2);
        Logs(ev, MelLogLevel.Error).Should().ContainSingle().Which.Should().Contain(S(status));
        Count(ev, "hit:1").Should().Be(0);
    }
}
