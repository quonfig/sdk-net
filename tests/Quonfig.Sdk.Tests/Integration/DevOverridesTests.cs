// AUTO-GENERATED from integration-test-data/tests/eval/dev_overrides.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using System;
using System.Threading.Tasks;
using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public sealed class DevOverridesTests
{

    [Fact(DisplayName = "override fires when quonfig-user.email matches")]
    public async Task OverrideFiresWhenQuonfigUserEmailMatches()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["quonfig-user"] = new ContextProperties { ["email"] = "bob@foo.com" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.dev-override");
        Assert.True(actual);
    }

    [Fact(DisplayName = "override does not fire when attribute absent (prod simulation)")]
    public async Task OverrideDoesNotFireWhenAttributeAbsentProdSimulation()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["email"] = "bob@foo.com" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.dev-override");
        Assert.False(actual);
    }

    [Fact(DisplayName = "override matches any email in IS_ONE_OF list")]
    public async Task OverrideMatchesAnyEmailInIsOneOfList()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["quonfig-user"] = new ContextProperties { ["email"] = "alice@foo.com" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.dev-override.multi-email");
        Assert.True(actual);
    }

    [Fact(DisplayName = "override beats customer rule by priority")]
    public async Task OverrideBeatsCustomerRuleByPriority()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["quonfig-user"] = new ContextProperties { ["email"] = "bob@foo.com" }, ["user"] = new ContextProperties { ["country"] = "DE" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.dev-override.priority");
        Assert.True(actual);
    }
}
