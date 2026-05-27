// AUTO-GENERATED from integration-test-data/tests/eval/get_weighted_values.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public class GetWeightedValuesTests
{

    [Fact(DisplayName = "weighted value is consistent 1")]
    public void WeightedValueIsConsistent1()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted", TestSetup.Map("user", TestSetup.Map("tracking_id", "a72c15f5")));
        Assert.Equal(1L, actual);
    }

    [Fact(DisplayName = "weighted value is consistent 2")]
    public void WeightedValueIsConsistent2()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted", TestSetup.Map("user", TestSetup.Map("tracking_id", "92a202f2")));
        Assert.Equal(2L, actual);
    }

    [Fact(DisplayName = "weighted value is consistent 3")]
    public void WeightedValueIsConsistent3()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted", TestSetup.Map("user", TestSetup.Map("tracking_id", "8f414100")));
        Assert.Equal(3L, actual);
    }
}
