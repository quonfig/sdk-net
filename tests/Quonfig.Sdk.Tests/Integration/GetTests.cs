// AUTO-GENERATED from integration-test-data/tests/eval/get.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public class GetTests
{

    [Fact(DisplayName = "get returns a found value for key")]
    public void GetReturnsAFoundValueForKey()
    {
        object? actual = TestSetup.ResolveCase("my-test-key", TestSetup.Map());
        Assert.Equal("my-test-value", actual);
    }

    [Fact(DisplayName = "get returns nil if value not found")]
    public void GetReturnsNilIfValueNotFound()
    {
        object? actual = TestSetup.ResolveCase("my-missing-key", TestSetup.Map());
        Assert.Null(actual);
    }

    [Fact(DisplayName = "get returns a default for a missing value if a default is given")]
    public void GetReturnsADefaultForAMissingValueIfADefaultIsGiven()
    {
        object? actual = TestSetup.GetCase("my-missing-key", TestSetup.Map(), "DEFAULT");
        Assert.Equal("DEFAULT", actual);
    }

    [Fact(DisplayName = "get ignores a provided default if the key is found")]
    public void GetIgnoresAProvidedDefaultIfTheKeyIsFound()
    {
        object? actual = TestSetup.GetCase("my-test-key", TestSetup.Map(), "DEFAULT");
        Assert.Equal("my-test-value", actual);
    }

    [Fact(DisplayName = "get can return a double")]
    public void GetCanReturnADouble()
    {
        object? actual = TestSetup.ResolveCase("my-double-key", TestSetup.Map());
        TestSetup.AssertDoubleEquals(9.95d, actual);
    }

    [Fact(DisplayName = "get can return a string list")]
    public void GetCanReturnAStringList()
    {
        object? actual = TestSetup.ResolveCase("my-string-list-key", TestSetup.Map());
        Assert.Equal(TestSetup.List("a", "b", "c"), actual);
    }

    [Fact(DisplayName = "can return a value provided by an environment variable")]
    public void CanReturnAValueProvidedByAnEnvironmentVariable()
    {
        object? actual = TestSetup.ResolveCase("prefab.secrets.encryption.key", TestSetup.Map());
        Assert.Equal("c87ba22d8662282abe8a0e4651327b579cb64a454ab0f4c170b45b15f049a221", actual);
    }

    [Fact(DisplayName = "can return a value provided by an environment variable after type coercion")]
    public void CanReturnAValueProvidedByAnEnvironmentVariableAfterTypeCoercion()
    {
        object? actual = TestSetup.ResolveCase("provided.a.number", TestSetup.Map());
        Assert.Equal(1234L, actual);
    }

    [Fact(DisplayName = "can decrypt and return a secret value (with decryption key in in env var)")]
    public void CanDecryptAndReturnASecretValueWithDecryptionKeyInInEnvVar()
    {
        object? actual = TestSetup.ResolveCase("a.secret.config", TestSetup.Map());
        Assert.Equal("hello.world", actual);
    }

    [Fact(DisplayName = "duration 200 ms")]
    public void Duration200Ms()
    {
        object? actual = TestSetup.ResolveCase("test.duration.PT0.2S", TestSetup.Map());
        TestSetup.AssertDurationMillis(actual, 200L);
    }

    [Fact(DisplayName = "duration 90S")]
    public void Duration90s()
    {
        object? actual = TestSetup.ResolveCase("test.duration.PT90S", TestSetup.Map());
        TestSetup.AssertDurationMillis(actual, 90000L);
    }

    [Fact(DisplayName = "duration 1.5M")]
    public void Duration15m()
    {
        object? actual = TestSetup.ResolveCase("test.duration.PT1.5M", TestSetup.Map());
        TestSetup.AssertDurationMillis(actual, 90000L);
    }

    [Fact(DisplayName = "duration 0.5H")]
    public void Duration05h()
    {
        object? actual = TestSetup.ResolveCase("test.duration.PT0.5H", TestSetup.Map());
        TestSetup.AssertDurationMillis(actual, 1800000L);
    }

    [Fact(DisplayName = "duration test.duration.P1DT6H2M1.5S")]
    public void DurationTestDurationP1dt6h2m15s()
    {
        object? actual = TestSetup.ResolveCase("test.duration.P1DT6H2M1.5S", TestSetup.Map());
        TestSetup.AssertDurationMillis(actual, 108121500L);
    }

    [Fact(DisplayName = "json test")]
    public void JsonTest()
    {
        object? actual = TestSetup.ResolveCase("test.json", TestSetup.Map());
        Assert.Equal(TestSetup.Map("a", 1L, "b", "c"), actual);
    }

    [Fact(DisplayName = "get returns a native json object (not a stringified payload)")]
    public void GetReturnsANativeJsonObjectNotAStringifiedPayload()
    {
        object? actual = TestSetup.ResolveCase("test.json", TestSetup.Map());
        Assert.Equal(TestSetup.Map("a", 1L, "b", "c"), actual);
    }

    [Fact(DisplayName = "list on left side test (1)")]
    public void ListOnLeftSideTest1()
    {
        object? actual = TestSetup.ResolveCase("left.hand.list.test", TestSetup.Map("user", TestSetup.Map("name", "james", "aka", TestSetup.List("happy", "sleepy"))));
        Assert.Equal("correct", actual);
    }

    [Fact(DisplayName = "list on left side test (2)")]
    public void ListOnLeftSideTest2()
    {
        object? actual = TestSetup.ResolveCase("left.hand.list.test", TestSetup.Map("user", TestSetup.Map("name", "james", "aka", TestSetup.List("a", "b"))));
        Assert.Equal("default", actual);
    }

    [Fact(DisplayName = "list on left side test opposite (1)")]
    public void ListOnLeftSideTestOpposite1()
    {
        object? actual = TestSetup.ResolveCase("left.hand.test.opposite", TestSetup.Map("user", TestSetup.Map("name", "james", "aka", TestSetup.List("happy", "sleepy"))));
        Assert.Equal("default", actual);
    }

    [Fact(DisplayName = "list on left side test (3)")]
    public void ListOnLeftSideTest3()
    {
        object? actual = TestSetup.ResolveCase("left.hand.test.opposite", TestSetup.Map("user", TestSetup.Map("name", "james", "aka", TestSetup.List("a", "b"))));
        Assert.Equal("correct", actual);
    }
}
