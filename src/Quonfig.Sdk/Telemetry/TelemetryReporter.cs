using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Quonfig.Sdk.Telemetry;

/// <summary>
/// Drives periodic telemetry submission for a <see cref="Quonfig"/> client.
///
/// <para>Starts a background <see cref="Task"/> that flushes pending collectors every
/// <c>baseInterval</c>; on transport failure the interval grows by ×1.5 (capped at
/// <c>maxInterval</c>) and resets to <c>baseInterval</c> on the next successful send. On
/// disposal a final flush is attempted synchronously. Mirrors sdk-java's
/// <c>TelemetryReporter</c>.</para>
/// </summary>
public sealed class TelemetryReporter : IAsyncDisposable
{
    private readonly ITelemetrySender _sender;
    private readonly string _instanceHash;
    private readonly EvaluationSummaryCollector _summaries;
    private readonly ContextShapeCollector _shapes;
    private readonly ExampleContextCollector _examples;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _baseInterval;
    private readonly TimeSpan _maxInterval;
    private readonly ILogger _logger;

    private readonly object _gate = new();
    private TimeSpan _currentInterval;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _disposed;

    /// <summary>Initializes a reporter. <see cref="Start"/> must be called to begin periodic flushes.</summary>
    public TelemetryReporter(
        ITelemetrySender sender,
        string instanceHash,
        EvaluationSummaryCollector summaries,
        ContextShapeCollector shapes,
        ExampleContextCollector examples,
        TimeSpan initialDelay,
        TimeSpan baseInterval,
        TimeSpan maxInterval,
        ILogger? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _instanceHash = instanceHash ?? throw new ArgumentNullException(nameof(instanceHash));
        _summaries = summaries ?? throw new ArgumentNullException(nameof(summaries));
        _shapes = shapes ?? throw new ArgumentNullException(nameof(shapes));
        _examples = examples ?? throw new ArgumentNullException(nameof(examples));
        _initialDelay = initialDelay;
        _baseInterval = baseInterval;
        _maxInterval = maxInterval;
        _currentInterval = baseInterval;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Current backoff interval (resets to <c>baseInterval</c> after a successful send).</summary>
    public TimeSpan CurrentInterval
    {
        get { lock (_gate) return _currentInterval; }
    }

    /// <summary>True after <see cref="DisposeAsync"/> has been awaited.</summary>
    public bool IsClosed { get { lock (_gate) return _disposed; } }

    /// <summary>Spins up the background flush loop. Idempotent; no-op when already started.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _runTask is not null) return;
            _cts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunLoopAsync(_cts.Token));
        }
    }

    /// <summary>
    /// Drains pending collectors and posts a single envelope. Returns silently when no events
    /// are pending. Throws on transport failure so callers can surface the error.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var envelope = BuildEnvelope();
        if (envelope is null) return;
        await _sender.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drains, sends, and updates <see cref="CurrentInterval"/>. Returns <c>true</c> on success
    /// (including the empty-no-op case), <c>false</c> when a send was attempted and threw.
    /// </summary>
    public async Task<bool> FlushAndApplyBackoffAsync(CancellationToken cancellationToken)
    {
        var envelope = BuildEnvelope();
        if (envelope is null)
        {
            lock (_gate) _currentInterval = _baseInterval;
            return true;
        }
        try
        {
            await _sender.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
            lock (_gate) _currentInterval = _baseInterval;
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lock (_gate)
            {
                long grownMs = (long)(_currentInterval.TotalMilliseconds * 1.5);
                if (grownMs <= (long)_currentInterval.TotalMilliseconds)
                {
                    grownMs = (long)_currentInterval.TotalMilliseconds + 1;
                }
                long nextMs = Math.Min(grownMs, (long)_maxInterval.TotalMilliseconds);
                _currentInterval = TimeSpan.FromMilliseconds(nextMs);
#pragma warning disable CA1031, CA2254
                _logger.LogWarning("telemetry sync failed; backing off to {Backoff}ms: {Error}", nextMs, e.Message);
#pragma warning restore CA1031, CA2254
            }
            return false;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Task? toAwait;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            toAwait = _runTask;
            _cts?.Cancel();
        }

        // Final synchronous flush before exit.
        try
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception e)
        {
            _logger.LogWarning("final telemetry flush failed: {Error}", e.Message);
        }
#pragma warning restore CA1031

        if (toAwait is not null)
        {
            try
            {
                await toAwait.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
#pragma warning disable CA1031
            catch (Exception e)
            {
                _logger.LogWarning("telemetry reporter loop exited with error: {Error}", e.Message);
            }
#pragma warning restore CA1031
        }

        _cts?.Dispose();
    }

    private IDictionary<string, object?>? BuildEnvelope()
    {
        var events = new List<IDictionary<string, object?>>(3);
        var s = _summaries.Drain();
        if (s is not null) events.Add(s);
        var sh = _shapes.Drain();
        if (sh is not null) events.Add(sh);
        var ex = _examples.Drain();
        if (ex is not null) events.Add(ex);
        if (events.Count == 0) return null;

        return new Dictionary<string, object?>
        {
            ["instanceHash"] = _instanceHash,
            ["events"] = events,
        };
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            if (_initialDelay > TimeSpan.Zero)
            {
                await Task.Delay(_initialDelay, ct).ConfigureAwait(false);
            }
            while (!ct.IsCancellationRequested)
            {
                await FlushAndApplyBackoffAsync(ct).ConfigureAwait(false);
                TimeSpan delay;
                lock (_gate) delay = _currentInterval;
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
#pragma warning disable CA1031
        catch (Exception e)
        {
            _logger.LogWarning("telemetry reporter loop crashed: {Error}", e.Message);
        }
#pragma warning restore CA1031
    }
}
