// AUTO-GENERATED from integration-test-data/tests/eval/context_precedence.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using System;
using System.Threading.Tasks;
using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public sealed class ContextPrecedenceTests
{

    [Fact(DisplayName = "returns the correct `flag` value using the global context (1)")]
    public async Task ReturnsTheCorrectFlagValueUsingTheGlobalContext1()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            GlobalContext = new ContextSet { ["user"] = new ContextProperties { ["isHuman"] = "verified" } },
        });
        var actual = client.IsFeatureEnabled("mixed.case.property.name");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns the correct `flag` value using the global context (2)")]
    public async Task ReturnsTheCorrectFlagValueUsingTheGlobalContext2()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            GlobalContext = new ContextSet { ["user"] = new ContextProperties { ["isHuman"] = "?" } },
        });
        var actual = client.IsFeatureEnabled("mixed.case.property.name");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns the correct `flag` value when local context clobbers global context (1)")]
    public async Task ReturnsTheCorrectFlagValueWhenLocalContextClobbersGlobalContext1()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            GlobalContext = new ContextSet { ["user"] = new ContextProperties { ["isHuman"] = "?" } },
        });
        var actual = client.IsFeatureEnabled("mixed.case.property.name", new ContextSet { ["user"] = new ContextProperties { ["isHuman"] = "verified" } });
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns the correct `flag` value when local context clobbers global context (2)")]
    public async Task ReturnsTheCorrectFlagValueWhenLocalContextClobbersGlobalContext2()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            GlobalContext = new ContextSet { ["user"] = new ContextProperties { ["isHuman"] = "verified" } },
        });
        var actual = client.IsFeatureEnabled("mixed.case.property.name", new ContextSet { ["user"] = new ContextProperties { ["isHuman"] = "?" } });
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns the correct `flag` value when block context clobbers global context (1)")]
    public async Task ReturnsTheCorrectFlagValueWhenBlockContextClobbersGlobalContext1()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            GlobalContext = new ContextSet { ["user"] = new ContextProperties { ["isHuman"] = "verified" } },
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["isHuman"] = "?" } });
        var actual = scoped.IsFeatureEnabled("mixed.case.property.name");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns the correct `flag` value when block context clobbers global context (2)")]
    public async Task ReturnsTheCorrectFlagValueWhenBlockContextClobbersGlobalContext2()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            GlobalContext = new ContextSet { ["user"] = new ContextProperties { ["isHuman"] = "?" } },
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["isHuman"] = "verified" } });
        var actual = scoped.IsFeatureEnabled("mixed.case.property.name");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns the correct `flag` value when local context clobbers block context (1)")]
    public async Task ReturnsTheCorrectFlagValueWhenLocalContextClobbersBlockContext1()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["isHuman"] = "verified" } })
            .WithContext(new ContextSet { ["user"] = new ContextProperties { ["isHuman"] = "?" } });
        var actual = scoped.IsFeatureEnabled("mixed.case.property.name");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns the correct `flag` value when local context clobbers block context (2)")]
    public async Task ReturnsTheCorrectFlagValueWhenLocalContextClobbersBlockContext2()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["isHuman"] = "?" } })
            .WithContext(new ContextSet { ["user"] = new ContextProperties { ["isHuman"] = "verified" } });
        var actual = scoped.IsFeatureEnabled("mixed.case.property.name");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns the correct `get` value using the global context (1)")]
    public async Task ReturnsTheCorrectGetValueUsingTheGlobalContext1()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            GlobalContext = new ContextSet { ["user"] = new ContextProperties { ["email"] = "test@prefab.cloud" } },
        });
        var actual = client.GetString("basic.rule.config");
        Assert.Equal("override", actual);
    }

    [Fact(DisplayName = "returns the correct `get` value using the global context (2)")]
    public async Task ReturnsTheCorrectGetValueUsingTheGlobalContext2()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            GlobalContext = new ContextSet { ["user"] = new ContextProperties { ["email"] = "test@example.com" } },
        });
        var actual = client.GetString("basic.rule.config");
        Assert.Equal("default", actual);
    }

    [Fact(DisplayName = "returns the correct `get` value when local context clobbers global context (1)")]
    public async Task ReturnsTheCorrectGetValueWhenLocalContextClobbersGlobalContext1()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            GlobalContext = new ContextSet { ["user"] = new ContextProperties { ["email"] = "test@example.com" } },
        });
        var actual = client.GetString("basic.rule.config", new ContextSet { ["user"] = new ContextProperties { ["email"] = "test@prefab.cloud" } });
        Assert.Equal("override", actual);
    }

    [Fact(DisplayName = "returns the correct `get` value when local context clobbers global context (2)")]
    public async Task ReturnsTheCorrectGetValueWhenLocalContextClobbersGlobalContext2()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            GlobalContext = new ContextSet { ["user"] = new ContextProperties { ["email"] = "test@prefab.cloud" } },
        });
        var actual = client.GetString("basic.rule.config", new ContextSet { ["user"] = new ContextProperties { ["email"] = "test@example.com" } });
        Assert.Equal("default", actual);
    }

    [Fact(DisplayName = "returns the correct `get` value when block context clobbers global context (1)")]
    public async Task ReturnsTheCorrectGetValueWhenBlockContextClobbersGlobalContext1()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            GlobalContext = new ContextSet { ["user"] = new ContextProperties { ["email"] = "test@prefab.cloud" } },
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["email"] = "test@example.com" } });
        var actual = scoped.GetString("basic.rule.config");
        Assert.Equal("default", actual);
    }

    [Fact(DisplayName = "returns the correct `get` value when block context clobbers global context (2)")]
    public async Task ReturnsTheCorrectGetValueWhenBlockContextClobbersGlobalContext2()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            GlobalContext = new ContextSet { ["user"] = new ContextProperties { ["email"] = "test@example.com" } },
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["email"] = "test@prefab.cloud" } });
        var actual = scoped.GetString("basic.rule.config");
        Assert.Equal("override", actual);
    }

    [Fact(DisplayName = "returns the correct `get` value when local context clobbers block context (1)")]
    public async Task ReturnsTheCorrectGetValueWhenLocalContextClobbersBlockContext1()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["email"] = "test@prefab.cloud" } })
            .WithContext(new ContextSet { ["user"] = new ContextProperties { ["email"] = "test@example.com" } });
        var actual = scoped.GetString("basic.rule.config");
        Assert.Equal("default", actual);
    }

    [Fact(DisplayName = "returns the correct `get` value when local context clobbers block context (2)")]
    public async Task ReturnsTheCorrectGetValueWhenLocalContextClobbersBlockContext2()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["email"] = "test@example.com" } })
            .WithContext(new ContextSet { ["user"] = new ContextProperties { ["email"] = "test@prefab.cloud" } });
        var actual = scoped.GetString("basic.rule.config");
        Assert.Equal("override", actual);
    }

    [Fact(DisplayName = "returns the correct `get` value when local context replaces the whole global named context (disjoint attributes)")]
    public async Task ReturnsTheCorrectGetValueWhenLocalContextReplacesTheWholeGlobalNamedContextDisjointAttributes()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            GlobalContext = new ContextSet { ["user"] = new ContextProperties { ["email"] = "test@prefab.cloud" } },
        });
        var actual = client.GetString("basic.rule.config", new ContextSet { ["user"] = new ContextProperties { ["plan"] = "pro" } });
        Assert.Equal("default", actual);
    }

    [Fact(DisplayName = "returns the correct `get` value when a named context the local context does not mention survives")]
    public async Task ReturnsTheCorrectGetValueWhenANamedContextTheLocalContextDoesNotMentionSurvives()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            GlobalContext = new ContextSet { ["user"] = new ContextProperties { ["email"] = "test@prefab.cloud" } },
        });
        var actual = client.GetString("basic.rule.config", new ContextSet { ["team"] = new ContextProperties { ["plan"] = "pro" } });
        Assert.Equal("override", actual);
    }
}
