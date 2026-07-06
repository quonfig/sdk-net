using System;
using System.Collections.Generic;

namespace Quonfig.Sdk.Telemetry;

/// <summary>
/// Accumulates failover-behavior counters over a flush window: how many config-fetch cycles fired
/// the parallel hedge's secondary leg, how many installs the reject-older ordering guard dropped,
/// and which upstream leg (primary/secondary) served each successful HTTP install. Every counter is
/// additive and carries no user data.
///
/// <para>Thread-safe via a single lock; written directly from the failover call sites in
/// <see cref="Quonfig"/> rather than through an evaluation queue — the call rate is per
/// config-refresh (not per-evaluation), so a plain lock has negligible overhead. Rides the same
/// telemetry envelope as the eval/context collectors via <see cref="TelemetryReporter"/>, and only
/// emits when at least one counter is non-zero, so a healthy steady-state client produces no
/// failover event at all. Mirrors sdk-go's <c>FailoverAggregator</c> (qfg-41nh.18).</para>
/// </summary>
public sealed class FailoverCollector
{
    private readonly object _gate = new();
    private long? _startMs;
    private long _hedgeFired;
    private long _guardRejected;
    private long _resolvedFromPrimary;
    private long _resolvedFromSecondary;

    // Reserved for last-known-good resolutions; backends emit 0 today. Kept for wire symmetry with
    // the other SDKs so the field is present on every failover event.
    private long _resolvedFromLkg;

    /// <summary>
    /// Records one config-fetch cycle whose parallel hedge fired its secondary leg (the primary was
    /// slow or errored). Counted once per cycle regardless of which leg's payload won the guard.
    /// </summary>
    public void RecordHedgeFired()
    {
        lock (_gate)
        {
            _startMs ??= NowMs();
            _hedgeFired++;
        }
    }

    /// <summary>
    /// Records one install dropped by the reject-older ordering guard — an equal-or-older snapshot on
    /// any network install path (HTTP config-fetch or SSE push).
    /// </summary>
    public void RecordGuardRejected()
    {
        lock (_gate)
        {
            _startMs ??= NowMs();
            _guardRejected++;
        }
    }

    /// <summary>
    /// Records one successful HTTP install by the leg that served it: <paramref name="sourceIndex"/>
    /// 0 is the primary, any index &gt; 0 is a failover/secondary leg. A negative index (an SSE or
    /// datadir install with no HTTP leg) is ignored.
    /// </summary>
    public void RecordResolvedFrom(int sourceIndex)
    {
        if (sourceIndex < 0)
        {
            return;
        }
        lock (_gate)
        {
            _startMs ??= NowMs();
            if (sourceIndex == 0)
            {
                _resolvedFromPrimary++;
            }
            else
            {
                _resolvedFromSecondary++;
            }
        }
    }

    /// <summary>
    /// Returns the window's counters as a telemetry event (<c>{ "failover": { ... } }</c>) and resets
    /// state. Returns <c>null</c> when no failover activity occurred (every counter zero), so a
    /// healthy steady-state client emits no failover event. Field names are camelCase exactly as
    /// api-telemetry's schema expects; <c>start</c>/<c>end</c> are unix milliseconds.
    /// </summary>
    public IDictionary<string, object?>? Drain()
    {
        lock (_gate)
        {
            if (_hedgeFired == 0 && _guardRejected == 0
                && _resolvedFromPrimary == 0 && _resolvedFromSecondary == 0 && _resolvedFromLkg == 0)
            {
                return null;
            }

            long end = NowMs();
            long start = _startMs ?? end;

            var failover = new Dictionary<string, object?>
            {
                ["start"] = start,
                ["end"] = end,
                ["hedgeFired"] = _hedgeFired,
                ["guardRejected"] = _guardRejected,
                ["resolvedFromPrimary"] = _resolvedFromPrimary,
                ["resolvedFromSecondary"] = _resolvedFromSecondary,
                ["resolvedFromLkg"] = _resolvedFromLkg,
            };

            _startMs = null;
            _hedgeFired = 0;
            _guardRejected = 0;
            _resolvedFromPrimary = 0;
            _resolvedFromSecondary = 0;
            _resolvedFromLkg = 0;

            return new Dictionary<string, object?> { ["failover"] = failover };
        }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
