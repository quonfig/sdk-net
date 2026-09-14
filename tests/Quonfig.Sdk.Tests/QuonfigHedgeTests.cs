using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk;
using Quonfig.Sdk.Telemetry;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// Parallel-failover hedge unit tests (qfg-7h5d.1.14.8), mirroring sdk-go's
/// <c>quonfig_hedge_test.go</c>. These pin the behaviors the chaos ordering scenarios assert
/// (o01 cold-standby, o03 heal-forward, o05 secondary-newer-wins) at the unit level, where a
/// per-leg request counter can prove the "secondary is never contacted on a fast primary" contract
/// that the toxiproxy chaos rig (no server-side counter) cannot.
///
/// <para>They use only the public client + internal diagnostics and default hedge timings, so the
/// file also compiles + runs against the pre-hedge sequential transport to capture the RED baseline:
/// on the sequential path the primary is always tried first and wins, so the secondary's divergent
/// generation is never installed and the per-leg counter shows the secondary was never contacted —
/// the inverse of what the hedge must produce in o05/o03.</para>
/// </summary>
public sealed class QuonfigHedgeTests
{
    private const string SdkKey = "test-backend-key";

    private static string EnvelopeJson(int generation) =>
        $"{{\"configs\":[],\"meta\":{{\"version\":\"gen-{generation}\",\"environment\":\"production\",\"generation\":{generation}}}}}";

    /// <summary>
    /// Starts a WireMock upstream pinned to <paramref name="generation"/>, optionally delaying every
    /// response by <paramref name="delay"/>. WireMock counts hits internally; the count is read off
    /// <see cref="WireMockServer.LogEntries"/>.
    /// </summary>
    private static WireMockServer Upstream(int generation, TimeSpan delay)
    {
        var server = WireMockServer.Start();
        var response = Response.Create()
            .WithStatusCode(200)
            .WithHeader("Content-Type", "application/json")
            .WithHeader("ETag", $"\"gen-{generation}\"")
            .WithBody(EnvelopeJson(generation));
        if (delay > TimeSpan.Zero)
        {
            response = response.WithDelay(delay);
        }
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(response);
        return server;
    }

    private static int Hits(WireMockServer server)
    {
        int n = 0;
        foreach (var _ in server.LogEntries) n++;
        return n;
    }

    /// <param name="hedgeDelay">Overrides the parallel-hedge delay. Defaults to null → the SDK's
    /// production ~2s default (the fast-primary/cold-standby tests need the real default). A
    /// slow-primary test can pass a small value to make the primary delay a large MULTIPLE of the
    /// hedge delay, so runner scheduler jitter can never flip which fires first (qfg-y7o7).</param>
    private static Quonfig NewHedgeClient(WireMockServer primary, WireMockServer secondary,
        TimeSpan? hedgeDelay = null)
    {
        var opts = new QuonfigOptions
        {
            SdkKey = SdkKey,
            ApiUrls = new[] { primary.Urls[0], secondary.Urls[0] },
            // No SSE / no fallback poller: keep the only installs the init hedge + explicit
            // RefreshAsync calls, so the hedge behavior is observed deterministically.
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromSeconds(10),
            OnInitFailure = OnInitFailure.ReturnDefaults,
            OnNoDefault = OnNoDefault.Ignore,
            // Telemetry stays ENABLED (these tests drain client.Failover) but a no-op sender keeps it
            // off the network, and a far-out initial delay stops the reporter's background loop from
            // draining the failover collector out from under the test's own Drain(). (qfg-gxm6)
            TelemetrySender = new NoopSender(),
            TelemetryInitialDelay = TimeSpan.FromMinutes(5),
        };
        if (hedgeDelay is { } hd)
        {
            opts.ConfigFetchHedgeDelay = hd;
        }
        return new Quonfig(opts);
    }

    private sealed class NoopSender : ITelemetrySender
    {
        public Task SendAsync(IDictionary<string, object?> payload, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private static async Task PollUntilGenerationAsync(Quonfig client, int want, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (client.HeldGeneration == want) return;
            await Task.Delay(20);
        }
        client.HeldGeneration.Should().Be(want,
            $"held generation should reach {want} within {within}");
    }

    /// <summary>
    /// Unit-level o01: both legs healthy and fast, secondary newer. A fast primary answers well
    /// inside the hedge delay, so the secondary is NEVER contacted (cold standby, zero extra load).
    /// The client holds the primary's (lower) generation and resolvedFrom stays 'primary'.
    /// This is the cold-standby proof the chaos rig cannot make (no server-side request counter).
    /// </summary>
    [Fact]
    public async Task FastPrimary_NeverContactsSecondary()
    {
        using var primary = Upstream(generation: 41, delay: TimeSpan.Zero);
        using var secondary = Upstream(generation: 42, delay: TimeSpan.Zero);

        await using var client = NewHedgeClient(primary, secondary);
        await client.InitAsync();

        client.HeldGeneration.Should().Be(41,
            "the fast primary wins; the secondary's newer 42 must not be installed");
        client.ResolvedFrom.Should().Be("primary");
        Hits(secondary).Should().Be(0,
            "cold standby — a fast primary must never trigger the hedge against the secondary");
        Hits(primary).Should().BeGreaterThan(0, "the primary must have been contacted");
    }

    /// <summary>
    /// Unit-level o05 and the cleanest RED→GREEN discriminator: the primary is SLOW and serves the
    /// OLDER generation (41); the secondary is fast and serves the NEWER generation (42). The hedge
    /// fires the secondary once the hedge delay elapses (primary still slow), installs 42, and when
    /// the slow primary's older 41 lands late the reject-older guard drops it.
    ///
    /// <para>On the pre-hedge sequential transport the primary is tried first; it answers (slowly,
    /// inside the per-URL timeout) with 41, the secondary is never contacted, and the client holds 41
    /// — so this test is RED. The hedge makes it hold 42 (GREEN).</para>
    /// </summary>
    [Fact]
    public async Task SecondaryNewer_WinsOverSlowOlderPrimary()
    {
        // 4s primary delay (well above the ~2s hedge delay) so the fast secondary deterministically
        // wins the first install even on a slow/contended CI runner where the hedge-delay timer can
        // fire late — a 2.5s delay left only a ~0.5s margin and flaked resolvedFromPrimary on net48.
        using var primary = Upstream(generation: 41, delay: TimeSpan.FromMilliseconds(4000));
        using var secondary = Upstream(generation: 42, delay: TimeSpan.Zero);

        await using var client = NewHedgeClient(primary, secondary);
        await client.InitAsync();

        // The hedge must have fired the secondary (slow primary) and installed its 42.
        await PollUntilGenerationAsync(client, 42, TimeSpan.FromSeconds(5));
        Hits(secondary).Should().BeGreaterThan(0,
            "the hedge must have fired the secondary against the slow primary");

        // The slow primary's older 41 lands late and on every subsequent refresh; the reject-older
        // guard must keep the client on 42.
        for (int i = 0; i < 3; i++)
        {
            await client.RefreshAsync();
        }
        client.HeldGeneration.Should().Be(42,
            "reject-older must drop the slow 41 — no regression to the older primary leg");
    }

    /// <summary>
    /// Unit-level o03: the primary is SLOW and serves the NEWER generation (42); the secondary is
    /// fast and serves the OLDER generation (41). The hedge seeds readiness off the secondary's 41,
    /// then heals forward to the primary's 42 when it lands — reject-older only blocks going
    /// backward, never forward.
    ///
    /// <para>On the pre-hedge sequential transport the secondary is never contacted (the slow primary
    /// answers first with 42), so the secondary's hit count is 0 — RED. The hedge contacts the
    /// secondary in parallel (GREEN).</para>
    /// </summary>
    [Fact]
    public async Task HealsForward_ToSlowNewerPrimary()
    {
        // Deterministic margin (qfg-y7o7): drive the hedge with a SMALL 200ms delay and keep the
        // primary an order of magnitude slower (2000ms), so the hedge timer fires ~1800ms before the
        // primary can settle. The old 2500ms-primary / 2000ms-default-hedge pairing left only a ~500ms
        // margin; on a load-starved windows/net48 runner the Task.Delay(2s) timer slipped past the
        // primary's 2.5s response, the primary won the WhenAny, the hedge was suppressed, and the
        // secondary was never contacted (Hits==0). A 10x primary/hedge ratio can't be flipped by
        // scheduler jitter, and the configurable ConfigFetchHedgeDelay keeps this off the ~2s default.
        using var primary = Upstream(generation: 42, delay: TimeSpan.FromMilliseconds(2000));
        using var secondary = Upstream(generation: 41, delay: TimeSpan.Zero);

        await using var client = NewHedgeClient(primary, secondary,
            hedgeDelay: TimeSpan.FromMilliseconds(200));
        await client.InitAsync();

        Hits(secondary).Should().BeGreaterThan(0,
            "the hedge must have fired the secondary against the slow primary");
        // Heal forward to the slow primary's newer 42.
        await PollUntilGenerationAsync(client, 42, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Failover telemetry call-site wiring (qfg-41nh.18): a slow OLDER primary + fast NEWER secondary
    /// drives every failover signal through the real client — the hedge fires the secondary
    /// (hedgeFired), the secondary's 42 installs (resolvedFromSecondary), and on the follow-up
    /// refreshes the slow primary's STRICTLY older 41 is dropped by the reject-older guard and counted
    /// (guardRejected; the secondary's same-generation 42 is dropped too but is deliberately not
    /// counted since qfg-rr5b). Draining the client's collector proves the three
    /// <c>_failover</c> record sites in <c>FetchAndInstallAsync</c> actually fired — the branches this
    /// change added, not merely the collector in isolation.
    /// </summary>
    [Fact]
    public async Task RecordsFailoverSignals_HedgeFired_ResolvedFromSecondary_GuardRejected()
    {
        // 4s primary delay (well above the ~2s hedge delay) so the fast secondary deterministically
        // wins the first install even on a slow/contended CI runner where the hedge-delay timer can
        // fire late — a 2.5s delay left only a ~0.5s margin and flaked resolvedFromPrimary on net48.
        using var primary = Upstream(generation: 41, delay: TimeSpan.FromMilliseconds(4000));
        using var secondary = Upstream(generation: 42, delay: TimeSpan.Zero);

        await using var client = NewHedgeClient(primary, secondary);
        await client.InitAsync();

        // The hedge fired the secondary and installed its newer 42.
        await PollUntilGenerationAsync(client, 42, TimeSpan.FromSeconds(5));

        // A couple of fully-drained refresh cycles: the slow primary's STRICTLY older 41 lands, is
        // guard-rejected and IS counted; the secondary's same-generation 42 is a silent no-op that is
        // NOT counted (qfg-rr5b).
        await client.RefreshAsync();
        await client.RefreshAsync();

        client.Failover.Should().NotBeNull("telemetry is enabled by default, so the collector exists");
        var ev = client.Failover!.Drain();
        ev.Should().NotBeNull("real failover activity must produce a failover event");
        var f = (IDictionary<string, object?>)ev!["failover"]!;

        ((long)f["hedgeFired"]!).Should().BeGreaterThanOrEqualTo(1L,
            "the slow primary must have triggered the hedge at least once");
        ((long)f["resolvedFromSecondary"]!).Should().BeGreaterThanOrEqualTo(1L,
            "the secondary's newer 42 must have been the installed leg");
        ((long)f["resolvedFromPrimary"]!).Should().Be(0L,
            "the slow older primary never won an install");
        ((long)f["guardRejected"]!).Should().BeGreaterThanOrEqualTo(1L,
            "the slow primary's STRICTLY older 41 must have been dropped and counted by the reject-older "
            + "guard (the secondary's same-generation 42 is dropped too, but is no longer counted — qfg-rr5b)");
    }

    /// <summary>
    /// A fast healthy primary is a steady-state client: the secondary never fires, nothing is
    /// guard-rejected, and the only signal is resolvedFromPrimary. Confirms the hedgeFired site is
    /// gated on the secondary actually firing (fired &gt; 1), not on every cycle.
    /// </summary>
    [Fact]
    public async Task FastPrimary_RecordsOnlyResolvedFromPrimary_NoHedge_NoGuardReject()
    {
        using var primary = Upstream(generation: 41, delay: TimeSpan.Zero);
        using var secondary = Upstream(generation: 42, delay: TimeSpan.Zero);

        await using var client = NewHedgeClient(primary, secondary);
        await client.InitAsync();
        await PollUntilGenerationAsync(client, 41, TimeSpan.FromSeconds(5));

        var ev = client.Failover!.Drain();
        ev.Should().NotBeNull("the primary install records a resolvedFrom signal");
        var f = (IDictionary<string, object?>)ev!["failover"]!;

        ((long)f["resolvedFromPrimary"]!).Should().BeGreaterThanOrEqualTo(1L);
        ((long)f["resolvedFromSecondary"]!).Should().Be(0L, "the secondary was never contacted");
        ((long)f["hedgeFired"]!).Should().Be(0L, "a fast primary must not fire the hedge");
        ((long)f["guardRejected"]!).Should().Be(0L, "nothing was dropped on a clean first install");
    }
}
