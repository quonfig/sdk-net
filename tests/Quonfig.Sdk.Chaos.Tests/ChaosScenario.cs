using System.Collections.Generic;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// YAML-shape model for one chaos scenario file under
/// <c>integration-test-data/chaos/scenarios/*.yaml</c>. Mirrors sdk-java's
/// <c>ChaosScenario</c>; the schema authority is
/// <c>integration-test-data/chaos/schema/scenario.schema.json</c>.
/// </summary>
internal sealed class ChaosScenario
{
    public string? Function { get; set; }
    public List<Run> Tests { get; set; } = new();

    internal sealed class Run
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public Setup Setup { get; set; } = new();
        public List<Event> Chaos { get; set; } = new();
        public List<Expectation> Expectations { get; set; } = new();
    }

    internal sealed class Setup
    {
        public string? Sdk { get; set; }
        public string? SseEndpoint { get; set; }
        public string? HttpEndpoint { get; set; }
        public int WallClockSeconds { get; set; } = 30;
        public string? UserCallback { get; set; }

        /// <summary>Rig topology: <c>single</c>, <c>failover</c> (1 upstream + 2 proxies), or <c>ordering</c> (2 upstreams).</summary>
        public string? Topology { get; set; }

        /// <summary>Ordering rig only: per-leg fixture generations the two upstreams are pinned to.</summary>
        public List<Upstream> Upstreams { get; set; } = new();
    }

    /// <summary>One leg of the ordering rig — a fixture upstream pinned to a chosen <c>Meta.generation</c>.</summary>
    internal sealed class Upstream
    {
        public string? Role { get; set; } // primary | secondary
        public int Generation { get; set; }
    }

    internal sealed class Event
    {
        public int AtMs { get; set; }
        public Inject? Inject { get; set; }
        public string? Clear { get; set; }
        public Process? Process { get; set; }
    }

    internal sealed class Inject
    {
        public string? Name { get; set; }
        // Convenience aliases — null means "not set".
        public int? SseSilentStallAfterMs { get; set; }
        public int? SseLatencyMs { get; set; }
        public int? SseBandwidthKbps { get; set; }
        public int? SseDownMs { get; set; }
        public int? BothDownMs { get; set; }
        public int? SseHalfOpenAfterBytes { get; set; }
        public int? SseHttpStatus { get; set; }
        // Failover/ordering rig faults on the PRIMARY HTTP leg ('http' proxy).
        // *_refused/*_hang carry a DURATION (restore after the elapsed ms); *_latency carries a
        // latency VALUE that persists for the rest of the run.
        public int? PrimaryRefusedMs { get; set; }
        public int? PrimaryHangMs { get; set; }
        public int? PrimaryLatencyMs { get; set; }
        // Raw toxiproxy escape hatch.
        public string? Proxy { get; set; }
        public Dictionary<string, object?>? Toxic { get; set; }
    }

    internal sealed class Process
    {
        public string? Action { get; set; }
        public int Count { get; set; }
        public int IntervalMs { get; set; }
    }

    internal sealed class Expectation
    {
        public int WithinMs { get; set; }
        public int MustHoldForMs { get; set; }
        public string AssertExpr { get; set; } = string.Empty;
    }
}
