using System.Threading;

namespace Quonfig.Sdk.Transport;

/// <summary>
/// Internal, test-only Layer 1 (SSE worker) counters, read by the chaos harness through
/// <c>InternalsVisibleTo</c> (qfg-6vno.4). Not public API and not exported as telemetry: sdk-net
/// has no metrics surface for <c>quonfig_sse_connect_attempts_total</c> or
/// <c>quonfig_sdk_worker_restart_total{layer="1"}</c>, and the chaos scenarios must read those
/// values from the SDK rather than derive them from state edges.
/// <para>One instance is owned by the <see cref="Quonfig"/> client and shared by every
/// <see cref="SseClient"/> it starts, so the totals survive the stream being rebuilt on leaving
/// auth-failed.</para>
/// </summary>
internal sealed class SseCounters
{
    private long _connectAttempts;
    private long _workerRestarts;

    /// <summary>Every SSE connect attempt, whatever its outcome (refused, non-200, headers only, or a live stream).</summary>
    public long ConnectAttempts => Interlocked.Read(ref _connectAttempts);

    /// <summary>
    /// Every Layer 1 worker run that ended and was restarted: a live stream that dropped, and an
    /// attempt that failed before its first frame. A clean shutdown is not a restart.
    /// </summary>
    public long WorkerRestarts => Interlocked.Read(ref _workerRestarts);

    internal void RecordConnectAttempt() => Interlocked.Increment(ref _connectAttempts);

    internal void RecordWorkerRestart() => Interlocked.Increment(ref _workerRestarts);
}
