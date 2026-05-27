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
/// <see cref="Stream"/>; the in-flight <see cref="StreamReader.ReadLineAsync()"/> then unblocks
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

    /// <summary>Default initial reconnect delay (1s) before exponential backoff kicks in.</summary>
    public static readonly TimeSpan DefaultInitialBackoff = TimeSpan.FromSeconds(1);

    /// <summary>Default upper bound for reconnect backoff (60s).</summary>
    public static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromSeconds(60);

    private const string SsePath = "/api/v2/sse/config";

    private readonly IReadOnlyList<Uri> _streamUrls;
    private readonly string _authHeader;
    private readonly string _sdkVersionHeader;
    private readonly Action<ConfigEnvelope> _onEnvelope;
    private readonly TimeSpan _readTimeout;
    private readonly TimeSpan _initialBackoff;
    private readonly TimeSpan _maxBackoff;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly Random _rng = new();

    /// <summary>Ordered list of base URLs (primary first).</summary>
    public IReadOnlyList<Uri> StreamUrls => _streamUrls;

    /// <summary>
    /// Layer 1 read watchdog. <see cref="TimeSpan.Zero"/> disables the watchdog
    /// (escape hatch for offline / proxy debugging).
    /// </summary>
    public TimeSpan ReadTimeout => _readTimeout;

    /// <summary>
    /// Initializes a new SSE client. Lifecycle is driven by <see cref="RunAsync"/>; the
    /// caller passes the cancellation token used to stop the loop.
    /// </summary>
    /// <param name="streamUrls">Ordered list of base URLs (primary first). Must be non-empty.</param>
    /// <param name="sdkKey">SDK key used as the password in HTTP Basic auth (<c>username=1</c>).</param>
    /// <param name="onEnvelope">Delegate invoked with each decoded <see cref="ConfigEnvelope"/>.
    /// Atomic-swap into the resolver is the caller's responsibility.</param>
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
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var delay = _initialBackoff;
        while (!cancellationToken.IsCancellationRequested)
        {
            bool anyRead = false;
            for (int i = 0; i < _streamUrls.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) return;
                bool consumed = await ConnectOnceAsync(_streamUrls[i], cancellationToken).ConfigureAwait(false);
                if (consumed)
                {
                    anyRead = true;
                    // Successful read — reset the long-tail backoff and stop walking the
                    // failover list. We'll loop right back to the primary on reconnect.
                    delay = _initialBackoff;
                    break;
                }
            }

            if (cancellationToken.IsCancellationRequested) return;

            // Jittered sleep then exponential backoff if nothing connected this round.
            var sleep = anyRead ? _initialBackoff : delay;
            try
            {
                await Task.Delay(sleep, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (!anyRead)
            {
                delay = NextBackoff(delay, _maxBackoff, _rng);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA1849:Call async methods when in an async method",
        Justification = "body.Dispose in the finally is fine — body is fully drained or watchdog-disposed by this point.")]
    private async Task<bool> ConnectOnceAsync(Uri baseUrl, CancellationToken cancellationToken)
    {
        var target = AppendPath(baseUrl, SsePath);
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.TryAddWithoutValidation("Authorization", _authHeader);
        request.Headers.TryAddWithoutValidation("X-Quonfig-SDK-Version", _sdkVersionHeader);
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
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
            try
            {
                return await ParseStreamAsync(body, _onEnvelope, _readTimeout, _logger, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                body.Dispose();
            }
        }
        finally
        {
            response.Dispose();
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
    /// <see cref="StreamReader.ReadLineAsync()"/> and the method returns. Returns true iff at
    /// least one byte was consumed (used by <see cref="RunAsync"/> to distinguish
    /// "live stream that ended" from "couldn't connect").
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
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "watchdog CTS lifetime is bound to the using statement.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Read loop must survive any single-event parse/handler failure.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA1849:Call async methods when in an async method",
        Justification = "Stream.Dispose in the watchdog callback is intentional — the callback runs on a timer thread, not the awaiter, and the dispose is what unblocks the in-flight read.")]
    public static async Task<bool> ParseStreamAsync(
        Stream body,
        Action<ConfigEnvelope> onEnvelope,
        TimeSpan readTimeout,
        ILogger logger,
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
        if (watchdogEnabled)
        {
            disposeOnStall = watchdog.Token.Register(() =>
            {
                stallFired = true;
                logger.LogWarning(
                    "SSE: read watchdog fired after {TimeoutMs}ms of silence — disposing body to force reconnect",
                    readTimeout.TotalMilliseconds);
                try
                {
                    body.Dispose();
                }
                catch (Exception)
                {
                    // Best-effort: the read loop is already on its way out.
                }
            });
            watchdog.CancelAfter(readTimeout);
        }

        // Leave the underlying stream open so the finally block in the caller is the
        // single owner of disposal — the watchdog disposes via the registered callback,
        // not via the reader.
        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var dataBuf = new StringBuilder();
        bool anyRead = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
#if NET8_0_OR_GREATER
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
#else
                    line = await reader.ReadLineAsync().ConfigureAwait(false);
#endif
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return anyRead;
                }
                catch (ObjectDisposedException)
                {
                    // Watchdog disposed the body mid-read; or the outer caller closed it.
                    return anyRead;
                }
                catch (IOException)
                {
                    // Socket reset, EOF mid-read, dispose-induced. Treat as normal stream end.
                    return anyRead;
                }

                if (line is null)
                {
                    // EOF — server closed the stream.
                    return anyRead;
                }

                anyRead = true;
                if (watchdogEnabled && !stallFired)
                {
                    // Re-arm the watchdog on every successful read.
                    try { watchdog.CancelAfter(readTimeout); }
                    catch (ObjectDisposedException) { /* loop is exiting */ }
                }

                if (line.Length == 0)
                {
                    Flush(dataBuf, onEnvelope, logger);
                    continue;
                }
                if (line[0] == ':')
                {
                    continue;
                }
                string? rest = StripFieldPrefix(line, "data:");
                if (rest is not null)
                {
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
        return anyRead;
    }

    /// <summary>
    /// Computes the next reconnect delay: doubles <paramref name="current"/>, caps at
    /// <paramref name="max"/>, then applies ±20% jitter. Public so tests can pin the math.
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
        // Jitter ±20% — uniform over [0.8x, 1.2x).
        double jitter = 0.8 + (rng.NextDouble() * 0.4);
        return TimeSpan.FromMilliseconds(capped * jitter);
    }

    private static void Flush(StringBuilder dataBuf, Action<ConfigEnvelope> onEnvelope, ILogger logger)
    {
        if (dataBuf.Length == 0) return;
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
            return;
        }
        if (envelope is null) return;
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
