using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Quonfig.Sdk.Eval;

/// <summary>
/// Rule evaluation engine. Mirrors sdk-java's <c>Evaluator</c> and sdk-go's
/// <c>EvaluateConfig</c>: for a given <see cref="ConfigResponse"/>, find the first rule whose
/// criteria all match, return its <see cref="Value"/> (after passing it through the
/// <see cref="Resolver"/> for env-var / weighted / decryption resolution). Environment-specific
/// rules take precedence over the row's default rule set.
///
/// <para>The first ConfigRow parse from the raw <see cref="System.Text.Json.JsonElement"/> is
/// cached per <see cref="ConfigResponse"/> instance so repeated evaluations of the same row
/// don't re-walk the JSON tree.</para>
/// </summary>
public sealed class Evaluator
{
    private readonly ConfigStore? _store;
    private readonly Resolver _resolver;
    private readonly ConditionalWeakTable<ConfigResponse, ConfigRow> _parsed = new();

    /// <summary>
    /// Called with the config key and property name when a <c>PROP_MATCHES</c> /
    /// <c>PROP_DOES_NOT_MATCH</c> regex times out and the criterion fails closed
    /// (qfg-goi1.2.15). The client logs it.
    /// </summary>
    internal Action<string, string>? OnRegexTimeout { get; set; }

    /// <summary>
    /// Initializes a new evaluator. <paramref name="store"/> is required for
    /// <c>IN_SEG</c>/<c>NOT_IN_SEG</c> recursion and for decryption-key lookup; if null, those
    /// operators behave as "segment not found" (sdk-java/sdk-go fall-back semantics).
    /// If <paramref name="resolver"/> is null, a default <see cref="Resolver"/> is wired up
    /// whose <see cref="Resolver.KeyResolver"/> recursively evaluates the decrypt-key config
    /// through this same evaluator.
    /// </summary>
    public Evaluator(ConfigStore? store, Resolver? resolver = null)
    {
        _store = store;
        _resolver = resolver ?? new Resolver(null, ResolveKey);
    }

    /// <summary>
    /// Convenience overload: builds a default <see cref="Resolver"/> with the supplied
    /// <paramref name="envLookup"/> while keeping the recursive decrypt-key resolution that the
    /// parameterless constructor wires up. Used by <see cref="Quonfig"/> when a caller overrides
    /// the env-var lookup (testability) without losing decryption support.
    /// </summary>
    public Evaluator(ConfigStore? store, Resolver.EnvLookup envLookup)
    {
        _store = store;
        _resolver = new Resolver(envLookup, ResolveKey);
    }

    /// <summary>
    /// Evaluates <paramref name="config"/> against <paramref name="contexts"/> in the given
    /// <paramref name="environmentId"/>. Returns the resolved match — or a "no match" outcome
    /// if no rule (env-specific or default) matched.
    /// </summary>
    public EvaluationMatch Evaluate(ConfigResponse config, ContextSet contexts, string? environmentId)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(contexts);
#else
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (contexts is null) throw new ArgumentNullException(nameof(contexts));
#endif
        var row = GetOrParse(config);
        return EvaluateRow(row, contexts, environmentId, null);
    }

    // segPath: the keys of the configs currently being evaluated above this one through
    // IN_SEG / NOT_IN_SEG. A segment reference back onto the path is a cycle and is treated as a
    // missing segment instead of recursing into an uncatchable StackOverflowException
    // (qfg-9dxb.7, sdk-go qfg-9dxb.4). It is a path, not a global visited set, so a diamond (two
    // segments that both reference a third) still resolves. Null for the top-level evaluation.
    private EvaluationMatch EvaluateRow(
        ConfigRow row, ContextSet contexts, string? environmentId, string[]? segPath)
    {
        if (!string.IsNullOrEmpty(environmentId))
        {
            var env = row.FindEnvironment(environmentId);
            if (env is not null)
            {
                var m = EvaluateRules(row, env.Rules, contexts, environmentId, segPath);
                if (m is not null) return m;
            }
        }

        var def = EvaluateRules(row, row.DefaultRules, contexts, environmentId, segPath);
        if (def is not null) return def;

        return EvaluationMatch.NoMatch(row.Id, row.Key, row.ValueType);
    }

    private EvaluationMatch? EvaluateRules(
        ConfigRow row, IReadOnlyList<Rule> rules, ContextSet contexts, string? environmentId, string[]? segPath)
    {
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (!AllCriteriaMatch(row.Key, rule.Criteria, contexts, segPath)) continue;

            // Resolver expands PROVIDED, weighted-buckets, decryption, env-var coercion.
            // It throws on env-var-missing / decryption failure — let it propagate so the
            // caller can surface the right error code. weightedIndex is >= 0 only when a
            // weighted-values bucket was chosen.
            var resolved = _resolver.Resolve(
                rule.Value, row.Key, row.ValueType, contexts, environmentId, out int weightedIndex, out string? missingHashProperty);

            // Canonical reason (mirrors sdk-go runtime_eval.go hasTargetingRules + integration-test-data
            // telemetry.yaml): SPLIT when a weighted bucket was resolved; otherwise STATIC only when
            // the config has NO real targeting anywhere (every criterion absent or ALWAYS_TRUE) and
            // the first rule won; otherwise TARGETING_MATCH — including a catch-all fallthrough inside
            // a config that does have targeting rules. qfg-q7yz.
            Reason reason;
            if (weightedIndex >= 0)
            {
                reason = Reason.Split;
            }
            else if (i == 0 && !HasTargetingRules(row))
            {
                reason = Reason.Static;
            }
            else
            {
                reason = Reason.TargetingMatch;
            }

            return EvaluationMatch.Matched(
                resolved, i, weightedIndex, reason, row.Id, row.Key, row.ValueType, missingHashProperty);
        }
        return null;
    }

    /// <summary>
    /// True if the config has any rule (in the default rule set or any environment-specific rule
    /// set) whose criteria include a non-<c>ALWAYS_TRUE</c> operator. Mirrors sdk-go's
    /// <c>hasTargetingRules</c>: a config that only matches via empty/ALWAYS_TRUE criteria is
    /// "static", so its match reports <see cref="Reason.Static"/> rather than
    /// <see cref="Reason.TargetingMatch"/>.
    /// </summary>
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
        foreach (var rule in rules)
        {
            foreach (var c in rule.Criteria)
            {
                if (!string.Equals(c.Operator, Operators.ALWAYS_TRUE, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private bool AllCriteriaMatch(
        string currentKey, IReadOnlyList<Criterion> criteria, ContextSet contexts, string[]? segPath)
    {
        foreach (var c in criteria)
        {
            if (!EvaluateOne(currentKey, c, contexts, segPath)) return false;
        }
        return true;
    }

    private bool EvaluateOne(string currentKey, Criterion criterion, ContextSet contexts, string[]? segPath)
    {
        var lookup = string.IsNullOrEmpty(criterion.PropertyName)
            ? ContextLookup.Absent
            : contexts.GetContextValue(criterion.PropertyName);
        object? rawValue = lookup.Exists ? lookup.Value?.ToObject() : null;

        Operators.SegmentResolver? segResolver = null;
        if (_store is not null)
        {
            segResolver = segKey =>
            {
                // A reference back onto the current evaluation path is a cycle: treat it like a
                // missing segment (IN_SEG false, NOT_IN_SEG true), matching sdk-go.
                if (string.Equals(segKey, currentKey, StringComparison.Ordinal)
                    || (segPath is not null && Array.IndexOf(segPath, segKey) >= 0))
                {
                    return SegmentResolverResult.NotFound;
                }
                var seg = _store.Get(segKey);
                if (seg is null) return SegmentResolverResult.NotFound;
                var childPath = Append(segPath, currentKey);
                var subMatch = EvaluateRow(GetOrParse(seg), contexts, "", childPath);
                if (!subMatch.IsMatch || subMatch.Value is null) return SegmentResolverResult.NotFound;
                return SegmentResolverResult.FromValue(subMatch.Value.Payload is bool b && b);
            };
        }

        var result = Operators.EvaluateCriterion(rawValue, lookup.Exists, criterion, segResolver, out var regexTimedOut);
        if (regexTimedOut) OnRegexTimeout?.Invoke(currentKey, criterion.PropertyName ?? "");
        return result;
    }

    // The key config is evaluated in the same environment as the config being decrypted, so an
    // environment-scoped key rule applies (sdk-go runtime_resolver.go; qfg-goi1.2.14).
    private Value? ResolveKey(string configKey, ContextSet contexts, string? environmentId)
    {
        if (_store is null) return null;
        var keyConfig = _store.Get(configKey);
        if (keyConfig is null) return null;
        // Get the unresolved candidate value from the matching rule; the outer Resolver call
        // will recurse to handle PROVIDED on the key config itself.
        var row = GetOrParse(keyConfig);
        var match = EvaluateMatchedRuleRaw(row, contexts, environmentId);
        return match;
    }

    private Value? EvaluateMatchedRuleRaw(ConfigRow row, ContextSet contexts, string? environmentId)
    {
        if (!string.IsNullOrEmpty(environmentId))
        {
            var env = row.FindEnvironment(environmentId);
            if (env is not null)
            {
                var rawEnv = FindFirstMatchingRawValue(row, env.Rules, contexts);
                if (rawEnv is not null) return rawEnv;
            }
        }
        return FindFirstMatchingRawValue(row, row.DefaultRules, contexts);
    }

    private Value? FindFirstMatchingRawValue(ConfigRow row, IReadOnlyList<Rule> rules, ContextSet contexts)
    {
        foreach (var rule in rules)
        {
            if (AllCriteriaMatch(row.Key, rule.Criteria, contexts, null)) return rule.Value;
        }
        return null;
    }

    private static string[] Append(string[]? path, string key)
    {
        if (path is null) return new[] { key };
        var next = new string[path.Length + 1];
        Array.Copy(path, next, path.Length);
        next[path.Length] = key;
        return next;
    }

    // GetValue is atomic: concurrent first evaluations may each parse, but all get the one row
    // that was stored. TryGetValue + Add threw on the losing threads (qfg-xmuj).
    private ConfigRow GetOrParse(ConfigResponse response) =>
        _parsed.GetValue(response, ConfigRowParser.Parse);
}
