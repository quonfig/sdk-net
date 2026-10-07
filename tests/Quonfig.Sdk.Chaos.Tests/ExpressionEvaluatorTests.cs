using System.Text.RegularExpressions;
using Xunit;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// Unit tests for the assertion grammar evaluator. Pin the operators, the OR/AND short-circuit
/// order, and the regex/quote-safe splitter so the chaos runner can rely on it without booting
/// docker.
/// </summary>
public class ExpressionEvaluatorTests
{
    [Fact]
    public void ConnectionStateEquality_PassesAndFails()
    {
        var probe = new ChaosProbe();
        probe.OnConnectionState(Sdk.ConnectionState.Connected);
        var ev = new ExpressionEvaluator(probe);
        Assert.True(ev.Evaluate("client.connectionState() == 'connected'").Passed);
        Assert.False(ev.Evaluate("client.connectionState() == 'disconnected'").Passed);
    }

    [Fact]
    public void OrShortCircuits_AcceptsReconnectingOrConnected()
    {
        var probe = new ChaosProbe();
        // Inferred "reconnecting" via an SSE drop (since sdk-net's enum has no such state and
        // sdk-net never reports it through the SDK callback). RecordSseDrop also bumps the
        // restart counter, but the test only cares about the state side here.
        probe.RecordSseDrop();
        var ev = new ExpressionEvaluator(probe);
        Assert.Equal("reconnecting", probe.ConnectionState());
        Assert.True(ev.Evaluate("client.connectionState() == 'reconnecting' OR client.connectionState() == 'connected'").Passed);
    }

    [Fact]
    public void AndCombinesMultipleLeafChecks()
    {
        var probe = new ChaosProbe();
        probe.OnConnectionState(Sdk.ConnectionState.Connected);
        var ev = new ExpressionEvaluator(probe);
        Assert.True(ev.Evaluate("client.connectionState() == 'connected' AND client.fallbackPollerActive() == false").Passed);
        // And fails if any leaf fails.
        probe.OnConnectionState(Sdk.ConnectionState.FallingBack);
        Assert.False(ev.Evaluate("client.connectionState() == 'connected' AND client.fallbackPollerActive() == false").Passed);
    }

    [Fact]
    public void SdkMetricByLayerInequality()
    {
        var probe = new ChaosProbe();
        probe.IncRestartLayer1();
        probe.IncRestartLayer1();
        var ev = new ExpressionEvaluator(probe);
        Assert.True(ev.Evaluate("client.sdkMetric('quonfig_sdk_worker_restart_total', layer='1') >= 1").Passed);
        Assert.True(ev.Evaluate("client.sdkMetric('quonfig_sdk_worker_restart_total', layer='1') == 2").Passed);
        Assert.False(ev.Evaluate("client.sdkMetric('quonfig_sdk_worker_restart_total', layer='2') >= 1").Passed);
    }

    [Fact]
    public void SdkMetricUnknownName_FailsLoudlyInsteadOfComparingAgainstZero()
    {
        // A metric the probe does not implement used to read as 0, so "== 0" (or "< 100")
        // passed without checking anything. It must fail and name the metric (qfg-goi1.2.23).
        var ev = new ExpressionEvaluator(new ChaosProbe());
        var r = ev.Evaluate("client.sdkMetric('typo_total') == 0");
        Assert.Equal(ExpressionEvaluator.Verdict.Fail, r.Outcome);
        Assert.Contains("unknown sdkMetric \"typo_total\"", r.Reason);
    }

    [Fact]
    public void SdkLogMatchesRegexCaseInsensitive()
    {
        var probe = new ChaosProbe();
        probe.Log("warning", "quonfig: OnConnectionStateChange handler threw: simulated user-callback panic");
        var ev = new ExpressionEvaluator(probe);
        // Strict-level path: warning log matches a warning-level assertion.
        var r = ev.Evaluate("client.sdkLog('warning', /callback|onConfigUpdate/i) >= 1");
        Assert.True(r.Passed, r.Reason);
    }

    [Fact]
    public void SdkLog_ErrorLevel_AlsoMatchesWarningSeverity()
    {
        // Scenario 10's literal YAML asks for sdkLog('error', ...). sdk-net catches user-callback
        // exceptions at LogWarning (the SDK recovered), so a strict-equal level filter would
        // mark the expectation red even though the diagnostic the assertion targets did fire.
        // The probe's "one-step-lower floor" rule makes this pass.
        var probe = new ChaosProbe();
        probe.Log("warning", "quonfig: OnConnectionStateChange handler threw: simulated user-callback panic");
        var ev = new ExpressionEvaluator(probe);
        var r = ev.Evaluate("client.sdkLog('error', /callback|onConfigUpdate/i) >= 1");
        Assert.True(r.Passed, r.Reason);
    }

    [Fact]
    public void SdkLog_ErrorLevel_StillRejectsInfoSeverity()
    {
        // Without the rule, an Info log line containing 'callback' would falsely satisfy an
        // 'error' assertion. The floor stops at Warning, not below it.
        var probe = new ChaosProbe();
        probe.Log("information", "quonfig: callback registered");
        var ev = new ExpressionEvaluator(probe);
        var r = ev.Evaluate("client.sdkLog('error', /callback/i) >= 1");
        Assert.False(r.Passed, r.Reason);
    }

    [Fact]
    public void SplitOutsideQuotesAndRegex_DoesNotSplitInsideRegex()
    {
        // The literal " OR " inside /a OR b/i must NOT split the expression.
        var expr = "client.sdkLog('error', /panic OR exception/i) >= 1";
        var parts = ExpressionEvaluator.SplitOutsideQuotesAndRegex(expr, " OR ");
        Assert.Single(parts);
        Assert.Equal(expr, parts[0]);
    }

    [Fact]
    public void SplitOutsideQuotesAndRegex_SplitsTopLevelOR()
    {
        var expr = "a == 'b' OR c == 'd'";
        var parts = ExpressionEvaluator.SplitOutsideQuotesAndRegex(expr, " OR ");
        Assert.Equal(2, parts.Count);
        Assert.Equal("a == 'b'", parts[0]);
        Assert.Equal("c == 'd'", parts[1]);
    }

    [Fact]
    public void LastSuccessfulRefresh_RelativeToNow()
    {
        var probe = new ChaosProbe();
        probe.RecordRefresh(System.DateTime.UtcNow); // ~0ms ago
        var ev = new ExpressionEvaluator(probe);
        // Recent install (within 5s) should satisfy ">= (now() - 5000)".
        Assert.True(ev.Evaluate("client.lastSuccessfulRefresh() >= (now() - 5000)").Passed);
        // A 10ms-old install should NOT satisfy ">= (now() - 1)" reliably; check the comparison
        // direction instead. lastSuccessfulRefresh() <= (now() - 0) is always true.
        Assert.True(ev.Evaluate("client.lastSuccessfulRefresh() <= (now() - 0)").Passed);
    }

    [Fact]
    public void UnknownExpression_FailsWithDescriptiveReason()
    {
        var probe = new ChaosProbe();
        var ev = new ExpressionEvaluator(probe);
        var r = ev.Evaluate("nope.what()");
        Assert.False(r.Passed);
        Assert.Contains("unrecognized expression", r.Reason);
    }

    private const string LagExpr = "server_metric('quonfig_subscriber_lag_seconds') == 0";

    [Fact]
    public void ServerMetric_IsSkippedWithReason_NotSilentZero()
    {
        // server_metric(...) used to be stubbed to 0, so "== 0" passed without checking anything.
        // The rig cannot read api-delivery's metrics, so the leaf must be SKIPPED with the reason
        // (qfg-goi1.1.5), never PASS.
        var ev = new ExpressionEvaluator(new ChaosProbe());
        foreach (var expr in new[] { LagExpr, "server_metric('quonfig_subscriber_lag_seconds') > 60" })
        {
            var r = ev.Evaluate(expr);
            Assert.Equal(ExpressionEvaluator.Verdict.Skipped, r.Outcome);
            Assert.False(r.Passed, expr);
            Assert.Contains("SKIPPED", r.Reason, System.StringComparison.Ordinal);
            Assert.Contains("OTLP", r.Reason, System.StringComparison.Ordinal);
            Assert.Contains("qfg-47c2.19", r.Reason, System.StringComparison.Ordinal);
            Assert.Contains("QuonfigSubscriberLagHigh", r.Reason, System.StringComparison.Ordinal);
            var note = Assert.Single(r.SkippedLeaves);
            Assert.Equal(expr, note.Expr);
            Assert.Equal(ExpressionEvaluator.ServerMetricSkipReason, note.Reason);
        }
    }

    [Fact]
    public void SkippedLeaf_IsNeutralInAnd_OtherLeavesStillEnforced()
    {
        // Scenario 02: "client.connectionState() == 'connected' AND server_metric(...) == 0".
        var probe = new ChaosProbe();
        probe.OnConnectionState(Sdk.ConnectionState.Connected);
        var ev = new ExpressionEvaluator(probe);

        var pass = ev.Evaluate("client.connectionState() == 'connected' AND " + LagExpr);
        Assert.Equal(ExpressionEvaluator.Verdict.Pass, pass.Outcome);
        Assert.Single(pass.SkippedLeaves);

        var fail = ev.Evaluate("client.connectionState() == 'disconnected' AND " + LagExpr);
        Assert.Equal(ExpressionEvaluator.Verdict.Fail, fail.Outcome);

        var allSkipped = ev.Evaluate(LagExpr + " AND " + LagExpr);
        Assert.Equal(ExpressionEvaluator.Verdict.Skipped, allSkipped.Outcome);
    }

    [Fact]
    public void SkippedLeaf_IsNeutralInOr_DoesNotSatisfyIt()
    {
        var probe = new ChaosProbe();
        probe.OnConnectionState(Sdk.ConnectionState.Connected);
        var ev = new ExpressionEvaluator(probe);

        Assert.Equal(ExpressionEvaluator.Verdict.Fail,
            ev.Evaluate("client.connectionState() == 'disconnected' OR " + LagExpr).Outcome);
        Assert.Equal(ExpressionEvaluator.Verdict.Pass,
            ev.Evaluate(LagExpr + " OR client.connectionState() == 'connected'").Outcome);
        Assert.Equal(ExpressionEvaluator.Verdict.Skipped,
            ev.Evaluate(LagExpr + " OR " + LagExpr).Outcome);
    }

    [Fact]
    public void SkipLog_AppendsOneLinePerSkippedLeaf()
    {
        var path = System.IO.Path.GetTempFileName();
        try
        {
            var log = new ChaosSkipLog(path);
            var note = new ExpressionEvaluator.SkipNote(LagExpr, ExpressionEvaluator.ServerMetricSkipReason);
            log.Record("01-baseline", 3, note);
            log.Record("02-silent-stall", 0, note);
            var lines = System.IO.File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Equal("01-baseline\texp[3]\t" + LagExpr + "\t" + ExpressionEvaluator.ServerMetricSkipReason, lines[0]);
            Assert.StartsWith("02-silent-stall\texp[0]\t", lines[1], System.StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void SkipLog_WithoutPath_IsANoOp()
    {
        var log = new ChaosSkipLog(null);
        log.Record("01-baseline", 0, new ExpressionEvaluator.SkipNote(LagExpr, "r"));
    }

    [Fact]
    public void Unused_RegexImportIsKept() => Assert.IsType<Regex>(new Regex("x"));
}
