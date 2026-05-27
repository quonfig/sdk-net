using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Quonfig.Sdk.Exceptions;
using Quonfig.Sdk.Wire;

namespace Quonfig.Sdk.Datadir;

/// <summary>
/// Reads a Quonfig workspace directory tree into a <see cref="ConfigEnvelope"/> — the same wire format
/// <c>api-delivery</c> returns over HTTP. This is the no-network code path used by integration tests
/// and consumers who bootstrap from a static distribution.
///
/// <para>Mirrors <c>sdk-java DatadirLoader</c>, <c>sdk-go workspace_loader.go</c>, and
/// <c>sdk-node datadir.ts</c>. Subdirs walked: <c>configs</c>, <c>feature-flags</c>, <c>segments</c>,
/// <c>log-levels</c>. <c>schemas/</c> is intentionally excluded — those files are raw JSON Schema
/// documents, not config envelopes, and loading them produces empty-key ghost rows in the store
/// (cross-SDK fix, qfg-b5yi).</para>
/// </summary>
public static class DatadirLoader
{
    private static readonly string[] Subdirs = { "configs", "feature-flags", "segments", "log-levels" };

    // CA1848 — compiled message delegate so per-file warnings allocate no string array.
    // Microsoft.Extensions.Logging.LogLevel clashes with the SDK's own log-level domain enum
    // (Quonfig.Sdk.LogLevel), so we fully qualify the BCL one here.
    private static readonly Action<ILogger, string, string, Exception?> SkipFileWarning =
        LoggerMessage.Define<string, string>(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            new EventId(1, nameof(DatadirLoader)),
            "Quonfig datadir: skipping {Path} ({Reason})");

    /// <summary>
    /// Loads a workspace at <paramref name="datadir"/> and returns an envelope tagged with
    /// <paramref name="environment"/>. Validates <paramref name="environment"/> against
    /// <c>{datadir}/quonfig.json</c> when present.
    /// </summary>
    public static ConfigEnvelope Load(string datadir, string environment)
        => Load(datadir, environment, NullLogger.Instance);

    /// <summary>Overload that accepts an <see cref="ILogger"/> for per-file warning routing.</summary>
    public static ConfigEnvelope Load(string datadir, string environment, ILogger logger)
    {
        if (string.IsNullOrEmpty(environment))
        {
            throw new InvalidOperationException(
                "environment required for datadir mode; pass environment or set QUONFIG_ENVIRONMENT");
        }

        var manifest = Path.Combine(datadir, "quonfig.json");
        var available = ReadEnvironmentNames(manifest);
        if (available.Count > 0 && !available.Contains(environment))
        {
            throw new InvalidOperationException(
                $"environment \"{environment}\" not found in workspace; available environments: " +
                    string.Join(", ", available));
        }

        var configs = new List<JsonElement>();
        foreach (var sub in Subdirs)
        {
            var subPath = Path.Combine(datadir, sub);
            if (!Directory.Exists(subPath)) continue;

            var files = Directory
                .EnumerateFiles(subPath, "*.json", SearchOption.AllDirectories)
                .Where(p =>
                {
                    var name = Path.GetFileName(p);
                    return name.Length > 0 && name[0] != '.';
                })
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal);

            foreach (var f in files)
            {
                try
                {
                    var bytes = File.ReadAllBytes(f);
                    var doc = JsonDocument.Parse(bytes);
                    configs.Add(doc.RootElement.Clone());
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
                {
                    SkipFileWarning(logger, f, ex.Message, ex);
                }
            }
        }

        if (configs.Count == 0)
        {
            throw new QuonfigException(
                $"Quonfig datadir at \"{datadir}\" loaded empty: no readable configs in any of " +
                    string.Join(", ", Subdirs) + ". Check the path and that the workspace tree exists.");
        }

        var workspaceId = new DirectoryInfo(datadir).Name;
        var meta = new Meta($"datadir:{datadir}", environment, workspaceId);
        return new ConfigEnvelope(configs, meta);
    }

    private static IReadOnlyList<string> ReadEnvironmentNames(string manifestPath)
    {
        if (!File.Exists(manifestPath)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            if (!doc.RootElement.TryGetProperty("environments", out var envs)) return Array.Empty<string>();
            if (envs.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            var result = new List<string>(envs.GetArrayLength());
            foreach (var e in envs.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) continue;
                var s = e.GetString();
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s!.Trim());
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            // Manifest unreadable — fall through and skip the environment check. The directory walk
            // will surface the real problem if there are no configs.
            return Array.Empty<string>();
        }
    }
}
