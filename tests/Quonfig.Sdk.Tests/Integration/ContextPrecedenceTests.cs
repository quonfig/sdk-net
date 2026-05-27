// AUTO-GENERATED from integration-test-data/tests/eval/context_precedence.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public class ContextPrecedenceTests
{

    [Fact(DisplayName = "returns the correct `flag` value using the global context (1)")]
    public void ReturnsTheCorrectFlagValueUsingTheGlobalContext1()
    {
        object? actual = TestSetup.EnabledCase("mixed.case.property.name", TestSetup.Map("user", TestSetup.Map("isHuman", "verified")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns the correct `flag` value using the global context (2)")]
    public void ReturnsTheCorrectFlagValueUsingTheGlobalContext2()
    {
        object? actual = TestSetup.EnabledCase("mixed.case.property.name", TestSetup.Map("user", TestSetup.Map("isHuman", "?")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns the correct `flag` value when local context clobbers global context (1)")]
    public void ReturnsTheCorrectFlagValueWhenLocalContextClobbersGlobalContext1()
    {
        object? actual = TestSetup.EnabledCase("mixed.case.property.name", TestSetup.Map("user", TestSetup.Map("isHuman", "verified")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns the correct `flag` value when local context clobbers global context (2)")]
    public void ReturnsTheCorrectFlagValueWhenLocalContextClobbersGlobalContext2()
    {
        object? actual = TestSetup.EnabledCase("mixed.case.property.name", TestSetup.Map("user", TestSetup.Map("isHuman", "?")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns the correct `flag` value when block context clobbers global context (1)")]
    public void ReturnsTheCorrectFlagValueWhenBlockContextClobbersGlobalContext1()
    {
        object? actual = TestSetup.EnabledCase("mixed.case.property.name", TestSetup.Map("user", TestSetup.Map("isHuman", "?")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns the correct `flag` value when block context clobbers global context (2)")]
    public void ReturnsTheCorrectFlagValueWhenBlockContextClobbersGlobalContext2()
    {
        object? actual = TestSetup.EnabledCase("mixed.case.property.name", TestSetup.Map("user", TestSetup.Map("isHuman", "verified")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns the correct `flag` value when local context clobbers block context (1)")]
    public void ReturnsTheCorrectFlagValueWhenLocalContextClobbersBlockContext1()
    {
        object? actual = TestSetup.EnabledCase("mixed.case.property.name", TestSetup.Map("user", TestSetup.Map("isHuman", "?")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns the correct `flag` value when local context clobbers block context (2)")]
    public void ReturnsTheCorrectFlagValueWhenLocalContextClobbersBlockContext2()
    {
        object? actual = TestSetup.EnabledCase("mixed.case.property.name", TestSetup.Map("user", TestSetup.Map("isHuman", "verified")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns the correct `get` value using the global context (1)")]
    public void ReturnsTheCorrectGetValueUsingTheGlobalContext1()
    {
        object? actual = TestSetup.ResolveCase("basic.rule.config", TestSetup.Map("user", TestSetup.Map("email", "test@prefab.cloud")));
        Assert.Equal("override", actual);
    }

    [Fact(DisplayName = "returns the correct `get` value using the global context (2)")]
    public void ReturnsTheCorrectGetValueUsingTheGlobalContext2()
    {
        object? actual = TestSetup.ResolveCase("basic.rule.config", TestSetup.Map("user", TestSetup.Map("email", "test@example.com")));
        Assert.Equal("default", actual);
    }

    [Fact(DisplayName = "returns the correct `get` value when local context clobbers global context (1)")]
    public void ReturnsTheCorrectGetValueWhenLocalContextClobbersGlobalContext1()
    {
        object? actual = TestSetup.ResolveCase("basic.rule.config", TestSetup.Map("user", TestSetup.Map("email", "test@prefab.cloud")));
        Assert.Equal("override", actual);
    }

    [Fact(DisplayName = "returns the correct `get` value when local context clobbers global context (2)")]
    public void ReturnsTheCorrectGetValueWhenLocalContextClobbersGlobalContext2()
    {
        object? actual = TestSetup.ResolveCase("basic.rule.config", TestSetup.Map("user", TestSetup.Map("email", "test@example.com")));
        Assert.Equal("default", actual);
    }

    [Fact(DisplayName = "returns the correct `get` value when block context clobbers global context (1)")]
    public void ReturnsTheCorrectGetValueWhenBlockContextClobbersGlobalContext1()
    {
        object? actual = TestSetup.ResolveCase("basic.rule.config", TestSetup.Map("user", TestSetup.Map("email", "test@example.com")));
        Assert.Equal("default", actual);
    }

    [Fact(DisplayName = "returns the correct `get` value when block context clobbers global context (2)")]
    public void ReturnsTheCorrectGetValueWhenBlockContextClobbersGlobalContext2()
    {
        object? actual = TestSetup.ResolveCase("basic.rule.config", TestSetup.Map("user", TestSetup.Map("email", "test@prefab.cloud")));
        Assert.Equal("override", actual);
    }

    [Fact(DisplayName = "returns the correct `get` value when local context clobbers block context (1)")]
    public void ReturnsTheCorrectGetValueWhenLocalContextClobbersBlockContext1()
    {
        object? actual = TestSetup.ResolveCase("basic.rule.config", TestSetup.Map("user", TestSetup.Map("email", "test@example.com")));
        Assert.Equal("default", actual);
    }

    [Fact(DisplayName = "returns the correct `get` value when local context clobbers block context (2)")]
    public void ReturnsTheCorrectGetValueWhenLocalContextClobbersBlockContext2()
    {
        object? actual = TestSetup.ResolveCase("basic.rule.config", TestSetup.Map("user", TestSetup.Map("email", "test@prefab.cloud")));
        Assert.Equal("override", actual);
    }
}
