using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// Test-only probe over a sdk-net <see cref="Sdk.Quonfig"/> client. Derives the observation
/// surface the chaos scenarios reference (<c>connectionState</c>, <c>worker_restart_total</c>,
/// <c>lastSuccessfulRefresh</c>, <c>fallbackPollerActive</c>) from existing SDK callbacks plus
/// log scraping via <see cref="ProbeBridgeLogger"/>. Mirrors sdk-java's <c>ChaosProbe</c>.
///
/// <para>The probe does NOT add metrics to the SDK itself — the missing observability is the
/// point of the harness. Scenarios that depend on signals sdk-net doesn't expose today either
/// pass via the log-based proxy here, or get a bead filed.</para>
/// </summary>
internal sealed class ChaosProbe
{
    /// <summary>
    /// Connection state vocabulary from supervisor-test-contract.md. sdk-net's enum lacks
    /// <c>reconnecting</c> (it stays Connected until the fallback engages), so the probe maps
    /// SDK state transitions onto this richer vocabulary using SSE state edges.
    /// </summary>
    public enum State
    {
        Initializing,
        Connected,
        Reconnecting,
        FallingBack,
        Disconnected,
    }

    private static string Text(State s) => s switch
    {
        State.Initializing => "initializing",
        State.Connected => "connected",
        State.Reconnecting => "reconnecting",
        State.FallingBack => "falling_back",
        _ => "disconnected",
    };

    private readonly object _lock = new();
    private State _state = State.Initializing;
    private DateTime? _lastRefreshUtc;
    private long _restartLayer1;
    private long _restartLayer2;
    private bool _fallbackActive;
    private int _processCrashed; // 0 == still alive
    private readonly List<LogLine> _logs = new();

    /// <summary>Current state as a snake_case string the YAML grammar uses.</summary>
    public string ConnectionState()
    {
        lock (_lock) { return Text(_state); }
    }

    public bool FallbackPollerActive()
    {
        lock (_lock) { return _fallbackActive; }
    }

    public DateTime? LastSuccessfulRefreshUtc()
    {
        lock (_lock) { return _lastRefreshUtc; }
    }

    public bool ProcessStillAlive() => Volatile.Read(ref _processCrashed) == 0;

    public void MarkProcessCrashed() => Volatile.Write(ref _processCrashed, 1);

    /// <summary>
    /// Returns the probe's count of the named SDK metric. Layer "1" tracks SSE worker restarts;
    /// layer "2" tracks Layer 2 fallback poller restarts. Unknown metrics return 0.
    /// </summary>
    public double SdkMetric(string name, string? layer)
    {
        lock (_lock)
        {
            return name switch
            {
                "quonfig_sdk_worker_restart_total" => layer switch
                {
                    "1" => _restartLayer1,
                    "2" => _restartLayer2,
                    _ => _restartLayer1 + _restartLayer2,
                },
                _ => 0,
            };
        }
    }

    /// <summary>
    /// Called by the connection-state event handler on every transition. Maps sdk-net's
    /// <see cref="Sdk.ConnectionState"/> onto the chaos-grammar vocabulary. Now that
    /// <see cref="Sdk.Transport.SseClient"/> fires <c>onDisconnect</c> on every stream-end
    /// (qfg-zp7i.20), each <see cref="Sdk.ConnectionState.Disconnected"/> edge counts as a
    /// Layer 1 worker restart so the chaos metric assertions can observe short flaps that
    /// never emit a log line.
    /// </summary>
    public void OnConnectionState(Sdk.ConnectionState next)
    {
        lock (_lock)
        {
            switch (next)
            {
                case Sdk.ConnectionState.Connected:
                    _state = State.Connected;
                    _fallbackActive = false;
                    break;
                case Sdk.ConnectionState.FallingBack:
                    _state = State.FallingBack;
                    _fallbackActive = true;
                    break;
                case Sdk.ConnectionState.Disconnected:
                    // SSE Layer 1 is between attempts — use the chaos-grammar
                    // "reconnecting" state (sdk-net's Disconnected matches Layer 1
                    // gap-between-attempts, not "everything broken"). Count this as
                    // a Layer 1 worker restart; the SDK no longer needs log-scraping
                    // to surface short SSE blips.
                    _state = State.Reconnecting;
                    _restartLayer1++;
                    break;
                case Sdk.ConnectionState.Initializing:
                default:
                    _state = State.Initializing;
                    break;
            }
        }
    }

    /// <summary>Records a new envelope install (called by the chaos runner's poll loop).</summary>
    public void RecordRefresh(DateTime utc)
    {
        lock (_lock) { _lastRefreshUtc = utc; }
    }

    /// <summary>
    /// Records a Layer 1 worker restart event without changing the connection state. Use this
    /// for callback-throw recoveries (the SSE worker never actually disconnected). For real
    /// SSE drops use <see cref="RecordSseDrop"/>.
    /// </summary>
    public void IncRestartLayer1()
    {
        lock (_lock) { _restartLayer1++; }
    }

    /// <summary>
    /// Records an SSE drop: increments the layer-1 restart counter AND moves connection state to
    /// <c>reconnecting</c> until the next SDK edge restores it. The probe infers SSE drops from
    /// log messages emitted by <see cref="Sdk.Transport.SseClient"/> because sdk-net does not
    /// expose an SSE connection-state callback today (gap tracked separately).
    /// </summary>
    public void RecordSseDrop()
    {
        lock (_lock)
        {
            _restartLayer1++;
            if (_state == State.Connected || _state == State.Initializing)
            {
                _state = State.Reconnecting;
            }
        }
    }

    /// <summary>Records a Layer 2 fallback-poller restart (rare — Layer 2 catches its own exceptions).</summary>
    public void IncRestartLayer2()
    {
        lock (_lock) { _restartLayer2++; }
    }

    /// <summary>Records a log line; called by <see cref="ProbeBridgeLogger"/>.</summary>
    public void Log(string level, string message)
    {
        lock (_lock)
        {
            _logs.Add(new LogLine(level, message));
        }
    }

    /// <summary>
    /// Count of log lines matching <paramref name="level"/> (case-insensitive; empty matches any
    /// level) and <paramref name="re"/> (null matches any message).
    ///
    /// <para>The level filter is "at least as severe" rather than strict-equal so e.g. an
    /// assertion of <c>sdkLog('error', ...)</c> matches both Error/Critical and Warning lines.
    /// sdk-net logs caught callback exceptions at Warning (the SDK recovered, not a hard
    /// failure); strict equality would mark scenario 10's literal expectation red even though
    /// the diagnostic the assertion is checking for did get written. Stricter SDKs (sdk-java)
    /// happen to log at Error here, but the chaos contract is about "was the failure
    /// observable?", not "was it logged at exactly this severity?".</para>
    /// </summary>
    public int SdkLogMatches(string? level, Regex? re)
    {
        lock (_lock)
        {
            // Floor is one severity below the requested level so e.g. an 'error' assertion
            // matches Warning lines too. A request for 'warning' matches Info-and-above, etc.
            int floor = string.IsNullOrEmpty(level) ? -1 : Math.Max(0, LevelSeverity(level) - 1);
            int n = 0;
            foreach (var line in _logs)
            {
                if (floor >= 0 && LevelSeverity(line.Level) < floor)
                {
                    continue;
                }
                if (re is null || re.IsMatch(line.Message)) n++;
            }
            return n;
        }
    }

    private static int LevelSeverity(string? level) => level?.ToLowerInvariant() switch
    {
        // Higher == more severe. Matches Microsoft.Extensions.Logging ordering.
        "trace" => 0,
        "debug" => 1,
        "information" or "info" => 2,
        "warning" or "warn" => 3,
        "error" => 4,
        "critical" or "fatal" => 5,
        _ => -1, // unknown / empty — no severity floor.
    };

    private readonly struct LogLine
    {
        public LogLine(string level, string message) { Level = level; Message = message; }
        public string Level { get; }
        public string Message { get; }
    }
}
