using global::Serilog.Events;
using QLogLevel = Quonfig.Sdk.LogLevel;

namespace Quonfig.Sdk.Serilog;

/// <summary>
/// Maps between cross-SDK <see cref="QLogLevel"/> wire-format levels and
/// <see cref="LogEventLevel"/> values used by Serilog. Mirrors sdk-java's
/// <c>LogbackLevelMapper</c> / <c>Log4jLevelMapper</c>.
/// </summary>
internal static class SerilogLevelMapper
{
    /// <summary>Maps a Quonfig level to the closest Serilog level.</summary>
    public static LogEventLevel ToSerilog(QLogLevel q) => q switch
    {
        QLogLevel.Fatal => LogEventLevel.Fatal,
        QLogLevel.Error => LogEventLevel.Error,
        QLogLevel.Warn => LogEventLevel.Warning,
        QLogLevel.Info => LogEventLevel.Information,
        QLogLevel.Debug => LogEventLevel.Debug,
        QLogLevel.Trace => LogEventLevel.Verbose,
        _ => LogEventLevel.Information,
    };
}
