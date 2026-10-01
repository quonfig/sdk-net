// AUTO-GENERATED from integration-test-data/tests/eval/telemetry.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using System;
using System.Threading.Tasks;
using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public sealed class TelemetryTests
{

    [Fact(DisplayName = "reason is STATIC for config with no targeting rules")]
    public async Task ReasonIsStaticForConfigWithNoTargetingRules()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet());
            telemetry.Evaluate(scoped, "brand.new.string");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "brand.new.string", "type", "CONFIG", "value", "hello.world", "value_type", "string", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("string", "hello.world"), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "reason is STATIC for feature flag with only ALWAYS_TRUE rules")]
    public async Task ReasonIsStaticForFeatureFlagWithOnlyAlwaysTrueRules()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet());
            telemetry.Evaluate(scoped, "always.true");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "always.true", "type", "FEATURE_FLAG", "value", true, "value_type", "bool", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("bool", true), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "reason is TARGETING_MATCH when config has targeting rules but evaluation falls through")]
    public async Task ReasonIsTargetingMatchWhenConfigHasTargetingRulesButEvaluationFallsThrough()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet());
            telemetry.Evaluate(scoped, "my-test-key");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "my-test-key", "type", "CONFIG", "value", "my-test-value", "value_type", "string", "count", 1L, "reason", 2L, "selected_value", TestSetup.Map("string", "my-test-value"), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 1L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "reason is TARGETING_MATCH when a targeting rule matches")]
    public async Task ReasonIsTargetingMatchWhenATargetingRuleMatches()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "michael" } });
            telemetry.Evaluate(scoped, "feature-flag.integer");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "feature-flag.integer", "type", "FEATURE_FLAG", "value", 5L, "value_type", "int", "count", 1L, "reason", 2L, "selected_value", TestSetup.Map("int", 5L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "reason is SPLIT for weighted value evaluation")]
    public async Task ReasonIsSplitForWeightedValueEvaluation()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["tracking_id"] = "92a202f2" } });
            telemetry.Evaluate(scoped, "feature-flag.weighted");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "feature-flag.weighted", "type", "FEATURE_FLAG", "value", 2L, "value_type", "int", "count", 1L, "reason", 3L, "selected_value", TestSetup.Map("int", 2L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L, "weighted_value_index", 2L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "reason is SPLIT for weighted value landing in bucket 0")]
    public async Task ReasonIsSplitForWeightedValueLandingInBucket0()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["tracking_id"] = "3e9459d6" } });
            telemetry.Evaluate(scoped, "feature-flag.weighted");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "feature-flag.weighted", "type", "FEATURE_FLAG", "value", 1L, "value_type", "int", "count", 1L, "reason", 3L, "selected_value", TestSetup.Map("int", 1L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L, "weighted_value_index", 0L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "reason is TARGETING_MATCH for feature flag fallthrough with targeting rules")]
    public async Task ReasonIsTargetingMatchForFeatureFlagFallthroughWithTargetingRules()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet());
            telemetry.Evaluate(scoped, "feature-flag.integer");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "feature-flag.integer", "type", "FEATURE_FLAG", "value", 3L, "value_type", "int", "count", 1L, "reason", 2L, "selected_value", TestSetup.Map("int", 3L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 1L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "evaluation summary deduplicates identical evaluations")]
    public async Task EvaluationSummaryDeduplicatesIdenticalEvaluations()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet());
            telemetry.Evaluate(scoped, "brand.new.string");
            telemetry.Evaluate(scoped, "brand.new.string");
            telemetry.Evaluate(scoped, "brand.new.string");
            telemetry.Evaluate(scoped, "brand.new.string");
            telemetry.Evaluate(scoped, "brand.new.string");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "brand.new.string", "type", "CONFIG", "value", "hello.world", "value_type", "string", "count", 5L, "reason", 1L, "selected_value", TestSetup.Map("string", "hello.world"), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "evaluation summary creates separate counters for different rules of same config")]
    public async Task EvaluationSummaryCreatesSeparateCountersForDifferentRulesOfSameConfig()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "michael" } });
            telemetry.Evaluate(scoped, "feature-flag.integer");
            var unscoped = client.WithContext(new ContextSet());
            telemetry.Evaluate(unscoped, "feature-flag.integer");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "feature-flag.integer", "type", "FEATURE_FLAG", "value", 5L, "value_type", "int", "count", 1L, "reason", 2L, "selected_value", TestSetup.Map("int", 5L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L)), TestSetup.Map("key", "feature-flag.integer", "type", "FEATURE_FLAG", "value", 3L, "value_type", "int", "count", 1L, "reason", 2L, "selected_value", TestSetup.Map("int", 3L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 1L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "evaluation summary groups by config key")]
    public async Task EvaluationSummaryGroupsByConfigKey()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet());
            telemetry.Evaluate(scoped, "brand.new.string");
            telemetry.Evaluate(scoped, "always.true");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "brand.new.string", "type", "CONFIG", "value", "hello.world", "value_type", "string", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("string", "hello.world"), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L)), TestSetup.Map("key", "always.true", "type", "FEATURE_FLAG", "value", true, "value_type", "bool", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("bool", true), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "selectedValue wraps string correctly")]
    public async Task SelectedvalueWrapsStringCorrectly()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet());
            telemetry.Evaluate(scoped, "brand.new.string");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "brand.new.string", "type", "CONFIG", "value", "hello.world", "value_type", "string", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("string", "hello.world"), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "selectedValue wraps boolean correctly")]
    public async Task SelectedvalueWrapsBooleanCorrectly()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet());
            telemetry.Evaluate(scoped, "brand.new.boolean");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "brand.new.boolean", "type", "CONFIG", "value", false, "value_type", "bool", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("bool", false), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "selectedValue wraps int correctly")]
    public async Task SelectedvalueWrapsIntCorrectly()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet());
            telemetry.Evaluate(scoped, "brand.new.int");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "brand.new.int", "type", "CONFIG", "value", 123L, "value_type", "int", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("int", 123L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "selectedValue wraps double correctly")]
    public async Task SelectedvalueWrapsDoubleCorrectly()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet());
            telemetry.Evaluate(scoped, "brand.new.double");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "brand.new.double", "type", "CONFIG", "value", 123.99d, "value_type", "double", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("double", 123.99d), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "selectedValue wraps string list correctly")]
    public async Task SelectedvalueWrapsStringListCorrectly()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet());
            telemetry.Evaluate(scoped, "my-string-list-key");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "my-string-list-key", "type", "CONFIG", "value", TestSetup.List("a", "b", "c"), "value_type", "string_list", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("stringList", TestSetup.List("a", "b", "c")), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "context shape merges fields across multiple records")]
    public async Task ContextShapeMergesFieldsAcrossMultipleRecords()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            telemetry.Evaluate(client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["name"] = "alice", ["age"] = new ContextValueLong(30L) } }), "brand.new.string");
            telemetry.Evaluate(client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["name"] = "bob", ["score"] = new ContextValueDouble(9.5d) }, ["team"] = new ContextProperties { ["name"] = "engineering" } }), "brand.new.string");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("name", "user", "field_types", TestSetup.Map("name", 2L, "age", 1L, "score", 4L)), TestSetup.Map("name", "team", "field_types", TestSetup.Map("name", 2L))), telemetry.Sent("context_shape"));
    }

    [Fact(DisplayName = "example contexts deduplicates by key value")]
    public async Task ExampleContextsDeduplicatesByKeyValue()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            telemetry.Evaluate(client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "user-123", ["name"] = "alice" } }), "brand.new.string");
            telemetry.Evaluate(client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "user-123", ["name"] = "bob" } }), "brand.new.string");
        }
        Assert.Equal(TestSetup.Map("user", TestSetup.Map("key", "user-123", "name", "alice")), telemetry.Sent("example_contexts"));
    }

    [Fact(DisplayName = "telemetry disabled emits nothing")]
    public async Task TelemetryDisabledEmitsNothing()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
        }))
        {
            var scoped = client.WithContext(new ContextSet());
            telemetry.Evaluate(scoped, "brand.new.string");
        }
        Assert.Null(telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "shapes only mode reports shapes but not examples")]
    public async Task ShapesOnlyModeReportsShapesButNotExamples()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
            ContextUploadMode = ContextUploadMode.ShapesOnly,
        }))
        {
            telemetry.Evaluate(client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["name"] = "alice", ["key"] = "alice-123" } }), "brand.new.string");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("name", "user", "field_types", TestSetup.Map("name", 2L, "key", 2L))), telemetry.Sent("context_shape"));
    }

    [Fact(DisplayName = "log level evaluations are excluded from telemetry")]
    public async Task LogLevelEvaluationsAreExcludedFromTelemetry()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet());
            telemetry.Evaluate(scoped, "log-level.prefab.criteria_evaluator");
        }
        Assert.Null(telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "empty context produces no context telemetry")]
    public async Task EmptyContextProducesNoContextTelemetry()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            telemetry.Evaluate(client.WithContext(new ContextSet()), "brand.new.string");
        }
        Assert.Null(telemetry.Sent("context_shape"));
    }

    [Fact(DisplayName = "confidential plain string is redacted in selectedValue")]
    public async Task ConfidentialPlainStringIsRedactedInSelectedvalue()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet());
            telemetry.Evaluate(scoped, "confidential.new.string");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "confidential.new.string", "type", "CONFIG", "value", "hello.world", "value_type", "string", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("string", "*****18aa7"), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "confidential encrypted string is redacted using ciphertext hash")]
    public async Task ConfidentialEncryptedStringIsRedactedUsingCiphertextHash()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            var scoped = client.WithContext(new ContextSet());
            telemetry.Evaluate(scoped, "a.secret.config");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "a.secret.config", "type", "CONFIG", "value", "hello.world", "value_type", "string", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("string", "*****936c9"), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), telemetry.Sent("evaluation_summary"));
    }
}
