using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Quonfig.Sdk;
using Xunit;
using Xunit.Abstractions;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// Failover + canonical-ordering chaos rigs for sdk-net (bead qfg-7h5d.1.11) — the Phase 2 port of
/// the sdk-go pilot. Drives the shared corpus in <c>integration-test-data/chaos/scenarios-failover/</c>
/// and <c>scenarios-ordering/</c> against a real sdk-net client.
///
/// <para>Two rigs, per the plan's topology:</para>
/// <list type="bullet">
///   <item><description><b>failover</b> — ONE fixture upstream behind the primary (<c>http</c>) +
///     <c>secondary</c> proxies. Faults hit the primary leg; the SDK must fail the HTTP config
///     fetch over to the secondary, fast (f01-f05).</description></item>
///   <item><description><b>ordering</b> — TWO fixture upstreams pinned to divergent
///     <c>Meta.generation</c>s (per scenario). A background refresh loop models ongoing polling so
///     the reject-older guard is exercised on the failover/refresh install path (o01-o04).</description></item>
/// </list>
///
/// <para>Unlike the single-upstream <see cref="ChaosTests"/>, these rigs spawn their own
/// api-delivery fixture upstream(s) from the prebuilt binary, so each scenario gets a clean,
/// generation-pinned server. The wrapper <c>scripts/run-failover-chaos.sh</c> builds the binary,
/// boots toxiproxy, and sets the env knobs below.</para>
///
/// <para>Gated on <c>QUONFIG_FAILOVER_CHAOS_RUN=1</c> (separate from the single-upstream suite's
/// <c>QUONFIG_CHAOS_RUN</c>) so the two never cross-trigger. Env knobs:</para>
/// <list type="bullet">
///   <item><c>CHAOS_API_DELIVERY_BIN</c> — path to the prebuilt api-delivery binary (required).</item>
///   <item><c>CHAOS_FIXTURE_DIR</c> — FIXTURE_DIR served by each upstream (required).</item>
///   <item><c>CHAOS_SDK_KEYS_FILE</c> — SDK_KEYS_FILE for each upstream (required).</item>
///   <item><c>CHAOS_UPSTREAM_HOST</c> — host toxiproxy uses to reach the upstreams (default <c>host.docker.internal</c>).</item>
///   <item><c>TOXIPROXY_URL</c> — admin API, default <c>http://127.0.0.1:8474</c>.</item>
///   <item><c>CHAOS_SSE_PORT</c>/<c>CHAOS_HTTP_PORT</c>/<c>CHAOS_SECONDARY_PORT</c> — host proxy ports (18550/18551/18552).</item>
///   <item><c>CHAOS_FIXTURE_SDK_KEY</c> — backend SDK key matching the fixture (default <c>test-backend-key</c>).</item>
///   <item><c>CHAOS_ONLY</c>/<c>CHAOS_SKIP</c> — comma scenario filters matched against the file base name (e.g. <c>o01-secondary-newer</c> or <c>f02</c>).</item>
///   <item><c>CHAOS_POLL_MS</c> — expectation poll interval (default 200).</item>
/// </list>
/// </summary>
[Trait("Category", "FailoverChaos")]
public sealed class FailoverChaosTests
{
    private readonly ITestOutputHelper _out;

    public FailoverChaosTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private const string GateEnv = "QUONFIG_FAILOVER_CHAOS_RUN";

    public static IEnumerable<object[]> FailoverFiles() => FilesIn(ChaosPaths.FailoverScenariosDir());

    public static IEnumerable<object[]> OrderingFiles() => FilesIn(ChaosPaths.OrderingScenariosDir());

    private static IEnumerable<object[]> FilesIn(string dir)
    {
        if (!Directory.Exists(dir))
        {
            yield return new object[] { "(no-scenarios-found)" };
            yield break;
        }
        foreach (var path in Directory.EnumerateFiles(dir, "*.yaml").OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return new object[] { Path.GetFileName(path) };
        }
    }

    [SkippableTheory]
    [MemberData(nameof(FailoverFiles))]
    public async Task Failover(string fileName)
    {
        Skip.IfNot(Environment.GetEnvironmentVariable(GateEnv) == "1",
            $"{GateEnv}!=1 — failover chaos suite gated to opt-in runs");
        Skip.If(fileName == "(no-scenarios-found)", "integration-test-data sibling not found");
        SkipForFilters(fileName);

        var scenario = ChaosYamlLoader.Load(Path.Combine(ChaosPaths.FailoverScenariosDir(), fileName));
        var env = RigEnv.Read();
        using var tp = await ConnectToxiproxyAsync(env).ConfigureAwait(false);

        foreach (var run in scenario.Tests)
        {
            // One upstream (generation 0) behind both HTTP legs + SSE. Identical content on both
            // legs proves failover routing, not divergent data (that's the ordering rig).
            var upstream = await SpawnUpstreamAsync(env, generation: 0).ConfigureAwait(false);
            try
            {
                await ReconfigureProxiesAsync(tp, env, upstream.Port, upstream.Port, upstream.Port).ConfigureAwait(false);
                await RunRigScenarioAsync(tp, env, run, ordering: false).ConfigureAwait(false);
            }
            finally
            {
                upstream.Dispose();
            }
        }
    }

    [SkippableTheory]
    [MemberData(nameof(OrderingFiles))]
    public async Task Ordering(string fileName)
    {
        Skip.IfNot(Environment.GetEnvironmentVariable(GateEnv) == "1",
            $"{GateEnv}!=1 — ordering chaos suite gated to opt-in runs");
        Skip.If(fileName == "(no-scenarios-found)", "integration-test-data sibling not found");
        SkipForFilters(fileName);

        var scenario = ChaosYamlLoader.Load(Path.Combine(ChaosPaths.OrderingScenariosDir(), fileName));
        var env = RigEnv.Read();
        using var tp = await ConnectToxiproxyAsync(env).ConfigureAwait(false);

        foreach (var run in scenario.Tests)
        {
            var (primaryGen, secondaryGen) = UpstreamGenerations(run.Setup.Upstreams);
            // Two upstreams pinned to the scenario's divergent generations.
            var primary = await SpawnUpstreamAsync(env, primaryGen).ConfigureAwait(false);
            var secondary = await SpawnUpstreamAsync(env, secondaryGen).ConfigureAwait(false);
            try
            {
                await ReconfigureProxiesAsync(tp, env, primary.Port, secondary.Port, primary.Port).ConfigureAwait(false);
                await RunRigScenarioAsync(tp, env, run, ordering: true).ConfigureAwait(false);
            }
            finally
            {
                secondary.Dispose();
                primary.Dispose();
            }
        }
    }

    // ----------------------------------------------------------------------------------------
    // Scenario runner
    // ----------------------------------------------------------------------------------------

    private async Task RunRigScenarioAsync(ToxiproxyClient tp, RigEnv env, ChaosScenario.Run run, bool ordering)
    {
        // Reset proxy state between runs.
        foreach (var p in new[] { "http", "secondary", "sse" })
        {
            await tp.ClearToxicsAsync(p).ConfigureAwait(false);
            await tp.SetEnabledAsync(p, true).ConfigureAwait(false);
        }

        var httpBase = "http://127.0.0.1:" + env.HttpPort;
        var secondaryBase = "http://127.0.0.1:" + env.SecondaryPort;
        var sseBase = "http://127.0.0.1:" + env.SsePort;
        bool sseEnabled = string.Equals(run.Setup.SseEndpoint, "chaos", StringComparison.Ordinal);

        var opts = new QuonfigOptions
        {
            SdkKey = env.SdkKey,
            ApiUrls = new[] { httpBase, secondaryBase },
            // SSE has a single endpoint (the primary stream). It is NOT given the secondary — the
            // SDK does not fail SSE over (an explicit design choice asserted by f05). When the
            // scenario disables SSE we leave StreamUrls empty so only the HTTP path runs.
            StreamUrls = sseEnabled ? new[] { sseBase } : Array.Empty<string>(),
            // Per-URL config-fetch timeout: failover needs a tight bound (~2.5s) so a hung/slow
            // primary sheds well inside the 4s failover budget. Ordering needs a bound ABOVE the
            // ~3s primary-latency toxic so a latent-but-healthy primary still answers and the newer
            // generation can heal forward.
            ConfigFetchTimeout = ordering ? TimeSpan.FromSeconds(8) : TimeSpan.FromMilliseconds(2500),
            InitTimeout = TimeSpan.FromSeconds(10),
            // Don't throw on a slow first fetch — the rig observes readiness from the install
            // timestamp, and background work (refresh loop / SSE) keeps trying.
            OnInitFailure = OnInitFailure.ReturnDefaults,
            OnNoDefault = OnNoDefault.Ignore,
        };

        // Pre-stage chaos events that fire at t=0 so the fault is present before the initial fetch
        // (f01 refused, f02 hang, f03/o03 latency). Remaining events + all restores are scheduled
        // relative to the baseline.
        var scheduled = new List<(int delayMs, Func<Task> action)>();
        var baseline = DateTime.UtcNow;
        if (run.Chaos is not null)
        {
            foreach (var ev in run.Chaos)
            {
                if (ev.Inject is null) continue;
                var (apply, restore, restoreAfterMs) = TranslateInject(tp, ev.Inject);
                if (ev.AtMs <= 0)
                {
                    await apply().ConfigureAwait(false);
                    _out.WriteLine($"[     0ms] inject {Describe(ev.Inject)} (pre-staged)");
                }
                else
                {
                    scheduled.Add((ev.AtMs, async () =>
                    {
                        await apply().ConfigureAwait(false);
                        _out.WriteLine($"[{ev.AtMs,6}ms] inject {Describe(ev.Inject)}");
                    }
                    ));
                }
                if (restore is not null && restoreAfterMs is { } d)
                {
                    scheduled.Add((Math.Max(ev.AtMs, 0) + d, async () =>
                    {
                        await restore().ConfigureAwait(false);
                        _out.WriteLine($"[{Math.Max(ev.AtMs, 0) + d,6}ms] restore {Describe(ev.Inject)}");
                    }
                    ));
                }
            }
        }

        var client = new Quonfig(opts);
        var probe = new FailoverProbe(client, sseConfiguredUrls: opts.StreamUrls.Count);
        var evaluator = new FailoverEvaluator(probe);

        var bgCts = new CancellationTokenSource();
        var bgTasks = new List<Task>();

        // Schedule chaos.
        foreach (var (delayMs, action) in scheduled)
        {
            bgTasks.Add(Task.Run(async () =>
            {
                var fireAt = baseline + TimeSpan.FromMilliseconds(delayMs);
                var wait = fireAt - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    try { await Task.Delay(wait, bgCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
                try { await action().ConfigureAwait(false); }
                catch (Exception ex) { _out.WriteLine("chaos event failed: " + ex.Message); }
            }));
        }

        // Ordering rig: a background refresh loop models ongoing config polling so the reject-older
        // guard (and heal-forward) is exercised on the failover/refresh install path.
        if (ordering)
        {
            bgTasks.Add(Task.Run(async () =>
            {
                while (!bgCts.IsCancellationRequested)
                {
                    try { await client.RefreshAsync(bgCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { _out.WriteLine("refresh failed: " + ex.Message); }
                    try { await Task.Delay(TimeSpan.FromMilliseconds(500), bgCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }));
        }

        int wallClockS = run.Setup.WallClockSeconds > 0 ? run.Setup.WallClockSeconds : 30;
        var deadline = baseline + TimeSpan.FromSeconds(wallClockS);
        int pollMs = int.Parse(EnvOr("CHAOS_POLL_MS", "200"));
        var states = run.Expectations.Select((e, i) => new ExpState(i, e)).ToList();

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                var now = DateTime.UtcNow;
                long elapsedMs = (long)(now - baseline).TotalMilliseconds;
                bool allTerminal = true;
                foreach (var s in states)
                {
                    if (s.Passed || s.Failed) continue;
                    var r = evaluator.Evaluate(s.Exp.AssertExpr);
                    s.LastReason = r.reason;
                    if (r.passed)
                    {
                        s.HeldSince ??= now;
                        if (s.HitAtMs < 0) s.HitAtMs = elapsedMs;
                        var holdFor = TimeSpan.FromMilliseconds(s.Exp.MustHoldForMs);
                        if (holdFor <= TimeSpan.Zero || (now - s.HeldSince.Value) >= holdFor) s.Passed = true;
                        else allTerminal = false;
                    }
                    else
                    {
                        s.HeldSince = null;
                        allTerminal = false;
                    }
                    if (!s.Passed && elapsedMs > s.Exp.WithinMs) s.Failed = true;
                }
                if (allTerminal) break;
                await Task.Delay(pollMs).ConfigureAwait(false);
            }
            foreach (var s in states)
            {
                if (!s.Passed) s.Failed = true;
            }
        }
        finally
        {
            bgCts.Cancel();
            try { await Task.WhenAll(bgTasks).ConfigureAwait(false); } catch { /* best-effort */ }
            bgCts.Dispose();
            try { await client.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { _out.WriteLine("close failed: " + ex.Message); }
        }

        int pass = 0;
        var failures = new List<string>();
        foreach (var s in states)
        {
            if (s.Passed)
            {
                pass++;
                _out.WriteLine($"PASS  exp[{s.Idx}] within={s.Exp.WithinMs} hold={s.Exp.MustHoldForMs}: {s.Exp.AssertExpr} (hit {s.HitAtMs}ms)");
            }
            else
            {
                _out.WriteLine($"FAIL  exp[{s.Idx}] within={s.Exp.WithinMs} hold={s.Exp.MustHoldForMs}: {s.Exp.AssertExpr} — last: {s.LastReason}");
                failures.Add($"exp[{s.Idx}] {s.Exp.AssertExpr} — last: {s.LastReason}");
            }
        }
        _out.WriteLine($"scenario summary: {pass} passed, {failures.Count} failed " +
            $"(ready={probe.Ready()}, held={probe.HeldGeneration()}, resolvedFrom={probe.ResolvedFrom()}, installs={probe.ConfigInstallCount()})");

        if (failures.Count > 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"scenario \"{run.Name}\" — {failures.Count}/{states.Count} expectations failed:\n{string.Join("\n", failures)}");
        }
    }

    // ----------------------------------------------------------------------------------------
    // Fault translation — primary HTTP leg ('http' proxy) and SSE leg ('sse' proxy)
    // ----------------------------------------------------------------------------------------

    private static (Func<Task> apply, Func<Task>? restore, int? restoreAfterMs) TranslateInject(
        ToxiproxyClient tp, ChaosScenario.Inject inj)
    {
        var name = string.IsNullOrEmpty(inj.Name) ? "anon" : inj.Name!;
        if (inj.PrimaryRefusedMs is { } refused)
        {
            // Port closed: disable the primary proxy so the SDK sees ECONNREFUSED, restore after the duration.
            return (() => tp.SetEnabledAsync("http", false), () => tp.SetEnabledAsync("http", true), refused);
        }
        if (inj.PrimaryHangMs is { } hang)
        {
            // 'timeout' toxic with timeout=0: accept the connection but never deliver data (hang)
            // until removed. Restore after the duration.
            var attrs = new Dictionary<string, object?> { ["timeout"] = 0 };
            return (() => tp.AddToxicAsync("http", name, "timeout", "downstream", attrs),
                    () => tp.RemoveToxicAsync("http", name), hang);
        }
        if (inj.PrimaryLatencyMs is { } latency)
        {
            // 'latency' toxic carries a latency VALUE that persists for the rest of the run.
            var attrs = new Dictionary<string, object?> { ["latency"] = latency };
            return (() => tp.AddToxicAsync("http", name, "latency", "downstream", attrs), null, null);
        }
        if (inj.SseDownMs is { } sseDown)
        {
            return (() => tp.SetEnabledAsync("sse", false), () => tp.SetEnabledAsync("sse", true), sseDown);
        }
        // Unknown inject — no-op.
        return (() => Task.CompletedTask, null, null);
    }

    private static string Describe(ChaosScenario.Inject inj)
    {
        if (inj.PrimaryRefusedMs is { } r) return $"primary_refused_ms={r}";
        if (inj.PrimaryHangMs is { } h) return $"primary_hang_ms={h}";
        if (inj.PrimaryLatencyMs is { } l) return $"primary_latency_ms={l}";
        if (inj.SseDownMs is { } s) return $"sse_down_ms={s}";
        return "?";
    }

    // ----------------------------------------------------------------------------------------
    // Upstream spawning + proxy wiring
    // ----------------------------------------------------------------------------------------

    private async Task<UpstreamProc> SpawnUpstreamAsync(RigEnv env, int generation)
    {
        int port = FreePort();
        var psi = new ProcessStartInfo
        {
            FileName = env.BinPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["PORT"] = port.ToString();
        psi.Environment["FIXTURE_DIR"] = env.FixtureDir;
        psi.Environment["SDK_KEYS_FILE"] = env.SdkKeysFile;
        psi.Environment["QUONFIG_ENVIRONMENT"] = "development";
        psi.Environment["SSE_HEARTBEAT_INTERVAL"] = "1s";
        psi.Environment["FIXTURE_GENERATION"] = generation.ToString();

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start api-delivery upstream");
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) _out.WriteLine($"[upstream:{port}] {e.Data}"); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) _out.WriteLine($"[upstream:{port}] {e.Data}"); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await WaitForHealthzAsync(port).ConfigureAwait(false);
        _out.WriteLine($"==> upstream up on :{port} (FIXTURE_GENERATION={generation})");
        return new UpstreamProc(proc, port);
    }

    private static async Task WaitForHealthzAsync(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var url = $"http://127.0.0.1:{port}/healthz";
        for (int i = 0; i < 40; i++)
        {
            try
            {
                using var resp = await http.GetAsync(url).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return;
            }
            catch (Exception) { /* not up yet */ }
            await Task.Delay(250).ConfigureAwait(false);
        }
        throw new InvalidOperationException($"api-delivery upstream did not become healthy on :{port} within 10s");
    }

    private static async Task ReconfigureProxiesAsync(
        ToxiproxyClient tp, RigEnv env, int httpUpstreamPort, int secondaryUpstreamPort, int sseUpstreamPort)
    {
        await tp.UpsertProxyAsync("http", "0.0.0.0:" + env.HttpPort, $"{env.UpstreamHost}:{httpUpstreamPort}").ConfigureAwait(false);
        await tp.UpsertProxyAsync("secondary", "0.0.0.0:" + env.SecondaryPort, $"{env.UpstreamHost}:{secondaryUpstreamPort}").ConfigureAwait(false);
        await tp.UpsertProxyAsync("sse", "0.0.0.0:" + env.SsePort, $"{env.UpstreamHost}:{sseUpstreamPort}").ConfigureAwait(false);
    }

    private static async Task<ToxiproxyClient> ConnectToxiproxyAsync(RigEnv env)
    {
        var tp = new ToxiproxyClient(env.ToxiproxyUrl);
        try
        {
            await tp.PingAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            tp.Dispose();
            throw new InvalidOperationException(
                $"toxiproxy not reachable at {env.ToxiproxyUrl}: {ex.Message} — run scripts/run-failover-chaos.sh", ex);
        }
        return tp;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static (int primary, int secondary) UpstreamGenerations(IReadOnlyList<ChaosScenario.Upstream> ups)
    {
        int primary = 0, secondary = 0;
        foreach (var u in ups)
        {
            if (string.Equals(u.Role, "primary", StringComparison.Ordinal)) primary = u.Generation;
            else if (string.Equals(u.Role, "secondary", StringComparison.Ordinal)) secondary = u.Generation;
        }
        return (primary, secondary);
    }

    private static void SkipForFilters(string fileName)
    {
        var baseName = fileName.EndsWith(".yaml", StringComparison.Ordinal)
            ? fileName.Substring(0, fileName.Length - 5) : fileName;
        var only = CsvSet(Environment.GetEnvironmentVariable("CHAOS_ONLY"));
        var skip = CsvSet(Environment.GetEnvironmentVariable("CHAOS_SKIP"));
        Skip.If(only.Count > 0 && !only.Any(o => baseName.Contains(o, StringComparison.Ordinal)),
            $"CHAOS_ONLY={string.Join(",", only)} — {baseName} not selected");
        Skip.If(skip.Any(s => baseName.Contains(s, StringComparison.Ordinal)),
            $"CHAOS_SKIP={string.Join(",", skip)} — {baseName} skipped");
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

    private static string EnvOr(string key, string def)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrEmpty(v) ? def : v!;
    }

    // ----------------------------------------------------------------------------------------
    // Support types
    // ----------------------------------------------------------------------------------------

    private sealed class RigEnv
    {
        public string BinPath { get; private set; } = "";
        public string FixtureDir { get; private set; } = "";
        public string SdkKeysFile { get; private set; } = "";
        public string UpstreamHost { get; private set; } = "host.docker.internal";
        public string ToxiproxyUrl { get; private set; } = "http://127.0.0.1:8474";
        public int SsePort { get; private set; } = 18550;
        public int HttpPort { get; private set; } = 18551;
        public int SecondaryPort { get; private set; } = 18552;
        public string SdkKey { get; private set; } = "test-backend-key";

        public static RigEnv Read()
        {
            string Require(string key)
            {
                var v = Environment.GetEnvironmentVariable(key);
                if (string.IsNullOrEmpty(v))
                {
                    throw new InvalidOperationException(
                        $"{key} is required for the failover/ordering rig — run scripts/run-failover-chaos.sh");
                }
                return v!;
            }
            return new RigEnv
            {
                BinPath = Require("CHAOS_API_DELIVERY_BIN"),
                FixtureDir = Require("CHAOS_FIXTURE_DIR"),
                SdkKeysFile = Require("CHAOS_SDK_KEYS_FILE"),
                UpstreamHost = EnvOr("CHAOS_UPSTREAM_HOST", "host.docker.internal"),
                ToxiproxyUrl = EnvOr("TOXIPROXY_URL", "http://127.0.0.1:8474"),
                SsePort = int.Parse(EnvOr("CHAOS_SSE_PORT", "18550")),
                HttpPort = int.Parse(EnvOr("CHAOS_HTTP_PORT", "18551")),
                SecondaryPort = int.Parse(EnvOr("CHAOS_SECONDARY_PORT", "18552")),
                SdkKey = EnvOr("CHAOS_FIXTURE_SDK_KEY", "test-backend-key"),
            };
        }
    }

    private sealed class UpstreamProc : IDisposable
    {
        private readonly Process _proc;
        public int Port { get; }

        public UpstreamProc(Process proc, int port) { _proc = proc; Port = port; }

        public void Dispose()
        {
            try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            try { _proc.WaitForExit(5000); } catch { /* best-effort */ }
            _proc.Dispose();
        }
    }

    private sealed class ExpState
    {
        public ExpState(int idx, ChaosScenario.Expectation exp) { Idx = idx; Exp = exp; }
        public int Idx { get; }
        public ChaosScenario.Expectation Exp { get; }
        public long HitAtMs { get; set; } = -1;
        public DateTime? HeldSince { get; set; }
        public bool Passed { get; set; }
        public bool Failed { get; set; }
        public string LastReason { get; set; } = "";
    }

    /// <summary>
    /// Observation surface for the failover/ordering assertions, read straight off the live client's
    /// internal diagnostics (exposed to this assembly via InternalsVisibleTo). Readiness is keyed on
    /// the install timestamp (HTTP config available), not the SSE connection state, so it is robust
    /// while SSE flaps in f05.
    /// </summary>
    private sealed class FailoverProbe
    {
        private readonly Quonfig _client;
        private readonly int _sseConfiguredUrls;

        public FailoverProbe(Quonfig client, int sseConfiguredUrls)
        {
            _client = client;
            _sseConfiguredUrls = sseConfiguredUrls;
        }

        public bool Ready() => _client.LastSuccessfulRefresh is not null;

        public int HeldGeneration() => _client.HeldGeneration;

        public int ConfigInstallCount() => _client.NetworkInstallCount;

        public string ResolvedFrom() => _client.ResolvedFrom;

        // sdk-net is configured with a single SSE endpoint (the primary stream) and the SDK does not
        // repoint SSE to a secondary — so a secondary SSE failover is structurally impossible. f05
        // asserts exactly this design choice.
        public bool SseFailedOverToSecondary() => false && _sseConfiguredUrls > 1;
    }

    /// <summary>
    /// Tiny evaluator for the failover/ordering assertion grammar:
    /// <c>client.ready()</c>, <c>client.resolvedFrom()</c>, <c>client.heldGeneration()</c>,
    /// <c>client.configInstallCount()</c>, <c>client.sseFailedOverToSecondary()</c>, joined by
    /// <c> AND </c>/<c> OR </c>. Reuses <see cref="ExpressionEvaluator.SplitOutsideQuotesAndRegex"/>
    /// for the boolean split so quoted operands aren't mis-parsed.
    /// </summary>
    private sealed class FailoverEvaluator
    {
        private static readonly Regex ReReady = new(@"^client\.ready\(\)\s*==\s*(true|false)$", RegexOptions.Compiled);
        private static readonly Regex ReResolved = new(@"^client\.resolvedFrom\(\)\s*==\s*'([^']+)'$", RegexOptions.Compiled);
        private static readonly Regex ReHeld = new(@"^client\.heldGeneration\(\)\s*(>=|<=|==|!=|<|>)\s*(-?\d+)$", RegexOptions.Compiled);
        private static readonly Regex ReInstalls = new(@"^client\.configInstallCount\(\)\s*(>=|<=|==|!=|<|>)\s*(\d+)$", RegexOptions.Compiled);
        private static readonly Regex ReSseFailover = new(@"^client\.sseFailedOverToSecondary\(\)\s*==\s*(true|false)$", RegexOptions.Compiled);

        private readonly FailoverProbe _probe;

        public FailoverEvaluator(FailoverProbe probe) { _probe = probe; }

        public (bool passed, string reason) Evaluate(string expr)
        {
            var e = (expr ?? "").Trim();
            if (e.Length == 0) return (true, "");
            if (e.Contains(" OR ", StringComparison.Ordinal))
            {
                var reasons = new List<string>();
                foreach (var p in ExpressionEvaluator.SplitOutsideQuotesAndRegex(e, " OR "))
                {
                    var r = Evaluate(p);
                    if (r.passed) return (true, "");
                    reasons.Add(r.reason);
                }
                return (false, "OR: " + string.Join(" | ", reasons));
            }
            if (e.Contains(" AND ", StringComparison.Ordinal))
            {
                foreach (var p in ExpressionEvaluator.SplitOutsideQuotesAndRegex(e, " AND "))
                {
                    var r = Evaluate(p);
                    if (!r.passed) return (false, "AND: " + r.reason);
                }
                return (true, "");
            }
            return Leaf(e);
        }

        private (bool, string) Leaf(string expr)
        {
            Match m;
            if ((m = ReReady.Match(expr)).Success)
            {
                bool want = bool.Parse(m.Groups[1].Value);
                bool got = _probe.Ready();
                return (got == want, $"ready={got} want {want}");
            }
            if ((m = ReResolved.Match(expr)).Success)
            {
                var want = m.Groups[1].Value;
                var got = _probe.ResolvedFrom();
                return (got == want, $"resolvedFrom={got} want {want}");
            }
            if ((m = ReHeld.Match(expr)).Success)
            {
                int got = _probe.HeldGeneration();
                int want = int.Parse(m.Groups[2].Value);
                return (Compare(m.Groups[1].Value, got, want), $"heldGeneration={got} {m.Groups[1].Value} {want}");
            }
            if ((m = ReInstalls.Match(expr)).Success)
            {
                int got = _probe.ConfigInstallCount();
                int want = int.Parse(m.Groups[2].Value);
                return (Compare(m.Groups[1].Value, got, want), $"configInstallCount={got} {m.Groups[1].Value} {want}");
            }
            if ((m = ReSseFailover.Match(expr)).Success)
            {
                bool want = bool.Parse(m.Groups[1].Value);
                bool got = _probe.SseFailedOverToSecondary();
                return (got == want, $"sseFailedOverToSecondary={got} want {want}");
            }
            return (false, "unrecognized expression: " + expr);
        }

        private static bool Compare(string op, int a, int b) => op switch
        {
            "==" => a == b,
            "!=" => a != b,
            "<" => a < b,
            "<=" => a <= b,
            ">" => a > b,
            ">=" => a >= b,
            _ => false,
        };
    }
}
