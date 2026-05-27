using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Quonfig.Sdk.Extensions.Logging;

/// <summary>
/// Per-category logger returned by <see cref="QuonfigLoggerProvider"/>. Consults the
/// wrapped <see cref="IQuonfig"/> instance on each <c>IsEnabled</c>/<c>Log</c> call and
/// gates the fan-out to wrapped inner provider loggers accordingly.
///
/// <para>Semantics — matches sdk-java's turbo filter:
/// <list type="bullet">
///   <item><description>Quonfig has no opinion → defer to inner loggers' own <c>IsEnabled</c>.</description></item>
///   <item><description>Quonfig allows (MEL level &gt;= configured floor) → permit; fan out to inner loggers.</description></item>
///   <item><description>Quonfig denies (MEL level &lt; configured floor) → block, regardless of inner.</description></item>
/// </list>
/// </para>
/// </summary>
internal sealed class QuonfigLogger : ILogger
{
    // AsyncLocal: an evaluation triggered by Quonfig itself (e.g. the SDK's own ILogger
    // emits a log record) would re-enter the filter and stack-overflow. The flag breaks
    // the cycle by treating in-eval calls as "no opinion" → defer to inner.
    private static readonly AsyncLocal<bool> InEval = new();

    private readonly string _category;
    private readonly IQuonfig _quonfig;
    private readonly ILogger[] _inner;

    public QuonfigLogger(string category, IQuonfig quonfig, ILogger[] inner)
    {
        _category = category;
        _quonfig = quonfig;
        _inner = inner;
    }

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        if (_inner.Length == 0) return NullScope.Instance;
        var scopes = new IDisposable?[_inner.Length];
        for (int i = 0; i < _inner.Length; i++)
        {
            scopes[i] = _inner[i].BeginScope(state);
        }
        return new CompositeScope(scopes);
    }

    /// <inheritdoc/>
    public bool IsEnabled(MelLogLevel logLevel)
    {
        if (logLevel == MelLogLevel.None) return false;
        if (InEval.Value) return AnyInnerEnabled(logLevel);

        InEval.Value = true;
        try
        {
            Sdk.LogLevel? configured;
            try
            {
                configured = _quonfig.GetLogLevel(_category);
            }
#pragma warning disable CA1031 // do not catch general types — Quonfig filter must not break logging
            catch
            {
                return AnyInnerEnabled(logLevel);
            }
#pragma warning restore CA1031
            if (configured is null) return AnyInnerEnabled(logLevel);
            var floor = QuonfigLevelMapper.ToMel(configured.Value);
            return logLevel >= floor;
        }
        finally
        {
            InEval.Value = false;
        }
    }

    /// <inheritdoc/>
    public void Log<TState>(
        MelLogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        for (int i = 0; i < _inner.Length; i++)
        {
            try
            {
                _inner[i].Log(logLevel, eventId, state, exception, formatter);
            }
#pragma warning disable CA1031 // never let one provider's throw take down the others
            catch
            {
                // swallowed — sibling providers must continue
            }
#pragma warning restore CA1031
        }
    }

    private bool AnyInnerEnabled(MelLogLevel level)
    {
        for (int i = 0; i < _inner.Length; i++)
        {
            if (_inner[i].IsEnabled(level)) return true;
        }
        return false;
    }

    private sealed class CompositeScope : IDisposable
    {
        private readonly IDisposable?[] _scopes;
        private int _disposed;
        public CompositeScope(IDisposable?[] scopes) => _scopes = scopes;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var s in _scopes)
            {
                try { s?.Dispose(); }
#pragma warning disable CA1031
                catch { /* swallow */ }
#pragma warning restore CA1031
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        private NullScope() { }
        public void Dispose() { }
    }
}
