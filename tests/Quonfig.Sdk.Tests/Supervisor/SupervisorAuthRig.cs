using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Quonfig.Sdk.Telemetry;
using Xunit.Sdk;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Quonfig.Sdk.Tests.Supervisor;

/// <summary>
/// Rig for supervisor contract Test 8 (auth-class response, qfg-goi1.10.28;
/// integration-test-data chaos/supervisor-test-contract.md at v2026.10.16). Port of sdk-go's
/// <c>supervisor_auth_rig_test.go</c>.
///
/// <para>The supervisor, the SSE client, the fallback-poller schedule and the auth-failed state
/// machine are all real. Only these are faked:</para>
/// <list type="bullet">
///   <item><description>the network: one <see cref="HttpMessageHandler"/> that answers the SSE
///     host with a scripted status and each config-fetch leg with a per-leg, per-cycle script
///     <c>(status, envelope)</c>;</description></item>
///   <item><description>the clock: every background wait the SDK requests goes through
///     <see cref="QuonfigOptions.TestWaitHook"/>, which records the requested duration and waits
///     1/<see cref="Compression"/> of it, so the tests assert the requested values (the
///     contract's anti-vacuity rule) without sleeping minutes.</description></item>
/// </list>
/// <para>Waits, hits (with the client state at that moment) and log records land in one ordered
/// event log, so the assertions can cut it at the entry ERROR and the exit INFO.</para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA2000",
    Justification = "The client is closed in DisposeAsync; the handler and logger live as long as the rig.")]
internal sealed class SupervisorAuthRig : IAsyncDisposable
{
    internal const string Key = "t8-secret-sdk-key";
    internal const int Compression = 2000;
    internal const string PrimaryUrl = "http://primary.t8.test";
    internal const string SecondaryUrl = "http://secondary.t8.test";
    internal const string StreamUrl = "http://stream.t8.test";

    internal static readonly int[] AuthStatuses = { 401, 403, 404, 410 };

    internal sealed class Event
    {
        public Event(string kind, TimeSpan duration = default, MelLogLevel level = MelLogLevel.None,
            string message = "", ConnectionState state = ConnectionState.Initializing, bool active = false)
        {
            Kind = kind;
            Duration = duration;
            Level = level;
            Message = message;
            State = state;
            Active = active;
        }

        /// <summary><c>wait:&lt;kind&gt;</c>, <c>hit:sse</c>, <c>hit:0</c>, <c>hit:1</c>, or <c>log</c>.</summary>
        public string Kind { get; }
        public TimeSpan Duration { get; }
        public MelLogLevel Level { get; }
        public string Message { get; }
        public ConnectionState State { get; }
        public bool Active { get; }

        public override string ToString() => Kind switch
        {
            "log" => $"log {Level} {Message}",
            _ when Kind.StartsWith("wait:", StringComparison.Ordinal) => $"{Kind} {Duration}",
            _ => $"{Kind} state={State} active={Active}",
        };
    }

    /// <summary>One leg's scripted answer for one fetch cycle.</summary>
    internal readonly struct Resp
    {
        public Resp(int status, int gen = 0, TimeSpan delay = default, bool exit = false)
        {
            Status = status;
            Gen = gen;
            Delay = delay;
            Exit = exit;
        }

        /// <summary>200 sends a valid envelope at <see cref="Gen"/>; 304 sends Not Modified.</summary>
        public int Status { get; }
        public int Gen { get; }
        /// <summary>Wait before answering (timeout sub-tests); honors the request's cancellation.</summary>
        public TimeSpan Delay { get; }
        /// <summary>Mark the rig "exited" when served (SSE then answers <see cref="SseAfter"/>).</summary>
        public bool Exit { get; }
    }

    private readonly object _gate = new();
    private readonly List<Event> _events = new();
    private int _leg0Calls;
    private int _exited;
    private Quonfig? _client;

    /// <summary>SSE status before the exit response is served.</summary>
    public int SseStatus { get; set; } = 503;

    /// <summary>SSE status after it (0 = same as <see cref="SseStatus"/>).</summary>
    public int SseAfter { get; set; }

    /// <summary>
    /// The leg's answer for fetch cycle n (n counts primary-leg requests, so cycle 1 is the init
    /// fetch).
    /// </summary>
    public Func<int, int, Resp> Leg { get; set; } = (_, _) => new Resp(200, 1);

    public int Leg0Calls => Volatile.Read(ref _leg0Calls);

    public bool Exited => Volatile.Read(ref _exited) != 0;

    public Quonfig Client => _client ?? throw new InvalidOperationException("rig not started");

    public void Record(Event e)
    {
        lock (_gate) { _events.Add(e); }
    }

    public List<Event> Snapshot()
    {
        lock (_gate) { return _events.ToList(); }
    }

    private TimeSpan Wait(string kind, TimeSpan requested)
    {
        Record(new Event("wait:" + kind, requested));
        return TimeSpan.FromTicks(requested.Ticks / Compression);
    }

    private (ConnectionState, bool) ClientState()
    {
        var c = Volatile.Read(ref _client);
        return c is null ? (ConnectionState.Initializing, false) : (c.ConnectionState, c.FallbackPollerActive);
    }

    private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri!.Host;
        var (state, active) = ClientState();
        if (host == new Uri(StreamUrl).Host)
        {
            Record(new Event("hit:sse", state: state, active: active));
            int status = Exited && SseAfter != 0 ? SseAfter : SseStatus;
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(status.ToString(CultureInfo.InvariantCulture)) };
        }

        int leg = host == new Uri(PrimaryUrl).Host ? 0 : 1;
        int cycle = leg == 0 ? Interlocked.Increment(ref _leg0Calls) : Volatile.Read(ref _leg0Calls);
        Record(new Event("hit:" + leg.ToString(CultureInfo.InvariantCulture), state: state, active: active));
        var resp = Leg(leg, cycle);
        if (resp.Delay > TimeSpan.Zero)
        {
            await Task.Delay(resp.Delay, ct).ConfigureAwait(false);
        }
        if (resp.Exit)
        {
            Interlocked.Exchange(ref _exited, 1);
        }
        switch (resp.Status)
        {
            case 200:
                var body = string.Format(CultureInfo.InvariantCulture,
                    "{{\"configs\":[],\"meta\":{{\"version\":\"gen-{0}\",\"environment\":\"Production\",\"generation\":{0}}}}}",
                    resp.Gen);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            default:
                return new HttpResponseMessage((HttpStatusCode)resp.Status)
                {
                    Content = new StringContent(string.Empty),
                };
        }
    }

    /// <summary>Builds the client (1 or 2 legs) and waits for init.</summary>
    public async Task StartAsync(int legs = 2, Action<QuonfigOptions>? configure = null)
    {
        var opts = new QuonfigOptions
        {
            SdkKey = Key,
            ApiUrls = legs == 1 ? new[] { PrimaryUrl } : new[] { PrimaryUrl, SecondaryUrl },
            StreamUrls = new[] { StreamUrl },
            InitTimeout = TimeSpan.FromSeconds(8),
            OnInitFailure = OnInitFailure.ReturnDefaults,
            OnNoDefault = OnNoDefault.Ignore,
            TelemetrySender = new NoopSender(),
            TelemetryInitialDelay = TimeSpan.FromMinutes(5),
            HttpMessageHandler = new Handler(this),
            Logger = new RigLogger(this),
            TestWaitHook = Wait,
        };
        configure?.Invoke(opts);
        var c = new Quonfig(opts);
        Volatile.Write(ref _client, c);
        await c.InitAsync().ConfigureAwait(false);
    }

    /// <summary>Polls <paramref name="cond"/> for up to 10s of wall clock.</summary>
    public async Task<List<Event>> AwaitAsync(string what, Func<List<Event>, bool> cond)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var ev = Snapshot();
            if (cond(ev)) return ev;
            await Task.Delay(2).ConfigureAwait(false);
        }
        var last = Snapshot();
        throw new XunitException($"timed out waiting for {what}; events:\n{Dump(last)}");
    }

    public async ValueTask DisposeAsync()
    {
        var c = Volatile.Read(ref _client);
        if (c is not null) await c.CloseAsync().ConfigureAwait(false);
    }

    // ---- event-log helpers ----------------------------------------------------------------

    public static bool HasText(string haystack, string needle) =>
#if NET8_0_OR_GREATER
        haystack.Contains(needle, StringComparison.Ordinal);
#else
        haystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
#endif

    public static int Count(IEnumerable<Event> ev, string kind) => ev.Count(e => e.Kind == kind);

    /// <summary>Index of the nth (1-based) event of <paramref name="kind"/> at or after
    /// <paramref name="from"/>, or -1.</summary>
    public static int Index(IReadOnlyList<Event> ev, int from, string kind, int nth = 1)
    {
        if (from < 0) return -1;
        for (int i = from; i < ev.Count; i++)
        {
            if (ev[i].Kind == kind && --nth == 0) return i;
        }
        return -1;
    }

    /// <summary>Index of the first log at <paramref name="level"/> at or after
    /// <paramref name="from"/>, or -1.</summary>
    public static int LogIndex(IReadOnlyList<Event> ev, int from, MelLogLevel level)
    {
        if (from < 0) return -1;
        for (int i = from; i < ev.Count; i++)
        {
            if (ev[i].Kind == "log" && ev[i].Level == level) return i;
        }
        return -1;
    }

    public static List<string> Logs(IEnumerable<Event> ev, MelLogLevel level) =>
        ev.Where(e => e.Kind == "log" && e.Level == level).Select(e => e.Message).ToList();

    public static List<TimeSpan> Waits(IEnumerable<Event> ev, string kind) =>
        ev.Where(e => e.Kind == "wait:" + kind).Select(e => e.Duration).ToList();

    public static List<Event> Slice(List<Event> ev, int from, int to) =>
        ev.GetRange(from, Math.Max(0, (to < 0 ? ev.Count : to) - from));

    /// <summary>
    /// sdk-net's SSE reconnect ladder from a fresh client
    /// (<see cref="Transport.SseClient.RunAsync"/> + <see cref="Transport.SseClient.NextBackoff"/>):
    /// the first failed connect sleeps the 1s base unjittered, then each sleep is the previous
    /// sleep doubled, capped at 60s, with +/-20% jitter.
    /// </summary>
    public static List<string> CheckSseLadder(IReadOnlyList<TimeSpan> waits)
    {
        var errs = new List<string>();
        double expect = 1000;
        for (int k = 0; k < waits.Count; k++)
        {
            double w = waits[k].TotalMilliseconds;
            double lo = k == 0 ? expect : expect * 0.8;
            double hi = k == 0 ? expect : expect * 1.2;
            if (w < lo - 1 || w > hi + 1)
            {
                errs.Add($"SSE sleep #{k + 1} = {waits[k]}, want within [{TimeSpan.FromMilliseconds(lo)}, {TimeSpan.FromMilliseconds(hi)}]");
            }
            expect = Math.Min(w * 2, 60_000);
        }
        return errs;
    }

    public static string Dump(IReadOnlyList<Event> ev)
    {
        var b = new StringBuilder();
        for (int i = 0; i < ev.Count; i++)
        {
            b.Append(i.ToString("D4", CultureInfo.InvariantCulture)).Append(' ').Append(ev[i]).Append('\n');
        }
        return b.ToString();
    }

    /// <summary>
    /// The entering cycle's primary hit: the first primary-leg request after SSE started (cycle 1
    /// is the init fetch, which runs before SSE).
    /// </summary>
    public static int EnteringCycle(List<Event> ev)
    {
        int firstSse = Index(ev, 0, "hit:sse");
        if (firstSse < 0) throw new XunitException("SSE never attempted;\n" + Dump(ev));
        int i = Index(ev, firstSse, "hit:0");
        if (i < 0) throw new XunitException("no fetch cycle after SSE started;\n" + Dump(ev));
        return i;
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly SupervisorAuthRig _rig;

        public Handler(SupervisorAuthRig rig) => _rig = rig;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _rig.HandleAsync(request, cancellationToken);
    }

    private sealed class RigLogger : ILogger
    {
        private readonly SupervisorAuthRig _rig;

        public RigLogger(SupervisorAuthRig rig) => _rig = rig;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(MelLogLevel logLevel) => true;

        public void Log<TState>(MelLogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            if (exception is not null) msg += " | " + exception.Message;
            _rig.Record(new Event("log", level: logLevel, message: msg));
        }
    }

    private sealed class NoopSender : ITelemetrySender
    {
        public Task SendAsync(IDictionary<string, object?> payload, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
