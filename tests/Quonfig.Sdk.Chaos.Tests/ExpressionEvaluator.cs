using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// Tiny evaluator for the chaos-scenario assertion expressions. Supports the same vocabulary as
/// sdk-go and sdk-java's runners — see
/// <c>integration-test-data/chaos/supervisor-test-contract.md</c> for the full grammar.
/// Returns <see cref="Result"/> pairs so the runner can log <em>why</em> an assertion failed
/// without needing a real parser.
/// </summary>
internal sealed class ExpressionEvaluator
{
    public readonly struct Result
    {
        public Result(bool passed, string reason) { Passed = passed; Reason = reason; }
        public bool Passed { get; }
        public string Reason { get; }
    }

    private static readonly Regex ReConnState =
        new(@"^client\.connectionState\(\)\s*(==|!=)\s*'([^']+)'$", RegexOptions.Compiled);
    private static readonly Regex ReFallback =
        new(@"^client\.fallbackPollerActive\(\)\s*==\s*(true|false)$", RegexOptions.Compiled);
    private static readonly Regex ReProcAlive =
        new(@"^client\.processStillAlive\(\)\s*==\s*(true|false)$", RegexOptions.Compiled);
    private static readonly Regex ReLastRefresh =
        new(@"^client\.lastSuccessfulRefresh\(\)\s*(>=|>|<=|<|==)\s*\(now\(\)\s*-\s*(\d+)\)$", RegexOptions.Compiled);
    private static readonly Regex ReSdkMetric =
        new(@"^client\.sdkMetric\(\s*'([^']+)'\s*(?:,\s*layer=\s*'([^']+)'\s*)?\)\s*(>=|<=|==|!=|<|>)\s*(\d+)$", RegexOptions.Compiled);
    private static readonly Regex ReServerMetric =
        new(@"^server_metric\(\s*'([^']+)'\s*\)\s*(>=|<=|==|!=|<|>)\s*(\d+)$", RegexOptions.Compiled);
    private static readonly Regex ReSdkLog =
        new(@"^client\.sdkLog\(\s*'([^']+)'\s*,\s*/(.+)/i\s*\)\s*(>=|<=|==|!=|<|>)\s*(\d+)$", RegexOptions.Compiled);

    private readonly ChaosProbe _probe;

    public ExpressionEvaluator(ChaosProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public Result Evaluate(string expr)
    {
        var e = (expr ?? string.Empty).Trim();
        if (e.Length == 0) return new Result(true, string.Empty);

        if (e.Contains(" OR ", StringComparison.Ordinal))
        {
            var parts = SplitOutsideQuotesAndRegex(e, " OR ");
            var reasons = new List<string>();
            foreach (var p in parts)
            {
                var r = Evaluate(p);
                if (r.Passed) return new Result(true, string.Empty);
                reasons.Add(r.Reason);
            }
            return new Result(false, "OR: " + string.Join(" | ", reasons));
        }
        if (e.Contains(" AND ", StringComparison.Ordinal))
        {
            foreach (var p in SplitOutsideQuotesAndRegex(e, " AND "))
            {
                var r = Evaluate(p);
                if (!r.Passed) return new Result(false, "AND: " + r.Reason);
            }
            return new Result(true, string.Empty);
        }
        return Leaf(e);
    }

    private Result Leaf(string expr)
    {
        Match m;
        if ((m = ReConnState.Match(expr)).Success)
        {
            var got = _probe.ConnectionState();
            var want = m.Groups[2].Value;
            var op = m.Groups[1].Value;
            var ok = op == "==" ? got == want : got != want;
            return new Result(ok, "connectionState=" + got + " " + op + " " + want);
        }
        if ((m = ReFallback.Match(expr)).Success)
        {
            var want = bool.Parse(m.Groups[1].Value);
            var got = _probe.FallbackPollerActive();
            return new Result(got == want, "fallbackPollerActive=" + got + " want " + want);
        }
        if ((m = ReProcAlive.Match(expr)).Success)
        {
            var want = bool.Parse(m.Groups[1].Value);
            var got = _probe.ProcessStillAlive();
            return new Result(got == want, "processStillAlive=" + got + " want " + want);
        }
        if ((m = ReLastRefresh.Match(expr)).Success)
        {
            var ago = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var last = _probe.LastSuccessfulRefreshUtc();
            var lastMs = last is null ? 0 : new DateTimeOffset(last.Value, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var threshold = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ago;
            var op = m.Groups[1].Value;
            var ok = CompareLong(op, lastMs, threshold);
            return new Result(ok, "lastSuccessfulRefresh=" + lastMs + " " + op + " " + threshold);
        }
        if ((m = ReSdkMetric.Match(expr)).Success)
        {
            var metric = m.Groups[1].Value;
            var layer = m.Groups[2].Success ? m.Groups[2].Value : null;
            var got = _probe.SdkMetric(metric, layer);
            var want = double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            var op = m.Groups[3].Value;
            var ok = CompareDouble(op, got, want);
            return new Result(ok, "sdkMetric(" + metric + ",layer=" + (layer ?? "*") + ")=" + got + " " + op + " " + want);
        }
        if ((m = ReServerMetric.Match(expr)).Success)
        {
            // Server-side metrics aren't exposed to the SDK — stub to 0 (matches sdk-java).
            double got = 0;
            var want = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            var op = m.Groups[2].Value;
            var ok = CompareDouble(op, got, want);
            return new Result(ok, "server_metric(" + m.Groups[1].Value + ")=0 " + op + " " + want);
        }
        if ((m = ReSdkLog.Match(expr)).Success)
        {
            var level = m.Groups[1].Value;
            var re = new Regex(m.Groups[2].Value, RegexOptions.IgnoreCase);
            var n = _probe.SdkLogMatches(level, re);
            var want = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            var op = m.Groups[3].Value;
            var ok = CompareLong(op, n, want);
            return new Result(ok, "sdkLog(" + level + ",/" + m.Groups[2].Value + "/i)=" + n + " " + op + " " + want);
        }
        return new Result(false, "unrecognized expression: " + expr);
    }

    private static bool CompareLong(string op, long a, long b) => op switch
    {
        "==" => a == b,
        "!=" => a != b,
        "<" => a < b,
        "<=" => a <= b,
        ">" => a > b,
        ">=" => a >= b,
        _ => false,
    };

    private static bool CompareDouble(string op, double a, double b) => op switch
    {
        "==" => a == b,
        "!=" => a != b,
        "<" => a < b,
        "<=" => a <= b,
        ">" => a > b,
        ">=" => a >= b,
        _ => false,
    };

    /// <summary>
    /// Splits <paramref name="expr"/> on <paramref name="sep"/> but ignores occurrences inside
    /// single-quoted strings or <c>/regex/i</c> literals. Public for tests.
    /// </summary>
    public static List<string> SplitOutsideQuotesAndRegex(string expr, string sep)
    {
        if (expr is null) throw new ArgumentNullException(nameof(expr));
        if (sep is null) throw new ArgumentNullException(nameof(sep));
        var parts = new List<string>();
        bool inSq = false;
        bool inRe = false;
        int start = 0;
        int i = 0;
        while (i < expr.Length)
        {
            char c = expr[i];
            if (c == '\'' && !inRe) inSq = !inSq;
            else if (c == '/' && !inSq) inRe = !inRe;
            if (!inSq && !inRe
                && i + sep.Length <= expr.Length
                && string.CompareOrdinal(expr, i, sep, 0, sep.Length) == 0)
            {
                parts.Add(expr.Substring(start, i - start));
                start = i + sep.Length;
                i = start;
                continue;
            }
            i++;
        }
        parts.Add(expr.Substring(start));
        return parts;
    }
}
