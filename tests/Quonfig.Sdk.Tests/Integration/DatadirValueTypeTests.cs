// AUTO-GENERATED from integration-test-data/tests/eval/datadir_value_type.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using System;
using System.Threading.Tasks;
using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public sealed class DatadirValueTypeTests
{

    [Fact(DisplayName = "datadir int config value is loaded as a number, not a string")]
    public async Task DatadirIntConfigValueIsLoadedAsANumberNotAString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = "Production",
        });
        var actual = client.GetLong("brand.new.int");
        Assert.Equal(123L, actual);
        TestSetup.AssertLoadedValueNumeric("Production", "brand.new.int");
    }

    [Fact(DisplayName = "datadir double config value is loaded as a number, not a string")]
    public async Task DatadirDoubleConfigValueIsLoadedAsANumberNotAString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = "Production",
        });
        var actual = client.GetDouble("my-double-key");
        TestSetup.AssertDoubleEquals(9.95d, actual);
        TestSetup.AssertLoadedValueNumeric("Production", "my-double-key");
    }
}
