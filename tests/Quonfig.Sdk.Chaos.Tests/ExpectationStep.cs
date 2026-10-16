namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// Per-expectation progress through one chaos run, shared by the default suite loop
/// (<see cref="ChaosTests"/>) and the failover/ordering loop (<see cref="FailoverChaosTests"/>).
/// </summary>
internal sealed class ExpectationTimeline
{
    public ExpectationTimeline(ChaosScenario.Expectation exp) { Exp = exp; }

    public ChaosScenario.Expectation Exp { get; }

    /// <summary>Elapsed ms when the current (unbroken) hold started; null when not holding.</summary>
    public long? HoldStartMs { get; set; }

    /// <summary>Elapsed ms when the hold that produced PASS started; -1 until PASS.</summary>
    public long HitAtMs { get; set; } = -1;

    public bool Passed { get; set; }
    public bool Failed { get; set; }
    public bool Skipped { get; set; }

    public bool Terminal => Passed || Failed || Skipped;
}

/// <summary>
/// The pure per-tick step of the chaos expectation loop, implementing the chaos README
/// "Timeline rules (every harness)". Extracted from the two run loops so the timing contract is
/// unit-testable without a chaos rig.
/// </summary>
internal static class ExpectationStep
{
    /// <summary>
    /// Advance <paramref name="t"/> by one sample at <paramref name="elapsedMs"/> from t=0.
    /// <paramref name="evaluate"/> samples the assertion; it is not called while the expectation
    /// is gated by <c>after_ms</c> (except for a shape-skipped row, whose evaluation reads no
    /// probe state). Returns true when the expectation is terminal after the step.
    /// </summary>
    public static bool Apply(ExpectationTimeline t, long elapsedMs, System.Func<ExpressionEvaluator.Verdict> evaluate)
    {
        if (t.Terminal) return true;
        // Rule 1: SKIPPED depends on the expression's shape, not time, so after_ms does not
        // affect it. Evaluate once so the caller can record the skip notes.
        if (ExpressionEvaluator.IsShapeSkipped(t.Exp.AssertExpr))
        {
            evaluate();
            t.Skipped = true;
            return true;
        }
        // Rule 1: before after_ms the expectation is not evaluated at all: no PASS, no hold
        // start, not terminal. within_ms is unchanged (the validator keeps after_ms < within_ms).
        if (elapsedMs < t.Exp.AfterMs) return false;
        var v = evaluate();
        if (v == ExpressionEvaluator.Verdict.Skipped)
        {
            t.Skipped = true;
            return true;
        }
        if (v == ExpressionEvaluator.Verdict.Pass)
        {
            t.HoldStartMs ??= elapsedMs;
            if (elapsedMs - t.HoldStartMs.Value >= t.Exp.MustHoldForMs)
            {
                t.Passed = true;
                t.HitAtMs = t.HoldStartMs.Value;
            }
        }
        else
        {
            t.HoldStartMs = null;
        }
        if (!t.Passed && elapsedMs > t.Exp.WithinMs) t.Failed = true;
        return t.Terminal;
    }

    /// <summary>End of the wall clock: anything not PASS or SKIPPED is FAIL.</summary>
    public static void Finish(ExpectationTimeline t)
    {
        if (!t.Passed && !t.Skipped) t.Failed = true;
    }
}
