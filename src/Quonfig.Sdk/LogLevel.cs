using System;
using System.Globalization;

namespace Quonfig.Sdk;

/// <summary>
/// Quonfig-internal log level enum, used by <c>Quonfig.GetLogLevel</c> and the
/// <c>Quonfig.Sdk.Extensions.Logging</c> / <c>Quonfig.Sdk.Serilog</c> filter packages to map
/// between Quonfig's log-level config payload and each logging library's level type.
///
/// Order is most-severe-first; the integer value is suitable for "is at least as severe"
/// comparisons. Mirrors the canonical level set in sdk-java, sdk-node, sdk-go, and sdk-ruby.
/// </summary>
public enum LogLevel
{
    /// <summary>FATAL — highest severity.</summary>
    Fatal = 0,

    /// <summary>ERROR.</summary>
    Error = 1,

    /// <summary>WARN.</summary>
    Warn = 2,

    /// <summary>INFO.</summary>
    Info = 3,

    /// <summary>DEBUG.</summary>
    Debug = 4,

    /// <summary>TRACE — lowest severity.</summary>
    Trace = 5,
}

/// <summary>
/// Helpers for <see cref="LogLevel"/>. Kept as a separate static class so the enum type itself
/// stays a plain CLI enum (and so this compiles cleanly on netstandard2.0).
/// </summary>
public static class LogLevels
{
    /// <summary>
    /// Parses the string representation of a level emitted by api-delivery / datadir log-level
    /// configs. Matches case-insensitively. Returns <c>null</c> for unknown / null input —
    /// callers should treat that as "no opinion".
    /// </summary>
    public static LogLevel? FromString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return Enum.TryParse<LogLevel>(s, ignoreCase: true, out var lvl) ? lvl : (LogLevel?)null;
    }
}
