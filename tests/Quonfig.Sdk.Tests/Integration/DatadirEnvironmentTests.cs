// AUTO-GENERATED from integration-test-data/tests/eval/datadir_environment.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using System;
using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public class DatadirEnvironmentTests
{

    [Fact(DisplayName = "datadir with environment option gets environment-specific value")]
    public void DatadirWithEnvironmentOptionGetsEnvironmentSpecificValue()
    {
        object? actual = TestSetup.DatadirGet(TestSetup.Map("datadir", TestSetup.DATADIR, "environment", "Production"), "james.test.key");
        Assert.Equal("test4", actual);
    }

    [Fact(DisplayName = "datadir with QUONFIG_ENVIRONMENT env var gets environment-specific value")]
    public void DatadirWithQuonfigEnvironmentEnvVarGetsEnvironmentSpecificValue()
    {
        TestSetup.WithEnv(TestSetup.Map("QUONFIG_ENVIRONMENT", "Production"), () =>
        {
            object? actual = TestSetup.DatadirGet(TestSetup.Map("datadir", TestSetup.DATADIR), "james.test.key");
            Assert.Equal("test4", actual);
        });
    }

    [Fact(DisplayName = "environment option supersedes QUONFIG_ENVIRONMENT env var")]
    public void EnvironmentOptionSupersedesQuonfigEnvironmentEnvVar()
    {
        TestSetup.WithEnv(TestSetup.Map("QUONFIG_ENVIRONMENT", "nonexistent"), () =>
        {
            object? actual = TestSetup.DatadirGet(TestSetup.Map("datadir", TestSetup.DATADIR, "environment", "Production"), "james.test.key");
            Assert.Equal("test4", actual);
        });
    }

    [Fact(DisplayName = "config without environment override returns default value")]
    public void ConfigWithoutEnvironmentOverrideReturnsDefaultValue()
    {
        object? actual = TestSetup.DatadirGet(TestSetup.Map("datadir", TestSetup.DATADIR, "environment", "Production"), "config.with.only.default.env.row");
        Assert.Equal("hello from no env row", actual);
    }

    [Fact(DisplayName = "datadir without environment fails to init")]
    public void DatadirWithoutEnvironmentFailsToInit()
    {
        Assert.Throws<InvalidOperationException>(() => TestSetup.DatadirClient(TestSetup.Map("datadir", TestSetup.DATADIR)));
    }

    [Fact(DisplayName = "datadir with invalid environment fails to init")]
    public void DatadirWithInvalidEnvironmentFailsToInit()
    {
        Assert.Throws<InvalidOperationException>(() => TestSetup.DatadirClient(TestSetup.Map("datadir", TestSetup.DATADIR, "environment", "nonexistent")));
    }
}
