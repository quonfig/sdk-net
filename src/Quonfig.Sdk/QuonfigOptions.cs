using System;
using System.Collections.Generic;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Quonfig.Sdk.Telemetry;
using Quonfig.Sdk.Wire;

namespace Quonfig.Sdk;

/// <summary>
/// Construction-time configuration for a <see cref="Quonfig"/> client. Mirrors sdk-java's
/// <c>Options</c> and sdk-go's <c>Config</c>; every field has an idiomatic .NET default so the
/// minimal usage is <c>new QuonfigOptions { SdkKey = "..." }</c>.
///
/// <para>Mode selection: if <see cref="Datadir"/> is set the client loads synchronously from a
/// workspace directory tree; if <see cref="Datafile"/> is set it loads from a serialized envelope;
/// otherwise the client uses HTTP+SSE against <see cref="ApiUrls"/> / <see cref="StreamUrls"/>.
/// Modes are mutually exclusive — the constructor throws if more than one is set.</para>
/// </summary>
public sealed class QuonfigOptions
{
    /// <summary>SDK key (used as the Basic-auth password against api-delivery). Required in HTTP+SSE mode.</summary>
    public string? SdkKey { get; set; }

    /// <summary>Built-in production api-delivery base URLs (primary first, then failover).</summary>
    internal static readonly IReadOnlyList<string> DefaultApiUrls = new[]
    {
        "https://primary.quonfig.com",
        "https://secondary.quonfig.com",
    };

    /// <summary>Built-in production api-delivery SSE base URLs (derived by prepending <c>stream.</c> to each <see cref="DefaultApiUrls"/> host).</summary>
    internal static readonly IReadOnlyList<string> DefaultStreamUrls = new[]
    {
        "https://stream.primary.quonfig.com",
        "https://stream.secondary.quonfig.com",
    };

    /// <summary>Built-in production api-telemetry base URL.</summary>
    internal const string DefaultTelemetryUrl = "https://telemetry.quonfig.com";

    private IReadOnlyList<string> _apiUrls = DefaultApiUrls;
    private IReadOnlyList<string> _streamUrls = DefaultStreamUrls;
    private string _telemetryUrl = DefaultTelemetryUrl;

    /// <summary>True once <see cref="ApiUrls"/> has been assigned — an explicit override wins over <c>QUONFIG_DOMAIN</c>.</summary>
    internal bool ApiUrlsExplicit { get; private set; }

    /// <summary>True once <see cref="StreamUrls"/> has been assigned — an explicit override wins over the derive-from-<see cref="ApiUrls"/> default.</summary>
    internal bool StreamUrlsExplicit { get; private set; }

    /// <summary>True once <see cref="TelemetryUrl"/> has been assigned — an explicit override wins over <c>QUONFIG_DOMAIN</c>.</summary>
    internal bool TelemetryUrlExplicit { get; private set; }

    /// <summary>
    /// Ordered list of api-delivery base URLs (primary first, then failover). When unset, defaults
    /// to the cluster derived from <c>QUONFIG_DOMAIN</c> (production <c>quonfig.com</c> when that env
    /// var is unset): <c>primary.&lt;domain&gt;</c> + <c>secondary.&lt;domain&gt;</c>. Setting this
    /// explicitly wins over <c>QUONFIG_DOMAIN</c> and replaces the whole list — a single URL disables
    /// automatic failover to the secondary (the client logs a warning at construction).
    /// </summary>
    public IReadOnlyList<string> ApiUrls
    {
        get => _apiUrls;
        set { _apiUrls = value; ApiUrlsExplicit = true; }
    }

    /// <summary>
    /// Ordered list of api-delivery SSE base URLs. When unset, each entry is derived from the
    /// corresponding <see cref="ApiUrls"/> host by prepending <c>stream.</c> (so an explicit
    /// <see cref="ApiUrls"/> override — or a <c>QUONFIG_DOMAIN</c>-derived one — is followed by the
    /// SSE stream automatically). Only <c>StreamUrls[0]</c> is ever streamed from: the SSE stream is
    /// pinned to the primary and never fails over (retry-forever with backoff); failover is an
    /// HTTP-poll-only property of <see cref="ApiUrls"/>.
    /// </summary>
    public IReadOnlyList<string> StreamUrls
    {
        get => _streamUrls;
        set { _streamUrls = value; StreamUrlsExplicit = true; }
    }

    /// <summary>
    /// api-telemetry base URL. When unset, defaults to the endpoint derived from <c>QUONFIG_DOMAIN</c>
    /// (<c>telemetry.&lt;domain&gt;</c>, production <c>telemetry.quonfig.com</c> when that env var is
    /// unset). Setting this explicitly wins over <c>QUONFIG_DOMAIN</c>.
    /// </summary>
    public string TelemetryUrl
    {
        get => _telemetryUrl;
        set { _telemetryUrl = value; TelemetryUrlExplicit = true; }
    }

    /// <summary>
    /// Environment slug evaluated against (e.g. <c>"production"</c>). Required in datadir mode;
    /// optional otherwise (datafile mode falls back to <c>envelope.meta.environment</c>).
    /// </summary>
    public string? Environment { get; set; }

    /// <summary>
    /// Workspace directory for datadir mode. When set, the constructor reads the on-disk tree
    /// synchronously and no network calls are made.
    /// </summary>
    public string? Datadir { get; set; }

    /// <summary>
    /// Datafile path for datafile mode. When set, the constructor reads the serialized envelope
    /// synchronously and no network calls are made.
    /// </summary>
    public string? Datafile { get; set; }

    /// <summary>
    /// Pre-parsed datafile envelope for datafile mode. Mutually exclusive with <see cref="Datafile"/>.
    /// When set, the envelope is installed at construction with no I/O. If <see cref="Environment"/>
    /// is not set, <c>envelope.meta.environment</c> is used as the evaluation environment.
    /// </summary>
    public ConfigEnvelope? DatafileEnvelope { get; set; }

    /// <summary>Opt-in: watch <see cref="Datadir"/> for file changes and reload atomically.</summary>
    public bool DatadirAutoReload { get; set; }

    /// <summary>Debounce window when <see cref="DatadirAutoReload"/> is on. Defaults to 200ms.</summary>
    public TimeSpan DatadirAutoReloadDebounce { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>How long the initial load may take before the <see cref="OnInitFailure"/> policy applies. Defaults to 10s.</summary>
    public TimeSpan InitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Per-URL deadline for a single config-fetch attempt, applied uniformly to the initial fetch,
    /// the fallback poller, and any in-band refresh. Each base URL in <see cref="ApiUrls"/> gets its
    /// own deadline, so a hung primary aborts after this duration and the secondary is tried within
    /// the remaining <see cref="InitTimeout"/> budget.
    ///
    /// <para>Additive and backward-compatible: the default (~3s) already makes a hung upstream fail
    /// over, so existing callers need not set it. Raise it only if a healthy upstream legitimately
    /// takes longer than 3s to answer; lower it to fail over even faster. A non-positive value falls
    /// back to the default. Bounds a single attempt only — it never touches the long-lived SSE
    /// stream, which keeps its own <see cref="SseReadTimeout"/>. Mirrors sdk-go's
    /// <c>WithConfigFetchTimeout</c>.</para>
    /// </summary>
    public TimeSpan ConfigFetchTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long the parallel-failover hedge waits for the primary leg before ALSO firing the
    /// secondary leg in parallel (it does NOT cancel the primary). A primary that succeeds before
    /// this elapses wins and the secondary is never contacted (cold standby, zero extra load on a
    /// healthy system); a primary that errors fast fires the secondary immediately.
    ///
    /// <para>Additive and backward-compatible (qfg-7h5d.1.14): the default (~2s) sits below a
    /// realistic slow-but-alive primary's worst case yet far enough below the per-leg abort that a
    /// healthy sub-second primary is never hedged. It must be less than <see cref="ConfigFetchHedgeAbort"/>.
    /// A non-positive value falls back to the default. Bounds only the hedged init/refresh
    /// config-fetch path — it never touches the long-lived SSE stream. Mirrors sdk-go's
    /// <c>WithConfigFetchHedgeDelay</c>.</para>
    /// </summary>
    public TimeSpan ConfigFetchHedgeDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Per-leg hard-abort deadline on the hedged config-fetch path. It MUST exceed the longest
    /// healable primary latency so a late-but-newer primary heals forward (rather than aborting), and
    /// MUST be less than <see cref="InitTimeout"/> so the init-path heal leg is not clipped; the
    /// client logs a warning at construction if <see cref="InitTimeout"/> &lt;= this value.
    ///
    /// <para>Additive and backward-compatible (qfg-7h5d.1.14): the default (~6s) sits between the
    /// typical heal latency and the default 10s <see cref="InitTimeout"/>. A non-positive value falls
    /// back to the default. The hedged path uses this in place of <see cref="ConfigFetchTimeout"/>,
    /// which still governs the sequential <c>FetchAsync</c> path. Mirrors sdk-go's
    /// <c>WithConfigFetchHedgeAbort</c>.</para>
    /// </summary>
    public TimeSpan ConfigFetchHedgeAbort { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>Policy when initial HTTP+SSE load exceeds <see cref="InitTimeout"/>. Defaults to <see cref="OnInitFailure.Throw"/>.</summary>
    public OnInitFailure OnInitFailure { get; set; } = OnInitFailure.Throw;

    /// <summary>Policy when a typed getter cannot resolve a value AND the caller supplied no default. Defaults to <see cref="Sdk.OnNoDefault.Throw"/>.</summary>
    public OnNoDefault OnNoDefault { get; set; } = OnNoDefault.Throw;

    /// <summary>
    /// Context merged into every evaluation as the lowest-precedence layer. Per-call contexts
    /// and bound contexts override these values key-by-key.
    /// </summary>
    public ContextSet? GlobalContext { get; set; }

    /// <summary>Master switch for the Layer 2 fallback poller. Defaults to enabled.</summary>
    public bool FallbackPollEnabled { get; set; } = true;

    /// <summary>Cadence between fallback fetches once engaged. Defaults to 60s.</summary>
    public TimeSpan FallbackPollInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>SSE-down duration before Layer 2 engages. Defaults to 120s (cross-SDK contract).</summary>
    public TimeSpan FallbackPollThreshold { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Layer 1 SSE read watchdog. Defaults to 90s. Pass <see cref="TimeSpan.Zero"/> to disable.</summary>
    public TimeSpan SseReadTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Whether to collect per-evaluation summary telemetry. Defaults to <c>true</c>.</summary>
    public bool CollectEvaluationSummaries { get; set; } = true;

    /// <summary>
    /// Granularity of context telemetry uploads. Defaults to <see cref="Sdk.ContextUploadMode.PeriodicExample"/>
    /// (since 1.3.0; was <see cref="Sdk.ContextUploadMode.ShapesOnly"/>): context field names and types,
    /// plus up to one example context per context key per hour, with its values. Set
    /// <see cref="Sdk.ContextUploadMode.ShapesOnly"/> to send field names and types only, or
    /// <see cref="Sdk.ContextUploadMode.None"/> to send no context data.
    /// </summary>
    public ContextUploadMode ContextUploadMode { get; set; } = ContextUploadMode.PeriodicExample;

    /// <summary>
    /// Optional <see cref="ITelemetrySender"/> for tests / DI. When set, the client uses it instead
    /// of the built-in <c>HttpTelemetrySender</c> (which posts to <see cref="TelemetryUrl"/>). Mirrors
    /// sdk-java's <c>telemetrySender</c> option. When null (the default) and an <see cref="SdkKey"/> is
    /// present, the built-in HTTP sender is used; telemetry is still gated on the eval/context opt-outs
    /// (a full opt-out — <see cref="CollectEvaluationSummaries"/> false AND
    /// <see cref="ContextUploadMode"/> <see cref="Sdk.ContextUploadMode.None"/> — emits nothing).
    /// A custom sender that throws is treated as a retryable failure (the batch is kept and handed to
    /// it again later, unchanged).
    /// </summary>
    public ITelemetrySender? TelemetrySender { get; set; }

    /// <summary>
    /// No longer used since 1.3.0: the first telemetry POST happens one <see cref="TelemetryFlushInterval"/>
    /// after start (and on <see cref="Quonfig.CloseAsync"/>). Kept so existing code compiles.
    /// </summary>
    public TimeSpan TelemetryInitialDelay { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Interval between telemetry POSTs (one tick). Defaults to 60s. A non-positive value falls back to the default.</summary>
    public TimeSpan TelemetryFlushInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// No longer used since 1.3.0: the adaptive backoff it capped was replaced by the transport policy
    /// (30s resend floor after a failure, <c>Retry-After</c> honored up to 10 min). Kept so existing code compiles.
    /// </summary>
    public TimeSpan TelemetryMaxInterval { get; set; } = TimeSpan.FromSeconds(600);

    /// <summary>
    /// Overall deadline for one telemetry POST, connect to response. Defaults to 15s (was 30s). A POST
    /// that times out is kept and resent later. A non-positive value falls back to the default.
    /// </summary>
    public TimeSpan TelemetryTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// TCP connect + TLS handshake deadline for telemetry POSTs. Defaults to 5s. Applied on net8.0 when
    /// the SDK builds its own HTTP handler; on netstandard2.0, or with an injected
    /// <see cref="HttpMessageHandler"/>, <see cref="TelemetryTimeout"/> bounds connect as well.
    /// </summary>
    public TimeSpan TelemetryConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum number of failed telemetry batches kept for a later resend. Defaults to 5; the oldest is
    /// dropped beyond it. A non-positive value falls back to the default.
    /// </summary>
    public int TelemetryMaxRetainedBatches { get; set; } = 5;

    /// <summary>
    /// Maximum total size, in bytes, of the failed telemetry batches kept for a later resend. Defaults
    /// to 2,097,152 (2MB); the oldest is dropped beyond it, and a single batch larger than this is sent
    /// once and never kept. A non-positive value falls back to the default.
    /// </summary>
    public int TelemetryMaxRetainedBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    /// A failed telemetry batch older than this is discarded instead of resent. Defaults to 5 minutes.
    /// A non-positive value falls back to the default.
    /// </summary>
    public TimeSpan TelemetryMaxRetainedAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum distinct evaluation-summary keys (config key and type) per telemetry window. Defaults
    /// to 10,000. New keys beyond it are not recorded; keys already recorded keep counting.
    /// </summary>
    public int TelemetryMaxEvaluationSummaries { get; set; } = 10_000;

    /// <summary>
    /// Maximum distinct context-shape fields (context name and field name) per telemetry window.
    /// Defaults to 10,000. New fields beyond it are not recorded.
    /// </summary>
    public int TelemetryMaxContextShapeFields { get; set; } = 10_000;

    /// <summary>
    /// Maximum example contexts per telemetry window (<see cref="Sdk.ContextUploadMode.PeriodicExample"/>).
    /// Defaults to 10,000. Examples beyond it are not recorded.
    /// </summary>
    public int TelemetryMaxExampleContexts { get; set; } = 10_000;

    /// <summary>Test seam: the clock that drives telemetry timers and time checks (contract tests).</summary>
    internal ITelemetryClock? TelemetryClock { get; set; }

    /// <summary>
    /// When set, <see cref="Quonfig.ShouldLog"/> evaluates this single config (with the logger
    /// path injected as <c>quonfig-sdk-logging.key</c>) instead of walking per-logger keys. Mirrors
    /// sdk-node / sdk-go behavior.
    /// </summary>
    public string? LoggerKey { get; set; }

    /// <summary>
    /// Optional callback fired after every envelope install (initial load and each subsequent
    /// SSE / fallback / datadir-watcher refresh). Mirrors sdk-java's <c>onConfigUpdate(Runnable)</c>
    /// and sdk-go's <c>WithOnConfigUpdate</c>: a convenience way to register a config-change
    /// listener at construction. Equivalent to subscribing to the <see cref="Quonfig.OnConfigChange"/>
    /// event before the first load. Subscribers must not throw.
    /// </summary>
    public Action? OnConfigChange { get; set; }

    /// <summary>Optional logger. Defaults to a no-op logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Optional <see cref="HttpMessageHandler"/> for tests / DI (injects into both
    /// <c>HttpTransport</c> and <c>SseClient</c>). Ownership stays with the caller.
    /// </summary>
    public HttpMessageHandler? HttpMessageHandler { get; set; }

    /// <summary>Optional env-var lookup override (testability). Defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
    public Func<string, string?>? EnvLookup { get; set; }

    /// <summary>
    /// Whether to auto-inject the dev-only <c>quonfig-user.email</c> evaluation context, read from
    /// the per-domain tokens file written by <c>qfg login</c> (<c>~/.quonfig/tokens.json</c> for
    /// production). <c>null</c> (the default) means unset: resolution falls through to the
    /// <c>QUONFIG_DEV_CONTEXT</c> env var (<c>"true"</c>/<c>"false"</c>), then defaults to ON. The
    /// loader no-ops when no tokens file exists, so default-on is inert in production. Set
    /// explicitly to <c>false</c> to force it off regardless of the env var. Mirrors sdk-node's
    /// <c>enableQuonfigUserContext</c>.
    /// </summary>
    public bool? EnableQuonfigUserContext { get; set; }
}
