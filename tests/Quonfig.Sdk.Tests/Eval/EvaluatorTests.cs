using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Quonfig.Sdk.Eval;
using Quonfig.Sdk.Wire;
using Xunit;
using ValueType = Quonfig.Sdk.Eval.ValueType;

namespace Quonfig.Sdk.Tests.Eval;

/// <summary>
/// Rule-evaluation contract — case-for-case port of sdk-java's <c>EvaluatorTest</c>. Each test
/// builds a minimal config row directly in JSON (the shape the wire emits) and asserts the
/// evaluator routes through environment-vs-default rules, AND criteria, segment recursion, and
/// weighted-value resolution exactly the way the sibling SDKs do.
/// </summary>
public sealed class EvaluatorTests
{
    private static ConfigResponse Parse(string key, string json)
    {
        var doc = JsonDocument.Parse(json);
        return new ConfigResponse(key, doc.RootElement.Clone());
    }

    private static ConfigStore StoreOf(params (string Key, string Json)[] rows)
    {
        var store = new ConfigStore();
        var elements = new List<JsonElement>();
        foreach (var (_, json) in rows)
        {
            elements.Add(JsonDocument.Parse(json).RootElement.Clone());
        }
        store.Update(new ConfigEnvelope(elements, null));
        return store;
    }

    [Fact]
    public void Evaluate_StaticReason_WhenFirstRuleHasNoCriteria()
    {
        var cfg = Parse("flag.x", """
            {
              "id": "1",
              "key": "flag.x",
              "type": "feature_flag",
              "valueType": "bool",
              "default": {
                "rules": [ { "criteria": [], "value": { "type": "bool", "value": true } } ]
              }
            }
            """);

        var ev = new Evaluator(null);
        var m = ev.Evaluate(cfg, new ContextSet(), "");

        m.IsMatch.Should().BeTrue();
        m.Value!.Payload.Should().Be(true);
        m.RuleIndex.Should().Be(0);
        m.Reason.Should().Be(Reason.Static);
        m.ConfigKey.Should().Be("flag.x");
        m.ConfigId.Should().Be("1");
    }

    [Fact]
    public void Evaluate_TargetingMatch_WhenCriteriaMatch()
    {
        var cfg = Parse("flag.internal", """
            {
              "key": "flag.internal",
              "type": "feature_flag",
              "valueType": "bool",
              "default": {
                "rules": [
                  {
                    "criteria": [
                      { "propertyName": "user.email", "operator": "PROP_ENDS_WITH_ONE_OF",
                        "valueToMatch": { "type": "string_list", "value": ["@quonfig.com"] } }
                    ],
                    "value": { "type": "bool", "value": true }
                  },
                  { "criteria": [], "value": { "type": "bool", "value": false } }
                ]
              }
            }
            """);

        var ev = new Evaluator(null);
        var ctx = new ContextSet { ["user"] = new ContextProperties() };
        ctx["user"]["email"] = "alice@quonfig.com";

        var m = ev.Evaluate(cfg, ctx, "");
        m.Value!.Payload.Should().Be(true);
        m.RuleIndex.Should().Be(0);
        m.Reason.Should().Be(Reason.TargetingMatch);
    }

    [Fact]
    public void Evaluate_FallsThroughToFallbackRule_WhenFirstDoesNotMatch()
    {
        var cfg = Parse("flag.internal", """
            {
              "key": "flag.internal",
              "type": "feature_flag",
              "valueType": "bool",
              "default": {
                "rules": [
                  {
                    "criteria": [
                      { "propertyName": "user.email", "operator": "PROP_ENDS_WITH_ONE_OF",
                        "valueToMatch": { "type": "string_list", "value": ["@quonfig.com"] } }
                    ],
                    "value": { "type": "bool", "value": true }
                  },
                  { "criteria": [], "value": { "type": "bool", "value": false } }
                ]
              }
            }
            """);

        var ev = new Evaluator(null);
        var ctx = new ContextSet { ["user"] = new ContextProperties() };
        ctx["user"]["email"] = "outsider@example.com";

        var m = ev.Evaluate(cfg, ctx, "");
        m.Value!.Payload.Should().Be(false);
        m.RuleIndex.Should().Be(1);
        m.Reason.Should().Be(Reason.Static);
    }

    [Fact]
    public void Evaluate_AndLogic_AcrossMultipleCriteria()
    {
        var cfg = Parse("flag.admin", """
            {
              "key": "flag.admin",
              "type": "feature_flag",
              "valueType": "bool",
              "default": {
                "rules": [
                  {
                    "criteria": [
                      { "propertyName": "user.email", "operator": "PROP_ENDS_WITH_ONE_OF",
                        "valueToMatch": { "type": "string_list", "value": ["@quonfig.com"] } },
                      { "propertyName": "user.role", "operator": "PROP_IS_ONE_OF",
                        "valueToMatch": { "type": "string_list", "value": ["admin"] } }
                    ],
                    "value": { "type": "bool", "value": true }
                  },
                  { "criteria": [], "value": { "type": "bool", "value": false } }
                ]
              }
            }
            """);

        var ev = new Evaluator(null);

        var ctxGood = new ContextSet { ["user"] = new ContextProperties() };
        ctxGood["user"]["email"] = "alice@quonfig.com";
        ctxGood["user"]["role"] = "admin";
        ev.Evaluate(cfg, ctxGood, "").Value!.Payload.Should().Be(true);

        var ctxPartial = new ContextSet { ["user"] = new ContextProperties() };
        ctxPartial["user"]["email"] = "alice@quonfig.com";
        ctxPartial["user"]["role"] = "user";
        ev.Evaluate(cfg, ctxPartial, "").Value!.Payload.Should().Be(false);
    }

    [Fact]
    public void EnvironmentRules_TakePrecedenceOverDefault()
    {
        var cfg = Parse("k.flag", """
            {
              "key": "k.flag",
              "type": "config",
              "valueType": "string",
              "environments": [
                {
                  "id": "env-1",
                  "rules": [ { "criteria": [], "value": { "type": "string", "value": "from-env" } } ]
                }
              ],
              "default": {
                "rules": [ { "criteria": [], "value": { "type": "string", "value": "from-default" } } ]
              }
            }
            """);

        var ev = new Evaluator(null);
        ev.Evaluate(cfg, new ContextSet(), "env-1").Value!.Payload.Should().Be("from-env");
        ev.Evaluate(cfg, new ContextSet(), "other-env").Value!.Payload.Should().Be("from-default");
        ev.Evaluate(cfg, new ContextSet(), "").Value!.Payload.Should().Be("from-default");
    }

    [Fact]
    public void EnvironmentRules_FallThroughToDefault_WhenNoEnvRuleMatches()
    {
        var cfg = Parse("k.flag", """
            {
              "key": "k.flag",
              "type": "config",
              "valueType": "string",
              "environments": [
                {
                  "id": "env-1",
                  "rules": [
                    {
                      "criteria": [
                        { "propertyName": "user.email", "operator": "PROP_IS_ONE_OF",
                          "valueToMatch": { "type": "string_list", "value": ["nobody@nowhere"] } }
                      ],
                      "value": { "type": "string", "value": "from-env" }
                    }
                  ]
                }
              ],
              "default": {
                "rules": [ { "criteria": [], "value": { "type": "string", "value": "from-default" } } ]
              }
            }
            """);

        var ev = new Evaluator(null);
        var m = ev.Evaluate(cfg, new ContextSet(), "env-1");
        m.Value!.Payload.Should().Be("from-default");
    }

    [Fact]
    public void SingularEnvironment_FromDeliveryWire_TakesPrecedenceOverDefault()
    {
        // api-delivery /api/v2/configs emits a SINGULAR "environment" object (the payload is
        // already scoped to the SDK key's environment) instead of the plural "environments"
        // array the datadir loader writes. The parser must read it; otherwise the env rules are
        // dropped and the evaluator silently serves the default value. (qfg-64m9)
        var cfg = Parse("flag.x", """
            {
              "id": "1",
              "key": "flag.x",
              "type": "feature_flag",
              "valueType": "bool",
              "environment": {
                "id": "development",
                "rules": [ { "criteria": [], "value": { "type": "bool", "value": false } } ]
              },
              "default": {
                "rules": [ { "criteria": [], "value": { "type": "bool", "value": true } } ]
              }
            }
            """);

        var ev = new Evaluator(null);
        // default is true, development override is false — the override must win.
        ev.Evaluate(cfg, new ContextSet(), "development").Value!.Payload.Should().Be(false);
    }

    [Fact]
    public void InSeg_ResolvesAnotherConfigAsBoolean()
    {
        // Segment config: true iff user.email ends with @quonfig.com
        const string segmentJson = """
            {
              "id": "10",
              "key": "seg.internal-emails",
              "type": "segment",
              "valueType": "bool",
              "default": {
                "rules": [
                  {
                    "criteria": [
                      { "propertyName": "user.email", "operator": "PROP_ENDS_WITH_ONE_OF",
                        "valueToMatch": { "type": "string_list", "value": ["@quonfig.com"] } }
                    ],
                    "value": { "type": "bool", "value": true }
                  },
                  { "criteria": [], "value": { "type": "bool", "value": false } }
                ]
              }
            }
            """;

        const string flagJson = """
            {
              "id": "20",
              "key": "flag.gated",
              "type": "feature_flag",
              "valueType": "bool",
              "default": {
                "rules": [
                  {
                    "criteria": [
                      { "operator": "IN_SEG",
                        "valueToMatch": { "type": "string", "value": "seg.internal-emails" } }
                    ],
                    "value": { "type": "bool", "value": true }
                  },
                  { "criteria": [], "value": { "type": "bool", "value": false } }
                ]
              }
            }
            """;

        var store = StoreOf(("seg.internal-emails", segmentJson), ("flag.gated", flagJson));
        var ev = new Evaluator(store);
        var flagCfg = store.Get("flag.gated")!;

        var insider = new ContextSet { ["user"] = new ContextProperties() };
        insider["user"]["email"] = "alice@quonfig.com";
        ev.Evaluate(flagCfg, insider, "").Value!.Payload.Should().Be(true);

        var outsider = new ContextSet { ["user"] = new ContextProperties() };
        outsider["user"]["email"] = "bob@example.com";
        ev.Evaluate(flagCfg, outsider, "").Value!.Payload.Should().Be(false);
    }

    [Fact]
    public void InSeg_SegmentMissing_ReturnsFalse_AndEvalFallsThrough()
    {
        var cfg = Parse("flag.gated", """
            {
              "key": "flag.gated",
              "type": "feature_flag",
              "valueType": "bool",
              "default": {
                "rules": [
                  {
                    "criteria": [
                      { "operator": "IN_SEG",
                        "valueToMatch": { "type": "string", "value": "seg.does-not-exist" } }
                    ],
                    "value": { "type": "bool", "value": true }
                  },
                  { "criteria": [], "value": { "type": "bool", "value": false } }
                ]
              }
            }
            """);

        var ev = new Evaluator(new ConfigStore());
        ev.Evaluate(cfg, new ContextSet(), "").Value!.Payload.Should().Be(false);
    }

    [Fact]
    public void Evaluate_ReturnsNoMatch_WhenAllRulesEmpty()
    {
        var cfg = Parse("k.empty", """
            {
              "key": "k.empty",
              "type": "config",
              "valueType": "string",
              "default": { "rules": [] }
            }
            """);
        var ev = new Evaluator(null);
        var m = ev.Evaluate(cfg, new ContextSet(), "");
        m.IsMatch.Should().BeFalse();
        m.Reason.Should().Be(Reason.Default);
        m.Value.Should().BeNull();
    }

    [Fact]
    public void WeightedValue_PassesThroughResolver_AndReturnsResolvedValue()
    {
        // Two weighted variants with non-zero weights; we'll target by user.id so Murmur3 picks
        // deterministically. The point of this test is to assert the Resolver hook actually fires
        // (the matched value is no longer ValueType.WeightedValues by the time we see it).
        var cfg = Parse("flag.coin", """
            {
              "key": "flag.coin",
              "type": "feature_flag",
              "valueType": "string",
              "default": {
                "rules": [
                  {
                    "criteria": [],
                    "value": {
                      "type": "weighted_values",
                      "value": {
                        "hashByPropertyName": "user.id",
                        "weightedValues": [
                          { "weight": 1, "value": { "type": "string", "value": "heads" } },
                          { "weight": 1, "value": { "type": "string", "value": "tails" } }
                        ]
                      }
                    }
                  }
                ]
              }
            }
            """);

        var ev = new Evaluator(null);
        var ctx = new ContextSet { ["user"] = new ContextProperties() };
        ctx["user"]["id"] = "alice";

        var m = ev.Evaluate(cfg, ctx, "");
        m.IsMatch.Should().BeTrue();
        m.Value!.Type.Should().Be(ValueType.String, "the resolver expanded the weighted variant");
        var picked = (string)m.Value.Payload!;
        picked.Should().BeOneOf("heads", "tails");
    }

    [Fact]
    public void MagicCurrentTimeProperty_IsResolvedAtCriterionEvaluation()
    {
        // PROP_BEFORE with quonfig.current-time should always be true for matchMillis in the future
        // and false for matchMillis in the past — no need to seed a context value.
        long inTheFuture = System.DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeMilliseconds();
        var cfg = Parse("flag.timed", $$"""
            {
              "key": "flag.timed",
              "type": "feature_flag",
              "valueType": "bool",
              "default": {
                "rules": [
                  {
                    "criteria": [
                      { "propertyName": "quonfig.current-time", "operator": "PROP_BEFORE",
                        "valueToMatch": { "type": "int", "value": {{inTheFuture}} } }
                    ],
                    "value": { "type": "bool", "value": true }
                  },
                  { "criteria": [], "value": { "type": "bool", "value": false } }
                ]
              }
            }
            """);
        var ev = new Evaluator(null);
        ev.Evaluate(cfg, new ContextSet(), "").Value!.Payload.Should().Be(true);
    }
}
