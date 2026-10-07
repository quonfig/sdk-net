using System.Collections.Generic;
using System.Linq;
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

    private static List<Dictionary<string, object?>> Counters(IDictionary<string, object?> env, string key)
    {
        var summaries = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)env["summaries"]!)["summaries"]!;
        foreach (var s in summaries)
        {
            if ((string)s["key"]! == key) return (List<Dictionary<string, object?>>)s["counters"]!;
        }
        throw new KeyNotFoundException(key);
    }

    // A JSON payload as an ENV_VAR-provided json config produces it: a FRESH dictionary per
    // evaluation (Resolver re-parses the env value every time).
    private static Dictionary<string, object?> FreshJson(bool reversed = false)
    {
        var nested = new Dictionary<string, object?> { ["b"] = new List<object?> { 1L, "x", null } };
        return reversed
            ? new Dictionary<string, object?> { ["nested"] = nested, ["a"] = 1L }
            : new Dictionary<string, object?> { ["a"] = 1L, ["nested"] = nested };
    }

    [Fact]
    public void push_equal_json_values_from_fresh_objects_collapse_into_one_counter()
    {
        // qfg-goi1.2.2: counters keyed on the value's reference grew one per evaluation.
        var c = new EvaluationSummaryCollector(enabled: true);
        for (int i = 0; i < 10_000; i++) c.Push(Stat(configKey: "json.env", selectedValue: FreshJson()));

        var counters = Counters(c.Drain()!, "json.env");
        counters.Should().HaveCount(1);
        counters[0]["count"].Should().Be(10_000L);
    }

    [Fact]
    public void push_json_values_differing_only_in_key_order_share_a_counter()
    {
        var c = new EvaluationSummaryCollector(enabled: true);
        c.Push(Stat(configKey: "json.env", selectedValue: FreshJson()));
        c.Push(Stat(configKey: "json.env", selectedValue: FreshJson(reversed: true)));

        var counters = Counters(c.Drain()!, "json.env");
        counters.Should().HaveCount(1);
        counters[0]["count"].Should().Be(2L);
    }

    [Fact]
    public void push_distinct_json_values_produce_distinct_counters()
    {
        var c = new EvaluationSummaryCollector(enabled: true);
        c.Push(Stat(configKey: "json.env", selectedValue: new Dictionary<string, object?> { ["a"] = 1L }));
        c.Push(Stat(configKey: "json.env", selectedValue: new Dictionary<string, object?> { ["a"] = 2L }));
        c.Push(Stat(configKey: "json.env", selectedValue: new Dictionary<string, object?> { ["a"] = "1" }));
        c.Push(Stat(configKey: "json.env", selectedValue: new List<object?> { 1L }));

        Counters(c.Drain()!, "json.env").Should().HaveCount(4);
    }

    [Fact]
    public void push_equal_string_lists_from_fresh_arrays_collapse_into_one_counter()
    {
        var c = new EvaluationSummaryCollector(enabled: true);
        for (int i = 0; i < 5; i++) c.Push(Stat(configKey: "list.env", selectedValue: new[] { "a", "b" }));

        var counters = Counters(c.Drain()!, "list.env");
        counters.Should().HaveCount(1);
        counters[0]["count"].Should().Be(5L);
    }

    [Fact]
    public void push_string_list_with_invalid_utf16_does_not_throw()
    {
        // A string list from an ENV_VAR can hold any .NET string, including a lone surrogate.
        // Telemetry runs on the evaluation path and must never throw into a getter.
        var c = new EvaluationSummaryCollector(enabled: true);
        var act = () =>
        {
            c.Push(Stat(configKey: "list.env", selectedValue: new[] { "a\uD800" }));
            c.Push(Stat(configKey: "list.env", selectedValue: new[] { "a\uD800" }));
        };
        act.Should().NotThrow();
        var counters = Counters(c.Drain()!, "list.env");
        counters.Should().HaveCount(1);
        counters[0]["count"].Should().Be(2L);
    }

    private sealed class ThrowingSequence : System.Collections.IEnumerable
    {
        public System.Collections.IEnumerator GetEnumerator() =>
            throw new System.InvalidOperationException("Collection was modified; enumeration operation may not execute.");
    }

    [Fact]
    public void push_when_canonical_walk_throws_falls_back_to_a_reference_key_and_reports_once()
    {
        // qfg-goi1.2.15 item 7: the canonical-JSON walk runs on the evaluation path. If it throws
        // (a value mutated mid-walk), Push must not throw into the getter: it keys that value by
        // reference, as before qfg-goi1.2.2, and reports the failure once.
        var failures = new List<System.Exception>();
        var c = new EvaluationSummaryCollector(enabled: true) { OnCanonicalFailure = failures.Add };
        var value = new ThrowingSequence();
        var act = () =>
        {
            c.Push(Stat(configKey: "json.bad", selectedValue: value));
            c.Push(Stat(configKey: "json.bad", selectedValue: value));
        };
        act.Should().NotThrow();
        failures.Should().HaveCount(1);
        var counters = Counters(c.Drain()!, "json.bad");
        counters.Should().HaveCount(1, "the same reference shares one counter");
        counters[0]["count"].Should().Be(2L);
    }

    [Fact]
    public void cap_bounds_total_counters_and_existing_counters_keep_counting()
    {
        // The cap bounds distinct counters in the window (sdk-go maxKeys), not only distinct
        // (key, type) summaries: one key with many distinct values must not grow past it.
        var c = new EvaluationSummaryCollector(enabled: true, maxDataSize: 3);
        for (int i = 0; i < 5; i++) c.Push(Stat(configKey: "many.values", selectedValue: "v" + i));
        c.Push(Stat(configKey: "other.key", selectedValue: "x"));
        c.Push(Stat(configKey: "many.values", selectedValue: "v0"));

        var env = c.Drain()!;
        var summaries = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)env["summaries"]!)["summaries"]!;
        summaries.Should().HaveCount(1, "a new key past the cap is not recorded");
        var counters = Counters(env, "many.values");
        counters.Should().HaveCount(3);
        counters.Single(ctr => (string)((Dictionary<string, object?>)ctr["selectedValue"]!)["string"]! == "v0")["count"]
            .Should().Be(2L, "an existing counter keeps counting at the cap");
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
