using Xunit;
using V = Quonfig.Sdk.Chaos.Tests.ExpressionEvaluator.Verdict;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// Pins the chaos README "Timeline rules (every harness)" on the extracted per-tick step:
/// after_ms gating (rule 1), hold reset on a false sample, FAIL past within_ms, and shape-based
/// SKIPPED for a whole-expression server_metric row.
/// </summary>
public class ExpectationStepTests
{
    private static ExpectationTimeline T(int within, int hold = 0, int after = 0, string expr = "client.processStillAlive() == true") =>
        new(new ChaosScenario.Expectation { WithinMs = within, MustHoldForMs = hold, AfterMs = after, AssertExpr = expr });

    [Fact]
    public void AfterMsGatesAnAlreadyTrueExpectation()
    {
        var t = T(within: 5000, after: 1000);
        var calls = 0;
        V Always() { calls++; return V.Pass; }

        Assert.False(ExpectationStep.Apply(t, 0, Always));
        Assert.False(ExpectationStep.Apply(t, 999, Always));
        Assert.False(t.Passed);
        Assert.Null(t.HoldStartMs);
        Assert.Equal(0, calls);

        Assert.True(ExpectationStep.Apply(t, 1000, Always));
        Assert.True(t.Passed);
        Assert.Equal(1000, t.HitAtMs);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AfterMsHoldStartsAtOrAfterTheGate()
    {
        var t = T(within: 10000, hold: 2000, after: 1000);
        V Always() => V.Pass;
        ExpectationStep.Apply(t, 500, Always);
        Assert.Null(t.HoldStartMs);
        ExpectationStep.Apply(t, 1200, Always);
        Assert.Equal(1200, t.HoldStartMs);
        ExpectationStep.Apply(t, 3000, Always);
        Assert.False(t.Passed);
        ExpectationStep.Apply(t, 3200, Always);
        Assert.True(t.Passed);
        Assert.Equal(1200, t.HitAtMs);
    }

    [Fact]
    public void AfterMsThenFalseFailsPastWithin()
    {
        var t = T(within: 3000, after: 1000);
        V Never() => V.Fail;
        ExpectationStep.Apply(t, 1500, Never);
        Assert.False(t.Terminal);
        ExpectationStep.Apply(t, 3001, Never);
        Assert.True(t.Failed);
    }

    [Fact]
    public void FalseSampleResetsHoldAndUnfinishedHoldFailsPastWithin()
    {
        var t = T(within: 5000, hold: 3000);
        ExpectationStep.Apply(t, 0, () => V.Pass);
        Assert.Equal(0, t.HoldStartMs);
        ExpectationStep.Apply(t, 2000, () => V.Fail);
        Assert.Null(t.HoldStartMs);
        ExpectationStep.Apply(t, 2500, () => V.Pass);
        Assert.Equal(2500, t.HoldStartMs);
        ExpectationStep.Apply(t, 5001, () => V.Pass);
        Assert.False(t.Passed);
        Assert.True(t.Failed);
    }

    [Fact]
    public void ServerMetricWithAfterMsIsSkippedNotGated()
    {
        var t = T(within: 5000, after: 1000, expr: "server_metric('quonfig_sse_connections') >= 1");
        Assert.True(ExpectationStep.Apply(t, 0, () => V.Skipped));
        Assert.True(t.Skipped);
        ExpectationStep.Finish(t);
        Assert.False(t.Failed);
    }

    [Fact]
    public void GatedExpectationNeverReachedFailsAtFinish()
    {
        var t = T(within: 5000, after: 1000);
        ExpectationStep.Apply(t, 0, () => V.Pass);
        ExpectationStep.Finish(t);
        Assert.True(t.Failed);
    }
}
