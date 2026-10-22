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
        var (probe, fake) = FakeClientView.Probe();
        fake.State = Sdk.ConnectionState.Connected;
        var ev = new ExpressionEvaluator(probe);
        Assert.True(ev.Evaluate("client.connectionState() == 'connected'").Passed);
        Assert.False(ev.Evaluate("client.connectionState() == 'disconnected'").Passed);
    }

    [Fact]
    public void OrShortCircuits_AcceptsDisconnectedOrConnected()
    {
        // Scenario 02 row 2 (ITD v2026.10.21.1): the SDK's Disconnected is reported as
        // 'disconnected', never an invented 'reconnecting' (qfg-6vno.4).
        var (probe, fake) = FakeClientView.Probe();
        fake.State = Sdk.ConnectionState.Disconnected;
        var ev = new ExpressionEvaluator(probe);
        Assert.Equal("disconnected", probe.ConnectionState());
        Assert.True(ev.Evaluate("client.connectionState() == 'disconnected' OR client.connectionState() == 'connected'").Passed);
        Assert.False(ev.Evaluate("client.connectionState() == 'reconnecting'").Passed);
    }

    [Fact]
    public void AndCombinesMultipleLeafChecks()
    {
        var (probe, fake) = FakeClientView.Probe();
        fake.State = Sdk.ConnectionState.Connected;
        var ev = new ExpressionEvaluator(probe);
        Assert.True(ev.Evaluate("client.connectionState() == 'connected' AND client.fallbackPollerActive() == false").Passed);
        // And fails if any leaf fails.
        fake.State = Sdk.ConnectionState.FallingBack;
        fake.FallbackActive = true;
        Assert.False(ev.Evaluate("client.connectionState() == 'connected' AND client.fallbackPollerActive() == false").Passed);
    }

    [Fact]
    public void SdkMetricByLayerInequality()
    {
        var (probe, fake) = FakeClientView.Probe();
        fake.Restarts1 = 2;
        var ev = new ExpressionEvaluator(probe);
        Assert.True(ev.Evaluate("client.sdkMetric('quonfig_sdk_worker_restart_total', layer='1') >= 1").Passed);
        Assert.True(ev.Evaluate("client.sdkMetric('quonfig_sdk_worker_restart_total', layer='1') == 2").Passed);
        Assert.False(ev.Evaluate("client.sdkMetric('quonfig_sdk_worker_restart_total', layer='2') >= 1").Passed);
    }

    [Fact]
    public void SdkMetricCallbackErrorsByCallbackLabel()
    {
        // Scenario 10 exp[0] (ITD v2026.10.22.2) narrows by callback='...', not layer='...'.
        var (probe, fake) = FakeClientView.Probe();
        var ev = new ExpressionEvaluator(probe);
        const string row = "client.sdkMetric('quonfig_sdk_callback_errors_total', callback='on_config_update') >= 1";
        Assert.False(ev.Evaluate(row).Passed);
        fake.CallbackErrors["on_config_update"] = 1;
        var r = ev.Evaluate(row);
        Assert.True(r.Passed, r.Reason);
        Assert.False(ev.Evaluate("client.sdkMetric('quonfig_sdk_callback_errors_total', callback='on_connection_state_change') >= 1").Passed);
        Assert.True(ev.Evaluate("client.sdkMetric('quonfig_sdk_callback_errors_total') == 1").Passed);
    }

    [Fact]
    public void SdkMetricUnknownLabel_FailsLoudly()
    {
        var ev = new ExpressionEvaluator(FakeClientView.Probe().Probe);
        var r = ev.Evaluate("client.sdkMetric('quonfig_sdk_worker_restart_total', callback='on_config_update') == 0");
        Assert.Equal(ExpressionEvaluator.Verdict.Fail, r.Outcome);
        Assert.Contains("with label callback", r.Reason);
    }

    [Fact]
    public void SdkMetricSseConnectAttempts_ReadsTheSdkCounter()
    {
        // Scenario 09's storm guard (attempts < 100) reads the SDK's own attempt counter, which
        // also counts attempts that failed before a frame and so fired no state edge (qfg-6vno.4).
        var (probe, fake) = FakeClientView.Probe();
        var ev = new ExpressionEvaluator(probe);
        Assert.True(ev.Evaluate("client.sdkMetric('quonfig_sse_connect_attempts_total') == 0").Passed);
        fake.Attempts = 150;
        var r = ev.Evaluate("client.sdkMetric('quonfig_sse_connect_attempts_total') < 100");
        Assert.False(r.Passed, r.Reason);
    }

    [Fact]
    public void SdkMetricUnknownName_FailsLoudlyInsteadOfComparingAgainstZero()
    {
        // A metric the probe does not implement used to read as 0, so "== 0" (or "< 100")
        // passed without checking anything. It must fail and name the metric (qfg-goi1.2.23).
        var ev = new ExpressionEvaluator(FakeClientView.Probe().Probe);
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
    public void SdkLog_ErrorLevel_DoesNotMatchWarningLines()
    {
        // sdkLog(level, re) counts lines at exactly that level (chaos README). The old probe
        // let 'error' match Warning, which hid that sdk-net logged a caught callback throw at
        // Warning (qfg-6vno.4; fixed to Error in qfg-6vno.21).
        var probe = new ChaosProbe();
        probe.Log("warning", "quonfig: OnConfigChange handler threw: simulated user-callback panic");
        var ev = new ExpressionEvaluator(probe);
        Assert.True(ev.Evaluate("client.sdkLog('error', /callback|onConfigUpdate/i) == 0").Passed);
        Assert.True(ev.Evaluate("client.sdkLog('warn', /callback/i) == 1").Passed);
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
        var (probe, fake) = FakeClientView.Probe();
        fake.LastRefresh = System.DateTimeOffset.UtcNow; // ~0ms ago
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
        var ev = new ExpressionEvaluator(FakeClientView.Probe().Probe);
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
        var (probe, fake) = FakeClientView.Probe();
        fake.State = Sdk.ConnectionState.Connected;
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
        var (probe, fake) = FakeClientView.Probe();
        fake.State = Sdk.ConnectionState.Connected;
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
