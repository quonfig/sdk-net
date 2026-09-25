using System;
using System.Reflection;

namespace Quonfig.Sdk;

/// <summary>SDK identity, sent as <c>X-Quonfig-SDK-Version: dotnet/{Version}</c> on every request.</summary>
public static class SdkInfo
{
    /// <summary>Marketing name of the package.</summary>
    public const string Name = "Quonfig.Sdk";

    /// <summary>
    /// The package version, read at runtime from the assembly's
    /// <see cref="AssemblyInformationalVersionAttribute"/> (fed by <c>&lt;Version&gt;</c> in
    /// <c>Directory.Build.props</c>) with any <c>+buildmetadata</c> suffix removed, so it can never
    /// drift from the published package. Falls back to <c>"unknown"</c> if the attribute is missing.
    /// Before 1.3.0 this was a hand-synced <c>const</c> that stayed at <c>"0.0.1"</c> (qfg-pkig).
    /// </summary>
    public static readonly string Version = ResolveVersion();

    private static string ResolveVersion()
    {
        string? info = typeof(SdkInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
        {
            info = typeof(SdkInfo).Assembly.GetName().Version?.ToString(3);
        }
        if (string.IsNullOrWhiteSpace(info)) return "unknown";
        return info!.Split('+')[0].Trim();
    }
}
