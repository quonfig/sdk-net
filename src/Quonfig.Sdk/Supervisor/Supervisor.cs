using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Quonfig.Sdk.Supervisor;

/// <summary>
/// Watcher-of-the-watchers for sdk-net background workers (qfg-zp7i.10; cross-SDK
/// parity with <c>sdk-java</c>'s <c>Supervisor</c> and <c>sdk-go</c>'s
/// <c>supervisor.go</c>). One <see cref="Supervisor"/> per Quonfig client. It owns the
/// Layer 1 (SSE) worker and the Layer 2 (fallback-poller) worker.
///
/// <para>Each worker runs inside a try/catch at the supervisor boundary. An
/// unhandled exception or non-cancellation early return:</para>
/// <list type="bullet">
///   <item>logs at <see cref="LogLevel.Error"/> with the worker's <c>layer</c> label,</item>
///   <item>increments <c>quonfig_sdk_worker_restart_total{layer="&lt;n&gt;"}</c>,</item>
///   <item>sleeps an exponential backoff (default 500ms → 30s cap), and</item>
///   <item>restarts the worker.</item>
/// </list>
///
/// <para>A clean exit driven by <see cref="StopAsync"/> is <em>not</em> counted as a
/// restart. <see cref="StopAsync"/> joins all workers within a configurable deadline
/// (default 5s) and is idempotent.</para>
///
/// <para>The Supervisor is also the source of truth for
/// <see cref="ConnectionState"/> and <see cref="LastSuccessfulRefresh"/>; workers
/// report into it via <see cref="SetConnectionState"/> and
/// <see cref="RecordSuccessfulRefresh"/>.</para>
///
/// <para>Reference: <c>project/plans/sdk-hardening-and-verification.md</c> §"Watcher
/// of the watchers" and <c>integration-test-data/chaos/supervisor-test-contract.md</c>.</para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1724:Type names should not match namespaces",
    Justification = "Supervisor is the canonical class under Quonfig.Sdk.Supervisor, matching the sdk-java package layout (com.quonfig.sdk.supervisor.Supervisor).")]
public sealed class Supervisor : IAsyncDisposable
{
    /// <summary>Default initial backoff (500ms) per the cross-SDK contract.</summary>
    public static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Default backoff cap (30s) per the cross-SDK contract.</summary>
    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>Default stop-deadline (5s) per the cross-SDK contract.</summary>
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;
    private readonly TimeSpan _stopTimeout;
    private readonly IReadOnlyList<WorkerSpec> _workers;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _stopCts = new();
    private readonly List<Task> _workerTasks = new();
    private readonly ConcurrentDictionary<string, long> _restartTotals = new();

    private int _startedOnce;
    private int _stoppedOnce;
    private long _connectionStateRaw = (long)ConnectionState.Initializing;
    private long _lastSuccessfulRefreshTicks;
    private long _hasRefresh;

    /// <summary>
    /// Constructs a Supervisor that will run the given workers on
    /// <see cref="Start"/>. Sub-millisecond bounds are accepted for tests; production
    /// callers should use defaults.
    /// </summary>
    /// <param name="workers">Workers to supervise. May be empty (in which case
    /// <see cref="Start"/> is a no-op).</param>
    /// <param name="initialDelay">Initial backoff. Defaults to
    /// <see cref="DefaultInitialDelay"/>.</param>
    /// <param name="maxDelay">Backoff cap. Defaults to <see cref="DefaultMaxDelay"/>.</param>
    /// <param name="stopTimeout">Stop-deadline. Defaults to <see cref="DefaultStopTimeout"/>.</param>
    /// <param name="logger">Optional logger; defaults to <see cref="NullLogger.Instance"/>.</param>
    public Supervisor(
        IEnumerable<WorkerSpec>? workers = null,
        TimeSpan? initialDelay = null,
        TimeSpan? maxDelay = null,
        TimeSpan? stopTimeout = null,
        ILogger? logger = null)
    {
        _initialDelay = initialDelay ?? DefaultInitialDelay;
        _maxDelay = maxDelay ?? DefaultMaxDelay;
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;
        _workers = workers is null ? Array.Empty<WorkerSpec>() : new List<WorkerSpec>(workers);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Spawns every worker as a background <see cref="Task"/>. Idempotent.</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _startedOnce, 1, 0) != 0) return;
        foreach (var spec in _workers)
        {
            // Capture for closure.
            var captured = spec;
            _workerTasks.Add(Task.Run(() => RunWorkerAsync(captured)));
        }
    }

    /// <summary>
    /// Signals every worker to wind down and awaits them up to the configured stop
    /// deadline (default 5s). Idempotent — second and subsequent calls are no-ops.
    /// </summary>
    public async Task StopAsync()
    {
        if (Interlocked.CompareExchange(ref _stoppedOnce, 1, 0) != 0) return;
        try
        {
#if NET8_0_OR_GREATER
            await _stopCts.CancelAsync().ConfigureAwait(false);
#else
            _stopCts.Cancel();
#endif
        }
        catch (ObjectDisposedException)
        {
            // Already disposed — nothing to cancel.
        }
        Task[] snapshot;
        lock (_workerTasks)
        {
            snapshot = _workerTasks.ToArray();
        }
        if (snapshot.Length > 0)
        {
            var all = Task.WhenAll(snapshot);
            var done = await Task.WhenAny(all, Task.Delay(_stopTimeout)).ConfigureAwait(false);
            if (!ReferenceEquals(done, all))
            {
                _logger.LogWarning(
                    "quonfig: supervisor.StopAsync() deadline exceeded after {Timeout}",
                    _stopTimeout);
            }
        }
        // After stop, transport state is "Disconnected" — caller-visible health
        // matches the cross-SDK contract.
        Volatile.Write(ref _connectionStateRaw, (long)ConnectionState.Disconnected);
    }

    private async Task RunWorkerAsync(WorkerSpec spec)
    {
        var ctx = new WorkerContext(_stopCts.Token);
        int attempt = 0;
        while (!_stopCts.IsCancellationRequested)
        {
            bool crashed = await RunOnceAsync(spec, ctx).ConfigureAwait(false);
            if (_stopCts.IsCancellationRequested) return;
            if (crashed)
            {
                IncRestart(spec.Layer);
            }
            // Every iteration (crashed or not) takes one tick of backoff so a
            // runaway worker can't hot-loop the supervisor.
            var delay = BackoffFor(attempt++);
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _stopCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Supervisor boundary: any worker exception must be caught, logged, and converted into a restart so the SDK keeps running.")]
    private async Task<bool> RunOnceAsync(WorkerSpec spec, WorkerContext ctx)
    {
        try
        {
            await spec.Worker(ctx).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (_stopCts.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (_stopCts.IsCancellationRequested) return false;
            _logger.LogError(
                ex,
                "quonfig: worker threw; restarting layer={Layer} err={Message}",
                spec.Layer,
                ex.Message);
            return true;
        }
    }

    /// <summary>
    /// Computes the sleep duration before the (<paramref name="attempt"/>+1)th
    /// restart. 500ms → 1s → 2s → 4s → 8s → 16s → 30s (cap). <paramref name="attempt"/>
    /// is 0-indexed; the returned value is capped at <c>maxDelay</c>.
    /// </summary>
    public TimeSpan BackoffFor(int attempt)
    {
        if (attempt < 0) attempt = 0;
        double initialMs = _initialDelay.TotalMilliseconds;
        double maxMs = _maxDelay.TotalMilliseconds;
        double d = initialMs;
        for (int i = 0; i < attempt; i++)
        {
            double next = d * 2.0;
            if (next >= maxMs)
            {
                return TimeSpan.FromMilliseconds(maxMs);
            }
            d = next;
        }
        if (d > maxMs) d = maxMs;
        return TimeSpan.FromMilliseconds(d);
    }

    /// <summary>
    /// Returns the running count of
    /// <c>quonfig_sdk_worker_restart_total{layer="&lt;layer&gt;"}</c> for this
    /// supervisor. Unknown layers return 0.
    /// </summary>
    public long WorkerRestartTotal(string layer)
    {
        if (layer is null) return 0;
        return _restartTotals.TryGetValue(layer, out var v) ? v : 0;
    }

    private void IncRestart(string layer)
    {
        _restartTotals.AddOrUpdate(layer, 1, (_, v) => v + 1);
    }

    /// <summary>
    /// Most recent transport state reported by any worker. Defaults to
    /// <see cref="ConnectionState.Initializing"/>.
    /// </summary>
    public ConnectionState ConnectionState =>
        (ConnectionState)Interlocked.Read(ref _connectionStateRaw);

    /// <summary>
    /// Records a transport-state transition. Workers call this — e.g. the SSE
    /// worker on connect/disconnect, the fallback poller when it engages.
    /// </summary>
    public void SetConnectionState(ConnectionState state)
    {
        Volatile.Write(ref _connectionStateRaw, (long)state);
    }

    /// <summary>
    /// Wall-clock time (UTC) of the most recent successful config install, or
    /// <c>null</c> if no envelope has been installed yet.
    ///
    /// <para><b>Do not wire this into a Kubernetes liveness probe.</b> A stuck
    /// refresh is a diagnostic signal, not a liveness signal — a probe based on
    /// freshness will amplify transient network blips into restart cascades.</para>
    /// </summary>
    public DateTime? LastSuccessfulRefresh
    {
        get
        {
            if (Interlocked.Read(ref _hasRefresh) == 0) return null;
            long ticks = Interlocked.Read(ref _lastSuccessfulRefreshTicks);
            return new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>
    /// Stamps "now" (UTC) as the most recent successful install. Callers invoke
    /// after atomically swapping a new envelope into the resolver.
    /// </summary>
    public void RecordSuccessfulRefresh()
    {
        Interlocked.Exchange(ref _lastSuccessfulRefreshTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _hasRefresh, 1);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopCts.Dispose();
    }
}

/// <summary>Layer label + worker body. The layer label is what shows up on the restart metric.</summary>
public sealed class WorkerSpec
{
    /// <summary>Metric layer label (e.g. <c>"1"</c> for SSE, <c>"2"</c> for fallback poll).</summary>
    public string Layer { get; }

    /// <summary>The worker body. Returns when stopped (clean) or throws (restart).</summary>
    public Func<WorkerContext, Task> Worker { get; }

    /// <summary>Constructs a new spec.</summary>
    public WorkerSpec(string layer, Func<WorkerContext, Task> worker)
    {
        Layer = layer ?? throw new ArgumentNullException(nameof(layer));
        Worker = worker ?? throw new ArgumentNullException(nameof(worker));
    }
}

/// <summary>
/// Cooperation surface passed to each worker. Workers either block on
/// <see cref="AwaitStopAsync"/> (woken by <see cref="Supervisor.StopAsync"/>) or
/// poll <see cref="IsStopped"/> between iterations.
/// </summary>
public sealed class WorkerContext
{
    /// <summary>Token cancelled when <see cref="Supervisor.StopAsync"/> fires.</summary>
    public CancellationToken StopToken { get; }

    internal WorkerContext(CancellationToken stopToken)
    {
        StopToken = stopToken;
    }

    /// <summary>True once <see cref="Supervisor.StopAsync"/> has been called.</summary>
    public bool IsStopped => StopToken.IsCancellationRequested;

    /// <summary>
    /// Blocks until <see cref="Supervisor.StopAsync"/> is called. Returns
    /// immediately if already stopped. Never throws — returns on cancellation.
    /// </summary>
    public async Task AwaitStopAsync()
    {
        if (StopToken.IsCancellationRequested) return;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (StopToken.Register(() => tcs.TrySetResult(true)))
        {
            await tcs.Task.ConfigureAwait(false);
        }
    }
}
