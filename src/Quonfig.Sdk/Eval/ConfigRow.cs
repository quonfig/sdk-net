using System;
using System.Collections.Generic;

namespace Quonfig.Sdk.Eval;

/// <summary>
/// A single rule inside a config row's rule set: an ordered list of <see cref="Criterion"/>s
/// (AND-combined — all must match) plus the <see cref="Value"/> returned when they all match.
/// Mirrors sdk-java's <c>Rule</c>.
/// </summary>
public sealed class Rule
{
    /// <summary>The AND-combined criteria. Empty list means "always matches".</summary>
    public IReadOnlyList<Criterion> Criteria { get; }

    /// <summary>The value returned when all criteria match.</summary>
    public Value Value { get; }

    /// <summary>Initializes a new rule.</summary>
    public Rule(IReadOnlyList<Criterion> criteria, Value value)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(value);
#else
        if (criteria is null) throw new ArgumentNullException(nameof(criteria));
        if (value is null) throw new ArgumentNullException(nameof(value));
#endif
        Criteria = criteria;
        Value = value;
    }
}

/// <summary>
/// Per-environment rule override block. The evaluator looks up the matching block by
/// <see cref="Id"/> before falling through to the row's default rules.
/// </summary>
public sealed class EvaluationEnvironment
{
    /// <summary>The environment id (e.g. <c>"Production"</c>, <c>"Staging"</c>).</summary>
    public string Id { get; }

    /// <summary>The ordered rules tried top-to-bottom; first match wins.</summary>
    public IReadOnlyList<Rule> Rules { get; }

    /// <summary>Initializes a new environment block.</summary>
    public EvaluationEnvironment(string id, IReadOnlyList<Rule> rules)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(rules);
#else
        if (id is null) throw new ArgumentNullException(nameof(id));
        if (rules is null) throw new ArgumentNullException(nameof(rules));
#endif
        Id = id;
        Rules = rules;
    }
}

/// <summary>
/// Fully parsed shape of a single <see cref="ConfigResponse"/>. The evaluator works against this
/// — not the raw <see cref="System.Text.Json.JsonElement"/> — so the JSON traversal happens once
/// per row update rather than on every evaluation.
/// </summary>
public sealed class ConfigRow
{
    /// <summary>Stable id of the row (the <c>id</c> field on the wire; may be empty for legacy rows).</summary>
    public string Id { get; }

    /// <summary>The config name (the <c>key</c> field on the wire).</summary>
    public string Key { get; }

    /// <summary>Row category — config, feature flag, or segment.</summary>
    public ConfigType Type { get; }

    /// <summary>Wire-level value type the resolver will coerce env-var strings into.</summary>
    public ValueType ValueType { get; }

    /// <summary>Default rule set used when no env-specific block matches.</summary>
    public IReadOnlyList<Rule> DefaultRules { get; }

    /// <summary>Per-environment overrides. Order-preserving, but the evaluator looks up by id.</summary>
    public IReadOnlyList<EvaluationEnvironment> Environments { get; }

    /// <summary>Initializes a new parsed config row.</summary>
    public ConfigRow(
        string id,
        string key,
        ConfigType type,
        ValueType valueType,
        IReadOnlyList<Rule> defaultRules,
        IReadOnlyList<EvaluationEnvironment> environments)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(defaultRules);
        ArgumentNullException.ThrowIfNull(environments);
#else
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (defaultRules is null) throw new ArgumentNullException(nameof(defaultRules));
        if (environments is null) throw new ArgumentNullException(nameof(environments));
#endif
        Id = id ?? "";
        Key = key;
        Type = type;
        ValueType = valueType;
        DefaultRules = defaultRules;
        Environments = environments;
    }

    /// <summary>Returns the environment block matching <paramref name="envId"/>, or null.</summary>
    public EvaluationEnvironment? FindEnvironment(string? envId)
    {
        if (string.IsNullOrEmpty(envId)) return null;
        foreach (var env in Environments)
        {
            if (string.Equals(env.Id, envId, StringComparison.Ordinal)) return env;
        }
        return null;
    }
}
