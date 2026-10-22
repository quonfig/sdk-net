using System;
using Microsoft.Extensions.Logging;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// <see cref="ILogger"/> that forwards every SDK log call into <see cref="ChaosProbe.Log"/> so
/// <c>client.sdkLog(level, /re/)</c> expectations count what the SDK actually wrote, at the level
/// it wrote it. It infers nothing: metrics and states come from the SDK (qfg-6vno.4).
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
        _probe.Log(logLevel.ToString().ToLowerInvariant(), message);
    }
}
