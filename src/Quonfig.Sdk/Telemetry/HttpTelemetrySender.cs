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
/// The SDK version header is <c>X-Quonfig-SDK-Version: dotnet/{ver}</c>. <see cref="SendAsync"/>
/// throws <see cref="HttpRequestException"/> on non-2xx responses or transport errors. The SDK's own
/// <see cref="TelemetryReporter"/> uses the byte-level path instead, which POSTs the retained bytes
/// verbatim and reads the status and <c>Retry-After</c> (qfg-y8je.10).</para>
///
/// <para>Timeouts (P1): <see cref="HttpClient.Timeout"/> is the overall per-request deadline (15s by
/// default). On net8.0, when the sender builds its own handler, <c>SocketsHttpHandler.ConnectTimeout</c>
/// bounds TCP connect + TLS (5s by default); on netstandard2.0, or with an injected handler, the
/// overall deadline covers connect as well.</para>
/// </summary>
public sealed class HttpTelemetrySender : ITelemetrySender, ITelemetryTransport, IDisposable
{
    /// <summary>Default per-request timeout (15s since 1.3.0; was 30s).</summary>
    public static readonly TimeSpan DefaultTimeout = TelemetryDefaults.Timeout;

    private const int BodySnippetBytes = 1024;

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
        : this(telemetryUrl, sdkKey, timeout, TelemetryDefaults.ConnectTimeout, messageHandler) { }

    /// <summary>
    /// Initializes a sender with an explicit connect/TLS timeout (applied on net8.0 when the sender
    /// builds its own handler; an injected <paramref name="messageHandler"/> is never modified).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "HttpClient takes ownership of the handler via disposeHandler:true and disposes it on Dispose().")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA5399:HttpClient is created without enabling CheckCertificateRevocationList",
        Justification = "Cross-SDK behavior intentionally relies on platform default revocation; mirrors HttpTransport.")]
    internal HttpTelemetrySender(
        Uri telemetryUrl,
        string sdkKey,
        TimeSpan timeout,
        TimeSpan connectTimeout,
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
                ConnectTimeout = connectTimeout > TimeSpan.Zero ? connectTimeout : TelemetryDefaults.ConnectTimeout,
            };
#else
            // No connect-timeout knob on HttpClientHandler: the overall deadline bounds connect too.
            _ = connectTimeout;
            var handler = new HttpClientHandler();
#endif
            _httpClient = new HttpClient(handler, disposeHandler: true);
        }
        _ownsClient = true;
        _httpClient.Timeout = timeout;
    }

    /// <summary>The resolved <c>POST /api/v1/telemetry/</c> endpoint.</summary>
    internal Uri Endpoint => _endpoint;

    string ITelemetryTransport.Url => _endpoint.ToString();

    /// <inheritdoc/>
    public async Task SendAsync(IDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(payload);
#else
        if (payload is null) throw new ArgumentNullException(nameof(payload));
#endif
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var result = await SendBytesAsync(body, cancellationToken).ConfigureAwait(false);
        if (result.Status < 200 || result.Status >= 300)
        {
            throw new HttpRequestException(
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "telemetry POST returned HTTP {0}", result.Status));
        }
    }

    Task<TelemetryHttpResult> ITelemetryTransport.SendAsync(TelemetryBatch batch, CancellationToken cancellationToken) =>
        SendBytesAsync(batch.Body, cancellationToken);

    /// <summary>
    /// POSTs <paramref name="body"/> exactly as given and returns the status, the raw <c>Retry-After</c>
    /// header and the first 1KB of the response body. Throws only when no response arrived.
    /// </summary>
    internal async Task<TelemetryHttpResult> SendBytesAsync(byte[] body, CancellationToken cancellationToken)
    {
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

        string? retryAfter = response.Headers.TryGetValues("Retry-After", out var values)
            ? string.Join(",", values)
            : null;
        string snippet = await ReadSnippetAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return new TelemetryHttpResult((int)response.StatusCode, retryAfter, snippet);
    }

    private static async Task<string> ReadSnippetAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null) return string.Empty;
#if NET8_0_OR_GREATER
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
        var buf = new byte[BodySnippetBytes];
        int read = 0;
        while (read < buf.Length)
        {
#if NET8_0_OR_GREATER
            int n = await stream.ReadAsync(buf.AsMemory(read, buf.Length - read), cancellationToken).ConfigureAwait(false);
#else
            int n = await stream.ReadAsync(buf, read, buf.Length - read, cancellationToken).ConfigureAwait(false);
#endif
            if (n == 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buf, 0, read);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}
