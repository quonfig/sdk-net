using System.IO;
using System.Text;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// Records every expression leaf the chaos runner skipped, one tab-separated line per leaf
/// (<c>scenario, exp[i], expression, reason</c>), so <c>scripts/run-chaos.sh</c> can print a
/// run-end "skipped expressions" tally and write it to the GitHub job summary (qfg-goi1.1.5).
/// xUnit does not surface per-test output for passing tests at the CI log verbosity, so the
/// tally goes through a file the wrapper script reads after <c>dotnet test</c> exits.
/// The path comes from <c>CHAOS_SKIP_LOG</c>; without it, recording is a no-op.
/// </summary>
internal sealed class ChaosSkipLog
{
    private static readonly object Gate = new();
    private static readonly Encoding NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly string? _path;

    public ChaosSkipLog(string? path)
    {
        _path = string.IsNullOrEmpty(path) ? null : path;
    }

    public static ChaosSkipLog FromEnvironment() =>
        new(System.Environment.GetEnvironmentVariable("CHAOS_SKIP_LOG"));

    public void Record(string scenario, int idx, ExpressionEvaluator.SkipNote note)
    {
        if (_path is null) return;
        var line = scenario + "\texp[" + idx + "]\t" + note.Expr + "\t" + note.Reason + "\n";
        lock (Gate)
        {
            File.AppendAllText(_path, line, NoBom);
        }
    }
}
