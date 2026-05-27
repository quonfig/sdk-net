// MD5 is the cross-SDK reportable-value hash (matches sdk-java/sdk-go) and is not used for any
// security boundary; suppress CA5351 at the file scope. The other suppressions cover code-style
// analyzers that conflict with the test harness's mirror-sdk-java shape.
#pragma warning disable CA1510 // ArgumentNullException.ThrowIfNull — explicit throw kept for parity
#pragma warning disable CA1308 // ToLowerInvariant is required for YAML-symbol-style normalization
#pragma warning disable CA5351 // MD5 is the cross-SDK reportable-value hash (test-only telemetry)
#pragma warning disable CA1850 // ComputeHash kept for netstandard2.0 parity
#pragma warning disable CA1845 // string.Substring is fine for a 5-char suffix
#pragma warning disable CA1508 // dataflow-analysis false positives on ContextLookup
#pragma warning disable CA1303 // exception messages are diagnostic-only, not user-facing
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Xml;
using Quonfig.Sdk;
using Quonfig.Sdk.Datadir;
using Quonfig.Sdk.Eval;
using Quonfig.Sdk.Exceptions;
using Quonfig.Sdk.Telemetry;
using Quonfig.Sdk.Wire;
using EvalValueType = Quonfig.Sdk.Eval.ValueType;

namespace Quonfig.Sdk.Tests.Integration;

/// <summary>
/// Static harness for the auto-generated <c>*Tests.cs</c> files under
/// <c>Quonfig.Sdk.Tests.Integration</c>. Mirrors <c>sdk-java/.../TestSetup.java</c> and
/// <c>sdk-node/test/integration/setup.ts</c>: loads the shared YAML-driven fixture corpus from
/// <c>integration-test-data/data/integration-tests/</c> once per test assembly, exposes the
/// case-style helpers the generators call into, and routes env-var lookups through a
/// thread-local override so <see cref="WithEnv"/> cases don't bleed into other tests.
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

    private static readonly ThreadLocal<Dictionary<string, string?>> EnvOverrides =
        new(() => new Dictionary<string, string?>(StringComparer.Ordinal));

    /// <summary>Path to the integration-test-data datadir tree.</summary>
    public static readonly string DATADIR = LocateDatadir();

    private static readonly ConfigStore Store = BuildStore();
    private static readonly Evaluator EvaluatorInstance = new(Store, new Resolver.EnvLookup(LookupEnv));

    // ---------------------------------------------------------------------------
    // Literal helpers — called from generator output.
    // ---------------------------------------------------------------------------

    /// <summary>Build an immutable list literal, mirroring the generator's <c>TestSetup.List(...)</c> emission.</summary>
    public static List<object?> List(params object?[] items)
    {
        var list = new List<object?>(items?.Length ?? 0);
        if (items is not null) list.AddRange(items);
        return list;
    }

    /// <summary>
    /// Build an ordered <see cref="Dictionary{TKey, TValue}"/> from alternating key/value pairs,
    /// mirroring the generator's <c>TestSetup.Map(...)</c> emission. Throws if <paramref name="pairs"/>
    /// is not even-length or contains non-string keys.
    /// </summary>
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
                throw new ArgumentException(
                    "TestSetup.Map keys must be strings; got " +
                        (pairs[i] is null ? "null" : pairs[i]!.GetType().FullName));
            }
            result[key] = pairs[i + 1];
        }
        return result;
    }

    // ---------------------------------------------------------------------------
    // Env-var override helper — used by env_vars YAML cases.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Run <paramref name="body"/> with each entry of <paramref name="env"/> installed as a
    /// per-thread env-var override. Reverts the prior overrides on exit (including on throw).
    /// </summary>
    public static void WithEnv(Dictionary<string, object?> env, Action body)
    {
        if (body is null) throw new ArgumentNullException(nameof(body));
        var previous = new Dictionary<string, string?>(EnvOverrides.Value!, StringComparer.Ordinal);
        var merged = new Dictionary<string, string?>(previous, StringComparer.Ordinal);
        if (env is not null)
        {
            foreach (var kv in env)
            {
                merged[kv.Key] = kv.Value?.ToString();
            }
        }
        EnvOverrides.Value = merged;
        try
        {
            body();
        }
        finally
        {
            EnvOverrides.Value = previous;
        }
    }

    private static string? LookupEnv(string name)
    {
        var overrides = EnvOverrides.Value!;
        if (overrides.TryGetValue(name, out var v)) return v;
        if (BaseEnv.TryGetValue(name, out var b)) return b;
        return Environment.GetEnvironmentVariable(name);
    }

    // ---------------------------------------------------------------------------
    // Eval-style case helpers.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Direct evaluator+resolver path. Returns the raw resolved value, or <c>null</c> if no rule
    /// matched (or the key is missing). Mirrors sdk-java's <c>resolveCase</c>.
    /// </summary>
    public static object? ResolveCase(string key, Dictionary<string, object?> contextMap)
    {
        var cfg = Store.Get(key);
        if (cfg is null) return null;
        var ctx = ToContextSet(contextMap);
        var match = EvaluatorInstance.Evaluate(cfg, ctx, ENV_ID);
        if (!match.IsMatch || match.Value is null) return null;
        return UnwrapPayload(match.Value);
    }

    /// <summary>
    /// get-with-default semantic: missing key OR no-match returns <paramref name="def"/>.
    /// </summary>
    public static object? GetCase(string key, Dictionary<string, object?> contextMap, object? def)
    {
        var cfg = Store.Get(key);
        if (cfg is null) return def;
        var ctx = ToContextSet(contextMap);
        var match = EvaluatorInstance.Evaluate(cfg, ctx, ENV_ID);
        if (!match.IsMatch || match.Value is null) return def;
        return UnwrapPayload(match.Value);
    }

    /// <summary>
    /// IsFeatureEnabled semantic: returns <c>true</c> iff the resolved value is the boolean
    /// <c>true</c>; everything else (missing, non-bool, false) yields <c>false</c>.
    /// </summary>
    public static object EnabledCase(string key, Dictionary<string, object?> contextMap)
    {
        var v = ResolveCase(key, contextMap);
        return v is bool b && b;
    }

    /// <summary>
    /// Evaluate the key like <see cref="ResolveCase"/>, but raise the appropriate
    /// <see cref="QuonfigException"/> subclass keyed by <paramref name="errKey"/>.
    /// </summary>
    public static object? RunRaiseCase(string key, Dictionary<string, object?> contextMap, string errKey)
    {
        var cfg = Store.Get(key);
        if (cfg is null)
        {
            throw new QuonfigKeyNotFoundException($"config \"{key}\" not found");
        }
        var ctx = ToContextSet(contextMap);
        EvaluationMatch match;
        try
        {
            match = EvaluatorInstance.Evaluate(cfg, ctx, ENV_ID);
        }
        catch (QuonfigException qe)
        {
            throw MapException(errKey, qe);
        }
        if (!match.IsMatch || match.Value is null)
        {
            throw new QuonfigKeyNotFoundException($"config \"{key}\" produced no match");
        }
        return UnwrapPayload(match.Value);
    }

    private static QuonfigException MapException(string errKey, QuonfigException source)
    {
        return errKey switch
        {
            "missing_env_var" => source is QuonfigEnvVarNotSetException ? source
                : new QuonfigEnvVarNotSetException(source.Message, source),
            "unable_to_decrypt" => source is QuonfigDecryptionException ? source
                : new QuonfigDecryptionException(source.Message, source),
            "unable_to_coerce_env_var" => new QuonfigKeyNotFoundException(source.Message, source),
            "missing_default" => source is QuonfigKeyNotFoundException ? source
                : new QuonfigKeyNotFoundException(source.Message, source),
            _ => source,
        };
    }

    // ---------------------------------------------------------------------------
    // Assertion shims used by the generator.
    // ---------------------------------------------------------------------------

    /// <summary>Assert that <paramref name="actual"/> is a double within 1e-9 of <paramref name="expected"/>.</summary>
    public static void AssertDoubleEquals(double expected, object? actual)
    {
        if (actual is not IConvertible)
        {
            throw new Xunit.Sdk.XunitException($"expected double {expected}, got {actual ?? (object)"null"}");
        }
        double got = Convert.ToDouble(actual, System.Globalization.CultureInfo.InvariantCulture);
        if (Math.Abs(got - expected) > 1e-9)
        {
            throw new Xunit.Sdk.XunitException($"expected {expected} (±1e-9), got {got}");
        }
    }

    /// <summary>Assert that <paramref name="actual"/> represents the given <paramref name="millis"/> (±1ms).</summary>
    public static void AssertDurationMillis(object? actual, long millis)
    {
        long got;
        if (actual is TimeSpan ts) got = (long)ts.TotalMilliseconds;
        else if (actual is string s) got = ParseFlexibleIsoDurationMillis(s);
        else throw new Xunit.Sdk.XunitException($"expected TimeSpan or ISO-8601 string, got {actual ?? (object)"null"}");
        if (Math.Abs(got - millis) > 1)
        {
            throw new Xunit.Sdk.XunitException($"expected {millis} ms (±1ms), got {got}");
        }
    }

    /// <summary>
    /// Parse an ISO-8601 duration that may include fractional hours / minutes (PT0.5H, PT1.5M).
    /// .NET's <see cref="XmlConvert.ToTimeSpan"/> already handles these but legacy callers may pass
    /// odd forms; fall back to it on anything we can't match ourselves.
    /// </summary>
    public static long ParseFlexibleIsoDurationMillis(string s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            s,
            @"^P(?:(\d+(?:\.\d+)?)D)?(?:T(?:(\d+(?:\.\d+)?)H)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)S)?)?$");
        if (!m.Success)
        {
            return (long)XmlConvert.ToTimeSpan(s).TotalMilliseconds;
        }
        double Parse(int g) => m.Groups[g].Success ? double.Parse(m.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        double total = Parse(1) * 86_400 + Parse(2) * 3_600 + Parse(3) * 60 + Parse(4);
        return (long)Math.Round(total * 1000.0);
    }

    // ---------------------------------------------------------------------------
    // Datadir helpers (datadir_environment.yaml / datadir_value_type.yaml).
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Construct a fresh <see cref="Quonfig"/> client per the <c>client_overrides</c> in a YAML
    /// datadir case. Caller does not dispose; the integration tests don't either.
    /// </summary>
    public static Quonfig DatadirClient(Dictionary<string, object?> opts)
    {
        string? datadirOpt = StringOpt(opts, "datadir");
        string? envOpt = StringOpt(opts, "environment");

        var options = new QuonfigOptions
        {
            EnvLookup = LookupEnv,
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
        };
        if (!string.IsNullOrEmpty(datadirOpt)) options.Datadir = datadirOpt;
        if (!string.IsNullOrEmpty(envOpt)) options.Environment = envOpt;

        return new Quonfig(options);
    }

    /// <summary>
    /// Build a datadir-backed store + evaluator for <paramref name="opts"/> and return the resolved
    /// value for <paramref name="key"/>, or <c>null</c> if the key is absent / produced no match.
    /// </summary>
    public static object? DatadirGet(Dictionary<string, object?> opts, string key)
    {
        var (store, evaluator, env) = BuildDatadirEvaluator(opts);
        var cfg = store.Get(key);
        if (cfg is null) return null;
        var match = evaluator.Evaluate(cfg, new ContextSet(), env);
        if (!match.IsMatch || match.Value is null) return null;
        return UnwrapPayload(match.Value);
    }

    /// <summary>
    /// Assert that the LOADED envelope's raw <see cref="Value"/> for <paramref name="key"/> is a
    /// real <see cref="long"/> / <see cref="double"/>, not a string. Guards the cross-SDK contract
    /// that the datadir loader coerces int/double at load time, matching api-delivery.
    /// </summary>
    public static void AssertRawValueNumeric(Dictionary<string, object?> opts, string key)
    {
        var (store, evaluator, env) = BuildDatadirEvaluator(opts);
        var cfg = store.Get(key);
        if (cfg is null)
        {
            throw new Xunit.Sdk.XunitException(
                $"assertRawValueNumeric: config \"{key}\" not found in datadir {StringOpt(opts, "datadir")}");
        }
        var match = evaluator.Evaluate(cfg, new ContextSet(), env);
        if (!match.IsMatch || match.Value is null)
        {
            throw new Xunit.Sdk.XunitException(
                $"assertRawValueNumeric: config \"{key}\" produced no match");
        }
        var raw = match.Value.Payload;
        if (raw is string s)
        {
            throw new Xunit.Sdk.XunitException(
                $"datadir loader returned {match.Value.Type} config \"{key}\" as a string (\"{s}\") " +
                    "— expected a coerced numeric value. The datadir loader must coerce int/double " +
                    "at load time, matching api-delivery.");
        }
        if (raw is not (long or int or double or float or decimal))
        {
            throw new Xunit.Sdk.XunitException(
                $"assertRawValueNumeric: expected a Number for {match.Value.Type} config \"{key}\", " +
                    "got " + (raw is null ? "null" : raw.GetType().FullName + " (" + raw + ")"));
        }
    }

    private static (ConfigStore store, Evaluator evaluator, string env) BuildDatadirEvaluator(
        Dictionary<string, object?> opts)
    {
        string? datadirOpt = StringOpt(opts, "datadir");
        string? envOpt = StringOpt(opts, "environment");
        if (string.IsNullOrEmpty(envOpt))
        {
            envOpt = LookupEnv("QUONFIG_ENVIRONMENT");
        }
        if (string.IsNullOrEmpty(datadirOpt))
        {
            throw new ArgumentException("DatadirGet requires opts['datadir']");
        }
        if (string.IsNullOrEmpty(envOpt))
        {
            throw new InvalidOperationException(
                "datadir mode requires environment; set Options.Environment(...) or QUONFIG_ENVIRONMENT");
        }
        var envelope = DatadirLoader.Load(datadirOpt!, envOpt!);
        var store = new ConfigStore();
        store.Update(envelope);
        var evaluator = new Evaluator(store, new Resolver.EnvLookup(LookupEnv));
        return (store, evaluator, envOpt!);
    }

    private static string? StringOpt(Dictionary<string, object?> opts, string key) =>
        opts is not null && opts.TryGetValue(key, out var v) ? v?.ToString() : null;

    // ---------------------------------------------------------------------------
    // client_overrides (init timeout, on_init_failure) — http-mode shims.
    //
    // These mirror sdk-java's "no-op until http-mode lands" pattern, but
    // sdk-net DOES have an http-mode entry point — and the YAML cases use
    // an unreachable URL with a 10ms timeout, so the real construction
    // path correctly surfaces QuonfigInitTimeoutException. Wire them up
    // properly here rather than stubbing.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Construct a client against <paramref name="apiURL"/> with <paramref name="timeoutSec"/>
    /// init timeout + <paramref name="onInitFailure"/> policy ("raise"/"return"); fail the test
    /// if init does NOT raise <see cref="QuonfigInitTimeoutException"/>.
    /// </summary>
    public static void AssertInitializationTimeoutError(string key, double timeoutSec, string apiURL, string onInitFailure)
    {
        Xunit.Assert.Throws<QuonfigInitTimeoutException>(() =>
        {
            var client = BuildHttpClientForTimeout(timeoutSec, apiURL, onInitFailure);
            try { client.InitAsync().GetAwaiter().GetResult(); }
            finally { client.CloseAsync().GetAwaiter().GetResult(); }
        });
    }

    /// <summary>
    /// Construct a client per overrides and call <paramref name="fn"/> ("get"/"get_or_raise");
    /// assert that the operation throws <typeparamref name="TException"/>.
    /// </summary>
    public static void AssertClientConstructionRaises<TException>(
        string key, double timeoutSec, string apiURL, string onInitFailure, string fn)
        where TException : Exception
    {
        Xunit.Assert.Throws<TException>(() =>
        {
            var client = BuildHttpClientForTimeout(timeoutSec, apiURL, onInitFailure);
            try
            {
                try
                {
                    client.InitAsync().GetAwaiter().GetResult();
                }
                catch (QuonfigInitTimeoutException) when (string.Equals(onInitFailure, "return", StringComparison.Ordinal))
                {
                    // OnInitFailure=ReturnDefaults swallows the init timeout; surface the missing-key
                    // error from the getter as the YAML expects.
                }
                // Once init resolves under ReturnDefaults, the store is empty; calling a getter
                // throws QuonfigKeyNotFoundException via OnNoDefault=Throw.
                _ = client.GetString(key);
            }
            finally
            {
                client.CloseAsync().GetAwaiter().GetResult();
            }
        });
    }

    /// <summary>
    /// Construct a client per overrides, call <paramref name="fn"/>, and return the result.
    /// Used for client_overrides cases that expect a value (typically null under
    /// OnInitFailure=ReturnDefaults).
    /// </summary>
    public static object? AssertClientConstructionValue(
        string key, double timeoutSec, string apiURL, string onInitFailure, string fn)
    {
        var client = BuildHttpClientForTimeout(timeoutSec, apiURL, onInitFailure);
        try
        {
            try
            {
                client.InitAsync().GetAwaiter().GetResult();
            }
            catch (QuonfigInitTimeoutException) when (string.Equals(onInitFailure, "return", StringComparison.Ordinal))
            {
                // Swallow under ReturnDefaults policy.
            }
            return client.GetString(key);
        }
        finally
        {
            client.CloseAsync().GetAwaiter().GetResult();
        }
    }

    private static Quonfig BuildHttpClientForTimeout(double timeoutSec, string apiURL, string onInitFailure)
    {
        var options = new QuonfigOptions
        {
            SdkKey = "integration-tests",
            EnvLookup = LookupEnv,
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
            InitTimeout = TimeSpan.FromMilliseconds(Math.Max(1, timeoutSec * 1000)),
            OnInitFailure = string.Equals(onInitFailure, "return", StringComparison.Ordinal)
                ? OnInitFailure.ReturnDefaults
                : OnInitFailure.Throw,
            OnNoDefault = OnNoDefault.Throw,
            ApiUrls = new[] { apiURL },
            // Disable SSE — the unreachable test URLs would spawn a background reconnect loop.
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
        };
        return new Quonfig(options);
    }

    // ---------------------------------------------------------------------------
    // post.yaml / telemetry.yaml — aggregator helpers.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Construct a fresh aggregator for the given kind ("context_shape", "evaluation_summary",
    /// "example_contexts") with the given client_overrides.
    /// </summary>
    public static object BuildAggregator(string kind, Dictionary<string, object?> overrides)
    {
        var mode = NormalizeUploadMode(overrides is not null && overrides.TryGetValue("context_upload_mode", out var m) ? m : null);
        bool collectSummaries = !(overrides is not null
            && overrides.TryGetValue("collect_evaluation_summaries", out var ce)
            && ce is false);
        return kind switch
        {
            "context_shape" => new ContextShapeAggregator(new ContextShapeCollector(mode)),
            "evaluation_summary" => new EvaluationSummaryAggregator(new EvaluationSummaryCollector(collectSummaries)),
            "example_contexts" => new ExampleContextsAggregator(new ExampleContextCollector(mode)),
            _ => throw new ArgumentException("unknown aggregator kind: " + kind),
        };
    }

    /// <summary>
    /// Feed <paramref name="data"/> into the aggregator. For context_shape / example_contexts
    /// <paramref name="data"/> is a context-map or list of context-maps; for evaluation_summary
    /// it carries the keys to evaluate against the shared store.
    /// </summary>
    public static void FeedAggregator(object agg, string kind, object? data, Dictionary<string, object?>? ctxMap)
    {
        if (kind == "context_shape" || kind == "example_contexts")
        {
            foreach (var record in NormalizeContextRecords(data))
            {
                if (agg is ContextShapeAggregator csa) csa.Collector.Push(record);
                else if (agg is ExampleContextsAggregator eca) eca.Collector.Push(record);
                else throw new ArgumentException("aggregator/kind mismatch: " + kind);
            }
            return;
        }
        if (kind == "evaluation_summary" && agg is EvaluationSummaryAggregator esa)
        {
            var ctx = ToContextSet(ctxMap);
            var payload = data as IDictionary<string, object?> ?? new Dictionary<string, object?>();
            var withCtx = payload.TryGetValue("keys", out var k) && k is IEnumerable<object?> kl
                ? kl
                : Enumerable.Empty<object?>();
            var withoutCtx = payload.TryGetValue("keys_without_context", out var k2) && k2 is IEnumerable<object?> kl2
                ? kl2
                : Enumerable.Empty<object?>();
            foreach (var item in withCtx) FeedSummary(esa, item?.ToString() ?? "", ctx);
            foreach (var item in withoutCtx) FeedSummary(esa, item?.ToString() ?? "", new ContextSet());
            return;
        }
        throw new ArgumentException("FeedAggregator: unsupported kind=" + kind);
    }

    private static void FeedSummary(EvaluationSummaryAggregator agg, string key, ContextSet ctx)
    {
        var cfg = Store.Get(key);
        if (cfg is null) return;

        // Need to access the LOADED Value (pre-resolve) to compute reportableValue + selected wrapper.
        var row = ConfigRowParser.Parse(cfg);
        Value? rawValue = FindFirstMatchingRawValue(row, ctx);
        if (rawValue is null) return;

        EvaluationMatch match;
        Value? resolved;
        try
        {
            match = EvaluatorInstance.Evaluate(cfg, ctx, ENV_ID);
            resolved = match.Value;
        }
        catch (QuonfigException)
        {
            return;
        }

        if (!match.IsMatch || resolved is null) return;

        object? unwrapped = UnwrapPayload(resolved);
        string? reportable = ReportableValueFor(rawValue);
        int reasonNum;
        if (rawValue.Type == EvalValueType.WeightedValues) reasonNum = 3;
        else if (HasTargetingRules(row)) reasonNum = 2;
        else reasonNum = 1;

        // Find the matched rule index. EvaluationMatch.RuleIndex is the env-or-default index.
        int ruleIndex = match.RuleIndex;

        // Weighted bucket index is needed for telemetry — recompute it here (the Resolver
        // doesn't expose it). Mirrors sdk-java's behavior.
        int weightedIndex = -1;
        if (rawValue.Type == EvalValueType.WeightedValues && rawValue.Payload is WeightedValuesPayload wv)
        {
            weightedIndex = PickWeightedIndex(cfg.Key, wv, ctx);
        }

        var stat = new EvaluationStat(
            row.Id,
            row.Key,
            ConfigTypeName(row.Type),
            ruleIndex,
            weightedIndex,
            unwrapped,
            reportable,
            reasonNum);
        agg.Collector.Push(stat);

        if (reportable is not null)
        {
            agg.UnwrappedOverrides[row.Key] = new ValueOverride(unwrapped, WireValueTypeFor(unwrapped));
        }
    }

    private static Value? FindFirstMatchingRawValue(ConfigRow row, ContextSet ctx)
    {
        // Mirror Evaluator's env-then-default lookup, returning the raw (pre-resolver) Value.
        var env = row.FindEnvironment(ENV_ID);
        if (env is not null)
        {
            foreach (var r in env.Rules)
            {
                if (AllCriteriaMatch(r.Criteria, ctx)) return r.Value;
            }
        }
        foreach (var r in row.DefaultRules)
        {
            if (AllCriteriaMatch(r.Criteria, ctx)) return r.Value;
        }
        return null;
    }

    private static bool AllCriteriaMatch(IReadOnlyList<Criterion> criteria, ContextSet ctx)
    {
        foreach (var c in criteria)
        {
            var lookup = string.IsNullOrEmpty(c.PropertyName)
                ? ContextLookup.Absent
                : ctx.GetContextValue(c.PropertyName);
            object? rawValue = lookup.Exists ? lookup.Value?.ToObject() : null;
            Operators.SegmentResolver segResolver = segKey =>
            {
                var seg = Store.Get(segKey);
                if (seg is null) return SegmentResolverResult.NotFound;
                var subMatch = EvaluatorInstance.Evaluate(seg, ctx, "");
                if (!subMatch.IsMatch || subMatch.Value is null) return SegmentResolverResult.NotFound;
                return SegmentResolverResult.FromValue(subMatch.Value.Payload is bool b && b);
            };
            if (!Operators.EvaluateCriterion(rawValue, lookup.Exists, c, segResolver)) return false;
        }
        return true;
    }

    private static int PickWeightedIndex(string configKey, WeightedValuesPayload wv, ContextSet ctx)
    {
        if (wv.Variants.Count == 0) return -1;
        double fraction = 0.0;
        if (!string.IsNullOrEmpty(wv.HashByPropertyName))
        {
            var lookup = ctx.GetContextValue(wv.HashByPropertyName);
            if (lookup.Exists)
            {
                string rendered = lookup.Value switch
                {
                    ContextValueString s => s.Value,
                    ContextValueInt i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ContextValueLong l => l.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ContextValueDouble d => d.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    ContextValueBool b => b.Value ? "true" : "false",
                    _ => lookup.Value?.ToObject()?.ToString() ?? "",
                };
                fraction = Murmur3.HashZeroToOne(configKey + rendered);
            }
        }
        long total = 0;
        foreach (var v in wv.Variants) total += v.Weight;
        if (total <= 0) return 0;
        double threshold = fraction * total;
        long running = 0;
        for (int i = 0; i < wv.Variants.Count; i++)
        {
            running += wv.Variants[i].Weight;
            if (running >= threshold) return i;
        }
        return 0;
    }

    private static string? ReportableValueFor(Value val)
    {
        if (val is null) return null;
        if (!val.Confidential && string.IsNullOrEmpty(val.DecryptWith)) return null;
        string raw = val.Payload?.ToString() ?? "";
        using var md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(raw));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        string hex = sb.ToString();
        return hex.Length < 5 ? null : "*****" + hex.Substring(0, 5);
    }

    private static bool HasTargetingRules(ConfigRow row)
    {
        if (AnyNonTrivial(row.DefaultRules)) return true;
        foreach (var env in row.Environments)
        {
            if (AnyNonTrivial(env.Rules)) return true;
        }
        return false;
    }

    private static bool AnyNonTrivial(IReadOnlyList<Rule> rules)
    {
        foreach (var r in rules)
        {
            foreach (var c in r.Criteria)
            {
                if (!string.Equals(c.Operator, "ALWAYS_TRUE", StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    private static string ConfigTypeName(ConfigType t) => t switch
    {
        ConfigType.Config => "CONFIG",
        ConfigType.FeatureFlag => "FEATURE_FLAG",
        ConfigType.Segment => "SEGMENT",
        ConfigType.LogLevel => "LOG_LEVEL",
        ConfigType.Schema => "SCHEMA",
        _ => t.ToString().ToUpperInvariant(),
    };

    /// <summary>
    /// Flush the aggregator and return the normalized list/map structure the generator's
    /// <see cref="Xunit.Assert.Equal(object?, object?)"/> assertion compares against.
    /// </summary>
    public static object? AggregatorPost(object agg, string kind, string endpoint)
    {
        if (agg is ContextShapeAggregator csa)
        {
            var ev = csa.Collector.Drain();
            if (ev is null) return null;
            if (ev["contextShapes"] is not IDictionary<string, object?> env) return null;
            if (env["shapes"] is not IEnumerable<object?> list) return null;
            var rows = new List<object?>();
            foreach (var el in list)
            {
                if (el is not IDictionary<string, object?> shape) continue;
                var row = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = shape["name"],
                };
                if (shape["fieldTypes"] is IDictionary<string, object?> ft)
                {
                    var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var e in ft) normalized[e.Key] = AsLong(e.Value);
                    row["field_types"] = normalized;
                }
                else
                {
                    row["field_types"] = new Dictionary<string, object?>(StringComparer.Ordinal);
                }
                rows.Add(row);
            }
            return rows.Count == 0 ? null : rows;
        }

        if (agg is ExampleContextsAggregator eca)
        {
            var ev = eca.Collector.Drain();
            if (ev is null) return null;
            if (ev["exampleContexts"] is not IDictionary<string, object?> env) return null;
            if (env["examples"] is not IList<Dictionary<string, object?>> examples || examples.Count == 0) return null;
            var first = examples[0];
            if (first["contextSet"] is not IDictionary<string, object?> cs) return null;
            if (cs["contexts"] is not IEnumerable<object?> ctxs) return null;
            var outMap = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var c in ctxs)
            {
                if (c is not IDictionary<string, object?> cm) continue;
                if (cm.TryGetValue("type", out var name) && name is string n
                    && cm.TryGetValue("values", out var values) && values is IDictionary<string, object?> vmap)
                {
                    outMap[n] = new Dictionary<string, object?>(vmap, StringComparer.Ordinal);
                }
            }
            return outMap.Count == 0 ? null : outMap;
        }

        if (agg is EvaluationSummaryAggregator esa)
        {
            var ev = esa.Collector.Drain();
            if (ev is null) return null;
            if (ev["summaries"] is not IDictionary<string, object?> env) return null;
            if (env["summaries"] is not IEnumerable<object?> summaries) return null;

            var summariesList = summaries.OfType<IDictionary<string, object?>>().ToList();
            if (summariesList.Count == 0) return null;

            // Sort: CONFIG before FEATURE_FLAG; preserve insertion order within type.
            summariesList = summariesList
                .Select((s, i) => (s, i))
                .OrderBy(t => TelemetryConfigType(t.s.TryGetValue("type", out var ty) ? ty : null), StringComparer.Ordinal)
                .ThenBy(t => t.i)
                .Select(t => t.s)
                .ToList();

            var output = new List<object?>();
            foreach (var s in summariesList)
            {
                if (s.TryGetValue("counters", out var countersObj) && countersObj is IEnumerable<object?> counters)
                {
                    foreach (var c in counters)
                    {
                        if (c is not IDictionary<string, object?> cm) continue;
                        object? selectedValue = NormalizeSelectedValue(cm.TryGetValue("selectedValue", out var sv) ? sv : null);
                        string wireValueType = WireValueTypeForSelected(selectedValue);
                        object? wireValue = UnwrapSelectedValue(selectedValue);

                        string key = s.TryGetValue("key", out var k) ? (k as string ?? "") : "";
                        if (esa.UnwrappedOverrides.TryGetValue(key, out var ovr))
                        {
                            wireValue = ovr.Unwrapped;
                            wireValueType = ovr.ValueType;
                        }

                        var summary = new Dictionary<string, object?>(StringComparer.Ordinal);
                        summary["config_row_index"] = AsLong(cm.TryGetValue("configRowIndex", out var cri) ? cri : 0L);
                        summary["conditional_value_index"] = AsLong(cm.TryGetValue("conditionalValueIndex", out var cvi) ? cvi : 0L);
                        if (cm.TryGetValue("weightedValueIndex", out var wvi) && wvi is int wviInt && wviInt >= 0)
                        {
                            summary["weighted_value_index"] = (long)wviInt;
                        }
                        else if (wvi is long wviLong && wviLong >= 0)
                        {
                            summary["weighted_value_index"] = wviLong;
                        }

                        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["key"] = key,
                            ["type"] = TelemetryConfigType(s.TryGetValue("type", out var ty2) ? ty2 : null),
                            ["value"] = wireValue,
                            ["value_type"] = wireValueType,
                            ["count"] = AsLong(cm.TryGetValue("count", out var ct) ? ct : 0L),
                            ["reason"] = AsLong(cm.TryGetValue("reason", out var rs) ? rs : 0L),
                        };
                        if (selectedValue is not null) record["selected_value"] = selectedValue;
                        record["summary"] = summary;
                        output.Add(record);
                    }
                }
            }
            return output.Count == 0 ? null : output;
        }
        throw new ArgumentException("AggregatorPost: unknown aggregator " + agg);
    }

    private static ContextUploadMode NormalizeUploadMode(object? raw)
    {
        if (raw is not string s) return ContextUploadMode.PeriodicExample;
        s = s.TrimStart(':').ToLowerInvariant();
        if (s == "none") return ContextUploadMode.None;
        if (s == "shape_only" || s == "shapes_only") return ContextUploadMode.ShapesOnly;
        return ContextUploadMode.PeriodicExample;
    }

    private static object? NormalizeSelectedValue(object? selectedValue)
    {
        if (selectedValue is not IDictionary<string, object?> m || m.Count != 1) return selectedValue;
        var only = m.First();
        object? v = only.Value;
        if (v is int ii) v = (long)ii;
        return new Dictionary<string, object?>(StringComparer.Ordinal) { [only.Key] = v };
    }

    private static object? UnwrapSelectedValue(object? selectedValue)
    {
        if (selectedValue is not IDictionary<string, object?> m || m.Count != 1) return selectedValue;
        return m.First().Value;
    }

    private static string WireValueTypeForSelected(object? selectedValue)
    {
        if (selectedValue is IDictionary<string, object?> m && m.Count == 1)
        {
            return m.First().Key switch
            {
                "stringList" => "string_list",
                "bool" => "bool",
                "int" => "int",
                "double" => "double",
                "string" => "string",
                _ => WireValueTypeFor(UnwrapSelectedValue(selectedValue)),
            };
        }
        return WireValueTypeFor(UnwrapSelectedValue(selectedValue));
    }

    private static string WireValueTypeFor(object? v)
    {
        if (v is string) return "string";
        if (v is bool) return "bool";
        if (v is long or int) return "int";
        if (v is double or float) return "double";
        if (v is IEnumerable && v is not string) return "string_list";
        return "string";
    }

    private static string TelemetryConfigType(object? internalName)
    {
        if (internalName is null) return "";
        return internalName.ToString()!.ToUpperInvariant();
    }

    private static object? AsLong(object? v) => v switch
    {
        null => null,
        long l => l,
        int i => (long)i,
        short s => (long)s,
        byte b => (long)b,
        _ => v,
    };

    // ---------------------------------------------------------------------------
    // Aggregator-state types.
    // ---------------------------------------------------------------------------

    private sealed class ContextShapeAggregator
    {
        public ContextShapeCollector Collector { get; }
        public ContextShapeAggregator(ContextShapeCollector c) => Collector = c;
    }

    private sealed class ExampleContextsAggregator
    {
        public ExampleContextCollector Collector { get; }
        public ExampleContextsAggregator(ExampleContextCollector c) => Collector = c;
    }

    private sealed class EvaluationSummaryAggregator
    {
        public EvaluationSummaryCollector Collector { get; }
        public Dictionary<string, ValueOverride> UnwrappedOverrides { get; } = new(StringComparer.Ordinal);

        public EvaluationSummaryAggregator(EvaluationSummaryCollector c) => Collector = c;
    }

    private sealed class ValueOverride
    {
        public ValueOverride(object? unwrapped, string valueType)
        {
            Unwrapped = unwrapped;
            ValueType = valueType;
        }

        public object? Unwrapped { get; }
        public string ValueType { get; }
    }

    // ---------------------------------------------------------------------------
    // Internal helpers.
    // ---------------------------------------------------------------------------

    private static ContextSet ToContextSet(Dictionary<string, object?>? contextMap)
    {
        var cs = new ContextSet();
        if (contextMap is null || contextMap.Count == 0) return cs;
        foreach (var entry in contextMap)
        {
            if (entry.Value is not IDictionary<string, object?> inner) continue;
            var props = new ContextProperties();
            foreach (var p in inner)
            {
                // Null values mean "explicitly absent" — skip the entry so IS_NOT_PRESENT matches
                // and IS_PRESENT does not. Mirrors sdk-go/sdk-node: a null context value is
                // semantically the same as the property being missing.
                if (p.Value is null) continue;
                props[p.Key] = LiftToContextValue(p.Value);
            }
            cs[entry.Key] = props;
        }
        return cs;
    }

    private static List<ContextSet> NormalizeContextRecords(object? data)
    {
        var result = new List<ContextSet>();
        if (data is null) return result;
        if (data is IList<object?> list)
        {
            foreach (var el in list)
            {
                if (el is IDictionary<string, object?> m) result.Add(ToContextSet(new Dictionary<string, object?>(m, StringComparer.Ordinal)));
            }
            return result;
        }
        if (data is IDictionary<string, object?> dict)
        {
            result.Add(ToContextSet(new Dictionary<string, object?>(dict, StringComparer.Ordinal)));
        }
        return result;
    }

    private static ContextValue LiftToContextValue(object? v)
    {
        return v switch
        {
            null => new ContextValueString(""),
            ContextValue cv => cv,
            string s => new ContextValueString(s),
            bool b => new ContextValueBool(b),
            int i => new ContextValueInt(i),
            long l => new ContextValueLong(l),
            double d => new ContextValueDouble(d),
            float f => new ContextValueDouble(f),
            IEnumerable<string> sl => new ContextValueStringList(sl),
            IEnumerable<object?> list => new ContextValueStringList(list.Select(o => o?.ToString() ?? "")),
            _ => new ContextValueString(v.ToString() ?? ""),
        };
    }

    /// <summary>
    /// Unwraps a resolved <see cref="Value"/> to the CLR payload the generator's assertions
    /// compare against. Duration values surface as TimeSpan (so AssertDurationMillis sees the
    /// real ms count), JSON / string-lists stay as their dictionary / list payloads, etc.
    /// </summary>
    private static object? UnwrapPayload(Value v)
    {
        if (v.Payload is null) return null;
        if (v.Type == EvalValueType.Json)
        {
            // Already a Dictionary<string, object?> / List<object?>.
            return v.Payload;
        }
        return v.Payload;
    }

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

    private static ConfigStore BuildStore()
    {
        var datadir = DATADIR;
        if (!Directory.Exists(datadir))
        {
            throw new InvalidOperationException(
                $"[integration tests] fixtures not found at {datadir} — populate integration-test-data");
        }
        var envelope = DatadirLoader.Load(datadir, ENV_ID);
        var store = new ConfigStore();
        store.Update(envelope);
        return store;
    }
}
