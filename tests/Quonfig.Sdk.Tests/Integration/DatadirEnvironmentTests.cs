// AUTO-GENERATED from integration-test-data/tests/eval/datadir_environment.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using System;
using System.Threading.Tasks;
using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public sealed class DatadirEnvironmentTests
{

    [Fact(DisplayName = "datadir with environment option gets environment-specific value")]
    public async Task DatadirWithEnvironmentOptionGetsEnvironmentSpecificValue()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = "Production",
        });
        var actual = client.GetString("james.test.key");
        Assert.Equal("test4", actual);
    }

    [Fact(DisplayName = "datadir with QUONFIG_ENVIRONMENT env var gets environment-specific value")]
    public async Task DatadirWithQuonfigEnvironmentEnvVarGetsEnvironmentSpecificValue()
    {
        using var env = TestSetup.Env("QUONFIG_ENVIRONMENT", "Production");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
        });
        var actual = client.GetString("james.test.key");
        Assert.Equal("test4", actual);
    }

    [Fact(DisplayName = "environment option supersedes QUONFIG_ENVIRONMENT env var")]
    public async Task EnvironmentOptionSupersedesQuonfigEnvironmentEnvVar()
    {
        using var env = TestSetup.Env("QUONFIG_ENVIRONMENT", "nonexistent");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = "Production",
        });
        var actual = client.GetString("james.test.key");
        Assert.Equal("test4", actual);
    }

    [Fact(DisplayName = "config without environment override returns default value")]
    public async Task ConfigWithoutEnvironmentOverrideReturnsDefaultValue()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = "Production",
        });
        var actual = client.GetString("config.with.only.default.env.row");
        Assert.Equal("hello from no env row", actual);
    }

    [Fact(DisplayName = "datadir without environment fails to init")]
    public async Task DatadirWithoutEnvironmentFailsToInit()
    {
        var options = new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
        };
        Assert.Throws<InvalidOperationException>(() => TestSetup.NewClient(options));
        await Task.CompletedTask;
    }

    [Fact(DisplayName = "datadir with invalid environment fails to init")]
    public async Task DatadirWithInvalidEnvironmentFailsToInit()
    {
        var options = new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = "nonexistent",
        };
        Assert.Throws<InvalidOperationException>(() => TestSetup.NewClient(options));
        await Task.CompletedTask;
    }
}
