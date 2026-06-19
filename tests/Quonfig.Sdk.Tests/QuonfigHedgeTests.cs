using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk;
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

    private static Quonfig NewHedgeClient(WireMockServer primary, WireMockServer secondary) =>
        new Quonfig(new QuonfigOptions
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
        });

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
        using var primary = Upstream(generation: 41, delay: TimeSpan.FromMilliseconds(2500));
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
        using var primary = Upstream(generation: 42, delay: TimeSpan.FromMilliseconds(2500));
        using var secondary = Upstream(generation: 41, delay: TimeSpan.Zero);

        await using var client = NewHedgeClient(primary, secondary);
        await client.InitAsync();

        Hits(secondary).Should().BeGreaterThan(0,
            "the hedge must have fired the secondary against the slow primary");
        // Heal forward to the slow primary's newer 42.
        await PollUntilGenerationAsync(client, 42, TimeSpan.FromSeconds(5));
    }
}
