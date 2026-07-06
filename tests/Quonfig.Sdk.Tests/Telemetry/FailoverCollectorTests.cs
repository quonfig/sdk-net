using System.Collections.Generic;
using FluentAssertions;
using Quonfig.Sdk.Telemetry;
using Xunit;

namespace Quonfig.Sdk.Tests.Telemetry;

/// <summary>
/// Unit coverage for <see cref="FailoverCollector"/>, mirroring sdk-go's
/// <c>failover_aggregator_test.go</c>: an empty window drains to <c>null</c>, counts accumulate and
/// reset on drain, a negative source index is ignored, and the drained event uses the exact
/// camelCase wire field names api-telemetry expects.
/// </summary>
public sealed class FailoverCollectorTests
{
    [Fact]
    public void EmptyWindowDrainsToNull()
    {
        var c = new FailoverCollector();
        c.Drain().Should().BeNull("a client with no failover activity emits no failover event");
    }

    [Fact]
    public void NegativeSourceIndexIsIgnored()
    {
        var c = new FailoverCollector();
        c.RecordResolvedFrom(-1); // SSE / datadir install with no HTTP leg
        c.Drain().Should().BeNull("a no-leg install must not, by itself, produce an event");
    }

    [Fact]
    public void CountsAccumulateAndResetOnDrain()
    {
        var c = new FailoverCollector();
        c.RecordHedgeFired();
        c.RecordHedgeFired();
        c.RecordGuardRejected();
        c.RecordResolvedFrom(0); // primary
        c.RecordResolvedFrom(1); // secondary
        c.RecordResolvedFrom(2); // secondary (any index > 0)

        var ev = c.Drain();
        ev.Should().NotBeNull();
        ev!.Should().ContainKey("failover");
        var f = (IDictionary<string, object?>)ev!["failover"]!;

        f["hedgeFired"].Should().Be(2L);
        f["guardRejected"].Should().Be(1L);
        f["resolvedFromPrimary"].Should().Be(1L);
        f["resolvedFromSecondary"].Should().Be(2L);
        f["resolvedFromLkg"].Should().Be(0L);
        ((long)f["start"]!).Should().BeGreaterThan(0L);
        ((long)f["end"]!).Should().BeGreaterThanOrEqualTo((long)f["start"]!);

        // Draining resets the window: the next empty window is null.
        c.Drain().Should().BeNull("drain must reset the window");
    }

    [Fact]
    public void DrainUsesExactCamelCaseFieldNames()
    {
        var c = new FailoverCollector();
        c.RecordHedgeFired();
        var ev = c.Drain();
        var f = (IDictionary<string, object?>)ev!["failover"]!;
        f.Keys.Should().BeEquivalentTo(new[]
        {
            "start", "end", "hedgeFired", "guardRejected",
            "resolvedFromPrimary", "resolvedFromSecondary", "resolvedFromLkg",
        });
    }
}
