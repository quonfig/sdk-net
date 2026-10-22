using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// What the chaos probe reads from the SDK. Every member is the SDK's own value: a public getter,
/// an internal test-only hook (<see cref="Sdk.Quonfig.SseConnectAttemptsTotal"/>,
/// <see cref="Sdk.Quonfig.WorkerRestartTotal"/>, <see cref="Sdk.Quonfig.FallbackPollerActive"/>),
/// or the task the SDK returned from <c>InitAsync</c>. Unit tests substitute a fake; the runner
/// uses <see cref="ClientView"/>.
/// </summary>
internal interface IChaosClientView
{
    Sdk.ConnectionState ConnectionState { get; }
    bool FallbackPollerActive { get; }
    DateTimeOffset? LastSuccessfulRefresh { get; }
    long SseConnectAttemptsTotal { get; }
    long WorkerRestartTotal(string layer);
    bool IsReady { get; }
    string? GetString(string key, string defaultValue);
}

/// <summary><see cref="IChaosClientView"/> over a live sdk-net client.</summary>
internal sealed class ClientView : IChaosClientView
{
    private readonly Sdk.Quonfig _c;

    public ClientView(Sdk.Quonfig client) { _c = client ?? throw new ArgumentNullException(nameof(client)); }

    public Sdk.ConnectionState ConnectionState => _c.ConnectionState;
    public bool FallbackPollerActive => _c.FallbackPollerActive;
    public DateTimeOffset? LastSuccessfulRefresh => _c.LastSuccessfulRefresh;
    public long SseConnectAttemptsTotal => _c.SseConnectAttemptsTotal;
    public long WorkerRestartTotal(string layer) => _c.WorkerRestartTotal(layer);
    public bool IsReady => _c.IsReady;
    public string? GetString(string key, string defaultValue) => _c.GetString(key, null, defaultValue);
}

/// <summary>
/// Test-only probe over a sdk-net client: the observation surface the chaos scenarios reference
/// (<c>connectionState</c>, <c>sdkMetric</c>, <c>lastSuccessfulRefresh</c>,
/// <c>fallbackPollerActive</c>, <c>sdkLog</c>, the init-failure vocabulary).
///
/// <para>qfg-6vno.4: the probe keeps no bookkeeping of its own for any value it reports. States and
/// metrics come from the SDK through <see cref="IChaosClientView"/>; log counts come from the lines
/// the SDK wrote through its <c>ILogger</c>; <c>readyEventCount</c> counts the SDK's own
/// <c>OnConfigChange</c> events. Before a client is attached every read fails loudly, so a
/// scenario can never pass on a default.</para>
/// </summary>
internal sealed class ChaosProbe
{
    private readonly object _lock = new();
    private IChaosClientView? _client;
    private Task? _init;
    private long _readyEvents;
    private int _unobservedFaults;
    private readonly List<LogLine> _logs = new();

    /// <summary>Wires the probe to the client every getter reads from.</summary>
    public void SetClient(IChaosClientView client)
    {
        lock (_lock) { _client = client ?? throw new ArgumentNullException(nameof(client)); }
    }

    public void SetClient(Sdk.Quonfig client) => SetClient(new ClientView(client));

    /// <summary>The task <c>InitAsync</c> returned; <see cref="InitState"/> reads its status.</summary>
    public void SetInitTask(Task init)
    {
        lock (_lock) { _init = init ?? throw new ArgumentNullException(nameof(init)); }
    }

    private IChaosClientView Client()
    {
        lock (_lock)
        {
            return _client ?? throw new InvalidOperationException("chaos probe read before a client was attached");
        }
    }

    /// <summary>
    /// The SDK's <see cref="Sdk.ConnectionState"/> in the supervisor-contract vocabulary
    /// (<c>initializing | connected | disconnected | falling_back</c>). A one-to-one rename; the
    /// probe never invents a state the contract forbids (no <c>reconnecting</c>).
    /// </summary>
    public string ConnectionState() => Client().ConnectionState switch
    {
        Sdk.ConnectionState.Initializing => "initializing",
        Sdk.ConnectionState.Connected => "connected",
        Sdk.ConnectionState.Disconnected => "disconnected",
        Sdk.ConnectionState.FallingBack => "falling_back",
        var other => throw new InvalidOperationException("unmapped sdk-net ConnectionState " + other),
    };

    /// <summary>The fallback poller's own engaged flag (internal SDK hook).</summary>
    public bool FallbackPollerActive() => Client().FallbackPollerActive;

    public DateTime? LastSuccessfulRefreshUtc() => Client().LastSuccessfulRefresh?.UtcDateTime;

    public bool Ready() => Client().IsReady;

    public string? GetString(string key, string defaultValue) => Client().GetString(key, defaultValue);

    /// <summary>
    /// The runner's process stayed up and no SDK task faulted unobserved. The chaos runner runs
    /// the SDK in-process, so a crash that takes the process down fails the run outright; the
    /// observable in-process signal is an unobserved task fault, recorded via
    /// <see cref="RecordUnobservedFault"/>.
    /// </summary>
    public bool ProcessStillAlive() => Volatile.Read(ref _unobservedFaults) == 0;

    public void RecordUnobservedFault() => Interlocked.Increment(ref _unobservedFaults);

    /// <summary>
    /// <c>'pending'</c> while the task <c>InitAsync</c> returned is running, <c>'returned'</c> once it
    /// completed, <c>'raised'</c> once it faulted. Reads the SDK's task, not a runner flag.
    /// </summary>
    public string InitState()
    {
        Task? t;
        lock (_lock) { t = _init; }
        if (t is null) throw new InvalidOperationException("client.initState() read but the scenario made no init call");
        if (!t.IsCompleted) return "pending";
        return t.Status == TaskStatus.RanToCompletion ? "returned" : "raised";
    }

    /// <summary>Called from the SDK's <c>OnConfigChange</c> event.</summary>
    public void OnConfigChange()
    {
        Task? t;
        lock (_lock) { t = _init; }
        // readyEventCount counts events fired AFTER the init call returned (chaos README).
        if (t is not null && t.IsCompleted) Interlocked.Increment(ref _readyEvents);
    }

    public long ReadyEventCount() => Interlocked.Read(ref _readyEvents);

    /// <summary>
    /// The SDK's value of the named metric, read through the client's internal test-only hooks.
    /// <c>Known</c> is false for a metric sdk-net does not expose, so the evaluator fails the
    /// expectation loudly instead of comparing against a silent 0.
    /// </summary>
    public (double Value, bool Known) SdkMetric(string name, string? layer)
    {
        var c = Client();
        return name switch
        {
            "quonfig_sdk_worker_restart_total" => (layer switch
            {
                null => c.WorkerRestartTotal("1") + c.WorkerRestartTotal("2"),
                _ => c.WorkerRestartTotal(layer),
            }, true),
            "quonfig_sse_connect_attempts_total" => (c.SseConnectAttemptsTotal, true),
            _ => (0, false),
        };
    }

    /// <summary>Records a log line the SDK wrote; called by <see cref="ProbeBridgeLogger"/>.</summary>
    public void Log(string level, string message)
    {
        lock (_lock)
        {
            _logs.Add(new LogLine(level, message));
        }
    }

    /// <summary>
    /// Count of SDK log lines at exactly <paramref name="level"/> (chaos README: "how many SDK log
    /// lines at exactly <c>level</c> match <c>re</c>") whose message matches <paramref name="re"/>.
    /// Level names are compared by severity, so <c>'warn'</c> matches Microsoft's
    /// <c>Warning</c>; an unknown level name throws.
    /// </summary>
    public int SdkLogMatches(string level, Regex? re)
    {
        int want = LevelSeverity(level);
        if (want < 0) throw new ArgumentException("unknown sdkLog level '" + level + "'", nameof(level));
        lock (_lock)
        {
            int n = 0;
            foreach (var line in _logs)
            {
                if (LevelSeverity(line.Level) != want) continue;
                if (re is null || re.IsMatch(line.Message)) n++;
            }
            return n;
        }
    }

    private static int LevelSeverity(string? level) => level?.ToLowerInvariant() switch
    {
        // Microsoft.Extensions.Logging ordering.
        "trace" => 0,
        "debug" => 1,
        "information" or "info" => 2,
        "warning" or "warn" => 3,
        "error" => 4,
        "critical" or "fatal" => 5,
        _ => -1,
    };

    private readonly struct LogLine
    {
        public LogLine(string level, string message) { Level = level; Message = message; }
        public string Level { get; }
        public string Message { get; }
    }
}
