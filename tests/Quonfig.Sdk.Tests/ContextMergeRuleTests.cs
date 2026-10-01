using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Quonfig.Sdk.Wire;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// Pins the documented context-merge rule (qfg-2agi.24 / qfg-2agi.38): a higher-precedence tier's
/// named context REPLACES the whole same-named context from a lower tier, and named contexts it does
/// not mention survive. Every case uses disjoint attributes, so a property-by-property merge (which
/// would keep <c>user.email</c> after a <c>user</c> overlay that only sets <c>plan</c>) turns these red.
/// Covered tiers: global + per-call, global + bound, nested bound, and the dev-only
/// <c>quonfig-user</c> context merged under the customer's global context.
/// </summary>
public sealed class ContextMergeRuleTests : IDisposable
{
    private const string UserEmailKey = "probe.user-email";
    private const string UserPlanKey = "probe.user-plan";
    private const string TeamIdKey = "probe.team-id";
    private const string DevEmailKey = "probe.dev-email";

    private readonly string _tmpHome;

    public ContextMergeRuleTests()
    {
        _tmpHome = Path.Combine(Path.GetTempPath(), "quonfig-merge-rule-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_tmpHome, ".quonfig"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmpHome, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A bool flag that is true only when <paramref name="property"/> equals <paramref name="value"/>.</summary>
    private static string ProbeFlag(string id, string key, string property, string value) => $$"""
        {
          "id": "{{id}}",
          "key": "{{key}}",
          "type": "feature_flag",
          "valueType": "bool",
          "sendToClientSdk": false,
          "environments": [
            {
              "id": "Production",
              "rules": [
                {
                  "criteria": [
                    {
                      "propertyName": "{{property}}",
                      "operator": "PROP_IS_ONE_OF",
                      "valueToMatch": { "type": "string_list", "value": ["{{value}}"] }
                    }
                  ],
                  "value": { "type": "bool", "value": true }
                },
                { "criteria": [{ "operator": "ALWAYS_TRUE" }], "value": { "type": "bool", "value": false } }
              ]
            }
          ],
          "default": {
            "rules": [
              { "criteria": [{ "operator": "ALWAYS_TRUE" }], "value": { "type": "bool", "value": false } }
            ]
          }
        }
        """;

    private static ConfigEnvelope Envelope()
    {
        var configs = new List<JsonElement>();
        foreach (var json in new[]
                 {
                     ProbeFlag("17779999990101", UserEmailKey, "user.email", "a@x.com"),
                     ProbeFlag("17779999990102", UserPlanKey, "user.plan", "pro"),
                     ProbeFlag("17779999990103", TeamIdKey, "team.id", "t1"),
                     ProbeFlag("17779999990104", DevEmailKey, "quonfig-user.email", "dev@x.com"),
                 })
        {
            using var doc = JsonDocument.Parse(json);
            configs.Add(doc.RootElement.Clone());
        }
        return new ConfigEnvelope(configs, new Meta("test", "Production", null));
    }

    private Quonfig Build(ContextSet? globalContext, bool devContext = false) =>
        new Quonfig(new QuonfigOptions
        {
            DatafileEnvelope = Envelope(),
            Environment = "Production",
            EnableQuonfigUserContext = devContext,
            GlobalContext = globalContext,
            EnvLookup = name => name == "QUONFIG_CONFIG_HOME" ? _tmpHome : null,
        });

    private static ContextSet Ctx(string name, string prop, string value, string? name2 = null, string? prop2 = null, string? value2 = null)
    {
        var set = new ContextSet
        {
            [name] = new ContextProperties { [prop] = new ContextValueString(value) },
        };
        if (name2 is not null) set[name2] = new ContextProperties { [prop2!] = new ContextValueString(value2!) };
        return set;
    }

    // Lower tier: user.email + team.id. Higher tier: user.plan only.
    private static ContextSet Lower() => Ctx("user", "email", "a@x.com", "team", "id", "t1");
    private static ContextSet Higher() => Ctx("user", "plan", "pro");

    [Fact]
    public void GlobalPlusPerCall_ReplacesWholeNamedContext_OthersSurvive()
    {
        var q = Build(Lower());
        var jit = Higher();

        Assert.Equal(true, q.GetBool(UserPlanKey, jit));
        Assert.Equal(false, q.GetBool(UserEmailKey, jit)); // global user.email dropped with the whole 'user'
        Assert.Equal(true, q.GetBool(TeamIdKey, jit)); // 'team' not mentioned per-call -> survives
    }

    [Fact]
    public void GlobalPlusBound_ReplacesWholeNamedContext_OthersSurvive()
    {
        var bound = Build(Lower()).WithContext(Higher());

        Assert.Equal(true, bound.GetBool(UserPlanKey));
        Assert.Equal(false, bound.GetBool(UserEmailKey));
        Assert.Equal(true, bound.GetBool(TeamIdKey));
    }

    [Fact]
    public void NestedBound_ReplacesWholeNamedContext_OthersSurvive()
    {
        var nested = Build(null).WithContext(Lower()).WithContext(Higher());

        Assert.Equal(true, nested.GetBool(UserPlanKey));
        Assert.Equal(false, nested.GetBool(UserEmailKey));
        Assert.Equal(true, nested.GetBool(TeamIdKey));
    }

    [Fact]
    public void DevContext_CustomerQuonfigUserContext_ReplacesWholeDevContext()
    {
        File.WriteAllText(Path.Combine(_tmpHome, ".quonfig", "tokens.json"),
            JsonSerializer.Serialize(new { userEmail = "dev@x.com" }));

        // Customer supplies a quonfig-user context with a DIFFERENT attribute (no email): it replaces
        // the dev context wholesale, so the token file's email no longer reaches evaluation.
        var q = Build(Ctx("quonfig-user", "name", "someone"), devContext: true);

        Assert.Equal(false, q.GetBool(DevEmailKey));
    }

    [Fact]
    public void DevContext_SurvivesCustomerGlobalContextThatDoesNotNameIt()
    {
        File.WriteAllText(Path.Combine(_tmpHome, ".quonfig", "tokens.json"),
            JsonSerializer.Serialize(new { userEmail = "dev@x.com" }));

        var q = Build(Lower(), devContext: true);

        Assert.Equal(true, q.GetBool(DevEmailKey));
        Assert.Equal(true, q.GetBool(UserEmailKey));
    }
}
