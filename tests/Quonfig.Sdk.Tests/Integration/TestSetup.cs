#pragma warning disable CA1303 // exception messages are diagnostic-only, not user-facing
#pragma warning disable CA1510 // ArgumentNullException.ThrowIfNull is unavailable on net48
#pragma warning disable CA1859 // object? return mirrors the YAML expected_data (list, map or null)
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Quonfig.Sdk.Datadir;
using Quonfig.Sdk.Eval;
using Quonfig.Sdk.Telemetry;

namespace Quonfig.Sdk.Tests.Integration;

/// <summary>
/// Harness for the auto-generated <c>*Tests.cs</c> files under
/// <c>Quonfig.Sdk.Tests.Integration</c> (generator: integration-test-data/generators/src/targets/dotnet.ts).
///
/// <para>Every generated case drives the PUBLIC <see cref="Quonfig"/> client (qfg-2agi.34). This class
/// only builds clients, scopes env-var overrides, captures what the real telemetry reporter sends, and
/// normalizes that payload to the YAML's expected shape. It contains no resolver, no parser and no
/// exception mapping: if the SDK does not throw, a raise case fails.</para>
/// </summary>
internal static class TestSetup
{
    public const string ENV_ID = "Production";

    private const string ENCRYPTION_KEY =
        "c87ba22d8662282abe8a0e4651327b579cb64a454ab0f4c170b45b15f049a221";

    private static readonly Dictionary<string, string> BaseEnv = new(StringComparer.Ordinal)
    {
        ["PREFAB_INTEGRATION_TEST_ENCRYPTION_KEY"] = ENCRYPTION_KEY,
        ["IS_A_NUMBER"] = "1234",
        ["NOT_A_NUMBER"] = "not_a_number",
    };

    // AsyncLocal (not ThreadLocal) so an override set at the top of an async test method stays in
    // force across its awaits and never leaks into a test running in parallel.
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> EnvOverrides = new();

    /// <summary>Path to the integration-test-data datadir tree.</summary>
    public static readonly string DATADIR = LocateDatadir();

    // ---------------------------------------------------------------------------
    // Clients.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Construct a public <see cref="Quonfig"/> client from <paramref name="options"/>. The harness adds
    /// only test plumbing: env lookups go through <see cref="Env"/>, the dev-only quonfig-user context
    /// and datadir file watching are off, and telemetry is off unless the case injected a sender.
    /// </summary>
    public static Quonfig NewClient(QuonfigOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        options.EnvLookup = LookupEnv;
        options.EnableQuonfigUserContext = false;
        options.DatadirAutoReload = false;
        if (options.TelemetrySender is null)
        {
            options.CollectEvaluationSummaries = false;
            options.ContextUploadMode = ContextUploadMode.None;
        }
        return new Quonfig(options);
    }

    // ---------------------------------------------------------------------------
    // Env-var overrides (YAML env_vars).
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Install env-var overrides (alternating name/value pairs) for the rest of the calling test;
    /// dispose to restore. Read through <see cref="QuonfigOptions.EnvLookup"/>.
    /// </summary>
    public static IDisposable Env(params string[] pairs)
    {
        if (pairs is null || pairs.Length % 2 != 0)
        {
            throw new ArgumentException("TestSetup.Env requires alternating name/value pairs");
        }
        var previous = EnvOverrides.Value;
        var merged = previous is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(previous.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
        for (int i = 0; i < pairs.Length; i += 2) merged[pairs[i]] = pairs[i + 1];
        EnvOverrides.Value = merged;
        return new EnvScope(previous);
    }

    private sealed class EnvScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string>? _previous;
        public EnvScope(IReadOnlyDictionary<string, string>? previous) => _previous = previous;
        public void Dispose() => EnvOverrides.Value = _previous;
    }

    private static string? LookupEnv(string name)
    {
        var overrides = EnvOverrides.Value;
        if (overrides is not null && overrides.TryGetValue(name, out var v)) return v;
        if (BaseEnv.TryGetValue(name, out var b)) return b;
        return Environment.GetEnvironmentVariable(name);
    }

    // ---------------------------------------------------------------------------
    // Literal helpers for JSON values and telemetry expectations.
    // ---------------------------------------------------------------------------

    /// <summary>List literal, mirroring the generator's <c>TestSetup.List(...)</c> emission.</summary>
    public static List<object?> List(params object?[] items)
    {
        var list = new List<object?>(items?.Length ?? 0);
        if (items is not null) list.AddRange(items);
        return list;
    }

    /// <summary>Ordered map literal from alternating key/value pairs (<c>TestSetup.Map(...)</c>).</summary>
    public static Dictionary<string, object?> Map(params object?[] pairs)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (pairs is null || pairs.Length == 0) return result;
        if (pairs.Length % 2 != 0)
        {
            throw new ArgumentException(
                $"TestSetup.Map requires alternating key/value pairs; got {pairs.Length} args");
        }
        for (int i = 0; i < pairs.Length; i += 2)
        {
            if (pairs[i] is not string key)
            {
                throw new ArgumentException("TestSetup.Map keys must be strings");
            }
            result[key] = pairs[i + 1];
        }
        return result;
    }

    /// <summary>Assert that <paramref name="actual"/> is within 1e-9 of <paramref name="expected"/>.</summary>
    public static void AssertDoubleEquals(double expected, double? actual)
    {
        if (actual is null || Math.Abs(actual.Value - expected) > 1e-9)
        {
            throw new Xunit.Sdk.XunitException(FormattableString.Invariant($"expected {expected} (±1e-9), got {actual?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "null"}"));
        }
    }

    // ---------------------------------------------------------------------------
    // datadir_value_type.yaml: the loaded envelope must carry a number, not a string.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Assert that the datadir loader produced numeric values for <paramref name="key"/>. The public
    /// typed getters convert a numeric string, so only the loaded envelope shows whether the loader
    /// coerced int/double at load time (matching api-delivery).
    /// </summary>
    public static void AssertLoadedValueNumeric(string environment, string key)
    {
        var envelope = DatadirLoader.Load(DATADIR, environment);
        var raw = envelope.Configs.FirstOrDefault(c =>
            c.ValueKind == JsonValueKind.Object
            && c.TryGetProperty("key", out var k)
            && k.ValueKind == JsonValueKind.String
            && k.GetString() == key);
        if (raw.ValueKind != JsonValueKind.Object)
        {
            throw new Xunit.Sdk.XunitException($"config \"{key}\" not in the loaded datadir envelope");
        }
        var row = ConfigRowParser.ParseRow(key, raw);
        var values = row.DefaultRules.Concat(row.Environments.SelectMany(e => e.Rules)).Select(r => r.Value).ToList();
        if (values.Count == 0)
        {
            throw new Xunit.Sdk.XunitException($"config \"{key}\" has no rule values");
        }
        foreach (var v in values)
        {
            if (v.Payload is not (long or int or double or float or decimal))
            {
                throw new Xunit.Sdk.XunitException(
                    $"datadir loader returned {v.Type} config \"{key}\" as " +
                    (v.Payload is null ? "null" : $"{v.Payload.GetType().Name} ({v.Payload})") +
                    "; expected a number coerced at load time, matching api-delivery");
            }
        }
    }

    // ---------------------------------------------------------------------------
    // post.yaml / telemetry.yaml: capture what the real reporter sends.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Captures the envelopes the client's real <see cref="TelemetryReporter"/> sends and records the
    /// value each public getter returned. Dispose the client to drain the reporter, then call
    /// <see cref="Sent"/>.
    /// </summary>
    internal sealed class TelemetryCapture
    {
        private static readonly Regex Redacted = new("^\\*{5}[0-9a-f]{5}$", RegexOptions.CultureInvariant);

        private readonly CapturingSender _sender = new();
        private readonly Dictionary<string, object?> _returned = new(StringComparer.Ordinal);

        public ITelemetrySender Sender => _sender;

        /// <summary>
        /// Evaluate <paramref name="key"/> through the public typed getter of the config's own type.
        /// The YAML names keys without their types, so try each typed Details getter until one does
        /// not report a type mismatch; a mismatch records no evaluation summary.
        /// </summary>
        public void Evaluate(IBoundQuonfig client, string key)
        {
            if (client is null) throw new ArgumentNullException(nameof(client));
            var attempts = new Func<(object? Value, ErrorCode? Code)>[]
            {
                () => Pick(client.GetBoolDetails(key)),
                () => Pick(client.GetLongDetails(key)),
                () => Pick(client.GetDoubleDetails(key)),
                () => Pick(client.GetStringListDetails(key)),
                () => Pick(client.GetJsonDetails(key)),
                () => Pick(client.GetDurationDetails(key)),
                () => Pick(client.GetStringDetails(key)),
            };
            foreach (var attempt in attempts)
            {
                var (value, code) = attempt();
                if (code == ErrorCode.TypeMismatch) continue;
                _returned[key] = value;
                return;
            }
            throw new Xunit.Sdk.XunitException($"no typed getter could read \"{key}\"");
        }

        private static (object? Value, ErrorCode? Code) Pick<T>(EvaluationDetails<T> d) => (d.Value, d.ErrorCode);

        /// <summary>
        /// The captured payload for <paramref name="kind"/> ("context_shape", "evaluation_summary",
        /// "example_contexts") in the YAML <c>expected_data</c> shape, or <c>null</c> when nothing of
        /// that kind was sent.
        /// </summary>
        public object? Sent(string kind)
        {
            var events = _sender.Envelopes
                .SelectMany(env => env.TryGetValue("events", out var e) && e is IEnumerable list
                    ? list.OfType<IDictionary<string, object?>>()
                    : Enumerable.Empty<IDictionary<string, object?>>())
                .ToList();
            return kind switch
            {
                "context_shape" => ContextShapes(events),
                "example_contexts" => ExampleContexts(events),
                "evaluation_summary" => EvaluationSummaries(events),
                _ => throw new ArgumentException("unknown telemetry kind: " + kind),
            };
        }

        private static object? ContextShapes(List<IDictionary<string, object?>> events)
        {
            var rows = new List<object?>();
            foreach (var ev in events)
            {
                if (!ev.TryGetValue("contextShapes", out var o) || o is not IDictionary<string, object?> env) continue;
                if (!env.TryGetValue("shapes", out var s) || s is not IEnumerable shapes) continue;
                foreach (var shape in shapes.OfType<IDictionary<string, object?>>())
                {
                    var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                    if (shape.TryGetValue("fieldTypes", out var ft) && ft is IDictionary<string, object?> ftm)
                    {
                        foreach (var e in ftm) fields[e.Key] = AsLong(e.Value);
                    }
                    rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["name"] = shape.TryGetValue("name", out var n) ? n : null,
                        ["field_types"] = fields,
                    });
                }
            }
            return rows.Count == 0 ? null : rows;
        }

        private static object? ExampleContexts(List<IDictionary<string, object?>> events)
        {
            foreach (var ev in events)
            {
                if (!ev.TryGetValue("exampleContexts", out var o) || o is not IDictionary<string, object?> env) continue;
                if (!env.TryGetValue("examples", out var ex) || ex is not IEnumerable examples) continue;
                var first = examples.OfType<IDictionary<string, object?>>().FirstOrDefault();
                if (first is null) continue;
                if (!first.TryGetValue("contextSet", out var csObj) || csObj is not IDictionary<string, object?> cs) continue;
                if (!cs.TryGetValue("contexts", out var cObj) || cObj is not IEnumerable contexts) continue;
                var outMap = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var c in contexts.OfType<IDictionary<string, object?>>())
                {
                    if (c.TryGetValue("type", out var name) && name is string n
                        && c.TryGetValue("values", out var values) && values is IDictionary<string, object?> vmap)
                    {
                        outMap[n] = new Dictionary<string, object?>(vmap, StringComparer.Ordinal);
                    }
                }
                if (outMap.Count > 0) return outMap;
            }
            return null;
        }

        private object? EvaluationSummaries(List<IDictionary<string, object?>> events)
        {
            var summaries = new List<IDictionary<string, object?>>();
            foreach (var ev in events)
            {
                if (!ev.TryGetValue("summaries", out var o) || o is not IDictionary<string, object?> env) continue;
                if (!env.TryGetValue("summaries", out var s) || s is not IEnumerable list) continue;
                summaries.AddRange(list.OfType<IDictionary<string, object?>>());
            }
            if (summaries.Count == 0) return null;

            // Stable order for comparison with the YAML: CONFIG before FEATURE_FLAG, then send order.
            var ordered = summaries
                .Select((s, i) => (s, i))
                .OrderBy(t => ConfigType(t.s), StringComparer.Ordinal)
                .ThenBy(t => t.i)
                .Select(t => t.s);

            var output = new List<object?>();
            foreach (var s in ordered)
            {
                string key = s.TryGetValue("key", out var k) ? k as string ?? "" : "";
                if (!s.TryGetValue("counters", out var co) || co is not IEnumerable counters) continue;
                foreach (var c in counters.OfType<IDictionary<string, object?>>())
                {
                    var selected = NormalizeSelected(c.TryGetValue("selectedValue", out var sv) ? sv : null);
                    object? value = Unwrap(selected);
                    string valueType = WireValueType(selected);
                    // A redacted counter carries only "*****<md5>"; the YAML's value / value_type are what
                    // the caller got back from the public getter.
                    if (value is string str && Redacted.IsMatch(str) && _returned.TryGetValue(key, out var returned))
                    {
                        value = returned;
                        valueType = WireValueTypeOf(returned);
                    }

                    var summary = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["config_row_index"] = AsLong(c.TryGetValue("configRowIndex", out var cri) ? cri : 0L),
                        ["conditional_value_index"] = AsLong(c.TryGetValue("conditionalValueIndex", out var cvi) ? cvi : 0L),
                    };
                    if (c.TryGetValue("weightedValueIndex", out var wvi) && AsLong(wvi) is long w && w >= 0)
                    {
                        summary["weighted_value_index"] = w;
                    }

                    var record = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["key"] = key,
                        ["type"] = ConfigType(s),
                        ["value"] = value,
                        ["value_type"] = valueType,
                        ["count"] = AsLong(c.TryGetValue("count", out var ct) ? ct : 0L),
                        ["reason"] = AsLong(c.TryGetValue("reason", out var rs) ? rs : 0L),
                    };
                    if (selected is not null) record["selected_value"] = selected;
                    record["summary"] = summary;
                    output.Add(record);
                }
            }
            return output.Count == 0 ? null : output;
        }

        private static string ConfigType(IDictionary<string, object?> summary) =>
            summary.TryGetValue("type", out var t) && t is not null
                ? t.ToString()!.ToUpperInvariant()
                : "";

        private static object? NormalizeSelected(object? selected)
        {
            if (selected is not IDictionary<string, object?> m || m.Count != 1) return selected;
            var only = m.First();
            return new Dictionary<string, object?>(StringComparer.Ordinal) { [only.Key] = AsLong(only.Value) };
        }

        private static object? Unwrap(object? selected) =>
            selected is IDictionary<string, object?> m && m.Count == 1 ? m.First().Value : selected;

        private static string WireValueType(object? selected)
        {
            if (selected is IDictionary<string, object?> m && m.Count == 1)
            {
                switch (m.First().Key)
                {
                    case "stringList": return "string_list";
                    case "bool": return "bool";
                    case "int": return "int";
                    case "double": return "double";
                    case "string": return "string";
                }
            }
            return WireValueTypeOf(Unwrap(selected));
        }

        private static string WireValueTypeOf(object? v) => v switch
        {
            string => "string",
            bool => "bool",
            long or int => "int",
            double or float => "double",
            IEnumerable => "string_list",
            _ => "string",
        };

        private static object? AsLong(object? v) => v switch
        {
            int i => (long)i,
            short s => (long)s,
            byte b => (long)b,
            _ => v,
        };
    }

    private sealed class CapturingSender : ITelemetrySender
    {
        private readonly object _gate = new();
        private readonly List<IDictionary<string, object?>> _envelopes = new();

        public IDictionary<string, object?>[] Envelopes
        {
            get { lock (_gate) { return _envelopes.ToArray(); } }
        }

        public Task SendAsync(IDictionary<string, object?> payload, CancellationToken cancellationToken)
        {
            lock (_gate) { _envelopes.Add(payload); }
            return Task.CompletedTask;
        }
    }

    // ---------------------------------------------------------------------------
    // Internal helpers.
    // ---------------------------------------------------------------------------

    private static string LocateDatadir()
    {
        // The dotnet test working dir is the test project root. Walk up to the monorepo root
        // where integration-test-data lives.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, "integration-test-data", "data", "integration-tests"));
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        // Final fallback: the absolute path is hard-coded so a CI failure surfaces the right
        // remediation (clone integration-test-data alongside sdk-net).
        return "/Users/jeffdwyer/code/quonfig/integration-test-data/data/integration-tests";
    }
}
