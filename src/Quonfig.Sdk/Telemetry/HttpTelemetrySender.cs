using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Quonfig.Sdk.Telemetry;

/// <summary>
/// Posts telemetry envelopes to <c>POST /api/v1/telemetry/</c> on api-telemetry.
///
/// <para>Auth is HTTP Basic with <c>1:&lt;sdkKey&gt;</c> (matching <see cref="Transport.HttpTransport"/>).
/// The SDK version header is <c>X-Quonfig-SDK-Version: dotnet/{ver}</c>. On non-2xx responses or
/// transport errors, throws <see cref="HttpRequestException"/> so the reporter applies its
/// backoff policy.</para>
/// </summary>
public sealed class HttpTelemetrySender : ITelemetrySender, IDisposable
{
    /// <summary>Default per-request timeout (30s).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private const string TelemetryPath = "/api/v1/telemetry/";

    private readonly Uri _endpoint;
    private readonly string _authHeader;
    private readonly string _sdkVersionHeader;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    /// <summary>Initializes a sender that owns its <see cref="HttpClient"/>.</summary>
    public HttpTelemetrySender(Uri telemetryUrl, string sdkKey)
        : this(telemetryUrl, sdkKey, DefaultTimeout, messageHandler: null) { }

    /// <summary>
    /// Initializes a sender. Pass <paramref name="messageHandler"/> to inject a handler for tests
    /// or DI; ownership of the injected handler stays with the caller.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "HttpClient takes ownership of the handler via disposeHandler:true and disposes it on Dispose().")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA5399:HttpClient is created without enabling CheckCertificateRevocationList",
        Justification = "Cross-SDK behavior intentionally relies on platform default revocation; mirrors HttpTransport.")]
    public HttpTelemetrySender(
        Uri telemetryUrl,
        string sdkKey,
        TimeSpan timeout,
        HttpMessageHandler? messageHandler)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(telemetryUrl);
        ArgumentNullException.ThrowIfNull(sdkKey);
#else
        if (telemetryUrl is null) throw new ArgumentNullException(nameof(telemetryUrl));
        if (sdkKey is null) throw new ArgumentNullException(nameof(sdkKey));
#endif
        string baseStr = telemetryUrl.GetLeftPart(UriPartial.Authority);
        string basePath = telemetryUrl.AbsolutePath;
        if (basePath == "/") basePath = string.Empty;
        if (basePath.Length > 0 && basePath[basePath.Length - 1] == '/')
        {
            basePath = basePath.Substring(0, basePath.Length - 1);
        }
        _endpoint = new Uri(baseStr + basePath + TelemetryPath);

        string creds = "1:" + sdkKey;
        _authHeader = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(creds));
        _sdkVersionHeader = "dotnet/" + SdkInfo.Version;

        if (messageHandler is not null)
        {
            _httpClient = new HttpClient(messageHandler, disposeHandler: false);
        }
        else
        {
#if NET8_0_OR_GREATER
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
#else
            var handler = new HttpClientHandler();
#endif
            _httpClient = new HttpClient(handler, disposeHandler: true);
        }
        _ownsClient = true;
        _httpClient.Timeout = timeout;
    }

    /// <inheritdoc/>
    public async Task SendAsync(IDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(payload);
#else
        if (payload is null) throw new ArgumentNullException(nameof(payload));
#endif
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        request.Headers.TryAddWithoutValidation("Authorization", _authHeader);
        request.Headers.TryAddWithoutValidation("X-Quonfig-SDK-Version", _sdkVersionHeader);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        int sc = (int)response.StatusCode;
        if (sc < 200 || sc >= 300)
        {
            throw new HttpRequestException($"telemetry POST returned HTTP {sc}");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}
