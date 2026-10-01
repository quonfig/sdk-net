// AUTO-GENERATED from integration-test-data/tests/eval/enabled_with_contexts.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using System;
using System.Threading.Tasks;
using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public sealed class EnabledWithContextsTests
{

    [Fact(DisplayName = "returns true from global context")]
    public async Task ReturnsTrueFromGlobalContext()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { [""] = new ContextProperties { ["domain"] = "prefab.cloud" }, ["user"] = new ContextProperties { ["key"] = "michael" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-seg.segment-and");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false due to local context override")]
    public async Task ReturnsFalseDueToLocalContextOverride()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { [""] = new ContextProperties { ["domain"] = "prefab.cloud" }, ["user"] = new ContextProperties { ["key"] = "michael" } })
            .WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "james" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-seg.segment-and");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for untouched scope context")]
    public async Task ReturnsFalseForUntouchedScopeContext()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { [""] = new ContextProperties { ["domain"] = "example.com" }, ["user"] = new ContextProperties { ["key"] = "nobody" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-seg.segment-and");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false due to partial scope context override of user.key")]
    public async Task ReturnsFalseDueToPartialScopeContextOverrideOfUserKey()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { [""] = new ContextProperties { ["domain"] = "example.com" }, ["user"] = new ContextProperties { ["key"] = "nobody" } })
            .WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "michael" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-seg.segment-and");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false due to partial scope context override of domain")]
    public async Task ReturnsFalseDueToPartialScopeContextOverrideOfDomain()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { [""] = new ContextProperties { ["domain"] = "example.com" }, ["user"] = new ContextProperties { ["key"] = "nobody" } })
            .WithContext(new ContextSet { [""] = new ContextProperties { ["domain"] = "prefab.cloud" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-seg.segment-and");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true due to local override of domain when scope user.key already matches")]
    public async Task ReturnsTrueDueToLocalOverrideOfDomainWhenScopeUserKeyAlreadyMatches()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { [""] = new ContextProperties { ["domain"] = "example.com" }, ["user"] = new ContextProperties { ["key"] = "michael" } })
            .WithContext(new ContextSet { [""] = new ContextProperties { ["domain"] = "prefab.cloud" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-seg.segment-and");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true due to full scope context override of user.key and domain")]
    public async Task ReturnsTrueDueToFullScopeContextOverrideOfUserKeyAndDomain()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { [""] = new ContextProperties { ["domain"] = "example.com" }, ["user"] = new ContextProperties { ["key"] = "nobody" } })
            .WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "michael" }, [""] = new ContextProperties { ["domain"] = "prefab.cloud" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-seg.segment-and");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for rule with different case on context property name")]
    public async Task ReturnsFalseForRuleWithDifferentCaseOnContextPropertyName()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("mixed.case.property.name", new ContextSet { ["user"] = new ContextProperties { ["IsHuman"] = "verified" } });
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for matching case on context property name")]
    public async Task ReturnsTrueForMatchingCaseOnContextPropertyName()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("mixed.case.property.name", new ContextSet { ["user"] = new ContextProperties { ["isHuman"] = "verified" } });
        Assert.True(actual);
    }
}
