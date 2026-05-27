using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Quonfig.Sdk;
using Xunit;
using Xunit.Abstractions;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// Cross-SDK chaos harness — sdk-net runner (bead qfg-zp7i.14).
///
/// <para>Wires sdk-net's xUnit runner to <c>integration-test-data/chaos/</c>. The shared launcher
/// (<c>integration-test-data/chaos/start-chaos.sh</c>) must already have booted toxiproxy and a
/// locally-spawned api-delivery (FIXTURE_DIR mode). This test reconfigures the seeded SSE/HTTP
/// proxies to point at that api-delivery, then runs each scenario against a fresh sdk-net client.</para>
///
/// <para>Gated on <c>QUONFIG_CHAOS_RUN=1</c> so the default <c>dotnet test</c> does not depend
/// on docker + toxiproxy + api-delivery being on the host. The chaos project is also a
/// separate csproj, so the main test workflow (which runs only
/// <c>tests/Quonfig.Sdk.Tests/</c>) doesn't even try to build chaos.</para>
///
/// <para>Run via <c>scripts/run-chaos.sh</c>, which handles the docker boot + api-delivery build
/// + teardown. Mirrors sdk-java's <c>ChaosTest.java</c>.</para>
///
/// <para>Environment knobs:</para>
/// <list type="bullet">
///   <item><c>QUONFIG_CHAOS_RUN=1</c> — required; gates the test (else SkippableFact reports skipped).</item>
///   <item><c>TOXIPROXY_URL</c> — admin API base, default <c>http://127.0.0.1:8474</c>.</item>
///   <item><c>CHAOS_SSE_PORT</c> — host SSE port, default <c>18550</c>.</item>
///   <item><c>CHAOS_HTTP_PORT</c> — host HTTP port, default <c>18551</c>.</item>
///   <item><c>CHAOS_API_DELIVERY_URL</c> — api-delivery base URL, default <c>http://127.0.0.1:6550</c>.</item>
///   <item><c>CHAOS_UPSTREAM_HOST</c> — hostname toxiproxy uses, default <c>host.docker.internal</c>.</item>
///   <item><c>CHAOS_ONLY</c>, <c>CHAOS_SKIP</c> — comma scenario lists (e.g. <c>"02,05,07,09"</c>).</item>
///   <item><c>CHAOS_POLL_MS</c> — expectation poll interval, default 250.</item>
///   <item><c>CHAOS_WALL_CLOCK_CAP_S</c> — hard cap on per-scenario wall-clock seconds (default 0 = none).</item>
///   <item><c>CHAOS_FIXTURE_SDK_KEY</c> — backend SDK key matching api-delivery's fixture file. Default <c>test-backend-key</c>.</item>
/// </list>
/// </summary>
[Trait("Category", "Chaos")]
public sealed class ChaosTests
{
    private readonly ITestOutputHelper _out;

    public ChaosTests(ITestOutputHelper output)
    {
        _out = output;
    }

    /// <summary>
    /// MemberData source: every scenario YAML, one row per filename. xUnit shows the filename as
    /// the test ID so a partial failure is easy to spot in CI output.
    /// </summary>
    public static IEnumerable<object[]> ScenarioFiles()
    {
        var dir = ChaosPaths.ScenariosDir();
        if (!Directory.Exists(dir))
        {
            // No scenarios — yield a sentinel so xUnit still reports something rather than "no
            // data" (which would silently skip without a clear cause). The test will Skip on
            // QUONFIG_CHAOS_RUN gating before it tries to read the file.
            yield return new object[] { "(no-scenarios-found)" };
            yield break;
        }
        foreach (var path in Directory.EnumerateFiles(dir, "*.yaml").OrderBy(p => p))
        {
            yield return new object[] { Path.GetFileName(path) };
        }
    }

    [SkippableTheory]
    [MemberData(nameof(ScenarioFiles))]
    public async Task Run(string fileName)
    {
        Skip.IfNot(
            Environment.GetEnvironmentVariable("QUONFIG_CHAOS_RUN") == "1",
            "QUONFIG_CHAOS_RUN!=1 — chaos suite gated to opt-in runs");
        Skip.If(fileName == "(no-scenarios-found)", "integration-test-data sibling not found");

        var only = CsvSet(Environment.GetEnvironmentVariable("CHAOS_ONLY"));
        var skip = CsvSet(Environment.GetEnvironmentVariable("CHAOS_SKIP"));
        var num = ScenarioNumber(fileName);
        Skip.If(only.Count > 0 && !only.Contains(num), $"CHAOS_ONLY={string.Join(",", only)} skip");
        Skip.If(skip.Contains(num), $"CHAOS_SKIP={string.Join(",", skip)} skip");

        var scenariosDir = ChaosPaths.ScenariosDir();
        var path = Path.Combine(scenariosDir, fileName);
        var scenario = ChaosYamlLoader.Load(path);

        var toxiUrl = EnvOr("TOXIPROXY_URL", "http://127.0.0.1:8474");
        var ssePort = int.Parse(EnvOr("CHAOS_SSE_PORT", "18550"));
        var httpPort = int.Parse(EnvOr("CHAOS_HTTP_PORT", "18551"));
        var apiUrl = EnvOr("CHAOS_API_DELIVERY_URL", "http://127.0.0.1:6550");
        var upstreamHost = EnvOr("CHAOS_UPSTREAM_HOST", "host.docker.internal");
        var upstreamPort = ParsePortFromUrl(apiUrl);
        var sdkKey = EnvOr("CHAOS_FIXTURE_SDK_KEY", "test-backend-key");
        var pollMs = int.Parse(EnvOr("CHAOS_POLL_MS", "250"));
        var wallClockCapS = int.Parse(EnvOr("CHAOS_WALL_CLOCK_CAP_S", "0"));

        using var tp = new ToxiproxyClient(toxiUrl);
        try
        {
            await tp.PingAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"toxiproxy not reachable at {toxiUrl}: {ex.Message} — run integration-test-data/chaos/start-chaos.sh first",
                ex);
        }

        await tp.UpsertProxyAsync("sse", "0.0.0.0:" + ssePort, upstreamHost + ":" + upstreamPort);
        await tp.UpsertProxyAsync("http", "0.0.0.0:" + httpPort, upstreamHost + ":" + upstreamPort);

        // Sequentially run each scenario test in this file (most files have one).
        foreach (var run in scenario.Tests)
        {
            await RunScenarioAsync(tp, run, ssePort, httpPort, sdkKey, pollMs, wallClockCapS);
        }
    }

    private async Task RunScenarioAsync(
        ToxiproxyClient tp,
        ChaosScenario.Run run,
        int ssePort,
        int httpPort,
        string sdkKey,
        int pollMs,
        int wallClockCapS)
    {
        // Reset proxy state between scenarios.
        await tp.ClearToxicsAsync("sse");
        await tp.ClearToxicsAsync("http");
        await tp.SetEnabledAsync("sse", true);
        await tp.SetEnabledAsync("http", true);

        var probe = new ChaosProbe();
        var evaluator = new ExpressionEvaluator(probe);
        var logger = new ProbeBridgeLogger(probe);

        var httpBase = "http://127.0.0.1:" + httpPort;
        var sseBase = "http://127.0.0.1:" + ssePort;

        var opts = new QuonfigOptions
        {
            SdkKey = sdkKey,
            ApiUrls = new[] { httpBase },
            StreamUrls = new[] { sseBase },
            // Datadir mode is N/A here — we want the HTTP+SSE path.
            InitTimeout = TimeSpan.FromSeconds(15),
            // Default 90s read watchdog matches scenario 02's within_ms=95000 + the SDK contract.
            Logger = logger,
            // Speed up Layer 2 engage detection for tests where the YAML asserts within ~135s.
            // (Cross-SDK default is 120s; we honor it.)
        };

        // Scenario 10 — user callback throws. sdk-net has no OnConfigUpdate callback; the closest
        // equivalent is OnConnectionStateChange, which fires when the SDK transitions through
        // Initializing → Connected on the first envelope install. The handler throw is caught by
        // the SDK and logged as a warning containing "OnConnectionStateChange handler threw"
        // (and our injected "user-callback panic" message). The ProbeBridgeLogger counts that
        // as a Layer 1 restart signal so the metric assertion fires.
        Sdk.Quonfig? client = null;
        Action<Sdk.ConnectionState>? stateHandler = null;
        try
        {
            client = new Sdk.Quonfig(opts);

            stateHandler = state =>
            {
                probe.OnConnectionState(state);
                if (state == Sdk.ConnectionState.Connected)
                {
                    probe.RecordRefresh(DateTime.UtcNow);
                }
                if ("throw".Equals(run.Setup?.UserCallback, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "simulated user-callback panic for chaos scenario 10");
                }
            };
            client.OnConnectionStateChange += stateHandler;
        }
        catch (Exception ex)
        {
            _out.WriteLine("client construction failed: " + ex.Message);
            probe.Log("error", ex.Message);
        }

        // Schedule chaos events.
        var baseline = DateTime.UtcNow;
        var injections = new ConcurrentDictionary<string, InjectionState>(StringComparer.Ordinal);
        var chaosTasks = new List<Task>();
        var chaosCts = new CancellationTokenSource();
        if (run.Chaos is not null)
        {
            foreach (var ev in run.Chaos)
            {
                var captured = ev;
                chaosTasks.Add(Task.Run(async () =>
                {
                    var fireAt = baseline + TimeSpan.FromMilliseconds(captured.AtMs);
                    var delay = fireAt - DateTime.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(delay, chaosCts.Token);
                        }
                        catch (OperationCanceledException) { return; }
                    }
                    try
                    {
                        if (captured.Inject is not null)
                        {
                            var st = await ApplyInjectAsync(tp, captured.Inject);
                            if (!string.IsNullOrEmpty(captured.Inject.Name) && st is not null)
                            {
                                injections[captured.Inject.Name!] = st;
                            }
                            _out.WriteLine($"[{captured.AtMs,6}ms] inject {DescribeInject(captured.Inject)}");
                        }
                        else if (!string.IsNullOrEmpty(captured.Clear))
                        {
                            if (injections.TryRemove(captured.Clear!, out var st))
                            {
                                await ClearInjectAsync(tp, st);
                            }
                            _out.WriteLine($"[{captured.AtMs,6}ms] clear {captured.Clear}");
                        }
                        else if (captured.Process is not null)
                        {
                            await ApplyProcessAsync(tp, captured.Process, chaosCts.Token);
                            _out.WriteLine($"[{captured.AtMs,6}ms] process {captured.Process.Action}/{captured.Process.Count}");
                        }
                    }
                    catch (Exception cex)
                    {
                        _out.WriteLine($"[{captured.AtMs,6}ms] chaos event failed: {cex.Message}");
                    }
                }, chaosCts.Token));
            }
        }

        var wallClockS = run.Setup?.WallClockSeconds > 0 ? run.Setup.WallClockSeconds : 30;
        if (wallClockCapS > 0 && wallClockS > wallClockCapS)
        {
            _out.WriteLine($"scenario \"{run.Name}\" wall_clock_seconds={wallClockS} capped to {wallClockCapS} via CHAOS_WALL_CLOCK_CAP_S");
            wallClockS = wallClockCapS;
        }
        var deadline = baseline + TimeSpan.FromSeconds(wallClockS);
        var states = new List<ExpState>();
        for (int i = 0; i < run.Expectations.Count; i++)
        {
            states.Add(new ExpState(i, run.Expectations[i]));
        }

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                var now = DateTime.UtcNow;
                var elapsedMs = (long)(now - baseline).TotalMilliseconds;
                bool allTerminal = true;
                foreach (var s in states)
                {
                    if (s.Passed || s.Failed) continue;
                    var r = evaluator.Evaluate(s.Exp.AssertExpr);
                    s.LastReason = r.Reason;
                    if (r.Passed)
                    {
                        if (s.HeldSince is null) { s.HeldSince = now; s.HitAtMs = elapsedMs; }
                        var holdFor = TimeSpan.FromMilliseconds(s.Exp.MustHoldForMs);
                        if (holdFor <= TimeSpan.Zero || (now - s.HeldSince.Value) >= holdFor)
                        {
                            s.Passed = true;
                        }
                        else
                        {
                            allTerminal = false;
                        }
                    }
                    else
                    {
                        s.HeldSince = null;
                        allTerminal = false;
                    }
                    if (!s.Passed && elapsedMs > s.Exp.WithinMs)
                    {
                        s.Failed = true;
                    }
                }
                if (allTerminal) break;
                await Task.Delay(pollMs);
            }
            foreach (var s in states)
            {
                if (!s.Passed) s.Failed = true;
            }
        }
        finally
        {
            chaosCts.Cancel();
            try { await Task.WhenAll(chaosTasks); } catch { /* best-effort */ }
            chaosCts.Dispose();
            if (client is not null)
            {
                if (stateHandler is not null)
                {
                    client.OnConnectionStateChange -= stateHandler;
                }
                try { await client.CloseAsync(); }
                catch (Exception cex) { _out.WriteLine("close failed: " + cex.Message); }
            }
        }

        int pass = 0, fail = 0;
        var failures = new List<string>();
        foreach (var s in states)
        {
            if (s.Passed)
            {
                pass++;
                _out.WriteLine(
                    $"PASS  exp[{s.Idx}] within={s.Exp.WithinMs}ms hold={s.Exp.MustHoldForMs}ms: {s.Exp.AssertExpr}  (hit at {s.HitAtMs}ms)");
            }
            else
            {
                fail++;
                var msg = $"FAIL  exp[{s.Idx}] within={s.Exp.WithinMs}ms hold={s.Exp.MustHoldForMs}ms: {s.Exp.AssertExpr} — last reason: {s.LastReason}";
                _out.WriteLine(msg);
                failures.Add(msg);
            }
        }
        _out.WriteLine(
            $"scenario summary: {pass} passed, {fail} failed (state={probe.ConnectionState()}, restartL1={probe.SdkMetric("quonfig_sdk_worker_restart_total", "1")}, fallback={probe.FallbackPollerActive()}, lastRefresh={probe.LastSuccessfulRefreshUtc()?.ToString("o") ?? "null"})");

        if (failures.Count > 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"scenario \"{run.Name}\" — {fail}/{states.Count} expectations failed:\n{string.Join("\n", failures)}");
        }
    }

    // ---- chaos injection translation ----

    private static async Task<InjectionState?> ApplyInjectAsync(ToxiproxyClient tp, ChaosScenario.Inject inj)
    {
        var name = string.IsNullOrEmpty(inj.Name) ? "anon" : inj.Name!;
        if (inj.SseSilentStallAfterMs is not null)
        {
            var attrs = new Dictionary<string, object?> { ["timeout"] = inj.SseSilentStallAfterMs };
            await tp.AddToxicAsync("sse", name, "timeout", "downstream", attrs);
            return new InjectionState("sse", name, Array.Empty<string>());
        }
        if (inj.SseLatencyMs is not null)
        {
            var attrs = new Dictionary<string, object?> { ["latency"] = inj.SseLatencyMs };
            await tp.AddToxicAsync("sse", name, "latency", "downstream", attrs);
            return new InjectionState("sse", name, Array.Empty<string>());
        }
        if (inj.SseBandwidthKbps is not null)
        {
            var attrs = new Dictionary<string, object?> { ["rate"] = inj.SseBandwidthKbps };
            await tp.AddToxicAsync("sse", name, "bandwidth", "downstream", attrs);
            return new InjectionState("sse", name, Array.Empty<string>());
        }
        if (inj.SseDownMs is not null)
        {
            await tp.SetEnabledAsync("sse", false);
            return new InjectionState(null, null, new[] { "sse" });
        }
        if (inj.BothDownMs is not null)
        {
            await tp.SetEnabledAsync("sse", false);
            await tp.SetEnabledAsync("http", false);
            return new InjectionState(null, null, new[] { "sse", "http" });
        }
        if (inj.SseHalfOpenAfterBytes is not null)
        {
            // Toxiproxy is TCP-only; the closest analog is to disable the proxy so the SDK
            // observes ECONNREFUSED on the next attempt. Mirrors sdk-java's qfg-47c2.29 fix.
            await tp.SetEnabledAsync("sse", false);
            return new InjectionState(null, null, new[] { "sse" });
        }
        if (inj.SseHttpStatus is not null)
        {
            // Toxiproxy can't rewrite HTTP responses — scenario 08 lives in scenarios-http-proxy
            // and isn't picked up by the default suite. No-op for safety.
            return new InjectionState(null, null, Array.Empty<string>());
        }
        if (!string.IsNullOrEmpty(inj.Proxy) && inj.Toxic is not null)
        {
            var type = inj.Toxic["type"]?.ToString() ?? "noop";
            var attrs = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (inj.Toxic.TryGetValue("attributes", out var raw)
                && raw is Dictionary<string, object?> rawAttrs)
            {
                foreach (var kv in rawAttrs) attrs[kv.Key] = kv.Value;
            }
            await tp.AddToxicAsync(inj.Proxy!, name, type, "downstream", attrs);
            return new InjectionState(inj.Proxy, name, Array.Empty<string>());
        }
        return null;
    }

    private static async Task ClearInjectAsync(ToxiproxyClient tp, InjectionState st)
    {
        if (st is null) return;
        if (!string.IsNullOrEmpty(st.Toxic) && !string.IsNullOrEmpty(st.Proxy))
        {
            await tp.RemoveToxicAsync(st.Proxy!, st.Toxic!);
        }
        foreach (var p in st.Enable)
        {
            await tp.SetEnabledAsync(p, true);
        }
    }

    private static async Task ApplyProcessAsync(ToxiproxyClient tp, ChaosScenario.Process p, CancellationToken ct)
    {
        if ("kill_sse_proxy".Equals(p.Action, StringComparison.Ordinal))
        {
            int count = Math.Max(1, p.Count);
            int interval = p.IntervalMs > 0 ? p.IntervalMs : 1000;
            for (int i = 0; i < count; i++)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    await tp.SetEnabledAsync("sse", false);
                    await Task.Delay(200, ct);
                    await tp.SetEnabledAsync("sse", true);
                }
                catch (OperationCanceledException) { return; }
                catch { /* best-effort */ }
                if (i < count - 1)
                {
                    try { await Task.Delay(Math.Max(0, interval - 200), ct); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
    }

    private static string DescribeInject(ChaosScenario.Inject inj)
    {
        if (inj.SseSilentStallAfterMs is not null) return "sse_silent_stall_after_ms=" + inj.SseSilentStallAfterMs;
        if (inj.SseLatencyMs is not null) return "sse_latency_ms=" + inj.SseLatencyMs;
        if (inj.SseBandwidthKbps is not null) return "sse_bandwidth_kbps=" + inj.SseBandwidthKbps;
        if (inj.SseDownMs is not null) return "sse_down_ms=" + inj.SseDownMs;
        if (inj.BothDownMs is not null) return "both_down_ms=" + inj.BothDownMs;
        if (inj.SseHalfOpenAfterBytes is not null) return "sse_half_open_after_bytes=" + inj.SseHalfOpenAfterBytes;
        if (inj.SseHttpStatus is not null) return "sse_http_status=" + inj.SseHttpStatus;
        return "?";
    }

    private static string EnvOr(string key, string def)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrEmpty(v) ? def : v!;
    }

    private static HashSet<string> CsvSet(string? s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(s)) return set;
        foreach (var part in s!.Split(','))
        {
            var p = part.Trim();
            if (p.Length > 0) set.Add(p);
        }
        return set;
    }

    private static int ParsePortFromUrl(string url)
    {
        int colon = url.LastIndexOf(':');
        if (colon < 0) throw new ArgumentException("no port in URL " + url);
        var rest = url.Substring(colon + 1);
        int slash = rest.IndexOf('/');
        if (slash >= 0) rest = rest.Substring(0, slash);
        return int.Parse(rest);
    }

    private static string ScenarioNumber(string fileName)
    {
        int dash = fileName.IndexOf('-');
        return dash > 0 ? fileName.Substring(0, dash) : fileName;
    }

    // ---- internal state types ----

    private sealed class InjectionState
    {
        public InjectionState(string? proxy, string? toxic, IReadOnlyList<string> enable)
        {
            Proxy = proxy;
            Toxic = toxic;
            Enable = enable;
        }
        public string? Proxy { get; }
        public string? Toxic { get; }
        public IReadOnlyList<string> Enable { get; }
    }

    private sealed class ExpState
    {
        public ExpState(int idx, ChaosScenario.Expectation exp) { Idx = idx; Exp = exp; }
        public int Idx { get; }
        public ChaosScenario.Expectation Exp { get; }
        public long HitAtMs { get; set; }
        public DateTime? HeldSince { get; set; }
        public bool Passed { get; set; }
        public bool Failed { get; set; }
        public string LastReason { get; set; } = string.Empty;
    }
}
