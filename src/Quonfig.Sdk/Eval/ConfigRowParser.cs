using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Xml;

namespace Quonfig.Sdk.Eval;

/// <summary>
/// Parses a wire <see cref="ConfigResponse"/> into the strongly-typed <see cref="ConfigRow"/>
/// the evaluator operates on. JSON traversal happens once per row update — never on the hot
/// evaluation path. Mirrors sdk-java's wire-to-domain mapper.
///
/// <para>Unknown enum values fall back to the most permissive option (Config / Unknown
/// ValueType) so a forward-compatible wire format never crashes the SDK on a fresh field
/// — the eval engine then either matches or fails closed.</para>
/// </summary>
public static class ConfigRowParser
{
    /// <summary>Parses the given config response into a typed <see cref="ConfigRow"/>.</summary>
    /// <exception cref="JsonException">If the JSON shape doesn't match the cross-SDK schema.</exception>
    public static ConfigRow Parse(ConfigResponse response)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(response);
#else
        if (response is null) throw new ArgumentNullException(nameof(response));
#endif
        return ParseRow(response.Key, response.Raw);
    }

    /// <summary>Parses a single config row from its raw JSON element.</summary>
    public static ConfigRow ParseRow(string key, JsonElement el)
    {
        string id = TryGetString(el, "id") ?? "";
        var typeStr = TryGetString(el, "type");
        var valueTypeStr = TryGetString(el, "valueType");

        var defaultRules = ParseRulesContainer(el, "default");
        var environments = ParseEnvironments(el);

        return new ConfigRow(
            id,
            key,
            ParseConfigType(typeStr),
            ParseValueType(valueTypeStr),
            defaultRules,
            environments);
    }

    private static IReadOnlyList<Rule> ParseRulesContainer(JsonElement el, string fieldName)
    {
        if (!el.TryGetProperty(fieldName, out var container) || container.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<Rule>();
        }
        if (!container.TryGetProperty("rules", out var rulesEl) || rulesEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Rule>();
        }
        var rules = new List<Rule>(rulesEl.GetArrayLength());
        foreach (var ruleEl in rulesEl.EnumerateArray())
        {
            rules.Add(ParseRule(ruleEl));
        }
        return rules;
    }

    private static IReadOnlyList<EvaluationEnvironment> ParseEnvironments(JsonElement el)
    {
        if (!el.TryGetProperty("environments", out var envs) || envs.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<EvaluationEnvironment>();
        }
        var result = new List<EvaluationEnvironment>(envs.GetArrayLength());
        foreach (var envEl in envs.EnumerateArray())
        {
            var envId = TryGetString(envEl, "id") ?? "";
            var rules = new List<Rule>();
            if (envEl.TryGetProperty("rules", out var rulesEl) && rulesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var ruleEl in rulesEl.EnumerateArray())
                {
                    rules.Add(ParseRule(ruleEl));
                }
            }
            result.Add(new EvaluationEnvironment(envId, rules));
        }
        return result;
    }

    private static Rule ParseRule(JsonElement el)
    {
        var criteria = new List<Criterion>();
        if (el.TryGetProperty("criteria", out var critsEl) && critsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var critEl in critsEl.EnumerateArray())
            {
                criteria.Add(ParseCriterion(critEl));
            }
        }
        Value value = el.TryGetProperty("value", out var valueEl)
            ? ParseValue(valueEl)
            : new Value(ValueType.String, null);
        return new Rule(criteria, value);
    }

    private static Criterion ParseCriterion(JsonElement el)
    {
        var propertyName = TryGetString(el, "propertyName");
        var op = TryGetString(el, "operator") ?? "";
        Value? valueToMatch = null;
        if (el.TryGetProperty("valueToMatch", out var vtm) && vtm.ValueKind == JsonValueKind.Object)
        {
            valueToMatch = ParseValue(vtm);
        }
        return new Criterion(propertyName, op, valueToMatch);
    }

    /// <summary>
    /// Parses a wire <c>{"type":"&lt;tag&gt;","value":...}</c> object into a <see cref="Value"/>.
    /// Public so other callers (the resolver, tests) can lift a JSON node without re-implementing
    /// the type dispatch.
    /// </summary>
    public static Value ParseValue(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            // Treat a bare scalar as a string-typed value (defensive).
            return new Value(ValueType.String, el.ToString());
        }

        var typeStr = TryGetString(el, "type") ?? "";
        bool confidential = el.TryGetProperty("confidential", out var confEl)
            && confEl.ValueKind == JsonValueKind.True;
        string? decryptWith = TryGetString(el, "decryptWith");
        if (string.IsNullOrEmpty(decryptWith)) decryptWith = null;

        if (!el.TryGetProperty("value", out var valueEl))
        {
            return new Value(ParseValueType(typeStr), null, confidential, decryptWith);
        }

        switch (typeStr)
        {
            case "bool":
                return new Value(ValueType.Bool, valueEl.ValueKind == JsonValueKind.True, confidential, decryptWith);
            case "int":
            case "long":
                if (valueEl.ValueKind == JsonValueKind.Number && valueEl.TryGetInt64(out var l)) return new Value(ValueType.Int, l, confidential, decryptWith);
                if (valueEl.ValueKind == JsonValueKind.String && long.TryParse(valueEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ls)) return new Value(ValueType.Int, ls, confidential, decryptWith);
                return new Value(ValueType.Int, 0L, confidential, decryptWith);
            case "double":
                if (valueEl.ValueKind == JsonValueKind.Number) return new Value(ValueType.Double, valueEl.GetDouble(), confidential, decryptWith);
                if (valueEl.ValueKind == JsonValueKind.String && double.TryParse(valueEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return new Value(ValueType.Double, d, confidential, decryptWith);
                return new Value(ValueType.Double, 0d, confidential, decryptWith);
            case "string":
                return new Value(ValueType.String, valueEl.GetString() ?? "", confidential, decryptWith);
            case "string_list":
                return new Value(ValueType.StringList, ParseStringList(valueEl), confidential, decryptWith);
            case "log_level":
                return new Value(ValueType.LogLevel, valueEl.GetString() ?? "", confidential, decryptWith);
            case "duration":
                {
                    var s = valueEl.GetString();
                    if (string.IsNullOrEmpty(s))
                    {
                        return new Value(ValueType.Duration, TimeSpan.Zero, confidential, decryptWith);
                    }
                    try
                    {
                        return new Value(ValueType.Duration, XmlConvert.ToTimeSpan(s!), confidential, decryptWith);
                    }
                    catch (FormatException)
                    {
                        return new Value(ValueType.Duration, TimeSpan.Zero, confidential, decryptWith);
                    }
                }
            case "json":
                return new Value(ValueType.Json, ConvertJson(valueEl), confidential, decryptWith);
            case "int_range":
                // Criterion-only type. Modeled as ValueType.Json with a {start,end} dictionary —
                // the IN_INT_RANGE operator reads it that way.
                return new Value(ValueType.Json, ConvertJson(valueEl), confidential, decryptWith);
            case "weighted_values":
                return new Value(ValueType.WeightedValues, ParseWeighted(valueEl), confidential, decryptWith);
            case "provided":
                return new Value(ValueType.Provided, ParseProvided(valueEl), confidential, decryptWith);
            default:
                // Unknown wire type — store the raw JSON element so callers can introspect.
                return new Value(ValueType.String, ConvertJson(valueEl), confidential, decryptWith);
        }
    }

    private static IReadOnlyList<string> ParseStringList(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>(el.GetArrayLength());
        foreach (var item in el.EnumerateArray())
        {
            list.Add(item.GetString() ?? "");
        }
        return list;
    }

    private static WeightedValuesPayload ParseWeighted(JsonElement el)
    {
        string? hashBy = null;
        var variants = new List<WeightedVariant>();
        if (el.ValueKind == JsonValueKind.Object)
        {
            hashBy = TryGetString(el, "hashByPropertyName");
            if (el.TryGetProperty("weightedValues", out var wvs) && wvs.ValueKind == JsonValueKind.Array)
            {
                foreach (var wvEl in wvs.EnumerateArray())
                {
                    int weight = 0;
                    if (wvEl.TryGetProperty("weight", out var wEl) && wEl.ValueKind == JsonValueKind.Number)
                    {
                        weight = wEl.GetInt32();
                    }
                    Value inner = wvEl.TryGetProperty("value", out var vEl)
                        ? ParseValue(vEl)
                        : new Value(ValueType.String, null);
                    variants.Add(new WeightedVariant(weight, inner));
                }
            }
        }
        return new WeightedValuesPayload(string.IsNullOrEmpty(hashBy) ? null : hashBy, variants);
    }

    private static ProvidedValue ParseProvided(JsonElement el)
    {
        var source = TryGetString(el, "source") ?? "ENV_VAR";
        var lookup = TryGetString(el, "lookup") ?? "";
        return new ProvidedValue(source, lookup);
    }

    private static ConfigType ParseConfigType(string? s) => s switch
    {
        "config" => ConfigType.Config,
        "feature_flag" => ConfigType.FeatureFlag,
        "segment" => ConfigType.Segment,
        _ => ConfigType.Unknown,
    };

    private static ValueType ParseValueType(string? s) => s switch
    {
        "bool" => ValueType.Bool,
        "int" => ValueType.Int,
        "long" => ValueType.Int,
        "double" => ValueType.Double,
        "string" => ValueType.String,
        "string_list" => ValueType.StringList,
        "log_level" => ValueType.LogLevel,
        "duration" => ValueType.Duration,
        "json" => ValueType.Json,
        "weighted_values" => ValueType.WeightedValues,
        "provided" => ValueType.Provided,
        _ => ValueType.String,
    };

    private static string? TryGetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static object? ConvertJson(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var prop in el.EnumerateObject())
                {
                    obj[prop.Name] = ConvertJson(prop.Value);
                }
                return obj;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in el.EnumerateArray()) list.Add(ConvertJson(item));
                return list;
            case JsonValueKind.String:
                return el.GetString();
            case JsonValueKind.Number:
                if (el.TryGetInt64(out long ll)) return ll;
                return el.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                return null;
        }
    }
}
