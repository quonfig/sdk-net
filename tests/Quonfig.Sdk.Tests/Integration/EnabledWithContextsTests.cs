// AUTO-GENERATED from integration-test-data/tests/eval/enabled_with_contexts.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public class EnabledWithContextsTests
{

    [Fact(DisplayName = "returns true from global context")]
    public void ReturnsTrueFromGlobalContext()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-seg.segment-and", TestSetup.Map("", TestSetup.Map("domain", "prefab.cloud"), "user", TestSetup.Map("key", "michael")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false due to local context override")]
    public void ReturnsFalseDueToLocalContextOverride()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-seg.segment-and", TestSetup.Map("", TestSetup.Map("domain", "prefab.cloud"), "user", TestSetup.Map("key", "james")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for untouched scope context")]
    public void ReturnsFalseForUntouchedScopeContext()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-seg.segment-and", TestSetup.Map("", TestSetup.Map("domain", "example.com"), "user", TestSetup.Map("key", "nobody")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false due to partial scope context override of user.key")]
    public void ReturnsFalseDueToPartialScopeContextOverrideOfUserKey()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-seg.segment-and", TestSetup.Map("", TestSetup.Map("domain", "example.com"), "user", TestSetup.Map("key", "michael")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false due to partial scope context override of domain")]
    public void ReturnsFalseDueToPartialScopeContextOverrideOfDomain()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-seg.segment-and", TestSetup.Map("", TestSetup.Map("domain", "example.com", "key", "prefab.cloud"), "user", TestSetup.Map("key", "nobody")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true due to full scope context override of user.key and domain")]
    public void ReturnsTrueDueToFullScopeContextOverrideOfUserKeyAndDomain()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-seg.segment-and", TestSetup.Map("", TestSetup.Map("domain", "prefab.cloud"), "user", TestSetup.Map("key", "michael")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for rule with different case on context property name")]
    public void ReturnsFalseForRuleWithDifferentCaseOnContextPropertyName()
    {
        object? actual = TestSetup.EnabledCase("mixed.case.property.name", TestSetup.Map("user", TestSetup.Map("IsHuman", "verified")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for matching case on context property name")]
    public void ReturnsTrueForMatchingCaseOnContextPropertyName()
    {
        object? actual = TestSetup.EnabledCase("mixed.case.property.name", TestSetup.Map("user", TestSetup.Map("isHuman", "verified")));
        Assert.Equal(true, actual);
    }
}
