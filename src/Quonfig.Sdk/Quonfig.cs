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
    private string? _effectiveEnvironment;

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
    /// always accepts its first snapshot, even at generation 0).
    /// </summary>
    private int _heldGeneration = -1;

    /// <summary>Count of envelopes actually installed from the network (rejected-older installs don't count).</summary>
    private int _networkInstallCount;

    /// <summary><see cref="HttpTransport.LastResolvedIndex"/> captured at the last accepted network install. <c>-1</c> until then.</summary>
    private int _resolvedFromIndex = -1;

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

        ValidateModes(options);

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
            ValidateHttpMode(options);
            _isDeliveryMode = true;
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
    public async Task InitAsync(CancellationToken cancellationToken = default)
    {
        if (_initTcs.Task.IsCompleted)
        {
            await _initTcs.Task.ConfigureAwait(false);
            return;
        }

        var timeoutTask = Task.Delay(_opts.InitTimeout, cancellationToken);
        var completed = await Task.WhenAny(_initTcs.Task, timeoutTask).ConfigureAwait(false);
        if (completed == _initTcs.Task)
        {
            await _initTcs.Task.ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_opts.OnInitFailure == OnInitFailure.Throw)
        {
            throw new QuonfigInitTimeoutException(
                FormattableString.Invariant($"Quonfig client initialization exceeded {_opts.InitTimeout}"));
        }
        // ReturnDefaults: surface no exception. Background init continues; getters return defaults
        // until the first envelope arrives.
    }

    /// <inheritdoc/>
    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        var watcher = _datadirWatcher;
        if (watcher is not null)
        {
            await watcher.DisposeAsync().ConfigureAwait(false);
            _datadirWatcher = null;
        }
        // Stop SSE first; the run task respects the CTS and unwinds.
        try { _sseCts?.Cancel(); } catch (ObjectDisposedException) { }
        var sseTask = _sseRunTask;
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
        _sseClient?.Dispose();
        _httpTransport?.Dispose();
        _sseCts?.Dispose();
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

    /// <inheritdoc/>
    public ConnectionState ConnectionState
    {
        get
        {
            var sup = _supervisor;
            if (sup is not null) return sup.ConnectionState;
            // No supervisor: either datadir/datafile mode (Connected after sync install) or
            // HTTP-mode pre-init (Initializing). The install timestamp tells the two apart.
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
        GetStringDetails(key, contexts, defaultValue).Value;

    /// <inheritdoc/>
    public int? GetInt(string key, ContextSet? contexts = null, int? defaultValue = null) =>
        GetIntDetails(key, contexts, defaultValue).Value;

    /// <inheritdoc/>
    public long? GetLong(string key, ContextSet? contexts = null, long? defaultValue = null) =>
        GetLongDetails(key, contexts, defaultValue).Value;

    /// <inheritdoc/>
    public bool? GetBool(string key, ContextSet? contexts = null, bool? defaultValue = null) =>
        GetBoolDetails(key, contexts, defaultValue).Value;

    /// <inheritdoc/>
    public double? GetDouble(string key, ContextSet? contexts = null, double? defaultValue = null) =>
        GetDoubleDetails(key, contexts, defaultValue).Value;

    /// <inheritdoc/>
    public IReadOnlyList<string>? GetStringList(string key, ContextSet? contexts = null, IReadOnlyList<string>? defaultValue = null) =>
        GetStringListDetails(key, contexts, defaultValue).Value;

    /// <inheritdoc/>
    public object? GetJson(string key, ContextSet? contexts = null, object? defaultValue = null) =>
        GetJsonDetails(key, contexts, defaultValue).Value;

    /// <inheritdoc/>
    public TimeSpan? GetDuration(string key, ContextSet? contexts = null, TimeSpan? defaultValue = null) =>
        GetDurationDetails(key, contexts, defaultValue).Value;

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
        ApplyOnNoDefault(key, defaultValue, TypedDetailsRaw<string?>(key, contexts, defaultValue, EvalValueType.String, CoerceString));

    /// <inheritdoc/>
    public EvaluationDetails<int?> GetIntDetails(string key, ContextSet? contexts = null, int? defaultValue = null) =>
        ApplyOnNoDefault(key, defaultValue, TypedDetailsRaw<int?>(key, contexts, defaultValue, EvalValueType.Int, CoerceInt));

    /// <inheritdoc/>
    public EvaluationDetails<long?> GetLongDetails(string key, ContextSet? contexts = null, long? defaultValue = null) =>
        ApplyOnNoDefault(key, defaultValue, TypedDetailsRaw<long?>(key, contexts, defaultValue, EvalValueType.Int, CoerceLong));

    /// <inheritdoc/>
    public EvaluationDetails<bool?> GetBoolDetails(string key, ContextSet? contexts = null, bool? defaultValue = null) =>
        ApplyOnNoDefault(key, defaultValue, TypedDetailsRaw<bool?>(key, contexts, defaultValue, EvalValueType.Bool, CoerceBoolNullable));

    /// <inheritdoc/>
    public EvaluationDetails<double?> GetDoubleDetails(string key, ContextSet? contexts = null, double? defaultValue = null) =>
        ApplyOnNoDefault(key, defaultValue, TypedDetailsRaw<double?>(key, contexts, defaultValue, EvalValueType.Double, CoerceDouble));

    /// <inheritdoc/>
    public EvaluationDetails<IReadOnlyList<string>?> GetStringListDetails(string key, ContextSet? contexts = null, IReadOnlyList<string>? defaultValue = null) =>
        ApplyOnNoDefault(key, defaultValue, TypedDetailsRaw<IReadOnlyList<string>?>(key, contexts, defaultValue, EvalValueType.StringList, CoerceStringList));

    /// <inheritdoc/>
    public EvaluationDetails<object?> GetJsonDetails(string key, ContextSet? contexts = null, object? defaultValue = null) =>
        ApplyOnNoDefault(key, defaultValue, TypedDetailsRaw<object?>(key, contexts, defaultValue, EvalValueType.Json, CoerceJson));

    /// <inheritdoc/>
    public EvaluationDetails<TimeSpan?> GetDurationDetails(string key, ContextSet? contexts = null, TimeSpan? defaultValue = null) =>
        ApplyOnNoDefault(key, defaultValue, TypedDetailsRaw<TimeSpan?>(key, contexts, defaultValue, EvalValueType.Duration, CoerceDuration));

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

    private static void ValidateHttpMode(QuonfigOptions opts)
    {
        if (string.IsNullOrEmpty(opts.SdkKey))
        {
            throw new ArgumentException(
                "QuonfigOptions.SdkKey required for HTTP+SSE mode; set SdkKey or use Datadir/Datafile",
                nameof(opts));
        }
        if (opts.ApiUrls.Count == 0)
        {
            throw new ArgumentException(
                "QuonfigOptions.ApiUrls must contain at least one URL", nameof(opts));
        }
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

        var devContext = DevContext.Load(options.ApiUrls, options.EnvLookup, _logger);
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
        }
        catch (Exception ex)
        {
            // Parse-then-swap: keep serving the previous envelope, do NOT fire OnConfigChange.
            _logger.LogWarning(ex, "quonfig: datadir reload failed; keeping previous envelope: {Message}", ex.Message);
        }
    }

    private async Task RunHttpInitAsync()
    {
        HttpTransport? transport = null;
        // Scope initCts outside the try so the catch clauses can distinguish a real init
        // timeout (initCts fired, or HttpClient.Timeout fired at the same per-request limit)
        // from a fast-fail transport error. The race between initCts and the underlying
        // HttpClient.Timeout — both set to _opts.InitTimeout — means either can win, and the
        // resulting exception type varies (OperationCanceledException vs HttpTransport's
        // QuonfigException wrap). Surface either as QuonfigInitTimeoutException so the
        // cross-SDK initialization_timeout contract holds. (qfg-zp7i.15-followup)
        using var initCts = new CancellationTokenSource(_opts.InitTimeout);
        try
        {
            var uris = _opts.ApiUrls.Select(u => new Uri(u, UriKind.Absolute));
            // Per-URL config-fetch timeout (qfg-7h5d.1.11): a hung primary aborts after
            // ConfigFetchTimeout on the SEQUENTIAL FetchAsync path. The init/refresh install path
            // uses the parallel hedge (qfg-7h5d.1.14): fire the primary first and, only if it is slow
            // or errors, fire the secondary in parallel — bounded by HedgeDelay/HedgeAbort. The same
            // transport instance backs the fallback poller and in-band refresh, so every config-fetch
            // path shares the per-leg ETag slots + the reject-older install guard.
            transport = new HttpTransport(
                uris, _opts.SdkKey!, _opts.InitTimeout, _opts.HttpMessageHandler,
                _opts.ConfigFetchTimeout, _opts.ConfigFetchHedgeDelay, _opts.ConfigFetchHedgeAbort);
            _httpTransport = transport;

            // The per-leg hedge abort must be strictly below InitTimeout so an init-path heal leg is
            // not clipped before it can land. Warn (not throw) so a tightened InitTimeout is still
            // usable; the hedge just may not heal-forward within the initial budget. Mirrors sdk-go's
            // construction-time WithConfigFetchHedgeAbort warning.
            if (_opts.InitTimeout <= _opts.ConfigFetchHedgeAbort)
            {
                _logger.LogWarning(
                    "quonfig: InitTimeout ({InitTimeout}) <= config-fetch hedge abort ({HedgeAbort}); init-path heal-forward may be clipped — raise InitTimeout above the hedge abort",
                    _opts.InitTimeout, _opts.ConfigFetchHedgeAbort);
            }

            // Drive one hedged fetch+install cycle. Latches readiness on the FIRST successful install;
            // a late-but-newer leg heals forward after. Throws when every fired leg failed (so the
            // catch clauses below apply OnInitFailure); a non-empty 304/no-op cycle is a success.
            await FetchAndInstallAsync(initial: true, initCts.Token).ConfigureAwait(false);
            _initTcs.TrySetResult(true);
            StartSse();
        }
        catch (OperationCanceledException ex)
        {
            HandleInitFailure(
                new QuonfigInitTimeoutException(
                    FormattableString.Invariant($"Quonfig client initialization exceeded {_opts.InitTimeout}"), ex));
        }
        catch (Exception ex) when (ex is QuonfigException || ex is HttpRequestException || ex is JsonException)
        {
            // Two paths land here as "init timed out":
            //   1. initCts.IsCancellationRequested: caller-CT fired before we caught the wrap.
            //   2. HttpClient.Timeout fired inside SendAsync (per-URL timeout = _opts.InitTimeout),
            //      surfacing a TaskCanceledException that HttpTransport.FetchAsync wraps in a
            //      bare QuonfigException ("HTTP timeout contacting …") because the OCE's
            //      cancellation token is the HttpClient's INTERNAL timeout-CTS, not initCts.
            //      The inner-exception chain ends in an OperationCanceledException, which is
            //      how we recognize it.
            // Either way, the cross-SDK initialization_timeout contract requires
            // QuonfigInitTimeoutException — not bare QuonfigException.
            if (initCts.IsCancellationRequested || ChainContainsOperationCancelled(ex))
            {
                HandleInitFailure(
                    new QuonfigInitTimeoutException(
                        FormattableString.Invariant($"Quonfig client initialization exceeded {_opts.InitTimeout}"), ex));
                return;
            }
            HandleInitFailure(
                new QuonfigException($"Quonfig client initialization failed: {ex.Message}", ex));
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
    /// </summary>
    private async Task FetchAndInstallAsync(bool initial, CancellationToken cancellationToken)
    {
        var transport = _httpTransport;
        if (transport is null) return;

        var reader = transport.FetchHedgedAsync(cancellationToken);

        bool installedOnce = false;
        int fired = 0;
        int failures = 0;
        Exception? lastError = null;

        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var leg))
            {
                fired++;
                if (leg.Error is not null)
                {
                    failures++;
                    lastError = leg.Error;
                    continue;
                }
                if (leg.NotModified || leg.Envelope is null)
                {
                    continue; // 304 — nothing changed on this leg.
                }
                // Reject-older guard + install are atomic under _installLock against every other
                // install path (SSE, fallback poller). TryInstallFromNetwork sets the held generation,
                // install count, and resolved-from leg together (resolvedFrom set atomically with held).
                bool installed = TryInstallFromNetwork(leg.Envelope, leg.LegIndex);
                if (installed)
                {
                    _supervisor?.RecordSuccessfulRefresh();
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
            }
        }

        if (installedOnce)
        {
            return;
        }

        // Nothing installed. If every fired leg failed, surface the failure (init path applies
        // OnInitFailure). Otherwise every leg was a 304 (established client, no change) — a no-op.
        if (initial && fired > 0 && failures == fired && lastError is not null)
        {
            throw lastError;
        }
        // initial with an all-304 / empty cycle, or a non-initial refresh: a no-op success.
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

    private void HandleInitFailure(QuonfigException error)
    {
        if (_opts.OnInitFailure == OnInitFailure.Throw)
        {
            _initTcs.TrySetException(error);
            return;
        }
        // ReturnDefaults: complete init successfully; SSE supervisor will keep trying.
        // Install an empty store so subsequent getters return FlagNotFound (and surface the
        // OnNoDefault policy) rather than "client not yet initialized" — matches sdk-java's
        // behavior under ReturnDefaults + OnNoDefault.Throw.
        InstallEmptyStoreIfNeeded();
        _logger.LogWarning(
            error,
            "quonfig: initial config fetch failed under OnInitFailure.ReturnDefaults; serving defaults until SSE delivers an envelope");
        _initTcs.TrySetResult(true);
        StartSse();
    }

    private void InstallEmptyStoreIfNeeded()
    {
        if (_store is not null) return;
        var store = new ConfigStore();
        _store = store;
        _evaluator = _opts.EnvLookup is null
            ? new Evaluator(store)
            : new Evaluator(store, new Resolver.EnvLookup(_opts.EnvLookup));
    }

    private void StartSse()
    {
        if (Volatile.Read(ref _closed) != 0) return;
        if (string.IsNullOrEmpty(_opts.SdkKey)) return;
        if (_opts.StreamUrls.Count == 0) return;

        var streams = _opts.StreamUrls.Select(u => new Uri(u, UriKind.Absolute)).ToList();

        var sseCts = new CancellationTokenSource();
        _sseCts = sseCts;

        FallbackPoller? fp = null;
        if (_opts.FallbackPollEnabled && _opts.FallbackPollInterval > TimeSpan.Zero)
        {
            fp = new FallbackPoller(
                fetch: ct => DoFallbackFetchAsync(ct),
                interval: _opts.FallbackPollInterval,
                threshold: _opts.FallbackPollThreshold,
                onEngage: () => UpdateConnectionState(Sdk.ConnectionState.FallingBack),
                onDisengage: () => UpdateConnectionState(Sdk.ConnectionState.Connected),
                logger: _logger);
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

        var sse = new SseClient(
            streamUrls: streams,
            sdkKey: _opts.SdkKey!,
            onEnvelope: env => OnSseEnvelope(env),
            onConnect: OnSseConnect,
            onDisconnect: OnSseDisconnect,
            readTimeout: _opts.SseReadTimeout,
            messageHandler: _opts.HttpMessageHandler,
            logger: _logger);
        _sseClient = sse;

        _sseRunTask = Task.Run(() => sse.RunAsync(sseCts.Token));
    }

    private async Task DoFallbackFetchAsync(CancellationToken ct)
    {
        var transport = _httpTransport;
        if (transport is null) return;
        try
        {
            // Fallback poller hedges too (qfg-7h5d.1.14): fire the primary first, hedge the secondary
            // only if slow/erroring. Each accepted leg installs only if it advances the held
            // generation (reject-older), so a failover to an older secondary never regresses an
            // established client. RecordSuccessfulRefresh fires per accepted install inside the loop.
            await FetchAndInstallAsync(initial: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown path.
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
        // 200-OK edge from the SSE worker. Tell the fallback poller SSE is live and flip
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
            TryInstallFromNetwork(envelope, sourceIndex: -1);
            _supervisor?.RecordSuccessfulRefresh();
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
    ///   <item><description>…an established client installs only if the incoming <c>Meta.generation</c>
    ///     is strictly greater than the held generation. An older payload is dropped, so a late
    ///     failover to a stale secondary can never move the client backward; a later, newer primary
    ///     win heals forward.</description></item>
    ///   <item><description>A same-generation snapshot is a no-op (not strictly greater), so an equal
    ///     second leg can't re-install or flap.</description></item>
    /// </list>
    /// The decision and the install are taken under <see cref="_installLock"/> so they are atomic
    /// with respect to every other network install path. Returns true if the envelope was installed.
    /// </summary>
    private bool TryInstallFromNetwork(ConfigEnvelope envelope, int sourceIndex)
    {
        lock (_installLock)
        {
            int incoming = envelope.Meta?.Generation ?? 0;
            // Established client: reject anything that doesn't strictly advance the watermark
            // (older = regression, equal = redundant no-op). A fresh client (_networkInstallCount
            // == 0) always installs, even at generation 0.
            if (_networkInstallCount > 0 && incoming <= _heldGeneration)
            {
                return false;
            }
            InstallEnvelope(envelope);
            _heldGeneration = incoming;
            _networkInstallCount++;
            if (sourceIndex >= 0)
            {
                _resolvedFromIndex = sourceIndex;
            }
            return true;
        }
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
            _evaluator = _opts.EnvLookup is null
                ? new Evaluator(store)
                : new Evaluator(store, new Resolver.EnvLookup(_opts.EnvLookup));
        }
        lock (_stateLock)
        {
            _localLastRefresh = DateTimeOffset.UtcNow;
        }
        FireConfigChange();
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
        Func<object?, T> coerce)
    {
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

        EvaluationMatch match;
        try
        {
            match = evaluator.Evaluate(cfg, effective, _effectiveEnvironment);
        }
        catch (QuonfigException ex)
        {
            return RowErrorDetails(cfg, fallback, ErrorCode.General, ex.Message);
        }
#pragma warning disable CA1031 // crash containment at the eval boundary
        catch (Exception ex)
        {
            return RowErrorDetails(cfg, fallback, ErrorCode.General, ex.Message);
        }
#pragma warning restore CA1031

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
            return RowErrorDetails(
                cfg, fallback, ErrorCode.TypeMismatch,
                FormattableString.Invariant($"cannot return \"{key}\" as {expected}: {ex.Message}"));
        }

        var matchReason = MapMatchReason(match);
        var variant = VariantFor(matchReason, match.RuleIndex, match.WeightedValueIndex);
        return new EvaluationDetails<T>(
            typed,
            matchReason,
            variant,
            variantIndex: null,
            errorCode: null,
            errorMessage: null,
            metadata: MetadataFor(match, matchReason));
    }

    private EvaluationDetails<T> ApplyOnNoDefault<T>(string key, T defaultValue, EvaluationDetails<T> details)
    {
        if (details.Reason != Reason.Error || details.ErrorCode != ErrorCode.FlagNotFound) return details;
        if (defaultValue is not null) return details;
        if (_opts.OnNoDefault == OnNoDefault.Throw)
        {
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

    private static TimeSpan? CoerceDuration(object? payload) => payload switch
    {
        null => null,
        TimeSpan t => t,
        string s => System.Xml.XmlConvert.ToTimeSpan(s),
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
