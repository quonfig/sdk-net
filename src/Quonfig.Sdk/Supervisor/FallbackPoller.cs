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
/// <para>Auth-failure rule (qfg-goi1.10.28, plan <c>2026-10-07-sdk-auth-failure-rule.md</c>
/// R1-R6): when one of the poller's cycles gets no 200/304 on any leg and a 401/403/404/410 on the
/// primary leg, the client enters auth-failed: the poller stops, SSE stops, one ERROR is logged,
/// and one probe cycle runs every T, T uniform in [150s, 300s], for as long as the process lives
/// (no doubling, no latch). The first probe answered with a 200 or 304 on any leg exits: one INFO,
/// SSE restarts, and the poller is back on its engage rule.</para>
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

    // The auth-failed probe period T is uniform in [AuthProbeMin, AuthProbeMax] (rule R3,
    // qfg-goi1.10.28). Internal: tests reach it through WaitHook, never an option.
    internal static readonly TimeSpan AuthProbeMin = TimeSpan.FromSeconds(150);
    internal static readonly TimeSpan AuthProbeMax = TimeSpan.FromSeconds(300);

    private readonly TimeSpan _interval;
    private readonly TimeSpan _threshold;
    private readonly Func<CancellationToken, Task<PollOutcome>> _fetch;
    private readonly Func<bool, Task>? _onAuthFailed;
    private readonly Random _rng = new();
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
    // Set while the client is in the auth-failed regime (qfg-goi1.10.28). Lives on the instance,
    // not in RunAsync, so a worker the supervisor restarts resumes probing instead of polling.
    private bool _authFailed;

    // Completed (and replaced) on every SetSseConnected call, under _lock. A wait
    // grabs the current task under the lock and parks on it, so an edge wakes the
    // worker without any polling (qfg-goi1.2.15).
    private TaskCompletionSource<bool> _edgeSignal = NewEdgeSignal();

    // Count of disconnected -> connected transitions (guarded by _lock). A connect that is
    // followed at once by a disconnect (an SSE connection that delivers one keepalive and
    // closes) leaves _sseConnected unchanged by the time the worker wakes, but it DID restart
    // the threshold; the wait compares this counter too so it re-evaluates (qfg-goi1.10.41).
    private long _connectTransitions;

    // Test seams (qfg-goi1.2.15): how many waits the worker entered, and how many
    // times a wait woke up without returning to the decision loop.
    private long _waitsEntered;
    private long _waitWakeups;

    internal long WaitsEntered => Interlocked.Read(ref _waitsEntered);

    internal long WaitWakeups => Interlocked.Read(ref _waitWakeups);

    /// <summary>
    /// Test seam (qfg-goi1.10.28): when set, every background wait the worker requests passes
    /// through it as <c>(kind, requested)</c> and the worker waits the returned duration instead.
    /// Kinds: <c>"engage"</c> (the disconnect threshold), <c>"poll"</c> (the engaged interval).
    /// Never a customer option.
    /// </summary>
    internal Func<string, TimeSpan, TimeSpan>? WaitHook { get; set; }

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
        : this(WrapFetch(fetch ?? throw new ArgumentNullException(nameof(fetch))),
            interval, threshold, onEngage, onDisengage, logger, onAuthFailed: null)
    {
    }

    /// <summary>
    /// Constructs a poller whose fetch reports each cycle's <see cref="PollOutcome"/>, which drives
    /// the auth-failure rule (qfg-goi1.10.28): an <see cref="PollOutcome.IsAuthRejected"/> cycle
    /// enters auth-failed. <paramref name="onAuthFailed"/> runs with <c>true</c> on entry (before
    /// the ERROR; the client stops SSE) and <c>false</c> on exit (after the INFO; the client
    /// restarts SSE).
    /// </summary>
    internal FallbackPoller(
        Func<CancellationToken, Task<PollOutcome>> fetch,
        TimeSpan? interval,
        TimeSpan? threshold,
        Action? onEngage,
        Action? onDisengage,
        ILogger? logger,
        Func<bool, Task>? onAuthFailed)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _onAuthFailed = onAuthFailed;
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
            if (connected && !_sseConnected) _connectTransitions++;
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

    private static Func<CancellationToken, Task<PollOutcome>> WrapFetch(Func<CancellationToken, Task> fetch) =>
        async ct =>
        {
            await fetch(ct).ConfigureAwait(false);
            return PollOutcome.Ok;
        };

    private static TaskCompletionSource<bool> NewEdgeSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True while the poller is engaged (i.e. SSE has been down past the threshold).</summary>
    public bool Active
    {
        get { lock (_lock) { return _engaged; } }
    }

    /// <summary>
    /// True while the client is in the auth-failed regime (qfg-goi1.10.28): SSE stopped, the
    /// poller off, one slow probe per period. Internal diagnostic; not a connection state.
    /// </summary>
    internal bool AuthFailed
    {
        get { lock (_lock) { return _authFailed; } }
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
        // True when the last wait was the engage-threshold wait and it ran to its deadline with
        // no SSE edge: the threshold has elapsed, so engage even if the wall clock (compressed
        // under the test wait hook) says otherwise.
        bool thresholdElapsed = false;
        try
        {
            while (!ctx.IsStopped)
            {
                if (AuthFailed)
                {
                    // R3: one probe per T, T uniform in [150s, 300s], re-drawn each time, no
                    // doubling, never gives up. SSE edges do not matter here (SSE is stopped).
                    try
                    {
                        await Task.Delay(Hooked("probe", AuthProbeWait()), ctx.StopToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    if (ctx.IsStopped) return;
                    await ProbeAsync(ctx.StopToken).ConfigureAwait(false);
                    continue;
                }

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
                long connectsAtDecision;
                string? waitKind;
                lock (_lock)
                {
                    connectedAtDecision = _sseConnected;
                    connectsAtDecision = _connectTransitions;
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
                        waitKind = null;
                    }
                    else
                    {
                        long sinceTicks = DateTime.UtcNow.Ticks - _disconnectedSinceTicks;
                        var since = TimeSpan.FromTicks(Math.Max(0, sinceTicks));
                        if (engagedLocal)
                        {
                            action = Action_.Fetch;
                            wait = _interval;
                            waitKind = "poll";
                        }
                        else if (thresholdElapsed || since >= _threshold)
                        {
                            _engaged = true;
                            engagedLocal = true;
                            action = Action_.EngageAndFetch;
                            wait = _interval;
                            waitKind = "poll";
                        }
                        else
                        {
                            action = Action_.None;
                            var remaining = _threshold - since;
                            wait = remaining < TimeSpan.FromMilliseconds(1)
                                ? TimeSpan.FromMilliseconds(1)
                                : remaining;
                            waitKind = "engage";
                        }
                    }
                }

                PollOutcome outcome = PollOutcome.Ok;
                switch (action)
                {
                    case Action_.EngageAndFetch:
                        SafeRun(_onEngage, "onEngage");
                        outcome = await SafeFetchAsync(ctx.StopToken).ConfigureAwait(false);
                        break;
                    case Action_.Fetch:
                        outcome = await SafeFetchAsync(ctx.StopToken).ConfigureAwait(false);
                        break;
                    case Action_.Disengage:
                        SafeRun(_onDisengage, "onDisengage");
                        break;
                    case Action_.None:
                    default:
                        break;
                }

                if (ctx.IsStopped) return;

                if (outcome.IsAuthRejected)
                {
                    // R2: this poll cycle qualifies. Enter auth-failed; the loop top then waits
                    // for the first probe instead of the poll interval.
                    engagedLocal = false;
                    thresholdElapsed = false;
                    await EnterAuthFailedAsync(outcome).ConfigureAwait(false);
                    continue;
                }

                // Wait — woken early by a SetSseConnected edge signal or by
                // ctx.StopToken (the deadline delay is bound to it, so
                // cancellation unblocks us immediately), then re-check state.
                try
                {
                    bool elapsed = await DelayWithPulseAsync(Hooked(waitKind, wait), connectedAtDecision, connectsAtDecision, ctx.StopToken)
                        .ConfigureAwait(false);
                    thresholdElapsed = elapsed && waitKind == "engage";
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
    /// Enters auth-failed (R2-R5), in this order: poller off (no onDisengage: the client is not
    /// recovering, and its state goes to disconnected, not connected), the client stops SSE, ONE
    /// ERROR naming the status and the host (never the key). The caller then arms the first probe.
    /// </summary>
    private async Task EnterAuthFailedAsync(PollOutcome outcome)
    {
        lock (_lock)
        {
            _engaged = false;
            _authFailed = true;
        }
        await SafeAuthCallbackAsync(true).ConfigureAwait(false);
        _logger.LogError(
            "quonfig: config fetch rejected: the SDK key is invalid or revoked (check the SDK key); streaming and polling stopped, retrying every few minutes until it is accepted status={Status} host={Host}",
            outcome.AuthStatus,
            outcome.Host);
    }

    /// <summary>
    /// One auth-failed probe: one fetch cycle across every leg. The first 200 (valid envelope) or
    /// 304 on any leg exits (R4): one INFO, the client restarts SSE from its base backoff, and the
    /// poller is back on its normal rule (engage after the threshold from now). Anything else logs
    /// DEBUG only and stays auth-failed.
    /// </summary>
    private async Task ProbeAsync(CancellationToken stop)
    {
        var outcome = await SafeFetchAsync(stop).ConfigureAwait(false);
        if (stop.IsCancellationRequested) return;
        if (!outcome.Answered)
        {
            _logger.LogDebug(
                "quonfig: auth-failed probe rejected (status={Status}); retrying later",
                outcome.AuthStatus);
            return;
        }
        lock (_lock)
        {
            _authFailed = false;
            // SSE is about to restart from scratch: the threshold runs from now.
            _sseConnected = false;
            _hasDisconnectStamp = true;
            _disconnectedSinceTicks = DateTime.UtcNow.Ticks;
        }
        _logger.LogInformation("quonfig: config fetch accepted; leaving auth-failed and resuming streaming and polling");
        await SafeAuthCallbackAsync(false).ConfigureAwait(false);
    }

    // T uniform in [AuthProbeMin, AuthProbeMax], fixed (no doubling). sdk-net has no polling
    // mode (no SSE means no background refresh), so the max(T, poll interval) clause never applies.
    private TimeSpan AuthProbeWait()
    {
        double span = (AuthProbeMax - AuthProbeMin).TotalMilliseconds;
        return AuthProbeMin + TimeSpan.FromMilliseconds(_rng.NextDouble() * span);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Layer 2 must survive any callback exception.")]
    private async Task SafeAuthCallbackAsync(bool entered)
    {
        if (_onAuthFailed is null) return;
        try
        {
            await _onAuthFailed(entered).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "quonfig: fallback poller onAuthFailed callback threw: {Message}", ex.Message);
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
    /// <param name="baselineConnects">The <c>_connectTransitions</c> count at the same decision:
    /// a connect that was undone by a disconnect before this wait re-checked still ends it, so
    /// the threshold restarts from the latest disconnect (qfg-goi1.10.41).</param>
    /// <param name="stop">Stop token; cancellation unblocks the wait.</param>
    /// <returns><c>true</c> when the wait ran to its deadline, <c>false</c> when an SSE edge
    /// ended it early.</returns>
    private async Task<bool> DelayWithPulseAsync(TimeSpan duration, bool baseline, long baselineConnects, CancellationToken stop)
    {
        if (duration <= TimeSpan.Zero) return true;
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
                    // edge — re-evaluate.
                    if (_sseConnected != baseline || _connectTransitions != baselineConnects) return false;
                    edge = _edgeSignal.Task;
                }
                var done = await Task.WhenAny(edge, deadline).ConfigureAwait(false);
                if (done == deadline)
                {
                    // Throws OperationCanceledException if the stop token fired.
                    await deadline.ConfigureAwait(false);
                    return true;
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

    private TimeSpan Hooked(string? kind, TimeSpan requested)
    {
        var hook = WaitHook;
        return kind is null || hook is null ? requested : hook(kind, requested);
    }

    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Layer 2 must survive any fetch exception — supervisor counts the restart only at the layer-1 boundary.")]
    private async Task<PollOutcome> SafeFetchAsync(CancellationToken stop)
    {
        try
        {
            return await _fetch(stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Quiet on shutdown.
            return PollOutcome.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "quonfig: fallback poller fetch threw: {Message}", ex.Message);
            return PollOutcome.Failed;
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
