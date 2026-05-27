using System;
using System.Collections.Concurrent;
using System.Threading;
using global::Serilog.Core;
using global::Serilog.Events;

namespace Quonfig.Sdk.Serilog;

/// <summary>
/// Exposes per-category <see cref="LoggingLevelSwitch"/> values that track
/// <see cref="IQuonfig.GetLogLevel"/> reactively. Customers wire the switches into their
/// Serilog configuration (root via <c>MinimumLevel.ControlledBy</c>, per-category via
/// <c>MinimumLevel.Override(prefix, sw)</c>); the provider re-evaluates every issued switch
/// on each <see cref="IQuonfig.OnConfigChange"/> event so Quonfig config edits propagate
/// to the running Serilog pipeline without re-wiring.
///
/// <para>Cross-SDK analog: sdk-java's logback turbo filter and log4j2 filter modules.</para>
/// </summary>
public sealed class QuonfigLoggingLevelSwitchProvider : IDisposable
{
    private readonly IQuonfig _quonfig;
    private readonly LogEventLevel _defaultLevel;
    private readonly ConcurrentDictionary<string, LoggingLevelSwitch> _switches =
        new(StringComparer.Ordinal);
    private readonly Action _onConfigChange;
    private int _disposed;

    /// <summary>
    /// Constructs a switch provider. <paramref name="defaultLevel"/> is applied when
    /// <see cref="IQuonfig.GetLogLevel"/> returns <c>null</c> (no rule matches).
    /// </summary>
    public QuonfigLoggingLevelSwitchProvider(
        IQuonfig quonfig,
        LogEventLevel defaultLevel = LogEventLevel.Information)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(quonfig);
#else
        if (quonfig is null) throw new ArgumentNullException(nameof(quonfig));
#endif
        _quonfig = quonfig;
        _defaultLevel = defaultLevel;

        _onConfigChange = RefreshAllSwitches;
        _quonfig.OnConfigChange += _onConfigChange;
    }

    /// <summary>
    /// Returns the level switch for <paramref name="category"/>, creating it on first
    /// access. The switch is immediately seeded from the current Quonfig configuration so
    /// callers can register it directly in their Serilog setup. Pass an empty string to
    /// get the root switch (suitable for <c>MinimumLevel.ControlledBy</c>).
    /// </summary>
    public LoggingLevelSwitch GetSwitch(string category)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(category);
#else
        if (category is null) throw new ArgumentNullException(nameof(category));
#endif
        return _switches.GetOrAdd(category, key =>
        {
            var sw = new LoggingLevelSwitch(_defaultLevel);
            sw.MinimumLevel = ResolveLevel(key);
            return sw;
        });
    }

    /// <summary>
    /// Re-evaluates every issued switch. Called automatically on
    /// <see cref="IQuonfig.OnConfigChange"/>; exposed for tests / manual triggers.
    /// </summary>
    public void RefreshAllSwitches()
    {
        foreach (var kv in _switches)
        {
            var level = ResolveLevel(kv.Key);
            if (kv.Value.MinimumLevel != level)
            {
                kv.Value.MinimumLevel = level;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _quonfig.OnConfigChange -= _onConfigChange;
    }

    private LogEventLevel ResolveLevel(string category)
    {
        Sdk.LogLevel? q;
        try
        {
            q = _quonfig.GetLogLevel(category);
        }
#pragma warning disable CA1031 // never break the logging pipeline
        catch
        {
            return _defaultLevel;
        }
#pragma warning restore CA1031
        return q is null ? _defaultLevel : SerilogLevelMapper.ToSerilog(q.Value);
    }
}
