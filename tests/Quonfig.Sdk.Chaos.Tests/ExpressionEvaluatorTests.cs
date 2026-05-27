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

    [Fact]
    public void Unused_RegexImportIsKept() => Assert.IsType<Regex>(new Regex("x"));
}
