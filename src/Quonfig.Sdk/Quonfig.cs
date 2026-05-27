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
    private readonly string? _effectiveEnvironment;
    private readonly TaskCompletionSource<bool> _initTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stateLock = new();

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

        if (!string.IsNullOrEmpty(options.Datadir))
        {
            InitDatadir(options.Datadir!);
        }
        else if (!string.IsNullOrEmpty(options.Datafile))
        {
            InitDatafile(options.Datafile!);
        }
        else
        {
            ValidateHttpMode(options);
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
        if (modes > 1)
        {
            throw new ArgumentException(
                "QuonfigOptions: set at most one of Datadir or Datafile",
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
        InstallEnvelope(envelope);
        _initTcs.TrySetResult(true);
    }

    private async Task RunHttpInitAsync()
    {
        HttpTransport? transport = null;
        try
        {
            var uris = _opts.ApiUrls.Select(u => new Uri(u, UriKind.Absolute));
            transport = new HttpTransport(uris, _opts.SdkKey!, _opts.InitTimeout, _opts.HttpMessageHandler);
            _httpTransport = transport;

            using var initCts = new CancellationTokenSource(_opts.InitTimeout);
            var envelope = await transport.FetchAsync(null, initCts.Token).ConfigureAwait(false);
            if (envelope is null)
            {
                throw new QuonfigException("initial config fetch returned 304 with no prior ETag");
            }
            InstallEnvelope(envelope);
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
            HandleInitFailure(
                new QuonfigException($"Quonfig client initialization failed: {ex.Message}", ex));
        }
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
            var envelope = await transport.FetchAsync(transport.LastETag, ct).ConfigureAwait(false);
            if (envelope is null) return; // 304 — nothing changed.
            InstallEnvelope(envelope);
            _supervisor?.RecordSuccessfulRefresh();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown path.
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "A bad envelope from SSE must not tear down the client — keep the previous store, log, continue.")]
    private void OnSseEnvelope(ConfigEnvelope envelope)
    {
        try
        {
            InstallEnvelope(envelope);
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

    private void InstallEnvelope(ConfigEnvelope envelope)
    {
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

        var effective = MergeContexts(_opts.GlobalContext, contexts) ?? new ContextSet();

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
        var variant = VariantFor(matchReason, match.RuleIndex, weightedIndex: -1);
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
