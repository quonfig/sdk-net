// AUTO-GENERATED from integration-test-data/tests/eval/get_feature_flag.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using System;
using System.Threading.Tasks;
using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public sealed class GetFeatureFlagTests
{

    [Fact(DisplayName = "get returns the underlying value for a feature flag")]
    public async Task GetReturnsTheUnderlyingValueForAFeatureFlag()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("feature-flag.integer");
        Assert.Equal(3L, actual);
    }

    [Fact(DisplayName = "get returns the underlying value for a feature flag that matches the highest precedent rule")]
    public async Task GetReturnsTheUnderlyingValueForAFeatureFlagThatMatchesTheHighestPrecedentRule()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("feature-flag.integer", new ContextSet { ["user"] = new ContextProperties { ["key"] = "michael" } });
        Assert.Equal(5L, actual);
    }
}
