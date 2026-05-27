using System;
using System.Collections.Generic;
using FluentAssertions;
using Quonfig.Sdk.Telemetry;
using Xunit;

namespace Quonfig.Sdk.Tests.Telemetry;

/// <summary>
/// Periodic-sample, rate-limit, and disabled-mode coverage for
/// <see cref="ExampleContextCollector"/>.
/// </summary>
public sealed class ExampleContextCollectorTests
{
    private static ContextSet WithUserKey(string key) => new()
    {
        ["user"] = new ContextProperties { ["key"] = key, ["plan"] = "pro" },
    };

    [Fact]
    public void push_when_mode_shapes_only_returns_null_on_drain()
    {
        var c = new ExampleContextCollector(ContextUploadMode.ShapesOnly);
        c.Push(WithUserKey("alice"));
        c.Drain().Should().BeNull("ShapesOnly disables example collection");
    }

    [Fact]
    public void push_periodic_example_records_full_context_with_values()
    {
        var c = new ExampleContextCollector(ContextUploadMode.PeriodicExample);
        c.Push(WithUserKey("alice"));

        var env = c.Drain();
        env.Should().NotBeNull();
        var examples = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)env!["exampleContexts"]!)["examples"]!;
        examples.Should().HaveCount(1);

        var contextSet = (Dictionary<string, object?>)examples[0]["contextSet"]!;
        var contexts = (List<Dictionary<string, object?>>)contextSet["contexts"]!;
        contexts.Should().HaveCount(1);
        contexts[0]["type"].Should().Be("user");

        var values = (Dictionary<string, object?>)contexts[0]["values"]!;
        values["key"].Should().Be("alice");
        values["plan"].Should().Be("pro");
    }

    [Fact]
    public void push_rate_limits_repeated_identical_keys_within_window()
    {
        var c = new ExampleContextCollector(
            ContextUploadMode.PeriodicExample, maxDataSize: 100, rateLimit: TimeSpan.FromHours(1));
        c.Push(WithUserKey("alice"));
        c.Push(WithUserKey("alice"));
        c.Push(WithUserKey("alice"));

        var env = c.Drain()!;
        var examples = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)env["exampleContexts"]!)["examples"]!;
        examples.Should().HaveCount(1, "rate-limit must drop repeated identical group keys");
    }

    [Fact]
    public void push_records_distinct_keys_separately()
    {
        var c = new ExampleContextCollector(ContextUploadMode.PeriodicExample);
        c.Push(WithUserKey("alice"));
        c.Push(WithUserKey("bob"));

        var env = c.Drain()!;
        var examples = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)env["exampleContexts"]!)["examples"]!;
        examples.Should().HaveCount(2);
    }

    [Fact]
    public void push_skips_contexts_with_no_key_or_tracking_id()
    {
        var c = new ExampleContextCollector(ContextUploadMode.PeriodicExample);
        var noKey = new ContextSet
        {
            ["user"] = new ContextProperties { ["plan"] = "pro" },
        };
        c.Push(noKey);

        c.Drain().Should().BeNull("contexts without key/trackingId are skipped");
    }

    [Fact]
    public void push_uses_tracking_id_when_key_is_absent()
    {
        var c = new ExampleContextCollector(ContextUploadMode.PeriodicExample);
        var ctx = new ContextSet
        {
            ["user"] = new ContextProperties { ["trackingId"] = "tr-1", ["plan"] = "pro" },
        };
        c.Push(ctx);

        var env = c.Drain()!;
        var examples = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)env["exampleContexts"]!)["examples"]!;
        examples.Should().HaveCount(1);
    }

    [Fact]
    public void drain_resets_state()
    {
        var c = new ExampleContextCollector(ContextUploadMode.PeriodicExample);
        c.Push(WithUserKey("alice"));
        c.Drain().Should().NotBeNull();
        c.Drain().Should().BeNull();
    }
}
