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
        var rig = new InjectContext(tp, ssePort, sdkKey);

        var httpBase = "http://127.0.0.1:" + httpPort;
        var sseBase = "http://127.0.0.1:" + ssePort;

        bool throwInCallback = "throw".Equals(run.Setup?.UserCallback, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(run.Setup?.UserCallback) && !throwInCallback)
        {
            throw new NotSupportedException($"unsupported setup.user_callback \"{run.Setup!.UserCallback}\"");
        }

        var opts = new QuonfigOptions
        {
            SdkKey = sdkKey,
            ApiUrls = new[] { httpBase },
            StreamUrls = new[] { sseBase },
            // Datadir mode is N/A here — we want the HTTP+SSE path.
            InitTimeout = TimeSpan.FromSeconds(15),
            // Shortened Layer 1 read watchdog (production default 90s). 90s loses scenario 02's
            // timing race: stall injected at 5s + 90s watchdog = the Disconnected edge lands at
            // ~95s, exactly on exp[1]'s within_ms=95000 deadline (observed red on the first CI
            // run). The fixture api-delivery heartbeats every 1s (SSE_HEARTBEAT_INTERVAL=1s in
            // run-chaos.sh). 10s (as sdk-java's rig), not sdk-go's 5s: scenario 03 adds 5000ms
            // latency to the SSE leg, which opens a ~6s gap between heartbeats once the toxic
            // lands, and a 5s watchdog fired on it (a false Layer 1 restart, observed once the
            // qfg-goi1.7 holds made 03 check its whole window). 02 (stall at 5s, restart within
            // 95s) stays reachable.
            SseReadTimeout = TimeSpan.FromSeconds(10),
            Logger = logger,
            // Layer 2 engage delay stays at the cross-SDK default (120s) — scenario 05's
            // within_ms=135000 accommodates it.
            // The rig observes behavior via connection-state / generation getters, not the telemetry
            // collector; opt out so the now-live reporter (qfg-gxm6) does not post to the default
            // (production) telemetry endpoint during a chaos run.
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
            // OnConfigChange is sdk-net's onConfigUpdate: readyEventCount counts it, and scenario
            // 10's user callback throws from it. Subscribed via the option so the first install
            // fires it.
            OnConfigChange = () =>
            {
                probe.OnConfigChange();
                if (throwInCallback)
                {
                    throw new InvalidOperationException("simulated user-callback panic for chaos scenario 10");
                }
            },
        };

        // Init-failure scenarios (00-*): the SDK's onInitFailure + initTimeout, and t=0 BEFORE the
        // init call (chaos README "Timeline rules" 2).
        bool initMode = !string.IsNullOrEmpty(run.Setup?.OnInitFailure);
        if (initMode)
        {
            opts.OnInitFailure = run.Setup!.OnInitFailure switch
            {
                "fail" => OnInitFailure.Fail,
                "wait" => OnInitFailure.Wait,
                "continue" => OnInitFailure.Continue,
                var other => throw new NotSupportedException($"unsupported setup.on_init_failure \"{other}\""),
            };
            if (run.Setup.InitTimeoutMs is { } ms) opts.InitTimeout = TimeSpan.FromMilliseconds(ms);
        }

        var injections = new ConcurrentDictionary<string, InjectionState>(StringComparer.Ordinal);
        var chaosErrors = new ConcurrentQueue<string>();
        var baseline = DateTime.UtcNow;

        // Pre-init injects (at_ms <= 0) are applied before the first fetch (Timeline rule 2).
        var later = new List<ChaosScenario.Event>();
        foreach (var ev in run.Chaos ?? new List<ChaosScenario.Event>())
        {
            if (ev.AtMs <= 0 && ev.Inject is not null)
            {
                await FireEventAsync(rig, ev, injections, CancellationToken.None);
                _out.WriteLine($"[     0ms] inject {DescribeInject(ev.Inject)} (pre-init)");
            }
            else
            {
                later.Add(ev);
            }
        }

        // A client that cannot be constructed fails the scenario: there is nothing to observe.
        var client = new Sdk.Quonfig(opts);
        probe.SetClient(client);
        Task? initTask = null;
        if (initMode)
        {
            // The init call runs on its own task so the tick loop keeps sampling while it blocks;
            // client.initState() reads that task's status. catch_init_failure: the runner observes
            // the raise and keeps the client.
            initTask = Task.Run(() => client.InitAsync());
            probe.SetInitTask(initTask);
        }
        else
        {
            // Background-fetching constructor: t=0 at or right after it.
            baseline = DateTime.UtcNow;
        }

        // processStillAlive(): an SDK task that faults unobserved is the in-process crash signal.
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) =>
        {
            if (e.Exception?.ToString().Contains("Quonfig.Sdk.", StringComparison.Ordinal) == true)
            {
                probe.RecordUnobservedFault();
            }
        };
        TaskScheduler.UnobservedTaskException += onUnobserved;

        // Schedule chaos events.
        var chaosTasks = new List<Task>();
        var chaosCts = new CancellationTokenSource();
        foreach (var ev in later)
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
                    await FireEventAsync(rig, captured, injections, chaosCts.Token);
                    _out.WriteLine($"[{captured.AtMs,6}ms] {DescribeEvent(captured)}");
                }
                catch (OperationCanceledException) when (chaosCts.IsCancellationRequested)
                {
                    // Scenario over.
                }
                catch (Exception cex)
                {
                    // A chaos step that did not apply means the scenario did not test what it
                    // says: fail the run (qfg-6vno.4), never continue as a silent no-op.
                    _out.WriteLine($"[{captured.AtMs,6}ms] chaos event failed: {cex.Message}");
                    chaosErrors.Enqueue($"chaos event at {captured.AtMs}ms ({DescribeEvent(captured)}) failed: {cex.Message}");
                }
            }, chaosCts.Token));
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
            var st = new ExpState(i, run.Expectations[i]);
            if (ChaosDeviations.Find(run.Name, run.Expectations[i].AssertExpr) is { } dev)
            {
                // A documented sdk-net deviation: reported SKIPPED with its reason, never PASS.
                st.Timeline.Skipped = true;
                st.SkippedLeaves = new[] { new ExpressionEvaluator.SkipNote(dev.Assert, "DEVIATION: " + dev.Reason) };
            }
            states.Add(st);
        }
        ChaosDeviations.AssertAllMatched(run.Name, run.Expectations);

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                var elapsedMs = (long)(DateTime.UtcNow - baseline).TotalMilliseconds;
                bool allTerminal = true;
                foreach (var s in states)
                {
                    var terminal = ExpectationStep.Apply(s.Timeline, elapsedMs, () =>
                    {
                        var r = evaluator.Evaluate(s.Exp.AssertExpr);
                        s.LastReason = r.Reason;
                        s.SkippedLeaves = r.SkippedLeaves;
                        return r.Outcome;
                    });
                    if (!terminal) allTerminal = false;
                }
                if (allTerminal) break;
                await Task.Delay(pollMs);
            }
            foreach (var s in states) ExpectationStep.Finish(s.Timeline);
        }
        finally
        {
            chaosCts.Cancel();
            try { await Task.WhenAll(chaosTasks); } catch { /* errors were recorded per event */ }
            chaosCts.Dispose();
            try { await client.CloseAsync(); }
            catch (Exception cex) { _out.WriteLine("close failed: " + cex.Message); }
            if (initTask is not null)
            {
                // Observe the init outcome so a raised init is not reported later as an unobserved fault.
                try { await initTask; }
                catch (Exception iex) { _out.WriteLine("init call raised: " + iex.GetType().Name + ": " + iex.Message); }
            }
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        }

        int pass = 0, fail = 0, skipped = 0;
        var failures = new List<string>(chaosErrors);
        var skipLog = ChaosSkipLog.FromEnvironment();
        foreach (var s in states)
        {
            foreach (var note in s.SkippedLeaves)
            {
                skipLog.Record(run.Name ?? "(unnamed)", s.Idx, note);
            }
            if (s.Skipped)
            {
                skipped++;
                _out.WriteLine(
                    $"SKIP  exp[{s.Idx}] within={s.Exp.WithinMs}ms hold={s.Exp.MustHoldForMs}ms: {s.Exp.AssertExpr} — SKIPPED: {string.Join(" | ", s.SkippedLeaves.Select(n => n.Reason).Distinct())}");
            }
            else if (s.Passed)
            {
                pass++;
                var skippedLeaves = s.SkippedLeaves.Count == 0
                    ? string.Empty
                    : "  [not checked: " + string.Join(" | ", s.SkippedLeaves) + "]";
                _out.WriteLine(
                    $"PASS  exp[{s.Idx}] within={s.Exp.WithinMs}ms hold={s.Exp.MustHoldForMs}ms after={s.Exp.AfterMs}ms: {s.Exp.AssertExpr}  (hit at {s.HitAtMs}ms){skippedLeaves}");
            }
            else
            {
                fail++;
                var msg = $"FAIL  exp[{s.Idx}] within={s.Exp.WithinMs}ms hold={s.Exp.MustHoldForMs}ms after={s.Exp.AfterMs}ms: {s.Exp.AssertExpr} — last reason: {s.LastReason}";
                _out.WriteLine(msg);
                failures.Add(msg);
            }
        }
        _out.WriteLine(
            $"scenario summary: {pass} passed, {fail} failed, {skipped} skipped, {chaosErrors.Count} chaos-event errors "
            + $"(state={probe.ConnectionState()}, restartL1={probe.SdkMetric("quonfig_sdk_worker_restart_total", "1").Value}, "
            + $"attempts={probe.SdkMetric("quonfig_sse_connect_attempts_total", null).Value}, fallback={probe.FallbackPollerActive()}, "
            + $"lastRefresh={probe.LastSuccessfulRefreshUtc()?.ToString("o") ?? "null"})");

        if (failures.Count > 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"scenario \"{run.Name}\" — {failures.Count} failure(s) ({fail}/{states.Count} expectations):\n{string.Join("\n", failures)}");
        }
    }

    // ---- chaos injection translation ----

    /// <summary>What an inject needs from the rig.</summary>
    internal sealed class InjectContext
    {
        public InjectContext(ToxiproxyClient tp, int ssePort, string sdkKey)
        {
            Tp = tp;
            SsePort = ssePort;
            SdkKey = sdkKey;
        }

        public ToxiproxyClient Tp { get; }
        public int SsePort { get; }
        public string SdkKey { get; }
    }

    private static async Task FireEventAsync(
        InjectContext rig,
        ChaosScenario.Event ev,
        ConcurrentDictionary<string, InjectionState> injections,
        CancellationToken ct)
    {
        if (ev.Inject is not null)
        {
            var st = await ApplyInjectAsync(rig, ev.Inject);
            if (!string.IsNullOrEmpty(ev.Inject.Name))
            {
                injections[ev.Inject.Name!] = st;
            }
        }
        else if (!string.IsNullOrEmpty(ev.Clear))
        {
            if (!injections.TryRemove(ev.Clear!, out var st))
            {
                throw new InvalidOperationException($"clear \"{ev.Clear}\" names no active injection");
            }
            await ClearInjectAsync(rig.Tp, st);
        }
        else if (ev.Process is not null)
        {
            await ApplyProcessAsync(rig.Tp, ev.Process, ct);
        }
        else
        {
            throw new NotSupportedException($"chaos step at_ms={ev.AtMs} has no inject, clear or process");
        }
    }

    /// <summary>
    /// Applies one inject through toxiproxy. An inject shape this rig does not implement throws
    /// (and so fails the scenario) instead of running as a no-op (qfg-6vno.4).
    /// </summary>
    internal static async Task<InjectionState> ApplyInjectAsync(InjectContext rig, ChaosScenario.Inject inj)
    {
        var tp = rig.Tp;
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
        if (inj.HttpDownMs is not null)
        {
            await tp.SetEnabledAsync("http", false);
            return new InjectionState(null, null, new[] { "http" });
        }
        if (inj.BothDownMs is not null)
        {
            await tp.SetEnabledAsync("sse", false);
            await tp.SetEnabledAsync("http", false);
            return new InjectionState(null, null, new[] { "sse", "http" });
        }
        if (inj.SseHalfOpenAfterBytes is { } afterBytes)
        {
            // "SSE returns 200, writes N body bytes, closes." toxiproxy's limit_data closes a
            // connection after a byte budget that counts the HTTP response head too, so the budget
            // is the measured response-head length plus N: each new attempt gets the 200 headers
            // and N body bytes, then EOF before its first frame — the path the first-frame rule
            // (qfg-goi1.10.41) governs. Replaces the old substitution of disabling the proxy for the
            // whole window (ECONNREFUSED), which never exercised "200 then close".
            int head = await MeasureSseResponseHeadBytesAsync(rig);
            var attrs = new Dictionary<string, object?> { ["bytes"] = head + afterBytes };
            await tp.AddToxicAsync("sse", name, "limit_data", "downstream", attrs);
            // The rug-pull also hits the stream that is already open: limit_data's budget on a
            // live link starts when the toxic lands, and at a ~20-byte keepalive per second it
            // would not run out inside the scenario window (observed: no restart in 25s). Toggling
            // the proxy closes the live link; the toxic stays, so every reconnect meets it.
            await tp.SetEnabledAsync("sse", false);
            await tp.SetEnabledAsync("sse", true);
            return new InjectionState("sse", name, Array.Empty<string>());
        }
        if (inj.SseHttpStatus is not null || inj.HttpStatus is not null)
        {
            // Toxiproxy is TCP-only and cannot rewrite an HTTP status; scenarios-http-proxy (08) is
            // not wired for sdk-net. Fail rather than run the scenario as a no-op.
            throw new NotSupportedException(
                "inject " + DescribeInject(inj) + ": HTTP status injection needs an HTTP-aware proxy; toxiproxy is TCP-only");
        }
        if (!string.IsNullOrEmpty(inj.Proxy) && inj.Toxic is not null)
        {
            var type = inj.Toxic.TryGetValue("type", out var t) ? t?.ToString() : null;
            if (string.IsNullOrEmpty(type))
            {
                throw new NotSupportedException("raw toxic inject without a type");
            }
            var attrs = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (inj.Toxic.TryGetValue("attributes", out var raw)
                && raw is Dictionary<string, object?> rawAttrs)
            {
                foreach (var kv in rawAttrs) attrs[kv.Key] = kv.Value;
            }
            await tp.AddToxicAsync(inj.Proxy!, name, type!, "downstream", attrs);
            return new InjectionState(inj.Proxy, name, Array.Empty<string>());
        }
        throw new NotSupportedException("unsupported inject " + DescribeInject(inj));
    }

    /// <summary>
    /// Length of the SSE response head (status line + headers + blank line) as the fixture
    /// api-delivery sends it through the sse proxy, measured with one raw request.
    /// </summary>
    private static async Task<int> MeasureSseResponseHeadBytesAsync(InjectContext rig)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync("127.0.0.1", rig.SsePort, cts.Token);
        using var stream = tcp.GetStream();
        var auth = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("1:" + rig.SdkKey));
        var req = "GET /api/v2/sse/config HTTP/1.1\r\nHost: 127.0.0.1:" + rig.SsePort
            + "\r\nAuthorization: Basic " + auth
            + "\r\nAccept: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n";
        var bytes = System.Text.Encoding.ASCII.GetBytes(req);
        await stream.WriteAsync(bytes, cts.Token);
        var buf = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one, cts.Token);
            if (n == 0) throw new IOException("SSE endpoint closed before the response head ended");
            buf.Add(one[0]);
            int c = buf.Count;
            if (c >= 4 && buf[c - 4] == '\r' && buf[c - 3] == '\n' && buf[c - 2] == '\r' && buf[c - 1] == '\n')
            {
                var statusLine = System.Text.Encoding.ASCII.GetString(buf.ToArray(), 0, Math.Min(c, 15));
                if (!statusLine.StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
                {
                    throw new IOException("SSE endpoint did not answer 200: " + statusLine);
                }
                return c;
            }
            if (c > 16384) throw new IOException("SSE response head larger than 16KiB");
        }
    }

    private static async Task ClearInjectAsync(ToxiproxyClient tp, InjectionState st)
    {
        if (!string.IsNullOrEmpty(st.Toxic) && !string.IsNullOrEmpty(st.Proxy))
        {
            await tp.RemoveToxicAsync(st.Proxy!, st.Toxic!);
        }
        foreach (var p in st.Enable)
        {
            await tp.SetEnabledAsync(p, true);
        }
    }

    /// <summary>Out-of-band process events. An action this rig does not implement throws.</summary>
    internal static async Task ApplyProcessAsync(ToxiproxyClient tp, ChaosScenario.Process p, CancellationToken ct)
    {
        if (!"kill_sse_proxy".Equals(p.Action, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"unsupported process action \"{p.Action}\"");
        }
        int count = Math.Max(1, p.Count);
        int interval = p.IntervalMs > 0 ? p.IntervalMs : 1000;
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await tp.SetEnabledAsync("sse", false);
            try
            {
                await Task.Delay(200, ct);
            }
            finally
            {
                // Always re-enable, even when the scenario ends mid-kill.
                await tp.SetEnabledAsync("sse", true);
            }
            if (i < count - 1)
            {
                await Task.Delay(Math.Max(0, interval - 200), ct);
            }
        }
    }

    private static string DescribeEvent(ChaosScenario.Event ev)
    {
        if (ev.Inject is not null) return "inject " + DescribeInject(ev.Inject);
        if (!string.IsNullOrEmpty(ev.Clear)) return "clear " + ev.Clear;
        if (ev.Process is not null) return $"process {ev.Process.Action}/{ev.Process.Count}";
        return "(empty step)";
    }

    private static string DescribeInject(ChaosScenario.Inject inj)
    {
        if (inj.SseSilentStallAfterMs is not null) return "sse_silent_stall_after_ms=" + inj.SseSilentStallAfterMs;
        if (inj.SseLatencyMs is not null) return "sse_latency_ms=" + inj.SseLatencyMs;
        if (inj.SseBandwidthKbps is not null) return "sse_bandwidth_kbps=" + inj.SseBandwidthKbps;
        if (inj.SseDownMs is not null) return "sse_down_ms=" + inj.SseDownMs;
        if (inj.BothDownMs is not null) return "both_down_ms=" + inj.BothDownMs;
        if (inj.HttpDownMs is not null) return "http_down_ms=" + inj.HttpDownMs;
        if (inj.HttpStatus is not null) return "http_status=" + inj.HttpStatus;
        if (inj.SseHalfOpenAfterBytes is not null) return "sse_half_open_after_bytes=" + inj.SseHalfOpenAfterBytes;
        if (inj.SseHttpStatus is not null) return "sse_http_status=" + inj.SseHttpStatus;
        if (!string.IsNullOrEmpty(inj.Proxy)) return "proxy=" + inj.Proxy + " toxic";
        return "(unrecognized inject" + (string.IsNullOrEmpty(inj.Name) ? "" : " name=" + inj.Name) + ")";
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

    internal sealed class InjectionState
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
        public ExpState(int idx, ChaosScenario.Expectation exp) { Idx = idx; Timeline = new ExpectationTimeline(exp); }
        public int Idx { get; }
        public ExpectationTimeline Timeline { get; }
        public ChaosScenario.Expectation Exp => Timeline.Exp;
        public long HitAtMs => Timeline.HitAtMs;
        public bool Passed => Timeline.Passed;
        public bool Skipped => Timeline.Skipped;
        public string LastReason { get; set; } = string.Empty;
        public IReadOnlyList<ExpressionEvaluator.SkipNote> SkippedLeaves { get; set; } =
            Array.Empty<ExpressionEvaluator.SkipNote>();
    }
}
