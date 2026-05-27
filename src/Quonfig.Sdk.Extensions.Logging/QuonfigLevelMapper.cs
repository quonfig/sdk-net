using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;
using QLogLevel = Quonfig.Sdk.LogLevel;

namespace Quonfig.Sdk.Extensions.Logging;

/// <summary>
/// Maps between the cross-SDK <see cref="QLogLevel"/> wire-format levels and
/// <see cref="MelLogLevel"/> values used by Microsoft.Extensions.Logging.
/// Mirrors sdk-java's <c>LogbackLevelMapper</c> / <c>Log4jLevelMapper</c>.
/// </summary>
internal static class QuonfigLevelMapper
{
    /// <summary>Maps a Quonfig level to the closest MEL level.</summary>
    public static MelLogLevel ToMel(QLogLevel q) => q switch
    {
        QLogLevel.Fatal => MelLogLevel.Critical,
        QLogLevel.Error => MelLogLevel.Error,
        QLogLevel.Warn => MelLogLevel.Warning,
        QLogLevel.Info => MelLogLevel.Information,
        QLogLevel.Debug => MelLogLevel.Debug,
        QLogLevel.Trace => MelLogLevel.Trace,
        _ => MelLogLevel.Information,
    };
}
