using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Quonfig.Sdk;

/// <summary>
/// Dev-only loader for the <c>quonfig-user.email</c> evaluation context. Mirrors
/// sdk-node's <c>devContext.ts</c>: it reads the per-domain tokens file written by
/// <c>qfg login</c> (<c>~/.quonfig/tokens.json</c> for production, or
/// <c>~/.quonfig/tokens-&lt;domain-with-dashes&gt;.json</c> for non-prod domains) and returns
/// a <see cref="ContextSet"/> carrying <c>{ "quonfig-user": { "email": userEmail } }</c>.
///
/// <para>The attribute is dev-only by construction: production servers never run
/// <c>qfg login</c> and therefore have no tokens file, so rules keyed on
/// <c>quonfig-user.email</c> are dead code in prod. That is why injection can default ON —
/// the loader simply no-ops when there is no file.</para>
/// </summary>
internal static class DevContext
{
    internal const string ContextName = "quonfig-user";

    /// <summary>
    /// Picks the per-domain tokens filename, mirroring cli/src/util/token-storage.ts and
    /// sdk-node's <c>tokenFilenameForApiUrls</c>. The CLI writes to <c>tokens.json</c> when
    /// QUONFIG_DOMAIN=quonfig.com (the default); any other domain (e.g. quonfig-staging.com)
    /// is suffixed (<c>tokens-quonfig-staging-com.json</c>). The SDK derives the domain from the
    /// first configured apiUrl by stripping a leading "app." or "primary." subdomain. An empty
    /// list, an unparseable URL, or a host that resolves to quonfig.com falls back to plain
    /// <c>tokens.json</c>.
    /// </summary>
    internal static string TokenFilenameForApiUrls(IReadOnlyList<string>? apiUrls)
    {
        var domain = DeriveDomainFromApiUrls(apiUrls);
        if (string.IsNullOrEmpty(domain) || domain == "quonfig.com")
        {
            return "tokens.json";
        }
        return "tokens-" + domain!.Replace('.', '-') + ".json";
    }

    private static string DeriveDomainFromApiUrls(IReadOnlyList<string>? apiUrls)
    {
        if (apiUrls is null || apiUrls.Count == 0 || string.IsNullOrEmpty(apiUrls[0]))
        {
            return "";
        }
        string host;
        try
        {
            host = new Uri(apiUrls[0], UriKind.Absolute).Host;
        }
        catch (UriFormatException)
        {
            return "";
        }
        if (string.IsNullOrEmpty(host)) return "";
        foreach (var prefix in new[] { "app.", "primary." })
        {
            if (host.StartsWith(prefix, StringComparison.Ordinal))
            {
                return host.Substring(prefix.Length);
            }
        }
        return host;
    }

    /// <summary>
    /// Reads the per-domain tokens file and returns a <see cref="ContextSet"/> with
    /// <c>{ "quonfig-user": { "email": userEmail } }</c> when a non-empty <c>userEmail</c> is
    /// present. Returns <c>null</c> when the file is missing or has no <c>userEmail</c>; logs a
    /// single warning and returns <c>null</c> on a parse error. The config home directory is
    /// <c>QUONFIG_CONFIG_HOME</c> when set (mirrors sdk-python; used by tests), else the user's
    /// profile directory.
    /// </summary>
    internal static ContextSet? Load(
        IReadOnlyList<string>? apiUrls,
        Func<string, string?>? envLookup,
        ILogger logger)
    {
        var home = ResolveConfigHome(envLookup);
        if (string.IsNullOrEmpty(home)) return null;
        var path = Path.Combine(home!, ".quonfig", TokenFilenameForApiUrls(apiUrls));

        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return null;
        }

        string? email;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            email = doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("userEmail", out var v)
                && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                "quonfig: dev-context could not parse {Path} ({Message}); skipping injection",
                path, ex.Message);
            return null;
        }

        if (string.IsNullOrEmpty(email)) return null;

        var set = new ContextSet();
        var props = new ContextProperties { ["email"] = new ContextValueString(email!) };
        set[ContextName] = props;
        return set;
    }

    private static string? ResolveConfigHome(Func<string, string?>? envLookup)
    {
        var lookup = envLookup ?? Environment.GetEnvironmentVariable;
        var configHome = lookup("QUONFIG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(configHome)) return configHome;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
