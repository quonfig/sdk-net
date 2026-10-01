// AUTO-GENERATED from integration-test-data/tests/eval/get_or_raise.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using System;
using System.Threading.Tasks;
using Quonfig.Sdk.Exceptions;
using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public sealed class GetOrRaiseTests
{

    [Fact(DisplayName = "get_or_raise can raise an error if value not found")]
    public async Task GetOrRaiseCanRaiseAnErrorIfValueNotFound()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigKeyNotFoundException>(() => client.GetString("my-missing-key"));
    }

    [Fact(DisplayName = "get_or_raise returns a default value instead of raising")]
    public async Task GetOrRaiseReturnsADefaultValueInsteadOfRaising()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("my-missing-key", defaultValue: "DEFAULT");
        Assert.Equal("DEFAULT", actual);
    }

    [Fact(DisplayName = "get_or_raise raises the correct error if it doesn't raise on init timeout")]
    public async Task GetOrRaiseRaisesTheCorrectErrorIfItDoesnTRaiseOnInitTimeout()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "integration-tests",
            ApiUrls = new[] { "https://app.staging-prefab.cloud" },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(10),
            OnInitFailure = OnInitFailure.ReturnDefaults,
        });
        await client.InitAsync();
        Assert.Throws<QuonfigKeyNotFoundException>(() => client.GetString("any-key"));
    }

    [Fact(DisplayName = "get_or_raise can raise an error if the client does not initialize in time")]
    public async Task GetOrRaiseCanRaiseAnErrorIfTheClientDoesNotInitializeInTime()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "integration-tests",
            ApiUrls = new[] { "https://app.staging-prefab.cloud" },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(10),
            OnInitFailure = OnInitFailure.Throw,
        });
        await Assert.ThrowsAsync<QuonfigInitTimeoutException>(() => client.InitAsync());
    }

    [Fact(DisplayName = "raises an error if a config is provided by a missing environment variable")]
    public async Task RaisesAnErrorIfAConfigIsProvidedByAMissingEnvironmentVariable()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigEnvVarNotSetException>(() => client.GetString("provided.by.missing.env.var"));
    }

    [Fact(DisplayName = "raises an error if an env-var-provided config cannot be coerced to configured type")]
    public async Task RaisesAnErrorIfAnEnvVarProvidedConfigCannotBeCoercedToConfiguredType()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetLong("provided.not.a.number"));
    }

    [Fact(DisplayName = "raises an error for decryption failure")]
    public async Task RaisesAnErrorForDecryptionFailure()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigDecryptionException>(() => client.GetString("a.broken.secret.config"));
    }

    [Fact(DisplayName = "raises an error if an env-var-provided duration 30s cannot be coerced")]
    public async Task RaisesAnErrorIfAnEnvVarProvidedDuration30sCannotBeCoerced()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_30S", "30s");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("provided.duration.malformed.30s"));
    }

    [Fact(DisplayName = "raises an error if an env-var-provided duration PT0.5H cannot be coerced")]
    public async Task RaisesAnErrorIfAnEnvVarProvidedDurationPt05hCannotBeCoerced()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_PT0_5H", "PT0.5H");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("provided.duration.malformed.PT0.5H"));
    }

    [Fact(DisplayName = "raises an error if an env-var-provided duration P1DT cannot be coerced")]
    public async Task RaisesAnErrorIfAnEnvVarProvidedDurationP1dtCannotBeCoerced()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_P1DT", "P1DT");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("provided.duration.malformed.P1DT"));
    }

    [Fact(DisplayName = "raises an error if an env-var-provided duration garbage cannot be coerced")]
    public async Task RaisesAnErrorIfAnEnvVarProvidedDurationGarbageCannotBeCoerced()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_GARBAGE", "garbage");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("provided.duration.malformed.garbage"));
    }

    [Fact(DisplayName = "raises an error if a stored duration 30s cannot be coerced")]
    public async Task RaisesAnErrorIfAStoredDuration30sCannotBeCoerced()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("test.duration.malformed.30s"));
    }

    [Fact(DisplayName = "raises an error if a stored duration PT0.5H cannot be coerced")]
    public async Task RaisesAnErrorIfAStoredDurationPt05hCannotBeCoerced()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("test.duration.malformed.PT0.5H"));
    }

    [Fact(DisplayName = "raises an error if a stored duration P1DT cannot be coerced")]
    public async Task RaisesAnErrorIfAStoredDurationP1dtCannotBeCoerced()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("test.duration.malformed.P1DT"));
    }

    [Fact(DisplayName = "raises an error if a stored duration garbage cannot be coerced")]
    public async Task RaisesAnErrorIfAStoredDurationGarbageCannotBeCoerced()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("test.duration.malformed.garbage"));
    }

    [Fact(DisplayName = "raises an error if a stored duration empty cannot be coerced")]
    public async Task RaisesAnErrorIfAStoredDurationEmptyCannotBeCoerced()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("test.duration.malformed.empty"));
    }
}
