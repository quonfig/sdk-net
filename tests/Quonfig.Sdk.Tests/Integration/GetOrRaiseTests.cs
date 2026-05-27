// AUTO-GENERATED from integration-test-data/tests/eval/get_or_raise.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using Quonfig.Sdk.Exceptions;
using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public class GetOrRaiseTests
{

    [Fact(DisplayName = "get_or_raise can raise an error if value not found")]
    public void GetOrRaiseCanRaiseAnErrorIfValueNotFound()
    {
        Assert.Throws<QuonfigKeyNotFoundException>(() =>
            TestSetup.RunRaiseCase("my-missing-key", TestSetup.Map(), "missing_default"));
    }

    [Fact(DisplayName = "get_or_raise returns a default value instead of raising")]
    public void GetOrRaiseReturnsADefaultValueInsteadOfRaising()
    {
        object? actual = TestSetup.GetCase("my-missing-key", TestSetup.Map(), "DEFAULT");
        Assert.Equal("DEFAULT", actual);
    }

    [Fact(DisplayName = "get_or_raise raises the correct error if it doesn't raise on init timeout")]
    public void GetOrRaiseRaisesTheCorrectErrorIfItDoesnTRaiseOnInitTimeout()
    {
        TestSetup.AssertClientConstructionRaises<QuonfigKeyNotFoundException>("any-key", 0.01d, "https://app.staging-prefab.cloud", "return", "get_or_raise");
    }

    [Fact(DisplayName = "get_or_raise can raise an error if the client does not initialize in time")]
    public void GetOrRaiseCanRaiseAnErrorIfTheClientDoesNotInitializeInTime()
    {
        TestSetup.AssertInitializationTimeoutError("any-key", 0.01d, "https://app.staging-prefab.cloud", "raise");
    }

    [Fact(DisplayName = "raises an error if a config is provided by a missing environment variable")]
    public void RaisesAnErrorIfAConfigIsProvidedByAMissingEnvironmentVariable()
    {
        Assert.Throws<QuonfigEnvVarNotSetException>(() =>
            TestSetup.RunRaiseCase("provided.by.missing.env.var", TestSetup.Map(), "missing_env_var"));
    }

    [Fact(DisplayName = "raises an error if an env-var-provided config cannot be coerced to configured type")]
    public void RaisesAnErrorIfAnEnvVarProvidedConfigCannotBeCoercedToConfiguredType()
    {
        Assert.Throws<QuonfigKeyNotFoundException>(() =>
            TestSetup.RunRaiseCase("provided.not.a.number", TestSetup.Map(), "unable_to_coerce_env_var"));
    }

    [Fact(DisplayName = "raises an error for decryption failure")]
    public void RaisesAnErrorForDecryptionFailure()
    {
        Assert.Throws<QuonfigDecryptionException>(() =>
            TestSetup.RunRaiseCase("a.broken.secret.config", TestSetup.Map(), "unable_to_decrypt"));
    }
}
