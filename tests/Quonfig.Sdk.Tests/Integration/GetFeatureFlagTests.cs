// AUTO-GENERATED from integration-test-data/tests/eval/get_feature_flag.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public class GetFeatureFlagTests
{

    [Fact(DisplayName = "get returns the underlying value for a feature flag")]
    public void GetReturnsTheUnderlyingValueForAFeatureFlag()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.integer", TestSetup.Map());
        Assert.Equal(3L, actual);
    }

    [Fact(DisplayName = "get returns the underlying value for a feature flag that matches the highest precedent rule")]
    public void GetReturnsTheUnderlyingValueForAFeatureFlagThatMatchesTheHighestPrecedentRule()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.integer", TestSetup.Map("user", TestSetup.Map("key", "michael")));
        Assert.Equal(5L, actual);
    }
}
