using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Quonfig.Sdk.Supervisor;

/// <summary>
/// Layer 2 fallback poller (qfg-zp7i.10; cross-SDK parity with sdk-java's
/// <c>FallbackPoller</c> and sdk-go's <c>fallback_poller.go</c>).
///
/// <para>The poller is idle while SSE is connected. When SSE has been disconnected
/// for at least <see cref="Threshold"/> (default 120s) the poller engages: it fires
/// an immediate fetch and then ticks at <see cref="Interval"/> (default 60s) until
/// SSE reconnects, at which point it disengages and returns to idle.</para>
///
/// <para>The Supervisor owns the worker thread; this class does not spawn its own
/// — register it via <see cref="Worker"/> under layer label <c>"2"</c>.</para>
///
/// <para><see cref="SetSseConnected(bool)"/> is the only state-edge input. Callers
/// (the Quonfig client's SSE state callback) feed transitions in; the poller
/// maintains its own disconnect-since timestamp.</para>
///
/// <para>Reference:
/// <c>project/plans/sdk-hardening-and-verification.md</c> §"Layer 2 (fallback poller)"
/// and <c>integration-test-data/chaos/supervisor-test-contract.md</c>.</para>
/// </summary>
public sealed class FallbackPoller
{
    /// <summary>Cross-SDK default: 120s of disconnect before Layer 2 engages.</summary>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromSeconds(120);

    /// <summary>Cross-SDK default poll cadence once engaged.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _interval;
    private readonly TimeSpan _threshold;
    private readonly Func<CancellationToken, Task> _fetch;
    private readonly Action? _onEngage;
    private readonly Action? _onDisengage;
    private readonly ILogger _logger;

    private readonly object _lock = new();
    // sseConnected starts true so the poller stays idle until the first real
    // disconnect edge. The Quonfig client's SSE state callback fires on every
    // transition; the first SetSseConnected(false) arms the threshold timer.
    private bool _sseConnected = true;
    private long _disconnectedSinceTicks;
    private bool _hasDisconnectStamp;
    private bool _engaged;

    // Completed (and replaced) on every SetSseConnected call, under _lock. A wait
    // grabs the current task under the lock and parks on it, so an edge wakes the
    // worker without any polling (qfg-goi1.2.15).
    private TaskCompletionSource<bool> _edgeSignal = NewEdgeSignal();

    // Test seams (qfg-goi1.2.15): how many waits the worker entered, and how many
    // times a wait woke up without returning to the decision loop.
    private long _waitsEntered;
    private long _waitWakeups;

    internal long WaitsEntered => Interlocked.Read(ref _waitsEntered);

    internal long WaitWakeups => Interlocked.Read(ref _waitWakeups);

    /// <summary>Threshold of SSE-down time before engaging.</summary>
    public TimeSpan Threshold => _threshold;

    /// <summary>Interval between fetches while engaged.</summary>
    public TimeSpan Interval => _interval;

    /// <summary>Constructs a new poller. Callbacks may be <c>null</c>.</summary>
    /// <param name="fetch">Fetch body. Invoked once per tick while engaged; an
    /// exception is logged and swallowed (Layer 2 must survive a transport
    /// blip).</param>
    /// <param name="interval">Tick cadence while engaged. Defaults to
    /// <see cref="DefaultInterval"/>.</param>
    /// <param name="threshold">SSE-down duration before engaging. Defaults to
    /// <see cref="DefaultThreshold"/>.</param>
    /// <param name="onEngage">Callback fired exactly once per engage transition.</param>
    /// <param name="onDisengage">Callback fired exactly once per disengage
    /// transition.</param>
    /// <param name="logger">Optional logger; defaults to
    /// <see cref="NullLogger.Instance"/>.</param>
    public FallbackPoller(
        Func<CancellationToken, Task> fetch,
        TimeSpan? interval = null,
        TimeSpan? threshold = null,
        Action? onEngage = null,
        Action? onDisengage = null,
        ILogger? logger = null)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _interval = interval ?? DefaultInterval;
        _threshold = threshold ?? DefaultThreshold;
        _onEngage = onEngage;
        _onDisengage = onDisengage;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Feeds an SSE connection state edge into the poller. Safe to call from any
    /// thread; never blocks.
    /// </summary>
    public void SetSseConnected(bool connected)
    {
        lock (_lock)
        {
            _sseConnected = connected;
            if (connected)
            {
                _hasDisconnectStamp = false;
                _disconnectedSinceTicks = 0;
            }
            else if (!_hasDisconnectStamp)
            {
                _disconnectedSinceTicks = DateTime.UtcNow.Ticks;
                _hasDisconnectStamp = true;
            }
            var signal = _edgeSignal;
            _edgeSignal = NewEdgeSignal();
            // RunContinuationsAsynchronously: the waiter resumes on the pool, not
            // inline on this caller's thread while it holds _lock.
            signal.TrySetResult(true);
        }
    }

    private static TaskCompletionSource<bool> NewEdgeSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True while the poller is engaged (i.e. SSE has been down past the threshold).</summary>
    public bool Active
    {
        get { lock (_lock) { return _engaged; } }
    }

    /// <summary>
    /// Returns the worker body suitable for handing to a
    /// <see cref="WorkerSpec"/> under layer label <c>"2"</c>.
    /// </summary>
    public Func<WorkerContext, Task> Worker => RunAsync;

    private enum Action_
    {
        None,
        EngageAndFetch,
        Fetch,
        Disengage,
    }

    private async Task RunAsync(WorkerContext ctx)
    {
        bool engagedLocal = false;
        try
        {
            while (!ctx.IsStopped)
            {
                Action_ action;
                TimeSpan wait;
                // The SSE state this tick's decision was made against. It is carried
                // into the wait below as the edge baseline instead of being re-read
                // there: between releasing the lock and entering the wait we run this
                // tick's side effects (engage/disengage callbacks, the fetch), and a
                // SetSseConnected that lands in that gap must still count as an edge
                // (qfg-vov2). Re-reading swallowed it and slept the full duration —
                // one hour on the connected branch.
                bool connectedAtDecision;
                lock (_lock)
                {
                    connectedAtDecision = _sseConnected;
                    if (connectedAtDecision)
                    {
                        if (engagedLocal)
                        {
                            _engaged = false;
                            engagedLocal = false;
                            action = Action_.Disengage;
                        }
                        else
                        {
                            action = Action_.None;
                        }
                        // No deadline while connected — the wait wakes on the SetSseConnected signal.
                        wait = TimeSpan.FromHours(1);
                    }
                    else
                    {
                        long sinceTicks = DateTime.UtcNow.Ticks - _disconnectedSinceTicks;
                        var since = TimeSpan.FromTicks(Math.Max(0, sinceTicks));
                        if (engagedLocal)
                        {
                            action = Action_.Fetch;
                            wait = _interval;
                        }
                        else if (since >= _threshold)
                        {
                            _engaged = true;
                            engagedLocal = true;
                            action = Action_.EngageAndFetch;
                            wait = _interval;
                        }
                        else
                        {
                            action = Action_.None;
                            var remaining = _threshold - since;
                            wait = remaining < TimeSpan.FromMilliseconds(1)
                                ? TimeSpan.FromMilliseconds(1)
                                : remaining;
                        }
                    }
                }

                switch (action)
                {
                    case Action_.EngageAndFetch:
                        SafeRun(_onEngage, "onEngage");
                        await SafeFetchAsync(ctx.StopToken).ConfigureAwait(false);
                        break;
                    case Action_.Fetch:
                        await SafeFetchAsync(ctx.StopToken).ConfigureAwait(false);
                        break;
                    case Action_.Disengage:
                        SafeRun(_onDisengage, "onDisengage");
                        break;
                    case Action_.None:
                    default:
                        break;
                }

                if (ctx.IsStopped) return;

                // Wait — woken early by a SetSseConnected edge signal or by
                // ctx.StopToken (the deadline delay is bound to it, so
                // cancellation unblocks us immediately), then re-check state.
                try
                {
                    await DelayWithPulseAsync(wait, connectedAtDecision, ctx.StopToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        finally
        {
            bool wasEngaged;
            lock (_lock)
            {
                wasEngaged = _engaged;
                _engaged = false;
            }
            if (wasEngaged) SafeRun(_onDisengage, "onDisengage");
        }
    }

    /// <summary>
    /// Waits up to <paramref name="duration"/> for either the deadline or an
    /// SSE state edge from <see cref="SetSseConnected"/>. The wait parks on the
    /// edge signal and a single deadline delay; nothing wakes in between
    /// (qfg-goi1.2.15: it used to poll on a 5 ms tick, ~200 wakeups/s for the
    /// life of every client).
    /// </summary>
    /// <param name="duration">Maximum time to wait.</param>
    /// <param name="baseline">The <c>_sseConnected</c> value the caller's wait
    /// decision was made against. Passed in rather than re-read here so a state
    /// change that lands between the caller's lock release and this wait is still
    /// seen as an edge (qfg-vov2).</param>
    /// <param name="stop">Stop token; cancellation unblocks the wait.</param>
    private async Task DelayWithPulseAsync(TimeSpan duration, bool baseline, CancellationToken stop)
    {
        if (duration <= TimeSpan.Zero) return;
        // Task.Delay rejects anything past int.MaxValue ms (~24.8 days). Returning
        // early is harmless: the caller just re-runs its decision.
        if (duration > MaxDelay) duration = MaxDelay;
        Interlocked.Increment(ref _waitsEntered);
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var deadline = Task.Delay(duration, deadlineCts.Token);
        try
        {
            while (true)
            {
                Task edge;
                // Checked BEFORE the first park as well as after every signal: the
                // edge may already have landed in the caller's post-lock window, in
                // which case there is nothing left to wait for.
                lock (_lock)
                {
                    if (_sseConnected != baseline) return; // edge — re-evaluate.
                    edge = _edgeSignal.Task;
                }
                var done = await Task.WhenAny(edge, deadline).ConfigureAwait(false);
                if (done == deadline)
                {
                    // Throws OperationCanceledException if the stop token fired.
                    await deadline.ConfigureAwait(false);
                    return;
                }
                // Signaled, but possibly with the same state (a repeated
                // SetSseConnected(true)); loop and re-check against the baseline.
                Interlocked.Increment(ref _waitWakeups);
            }
        }
        finally
        {
            // Release the pending deadline timer when we return on an edge.
            deadlineCts.Cancel();
        }
    }

    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Layer 2 must survive any fetch exception — supervisor counts the restart only at the layer-1 boundary.")]
    private async Task SafeFetchAsync(CancellationToken stop)
    {
        try
        {
            await _fetch(stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Quiet on shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "quonfig: fallback poller fetch threw: {Message}", ex.Message);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Layer 2 must survive any callback exception.")]
    private void SafeRun(Action? r, string name)
    {
        if (r is null) return;
        try
        {
            r();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "quonfig: fallback poller {Name} callback threw: {Message}",
                name,
                ex.Message);
        }
    }
}
