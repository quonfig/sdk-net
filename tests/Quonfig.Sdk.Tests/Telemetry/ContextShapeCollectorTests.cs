using System.Collections.Generic;
using FluentAssertions;
using Quonfig.Sdk.Telemetry;
using Xunit;

namespace Quonfig.Sdk.Tests.Telemetry;

/// <summary>
/// Shape aggregation + payload shape + disabled-mode no-op coverage for
/// <see cref="ContextShapeCollector"/>. Mirrors the integration-test expectation that
/// <c>field_types</c> codes are <c>name=2 (string), age=1 (int), human=5 (bool), salary=4 (double),
/// permissions=10 (array)</c>.
/// </summary>
public sealed class ContextShapeCollectorTests
{
    private static ContextSet SampleContext()
    {
        var cs = new ContextSet();
        var user = new ContextProperties
        {
            ["name"] = "Michael",
            ["age"] = 38,
            ["human"] = true,
        };
        var role = new ContextProperties
        {
            ["name"] = "developer",
            ["admin"] = false,
            ["salary"] = 15.75,
            ["permissions"] = new[] { "read", "write" },
        };
        cs["user"] = user;
        cs["role"] = role;
        return cs;
    }

    [Fact]
    public void push_when_mode_none_returns_null_on_drain()
    {
        var c = new ContextShapeCollector(ContextUploadMode.None);
        c.Push(SampleContext());
        c.Drain().Should().BeNull("None mode disables shape collection");
    }

    [Fact]
    public void push_collects_field_type_codes_per_named_context()
    {
        var c = new ContextShapeCollector(ContextUploadMode.ShapesOnly);
        c.Push(SampleContext());

        var env = c.Drain();
        env.Should().NotBeNull();
        var shapes = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)env!["contextShapes"]!)["shapes"]!;
        shapes.Should().HaveCount(2);

        Dictionary<string, object?>? user = null;
        Dictionary<string, object?>? role = null;
        foreach (var s in shapes)
        {
            if ((string)s["name"]! == "user") user = s;
            else if ((string)s["name"]! == "role") role = s;
        }
        user.Should().NotBeNull();
        role.Should().NotBeNull();

        var userFt = (Dictionary<string, object?>)user!["fieldTypes"]!;
        userFt["name"].Should().Be(2);
        userFt["age"].Should().Be(1);
        userFt["human"].Should().Be(5);

        var roleFt = (Dictionary<string, object?>)role!["fieldTypes"]!;
        roleFt["name"].Should().Be(2);
        roleFt["admin"].Should().Be(5);
        roleFt["salary"].Should().Be(4);
        roleFt["permissions"].Should().Be(10);
    }

    [Fact]
    public void push_merges_new_property_names_into_existing_named_context_shape()
    {
        var c = new ContextShapeCollector(ContextUploadMode.ShapesOnly);
        var first = new ContextSet { ["user"] = new ContextProperties { ["a"] = "x" } };
        var second = new ContextSet { ["user"] = new ContextProperties { ["b"] = 1 } };
        c.Push(first);
        c.Push(second);

        var env = c.Drain()!;
        var shapes = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)env["contextShapes"]!)["shapes"]!;
        shapes.Should().HaveCount(1);
        var ft = (Dictionary<string, object?>)shapes[0]["fieldTypes"]!;
        ft.Should().ContainKey("a").And.ContainKey("b");
        ft["a"].Should().Be(2);
        ft["b"].Should().Be(1);
    }

    [Fact]
    public void drain_resets_state()
    {
        var c = new ContextShapeCollector(ContextUploadMode.ShapesOnly);
        c.Push(SampleContext());
        c.Drain().Should().NotBeNull();
        c.Drain().Should().BeNull("the second drain must be empty");
    }

    [Fact]
    public void periodic_example_mode_also_enables_shape_collection()
    {
        var c = new ContextShapeCollector(ContextUploadMode.PeriodicExample);
        c.IsEnabled.Should().BeTrue();
        c.Push(SampleContext());
        c.Drain().Should().NotBeNull();
    }
}
