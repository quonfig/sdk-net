using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Quonfig.Sdk.Wire;

namespace Quonfig.Sdk.Transport;

/// <summary>
/// SSE transport for real-time <see cref="ConfigEnvelope"/> updates from the
/// <c>api-delivery</c> <c>GET /api/v2/sse/config</c> endpoint. Mirrors the shape of
/// <c>sdk-java SseClient</c>: a hand-rolled line parser over
/// <see cref="HttpClient.SendAsync(HttpRequestMessage, HttpCompletionOption, CancellationToken)"/>
/// with <see cref="HttpCompletionOption.ResponseHeadersRead"/>, plus a Layer 1 stall watchdog
/// that disposes the response body when no bytes arrive within <see cref="ReadTimeout"/>.
///
/// <para>The watchdog uses a per-connection <see cref="CancellationTokenSource"/> armed via
/// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>, re-armed on every successful
/// read. When the CTS fires, a callback disposes the underlying body
/// <see cref="Stream"/>; the in-flight line read then unblocks
/// (surfaces as <see cref="ObjectDisposedException"/> / <see cref="IOException"/>), the read
/// loop falls through to reconnect with exponential backoff.</para>
///
/// <para>Auth + version headers match <see cref="HttpTransport"/>: HTTP Basic with
/// <c>username=1, password=sdkKey</c>, <c>Accept: text/event-stream</c>, and
/// <c>X-Quonfig-SDK-Version: dotnet/{ver}</c>.</para>
///
/// <para>This class is the Layer 1 worker; lifecycle (start, stop, atomic envelope swap into the
/// resolver, ConnectionState surface) is owned by the Supervisor (bead .10) and public
/// <c>Quonfig</c> client (bead .11). The supervisor passes its own
/// <see cref="CancellationToken"/> to <see cref="RunAsync"/>; a clean shutdown unwinds within
/// 5 seconds of cancellation.</para>
/// </summary>
public sealed class SseClient : IDisposable
{
    /// <summary>Default Layer 1 read watchdog (90s = 3x the api-delivery 30s comment heartbeat).</summary>
    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Fixed bound on the wait for SSE response headers (90s). Deliberately NOT derived from
    /// <see cref="ReadTimeout"/>: api-delivery sends headers together with the first event, which
    /// on a cold workspace is the 30s heartbeat, and the chaos harness compresses the read
    /// timeout to 5s — tying the two made every connect time out under 5s latency (qfg-d1o9).
    /// </summary>
    internal static readonly TimeSpan DefaultHeaderTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Default initial reconnect delay (1s) before exponential backoff kicks in.</summary>
    public static readonly TimeSpan DefaultInitialBackoff = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Default upper bound for reconnect backoff (30s, jitter included), the same cap as sdk-go,
    /// sdk-node and supervisor contract Test 2 (qfg-n3nr).
    /// </summary>
    public static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromSeconds(30);

    private const string SsePath = "/api/v2/sse/config";

    private readonly IReadOnlyList<Uri> _streamUrls;
    private readonly string _authHeader;
    private readonly string _sdkVersionHeader;
    private readonly Action<ConfigEnvelope> _onEnvelope;
    private readonly Action? _onConnect;
    private readonly Action? _onDisconnect;
    private readonly TimeSpan _readTimeout;
    private readonly TimeSpan _initialBackoff;
    private readonly TimeSpan _maxBackoff;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly Random _rng = new();
    private int _maxConnectedStreamIndex = -1;

    // Set when a stream ended because an event passed SizeCaps.MaxSseEventBytes (qfg-6vno.20.6).
    // The server resends the same oversized snapshot on every connect, so while this is set only
    // an ENVELOPE counts as a connection's first frame (a keepalive alone does not), the attempt
    // stays a failed one, and the fallback poller keeps config fresh over HTTP. Cleared by the
    // first envelope that gets through. Same gate as sdk-go's `oversized`.
    private int _oversized;

    /// <summary>Ordered list of base URLs (primary first).</summary>
    public IReadOnlyList<Uri> StreamUrls => _streamUrls;

    /// <summary>
    /// Highest <see cref="StreamUrls"/> index a 200-OK stream has EVER been established with;
    /// <c>-1</c> until the first connect. Because the stream is pinned to <c>StreamUrls[0]</c>
    /// (SSE never fails over — the f05 invariant), this latches at <c>0</c>; a value &gt; 0 would
    /// mean the stream repointed to a failover leg. Derived from the real connection machinery
    /// (recorded on each 200-OK edge), not assumed, so the chaos suite's f05 probe catches a
    /// regression that reintroduces stream-leg walking.
    /// </summary>
    internal int MaxConnectedStreamIndex => Volatile.Read(ref _maxConnectedStreamIndex);

    /// <summary>
    /// Layer 1 read watchdog. <see cref="TimeSpan.Zero"/> disables the watchdog
    /// (escape hatch for offline / proxy debugging).
    /// </summary>
    public TimeSpan ReadTimeout => _readTimeout;

    /// <summary>
    /// Bound on the wait for response headers on each connect attempt. A peer that accepts the
    /// TCP connection but never answers would otherwise wedge streaming forever, since the
    /// <see cref="ReadTimeout"/> watchdog only arms once headers arrive. Defaults to
    /// <see cref="DefaultHeaderTimeout"/>; settable for tests only. <see cref="TimeSpan.Zero"/>
    /// disables it.
    /// </summary>
    internal TimeSpan HeaderTimeout { get; set; } = DefaultHeaderTimeout;

    /// <summary>
    /// Test seam (qfg-goi1.10.28): when set, each reconnect sleep passes through it as
    /// <c>("sse", requested)</c> and the loop sleeps the returned duration instead. Never a
    /// customer option.
    /// </summary>
    internal Func<string, TimeSpan, TimeSpan>? WaitHook { get; set; }

    /// <summary>
    /// Internal, test-only Layer 1 counters (connect attempts, worker restarts) for the chaos
    /// harness (qfg-6vno.4). Null outside a <see cref="Quonfig"/>-owned stream.
    /// </summary>
    internal SseCounters? Counters { get; set; }

    /// <summary>
    /// Initializes a new SSE client. Lifecycle is driven by <see cref="RunAsync"/>; the
    /// caller passes the cancellation token used to stop the loop.
    /// </summary>
    /// <param name="streamUrls">Ordered list of base URLs (primary first). Must be non-empty.
    /// Only <c>streamUrls[0]</c> is ever streamed from — SSE is pinned to the primary and never
    /// fails over (see <see cref="RunAsync"/>); the rest of the list is accepted for
    /// backward compatibility and diagnostics.</param>
    /// <param name="sdkKey">SDK key used as the password in HTTP Basic auth (<c>username=1</c>).</param>
    /// <param name="onEnvelope">Delegate invoked with each decoded <see cref="ConfigEnvelope"/>.
    /// Atomic-swap into the resolver is the caller's responsibility.</param>
    /// <param name="onConnect">Optional delegate invoked once per connection at its FIRST VALID
    /// FRAME (a keepalive comment block or a config envelope), not at the 200 response headers
    /// (qfg-goi1.10.41). Paired one-to-one with <paramref name="onDisconnect"/>.
    /// Callers wire this into the Quonfig instance to flip the <see cref="Supervisor.FallbackPoller"/>
    /// back into "SSE connected" and update <see cref="ConnectionState"/> to
    /// <see cref="ConnectionState.Connected"/>.</param>
    /// <param name="onDisconnect">Optional delegate invoked exactly when a previously-fired
    /// <paramref name="onConnect"/> tears down (stream EOF, watchdog dispose, IO error). Never
    /// fires without a preceding <paramref name="onConnect"/>. Lets the surrounding Quonfig
    /// flip <see cref="Supervisor.FallbackPoller"/> back to "SSE disconnected" so the
    /// threshold timer arms and Layer 2 can engage if the outage outlives it.</param>
    /// <param name="readTimeout">Layer 1 watchdog timeout. Defaults to <see cref="DefaultReadTimeout"/>.
    /// Pass <see cref="TimeSpan.Zero"/> to disable.</param>
    /// <param name="initialBackoff">Initial reconnect delay. Defaults to <see cref="DefaultInitialBackoff"/>.</param>
    /// <param name="maxBackoff">Reconnect backoff cap. Defaults to <see cref="DefaultMaxBackoff"/>.</param>
    /// <param name="messageHandler">Optional <see cref="HttpMessageHandler"/> for DI/tests.
    /// Caller retains ownership when injected.</param>
    /// <param name="logger">Optional logger. Defaults to <see cref="NullLogger.Instance"/>.</param>
    public SseClient(
        IEnumerable<Uri> streamUrls,
        string sdkKey,
        Action<ConfigEnvelope> onEnvelope,
        Action? onConnect = null,
        Action? onDisconnect = null,
        TimeSpan? readTimeout = null,
        TimeSpan? initialBackoff = null,
        TimeSpan? maxBackoff = null,
        HttpMessageHandler? messageHandler = null,
        ILogger? logger = null)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(streamUrls);
        ArgumentNullException.ThrowIfNull(sdkKey);
        ArgumentNullException.ThrowIfNull(onEnvelope);
#else
        if (streamUrls is null) throw new ArgumentNullException(nameof(streamUrls));
        if (sdkKey is null) throw new ArgumentNullException(nameof(sdkKey));
        if (onEnvelope is null) throw new ArgumentNullException(nameof(onEnvelope));
#endif

        var list = new List<Uri>();
        foreach (var u in streamUrls)
        {
            if (u is null) throw new ArgumentException("streamUrls must not contain null entries", nameof(streamUrls));
            list.Add(u);
        }
        if (list.Count == 0)
        {
            throw new ArgumentException("streamUrls must contain at least one URL", nameof(streamUrls));
        }
        _streamUrls = list;
        _onEnvelope = onEnvelope;
        _onConnect = onConnect;
        _onDisconnect = onDisconnect;
        _readTimeout = readTimeout ?? DefaultReadTimeout;
        _initialBackoff = initialBackoff ?? DefaultInitialBackoff;
        _maxBackoff = maxBackoff ?? DefaultMaxBackoff;
        _logger = logger ?? NullLogger.Instance;

        string creds = "1:" + sdkKey;
        _authHeader = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(creds));
        _sdkVersionHeader = "dotnet/" + SdkInfo.Version;

        _httpClient = BuildClient(messageHandler);
        _ownsClient = true;
        // SSE streams are long-lived; rely on the read watchdog, not HttpClient.Timeout,
        // for stall detection. Timeout.InfiniteTimeSpan suppresses the per-request budget.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "HttpClient takes ownership of the handler via disposeHandler:true and disposes it on Dispose().")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA5399:HttpClient is created without enabling CheckCertificateRevocationList",
        Justification = "Cross-SDK behavior intentionally relies on platform default revocation. Mirrors HttpTransport.")]
    private static HttpClient BuildClient(HttpMessageHandler? injected)
    {
        if (injected is not null)
        {
            return new HttpClient(injected, disposeHandler: false);
        }
#if NET8_0_OR_GREATER
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
#else
        var handler = new HttpClientHandler();
#endif
        return new HttpClient(handler, disposeHandler: true);
    }

    /// <summary>
    /// Runs the connect/parse/reconnect loop until <paramref name="cancellationToken"/> fires.
    /// Idempotent in the sense that the task itself is single-use — start one per client
    /// instance. Returns when the token is cancelled; throws nothing on cancellation.
    ///
    /// <para>The stream is PINNED to <c>StreamUrls[0]</c> and never walks the rest of the list —
    /// SSE does not fail over (the f05 invariant, sdk-go semantics). Failover is an HTTP-poll-only
    /// property: repointing the long-lived stream at a secondary would silently bind config
    /// freshness to the mirror and park SSE herds on infrastructure sized for poll traffic. A
    /// dead primary is retried forever with exponential backoff (reset only after a connection
    /// that delivered its first valid frame, qfg-goi1.10.41); during the outage the
    /// <see cref="Supervisor.FallbackPoller"/> covers freshness via the hedged HTTP path, which
    /// does keep both legs.</para>
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var delay = _initialBackoff;
        while (!cancellationToken.IsCancellationRequested)
        {
            // consumed == "this connection delivered a valid frame". A 200 that ends before its
            // first frame (headers only, a block cut off by EOF, garbage) is a failed attempt and
            // grows the backoff exactly like a connect error (qfg-goi1.10.41).
            bool consumed = await ConnectOnceAsync(0, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return;
            // The worker run ended (stream drop, or an attempt that failed before its first frame)
            // and the loop is about to restart it.
            Counters?.RecordWorkerRestart();
            if (consumed)
            {
                // The connection delivered a frame — reset the long-tail backoff before
                // reconnecting to the same (primary) stream endpoint.
                delay = _initialBackoff;
            }

            // Jittered sleep then exponential backoff if the connect delivered no frame.
            var sleep = ReconnectSleep(consumed, delay, _initialBackoff, _rng);
            var hook = WaitHook;
            if (hook is not null) sleep = hook("sse", sleep);
            try
            {
                await Task.Delay(sleep, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (!consumed)
            {
                delay = NextBackoff(delay, _maxBackoff, _rng);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA1849:Call async methods when in an async method",
        Justification = "body.Dispose in the finally is fine — body is fully drained or watchdog-disposed by this point.")]
    private async Task<bool> ConnectOnceAsync(int urlIndex, CancellationToken cancellationToken)
    {
        Counters?.RecordConnectAttempt();
        var target = AppendPath(_streamUrls[urlIndex], SsePath);
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.TryAddWithoutValidation("Authorization", _authHeader);
        request.Headers.TryAddWithoutValidation("X-Quonfig-SDK-Version", _sdkVersionHeader);
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // Bound the header wait with its own fixed timeout (NOT the read watchdog, which only
        // arms once headers arrive). Disarmed as soon as headers land so it never touches the body.
        var headerTimeout = HeaderTimeout;
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (headerTimeout > TimeSpan.Zero)
        {
            headerCts.CancelAfter(headerTimeout);
        }

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerCts.Token)
                .ConfigureAwait(false);
            headerCts.CancelAfter(Timeout.InfiniteTimeSpan);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            return false;
        }
        catch (OperationCanceledException ex) when (headerCts.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "SSE: no response headers from {Target} within {HeaderTimeout}; reconnecting", target, headerTimeout);
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "SSE: transport error contacting {Target}: {Message}", target, ex.Message);
            return false;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "SSE: connect timeout contacting {Target}", target);
            return false;
        }

        try
        {
            int sc = (int)response.StatusCode;
            if (sc != 200)
            {
                _logger.LogWarning("SSE: non-200 status {Status} from {Target}", sc, target);
                return false;
            }

#if NET8_0_OR_GREATER
            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
            var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
            // Record which stream leg this 200-OK connection was established with BEFORE any
            // connect edge can fire, so observers (the f05 chaos probe via Quonfig) read a
            // consistent value from inside the onConnect callback.
            if (urlIndex > Volatile.Read(ref _maxConnectedStreamIndex))
            {
                Volatile.Write(ref _maxConnectedStreamIndex, urlIndex);
            }

            // The 200 headers alone are NOT a connection (qfg-goi1.10.41): onConnect fires at the
            // connection's FIRST VALID FRAME, from inside the parser and before that frame's
            // envelope is handed over. The surrounding Quonfig uses it to flip the FallbackPoller
            // back to "connected". onDisconnect in the finally fires only if onConnect did, so a
            // connection that ends before its first frame reports no edge at all and leaves the
            // poller's engage timer running.
            bool firedConnect = false;
            bool gated = Volatile.Read(ref _oversized) != 0;
            try
            {
                var result = await ParseStreamCoreAsync(
                        body,
                        _onEnvelope,
                        _readTimeout,
                        _logger,
                        () =>
                        {
                            // Under the oversized gate this fires only for an envelope: the stream
                            // is usable again and the oversized episode is over.
                            if (gated) Volatile.Write(ref _oversized, 0);
                            firedConnect = true;
                            FireSafe(_onConnect, "onConnect");
                        },
                        gated,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result.Oversized)
                {
                    // An event over the cap can never be read, and the server resends it on every
                    // reconnect: a failed attempt (exponential backoff), not a frame. One WARN per
                    // episode; repeats at DEBUG.
                    var shown = HttpTransport.Redact(target);
                    if (Interlocked.Exchange(ref _oversized, 1) == 0)
                    {
                        _logger.LogWarning(
                            "SSE: event from {Target} exceeds the maximum size of {MaxBytes} bytes; dropping it and backing off (fallback polling will take over)",
                            shown, SizeCaps.MaxSseEventBytes);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "SSE: event from {Target} still exceeds the maximum size of {MaxBytes} bytes; backing off",
                            shown, SizeCaps.MaxSseEventBytes);
                    }
                    return false;
                }
                if (!result.FrameSeen && !cancellationToken.IsCancellationRequested)
                {
                    Counters?.RecordOkWithoutFrame();
                }
                return result.FrameSeen;
            }
            finally
            {
                body.Dispose();
                if (firedConnect)
                {
                    FireSafe(_onDisconnect, "onDisconnect");
                }
            }
        }
        finally
        {
            response.Dispose();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "A misbehaving connect/disconnect handler must not tear down the reconnect loop.")]
    private void FireSafe(Action? handler, string name)
    {
        if (handler is null) return;
        try
        {
            handler();
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.LogWarning(ex, "SSE: {Name} handler threw: {Message}", name, ex.Message);
        }
    }

    /// <summary>
    /// Reads SSE frames from <paramref name="body"/>, invoking <paramref name="onEnvelope"/>
    /// for each complete event. Implements the minimal subset of the SSE spec consumed by
    /// api-delivery: <c>data:</c> field (multi-line joined with <c>\n</c>), comments (lines
    /// starting with <c>:</c>) ignored, blank line as event boundary; <c>id:</c>, <c>event:</c>,
    /// <c>retry:</c> recognized but unused. A per-connection
    /// <see cref="CancellationTokenSource"/> armed via
    /// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> disposes <paramref name="body"/>
    /// if no bytes arrive within <paramref name="readTimeout"/>; this unblocks
    /// the in-flight read and the method returns. Returns true iff the
    /// stream delivered at least one VALID FRAME (qfg-goi1.10.41): a blank-line-terminated block
    /// that is comment-only (the server keepalive) or whose <c>data:</c> decodes and validates as
    /// a config envelope. Headers alone, a block cut off by EOF, malformed or non-envelope data,
    /// and an <c>id:</c>-only or empty block are not frames. <see cref="RunAsync"/> resets its
    /// backoff only when this is true. An event over 4 MiB (one line, or its joined <c>data:</c>
    /// lines) is not a frame: reading stops there and the method returns (qfg-6vno.20.6).
    /// </summary>
    public static Task<bool> ParseStreamAsync(
        Stream body,
        Action<ConfigEnvelope> onEnvelope,
        TimeSpan readTimeout,
        CancellationToken cancellationToken)
        => ParseStreamAsync(body, onEnvelope, readTimeout, NullLogger.Instance, cancellationToken);

    /// <summary>
    /// Logger-aware overload. See the no-logger variant for behavior.
    /// </summary>
    public static Task<bool> ParseStreamAsync(
        Stream body,
        Action<ConfigEnvelope> onEnvelope,
        TimeSpan readTimeout,
        ILogger logger,
        CancellationToken cancellationToken)
        => ParseStreamAsync(body, onEnvelope, readTimeout, logger, onFirstFrame: null, cancellationToken);

    /// <summary>
    /// Frame-aware overload: <paramref name="onFirstFrame"/> runs once, at the first valid frame,
    /// before that frame's envelope (if any) reaches <paramref name="onEnvelope"/>.
    /// </summary>
    internal static async Task<bool> ParseStreamAsync(
        Stream body,
        Action<ConfigEnvelope> onEnvelope,
        TimeSpan readTimeout,
        ILogger logger,
        Action? onFirstFrame,
        CancellationToken cancellationToken)
        => (await ParseStreamCoreAsync(body, onEnvelope, readTimeout, logger, onFirstFrame, requireEnvelope: false, cancellationToken)
            .ConfigureAwait(false)).FrameSeen;

    /// <summary>What one connection's parse saw.</summary>
    internal readonly struct ParseResult
    {
        public ParseResult(bool frameSeen, bool oversized)
        {
            FrameSeen = frameSeen;
            Oversized = oversized;
        }

        /// <summary>The stream delivered at least one valid frame.</summary>
        public bool FrameSeen { get; }

        /// <summary>The stream ended because an event passed <see cref="SizeCaps.MaxSseEventBytes"/>.</summary>
        public bool Oversized { get; }
    }

    /// <summary>
    /// The parser. <paramref name="requireEnvelope"/> is the oversized gate: when true, a
    /// comment-only block (keepalive) is not a frame; only a config envelope is. An event over
    /// <see cref="SizeCaps.MaxSseEventBytes"/> (one line, or the joined <c>data:</c> lines) ends
    /// the parse at once with <see cref="ParseResult.Oversized"/> set; nothing from it is delivered
    /// and memory stays bounded near the cap even for a line with no terminator.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "watchdog CTS lifetime is bound to the using statement.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Read loop must survive any single-event parse/handler failure.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA1849:Call async methods when in an async method",
        Justification = "Stream.Dispose in the watchdog callback is intentional — the callback runs on a timer thread, not the awaiter, and the dispose is what unblocks the in-flight read.")]
    internal static async Task<ParseResult> ParseStreamCoreAsync(
        Stream body,
        Action<ConfigEnvelope> onEnvelope,
        TimeSpan readTimeout,
        ILogger logger,
        Action? onFirstFrame,
        bool requireEnvelope,
        CancellationToken cancellationToken)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(onEnvelope);
        ArgumentNullException.ThrowIfNull(logger);
#else
        if (body is null) throw new ArgumentNullException(nameof(body));
        if (onEnvelope is null) throw new ArgumentNullException(nameof(onEnvelope));
        if (logger is null) throw new ArgumentNullException(nameof(logger));
#endif

        bool watchdogEnabled = readTimeout > TimeSpan.Zero;
        using var watchdog = new CancellationTokenSource();
        CancellationTokenRegistration disposeOnStall = default;
        var stallFired = false;

        void DisposeBody(string reason)
        {
            try
            {
                body.Dispose();
            }
            catch (Exception ex)
            {
                // Best-effort: the read loop is already on its way out, but
                // give operators a signal that the dispose itself failed.
                try
                {
                    logger.LogWarning(ex,
                        "SSE: {Reason} body.Dispose() threw — {Message}", reason, ex.Message);
                }
                catch
                {
                    // Logger itself failed; nothing left to do.
                }
            }
        }

        if (watchdogEnabled)
        {
            disposeOnStall = watchdog.Token.Register(() =>
            {
                stallFired = true;
                logger.LogWarning(
                    "SSE: read watchdog fired after {TimeoutMs}ms of silence — disposing body to force reconnect",
                    readTimeout.TotalMilliseconds);
                DisposeBody("watchdog");
            });
            watchdog.CancelAfter(readTimeout);
        }

        // qfg-6vno.13: cancelling the caller's token must unblock an in-flight read. On
        // netstandard2.0 ReadLineAsync takes no token and a .NET Framework network read ignores
        // cancellation, so without this a connected read stayed blocked until the watchdog, and
        // CloseAsync / the auth-failed entry waited their full 5 s. Disposing the body is what
        // unblocks the read, as the watchdog does. Registered on every target: it is a no-op cost
        // where the read already honors the token.
        using var disposeOnCancel = cancellationToken.Register(() => DisposeBody("cancellation"));

        // The reader never owns the body: the caller's finally is the single owner of disposal,
        // and the watchdog / cancellation dispose it via the registered callbacks. It caps one
        // line at SizeCaps.MaxSseEventBytes and stops reading there (qfg-6vno.20.6).
        var reader = new BoundedLineReader(body, SizeCaps.MaxSseEventBytes);
        var dataBuf = new StringBuilder();
        // UTF-8 bytes of the current event's joined data (newline separators included), capped
        // at SizeCaps.MaxSseEventBytes like a single line.
        long dataBytes = 0;
        bool frameSeen = false;
        // Per-block flags, reset at every blank line: a block is a keepalive frame only if it
        // holds at least one comment line and no field line.
        bool sawComment = false;
        bool sawField = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return new ParseResult(frameSeen, oversized: false);
                }
                catch (ObjectDisposedException)
                {
                    // Watchdog disposed the body mid-read; or the outer caller closed it.
                    return new ParseResult(frameSeen, oversized: false);
                }
                catch (IOException)
                {
                    // Socket reset, EOF mid-read, dispose-induced. Treat as normal stream end.
                    return new ParseResult(frameSeen, oversized: false);
                }

                if (line is null)
                {
                    // EOF — server closed the stream — or a line passed the cap.
                    return new ParseResult(frameSeen, reader.Oversized);
                }

                if (watchdogEnabled && !stallFired)
                {
                    // Re-arm the watchdog on every successful read.
                    try { watchdog.CancelAfter(readTimeout); }
                    catch (ObjectDisposedException) { /* loop is exiting */ }
                }

                if (line.Length == 0)
                {
                    var envelope = Decode(dataBuf, logger);
                    dataBytes = 0;
                    bool isFrame = envelope is not null || (!requireEnvelope && sawComment && !sawField);
                    sawComment = false;
                    sawField = false;
                    if (isFrame && !frameSeen)
                    {
                        frameSeen = true;
                        onFirstFrame?.Invoke();
                    }
                    if (envelope is not null)
                    {
                        Deliver(envelope, onEnvelope, logger);
                    }
                    continue;
                }
                if (line[0] == ':')
                {
                    sawComment = true;
                    continue;
                }
                sawField = true;
                string? rest = StripFieldPrefix(line, "data:");
                if (rest is not null)
                {
                    // The field prefix is ASCII, so its char count is its byte count.
                    dataBytes += (dataBuf.Length > 0 ? 1 : 0) + reader.LastLineBytes - (line.Length - rest.Length);
                    if (dataBytes > SizeCaps.MaxSseEventBytes)
                    {
                        return new ParseResult(frameSeen, oversized: true);
                    }
                    if (dataBuf.Length > 0) dataBuf.Append('\n');
                    dataBuf.Append(rest);
                }
                // id:/event:/retry: are recognized but otherwise ignored.
            }
        }
        finally
        {
            disposeOnStall.Dispose();
        }
        return new ParseResult(frameSeen, oversized: false);
    }

    /// <summary>
    /// The sleep before the next reconnect: after a stream that delivered data, the initial
    /// backoff with +/-20% jitter (qfg-goi1.2.15, sdk-go jitters every reconnect, so a fleet
    /// recycled by an api-delivery deploy does not reconnect in lockstep); after a failed connect,
    /// the current (already jittered) backoff.
    /// </summary>
    internal static TimeSpan ReconnectSleep(bool consumed, TimeSpan delay, TimeSpan initialBackoff, Random rng) =>
        consumed ? Jitter(initialBackoff, rng) : delay;

    // +/-20%, uniform over [0.8x, 1.2x).
    private static TimeSpan Jitter(TimeSpan d, Random rng) =>
        TimeSpan.FromMilliseconds(d.TotalMilliseconds * (0.8 + (rng.NextDouble() * 0.4)));

    /// <summary>
    /// Computes the next reconnect delay: doubles <paramref name="current"/>, caps at
    /// <paramref name="max"/>, applies ±20% jitter, then caps at <paramref name="max"/> again so
    /// the jitter never pushes a sleep past the cap (qfg-n3nr). Public so tests can pin the math.
    /// </summary>
    public static TimeSpan NextBackoff(TimeSpan current, TimeSpan max, Random rng)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(rng);
#else
        if (rng is null) throw new ArgumentNullException(nameof(rng));
#endif
        double doubled = current.TotalMilliseconds * 2.0;
        double capped = Math.Min(doubled, max.TotalMilliseconds);
        double jittered = Jitter(TimeSpan.FromMilliseconds(capped), rng).TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Min(jittered, max.TotalMilliseconds));
    }

    // Decodes the buffered data block. Returns null (and clears the buffer) when the block is
    // empty, malformed, or valid JSON that is not a config envelope — none of which is a frame.
    private static ConfigEnvelope? Decode(StringBuilder dataBuf, ILogger logger)
    {
        if (dataBuf.Length == 0) return null;
        string payload = dataBuf.ToString();
        dataBuf.Clear();
        ConfigEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ConfigEnvelope>(payload);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "SSE: discarding malformed envelope: {Message}", ex.Message);
            return null;
        }
        if (envelope is null) return null;
        if (!envelope.IsWellFormed())
        {
            // Valid JSON but not a config envelope (no meta.version) — drop it exactly like
            // malformed JSON; installing it would wipe every key (qfg-9dxb.3).
            logger.LogWarning("SSE: discarding event that is not a config envelope (missing meta.version)");
            return null;
        }
        return envelope;
    }

    private static void Deliver(ConfigEnvelope envelope, Action<ConfigEnvelope> onEnvelope, ILogger logger)
    {
        try
        {
            onEnvelope(envelope);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            logger.LogWarning(ex, "SSE: onEnvelope handler threw: {Message}", ex.Message);
        }
    }

    private static string? StripFieldPrefix(string line, string prefix)
    {
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        string rest = line.Substring(prefix.Length);
        if (rest.Length > 0 && rest[0] == ' ') rest = rest.Substring(1);
        return rest;
    }

    private static Uri AppendPath(Uri baseUrl, string path)
    {
        string baseStr = baseUrl.GetLeftPart(UriPartial.Authority);
        string basePath = baseUrl.AbsolutePath;
        if (basePath == "/") basePath = string.Empty;
        if (basePath.Length > 0 && basePath[basePath.Length - 1] == '/')
        {
            basePath = basePath.Substring(0, basePath.Length - 1);
        }
        return new Uri(baseStr + basePath + path, UriKind.Absolute);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
