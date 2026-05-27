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
        _resolver = resolver ?? new Resolver(keyResolver: ResolveKey);
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
        return EvaluateRow(row, contexts, environmentId);
    }

    private EvaluationMatch EvaluateRow(ConfigRow row, ContextSet contexts, string? environmentId)
    {
        if (!string.IsNullOrEmpty(environmentId))
        {
            var env = row.FindEnvironment(environmentId);
            if (env is not null)
            {
                var m = EvaluateRules(row, env.Rules, contexts);
                if (m is not null) return m;
            }
        }

        var def = EvaluateRules(row, row.DefaultRules, contexts);
        if (def is not null) return def;

        return EvaluationMatch.NoMatch(row.Id, row.Key, row.ValueType);
    }

    private EvaluationMatch? EvaluateRules(ConfigRow row, IReadOnlyList<Rule> rules, ContextSet contexts)
    {
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (!AllCriteriaMatch(rule.Criteria, contexts)) continue;

            // Resolver expands PROVIDED, weighted-buckets, decryption, env-var coercion.
            // It throws on env-var-missing / decryption failure — let it propagate so the
            // caller can surface the right error code.
            var resolved = _resolver.Resolve(rule.Value, row.Key, row.ValueType, contexts);

            // STATIC only when the very first rule has no criteria (matches sdk-java spec
            // — see EvaluatorTest.evaluate_fallsThroughToFallbackRule_whenFirstDoesNotMatch).
            Reason reason = (i == 0 && rule.Criteria.Count == 0) || rule.Criteria.Count == 0
                ? Reason.Static
                : Reason.TargetingMatch;

            return EvaluationMatch.Matched(resolved, i, reason, row.Id, row.Key, row.ValueType);
        }
        return null;
    }

    private bool AllCriteriaMatch(IReadOnlyList<Criterion> criteria, ContextSet contexts)
    {
        foreach (var c in criteria)
        {
            if (!EvaluateOne(c, contexts)) return false;
        }
        return true;
    }

    private bool EvaluateOne(Criterion criterion, ContextSet contexts)
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
                var seg = _store.Get(segKey);
                if (seg is null) return SegmentResolverResult.NotFound;
                var subMatch = Evaluate(seg, contexts, "");
                if (!subMatch.IsMatch || subMatch.Value is null) return SegmentResolverResult.NotFound;
                return SegmentResolverResult.FromValue(subMatch.Value.Payload is bool b && b);
            };
        }

        return Operators.EvaluateCriterion(rawValue, lookup.Exists, criterion, segResolver);
    }

    private Value? ResolveKey(string configKey, ContextSet contexts)
    {
        if (_store is null) return null;
        var keyConfig = _store.Get(configKey);
        if (keyConfig is null) return null;
        // Get the unresolved candidate value from the matching rule; the outer Resolver call
        // will recurse to handle PROVIDED on the key config itself.
        var row = GetOrParse(keyConfig);
        var match = EvaluateMatchedRuleRaw(row, contexts, "");
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
            if (AllCriteriaMatch(rule.Criteria, contexts)) return rule.Value;
        }
        return null;
    }

    private ConfigRow GetOrParse(ConfigResponse response)
    {
        if (_parsed.TryGetValue(response, out var existing)) return existing;
        var parsed = ConfigRowParser.Parse(response);
        _parsed.Add(response, parsed);
        return parsed;
    }
}
