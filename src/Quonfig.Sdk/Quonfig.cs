using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Quonfig.Sdk.Datadir;
using Quonfig.Sdk.Eval;
using Quonfig.Sdk.Exceptions;
using Quonfig.Sdk.Supervisor;
using Quonfig.Sdk.Telemetry;
using Quonfig.Sdk.Transport;
using Quonfig.Sdk.Wire;
using EvalValueType = Quonfig.Sdk.Eval.ValueType;

namespace Quonfig.Sdk;

/// <summary>
/// Main public client for the Quonfig .NET SDK. Composes <see cref="DatadirLoader"/>,
/// <see cref="HttpTransport"/>, <see cref="SseClient"/>, <see cref="Supervisor.Supervisor"/>,
/// <see cref="FallbackPoller"/>, <see cref="Evaluator"/>, <see cref="Resolver"/>, and
/// <see cref="ConfigStore"/> behind the public <see cref="IQuonfig"/> surface. Mirrors
/// sdk-java's <c>com.quonfig.sdk.Quonfig</c> shape; see the cross-SDK contract in
/// <c>project/plans/sdk-net.md</c> §"Public API surface".
///
/// <para>Construction modes (mutually exclusive):
/// <list type="bullet">
///   <item><description><b>datadir</b> — load configs synchronously from a workspace directory tree.</description></item>
///   <item><description><b>datafile</b> — load a pre-serialized envelope from disk.</description></item>
///   <item><description><b>http+sse</b> (default) — fetch the initial envelope from <see cref="QuonfigOptions.ApiUrls"/>
///       and stream updates over SSE from <see cref="QuonfigOptions.StreamUrls"/>.</description></item>
/// </list>
/// </para>
///
/// <para>Lifecycle: construct, then <c>await client.InitAsync()</c>. Sync getters read from the
/// in-memory cache only — they do NOT block on init. Call <c>await client.CloseAsync()</c> or
/// dispose the client (<c>await using</c>) to shut down background workers.</para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1724:Type names should not match namespaces",
    Justification = "Quonfig is the canonical client class under the Quonfig.Sdk namespace, mirroring the sdk-java type name (com.quonfig.sdk.Quonfig).")]
public sealed class Quonfig : IQuonfig
{
    /// <summary>
    /// Cross-SDK constant: when <see cref="ShouldLog"/> evaluates per-logger rules it injects the
    /// logger path as the <c>key</c> field of a context named <c>quonfig-sdk-logging</c>. Load-bearing
    /// for api-telemetry example-context capture; do not rename without updating the matching
    /// constants in every other SDK.
    /// </summary>
    internal const string LoggingContextName = "quonfig-sdk-logging";

    private readonly QuonfigOptions _opts;
    private readonly ILogger _logger;

    // Config keys already warned about a weighted rollout whose hash property was missing from the
    // context (qfg-9dxb.8). One warning per key per client; bounded by the number of flags.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _hashPropertyMissingWarned =
        new(StringComparer.Ordinal);

    // Config keys already warned about a value that could not be resolved (ENV_VAR not set or not
    // coercible, decryption failure; qfg-2agi.17). One warning per key per client.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _resolveErrorWarned =
        new(StringComparer.Ordinal);

    // Config keys already warned about a PROP_MATCHES regex timeout (qfg-goi1.2.15). One warning
    // per key per client.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _regexTimeoutWarned =
        new(StringComparer.Ordinal);

    // 1 once a telemetry failure on the evaluation path has been logged (qfg-goi1.2.15).
    private int _telemetryFailureLogged;
    private string? _effectiveEnvironment;

    /// <summary>
    /// Effective endpoints after applying <c>QUONFIG_DOMAIN</c> and the derive-stream-from-api rule
    /// (see <see cref="EndpointResolver"/>). Used everywhere the client needs a URL; the raw
    /// <see cref="QuonfigOptions"/> values are only inputs to this resolution.
    /// </summary>
    private readonly IReadOnlyList<string> _apiUrls;
    private readonly IReadOnlyList<string> _streamUrls;
    private readonly string _telemetryUrl;

    /// <summary>
    /// The global context applied as the lowest-precedence layer to every evaluation. Equals
    /// <see cref="QuonfigOptions.GlobalContext"/> with the dev-only <c>quonfig-user.email</c>
    /// context merged UNDER it (customer keys win on collision) when dev-context injection is
    /// enabled and a tokens file is present. Computed once at construction.
    /// </summary>
    private readonly ContextSet? _globalContext;

    /// <summary>
    /// True when the client loads over HTTP/SSE against an SDK key (delivery mode), as opposed to
    /// datadir/datafile mode. In delivery mode the server's <c>meta.environment</c> is authoritative:
    /// the active environment is determined by the SDK key, so an explicit <see cref="QuonfigOptions.Environment"/>
    /// pin (or <c>QUONFIG_ENVIRONMENT</c>) is ignored for evaluation and only emits a WARN at init.
    /// Matches sdk-go, where the pin feeds only the datadir loader and eval never branches on it.
    /// </summary>
    private readonly bool _isDeliveryMode;
    private readonly TaskCompletionSource<bool> _initTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes on the first config install (network, datadir or datafile). Backs <see cref="IsReady"/>.</summary>
    private readonly TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Stops the init retry loop and the not-ready reminder on <see cref="CloseAsync"/>.</summary>
    private readonly CancellationTokenSource _initLoopCts = new();

    /// <summary>Started at construction: the "since start" clock for the init log contract.</summary>
    private readonly System.Diagnostics.Stopwatch _initClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>1 once an init WARN/ERROR was logged, so the recovery line is logged at the same level.</summary>
    private int _initFailureLogged;

    /// <summary>The last init attempt's per-leg errors (<c>primary=host: error; secondary=host: error</c>).</summary>
    private volatile string _lastInitErrors = string.Empty;
    private readonly object _stateLock = new();

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2213:Disposable fields should be disposed",
        Justification = "DatadirWatcher is disposed via CloseAsync.")]
    private volatile DatadirWatcher? _datadirWatcher;

    private volatile ConfigStore? _store;
    private volatile Evaluator? _evaluator;
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2213:Disposable fields should be disposed",
        Justification = "Supervisor is stopped via CloseAsync (StopAsync calls DisposeAsync internally).")]
    private volatile Supervisor.Supervisor? _supervisor;
    private volatile FallbackPoller? _fallbackPoller;
    private volatile SseClient? _sseClient;
    private volatile HttpTransport? _httpTransport;
    private volatile Task? _sseRunTask;
    private CancellationTokenSource? _sseCts;
    // Guards _sseCts / _sseClient / _sseRunTask: SSE is stopped on entry to auth-failed and
    // restarted on exit (qfg-goi1.10.28), racing CloseAsync.
    private readonly object _sseLock = new();

    private DateTimeOffset? _localLastRefresh;
    private ConnectionState _lastObservedState = ConnectionState.Initializing;
    private int _closed;

    /// <summary>
    /// Serializes the reject-older guard decision with the install that follows so the two are
    /// atomic across every network install path (initial fetch, fallback poller, SSE snapshot /
    /// update, in-band refresh). Without this lock a concurrent SSE update and fallback fetch could
    /// both pass the guard and install out of order. Datadir/datafile installs are a local source of
    /// truth and bypass the guard entirely. (qfg-7h5d.1.11)
    /// </summary>
    private readonly object _installLock = new();

    /// <summary>
    /// The <c>Meta.generation</c> currently held — the watermark the reject-older guard compares
    /// against. <c>-1</c> means nothing has been installed from the network yet (a fresh client
    /// always accepts its first snapshot, even at generation 0). <c>0</c> means only unversioned
    /// payloads have been installed, so further gen-0 payloads still install (qfg-9dxb.9).
    /// </summary>
    private int _heldGeneration = -1;

    /// <summary>
    /// <c>OnConfigChange</c> deliveries queued by <see cref="InstallEnvelope"/> and not yet fired.
    /// Installs only count here; the handlers run from <see cref="DeliverConfigChanges"/>, which every
    /// install path calls AFTER releasing <see cref="_installLock"/> (qfg-6vno.12, sdk-go qfg-eefx).
    /// </summary>
    private int _pendingConfigChanges;

    /// <summary>1 while one thread is draining <see cref="_pendingConfigChanges"/>; keeps handlers serial.</summary>
    private int _configChangeDraining;

    /// <summary>Count of envelopes actually installed from the network (rejected-older installs don't count).</summary>
    private int _networkInstallCount;

    /// <summary><see cref="HttpTransport.LastResolvedIndex"/> captured at the last accepted network install. <c>-1</c> until then.</summary>
    private int _resolvedFromIndex = -1;

    /// <summary>
    /// Accumulates failover-behavior counters (hedge-fired / guard-rejected / resolved-from) for the
    /// telemetry wire. Non-null only when telemetry is enabled (see the constructor); the failover
    /// call sites use <c>_failover?.Record*()</c> so a full telemetry opt-out records nothing. Rides
    /// the same envelope as the eval/context collectors via <see cref="TelemetryReporter"/>
    /// (qfg-41nh.18).
    /// </summary>
    private readonly FailoverCollector? _failover;

    /// <summary>
    /// Runtime telemetry pipeline (qfg-gxm6). Non-null only when telemetry is enabled AND a sender
    /// resolves (delivery mode with an SDK key, or an injected <see cref="QuonfigOptions.TelemetrySender"/>).
    /// The collectors are fed at the evaluation call sites in <c>TypedDetailsRaw</c>; the
    /// reporter periodically drains them (plus <see cref="_failover"/>) and posts to api-telemetry,
    /// and flushes once more on <see cref="CloseAsync"/>. Mirrors sdk-java's client-owned reporter
    /// lifecycle. Datadir/datafile clients construct none of this and emit no runtime telemetry.
    /// </summary>
    private readonly EvaluationSummaryCollector? _summaries;
    private readonly ContextShapeCollector? _shapes;
    private readonly ExampleContextCollector? _examples;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2213:Disposable fields should be disposed",
        Justification = "TelemetryReporter is flushed and disposed via CloseAsync (DisposeAsync -> CloseAsync).")]
    private readonly TelemetryReporter? _telemetryReporter;

    /// <summary>The built-in HTTP telemetry sender, when the client created it (disposed on close).</summary>
    private readonly HttpTelemetrySender? _ownedTelemetrySender;

    /// <summary>Constructs a client. <see cref="InitAsync"/> must be awaited before sync getters are reliable.</summary>
    public Quonfig(QuonfigOptions options)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(options);
#else
        if (options is null) throw new ArgumentNullException(nameof(options));
#endif
        _opts = options;
        _logger = options.Logger ?? NullLogger.Instance;

        var envLookup = options.EnvLookup ?? System.Environment.GetEnvironmentVariable;

        // Cross-SDK env-var fallbacks (option wins over env). QUONFIG_BACKEND_SDK_KEY seeds the SDK
        // key and QUONFIG_ENVIRONMENT seeds the evaluation environment when the caller left the
        // matching option unset, so a service that exports the canonical vars (as `qfg run` and every
        // fly.*.toml do) constructs with a bare `new QuonfigOptions()`. Mirrors sdk-go
        // (applyAPIKeyEnvOverride / applyEnvironmentEnvOverride), sdk-python (client.py),
        // sdk-node and sdk-java. Applied in place before mode validation so an env-provided key
        // satisfies ValidateHttpMode and an env-provided environment satisfies datadir mode; a
        // QUONFIG_ENVIRONMENT pin still no-ops (with the same WARN) in delivery mode, matching sdk-go.
        if (string.IsNullOrEmpty(options.SdkKey))
        {
            var envSdkKey = envLookup("QUONFIG_BACKEND_SDK_KEY");
            if (!string.IsNullOrEmpty(envSdkKey)) options.SdkKey = envSdkKey;
        }
        if (string.IsNullOrEmpty(options.Environment))
        {
            var envEnvironment = envLookup("QUONFIG_ENVIRONMENT");
            if (!string.IsNullOrEmpty(envEnvironment)) options.Environment = envEnvironment;
        }

        // Resolve the effective endpoints once (QUONFIG_DOMAIN override + derive-stream-from-api).
        // Every URL the client uses comes from here, not directly from options — a customer who
        // overrides only ApiUrls (e.g. to staging) now streams from the matching stream host and
        // has their telemetry follow, instead of silently hitting the production cluster.
        var (apiUrls, streamUrls, telemetryUrl) = EndpointResolver.Resolve(options, envLookup);
        _apiUrls = apiUrls;
        _streamUrls = streamUrls;
        _telemetryUrl = telemetryUrl;

        ValidateModes(options);

        // Telemetry pipeline (qfg-gxm6, mirrors sdk-java): construct the collectors + reporter when
        // telemetry is enabled AND a sender resolves. A sender resolves in delivery mode (the built-in
        // HTTP sender needs an SDK key) or when one is injected via QuonfigOptions.TelemetrySender —
        // datadir/datafile clients emit no runtime telemetry. Failover counters carry no user data and
        // ride the same envelope, so they follow the same enable gate: a FULL opt-out (no eval
        // summaries AND no context uploads) records nothing. Mirrors sdk-go, where the failover
        // aggregator lives on the Submitter, which is only constructed when telemetry is enabled.
        // (qfg-41nh.18 + qfg-gxm6)
        bool telemetryEnabled = options.CollectEvaluationSummaries
            || options.ContextUploadMode != ContextUploadMode.None;
        var telemetrySender = telemetryEnabled ? ResolveTelemetrySender(options, _telemetryUrl) : null;
        if (telemetrySender is not null)
        {
            _ownedTelemetrySender = options.TelemetrySender is null ? telemetrySender as HttpTelemetrySender : null;
            // Aggregator caps (P6): a non-positive option falls back to the 10,000 default.
            _summaries = new EvaluationSummaryCollector(
                options.CollectEvaluationSummaries,
                TelemetryDefaults.PositiveOr(options.TelemetryMaxEvaluationSummaries, TelemetryDefaults.MaxEvaluationSummaries));
            _shapes = new ContextShapeCollector(
                options.ContextUploadMode,
                TelemetryDefaults.PositiveOr(options.TelemetryMaxContextShapeFields, TelemetryDefaults.MaxContextShapeFields));
            _examples = new ExampleContextCollector(
                options.ContextUploadMode,
                TelemetryDefaults.PositiveOr(options.TelemetryMaxExampleContexts, TelemetryDefaults.MaxExampleContexts),
                TimeSpan.FromHours(1));
            _summaries.OnCanonicalFailure = LogTelemetryFailureOnce;
            _failover = new FailoverCollector();
            _telemetryReporter = new TelemetryReporter(
                telemetrySender,
                Guid.NewGuid().ToString(),
                _summaries,
                _shapes,
                _examples,
                _failover,
                _logger,
                TelemetryReporterSettings.From(options),
                options.TelemetryClock);
            _telemetryReporter.Start();
        }

        _effectiveEnvironment = options.Environment;
        _globalContext = ResolveGlobalContext(options);

        // Subscribe the convenience callback before any load so the initial (synchronous, in
        // datadir/datafile mode) envelope install fires it. Mirrors sdk-java registering
        // options.onConfigUpdate() at construction.
        if (options.OnConfigChange is not null)
        {
            OnConfigChange += options.OnConfigChange;
        }

        if (!string.IsNullOrEmpty(options.Datadir))
        {
            InitDatadir(options.Datadir!);
        }
        else if (!string.IsNullOrEmpty(options.Datafile))
        {
            InitDatafile(options.Datafile!);
        }
        else if (options.DatafileEnvelope is not null)
        {
            InitDatafileEnvelope(options.DatafileEnvelope);
        }
        else
        {
            ValidateHttpMode(options, _apiUrls);
            _isDeliveryMode = true;
            // A single explicit ApiUrl disables automatic failover: the default (and every
            // QUONFIG_DOMAIN-derived) list carries both a primary and a secondary leg that the SDK
            // hedges/fails over between. An explicit override replaces that list wholesale, so a
            // one-entry override silently drops the secondary. Warn once at construction (sdk-go
            // parity, qfg-41nh.26/.27).
            if (options.ApiUrlsExplicit && _apiUrls.Count < 2)
            {
                _logger.LogWarning(
                    "quonfig: explicit ApiUrls disables automatic failover to the secondary; pass both primary and secondary URLs to keep it");
            }
            // Delivery (SDK-key) mode: the server's meta.environment is authoritative, so an
            // explicit Environment pin (or QUONFIG_ENVIRONMENT) is ignored for evaluation. Warn
            // once at init so a mis-set pin doesn't silently no-op. Matches sdk-go, where the pin
            // only feeds the datadir loader and eval always uses the installed envelope's env.
            if (!string.IsNullOrEmpty(options.Environment))
            {
                _logger.LogWarning(
                    "quonfig: environment '{Environment}' was set but the client is in delivery (SDK-key) mode; the active environment is determined by the SDK key, so this setting is ignored (it applies only when loading from a local data dir)",
                    options.Environment);
            }
            // Install an empty store immediately so getters called before init finishes (or
            // after init fails under OnInitFailure.ReturnDefaults) surface FlagNotFound for any
            // key — letting the OnNoDefault policy fire — instead of a "not yet initialized"
            // general error. Cross-SDK contract: pre-init getters return the supplied default
            // or throw via OnNoDefault, never silently swallow the request.
            InstallEmptyStoreIfNeeded();
            // Kick off background HTTP init. The constructor returns immediately; callers
            // await InitAsync() to know when it finished (or when the OnInitFailure policy
            // applied to a timeout).
            _ = Task.Run(RunHttpInitAsync);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// In delivery mode the background init loop owns the <see cref="QuonfigOptions.InitTimeout"/>
    /// deadline (measured from construction) and applies <see cref="QuonfigOptions.OnInitFailure"/>:
    /// <see cref="OnInitFailure.Fail"/> throws a <see cref="QuonfigInitException"/>,
    /// <see cref="OnInitFailure.Continue"/> returns, <see cref="OnInitFailure.Wait"/> keeps blocking
    /// until the first config arrives. <paramref name="cancellationToken"/> bounds the wait.
    /// </remarks>
    public Task InitAsync(CancellationToken cancellationToken = default) =>
        AwaitWithCancellationAsync(_initTcs.Task, cancellationToken);

    /// <summary>
    /// True once the client holds a config from Quonfig (the first network install, or the datadir /
    /// datafile load). Under <see cref="OnInitFailure.Wait"/>, wire a readiness probe to this so the
    /// orchestrator holds traffic until config lands. Stays false while a client that hit
    /// <see cref="OnInitFailure.Continue"/> (or a caught <see cref="OnInitFailure.Fail"/>) serves code
    /// defaults, and flips to true when the background retry heals.
    /// </summary>
    public bool IsReady => _readyTcs.Task.IsCompleted;

    /// <summary>
    /// Completes when <see cref="IsReady"/> becomes true, whatever <see cref="OnInitFailure"/> is set
    /// to; it never throws an init error. Pass a <paramref name="cancellationToken"/> to bound the wait.
    /// </summary>
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) =>
        AwaitWithCancellationAsync(_readyTcs.Task, cancellationToken);

    private static async Task AwaitWithCancellationAsync(Task task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted || !cancellationToken.CanBeCanceled)
        {
            await task.ConfigureAwait(false);
            return;
        }
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken)))
        {
            var first = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
            await first.ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _initLoopCts.Cancel(); } catch (ObjectDisposedException) { }
        var watcher = _datadirWatcher;
        if (watcher is not null)
        {
            await watcher.DisposeAsync().ConfigureAwait(false);
            _datadirWatcher = null;
        }
        // Stop SSE first; the run task respects the CTS and unwinds. _closed is already set, so
        // an auth-failed exit cannot start a new stream after this snapshot.
        CancellationTokenSource? sseCts;
        SseClient? sseClient;
        Task? sseTask;
        lock (_sseLock)
        {
            sseCts = _sseCts;
            sseClient = _sseClient;
            sseTask = _sseRunTask;
        }
        try { sseCts?.Cancel(); } catch (ObjectDisposedException) { }
        if (sseTask is not null)
        {
            try
            {
                await Task.WhenAny(sseTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* expected */ }
        }
        var sup = _supervisor;
        if (sup is not null)
        {
            await sup.StopAsync().ConfigureAwait(false);
        }
        sseClient?.Dispose();
        _httpTransport?.Dispose();
        sseCts?.Dispose();

        // Stop the telemetry reporter last: DisposeAsync gives the live window (eval summaries, context
        // shapes, failover counters) one POST with a 5s deadline and never drains the retained queue,
        // so close never blocks on a slow telemetry endpoint. (qfg-gxm6, qfg-y8je.10)
        var reporter = _telemetryReporter;
        if (reporter is not null)
        {
            await reporter.DisposeAsync().ConfigureAwait(false);
        }
        _ownedTelemetrySender?.Dispose();
        _initLoopCts.Dispose();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => new(CloseAsync());

    /// <inheritdoc/>
    public IReadOnlyList<string> Keys()
    {
        var store = _store;
        return store is null ? Array.Empty<string>() : store.Keys();
    }

    /// <inheritdoc/>
    public IBoundQuonfig WithContext(ContextSet contexts) =>
        new BoundQuonfig(this, contexts ?? new ContextSet());

    /// <inheritdoc/>
    public DateTimeOffset? LastSuccessfulRefresh
    {
        get
        {
            var sup = _supervisor;
            if (sup is not null && sup.LastSuccessfulRefresh is { } t)
            {
                return new DateTimeOffset(t, TimeSpan.Zero);
            }
            lock (_stateLock) { return _localLastRefresh; }
        }
    }

    // HTTP+SSE delivery with a stream StartSse() will run: an SDK key, stream URLs, and neither
    // a datadir nor a datafile (those modes install synchronously and never build a supervisor).
    private bool StreamConfigured =>
        !string.IsNullOrEmpty(_opts.SdkKey) && _streamUrls.Count > 0 && _isDeliveryMode;

    /// <inheritdoc/>
    public ConnectionState ConnectionState
    {
        get
        {
            var sup = _supervisor;
            if (sup is not null) return sup.ConnectionState;
            // No supervisor: datadir/datafile mode (Connected after sync install), or HTTP mode
            // before StartSse() builds it. With a stream configured, HTTP mode stays Initializing
            // until the stream reports: the init fetch completes InitAsync just before StartSse()
            // runs, and reading Connected in that window would claim a stream that has not
            // delivered a frame (sdk-go, sdk-java qfg-goi1.10.42). Without a stream there is
            // never a supervisor, so the install timestamp decides, as in datadir mode.
            if (StreamConfigured) return ConnectionState.Initializing;
            return LastSuccessfulRefresh is null ? ConnectionState.Initializing : ConnectionState.Connected;
        }
    }

    /// <inheritdoc/>
    public event Action<ConnectionState>? OnConnectionStateChange;

    /// <inheritdoc/>
    public event Action? OnConfigChange;

    // ---------------- Typed getters ----------------

    /// <inheritdoc/>
    public string? GetString(string key, ContextSet? contexts = null, string? defaultValue = null) =>
        Evaluate(key, contexts, defaultValue, EvalValueType.String, CoerceString, raise: true).Value;

    /// <inheritdoc/>
    public int? GetInt(string key, ContextSet? contexts = null, int? defaultValue = null) =>
        Evaluate(key, contexts, defaultValue, EvalValueType.Int, CoerceInt, raise: true).Value;

    /// <inheritdoc/>
    public long? GetLong(string key, ContextSet? contexts = null, long? defaultValue = null) =>
        Evaluate(key, contexts, defaultValue, EvalValueType.Int, CoerceLong, raise: true).Value;

    /// <inheritdoc/>
    public bool? GetBool(string key, ContextSet? contexts = null, bool? defaultValue = null) =>
        Evaluate(key, contexts, defaultValue, EvalValueType.Bool, CoerceBoolNullable, raise: true).Value;

    /// <inheritdoc/>
    public double? GetDouble(string key, ContextSet? contexts = null, double? defaultValue = null) =>
        Evaluate(key, contexts, defaultValue, EvalValueType.Double, CoerceDouble, raise: true).Value;

    /// <inheritdoc/>
    public IReadOnlyList<string>? GetStringList(string key, ContextSet? contexts = null, IReadOnlyList<string>? defaultValue = null) =>
        Evaluate(key, contexts, defaultValue, EvalValueType.StringList, CoerceStringList, raise: true).Value;

    /// <inheritdoc/>
    public object? GetJson(string key, ContextSet? contexts = null, object? defaultValue = null) =>
        Evaluate(key, contexts, defaultValue, EvalValueType.Json, CoerceJson, raise: true).Value;

    /// <inheritdoc/>
    public TimeSpan? GetDuration(string key, ContextSet? contexts = null, TimeSpan? defaultValue = null) =>
        Evaluate(key, contexts, defaultValue, EvalValueType.Duration, CoerceDuration, raise: true).Value;

    /// <inheritdoc/>
    public bool IsFeatureEnabled(string key, ContextSet? contexts = null)
    {
        // IsFeatureEnabled bypasses OnNoDefault entirely — always returns a bool.
        var details = TypedDetailsRaw<bool>(key, contexts, false, EvalValueType.Bool, CoerceBool);
        return details.Value;
    }

    /// <inheritdoc/>
    public bool ShouldLog(string loggerPath, LogLevel desired, ContextSet? contexts = null, LogLevel? defaultLevel = null)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(loggerPath);
#else
        if (loggerPath is null) throw new ArgumentNullException(nameof(loggerPath));
#endif
        var resolved = ResolveLogLevelString(loggerPath, contexts);
        if (resolved is null)
        {
            // No log-level rule at any parent — apply the explicit default if given, else allow.
            return defaultLevel is null || (int)defaultLevel.Value >= (int)desired;
        }
        var parsed = LogLevels.FromString(resolved);
        if (parsed is null) return true; // unparseable wins-with-allow (don't drop logs)
        return (int)parsed.Value >= (int)desired;
    }

    /// <inheritdoc/>
    public LogLevel? GetLogLevel(string loggerPath, ContextSet? contexts = null)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(loggerPath);
#else
        if (loggerPath is null) throw new ArgumentNullException(nameof(loggerPath));
#endif
        var resolved = ResolveLogLevelString(loggerPath, contexts);
        return resolved is null ? null : LogLevels.FromString(resolved);
    }

    // ---------------- EvaluationDetails getters ----------------

    /// <inheritdoc/>
    public EvaluationDetails<string?> GetStringDetails(string key, ContextSet? contexts = null, string? defaultValue = null) =>
        Evaluate(key, contexts, defaultValue, EvalValueType.String, CoerceString, raise: false);

    /// <inheritdoc/>
    public EvaluationDetails<int?> GetIntDetails(string key, ContextSet? contexts = null, int? defaultValue = null) =>
        Evaluate(key, contexts, defaultValue, EvalValueType.Int, CoerceInt, raise: false);

    /// <inheritdoc/>
    public EvaluationDetails<long?> GetLongDetails(string key, ContextSet? contexts = null, long? defaultValue = null) =>
        Evaluate(key, contexts, defaultValue, EvalValueType.Int, CoerceLong, raise: false);

    /// <inheritdoc/>
    public EvaluationDetails<bool?> GetBoolDetails(string key, ContextSet? contexts = null, bool? defaultValue = null) =>
        Evaluate(key, contexts, defaultValue, EvalValueType.Bool, CoerceBoolNullable, raise: false);

    /// <inheritdoc/>
    public EvaluationDetails<double?> GetDoubleDetails(string key, ContextSet? contexts = null, double? defaultValue = null) =>
        Evaluate(key, contexts, defaultValue, EvalValueType.Double, CoerceDouble, raise: false);

    /// <inheritdoc/>
    public EvaluationDetails<IReadOnlyList<string>?> GetStringListDetails(string key, ContextSet? contexts = null, IReadOnlyList<string>? defaultValue = null) =>
        Evaluate(key, contexts, defaultValue, EvalValueType.StringList, CoerceStringList, raise: false);

    /// <inheritdoc/>
    public EvaluationDetails<object?> GetJsonDetails(string key, ContextSet? contexts = null, object? defaultValue = null) =>
        Evaluate(key, contexts, defaultValue, EvalValueType.Json, CoerceJson, raise: false);

    /// <inheritdoc/>
    public EvaluationDetails<TimeSpan?> GetDurationDetails(string key, ContextSet? contexts = null, TimeSpan? defaultValue = null) =>
        Evaluate(key, contexts, defaultValue, EvalValueType.Duration, CoerceDuration, raise: false);

    // ---------------- Internals ----------------

    private static void ValidateModes(QuonfigOptions opts)
    {
        int modes = 0;
        if (!string.IsNullOrEmpty(opts.Datadir)) modes++;
        if (!string.IsNullOrEmpty(opts.Datafile)) modes++;
        if (opts.DatafileEnvelope is not null) modes++;
        if (modes > 1)
        {
            throw new ArgumentException(
                "QuonfigOptions: set at most one of Datadir, Datafile, or DatafileEnvelope",
                nameof(opts));
        }
    }

    private static void ValidateHttpMode(QuonfigOptions opts, IReadOnlyList<string> apiUrls)
    {
        if (string.IsNullOrEmpty(opts.SdkKey))
        {
            throw new ArgumentException(
                "QuonfigOptions.SdkKey required for HTTP+SSE mode; set SdkKey or use Datadir/Datafile",
                nameof(opts));
        }
        if (apiUrls.Count == 0)
        {
            throw new ArgumentException(
                "QuonfigOptions.ApiUrls must contain at least one URL", nameof(opts));
        }
    }

    /// <summary>
    /// Resolves the telemetry sender: an injected <see cref="QuonfigOptions.TelemetrySender"/> wins;
    /// otherwise, in delivery mode (an <see cref="QuonfigOptions.SdkKey"/> is present) the built-in
    /// <see cref="HttpTelemetrySender"/> posts to <paramref name="telemetryUrl"/>, reusing any
    /// injected <see cref="QuonfigOptions.HttpMessageHandler"/>. Returns <c>null</c> when there is no
    /// SDK key and no injected sender (datadir/datafile mode), so no reporter is built. Mirrors
    /// sdk-java's <c>resolveTelemetrySender</c>.
    /// </summary>
    private static ITelemetryTransport? ResolveTelemetrySender(QuonfigOptions opts, string telemetryUrl)
    {
        if (opts.TelemetrySender is not null)
        {
            return opts.TelemetrySender as ITelemetryTransport ?? new TelemetrySenderTransport(opts.TelemetrySender);
        }
        if (string.IsNullOrEmpty(opts.SdkKey))
        {
            return null;
        }
        var settings = TelemetryReporterSettings.From(opts);
        return new HttpTelemetrySender(
            new Uri(telemetryUrl, UriKind.Absolute),
            opts.SdkKey!,
            settings.Timeout,
            settings.ConnectTimeout,
            opts.HttpMessageHandler);
    }

    /// <summary>
    /// Computes the lowest-precedence global context: the customer's <see cref="QuonfigOptions.GlobalContext"/>
    /// with the dev-only <c>quonfig-user.email</c> context merged UNDER it (customer keys win on
    /// collision). Resolution of the dev-context knob mirrors the cross-SDK contract:
    /// explicit <see cref="QuonfigOptions.EnableQuonfigUserContext"/> wins; else the
    /// <c>QUONFIG_DEV_CONTEXT</c> env var (<c>"true"</c>/<c>"false"</c>); else default ON. The loader
    /// no-ops without a tokens file, so default-on is inert in production.
    /// </summary>
    private ContextSet? ResolveGlobalContext(QuonfigOptions options)
    {
        if (!DevContextEnabled(options)) return options.GlobalContext;

        // Use the RESOLVED api URLs so the per-domain tokens file matches a QUONFIG_DOMAIN / explicit
        // ApiUrls override (e.g. staging), not the built-in production default.
        var devContext = DevContext.Load(_apiUrls, options.EnvLookup, _logger);
        if (devContext is null) return options.GlobalContext;

        // Customer-supplied keys win on collision: customer GlobalContext is the overlay.
        return MergeContexts(devContext, options.GlobalContext);
    }

    private static bool DevContextEnabled(QuonfigOptions options)
    {
        if (options.EnableQuonfigUserContext.HasValue)
        {
            return options.EnableQuonfigUserContext.Value;
        }
        var lookup = options.EnvLookup ?? System.Environment.GetEnvironmentVariable;
        var env = lookup("QUONFIG_DEV_CONTEXT");
        if (string.Equals(env, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(env, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private void InitDatadir(string datadir)
    {
        if (string.IsNullOrEmpty(_opts.Environment))
        {
            throw new InvalidOperationException(
                "QuonfigOptions.Environment required for Datadir mode; set Environment or QUONFIG_ENVIRONMENT");
        }
        var envelope = DatadirLoader.Load(datadir, _opts.Environment!, _logger);
        InstallEnvelope(envelope);
        DeliverConfigChanges();
        _initTcs.TrySetResult(true);

        if (_opts.DatadirAutoReload)
        {
            StartDatadirWatcher(datadir);
        }
    }

    private void InitDatafile(string datafilePath)
    {
        ConfigEnvelope envelope;
        try
        {
            using var stream = File.OpenRead(datafilePath);
            envelope = JsonSerializer.Deserialize<ConfigEnvelope>(stream)
                ?? throw new QuonfigException($"datafile {datafilePath} parsed to a null envelope");
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            throw new QuonfigException($"failed to read datafile {datafilePath}: {ex.Message}", ex);
        }
        InstallDatafileEnvelope(envelope);
    }

    private void InitDatafileEnvelope(ConfigEnvelope envelope) => InstallDatafileEnvelope(envelope);

    private void InstallDatafileEnvelope(ConfigEnvelope envelope)
    {
        // The meta.environment fallback now lives in InstallEnvelope so every install path
        // (datafile, datadir, HTTP init, HTTP refresh, SSE) shares it. (qfg-64m9)
        InstallEnvelope(envelope);
        DeliverConfigChanges();
        _initTcs.TrySetResult(true);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Auto-reload start failure must degrade gracefully — log and continue serving the initial envelope.")]
    private void StartDatadirWatcher(string datadir)
    {
        DatadirWatcher? watcher = null;
        try
        {
            watcher = new DatadirWatcher(
                datadir,
                _opts.DatadirAutoReloadDebounce,
                onChange: () => ReloadDatadir(datadir),
                onError: ex => _logger.LogWarning(ex, "quonfig: datadir watcher error: {Message}", ex.Message),
                logger: _logger);

            if (watcher.Start())
            {
                _datadirWatcher = watcher;
                watcher = null; // ownership transferred to the field; CloseAsync will dispose
            }
            else
            {
                _logger.LogWarning("quonfig: datadir auto-reload watcher failed to start; continuing without watching");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "quonfig: datadir auto-reload setup threw: {Message}", ex.Message);
        }
        finally
        {
            watcher?.Dispose();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "A bad reload must not tear down the client — keep the previous store, log, continue.")]
    private void ReloadDatadir(string datadir)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        try
        {
            var envelope = DatadirLoader.Load(datadir, _effectiveEnvironment!, _logger);
            InstallEnvelope(envelope);
            DeliverConfigChanges();
        }
        catch (Exception ex)
        {
            // Parse-then-swap: keep serving the previous envelope, do NOT fire OnConfigChange.
            _logger.LogWarning(ex, "quonfig: datadir reload failed; keeping previous envelope: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// One-shot startup log announcing the chosen Layer 1 (SSE) and Layer 2 (fallback poll)
    /// configuration. Parity with sdk-go's <c>logPollingMode</c> / sdk-java's
    /// <c>logPollingMode</c>: deployers grep for this line to confirm the background-refresh
    /// semantics their options produced. Note that in sdk-net the fallback poller is started by
    /// the SSE worker wiring, so without stream URLs there is NO background refresh at all —
    /// the mode label reflects that honestly.
    /// </summary>
    private void LogStartupMode()
    {
        bool sseConfigured = !string.IsNullOrEmpty(_opts.SdkKey) && _streamUrls.Count > 0;
        bool fallbackConfigured = _opts.FallbackPollEnabled && _opts.FallbackPollInterval > TimeSpan.Zero;
        string mode = sseConfigured
            ? (fallbackConfigured ? "sse-with-fallback-poll" : "sse-only")
            : "no-background-refresh";
        _logger.LogInformation(
            "quonfig: polling configuration mode={Mode} sse_enabled={SseEnabled} fallback_poll_enabled={FallbackPollEnabled} fallback_poll_interval={FallbackPollInterval} fallback_poll_threshold={FallbackPollThreshold}",
            mode,
            sseConfigured,
            fallbackConfigured,
            _opts.FallbackPollInterval,
            _opts.FallbackPollThreshold);
    }

    /// <summary>
    /// The delivery-mode init loop (qfg-9p4i.10; spec 2026-10-05-init-failure-design.md B.3, C). Each
    /// attempt is one hedged primary+secondary fetch; between attempts the loop waits with jittered
    /// backoff (0.5s, 1s, 2s, ... capped at 30s). It owns the <see cref="QuonfigOptions.InitTimeout"/>
    /// deadline (measured from construction) and applies <see cref="QuonfigOptions.OnInitFailure"/>:
    /// fail raises at the deadline (or at once on 401/403/404/410 from every configured leg), continue returns, wait keeps
    /// retrying until the first config lands. After fail/continue the update channel (SSE plus the
    /// fallback poller) keeps retrying, so no client object is ever permanently dead.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Runs detached: any unexpected fault must settle InitAsync instead of leaving it hanging.")]
    private async Task RunHttpInitAsync()
    {
        var mode = _opts.OnInitFailure;
        LogStartupMode();
        var uris = _apiUrls.Select(u => new Uri(u, UriKind.Absolute)).ToList();
        try
        {
            // Per-URL config-fetch timeout (qfg-7h5d.1.11): a hung primary aborts after
            // ConfigFetchTimeout on the SEQUENTIAL FetchAsync path. The init/refresh install path
            // uses the parallel hedge (qfg-7h5d.1.14): fire the primary first and, only if it is slow
            // or errors, fire the secondary in parallel — bounded by HedgeDelay/HedgeAbort. The same
            // transport instance backs the fallback poller and in-band refresh, so every config-fetch
            // path shares the per-leg ETag slots + the reject-older install guard. Disposed by CloseAsync.
            //
            // The shared HttpClient's own Timeout is only a ceiling above the per-leg deadlines: it must
            // NOT come from InitTimeout, which would (a) throw for InitTimeout <= 0 and (b) cap every
            // later fetch (wait-mode retries, the fallback poller, RefreshAsync) at the init budget
            // forever (qfg-9p4i.17.5, D3).
            _httpTransport = new HttpTransport(
                uris, _opts.SdkKey!, TransportCeiling(_opts), _opts.HttpMessageHandler,
                _opts.ConfigFetchTimeout, _opts.ConfigFetchHedgeDelay, _opts.ConfigFetchHedgeAbort);
        }
        catch (Exception ex)
        {
            _initTcs.TrySetException(new QuonfigInitException($"Quonfig client initialization failed: {ex.Message}", ex));
            return;
        }

        // The per-leg hedge abort must be strictly below InitTimeout so an init-path heal leg is
        // not clipped before it can land. Warn (not throw) so a tightened InitTimeout is still
        // usable; the hedge just may not heal-forward within the initial budget. Mirrors sdk-go's
        // construction-time WithConfigFetchHedgeAbort warning.
        // D3 (qfg-9p4i.17.5): InitTimeout <= 0 (including InfiniteTimeSpan) means exactly ONE attempt,
        // bounded by the per-leg deadlines, then OnInitFailure applies. Never "no deadline", never a
        // decision before any attempt.
        bool singleAttempt = _opts.InitTimeout <= TimeSpan.Zero;
        if (!singleAttempt && _opts.InitTimeout <= _opts.ConfigFetchHedgeAbort)
        {
            _logger.LogWarning(
                "quonfig: InitTimeout ({InitTimeout}) <= config-fetch hedge abort ({HedgeAbort}); init-path heal-forward may be clipped — raise InitTimeout above the hedge abort",
                _opts.InitTimeout, _opts.ConfigFetchHedgeAbort);
        }

        string timeoutText = FormatTimeout(_opts.InitTimeout);
        LogInit(Microsoft.Extensions.Logging.LogLevel.Debug, InitEventStart, null, FormattableString.Invariant(
            $"[quonfig] init: fetching config (initTimeout={timeoutText}, onInitFailure={ModeName(mode)})"));

        var loopToken = _initLoopCts.Token;
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(loopToken);
        // Measured from construction, like the old InitAsync-side timer it replaces.
        if (!singleAttempt)
        {
            var remaining = _opts.InitTimeout - _initClock.Elapsed;
            deadlineCts.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }

        var random = new Random();
        int attempt = 0;
        bool timedOut = false;          // the InitTimeout deadline has passed (only wait keeps looping)
        bool warnedFirst = false;
        bool authLogged = false;
        bool lastAttemptTimedOut = false;
        Exception? lastError = null;
        var nextProgress = TimeSpan.FromSeconds(5);
        var nextWaitProgress = TimeSpan.Zero;

        while (true)
        {
            if (loopToken.IsCancellationRequested)
            {
                SettleInitOnClose(mode);
                return;
            }
            if (!timedOut && (deadlineCts.IsCancellationRequested || (singleAttempt && attempt >= 1)))
            {
                timedOut = true;
                // D2: timeout iff the LAST attempt hung, or no attempt completed.
                if (OnInitDeadline(mode, timeoutText, lastAttemptTimedOut || attempt == 0, lastError))
                {
                    return;
                }
                nextWaitProgress = _initClock.Elapsed + TimeSpan.FromSeconds(30);
            }

            attempt++;
            var failed = new List<LegResult>();
            var token = timedOut ? loopToken : deadlineCts.Token;
            try
            {
                // Drive one hedged fetch+install cycle. Latches readiness on the FIRST successful
                // install; a late-but-newer leg heals forward after. Throws when every fired leg failed.
                await FetchAndInstallAsync(initial: true, token, failed).ConfigureAwait(false);
                _initTcs.TrySetResult(true);
                StartSse();
                return;
            }
            catch (OperationCanceledException ex)
            {
                if (loopToken.IsCancellationRequested)
                {
                    SettleInitOnClose(mode);
                    return;
                }
                lastError = ex;
                // The deadline cut the attempt mid-flight: the legs still in flight hung. It is a hang
                // unless a leg that already settled answered with a fast error (D2).
                lastAttemptTimedOut = failed.All(IsTimedOutLeg);
            }
            catch (Exception ex)
            {
                if (loopToken.IsCancellationRequested)
                {
                    SettleInitOnClose(mode);
                    return;
                }
                lastError = ex;
                // D2: the attempt hung only when EVERY leg in it ended on a timeout/abort. Legs that
                // answered with errors (refused, 5xx, bad body) make it "unavailable".
                lastAttemptTimedOut = failed.Count > 0
                    ? failed.All(IsTimedOutLeg)
                    : (!timedOut && deadlineCts.IsCancellationRequested) || ChainContainsOperationCancelled(ex);
            }

            if (IsReady)
            {
                // A leg already installed this attempt (readiness latched, InitAsync returned) and only a
                // late leg still draining failed or was cut by the deadline: init succeeded. Never log or
                // apply an init failure for it.
                _initTcs.TrySetResult(true);
                StartSse();
                return;
            }

            _lastInitErrors = FormatLegErrors(uris, failed, lastAttemptTimedOut);

            // An auth-class status from every configured leg: the key is rejected. fail raises now and continue
            // continues now (B.3); wait logs ERROR and keeps retrying (the key may be provisioned late).
            // D1: auth class {401, 403, 404, 410} from EVERY configured leg (never the primary alone).
            if (failed.Select(l => l.LegIndex).Distinct().Count() >= uris.Count
                && failed.All(l => HttpTransport.IsAuthClassStatus(l.Status)))
            {
                int status = failed.OrderBy(l => l.LegIndex).First().Status;
                string by = uris.Count switch
                {
                    1 => "primary",
                    2 => "primary and secondary",
                    _ => FormattableString.Invariant($"all {uris.Count} delivery URLs"),
                };
                if (mode == OnInitFailure.Fail)
                {
                    LogInit(Microsoft.Extensions.Logging.LogLevel.Error, InitEventAuth, null, FormattableString.Invariant(
                        $"[quonfig] init FAILED: SDK key rejected ({status}) by {by}. Check the key and environment. onInitFailure=fail: raising now."));
                    HandleInitFailure(new QuonfigInitException(
                        FormattableString.Invariant($"Quonfig client initialization failed: SDK key rejected ({status}) by {by}"),
                        QuonfigInitFailureReason.Unauthorized, lastError));
                    return;
                }
                if (mode == OnInitFailure.Continue)
                {
                    LogInit(Microsoft.Extensions.Logging.LogLevel.Error, InitEventAuth, null, FormattableString.Invariant(
                        $"[quonfig] init: SDK key rejected ({status}) by {by}. Check the key and environment. onInitFailure=continue: STARTING ON CODE DEFAULTS. Retrying in background."));
                    HandleInitFailure(null);
                    return;
                }
                if (!authLogged)
                {
                    authLogged = true;
                    LogInit(Microsoft.Extensions.Logging.LogLevel.Error, InitEventAuth, null, FormattableString.Invariant(
                        $"[quonfig] init: SDK key rejected ({status}) by {by}. Check the key and environment. onInitFailure=wait: retrying (the key may be provisioned late)."));
                }
                warnedFirst = true;
            }

            if (singleAttempt)
            {
                warnedFirst = true; // D3: no "retrying until initTimeout" line; the decision line carries <errs>.
            }
            if (!warnedFirst)
            {
                warnedFirst = true;
                LogInit(Microsoft.Extensions.Logging.LogLevel.Warning, InitEventRetrying, null,
                    $"[quonfig] init: config not loaded yet, retrying until initTimeout ({timeoutText}). {_lastInitErrors}");
            }

            var elapsed = _initClock.Elapsed;
            if (!timedOut && elapsed >= nextProgress && !deadlineCts.IsCancellationRequested)
            {
                LogInit(Microsoft.Extensions.Logging.LogLevel.Warning, InitEventProgress, null, FormattableString.Invariant(
                    $"[quonfig] init: still waiting for config ({elapsed.TotalSeconds:0.0}s elapsed of {timeoutText}, attempt {attempt}). {_lastInitErrors}"));
                while (nextProgress <= elapsed) nextProgress += TimeSpan.FromSeconds(5);
            }
            else if (timedOut && elapsed >= nextWaitProgress)
            {
                LogInit(Microsoft.Extensions.Logging.LogLevel.Warning, InitEventProgress, null, FormattableString.Invariant(
                    $"[quonfig] init: still blocked waiting for config ({elapsed.TotalSeconds:0}s elapsed, attempt {attempt}). {_lastInitErrors}"));
                while (nextWaitProgress <= elapsed) nextWaitProgress += TimeSpan.FromSeconds(30);
            }

            if (singleAttempt && !timedOut)
            {
                continue; // D3: the one attempt is done; the top of the loop applies OnInitFailure now.
            }

            // Jittered backoff: 0.5s, 1s, 2s, 4s, ... capped at 30s, +/-20%. Before the deadline the
            // sleep is cut short by it so the OnInitFailure decision lands on time.
            double baseMs = Math.Min(30_000d, 500d * Math.Pow(2, Math.Min(attempt - 1, 16)));
            var delay = TimeSpan.FromMilliseconds(baseMs * (0.8 + (0.4 * random.NextDouble())));
            try
            {
                await Task.Delay(delay, timedOut ? loopToken : deadlineCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Deadline reached or client closed: the top of the loop decides.
            }
        }
    }

    private static readonly EventId InitEventStart = new(4100, "quonfig.init.start");
    private static readonly EventId InitEventRetrying = new(4101, "quonfig.init.retrying");
    private static readonly EventId InitEventProgress = new(4102, "quonfig.init.progress");
    private static readonly EventId InitEventTimeout = new(4103, "quonfig.init.timeout");
    private static readonly EventId InitEventAuth = new(4104, "quonfig.init.auth_failed");
    private static readonly EventId InitEventNotConnected = new(4105, "quonfig.init.not_connected");
    private static readonly EventId InitEventRecovered = new(4106, "quonfig.init.recovered");

    /// <summary>Logs one init-contract line (section C). The <see cref="EventId"/> name is the structured <c>event</c>.</summary>
    private void LogInit(Microsoft.Extensions.Logging.LogLevel level, EventId eventId, Exception? exception, string line)
    {
        if (level >= Microsoft.Extensions.Logging.LogLevel.Warning)
        {
            Volatile.Write(ref _initFailureLogged, 1);
        }
        _logger.Log(level, eventId, exception, "{QuonfigInitMessage}", line);
    }

    /// <summary>
    /// Applies <see cref="QuonfigOptions.OnInitFailure"/> when the init deadline passes without a config.
    /// Returns true when the loop is done (fail, continue); false for wait, which keeps retrying.
    /// </summary>
    private bool OnInitDeadline(OnInitFailure mode, string timeoutText, bool hung, Exception? lastError)
    {
        string errs = _lastInitErrors.Length > 0 ? _lastInitErrors : "no delivery leg answered";
        string after = _opts.InitTimeout <= TimeSpan.Zero
            ? "one attempt (initTimeout <= 0)"
            : FormattableString.Invariant($"{_opts.InitTimeout.TotalSeconds:0.0}s");
        if (mode == OnInitFailure.Wait)
        {
            LogInit(Microsoft.Extensions.Logging.LogLevel.Warning, InitEventTimeout, null,
                $"[quonfig] init: no config after {after}. onInitFailure=wait: still blocking startup and retrying indefinitely. {errs}. Use onInitFailure=fail or continue to bound startup.");
            return false;
        }
        if (mode == OnInitFailure.Continue)
        {
            LogInit(Microsoft.Extensions.Logging.LogLevel.Error, InitEventTimeout, null,
                $"[quonfig] init: no config after {after}. onInitFailure=continue: STARTING ON CODE DEFAULTS. This instance may disagree with the rest of your fleet. Retrying in background.");
            HandleInitFailure(null);
            return true;
        }
        QuonfigInitException error = hung
            ? new QuonfigInitTimeoutException($"Quonfig client initialization timed out: no config after {after}. {errs}", lastError)
            : new QuonfigInitException(
                $"Quonfig client initialization failed: no config after {after}. {errs}",
                QuonfigInitFailureReason.Unavailable,
                lastError);
        LogInit(Microsoft.Extensions.Logging.LogLevel.Error, InitEventTimeout, null,
            $"[quonfig] init FAILED: no config after {after}. {errs}. onInitFailure=fail: raising {error.GetType().Name}. Options: onInitFailure=wait to keep waiting, onInitFailure=continue to start on code defaults, or raise initTimeout.");
        HandleInitFailure(error);
        return true;
    }

    /// <summary>The client was closed before init finished: settle InitAsync instead of leaving it hanging.</summary>
    private void SettleInitOnClose(OnInitFailure mode)
    {
        if (mode == OnInitFailure.Continue)
        {
            _initTcs.TrySetResult(true);
            return;
        }
        _initTcs.TrySetException(new QuonfigInitException(
            "Quonfig client was closed before initialization completed", QuonfigInitFailureReason.Unavailable, null));
    }

    /// <summary>A failed leg that ended on a timeout/abort rather than an answer or a fast error.</summary>
    private static bool IsTimedOutLeg(LegResult leg) => leg.Status == 0 && ChainContainsOperationCancelled(leg.Error);

    /// <summary>
    /// The shared transport's HttpClient.Timeout: a ceiling ABOVE the per-leg deadlines
    /// (<see cref="QuonfigOptions.ConfigFetchTimeout"/>, <see cref="QuonfigOptions.ConfigFetchHedgeAbort"/>),
    /// which are what actually bound each request. Independent of <see cref="QuonfigOptions.InitTimeout"/>.
    /// </summary>
    private static TimeSpan TransportCeiling(QuonfigOptions opts)
    {
        var ceiling = HttpTransport.DefaultHttpTimeout;
        if (opts.ConfigFetchTimeout > ceiling) ceiling = opts.ConfigFetchTimeout;
        if (opts.ConfigFetchHedgeAbort > ceiling) ceiling = opts.ConfigFetchHedgeAbort;
        return ceiling;
    }

    /// <summary>Canonical lower-case name of an <see cref="OnInitFailure"/> value (the deprecated aliases share these values).</summary>
    private static string ModeName(OnInitFailure mode) => mode switch
    {
        OnInitFailure.Continue => "continue",
        OnInitFailure.Wait => "wait",
        _ => "fail",
    };

    /// <summary><c>10s</c>, <c>0.5s</c>: the init timeout as the log contract prints it.</summary>
    private static string FormatTimeout(TimeSpan timeout) => timeout <= TimeSpan.Zero
        ? "0s"
        : timeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "s";

    /// <summary><c>primary=host: error; secondary=host: error</c>. Hosts only, never the key or URL credentials.</summary>
    private static string FormatLegErrors(IReadOnlyList<Uri> uris, List<LegResult> failed, bool hung)
    {
        if (failed.Count == 0)
        {
            return hung && uris.Count > 0 ? $"primary={uris[0].Authority}: timed out" : string.Empty;
        }
        return string.Join("; ", failed.OrderBy(l => l.LegIndex).Select(l =>
        {
            string name = l.LegIndex == 0 ? "primary" : l.LegIndex == 1 ? "secondary" : FormattableString.Invariant($"leg{l.LegIndex}");
            string host = l.LegIndex >= 0 && l.LegIndex < uris.Count ? uris[l.LegIndex].Authority : "?";
            return $"{name}={host}: {LegErrorText(l)}";
        }));
    }

    private static string LegErrorText(LegResult leg)
    {
        if (leg.Status > 0) return FormattableString.Invariant($"HTTP {leg.Status}");
        if (ChainContainsOperationCancelled(leg.Error)) return "timed out";
        var inner = leg.Error?.InnerException;
        return inner is HttpRequestException ? inner.Message : leg.Error?.Message ?? "unknown error";
    }

    /// <summary>
    /// Heal line (section C): logged on the first network install, at Warning, only when an init
    /// WARN/ERROR was logged first, so the filter level that saw the failure also sees the recovery.
    /// </summary>
    private void LogInitRecoveredIfFailed(int sourceIndex, string? version)
    {
        if (Volatile.Read(ref _initFailureLogged) == 0) return;
        string via = sourceIndex switch
        {
            0 => "primary",
            1 => "secondary",
            < 0 => "stream",
            _ => FormattableString.Invariant($"leg{sourceIndex}"),
        };
        _logger.Log(Microsoft.Extensions.Logging.LogLevel.Warning, InitEventRecovered, null, "{QuonfigInitMessage}",
            FormattableString.Invariant(
                $"[quonfig] init recovered: config loaded after {_initClock.Elapsed.TotalSeconds:0.0}s via {via} (version {version ?? "unknown"}). Now serving live config."));
    }

    /// <summary>
    /// While a client that continued (or a caught fail) is still not ready: WARN every 30s for 5
    /// minutes, then every 5 minutes (section C), until the background retry heals or the client closes.
    /// </summary>
    private async Task RemindNotConnectedAsync()
    {
        var token = _initLoopCts.Token;
        int reminders = 0;
        try
        {
            while (!IsReady && !token.IsCancellationRequested)
            {
                var wait = reminders < 10 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5);
                var finished = await Task.WhenAny(_readyTcs.Task, Task.Delay(wait, token)).ConfigureAwait(false);
                if (finished == _readyTcs.Task || token.IsCancellationRequested) return;
                reminders++;
                var since = _initClock.Elapsed;
                string sinceText = FormattableString.Invariant($"{(int)since.TotalMinutes}m{since.Seconds}s");
                LogInit(Microsoft.Extensions.Logging.LogLevel.Warning, InitEventNotConnected, null,
                    $"[quonfig] still not connected to Quonfig delivery ({sinceText} since start); serving defaults. {_lastInitErrors}");
            }
        }
        catch (OperationCanceledException)
        {
            // Closed.
        }
    }

    /// <summary>
    /// Drives one parallel-failover hedge cycle and installs whatever arrives through the reject-older
    /// guard. The hedge fires the primary first and, only if it is slow or errors, fires the secondary
    /// in parallel; results are installed as they arrive so watermark-max falls out (higher generation
    /// wins, a late older payload never regresses, a late newer payload heals forward). Readiness
    /// latches on the FIRST successful install (the init <c>_initTcs</c> completes immediately and
    /// <c>InitAsync</c> can return) while this method keeps draining so a late-but-newer leg heals
    /// forward before it returns. Mirrors sdk-go's <c>fetchAndInstall</c>.
    ///
    /// <para>Concurrent hedge cycles (a manual <see cref="RefreshAsync"/> racing the fallback poller,
    /// say) are safe and NOT coalesced: each leg uses its own per-URL ETag slot, every install is
    /// serialized through <see cref="_installLock"/> + the reject-older guard (so an equal-or-older
    /// payload is a no-op and the install count can't double), and each leg is bounded by the per-leg
    /// hedge abort. A coalescing gate would make a manual <see cref="RefreshAsync"/> silently no-op
    /// whenever a background fetch is in flight, violating the refresh contract.</para>
    ///
    /// <para>When <paramref name="initial"/> is true and every fired leg failed (nothing installed),
    /// this throws the last leg's error so <see cref="RunHttpInitAsync"/>'s catch clauses apply
    /// <see cref="OnInitFailure"/>. An all-304 / no-op cycle returns without throwing.</para>
    ///
    /// <para>Returns the cycle's <see cref="PollOutcome"/> for the auth-failure rule
    /// (qfg-goi1.10.28, R2): answered when any leg returned 200 with a valid envelope or 304;
    /// auth-rejected when no leg did and the PRIMARY leg's status is 401/403/404/410 (primary
    /// 401 + secondary 503/timeout qualifies; primary 503 + secondary 401 does not). Only the
    /// fallback poller acts on it; init and <see cref="RefreshAsync"/> ignore it.</para>
    /// </summary>
    private async Task<PollOutcome> FetchAndInstallAsync(
        bool initial, CancellationToken cancellationToken, List<LegResult>? failedLegs = null)
    {
        var transport = _httpTransport;
        if (transport is null) return PollOutcome.Failed;

        var reader = transport.FetchHedgedAsync(cancellationToken);

        bool installedOnce = false;
        int fired = 0;
        int failures = 0;
        int authFailures = 0;
        int primaryStatus = 0;
        Exception? lastError = null;
        var firedLegs = new HashSet<int>();

        await DrainAsync(reader).ConfigureAwait(false);

        // Failover observability: if more than the primary leg fired, the hedge fired its secondary
        // leg this cycle (the primary was slow or errored). Recorded once per cycle regardless of
        // which leg's payload won the guard (qfg-41nh.18).
        if (fired > 1)
        {
            _failover?.RecordHedgeFired();
        }

        // D1 (qfg-9p4i.17.5): the init auth fast-fail needs an auth-class answer from EVERY configured
        // leg, but the hedge only ever fires the primary and the secondary. When every fired leg was
        // auth-rejected and more legs are configured, ask the rest before deciding (and install from
        // any of them that answers), so the rule is reachable with 3+ URLs.
        if (initial && !installedOnce && fired > 0 && authFailures == fired && firedLegs.Count < transport.ApiUrls.Count)
        {
            var rest = Enumerable.Range(0, transport.ApiUrls.Count).Where(i => !firedLegs.Contains(i)).ToList();
            await DrainAsync(transport.FetchLegsAsync(rest, cancellationToken)).ConfigureAwait(false);
        }

        if (installedOnce)
        {
            return PollOutcome.Ok;
        }

        // Nothing installed. If every fired leg failed, surface the failure (init path applies
        // OnInitFailure). Otherwise every leg was a 304 (established client, no change) — a no-op.
        if (initial && fired > 0 && failures == fired && lastError is not null)
        {
            throw lastError;
        }
        if (fired > 0 && failures == fired)
        {
            // Auth-failure rule R2: no leg answered and the primary returned an auth-class status.
            return HttpTransport.IsAuthClassStatus(primaryStatus)
                ? PollOutcome.AuthRejected(primaryStatus, transport.PrimaryHost)
                : PollOutcome.Failed;
        }
        // initial with an all-304 / empty cycle, or a non-initial refresh: a no-op success.
        return PollOutcome.Ok;

        async Task DrainAsync(System.Threading.Channels.ChannelReader<LegResult> legs)
        {
            while (await legs.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (legs.TryRead(out var leg))
                {
                    fired++;
                    firedLegs.Add(leg.LegIndex);
                    if (leg.LegIndex == 0)
                    {
                        primaryStatus = leg.Status;
                    }
                    if (leg.Error is not null)
                    {
                        failures++;
                        if (HttpTransport.IsAuthClassStatus(leg.Status)) authFailures++;
                        lastError = leg.Error;
                        failedLegs?.Add(leg);
                        continue;
                    }
                    // Any ANSWERED leg — 200-with-install, 304, or a guard-rejected older/same-
                    // generation payload — is a successful poll of the delivery tier: the server
                    // responded; there may simply have been nothing newer to install. Stamp
                    // LastSuccessfulRefresh per answered leg so freshness reports "config is
                    // reachable" (sdk-go post-qfg-41nh.11 semantics). The SSE path is stricter: a
                    // PUSHED envelope that fails the reject-older guard does NOT stamp (see
                    // OnSseEnvelope) — counting stale replays would report a frozen client as fresh.
                    _supervisor?.RecordSuccessfulRefresh();
                    if (leg.NotModified || leg.Envelope is null)
                    {
                        continue; // 304 — nothing changed on this leg.
                    }
                    // Reject-older guard + install are atomic under _installLock against every other
                    // install path (SSE, fallback poller). InstallFromNetwork sets the held generation,
                    // install count, and resolved-from leg together (resolvedFrom set atomically with held).
                    var outcome = InstallFromNetwork(leg.Envelope, leg.LegIndex);
                    if (outcome == NetworkInstallOutcome.Installed)
                    {
                        // Failover observability: record which leg (primary index 0 / secondary index
                        // > 0) served the config now held (qfg-41nh.18).
                        _failover?.RecordResolvedFrom(leg.LegIndex);
                        if (!installedOnce)
                        {
                            installedOnce = true;
                            if (initial)
                            {
                                // Latch readiness on the FIRST successful install; keep draining so a
                                // late-but-newer leg heals forward before this call returns.
                                _initTcs.TrySetResult(true);
                            }
                        }
                    }
                    else if (outcome == NetworkInstallOutcome.RejectedOlder)
                    {
                        // A 200 dropped by the reject-older guard because it was STRICTLY older than the
                        // held generation: a leg tried to move this client backwards. That is the failover
                        // signal worth alerting on, so count it (qfg-41nh.18, narrowed by qfg-rr5b). The
                        // fetch itself succeeded (liveness already stamped above) — only the install was a
                        // no-op.
                        _failover?.RecordGuardRejected();
                    }
                    // NetworkInstallOutcome.RejectedSameGeneration: the server answered with the config we
                    // already hold (a cold per-leg ETag slot on a fresh transport / reconnect, or the
                    // fallback poller's engage-time fetch). Nothing to install and nothing wrong — a silent
                    // no-op, deliberately NOT counted as guardRejected (qfg-rr5b).
                    else if (outcome == NetworkInstallOutcome.RejectedUnversioned)
                    {
                        // A gen<=0 payload while a real generation is held — not installed, but not
                        // provably older, so a silent no-op (qfg-9dxb.9). Forget its ETag: the server can
                        // repair the generation for the SAME sha, and a remembered ETag would 304 the
                        // repaired payload until the next commit (qfg-9dxb.9 follow-up).
                        transport.ForgetETag(leg.LegIndex, leg.ETag);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Walks the inner-exception chain of <paramref name="ex"/>; returns true if any node is an
    /// <see cref="OperationCanceledException"/> (which includes <see cref="TaskCanceledException"/>).
    /// Used by <see cref="RunHttpInitAsync"/> to detect init-timeout failures that have been wrapped
    /// in <see cref="QuonfigException"/> by <c>HttpTransport.FetchAsync</c>.
    /// </summary>
    private static bool ChainContainsOperationCancelled(Exception? ex)
    {
        var depth = 0;
        while (ex is not null && depth++ < 8)
        {
            if (ex is OperationCanceledException) return true;
            ex = ex.InnerException;
        }
        return false;
    }

    /// <summary>
    /// Ends init without a config under fail (<paramref name="error"/> set: InitAsync raises it) or
    /// continue (<paramref name="error"/> null: InitAsync returns). Either way the client keeps an empty
    /// store (getters return their defaults or honor OnNoDefault) and keeps retrying through the update
    /// channel (SSE plus the fallback poller), so a caller that catches the fail and keeps this object
    /// still gets a client that heals (qfg-9p4i.3; no permanently dead clients).
    /// </summary>
    private void HandleInitFailure(QuonfigInitException? error)
    {
        InstallEmptyStoreIfNeeded();
        if (error is null)
        {
            _initTcs.TrySetResult(true);
        }
        else
        {
            _initTcs.TrySetException(error);
        }
        StartSse();
        _ = Task.Run(RemindNotConnectedAsync);
    }

    private void InstallEmptyStoreIfNeeded()
    {
        if (_store is not null) return;
        var store = new ConfigStore();
        _store = store;
        _evaluator = NewEvaluator(store);
    }

    private void StartSse()
    {
        if (Volatile.Read(ref _closed) != 0) return;
        if (string.IsNullOrEmpty(_opts.SdkKey)) return;
        if (_streamUrls.Count == 0) return;

        FallbackPoller? fp = null;
        if (_opts.FallbackPollEnabled && _opts.FallbackPollInterval > TimeSpan.Zero)
        {
            fp = new FallbackPoller(
                fetch: ct => DoFallbackFetchAsync(ct),
                interval: _opts.FallbackPollInterval,
                threshold: _opts.FallbackPollThreshold,
                onEngage: () =>
                {
                    // Layer 2 engaging IS the outage signal: SSE has been down past the
                    // threshold and freshness is now on the poll cadence. Warning level so a
                    // customer with any wired ILogger can alert on it (sdk-go parity).
                    _logger.LogWarning(
                        "quonfig: Layer 2 fallback poller engaged (SSE disconnected past threshold) interval={FallbackPollInterval} threshold={FallbackPollThreshold}",
                        _opts.FallbackPollInterval,
                        _opts.FallbackPollThreshold);
                    UpdateConnectionState(Sdk.ConnectionState.FallingBack);
                },
                onDisengage: () =>
                {
                    _logger.LogInformation("quonfig: Layer 2 fallback poller disengaged (SSE recovered)");
                    UpdateConnectionState(Sdk.ConnectionState.Connected);
                },
                logger: _logger,
                onAuthFailed: OnAuthFailedAsync)
            {
                WaitHook = _opts.TestWaitHook,
            };
            _fallbackPoller = fp;
        }

        var workers = new List<WorkerSpec>();
        if (fp is not null)
        {
            workers.Add(new WorkerSpec("2", fp.Worker));
        }
        var sup = new Supervisor.Supervisor(workers, logger: _logger);
        _supervisor = sup;
        sup.Start();

        // Arm the disconnect timestamp immediately: SSE has not yet connected. If it never
        // does, fallback engages once the 120s threshold elapses.
        fp?.SetSseConnected(false);

        StartSseStream();
    }

    /// <summary>
    /// Starts a brand-new <see cref="SseClient"/> (so it begins at its base backoff). Called at
    /// startup and when the client leaves auth-failed (qfg-goi1.10.28, R4). No-op once closed.
    /// </summary>
    private void StartSseStream()
    {
        lock (_sseLock)
        {
            if (Volatile.Read(ref _closed) != 0) return;
            var streams = _streamUrls.Select(u => new Uri(u, UriKind.Absolute)).ToList();
            var sseCts = new CancellationTokenSource();
            _sseCts = sseCts;
            var sse = new SseClient(
            streamUrls: streams,
            sdkKey: _opts.SdkKey!,
            onEnvelope: env => OnSseEnvelope(env),
            onConnect: OnSseConnect,
            onDisconnect: OnSseDisconnect,
            readTimeout: _opts.SseReadTimeout,
            messageHandler: _opts.HttpMessageHandler,
            logger: _logger)
            {
                WaitHook = _opts.TestWaitHook,
                Counters = _sseCounters,
            };
            _sseClient = sse;
            _sseRunTask = Task.Run(() => sse.RunAsync(sseCts.Token));
        }
    }

    /// <summary>
    /// Stops the SSE stream and waits (bounded) for its loop to unwind, so no SSE attempt or SSE
    /// log line lands after the caller's next step. Used on entry to auth-failed (R3: SSE is not
    /// attempted while auth-failed).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort teardown of a cancelled SSE loop; it must not take the poller down.")]
    private async Task StopSseStreamAsync()
    {
        CancellationTokenSource? cts;
        SseClient? sse;
        Task? run;
        lock (_sseLock)
        {
            cts = _sseCts;
            sse = _sseClient;
            run = _sseRunTask;
            _sseCts = null;
            _sseClient = null;
            _sseRunTask = null;
        }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        if (run is not null)
        {
            try
            {
                await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The loop's own fault is not ours to surface here.
            }
            if (run.IsCompleted)
            {
                sse?.Dispose();
                cts?.Dispose();
            }
        }
    }

    /// <summary>
    /// Auth-failure rule hook from the fallback poller (qfg-goi1.10.28). On entry: stop SSE and
    /// report <see cref="ConnectionState.Disconnected"/> (never falling_back, R6). On exit: restart
    /// SSE as a brand-new client from its base backoff (R4); the state stays disconnected until
    /// SSE connects.
    /// </summary>
    private async Task OnAuthFailedAsync(bool entered)
    {
        if (entered)
        {
            await StopSseStreamAsync().ConfigureAwait(false);
            UpdateConnectionState(Sdk.ConnectionState.Disconnected);
            return;
        }
        StartSseStream();
    }

    private async Task<PollOutcome> DoFallbackFetchAsync(CancellationToken ct)
    {
        var transport = _httpTransport;
        if (transport is null) return PollOutcome.Failed;
        try
        {
            // Fallback poller hedges too (qfg-7h5d.1.14): fire the primary first, hedge the secondary
            // only if slow/erroring. Each accepted leg installs only if it advances the held
            // generation (reject-older), so a failover to an older secondary never regresses an
            // established client. RecordSuccessfulRefresh fires per ANSWERED leg inside the loop
            // (installed or not — a 304/same-gen answer is still a successful poll).
            return await FetchAndInstallAsync(initial: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown path.
            return PollOutcome.Failed;
        }
    }

    /// <summary>
    /// Performs a single guarded config fetch through the shared <see cref="HttpTransport"/> and
    /// installs the result only if it advances the held generation. Exposed to the chaos harness to
    /// model ongoing config polling (the failover/refresh install path) for the ordering rig; it
    /// routes through the same per-URL timeout and the same reject-older guard as every other
    /// network path, so it exercises the real mechanism rather than a test-only shortcut. A 304 or a
    /// rejected-older payload is a no-op. Errors are swallowed (the next tick retries); shutdown
    /// cancellation is honored.
    /// </summary>
    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var transport = _httpTransport;
        if (transport is null) return;
        try
        {
            // A manual refresh ALWAYS actually fetches — there is no coalescing gate (qfg-7h5d.1.14):
            // overlapping cycles are safe via per-leg ETag isolation + the serialized reject-older
            // install guard + per-leg hedge abort. Drives the same hedge + reject-older path as init,
            // so it exercises the real failover/heal-forward mechanism, not a test-only shortcut.
            await FetchAndInstallAsync(initial: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown path.
        }
        catch (QuonfigException ex)
        {
            // All legs failed this tick; keep serving the held envelope and let the next tick retry.
            _logger.LogDebug(ex, "quonfig: refresh fetch failed; keeping held envelope: {Message}", ex.Message);
        }
    }

    private void OnSseConnect()
    {
        // First-valid-frame edge from the SSE worker (a keepalive or an envelope; the 200
        // headers alone do not count, qfg-goi1.10.41). Tell the fallback poller SSE is live and flip
        // the customer-visible ConnectionState. If Layer 2 happened to be engaged, the
        // SetSseConnected(true) call triggers its disengage path.
        _fallbackPoller?.SetSseConnected(true);
        UpdateConnectionState(Sdk.ConnectionState.Connected);
    }

    private void OnSseDisconnect()
    {
        // Stream torn down (EOF, watchdog, IO error). Arm the FallbackPoller's disconnect
        // timer so Layer 2 can engage if SSE doesn't come back within the threshold, and
        // surface Disconnected to OnConnectionStateChange listeners so chaos probes and
        // customer code can observe real outages (no longer log-scraping required).
        _fallbackPoller?.SetSseConnected(false);
        UpdateConnectionState(Sdk.ConnectionState.Disconnected);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "A bad envelope from SSE must not tear down the client — keep the previous store, log, continue.")]
    private void OnSseEnvelope(ConfigEnvelope envelope)
    {
        try
        {
            // Reject-older guard on the SSE initial snapshot and every SSE update: install only if
            // it advances the held generation (qfg-7h5d.1.11). The SSE leg is its own host (it does
            // not fail over), so the resolved index for an SSE install is left unchanged — it is not
            // an HTTP failover leg.
            //
            // Stamp freshness ONLY on an accepted install (sdk-java parity, Quonfig.java:648-651).
            // A guard-REJECTED SSE envelope is a stale replay; stamping it would advance
            // LastSuccessfulRefresh while the client is effectively frozen on old config
            // (qfg-41nh.8). This is deliberately stricter than the HTTP poll path in
            // FetchAndInstallAsync, where an answered request with nothing newer IS a
            // successful refresh — there the CLIENT asked and the server answered; here the
            // server pushed something the guard had to discard.
            var outcome = InstallFromNetwork(envelope, sourceIndex: -1);
            if (outcome == NetworkInstallOutcome.Installed)
            {
                _supervisor?.RecordSuccessfulRefresh();
            }
            else if (outcome == NetworkInstallOutcome.RejectedOlder)
            {
                // A STRICTLY older pushed envelope — a stale replay trying to move this client
                // backwards. Count it for failover observability (qfg-41nh.18, narrowed by qfg-rr5b).
                // Freshness is deliberately NOT stamped here (unlike the HTTP poll path) — a stale
                // replay must not advance LastSuccessfulRefresh while the client is effectively frozen
                // on old config (qfg-41nh.8).
                _failover?.RecordGuardRejected();
            }
            // NetworkInstallOutcome.RejectedSameGeneration: api-delivery's sendInitialConfig re-sends
            // the CURRENT envelope on every connect (SDK clients send no Last-Event-Id), so every SSE
            // reconnect re-delivers config this client already holds. Nothing to install and nothing
            // wrong — a silent no-op, deliberately NOT counted as guardRejected, and (as before) not
            // stamped either (qfg-rr5b). NetworkInstallOutcome.RejectedUnversioned (gen<=0 while a
            // real generation is held) is handled the same way: dropped, not counted, not stamped
            // (qfg-9dxb.9).
            // Receiving an envelope means the SSE stream is live; update connection state and
            // tell the fallback poller it can stand down.
            _fallbackPoller?.SetSseConnected(true);
            UpdateConnectionState(Sdk.ConnectionState.Connected);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "quonfig: SSE envelope install failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Applies the canonical reject-older rule to an envelope arriving on a network install path
    /// (initial fetch, fallback poller, SSE snapshot / update, in-band refresh), then installs it
    /// when accepted. The rule is the whole story — there is no source ranking:
    /// <list type="bullet">
    ///   <item><description>A fresh client (nothing installed yet) always accepts the first snapshot,
    ///     even at generation 0. A stale secondary can therefore seed a fresh client, but…</description></item>
    ///   <item><description>…an UNVERSIONED payload (incoming <c>Meta.generation</c> &lt;= 0) installs
    ///     only while the held generation is 0, i.e. the client has never held a real generation.
    ///     Pre-watermark servers that sent generation 0 on every payload are long gone; today gen 0
    ///     only comes from a server whose git object store is damaged (rev-count failed), so it must
    ///     never override a held real generation (qfg-9dxb.9).</description></item>
    ///   <item><description>Otherwise an established client installs only if the incoming generation
    ///     is strictly greater than the held generation. An older payload is dropped, so a late
    ///     failover to a stale secondary can never move the client backward; a later, newer primary
    ///     win heals forward.</description></item>
    ///   <item><description>A same-generation snapshot is a no-op (not strictly greater), so an equal
    ///     second leg can't re-install or flap.</description></item>
    /// </list>
    /// The decision and the install are taken under <see cref="_installLock"/> so they are atomic
    /// with respect to every other network install path.
    ///
    /// <para>The two rejection shapes are reported separately (<see cref="NetworkInstallOutcome"/>)
    /// because they mean different things to failover telemetry: only a STRICTLY older payload is a
    /// leg trying to move the client backwards (<c>guardRejected</c>), while an equal-generation
    /// re-delivery is ordinary steady-state traffic (qfg-rr5b). A rejected unversioned payload is
    /// likewise not provably older (it carries no ordering info), so it is a silent no-op too
    /// (qfg-9dxb.9). The INSTALL decision is identical for all three — none is installed.</para>
    ///
    /// <para><c>OnConfigChange</c> fires after <see cref="_installLock"/> is released, so a slow or
    /// re-entrant handler never stalls or deadlocks the other install paths (qfg-6vno.12).</para>
    /// </summary>
    private NetworkInstallOutcome InstallFromNetwork(ConfigEnvelope envelope, int sourceIndex)
    {
        var outcome = InstallFromNetworkLocked(envelope, sourceIndex);
        DeliverConfigChanges();
        return outcome;
    }

    private NetworkInstallOutcome InstallFromNetworkLocked(ConfigEnvelope envelope, int sourceIndex)
    {
        lock (_installLock)
        {
            int incoming = envelope.Meta?.Generation ?? 0;
            // Canonical rule (mirrors sdk-go shouldInstall, qfg-9dxb.9):
            //   first install (_networkInstallCount == 0) -> install, even at generation 0
            //   incoming <= 0 (unversioned)              -> install ONLY if held == 0 (the client
            //                                               has never held a real generation)
            //   otherwise                                -> install iff incoming > held
            // Pre-watermark servers that sent generation 0 on every payload are long dead. Gen 0
            // now only comes from a server whose git object store is damaged (rev-count failed) —
            // the least trustworthy source — so it must never override a held real generation.
            // Such a rejection is NOT provably older (no ordering info), so it is a silent no-op,
            // never counted as guardRejected.
            if (_networkInstallCount > 0)
            {
                if (incoming <= 0)
                {
                    if (_heldGeneration > 0)
                    {
                        return NetworkInstallOutcome.RejectedUnversioned;
                    }
                }
                else if (incoming <= _heldGeneration)
                {
                    return incoming == _heldGeneration
                        ? NetworkInstallOutcome.RejectedSameGeneration
                        : NetworkInstallOutcome.RejectedOlder;
                }
            }
            InstallEnvelope(envelope);
            // An unversioned install (incoming <= 0, only reachable while held <= 0) must never
            // LOWER the held watermark (qfg-9dxb.3 Fix A); Math.Max lifts a fresh client's -1
            // sentinel to 0 on an unversioned first install.
            _heldGeneration = incoming > 0 ? incoming : Math.Max(_heldGeneration, incoming);
            _networkInstallCount++;
            if (sourceIndex >= 0)
            {
                _resolvedFromIndex = sourceIndex;
            }
            if (_networkInstallCount == 1)
            {
                LogInitRecoveredIfFailed(sourceIndex, envelope.Meta?.Version);
            }
            return NetworkInstallOutcome.Installed;
        }
    }

    /// <summary>
    /// What the reject-older guard did with an envelope arriving on a network install path. The two
    /// rejection shapes are distinguished only for failover telemetry (qfg-rr5b) — neither installs.
    /// </summary>
    private enum NetworkInstallOutcome
    {
        /// <summary>The envelope advanced the watermark (or was a fresh client's first install, or an unversioned payload while held == 0) and was installed.</summary>
        Installed,

        /// <summary>
        /// The incoming generation EQUALLED the held generation: the server re-delivered config this
        /// client already holds (SSE reconnect resend, cold-ETag poll, fallback-poller engage fetch).
        /// A silent no-op — normal steady-state traffic, never counted as <c>guardRejected</c>.
        /// </summary>
        RejectedSameGeneration,

        /// <summary>
        /// The incoming generation was STRICTLY LOWER than the held generation: a leg tried to move
        /// this client backwards. This is the only shape counted as <c>guardRejected</c>.
        /// </summary>
        RejectedOlder,

        /// <summary>
        /// The incoming payload was UNVERSIONED (generation &lt;= 0) while a real (positive) generation
        /// is held. Gen 0 only comes from a damaged-store server, so it must not override the held
        /// generation — but it carries no ordering info, so it is not provably older: a silent no-op,
        /// never counted as <c>guardRejected</c> (qfg-9dxb.9).
        /// </summary>
        RejectedUnversioned,
    }

    /// <summary>
    /// Test/diagnostic: the <c>Meta.generation</c> currently held, or <c>-1</c> if no network
    /// envelope has been installed yet. Used by the chaos ordering rig to assert the SDK holds the
    /// higher generation and never regresses.
    /// </summary>
    internal int HeldGeneration { get { lock (_installLock) { return _heldGeneration; } } }

    /// <summary>Test/diagnostic: count of envelopes actually installed from the network (rejected-older installs excluded).</summary>
    internal int NetworkInstallCount { get { lock (_installLock) { return _networkInstallCount; } } }

    /// <summary>
    /// Test/diagnostic: which leg served the last accepted HTTP install — <c>"primary"</c> (index 0),
    /// <c>"secondary"</c> (index 1), <c>"url{n}"</c> for further legs, or <c>"none"</c> before the
    /// first install. Lets the chaos failover rig assert a failover resolved off the secondary.
    /// </summary>
    internal string ResolvedFrom
    {
        get
        {
            int idx;
            lock (_installLock) { idx = _resolvedFromIndex; }
            return idx switch
            {
                < 0 => "none",
                0 => "primary",
                1 => "secondary",
                _ => FormattableString.Invariant($"url{idx}"),
            };
        }
    }

    /// <summary>
    /// Test/diagnostic: whether the live SSE stream EVER established a connection on a
    /// non-primary stream leg. Always <c>false</c> by design — <see cref="SseClient"/> pins the
    /// stream to <c>StreamUrls[0]</c> and never walks the failover list (the f05 invariant;
    /// failover is an HTTP-poll-only property). Derived from the transport's real connection
    /// bookkeeping (<see cref="SseClient.MaxConnectedStreamIndex"/>), not assumed, so the chaos
    /// suite's f05 assertion catches a regression that reintroduces stream-leg walking.
    /// </summary>
    internal bool SseFailedOverToSecondary => (_sseClient?.MaxConnectedStreamIndex ?? -1) > 0;

    /// <summary>Test/diagnostic: whether the Layer 2 fallback poller is engaged right now.</summary>
    internal bool FallbackPollerActive => _fallbackPoller?.Active ?? false;

    /// <summary>Layer 1 counters shared by every SSE stream this client starts (test-only, qfg-6vno.4).</summary>
    private readonly SseCounters _sseCounters = new();

    /// <summary>
    /// Internal, test-only: every SSE connect attempt (chaos <c>quonfig_sse_connect_attempts_total</c>).
    /// </summary>
    internal long SseConnectAttemptsTotal => _sseCounters.ConnectAttempts;

    /// <summary>
    /// Test-only: SSE attempts that got a 200 and ended before their first valid frame. The chaos
    /// harness reads it to prove a half-open inject really produced "200 then close".
    /// </summary>
    internal long SseOkWithoutFrameTotal => _sseCounters.OkWithoutFrame;

    /// <summary>
    /// Internal, test-only: <c>quonfig_sdk_worker_restart_total</c> for <paramref name="layer"/>.
    /// Layer "1" is the SSE worker (each run that ended and was restarted); layer "2" is the
    /// supervised fallback poller. Any other layer reads 0.
    /// </summary>
    internal long WorkerRestartTotal(string layer) => layer switch
    {
        "1" => _sseCounters.WorkerRestarts,
        "2" => _supervisor?.WorkerRestartTotal("2") ?? 0,
        _ => 0,
    };

    /// <summary>
    /// Test/diagnostic: the failover-telemetry collector (hedge-fired / guard-rejected / resolved-from
    /// counters), or <c>null</c> when telemetry is fully opted out. Lets tests drain the counters to
    /// prove the failover call sites fired. (qfg-41nh.18)
    /// </summary>
    internal FailoverCollector? Failover => _failover;

    /// <summary>Test/diagnostic: the telemetry reporter, or <c>null</c> when no telemetry runs (qfg-y8je.10).</summary>
    internal TelemetryReporter? TelemetryReporter => _telemetryReporter;

    /// <summary>Test/diagnostic: the evaluation-summary collector, or <c>null</c> when no telemetry runs.</summary>
    internal EvaluationSummaryCollector? EvaluationSummaries => _summaries;

    /// <summary>Test/diagnostic: the context-shape collector, or <c>null</c> when no telemetry runs.</summary>
    internal ContextShapeCollector? ContextShapes => _shapes;

    /// <summary>Test/diagnostic: the example-context collector, or <c>null</c> when no telemetry runs.</summary>
    internal ExampleContextCollector? ExampleContexts => _examples;

    /// <summary>Test/diagnostic: the effective api URLs after <c>QUONFIG_DOMAIN</c> + explicit-override resolution.</summary>
    internal IReadOnlyList<string> ResolvedApiUrls => _apiUrls;

    /// <summary>Test/diagnostic: the effective SSE stream URLs (derived from <see cref="ResolvedApiUrls"/> unless set explicitly).</summary>
    internal IReadOnlyList<string> ResolvedStreamUrls => _streamUrls;

    /// <summary>Test/diagnostic: the effective telemetry URL after resolution.</summary>
    internal string ResolvedTelemetryUrl => _telemetryUrl;

    private void InstallEnvelope(ConfigEnvelope envelope)
    {
        // Environment resolution (cross-SDK contract, qfg-pinh):
        //   - Delivery (HTTP/SSE SDK-key) mode: the server's meta.environment is AUTHORITATIVE.
        //     Always adopt it, overwriting any Environment pin — the pin is datadir-only and was
        //     warned-about at init. This matches sdk-go, where eval never branches on the pin and
        //     always uses the installed envelope's meta.environment.
        //   - Datadir/datafile mode: meta.environment already carries the pin (DatadirLoader sets
        //     meta.environment = pin) or the datafile's own slug. Keep the prior "only when the
        //     pin is empty" fallback so an explicit pin still wins over a datafile's embedded slug.
        // Without adopting meta.environment the evaluator runs with a null env id, never matches the
        // singular per-config "environment" block, and silently serves the row's default rules. (qfg-64m9)
        if (_isDeliveryMode)
        {
            _effectiveEnvironment = envelope.Meta?.Environment;
        }
        else if (string.IsNullOrEmpty(_effectiveEnvironment) && !string.IsNullOrEmpty(envelope.Meta?.Environment))
        {
            _effectiveEnvironment = envelope.Meta!.Environment;
        }
        var store = _store ?? new ConfigStore();
        store.Update(envelope);
        if (_store is null)
        {
            _store = store;
            // Build the evaluator the first time we have a store. Subsequent updates re-use the
            // same evaluator — ConfigStore.Update atomically swaps the underlying map so the
            // evaluator's _store reference always sees the latest snapshot.
            _evaluator = NewEvaluator(store);
        }
        lock (_stateLock)
        {
            _localLastRefresh = DateTimeOffset.UtcNow;
        }
        _readyTcs.TrySetResult(true);
        // Queue the OnConfigChange delivery; the caller fires it via DeliverConfigChanges once it
        // has released _installLock. Never run user code here (qfg-6vno.12).
        Interlocked.Increment(ref _pendingConfigChanges);
    }

    /// <summary>
    /// Fires every queued <c>OnConfigChange</c> delivery, one at a time, in install order. Called by
    /// each install path after it releases <see cref="_installLock"/>. A single drainer keeps the
    /// handlers serial: a thread that finds another one draining leaves its queued delivery to that
    /// drainer, so an install made from inside a handler (or concurrently on another thread) is
    /// delivered after the running handler returns, and never blocks on it. Mirrors sdk-go qfg-eefx.
    /// </summary>
    private void DeliverConfigChanges()
    {
        while (Volatile.Read(ref _pendingConfigChanges) > 0)
        {
            if (Interlocked.CompareExchange(ref _configChangeDraining, 1, 0) != 0)
            {
                return; // the active drainer will pick our delivery up
            }
            try
            {
                while (Volatile.Read(ref _pendingConfigChanges) > 0)
                {
                    Interlocked.Decrement(ref _pendingConfigChanges);
                    FireConfigChange();
                }
            }
            finally
            {
                Volatile.Write(ref _configChangeDraining, 0);
            }
            // Re-check: a delivery queued between our last read and releasing the drain flag would
            // otherwise be stranded (its thread saw us draining and returned).
        }
    }

    private void FireConfigChange()
    {
        var handler = OnConfigChange;
        if (handler is null) return;
        foreach (var del in handler.GetInvocationList())
        {
            try
            {
                ((Action)del)();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogWarning(ex, "quonfig: OnConfigChange handler threw: {Message}", ex.Message);
            }
        }
    }

    private void UpdateConnectionState(ConnectionState next)
    {
        var sup = _supervisor;
        sup?.SetConnectionState(next);

        ConnectionState previous;
        bool fire;
        lock (_stateLock)
        {
            previous = _lastObservedState;
            fire = previous != next;
            _lastObservedState = next;
        }
        if (!fire) return;
        var handler = OnConnectionStateChange;
        if (handler is null) return;
        foreach (var del in handler.GetInvocationList())
        {
            try
            {
                ((Action<ConnectionState>)del)(next);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogWarning(ex, "quonfig: OnConnectionStateChange handler threw: {Message}", ex.Message);
            }
        }
    }

    // ---------------- Evaluation core ----------------

    private EvaluationDetails<T> TypedDetailsRaw<T>(
        string key,
        ContextSet? contexts,
        T fallback,
        EvalValueType expected,
        Func<object?, T> coerce) =>
        TypedDetailsRaw(key, contexts, fallback, expected, coerce, out _);

    // resolveError: the QuonfigException (env var not set / not coercible, decryption failure)
    // raised while resolving the matched value, so the public getters can apply the
    // malformed-value contract (qfg-2agi.17). Null for every other outcome.
    private EvaluationDetails<T> TypedDetailsRaw<T>(
        string key,
        ContextSet? contexts,
        T fallback,
        EvalValueType expected,
        Func<object?, T> coerce,
        out QuonfigException? resolveError)
    {
        resolveError = null;
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(key);
#else
        if (key is null) throw new ArgumentNullException(nameof(key));
#endif
        var store = _store;
        var evaluator = _evaluator;
        if (store is null || evaluator is null)
        {
            return ErrorDetails(key, fallback, ErrorCode.General, "Quonfig client not yet initialized");
        }

        var cfg = store.Get(key);
        if (cfg is null)
        {
            return ErrorDetails(key, fallback, ErrorCode.FlagNotFound, FormattableString.Invariant($"config \"{key}\" not found"));
        }

        var effective = MergeContexts(_globalContext, contexts) ?? new ContextSet();

        // Context telemetry (qfg-gxm6): record the shape / example of the evaluation context on every
        // resolved-config evaluation, mirroring sdk-java. No-op when telemetry is disabled (collectors
        // null) or when ContextUploadMode is None (the collectors self-gate on the mode).
        // Telemetry never throws into a getter (qfg-goi1.2.15). Each collector is guarded on its
        // own, so a failure in one does not skip the other.
#pragma warning disable CA1031 // telemetry containment at the eval boundary; logged once
        try
        {
            _shapes?.Push(effective);
        }
        catch (Exception ex)
        {
            LogTelemetryFailureOnce(ex);
        }
        try
        {
            _examples?.Push(effective);
        }
        catch (Exception ex)
        {
            LogTelemetryFailureOnce(ex);
        }
#pragma warning restore CA1031

        EvaluationMatch match;
        try
        {
            match = evaluator.Evaluate(cfg, effective, _effectiveEnvironment);
        }
        catch (QuonfigException ex)
        {
            resolveError = ex;
            return RowErrorDetails(cfg, fallback, ErrorCode.General, ex.Message);
        }
#pragma warning disable CA1031 // crash containment at the eval boundary
        catch (Exception ex)
        {
            return RowErrorDetails(cfg, fallback, ErrorCode.General, ex.Message);
        }
#pragma warning restore CA1031

        if (match.MissingHashPropertyName is not null && _hashPropertyMissingWarned.TryAdd(match.ConfigKey, 0))
        {
            _logger.LogWarning(
                "quonfig: weighted rollout for \"{Key}\" hashes on \"{Property}\" which is missing from context; hashing an empty value instead",
                match.ConfigKey, match.MissingHashPropertyName);
        }

        if (!match.IsMatch || match.Value is null)
        {
            return new EvaluationDetails<T>(
                fallback,
                Reason.Default,
                "default",
                null,
                null,
                null,
                MetadataFor(match, Reason.Default));
        }

        if (!IsCompatible(match.Value.Type, expected))
        {
            return RowErrorDetails(
                cfg, fallback, ErrorCode.TypeMismatch,
                FormattableString.Invariant($"config \"{key}\" is {match.Value.Type}, caller expected {expected}"));
        }

        T typed;
        try
        {
            typed = coerce(match.Value.Payload);
        }
        catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException)
        {
            // A stored value that cannot be read as the requested type follows the same
            // malformed-value contract as an ENV_VAR one (qfg-2agi.12): Resolved() logs a warning
            // once per key; a plain typed getter raises under OnNoDefault.Throw when no
            // defaultValue is supplied, a Get*Details getter reports Reason=Error. The message names the key only; the raw value may be sensitive.
            resolveError = new QuonfigCoercionException(
                FormattableString.Invariant($"config \"{key}\" cannot be read as {expected}: {ex.Message}"), ex);
            return RowErrorDetails(
                cfg, fallback, ErrorCode.TypeMismatch,
                FormattableString.Invariant($"cannot return \"{key}\" as {expected}: {ex.Message}"));
        }

        var matchReason = MapMatchReason(match);
        var variant = VariantFor(matchReason, match.RuleIndex, match.WeightedValueIndex);

        // Evaluation-summary telemetry (qfg-gxm6): record one observation per resolved evaluation,
        // mirroring sdk-java. Confidential / encrypted values are redacted to the cross-SDK
        // reportable marker so plaintext secrets never reach the wire. The collector self-skips
        // LOG_LEVEL rows and no-ops when disabled; ConfigType is the raw wire type string
        // (lowercase, e.g. "config" / "feature_flag" / "log_level"), matching sdk-node/sdk-go.
        if (_summaries is not null)
        {
#pragma warning disable CA1031 // telemetry containment at the eval boundary; logged once (qfg-goi1.2.15)
            try
            {
                string? reportable = Eval.Resolver.ReportableValueFor(match.Value);
                _summaries.Push(new EvaluationStat(
                    match.ConfigId,
                    match.ConfigKey,
                    TryGetType(cfg) ?? string.Empty,
                    match.RuleIndex,
                    match.WeightedValueIndex,
                    typed,
                    reportable,
                    matchReason));
            }
            catch (Exception ex)
            {
                LogTelemetryFailureOnce(ex);
            }
#pragma warning restore CA1031
        }

        // JSON and string-list values are stored once per config and shared by every evaluation.
        // Hand the caller its own deep copy, with the same runtime types (Dictionary<string, object?>,
        // List<object?>, List<string>, string[]), so editing a result cannot change the config for
        // anyone else (qfg-goi1.2.15). Telemetry above keeps the stored value, which nobody mutates.
        if (expected == EvalValueType.Json || expected == EvalValueType.StringList)
        {
            typed = (T)CopyForCaller(typed)!;
        }

        return new EvaluationDetails<T>(
            typed,
            matchReason,
            variant,
            variantIndex: null,
            errorCode: null,
            errorMessage: null,
            metadata: MetadataFor(match, matchReason));
    }

    // raise: true for the plain typed getters, which apply OnNoDefault.Throw; false for the
    // Get*Details getters, which never throw and report Reason=Error instead (qfg-2agi.12).
    private EvaluationDetails<T> Evaluate<T>(
        string key,
        ContextSet? contexts,
        T defaultValue,
        EvalValueType expected,
        Func<object?, T> coerce,
        bool raise)
    {
        var details = TypedDetailsRaw(key, contexts, defaultValue, expected, coerce, out var resolveError);
        return Resolved(key, defaultValue, details, resolveError, raise);
    }

    // Malformed-value contract (qfg-2agi.17): a value that cannot be resolved logs a warning once
    // per key in every mode; with no defaultValue under OnNoDefault.Throw a plain typed getter
    // raises the specific QuonfigException subtype; otherwise (and always for Get*Details) the
    // default (or null) is returned with Reason=Error.
    private EvaluationDetails<T> Resolved<T>(string key, T defaultValue, EvaluationDetails<T> details, QuonfigException? resolveError, bool raise)
    {
        if (resolveError is not null)
        {
            if (_resolveErrorWarned.TryAdd(key, 0))
            {
                // A coercion message can carry a parser message that quotes the value (a stored value
                // can be sensitive too), so log a fixed reason instead.
                var why = resolveError is QuonfigCoercionException
                    ? "value cannot be converted to the config's declared type"
                    : resolveError.Message;
                _logger.LogWarning(
                    "quonfig: config \"{Key}\" could not be resolved ({ErrorType}): {Reason}; returning default",
                    key, resolveError.GetType().Name, why);
            }
            if (raise && defaultValue is null && _opts.OnNoDefault == OnNoDefault.Throw) throw resolveError;
            return details;
        }
        return ApplyOnNoDefault(key, defaultValue, details, raise);
    }

    private EvaluationDetails<T> ApplyOnNoDefault<T>(string key, T defaultValue, EvaluationDetails<T> details, bool raise)
    {
        if (details.Reason != Reason.Error || details.ErrorCode != ErrorCode.FlagNotFound) return details;
        if (defaultValue is not null) return details;
        if (_opts.OnNoDefault == OnNoDefault.Throw)
        {
            // Get*Details report FlagNotFound instead of throwing (qfg-2agi.12).
            if (!raise) return details;
            throw new QuonfigKeyNotFoundException(
                FormattableString.Invariant($"config \"{key}\" not found and no defaultValue supplied"));
        }
        if (_opts.OnNoDefault == OnNoDefault.Warn)
        {
            _logger.LogWarning(
                "quonfig: config \"{Key}\" not found and no defaultValue supplied; returning default", key);
        }
        return details;
    }

    private EvaluationDetails<T> ErrorDetails<T>(string key, T fallback, ErrorCode code, string message) =>
        new EvaluationDetails<T>(
            fallback,
            Reason.Error,
            "default",
            null,
            code,
            message,
            BaseMetadata(null, key, null));

    private EvaluationDetails<T> RowErrorDetails<T>(ConfigResponse cfg, T fallback, ErrorCode code, string message)
    {
        IReadOnlyDictionary<string, object?> meta = BaseMetadata(
            TryGetId(cfg), cfg.Key, TryGetType(cfg));
        return new EvaluationDetails<T>(
            fallback,
            Reason.Error,
            "default",
            null,
            code,
            message,
            meta);
    }

    private IReadOnlyDictionary<string, object?> BaseMetadata(string? configId, string? configKey, string? configType)
    {
        var m = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(configId)) m["configId"] = configId;
        if (!string.IsNullOrEmpty(configKey)) m["configKey"] = configKey;
        if (!string.IsNullOrEmpty(configType)) m["configType"] = configType;
        if (!string.IsNullOrEmpty(_effectiveEnvironment)) m["environment"] = _effectiveEnvironment;
        return m;
    }

    private IReadOnlyDictionary<string, object?> MetadataFor(EvaluationMatch match, Reason reason)
    {
        var m = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(match.ConfigId)) m["configId"] = match.ConfigId;
        if (!string.IsNullOrEmpty(match.ConfigKey)) m["configKey"] = match.ConfigKey;
        m["configType"] = match.ValueType.ToString();
        if (match.RuleIndex >= 0 && (reason == Reason.TargetingMatch || reason == Reason.Split))
        {
            m["ruleIndex"] = match.RuleIndex;
        }
        if (reason == Reason.Split && match.WeightedValueIndex >= 0)
        {
            m["weightedValueIndex"] = match.WeightedValueIndex;
        }
        if (match.MissingHashPropertyName is not null) m["hashPropertyMissing"] = true;
        if (!string.IsNullOrEmpty(_effectiveEnvironment)) m["environment"] = _effectiveEnvironment;
        return m;
    }

    private static Reason MapMatchReason(EvaluationMatch match)
    {
        if (!match.IsMatch) return Reason.Default;
        return match.Reason switch
        {
            Reason.Static => Reason.Static,
            Reason.TargetingMatch => Reason.TargetingMatch,
            Reason.Split => Reason.Split,
            Reason.Default => Reason.Default,
            _ => Reason.Default,
        };
    }

    private static string VariantFor(Reason reason, int ruleIndex, int weightedIndex) => reason switch
    {
        Reason.Static => "static",
        Reason.TargetingMatch => ruleIndex >= 0
            ? FormattableString.Invariant($"targeting:{ruleIndex}")
            : "targeting",
        Reason.Split => weightedIndex >= 0
            ? FormattableString.Invariant($"split:{weightedIndex}")
            : "split",
        _ => "default",
    };

    private static bool IsCompatible(EvalValueType actual, EvalValueType requested)
    {
        if (actual == requested) return true;
        // log_level rows are STRING-shaped on the wire; allow string getters to read them.
        if (requested == EvalValueType.String && actual == EvalValueType.LogLevel) return true;
        if (requested == EvalValueType.String && actual == EvalValueType.Duration) return true;
        return false;
    }

    /// <summary>Internal: per-call merge of base + overlay. Overlay wins on key collision.</summary>
    internal static ContextSet? MergeContexts(ContextSet? baseCtx, ContextSet? overlay)
    {
        if (baseCtx is null && overlay is null) return null;
        if (overlay is null || overlay.Count == 0) return baseCtx;
        if (baseCtx is null || baseCtx.Count == 0) return overlay;

        var merged = new ContextSet();
        CopyInto(merged, baseCtx);
        CopyInto(merged, overlay);
        return merged;
    }

    private static void CopyInto(ContextSet dest, ContextSet src)
    {
        foreach (var kv in src)
        {
            var props = new ContextProperties();
            foreach (var p in kv.Value) props[p.Key] = p.Value;
            dest[kv.Key] = props;
        }
    }

    // ---------------- ShouldLog / GetLogLevel hierarchy ----------------

    private string? ResolveLogLevelString(string loggerPath, ContextSet? userCtx)
    {
        var withLoggerCtx = new ContextSet();
        if (userCtx is not null)
        {
            foreach (var kv in userCtx)
            {
                var props = new ContextProperties();
                foreach (var p in kv.Value) props[p.Key] = p.Value;
                withLoggerCtx[kv.Key] = props;
            }
        }
        var loggingCtx = new ContextProperties();
        loggingCtx["key"] = new ContextValueString(loggerPath);
        withLoggerCtx[LoggingContextName] = loggingCtx;

        if (!string.IsNullOrEmpty(_opts.LoggerKey))
        {
            return LookupLogLevel(_opts.LoggerKey!, withLoggerCtx);
        }

        string key = loggerPath;
        while (true)
        {
            var resolved = LookupLogLevel(key, withLoggerCtx);
            if (resolved is not null) return resolved;
            if (key.Length == 0) return null;
#if NETSTANDARD2_0
            int dot = key.LastIndexOf('.');
#else
            int dot = key.LastIndexOf('.');
#endif
            key = dot < 0 ? string.Empty : key.Substring(0, dot);
        }
    }

    private string? LookupLogLevel(string configKey, ContextSet ctx)
    {
        var details = TypedDetailsRaw<string?>(configKey, ctx, null, EvalValueType.String, CoerceString);
        if (details.Reason == Reason.Error) return null;
        return details.Value;
    }

    // ---------------- Coercion helpers ----------------

    private static string? CoerceString(object? payload) => payload switch
    {
        null => null,
        string s => s,
        TimeSpan t => System.Xml.XmlConvert.ToString(t),
        _ => payload.ToString(),
    };

    private static int? CoerceInt(object? payload) => payload switch
    {
        null => null,
        int i => i,
        long l => checked((int)l),
        double d => checked((int)d),
        _ => Convert.ToInt32(payload, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static long? CoerceLong(object? payload) => payload switch
    {
        null => null,
        long l => l,
        int i => (long)i,
        double d => checked((long)d),
        _ => Convert.ToInt64(payload, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static bool? CoerceBoolNullable(object? payload) => payload switch
    {
        null => null,
        bool b => b,
        _ => Convert.ToBoolean(payload, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static bool CoerceBool(object? payload) => payload switch
    {
        null => false,
        bool b => b,
        _ => false,
    };

    private static double? CoerceDouble(object? payload) => payload switch
    {
        null => null,
        double d => d,
        long l => (double)l,
        int i => (double)i,
        _ => Convert.ToDouble(payload, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static IReadOnlyList<string>? CoerceStringList(object? payload) => payload switch
    {
        null => null,
        IReadOnlyList<string> rs => rs,
        IEnumerable<string> es => es.ToList(),
        _ => throw new InvalidCastException("payload is not a string list"),
    };

    private static object? CoerceJson(object? payload) => payload;

    // Deep copy of a stored JSON / string-list value for the caller (qfg-goi1.2.15). Scalars are
    // immutable and returned as is.
    private static object? CopyForCaller(object? value)
    {
        switch (value)
        {
            case Dictionary<string, object?> dict:
                var obj = new Dictionary<string, object?>(dict.Count, dict.Comparer);
                foreach (var kv in dict) obj[kv.Key] = CopyForCaller(kv.Value);
                return obj;
            case List<object?> list:
                var items = new List<object?>(list.Count);
                foreach (var item in list) items.Add(CopyForCaller(item));
                return items;
            case List<string> strings:
                return new List<string>(strings);
            case string[] array:
                return array.Length == 0 ? array : (string[])array.Clone();
            default:
                return value;
        }
    }

    private Evaluator NewEvaluator(ConfigStore store)
    {
        var evaluator = _opts.EnvLookup is null
            ? new Evaluator(store)
            : new Evaluator(store, new Resolver.EnvLookup(_opts.EnvLookup));
        evaluator.OnRegexTimeout = WarnRegexTimeoutOnce;
        return evaluator;
    }

    // A timed-out regex fails closed, so a rule that always times out never matches. Say so once per
    // config key. The context value is never logged: it may be sensitive.
    private void WarnRegexTimeoutOnce(string configKey, string propertyName)
    {
        if (!_regexTimeoutWarned.TryAdd(configKey, 0)) return;
        _logger.LogWarning(
            "quonfig: config \"{Key}\": a regex match on property \"{Property}\" timed out; the criterion is treated as not matching. Later timeouts for this config are not logged",
            configKey, propertyName);
    }

    private void LogTelemetryFailureOnce(Exception ex)
    {
        if (Interlocked.Exchange(ref _telemetryFailureLogged, 1) != 0) return;
        _logger.LogWarning(
            ex,
            "quonfig: evaluation telemetry failed ({ExceptionType}); evaluation results are unaffected and later telemetry failures are not logged",
            ex.GetType().Name);
    }

    private static TimeSpan? CoerceDuration(object? payload) => payload switch
    {
        null => null,
        TimeSpan t => t,
        string s => IsoDuration.Parse(s),
        _ => throw new InvalidCastException("payload is not a duration"),
    };

    private static string? TryGetId(ConfigResponse cfg) =>
        cfg.Raw.ValueKind == JsonValueKind.Object
        && cfg.Raw.TryGetProperty("id", out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? TryGetType(ConfigResponse cfg) =>
        cfg.Raw.ValueKind == JsonValueKind.Object
        && cfg.Raw.TryGetProperty("type", out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
