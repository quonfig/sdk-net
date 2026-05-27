using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Quonfig.Sdk.Tests;

public sealed class ContextSetTests
{
    [Fact]
    public void ImplicitConversions_LetCallersAssignBareValues()
    {
        // The smoke test the plan calls out: callers should NOT need `new ContextValueString(...)`.
        var ctx = new ContextSet
        {
            ["user"] = new ContextProperties
            {
                ["plan"] = "pro",
                ["age"] = 42,
                ["sessions"] = 12345678901L,
                ["score"] = 3.14,
                ["beta"] = true,
                ["tags"] = new[] { "a", "b" },
            },
        };

        var user = ctx["user"];
        user["plan"].Should().BeOfType<ContextValueString>().Which.Value.Should().Be("pro");
        user["age"].Should().BeOfType<ContextValueInt>().Which.Value.Should().Be(42);
        user["sessions"].Should().BeOfType<ContextValueLong>().Which.Value.Should().Be(12345678901L);
        user["score"].Should().BeOfType<ContextValueDouble>().Which.Value.Should().Be(3.14);
        user["beta"].Should().BeOfType<ContextValueBool>().Which.Value.Should().BeTrue();
        user["tags"].Should().BeOfType<ContextValueStringList>().Which.Values.Should().Equal("a", "b");
    }

    [Fact]
    public void DottedLookup_FindsValuesInNamedContext()
    {
        var ctx = new ContextSet
        {
            ["user"] = new ContextProperties { ["email"] = "alice@example.com" },
        };
        var lookup = ctx.GetContextValue("user.email");
        lookup.Exists.Should().BeTrue();
        lookup.Value.Should().BeOfType<ContextValueString>().Which.Value.Should().Be("alice@example.com");
    }

    [Fact]
    public void BarePropertyName_LooksUpInUnnamedContext()
    {
        var ctx = new ContextSet
        {
            [""] = new ContextProperties { ["foo"] = "bar" },
        };
        var lookup = ctx.GetContextValue("foo");
        lookup.Exists.Should().BeTrue();
        lookup.Value.Should().BeOfType<ContextValueString>().Which.Value.Should().Be("bar");
    }

    [Fact]
    public void MissingPath_ReturnsAbsentLookup()
    {
        var ctx = new ContextSet();
        ctx.GetContextValue("user.email").Exists.Should().BeFalse();
        ctx.GetContextValue(null).Exists.Should().BeFalse();
    }

    [Fact]
    public void MagicCurrentTimeProperties_ReturnWallClockMillis()
    {
        var ctx = new ContextSet();
        foreach (var prop in new[] { "prefab.current-time", "quonfig.current-time", "reforge.current-time" })
        {
            var lookup = ctx.GetContextValue(prop);
            lookup.Exists.Should().BeTrue();
            lookup.Value.Should().BeOfType<ContextValueLong>();
        }
    }

    [Fact]
    public void ContextValue_SerializesToWireDiscriminatedUnion()
    {
        // Wire shape on a config value is {"type":"string","value":"..."}.
        // The same shape is reused for context property uploads (telemetry shapes), so
        // round-tripping ContextValue through System.Text.Json should produce that shape.
        ContextValue cv = "hello";
        string json = JsonSerializer.Serialize(cv);
        json.Should().Contain("\"type\":\"string\"");
        json.Should().Contain("\"value\":\"hello\"");

        var back = JsonSerializer.Deserialize<ContextValue>(json);
        back.Should().BeOfType<ContextValueString>().Which.Value.Should().Be("hello");
    }

    private static readonly ContextValue[] s_roundTripCases =
    [
        new ContextValueString("s"),
        new ContextValueInt(7),
        new ContextValueLong(8_589_934_592L),
        new ContextValueDouble(1.5),
        new ContextValueBool(false),
        new ContextValueStringList(new[] { "x", "y", "z" }),
    ];

    [Fact]
    public void ContextValue_RoundTrips_ForEveryVariant()
    {
        foreach (var original in s_roundTripCases)
        {
            string json = JsonSerializer.Serialize(original);
            var back = JsonSerializer.Deserialize<ContextValue>(json);
            back.Should().Be(original);
        }
    }
}
