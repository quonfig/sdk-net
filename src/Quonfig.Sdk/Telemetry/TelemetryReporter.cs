using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Quonfig.Sdk.Telemetry;

/// <summary>
/// Drives periodic telemetry submission for a <see cref="Quonfig"/> client.
///
/// <para>Once per flush interval (60s by default) the reporter drains the collectors, serializes the
/// window once, and hands the bytes to a retained queue that implements the telemetry transport
/// policy (qfg-y8je.10): one POST in flight; a failed batch (timeout, network error, 408, 429, 5xx)
/// is kept byte-for-byte and resent unchanged no sooner than 30s later and after any
/// <c>Retry-After</c> (up to 10 min); at most 5 batches / 2MB for 5 min; 401/403/404 disable
/// telemetry for the process; any other 4xx drops that batch. On disposal the live window gets one
/// POST with a 5s deadline and the retained queue is not drained. Mirrors sdk-node's reporter.</para>
/// </summary>
public sealed class TelemetryReporter : IAsyncDisposable
{
    private readonly string _instanceHash;
    private readonly EvaluationSummaryCollector _summaries;
    private readonly ContextShapeCollector _shapes;
    private readonly ExampleContextCollector _examples;
    private readonly FailoverCollector? _failover;
    private readonly ILogger _logger;
    private readonly ITelemetryClock _clock;
    private readonly TelemetryTransportQueue _queue;

    private readonly object _gate = new();
    private IDisposable? _timer;
    private bool _started;
    private bool _closed;
    private bool _tickRunning;
    private Task _currentTick = Task.CompletedTask;
    private Task? _closeTask;

    /// <summary>
    /// Initializes a reporter. <see cref="Start"/> must be called to begin periodic flushes.
    /// <paramref name="failover"/> is optional; when supplied its counters ride the same envelope as
    /// the eval/context collectors (qfg-41nh.18).
    ///
    /// <para>Since 1.3.0 <paramref name="baseInterval"/> is the fixed flush interval (a non-positive
    /// value falls back to 60s); <paramref name="initialDelay"/> and <paramref name="maxInterval"/> are
    /// ignored because the adaptive backoff was replaced by the transport policy.</para>
    /// </summary>
    public TelemetryReporter(
        ITelemetrySender sender,
        string instanceHash,
        EvaluationSummaryCollector summaries,
        ContextShapeCollector shapes,
        ExampleContextCollector examples,
        TimeSpan initialDelay,
        TimeSpan baseInterval,
        TimeSpan maxInterval,
        ILogger? logger = null,
        FailoverCollector? failover = null)
        : this(
            ToTransport(sender),
            instanceHash,
            summaries,
            shapes,
            examples,
            failover,
            logger,
            new TelemetryReporterSettings(flushInterval: baseInterval),
            clock: null)
    {
        _ = initialDelay;
        _ = maxInterval;
    }

    internal TelemetryReporter(
        ITelemetryTransport transport,
        string instanceHash,
        EvaluationSummaryCollector summaries,
        ContextShapeCollector shapes,
        ExampleContextCollector examples,
        FailoverCollector? failover,
        ILogger? logger,
        TelemetryReporterSettings settings,
        ITelemetryClock? clock)
    {
        _instanceHash = instanceHash ?? throw new ArgumentNullException(nameof(instanceHash));
        _summaries = summaries ?? throw new ArgumentNullException(nameof(summaries));
        _shapes = shapes ?? throw new ArgumentNullException(nameof(shapes));
        _examples = examples ?? throw new ArgumentNullException(nameof(examples));
        _failover = failover;
        _logger = logger ?? NullLogger.Instance;
        _clock = clock ?? SystemTelemetryClock.Instance;
        Settings = settings;
        _queue = new TelemetryTransportQueue(
            transport,
            _logger,
            _clock,
            settings.Timeout,
            settings.MaxRetainedBatches,
            settings.MaxRetainedBytes,
            settings.MaxRetainedAge,
            OnDisabled);
    }

    /// <summary>
    /// The flush interval. Before 1.3.0 this grew on failure (adaptive backoff); it is now fixed, and
    /// failures are paced by the 30s resend floor and <c>Retry-After</c> instead.
    /// </summary>
    public TimeSpan CurrentInterval => Settings.FlushInterval;

    /// <summary>True after <see cref="DisposeAsync"/> has been called.</summary>
    public bool IsClosed
    {
        get { lock (_gate) return _closed; }
    }

    /// <summary>Resolved transport settings; the contract tests read the shipped defaults here.</summary>
    internal TelemetryReporterSettings Settings { get; }

    /// <summary>The contract's <c>retained_count</c>.</summary>
    internal int RetainedCount => _queue.RetainedCount;

    /// <summary>The contract's <c>retained_bytes</c>.</summary>
    internal long RetainedBytes => _queue.RetainedBytes;

    /// <summary>The contract's <c>telemetry_enabled()</c>: false once a 401/403/404 disabled telemetry.</summary>
    internal bool TelemetryEnabled => !_queue.Disabled;

    /// <summary>A telemetry POST is in flight.</summary>
    internal bool InFlight => _queue.Busy;

    /// <summary>The tick timer is armed.</summary>
    internal bool TimerActive
    {
        get { lock (_gate) return _timer is not null; }
    }

    /// <summary>
    /// Starts the tick timer. Fixed cadence: tick k fires at k × the flush interval regardless of how
    /// long a drain takes. Idempotent; no-op when closed or disabled.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_closed || _started) return;
            _started = true;
        }
        Schedule();
    }

    /// <summary>
    /// Sends the live window now: waits for an in-flight POST first, then runs a tick, so after a
    /// failure it respects the 30s floor and <c>Retry-After</c>. Never throws on transport failure
    /// (before 1.3.0 it did); throws <see cref="OperationCanceledException"/> only when
    /// <paramref name="cancellationToken"/> is already cancelled.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsClosed || _queue.Disabled) return;
        await WhenIdleAsync().ConfigureAwait(false);
        await TickAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Deprecated since 1.3.0: use <see cref="FlushAsync"/>. Runs <see cref="FlushAsync"/> and returns
    /// <c>false</c> while telemetry POSTs are failing, <c>true</c> otherwise. The interval no longer
    /// changes.
    /// </summary>
    public async Task<bool> FlushAndApplyBackoffAsync(CancellationToken cancellationToken)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        return !_queue.Failing;
    }

    /// <summary>
    /// Shutdown (P8): stops the timer, aborts any in-flight POST, then gives the live window one POST
    /// with a 5s deadline. The retained queue is not drained, and the call returns within the deadline
    /// even against a hanging endpoint. A second call is a no-op.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Task close;
        lock (_gate)
        {
            if (_closed) return new ValueTask(_closeTask ?? Task.CompletedTask);
            _closed = true;
            _timer?.Dispose();
            _timer = null;
        }
        close = CloseAsync();
        lock (_gate) _closeTask = close;
        return new ValueTask(close);
    }

    /// <summary>
    /// One tick of the contract's model: skip if closed, disabled or a tick (and so a POST) is still
    /// running (P2; the live window keeps aggregating); expire aged batches; skip if the 30s floor or
    /// Retry-After has not elapsed; serialize the live window once and append it; drain oldest-first.
    /// </summary>
    internal Task TickAsync()
    {
        lock (_gate)
        {
            if (_closed || _tickRunning || _queue.Disabled) return Task.CompletedTask;
            _tickRunning = true;
        }
        var run = RunTickAsync();
        lock (_gate)
        {
            if (_tickRunning) _currentTick = run;
        }
        return run;
    }

    /// <summary>Resolves when no tick (and so no POST) is running.</summary>
    internal async Task WhenIdleAsync()
    {
        for (; ; )
        {
            Task t;
            lock (_gate)
            {
                if (!_tickRunning) return;
                t = _currentTick;
            }
            await t.ConfigureAwait(false);
            await Task.Yield();
        }
    }

    private async Task RunTickAsync()
    {
        try
        {
            _queue.Expire();
            if (!_queue.SendAllowed()) return;
            var batch = SerializeWindow();
            if (batch is not null) _queue.Append(batch);
            await _queue.DrainAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // telemetry never throws into the host
        catch (Exception e)
#pragma warning restore CA1031
        {
            _logger.LogDebug(e, "Telemetry tick failed: {Error}", e.Message);
        }
        finally
        {
            lock (_gate) _tickRunning = false;
        }
    }

    private async Task CloseAsync()
    {
        _queue.AbortInFlight();
        try
        {
            if (_queue.Disabled) return;
            var batch = SerializeWindow();
            if (batch is null) return;
            var deadline = TelemetryDefaults.ShutdownFlushDeadline < Settings.Timeout
                ? TelemetryDefaults.ShutdownFlushDeadline
                : Settings.Timeout;
            await _queue.SendFinalAsync(batch, deadline).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception e)
#pragma warning restore CA1031
        {
            _logger.LogDebug(e, "Telemetry final flush failed: {Error}", e.Message);
        }
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_closed || _queue.Disabled) return;
            _timer = _clock.Schedule(Settings.FlushInterval, Fire);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_closed) return;
        }
        Schedule();
        _ = TickAsync();
    }

    private void OnDisabled()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
        _summaries.Disable();
        _shapes.Disable();
        _examples.Disable();
        _failover?.Disable();
    }

    /// <summary>
    /// Drains the collectors into one serialized window. This is the only serialization: the queue
    /// stores and resends these exact bytes (P5, P9).
    /// </summary>
    private TelemetryBatch? SerializeWindow()
    {
        var events = new List<IDictionary<string, object?>>(4);
        var s = _summaries.Drain();
        if (s is not null) events.Add(s);
        var sh = _shapes.Drain();
        if (sh is not null) events.Add(sh);
        var ex = _examples.Drain();
        if (ex is not null) events.Add(ex);
        var fo = _failover?.Drain();
        if (fo is not null) events.Add(fo);
        if (events.Count == 0) return null;

        var payload = new Dictionary<string, object?>
        {
            ["instanceHash"] = _instanceHash,
            ["events"] = events,
        };
        return new TelemetryBatch(JsonSerializer.SerializeToUtf8Bytes(payload), payload);
    }

    private static ITelemetryTransport ToTransport(ITelemetrySender sender)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(sender);
#else
        if (sender is null) throw new ArgumentNullException(nameof(sender));
#endif
        return sender as ITelemetryTransport ?? new TelemetrySenderTransport(sender);
    }
}
