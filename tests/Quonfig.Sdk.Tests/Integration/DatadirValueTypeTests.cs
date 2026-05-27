// AUTO-GENERATED from integration-test-data/tests/eval/datadir_value_type.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public class DatadirValueTypeTests
{

    [Fact(DisplayName = "datadir int config value is loaded as a number, not a string")]
    public void DatadirIntConfigValueIsLoadedAsANumberNotAString()
    {
        object? actual = TestSetup.DatadirGet(TestSetup.Map("datadir", TestSetup.DATADIR, "environment", "Production"), "brand.new.int");
        Assert.Equal(123L, actual);
        TestSetup.AssertRawValueNumeric(TestSetup.Map("datadir", TestSetup.DATADIR, "environment", "Production"), "brand.new.int");
    }

    [Fact(DisplayName = "datadir double config value is loaded as a number, not a string")]
    public void DatadirDoubleConfigValueIsLoadedAsANumberNotAString()
    {
        object? actual = TestSetup.DatadirGet(TestSetup.Map("datadir", TestSetup.DATADIR, "environment", "Production"), "my-double-key");
        TestSetup.AssertDoubleEquals(9.95d, actual);
        TestSetup.AssertRawValueNumeric(TestSetup.Map("datadir", TestSetup.DATADIR, "environment", "Production"), "my-double-key");
    }
}
