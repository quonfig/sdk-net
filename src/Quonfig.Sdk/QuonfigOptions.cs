using System;
using System.Collections.Generic;
using System.Net.Http;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// Ordered list of api-delivery base URLs (primary first, then failover). Defaults to the
    /// production primary + secondary cluster.
    /// </summary>
    public IReadOnlyList<string> ApiUrls { get; set; } = new[]
    {
        "https://primary.quonfig.com",
        "https://secondary.quonfig.com",
    };

    /// <summary>
    /// Ordered list of api-delivery SSE base URLs. Defaults to the production stream cluster.
    /// </summary>
    public IReadOnlyList<string> StreamUrls { get; set; } = new[]
    {
        "https://stream.primary.quonfig.com",
        "https://stream.secondary.quonfig.com",
    };

    /// <summary>api-telemetry base URL. Defaults to the production endpoint.</summary>
    public string TelemetryUrl { get; set; } = "https://telemetry.quonfig.com";

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

    /// <summary>Opt-in: watch <see cref="Datadir"/> for file changes and reload atomically.</summary>
    public bool DatadirAutoReload { get; set; }

    /// <summary>Debounce window when <see cref="DatadirAutoReload"/> is on. Defaults to 200ms.</summary>
    public TimeSpan DatadirAutoReloadDebounce { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>How long the initial load may take before the <see cref="OnInitFailure"/> policy applies. Defaults to 10s.</summary>
    public TimeSpan InitTimeout { get; set; } = TimeSpan.FromSeconds(10);

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

    /// <summary>Whether to collect per-evaluation summary telemetry. Reserved for qfg-zp7i.12.</summary>
    public bool CollectEvaluationSummaries { get; set; } = true;

    /// <summary>Granularity of context telemetry uploads. Reserved for qfg-zp7i.12.</summary>
    public ContextUploadMode ContextUploadMode { get; set; } = ContextUploadMode.ShapesOnly;

    /// <summary>
    /// When set, <see cref="Quonfig.ShouldLog"/> evaluates this single config (with the logger
    /// path injected as <c>quonfig-sdk-logging.key</c>) instead of walking per-logger keys. Mirrors
    /// sdk-node / sdk-go behavior.
    /// </summary>
    public string? LoggerKey { get; set; }

    /// <summary>Optional logger. Defaults to a no-op logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Optional <see cref="HttpMessageHandler"/> for tests / DI (injects into both
    /// <c>HttpTransport</c> and <c>SseClient</c>). Ownership stays with the caller.
    /// </summary>
    public HttpMessageHandler? HttpMessageHandler { get; set; }

    /// <summary>Optional env-var lookup override (testability). Defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
    public Func<string, string?>? EnvLookup { get; set; }
}
