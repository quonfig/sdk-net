using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Quonfig.Sdk.Exceptions;
using Quonfig.Sdk.Wire;

namespace Quonfig.Sdk.Transport;

/// <summary>
/// HTTP transport for the <c>api-delivery</c> config endpoint. Issues a single
/// <c>GET /api/v2/configs</c> request per <see cref="FetchAsync"/> call, walking
/// <see cref="ApiUrls"/> in order on transport error or non-2xx/304 response.
///
/// <para>Mirrors <c>sdk-java HttpTransport</c> and <c>sdk-python Transport.fetch</c>: HTTP Basic
/// auth (<c>username=1, password=sdkKey</c>), ETag round-trip with 304-as-no-change semantics,
/// 10-second default timeout. The SDK version header is <c>X-Quonfig-SDK-Version: dotnet/{ver}</c>.</para>
///
/// <para>One <see cref="HttpClient"/> is shared across all calls. On net8.0 the underlying handler
/// is <c>SocketsHttpHandler</c> with a 5-minute <c>PooledConnectionLifetime</c> so long-running
/// hosts refresh sockets and pick up DNS changes; on netstandard2.0 the SDK falls back to the
/// stock <see cref="HttpClientHandler"/>.</para>
///
/// <para>Callers tracking caching state can read <see cref="LastETag"/> after each successful
/// call; on 304 the property is left at the value the server returned previously, matching
/// the cross-SDK behavior.</para>
/// </summary>
public sealed class HttpTransport : IDisposable
{
    /// <summary>Default per-request timeout (10s) used when no explicit value is given.</summary>
    public static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Default per-URL config-fetch deadline (~3s) applied to a single failover leg on the
    /// SEQUENTIAL <see cref="FetchAsync"/> path when no explicit value is supplied. Short enough that
    /// a hung primary fails over to the secondary well inside a default 10s <c>InitTimeout</c>, yet
    /// long enough to tolerate a slow-but-healthy upstream. Per-attempt only — it never touches the
    /// long-lived SSE stream. The hedged <see cref="FetchHedgedAsync"/> path uses
    /// <see cref="DefaultConfigFetchHedgeDelay"/> / <see cref="DefaultConfigFetchHedgeAbort"/> instead.
    /// </summary>
    public static readonly TimeSpan DefaultConfigFetchTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Default hedge delay (~2s): how long <see cref="FetchHedgedAsync"/> waits for the primary leg
    /// before ALSO firing the secondary leg in parallel. Below a realistic slow-but-alive primary's
    /// worst case yet far enough below the per-leg abort that a healthy sub-second primary is never
    /// hedged (the secondary stays a cold standby, adding zero load on a healthy system).
    /// </summary>
    public static readonly TimeSpan DefaultConfigFetchHedgeDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Default per-leg hard-abort deadline (~6s) on the hedged path. MUST exceed the longest healable
    /// primary latency so a late-but-newer primary heals forward (rather than aborting), and MUST be
    /// less than <c>InitTimeout</c> so the init-path heal leg is not clipped.
    /// </summary>
    public static readonly TimeSpan DefaultConfigFetchHedgeAbort = TimeSpan.FromSeconds(6);

    private const string ConfigsPath = "/api/v2/configs";

    private readonly IReadOnlyList<Uri> _apiUrls;
    private readonly string _authHeader;
    private readonly string _sdkVersionHeader;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly TimeSpan _configFetchTimeout;
    private readonly TimeSpan _hedgeDelay;
    private readonly TimeSpan _hedgeAbort;

    /// <summary>
    /// PER-LEG ETag slots: <c>_etags[i]</c> is the last ETag seen from <see cref="ApiUrls"/>[i]. The
    /// hedge runs both legs concurrently, so a single shared ETag would let a 304 from one leg mask
    /// the other (and would be a data race with two legs in flight). <c>_etagLock</c> guards
    /// reads/writes; each leg snapshots its slot before the request and writes the response ETag back
    /// after — the network wait happens with no lock held. Mirrors sdk-go's <c>etags []string</c>
    /// under <c>etagMu</c>.
    /// </summary>
    private readonly object _etagLock = new();
    private readonly string?[] _etags;

    /// <summary>Ordered list of base URLs (primary first, then secondaries).</summary>
    public IReadOnlyList<Uri> ApiUrls => _apiUrls;

    /// <summary>
    /// ETag stored from the most recent successful HTTP 200 response on the SEQUENTIAL
    /// <see cref="FetchAsync"/> path. <c>null</c> until the first 200; left unchanged on 304 so
    /// sequential callers don't lose their cache key. The hedged path uses the per-leg
    /// <c>_etags</c> slots instead, so a concurrent secondary leg never clobbers the primary's tag.
    /// </summary>
    public string? LastETag { get; private set; }

    /// <summary>
    /// Zero-based index into <see cref="ApiUrls"/> of the leg that served the most recent
    /// successful fetch (HTTP 200 or 304) on the SEQUENTIAL <see cref="FetchAsync"/> path. <c>-1</c>
    /// until the first success. Lets callers report which endpoint resolved — <c>0</c> is the
    /// primary, <c>1</c> the secondary — so a failover is observable. Updated only on success; an
    /// all-URLs-fail throw leaves it unchanged. The hedged path reports each leg's index on its own
    /// <see cref="LegResult"/> instead (so resolvedFrom is set atomically with the install).
    /// </summary>
    public int LastResolvedIndex { get; private set; } = -1;

    /// <summary>
    /// Initializes a new transport. The default constructor wires its own <see cref="HttpClient"/>;
    /// pass <paramref name="messageHandler"/> to inject one (DI / tests). Ownership of the injected
    /// handler stays with the caller — only the internally-created client is disposed.
    /// </summary>
    /// <param name="apiUrls">Ordered list of base URLs (primary first). Must be non-empty.</param>
    /// <param name="sdkKey">SDK key used as the password in HTTP Basic auth (<c>username=1</c>).</param>
    /// <param name="timeout">Overall per-request ceiling on the shared <see cref="HttpClient"/>.
    /// Defaults to <see cref="DefaultHttpTimeout"/>. Bounds the whole failover walk; the tighter
    /// <paramref name="configFetchTimeout"/> bounds each individual leg.</param>
    /// <param name="messageHandler">Optional handler for tests / DI. When provided, the caller
    /// retains ownership and is responsible for disposing it.</param>
    /// <param name="configFetchTimeout">Per-URL deadline for a single failover leg on the SEQUENTIAL
    /// <see cref="FetchAsync"/> path. A hung primary aborts after this duration so the secondary is
    /// tried within the remaining budget. Defaults to <see cref="DefaultConfigFetchTimeout"/> (~3s)
    /// when null or non-positive.</param>
    /// <param name="hedgeDelay">How long <see cref="FetchHedgedAsync"/> waits for the primary leg
    /// before also firing the secondary in parallel. Defaults to
    /// <see cref="DefaultConfigFetchHedgeDelay"/> (~2s) when null or non-positive.</param>
    /// <param name="hedgeAbort">Per-leg hard-abort deadline on the hedged path. Defaults to
    /// <see cref="DefaultConfigFetchHedgeAbort"/> (~6s) when null or non-positive.</param>
    public HttpTransport(
        IEnumerable<Uri> apiUrls,
        string sdkKey,
        TimeSpan? timeout = null,
        HttpMessageHandler? messageHandler = null,
        TimeSpan? configFetchTimeout = null,
        TimeSpan? hedgeDelay = null,
        TimeSpan? hedgeAbort = null)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(apiUrls);
        ArgumentNullException.ThrowIfNull(sdkKey);
#else
        if (apiUrls is null) throw new ArgumentNullException(nameof(apiUrls));
        if (sdkKey is null) throw new ArgumentNullException(nameof(sdkKey));
#endif

        var list = new List<Uri>();
        foreach (var u in apiUrls)
        {
            if (u is null) throw new ArgumentException("apiUrls must not contain null entries", nameof(apiUrls));
            list.Add(u);
        }
        if (list.Count == 0)
        {
            throw new ArgumentException("apiUrls must contain at least one URL", nameof(apiUrls));
        }
        _apiUrls = list;
        _etags = new string?[list.Count];

        string creds = "1:" + sdkKey;
        _authHeader = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(creds));
        _sdkVersionHeader = "dotnet/" + SdkInfo.Version;

        _httpClient = BuildClient(messageHandler);
        _ownsClient = true;
        _httpClient.Timeout = timeout ?? DefaultHttpTimeout;
        _configFetchTimeout = configFetchTimeout is { } t && t > TimeSpan.Zero
            ? t
            : DefaultConfigFetchTimeout;
        _hedgeDelay = hedgeDelay is { } hd && hd > TimeSpan.Zero
            ? hd
            : DefaultConfigFetchHedgeDelay;
        _hedgeAbort = hedgeAbort is { } ha && ha > TimeSpan.Zero
            ? ha
            : DefaultConfigFetchHedgeAbort;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "HttpClient takes ownership of the handler via disposeHandler:true and disposes it on Dispose().")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA5399:HttpClient is created without enabling CheckCertificateRevocationList",
        Justification = "Cross-SDK behavior intentionally relies on platform default revocation. Java/Python/Go peers do not enable CRL either; toggling it here would diverge.")]
    private static HttpClient BuildClient(HttpMessageHandler? injected)
    {
        if (injected is not null)
        {
            // disposeHandler:false — caller retains ownership of the injected handler.
            return new HttpClient(injected, disposeHandler: false);
        }
#if NET8_0_OR_GREATER
        var handler = new SocketsHttpHandler
        {
            // Refresh pooled connections so long-running hosts pick up DNS / cert changes.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
#else
        var handler = new HttpClientHandler();
#endif
        return new HttpClient(handler, disposeHandler: true);
    }

    /// <summary>
    /// Fetches the current envelope from <c>GET /api/v2/configs</c>. Sends <c>If-None-Match</c>
    /// when <paramref name="etag"/> is non-null; returns <c>null</c> on HTTP 304. On transport
    /// error or non-2xx/304 from the current URL, advances to the next entry in <see cref="ApiUrls"/>;
    /// throws <see cref="QuonfigException"/> only when every URL has been tried unsuccessfully.
    /// </summary>
    public async Task<ConfigEnvelope?> FetchAsync(string? etag, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (int i = 0; i < _apiUrls.Count; i++)
        {
            var baseUrl = _apiUrls[i];
            var target = BuildTarget(baseUrl);
            using var request = BuildRequest(target, etag);
            HttpResponseMessage? response = null;
            // Bound this single leg so a hung primary (accepts the connection but never responds)
            // aborts after _configFetchTimeout instead of consuming the caller's whole budget,
            // leaving time to reach the secondary. The linked CTS fires the EARLIER of (caller
            // cancellation, per-URL deadline); a per-URL timeout cancels only this attempt, then
            // the loop advances to the next URL. (qfg-7h5d.1.11)
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(_configFetchTimeout);
            var attemptToken = attemptCts.Token;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptToken).ConfigureAwait(false);

                int sc = (int)response.StatusCode;
                if (sc == 304)
                {
                    LastResolvedIndex = i;
                    return null;
                }
                if (sc >= 200 && sc < 300)
                {
                    string? responseETag = response.Headers.ETag?.Tag;
                    if (responseETag is not null)
                    {
                        LastETag = responseETag;
                    }
#if NET8_0_OR_GREATER
                    using var bodyStream = await response.Content.ReadAsStreamAsync(attemptToken).ConfigureAwait(false);
#else
                    using var bodyStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
                    var envelope = await JsonSerializer
                        .DeserializeAsync<ConfigEnvelope>(bodyStream, cancellationToken: attemptToken)
                        .ConfigureAwait(false);
                    if (envelope is null)
                    {
                        throw new QuonfigException($"api-delivery at {target} returned an empty/null envelope body");
                    }
                    LastResolvedIndex = i;
                    return envelope;
                }
                lastError = new QuonfigException(FormattableString.Invariant($"HTTP {sc} from {target}"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The CALLER cancelled (or its overall deadline elapsed) — propagate, don't fail over.
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // Either the per-URL deadline fired (attemptToken, caller still live) or HttpClient's
                // own Timeout elapsed. Both surface as OperationCanceledException / TaskCanceledException
                // on net8 and NS2.0. Treat as a per-leg timeout and advance to the next URL.
                lastError = new QuonfigException($"HTTP timeout contacting {target}", ex);
            }
            catch (HttpRequestException ex)
            {
                lastError = new QuonfigException($"transport error contacting {target}: {ex.Message}", ex);
            }
            catch (JsonException ex)
            {
                // Body parse failure — treat as a transport-layer fault so the next URL is tried.
                lastError = new QuonfigException($"failed to parse envelope from {target}: {ex.Message}", ex);
            }
            finally
            {
                response?.Dispose();
            }
        }

        throw new QuonfigException(
            $"all {_apiUrls.Count} api-delivery URL(s) failed; last error: {lastError?.Message}",
            lastError);
    }

    /// <summary>
    /// Fires the PRIMARY leg (index 0) and, if it has not settled within the hedge delay OR errors
    /// fast, ALSO fires the SECONDARY leg (index 1) in parallel — without cancelling the primary.
    /// Each leg runs under its own per-leg hard-abort deadline and its own ETag slot. Every FIRED leg
    /// emits exactly one <see cref="LegResult"/> to the returned reader in arrival order; the reader
    /// completes once every fired leg has settled, so the number drained equals the number fired.
    ///
    /// <para>A fast healthy primary means the secondary is NEVER contacted (cold standby, zero extra
    /// load). The caller installs each successful result through the reject-older guard so
    /// watermark-max (higher generation wins; a late older payload does not regress; a late newer
    /// payload heals forward) falls out without any source ranking. There is NO coalescing/in-flight
    /// gate: overlapping cycles are safe (per-leg ETag isolation + the serialized install guard +
    /// per-leg abort), and a manual refresh must always actually fetch. Mirrors sdk-go's
    /// <c>FetchConfigsHedged</c>.</para>
    /// </summary>
    internal ChannelReader<LegResult> FetchHedgedAsync(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<LegResult>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        _ = RunHedgeAsync(channel.Writer, cancellationToken);
        return channel.Reader;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "This runs detached (fire-and-forget). Any unexpected orchestration fault must be surfaced on the channel so the caller's drain loop sees one error instead of hanging forever; rethrowing would fault an unobserved Task.")]
    private async Task RunHedgeAsync(ChannelWriter<LegResult> writer, CancellationToken cancellationToken)
    {
        try
        {
            bool hasSecondary = _apiUrls.Count > 1;
            // CAS guard so the secondary fires AT MOST ONCE and NEVER after a fast primary win.
            // 0 = not yet decided, 1 = fired-or-suppressed. Mirrors sdk-go's secondaryFired atomic.
            int secondaryDecided = 0;
            var legTasks = new List<Task>();

            void FireSecondary()
            {
                if (!hasSecondary) return;
                if (Interlocked.CompareExchange(ref secondaryDecided, 1, 0) == 0)
                {
                    legTasks.Add(Task.Run(async () =>
                    {
                        var r = await FetchLegAsync(1, cancellationToken).ConfigureAwait(false);
                        await writer.WriteAsync(r, cancellationToken).ConfigureAwait(false);
                    }, cancellationToken));
                }
            }

            // Fire the primary. Its result is forwarded to the caller AND captured locally so the
            // arbiter below can inspect it to decide the hedge.
            var primaryTcs = new TaskCompletionSource<LegResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            legTasks.Add(Task.Run(async () =>
            {
                var r = await FetchLegAsync(0, cancellationToken).ConfigureAwait(false);
                primaryTcs.TrySetResult(r);
                await writer.WriteAsync(r, cancellationToken).ConfigureAwait(false);
            }, cancellationToken));

            // Wait for EITHER the primary to settle OR the hedge delay to elapse.
            using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var delayTask = Task.Delay(_hedgeDelay, delayCts.Token);
                var settled = await Task.WhenAny(primaryTcs.Task, delayTask).ConfigureAwait(false);
                if (settled == primaryTcs.Task)
                {
                    // Primary settled before the hedge delay. Stop the timer.
                    delayCts.Cancel();
                    var lr = primaryTcs.Task.Result;
                    if (lr.Error is not null)
                    {
                        FireSecondary(); // fast error -> hedge now
                    }
                    else
                    {
                        // fast success/304 -> never hedge (suppress the secondary permanently).
                        Interlocked.CompareExchange(ref secondaryDecided, 1, 0);
                    }
                }
                else
                {
                    // Hedge delay elapsed. Re-check the primary so one that JUST won the boundary race
                    // does not trigger an unnecessary hedge.
                    if (primaryTcs.Task.IsCompleted)
                    {
                        var lr = primaryTcs.Task.Result;
                        if (lr.Error is not null) FireSecondary();
                        else Interlocked.CompareExchange(ref secondaryDecided, 1, 0);
                    }
                    else
                    {
                        FireSecondary(); // primary still in flight -> hedge in parallel
                    }
                }
            }

            await Task.WhenAll(legTasks).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            // Orchestration failed unexpectedly (e.g. caller cancellation). Surface it on the channel
            // so the caller's drain loop sees one error rather than hanging, then complete.
            writer.TryComplete(ex);
        }
    }

    /// <summary>
    /// Fetches <c>GET /api/v2/configs</c> from <see cref="ApiUrls"/>[<paramref name="legIndex"/>],
    /// using only THAT leg's ETag slot (snapshot under the lock before the request, write-back
    /// after), bounded by its own per-leg hard-abort deadline. Fully reads/decodes the body before
    /// returning. Never throws — every outcome (200, 304, error) is returned as a
    /// <see cref="LegResult"/> carrying <paramref name="legIndex"/>. Mirrors sdk-go's
    /// <c>fetchFromURLAt</c>.
    /// </summary>
    private async Task<LegResult> FetchLegAsync(int legIndex, CancellationToken cancellationToken)
    {
        if (legIndex < 0 || legIndex >= _apiUrls.Count)
        {
            return LegResult.Fail(legIndex, new QuonfigException($"leg index {legIndex} out of range"));
        }
        var baseUrl = _apiUrls[legIndex];
        var target = BuildTarget(baseUrl);

        string? etag;
        lock (_etagLock) { etag = _etags[legIndex]; }

        using var request = BuildRequest(target, etag);
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(_hedgeAbort);
        var attemptToken = attemptCts.Token;
        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptToken).ConfigureAwait(false);
            int sc = (int)response.StatusCode;
            if (sc == 304)
            {
                return LegResult.Unchanged(legIndex);
            }
            if (sc >= 200 && sc < 300)
            {
                string? responseETag = response.Headers.ETag?.Tag;
                if (responseETag is not null)
                {
                    lock (_etagLock) { _etags[legIndex] = responseETag; }
                }
#if NET8_0_OR_GREATER
                using var bodyStream = await response.Content.ReadAsStreamAsync(attemptToken).ConfigureAwait(false);
#else
                using var bodyStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
                var envelope = await JsonSerializer
                    .DeserializeAsync<ConfigEnvelope>(bodyStream, cancellationToken: attemptToken)
                    .ConfigureAwait(false);
                if (envelope is null)
                {
                    return LegResult.Fail(legIndex, new QuonfigException($"api-delivery at {target} returned an empty/null envelope body"));
                }
                return LegResult.Ok(legIndex, envelope);
            }
            return LegResult.Fail(legIndex, new QuonfigException(FormattableString.Invariant($"HTTP {sc} from {target}")));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER cancelled (or shut down) — surface as the leg's error; the orchestrator and
            // the install loop both treat it as a failed leg.
            return LegResult.Fail(legIndex, new QuonfigException($"hedge leg {legIndex} cancelled contacting {target}"));
        }
        catch (OperationCanceledException ex)
        {
            // The per-leg hard-abort fired (or HttpClient.Timeout). This leg failed — the other leg
            // (if fired) may still install.
            return LegResult.Fail(legIndex, new QuonfigException($"HTTP timeout contacting {target}", ex));
        }
        catch (HttpRequestException ex)
        {
            return LegResult.Fail(legIndex, new QuonfigException($"transport error contacting {target}: {ex.Message}", ex));
        }
        catch (JsonException ex)
        {
            return LegResult.Fail(legIndex, new QuonfigException($"failed to parse envelope from {target}: {ex.Message}", ex));
        }
        finally
        {
            response?.Dispose();
        }
    }

    private HttpRequestMessage BuildRequest(Uri target, string? etag)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, target);
        req.Headers.TryAddWithoutValidation("Authorization", _authHeader);
        req.Headers.TryAddWithoutValidation("X-Quonfig-SDK-Version", _sdkVersionHeader);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(etag))
        {
            // The wire-spec etag includes its own quotes (e.g. "abc"); pass through as-is.
            req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }
        return req;
    }

    private static Uri BuildTarget(Uri baseUrl)
    {
        string baseStr = baseUrl.GetLeftPart(UriPartial.Authority);
        string basePath = baseUrl.AbsolutePath;
        if (basePath == "/") basePath = string.Empty;
        if (basePath.Length > 0 && basePath[basePath.Length - 1] == '/')
        {
            basePath = basePath.Substring(0, basePath.Length - 1);
        }
        return new Uri(baseStr + basePath + ConfigsPath);
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
