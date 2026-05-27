using System.IO;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// Locates the shared <c>integration-test-data/chaos/scenarios/</c> directory relative to where
/// the test process is running. The chaos runner is invoked from the sdk-net repo root via
/// <c>scripts/run-chaos.sh</c>; on dev hosts the sibling layout is
/// <c>~/code/quonfig/{sdk-net,integration-test-data}/</c>; on CI the workflow checks out
/// <c>integration-test-data</c> under the workspace root.
/// </summary>
internal static class ChaosPaths
{
    public static string ScenariosDir()
    {
        var cwd = Directory.GetCurrentDirectory();
        // Walk up from the current directory, looking for a sibling integration-test-data.
        var dir = cwd;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, "..", "integration-test-data", "chaos", "scenarios"));
            if (Directory.Exists(candidate)) return candidate;
            var sibling = Path.GetFullPath(Path.Combine(dir, "integration-test-data", "chaos", "scenarios"));
            if (Directory.Exists(sibling)) return sibling;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        // Default to the sibling path even if it doesn't exist so test failure messages are
        // informative.
        return Path.GetFullPath(Path.Combine(cwd, "..", "integration-test-data", "chaos", "scenarios"));
    }
}
