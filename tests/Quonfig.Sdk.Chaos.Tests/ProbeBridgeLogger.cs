using System;
using Microsoft.Extensions.Logging;
// Disambiguate: Quonfig.Sdk.LogLevel is a customer-facing enum (Fatal..Trace) that shadows
// Microsoft's logging enum (Trace..None) when this file's namespace is rooted under Quonfig.Sdk.
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// <see cref="ILogger"/> that forwards every SDK log call into <see cref="ChaosProbe.Log"/> so
/// log-matching scenario expectations (e.g. scenario 10 looking for <c>/callback|onConfigUpdate/i</c>)
/// can observe what the SDK actually wrote.
///
/// <para>Additionally infers Layer 1 SSE restart events from log messages emitted by
/// <see cref="Sdk.Transport.SseClient"/> when the watchdog disposes the body or when the
/// transport throws. sdk-net does not expose an SSE connection-state callback today; this
/// log-driven proxy is the chaos runner's only signal. The patterns are pinned to the literal
/// messages SseClient emits — if the SDK rewords its logs, the chaos probe needs updating.</para>
/// </summary>
internal sealed class ProbeBridgeLogger : ILogger
{
    private readonly ChaosProbe _probe;

    public ProbeBridgeLogger(ChaosProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(global::Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        global::Microsoft.Extensions.Logging.LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (formatter is null) return;
        var message = formatter(state, exception);
        if (exception is not null)
        {
            message = message + " | " + exception;
        }
        var levelText = logLevel.ToString().ToLowerInvariant();
        _probe.Log(levelText, message);

        // Distinguish two flavors of "Layer 1 worker restart" the SDK logs today, because they
        // have different observability consequences for the chaos probe:
        //   * SSE drop  — SSE connection ended unexpectedly, state should flip to "reconnecting".
        //   * Callback throw — user code threw inside a callback, SDK caught and recovered;
        //     state stays "connected" (the SSE worker never actually disconnected).
        // Both increment worker_restart_total layer=1; only the first moves connectionState.
        if (IsSseDropSignal(message))
        {
            _probe.RecordSseDrop();
        }
        else if (IsCallbackThrowSignal(message))
        {
            _probe.IncRestartLayer1();
        }
    }

    /// <summary>
    /// SSE-worker restart signals: connection ended, refused, or timed out. The SDK logs these
    /// via <see cref="Sdk.Transport.SseClient"/>; if it ever wires a real connection-state
    /// callback the probe can drop this scrape.
    /// </summary>
    private static bool IsSseDropSignal(string message)
    {
        if (message.Contains("read watchdog fired", StringComparison.OrdinalIgnoreCase)) return true;
        if (message.Contains("SSE: transport error", StringComparison.OrdinalIgnoreCase)) return true;
        if (message.Contains("SSE: connect timeout", StringComparison.OrdinalIgnoreCase)) return true;
        if (message.Contains("SSE: non-200 status", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// User-callback throws caught at the SDK boundary. Counted toward <c>worker_restart_total</c>
    /// but NOT a connection-state transition. Scenario 10 leans on
    /// <c>OnConnectionStateChange handler threw</c> because sdk-net has no <c>OnConfigUpdate</c>
    /// callback to throw from directly.
    /// </summary>
    private static bool IsCallbackThrowSignal(string message)
    {
        if (message.Contains("onEnvelope handler threw", StringComparison.OrdinalIgnoreCase)) return true;
        if (message.Contains("OnConnectionStateChange handler threw", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
