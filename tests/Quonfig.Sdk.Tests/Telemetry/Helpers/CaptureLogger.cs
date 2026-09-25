using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Quonfig.Sdk.Tests.Telemetry.Helpers;

using LogLevel = Microsoft.Extensions.Logging.LogLevel;

/// <summary>An <see cref="ILogger"/> at every level that records <c>(level, message)</c> lines.</summary>
internal sealed class CaptureLogger : ILogger
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, string Message)> _lines = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Lines
    {
        get { lock (_gate) return _lines.ToList(); }
    }

    public void Clear()
    {
        lock (_gate) _lines.Clear();
    }

    /// <summary>The contract's <c>log_count(level, /re/)</c>.</summary>
    public int LogCount(LogLevel level, string pattern = ".*")
    {
        var re = new Regex(pattern, RegexOptions.IgnoreCase);
        return Lines.Count(l => l.Level == level && re.IsMatch(l.Message));
    }

    public string First(LogLevel level) => Lines.First(l => l.Level == level).Message;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate) _lines.Add((logLevel, formatter(state, exception)));
    }
}
