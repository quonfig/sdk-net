using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Quonfig.Sdk;

/// <summary>
/// Resolves the effective api / SSE-stream / telemetry endpoints for a <see cref="Quonfig"/> client
/// from a <see cref="QuonfigOptions"/>, mirroring the cross-SDK <c>QUONFIG_DOMAIN</c> convention
/// (sdk-go <c>options.go</c> + <c>stream_url.go</c>; cli <c>util/domain-urls.ts</c>).
///
/// <para>Resolution precedence, per field:
/// <list type="number">
///   <item><description>an explicit option (the property was assigned) always wins;</description></item>
///   <item><description>else the <c>QUONFIG_DOMAIN</c> env var derives <c>primary.&lt;domain&gt;</c> /
///     <c>secondary.&lt;domain&gt;</c> (api) and <c>telemetry.&lt;domain&gt;</c> (telemetry);</description></item>
///   <item><description>else the built-in production defaults.</description></item>
/// </list>
/// The SSE stream URLs always <b>follow</b> the resolved api URLs (unless explicitly overridden):
/// each stream URL is derived from the matching api URL by prepending <c>stream.</c> to its host.
/// This is what makes a customer who overrides only <see cref="QuonfigOptions.ApiUrls"/> (e.g. to
/// staging) stream from the matching host rather than silently from the production stream cluster.</para>
/// </summary>
internal static class EndpointResolver
{
    /// <summary>
    /// Returns the SSE/streaming URL for an api base URL by prepending <c>stream.</c> to the host and
    /// leaving scheme, port, user-info, path, query, and fragment untouched. A normalized root path
    /// (<c>"/"</c>) is dropped so <c>https://primary.quonfig.com</c> maps to
    /// <c>https://stream.primary.quonfig.com</c> (no trailing slash), matching the built-in default
    /// stream URLs exactly. Mirrors sdk-go's <c>deriveStreamURL</c>. Returns the input unchanged when
    /// it does not parse as an absolute URL with a host (never throws).
    /// </summary>
    internal static string DeriveStreamUrl(string apiUrl)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            return apiUrl;
        }
        string trimmed = apiUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return trimmed;
        }

        var sb = new StringBuilder();
        sb.Append(uri.Scheme).Append("://");
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            sb.Append(uri.UserInfo).Append('@');
        }
        sb.Append("stream.").Append(uri.Host);
        if (!uri.IsDefaultPort)
        {
            sb.Append(':').Append(uri.Port.ToString(CultureInfo.InvariantCulture));
        }
        // Preserve a real path/query but drop the normalized "/" root so the default (no-path) URL
        // round-trips without a spurious trailing slash (which would double up when the SSE path is
        // appended).
        string pathAndQuery = uri.PathAndQuery;
        if (!string.Equals(pathAndQuery, "/", StringComparison.Ordinal))
        {
            sb.Append(pathAndQuery);
        }
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            sb.Append(uri.Fragment);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resolves the effective (ApiUrls, StreamUrls, TelemetryUrl) triple for <paramref name="opts"/>,
    /// applying the <c>QUONFIG_DOMAIN</c> override (via <paramref name="envLookup"/>) and deriving the
    /// stream URLs from the resolved api URLs when they were not set explicitly.
    /// </summary>
    internal static (IReadOnlyList<string> ApiUrls, IReadOnlyList<string> StreamUrls, string TelemetryUrl) Resolve(
        QuonfigOptions opts, Func<string, string?> envLookup)
    {
        string? domain = envLookup("QUONFIG_DOMAIN");
        bool hasDomain = !string.IsNullOrEmpty(domain);

        IReadOnlyList<string> apiUrls;
        if (opts.ApiUrlsExplicit)
        {
            apiUrls = opts.ApiUrls;
        }
        else if (hasDomain)
        {
            apiUrls = new[] { "https://primary." + domain, "https://secondary." + domain };
        }
        else
        {
            apiUrls = opts.ApiUrls; // built-in production default
        }

        IReadOnlyList<string> streamUrls;
        if (opts.StreamUrlsExplicit)
        {
            streamUrls = opts.StreamUrls;
        }
        else
        {
            var derived = new string[apiUrls.Count];
            for (int i = 0; i < apiUrls.Count; i++)
            {
                derived[i] = DeriveStreamUrl(apiUrls[i]);
            }
            streamUrls = derived;
        }

        string telemetryUrl;
        if (opts.TelemetryUrlExplicit)
        {
            telemetryUrl = opts.TelemetryUrl;
        }
        else if (hasDomain)
        {
            telemetryUrl = "https://telemetry." + domain;
        }
        else
        {
            telemetryUrl = opts.TelemetryUrl; // built-in production default
        }

        return (apiUrls, streamUrls, telemetryUrl);
    }
}
