using System.Collections.Generic;
using FluentAssertions;
using Quonfig.Sdk.Telemetry;
using Xunit;

namespace Quonfig.Sdk.Tests.Telemetry;

/// <summary>
/// Aggregation, payload-shape, and disabled-mode coverage for
/// <see cref="EvaluationSummaryCollector"/>. Mirrors the contract exercised by sdk-java's
/// <c>EvaluationSummaryCollectorTest</c>.
/// </summary>
public sealed class EvaluationSummaryCollectorTests
{
    private static EvaluationStat Stat(
        string configId = "cfg-1",
        string configKey = "feature.foo",
        string configType = "CONFIG",
        int ruleIndex = 0,
        int weightedValueIndex = -1,
        object? selectedValue = null,
        string? reportableValue = null,
        int reason = 1)
        => new(configId, configKey, configType, ruleIndex, weightedValueIndex,
            selectedValue ?? "value-a", reportableValue, reason);

    [Fact]
    public void push_when_disabled_returns_null_on_drain()
    {
        var c = new EvaluationSummaryCollector(enabled: false);
        c.Push(Stat());
        c.Drain().Should().BeNull("disabled collectors must not emit envelopes");
    }

    [Fact]
    public void push_collapses_repeated_identical_evaluations_into_single_counter()
    {
        var c = new EvaluationSummaryCollector(enabled: true);
        for (int i = 0; i < 7; i++) c.Push(Stat());

        var env = c.Drain();
        env.Should().NotBeNull();
        var summaries = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)env!["summaries"]!)["summaries"]!;
        summaries.Should().HaveCount(1);
        var counters = (List<Dictionary<string, object?>>)summaries[0]["counters"]!;
        counters.Should().HaveCount(1);
        counters[0]["count"].Should().Be(7L);
    }

    [Fact]
    public void push_distinct_selected_values_produces_distinct_counters_under_same_key()
    {
        var c = new EvaluationSummaryCollector(enabled: true);
        c.Push(Stat(selectedValue: "a"));
        c.Push(Stat(selectedValue: "b"));
        c.Push(Stat(selectedValue: "a"));

        var env = c.Drain()!;
        var summaries = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)env["summaries"]!)["summaries"]!;
        summaries.Should().HaveCount(1);
        var counters = (List<Dictionary<string, object?>>)summaries[0]["counters"]!;
        counters.Should().HaveCount(2);
        long total = 0;
        foreach (var ctr in counters) total += (long)ctr["count"]!;
        total.Should().Be(3);
    }

    [Fact]
    public void payload_shape_wraps_selected_value_by_type()
    {
        var c = new EvaluationSummaryCollector(enabled: true);
        c.Push(Stat(selectedValue: true));
        c.Push(Stat(configKey: "feature.bar", selectedValue: 42L));
        c.Push(Stat(configKey: "feature.baz", selectedValue: 3.14));
        c.Push(Stat(configKey: "feature.qux", selectedValue: new[] { "a", "b" }));

        var env = c.Drain()!;
        var summaries = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)env["summaries"]!)["summaries"]!;
        var wrappers = new HashSet<string>();
        foreach (var s in summaries)
        {
            foreach (var ctr in (List<Dictionary<string, object?>>)s["counters"]!)
            {
                var sv = (Dictionary<string, object?>)ctr["selectedValue"]!;
                foreach (var k in sv.Keys) wrappers.Add(k);
            }
        }
        wrappers.Should().BeEquivalentTo(new[] { "bool", "int", "double", "stringList" });
    }

    [Fact]
    public void payload_uses_reportable_value_with_string_wrapper_when_redacted()
    {
        var c = new EvaluationSummaryCollector(enabled: true);
        c.Push(Stat(selectedValue: "secret-plaintext", reportableValue: "<encrypted>"));

        var env = c.Drain()!;
        var summaries = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)env["summaries"]!)["summaries"]!;
        var counter = (List<Dictionary<string, object?>>)summaries[0]["counters"]!;
        var sv = (Dictionary<string, object?>)counter[0]["selectedValue"]!;
        sv.Should().ContainKey("string");
        sv["string"].Should().Be("<encrypted>");
    }

    [Fact]
    public void log_level_evaluations_are_skipped()
    {
        var c = new EvaluationSummaryCollector(enabled: true);
        c.Push(Stat(configType: "LOG_LEVEL"));

        c.Drain().Should().BeNull("LOG_LEVEL evaluations are not summarized");
    }

    [Fact]
    public void drain_resets_state_atomically()
    {
        var c = new EvaluationSummaryCollector(enabled: true);
        c.Push(Stat());
        c.Drain().Should().NotBeNull();
        c.Drain().Should().BeNull("a second drain right after the first must return null");
    }

    [Fact]
    public void drain_emits_weighted_value_index_only_when_nonnegative()
    {
        var c = new EvaluationSummaryCollector(enabled: true);
        c.Push(Stat(weightedValueIndex: -1));
        c.Push(Stat(configKey: "feature.split", weightedValueIndex: 2));

        var env = c.Drain()!;
        var summaries = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)env["summaries"]!)["summaries"]!;
        foreach (var s in summaries)
        {
            var counters = (List<Dictionary<string, object?>>)s["counters"]!;
            foreach (var ctr in counters)
            {
                if ((string)s["key"]! == "feature.split")
                {
                    ctr.Should().ContainKey("weightedValueIndex");
                    ctr["weightedValueIndex"].Should().Be(2);
                }
                else
                {
                    ctr.Should().NotContainKey("weightedValueIndex");
                }
            }
        }
    }
}
