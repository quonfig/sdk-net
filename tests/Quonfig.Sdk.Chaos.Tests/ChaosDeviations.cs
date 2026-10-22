using System;
using System.Collections.Generic;
using System.Linq;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// Scenario rows sdk-net cannot meet as written, each with its reason and the bead that owns the
/// decision (qfg-6vno.4). A listed row is reported SKIPPED with a <c>DEVIATION:</c> reason, never
/// PASS, and lands in the run-end skipped-expressions tally. A deviation whose row is no longer in
/// the scenario fails the run, so the list cannot go stale silently.
/// </summary>
internal static class ChaosDeviations
{
    internal sealed record Deviation(string ScenarioPrefix, string Assert, string Reason);

    // Empty since qfg-6vno.21: scenario 10's two rows (callback throw counted in
    // callback_errors_total and logged at Error) now run for real. Add a row here only with its
    // reason and owning bead.
    internal static readonly IReadOnlyList<Deviation> All = Array.Empty<Deviation>();

    public static Deviation? Find(string? scenarioName, string assertExpr, IReadOnlyList<Deviation>? list = null) =>
        (list ?? All).FirstOrDefault(d => Applies(d, scenarioName) && d.Assert == (assertExpr ?? string.Empty).Trim());

    /// <summary>Throws when a deviation for this scenario matches none of its rows.</summary>
    public static void AssertAllMatched(string? scenarioName, IReadOnlyList<ChaosScenario.Expectation> rows, IReadOnlyList<Deviation>? list = null)
    {
        foreach (var d in (list ?? All).Where(d => Applies(d, scenarioName)))
        {
            if (!rows.Any(r => (r.AssertExpr ?? string.Empty).Trim() == d.Assert))
            {
                throw new InvalidOperationException(
                    $"stale chaos deviation: scenario \"{scenarioName}\" has no row \"{d.Assert}\"; update ChaosDeviations.cs");
            }
        }
    }

    private static bool Applies(Deviation d, string? scenarioName) =>
        (scenarioName ?? string.Empty).StartsWith(d.ScenarioPrefix, StringComparison.Ordinal);
}
