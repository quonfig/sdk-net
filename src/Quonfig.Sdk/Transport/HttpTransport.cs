using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
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

    private const string ConfigsPath = "/api/v2/configs";

    private readonly IReadOnlyList<Uri> _apiUrls;
    private readonly string _authHeader;
    private readonly string _sdkVersionHeader;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    /// <summary>Ordered list of base URLs (primary first, then secondaries).</summary>
    public IReadOnlyList<Uri> ApiUrls => _apiUrls;

    /// <summary>
    /// ETag stored from the most recent successful HTTP 200 response. <c>null</c> until the
    /// first 200; left unchanged on 304 so callers don't lose their cache key.
    /// </summary>
    public string? LastETag { get; private set; }

    /// <summary>
    /// Initializes a new transport. The default constructor wires its own <see cref="HttpClient"/>;
    /// pass <paramref name="messageHandler"/> to inject one (DI / tests). Ownership of the injected
    /// handler stays with the caller — only the internally-created client is disposed.
    /// </summary>
    /// <param name="apiUrls">Ordered list of base URLs (primary first). Must be non-empty.</param>
    /// <param name="sdkKey">SDK key used as the password in HTTP Basic auth (<c>username=1</c>).</param>
    /// <param name="timeout">Per-request timeout. Defaults to <see cref="DefaultHttpTimeout"/>.</param>
    /// <param name="messageHandler">Optional handler for tests / DI. When provided, the caller
    /// retains ownership and is responsible for disposing it.</param>
    public HttpTransport(
        IEnumerable<Uri> apiUrls,
        string sdkKey,
        TimeSpan? timeout = null,
        HttpMessageHandler? messageHandler = null)
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

        string creds = "1:" + sdkKey;
        _authHeader = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(creds));
        _sdkVersionHeader = "dotnet/" + SdkInfo.Version;

        _httpClient = BuildClient(messageHandler);
        _ownsClient = true;
        _httpClient.Timeout = timeout ?? DefaultHttpTimeout;
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
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                int sc = (int)response.StatusCode;
                if (sc == 304)
                {
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
                    using var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
                    using var bodyStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
                    var envelope = await JsonSerializer
                        .DeserializeAsync<ConfigEnvelope>(bodyStream, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    if (envelope is null)
                    {
                        throw new QuonfigException($"api-delivery at {target} returned an empty/null envelope body");
                    }
                    return envelope;
                }
                lastError = new QuonfigException(FormattableString.Invariant($"HTTP {sc} from {target}"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // HttpClient surfaces both connect timeouts and Timeout-elapsed as
                // OperationCanceledException (or TaskCanceledException) on net8 and NS2.0.
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
