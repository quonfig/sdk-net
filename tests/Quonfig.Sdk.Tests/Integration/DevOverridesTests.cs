// AUTO-GENERATED from integration-test-data/tests/eval/dev_overrides.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public class DevOverridesTests
{

    [Fact(DisplayName = "override fires when quonfig-user.email matches")]
    public void OverrideFiresWhenQuonfigUserEmailMatches()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.dev-override", TestSetup.Map("quonfig-user", TestSetup.Map("email", "bob@foo.com")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "override does not fire when attribute absent (prod simulation)")]
    public void OverrideDoesNotFireWhenAttributeAbsentProdSimulation()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.dev-override", TestSetup.Map("user", TestSetup.Map("email", "bob@foo.com")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "override matches any email in IS_ONE_OF list")]
    public void OverrideMatchesAnyEmailInIsOneOfList()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.dev-override.multi-email", TestSetup.Map("quonfig-user", TestSetup.Map("email", "alice@foo.com")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "override beats customer rule by priority")]
    public void OverrideBeatsCustomerRuleByPriority()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.dev-override.priority", TestSetup.Map("quonfig-user", TestSetup.Map("email", "bob@foo.com"), "user", TestSetup.Map("country", "DE")));
        Assert.Equal(true, actual);
    }
}
