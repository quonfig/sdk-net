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
            Monitor.PulseAll(_lock);
        }
    }

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
                lock (_lock)
                {
                    if (_sseConnected)
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
                        // No deadline while connected — wake on SetSseConnected via PulseAll.
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

                // Wait — woken early by SetSseConnected (Monitor.PulseAll) or by
                // ctx.StopToken via a registration on the wait. We use
                // Task.Delay with the stop token so cancellation unblocks us
                // immediately, then re-check state.
                try
                {
                    await DelayWithPulseAsync(wait, ctx.StopToken).ConfigureAwait(false);
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
    /// Waits up to <paramref name="duration"/> for either the deadline or a
    /// pulse from <see cref="SetSseConnected"/>. We poll on a short tick because
    /// <see cref="Monitor"/> can't await asynchronously; the tick is bounded by
    /// the requested duration so callers paying for a long sleep don't busy-loop.
    /// </summary>
    private async Task DelayWithPulseAsync(TimeSpan duration, CancellationToken stop)
    {
        if (duration <= TimeSpan.Zero) return;
        // Use a short polling tick so SetSseConnected edges arrive fast in tests
        // that don't want to wait the full interval. In production the tick is
        // capped by the requested duration anyway.
        var tick = duration < TimeSpan.FromMilliseconds(5)
            ? duration
            : TimeSpan.FromMilliseconds(5);
        var deadline = DateTime.UtcNow + duration;
        bool startedConnected;
        lock (_lock) { startedConnected = _sseConnected; }
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(tick, stop).ConfigureAwait(false);
            bool connectedNow;
            lock (_lock) { connectedNow = _sseConnected; }
            if (connectedNow != startedConnected) return; // edge — re-evaluate.
        }
    }

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
