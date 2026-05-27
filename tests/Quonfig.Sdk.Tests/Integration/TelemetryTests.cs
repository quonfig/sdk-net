// AUTO-GENERATED from integration-test-data/tests/eval/telemetry.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public class TelemetryTests
{

    [Fact(DisplayName = "reason is STATIC for config with no targeting rules")]
    public void ReasonIsStaticForConfigWithNoTargetingRules()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("brand.new.string")), TestSetup.Map());
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "brand.new.string", "type", "CONFIG", "value", "hello.world", "value_type", "string", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("string", "hello.world"), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "reason is STATIC for feature flag with only ALWAYS_TRUE rules")]
    public void ReasonIsStaticForFeatureFlagWithOnlyAlwaysTrueRules()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("always.true")), TestSetup.Map());
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "always.true", "type", "FEATURE_FLAG", "value", true, "value_type", "bool", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("bool", true), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "reason is TARGETING_MATCH when config has targeting rules but evaluation falls through")]
    public void ReasonIsTargetingMatchWhenConfigHasTargetingRulesButEvaluationFallsThrough()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("my-test-key")), TestSetup.Map());
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "my-test-key", "type", "CONFIG", "value", "my-test-value", "value_type", "string", "count", 1L, "reason", 2L, "selected_value", TestSetup.Map("string", "my-test-value"), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 1L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "reason is TARGETING_MATCH when a targeting rule matches")]
    public void ReasonIsTargetingMatchWhenATargetingRuleMatches()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("feature-flag.integer")), TestSetup.Map("user", TestSetup.Map("key", "michael")));
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "feature-flag.integer", "type", "FEATURE_FLAG", "value", 5L, "value_type", "int", "count", 1L, "reason", 2L, "selected_value", TestSetup.Map("int", 5L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "reason is SPLIT for weighted value evaluation")]
    public void ReasonIsSplitForWeightedValueEvaluation()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("feature-flag.weighted")), TestSetup.Map("user", TestSetup.Map("tracking_id", "92a202f2")));
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "feature-flag.weighted", "type", "FEATURE_FLAG", "value", 2L, "value_type", "int", "count", 1L, "reason", 3L, "selected_value", TestSetup.Map("int", 2L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L, "weighted_value_index", 2L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "reason is TARGETING_MATCH for feature flag fallthrough with targeting rules")]
    public void ReasonIsTargetingMatchForFeatureFlagFallthroughWithTargetingRules()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("feature-flag.integer")), TestSetup.Map());
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "feature-flag.integer", "type", "FEATURE_FLAG", "value", 3L, "value_type", "int", "count", 1L, "reason", 2L, "selected_value", TestSetup.Map("int", 3L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 1L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "evaluation summary deduplicates identical evaluations")]
    public void EvaluationSummaryDeduplicatesIdenticalEvaluations()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("brand.new.string", "brand.new.string", "brand.new.string", "brand.new.string", "brand.new.string")), TestSetup.Map());
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "brand.new.string", "type", "CONFIG", "value", "hello.world", "value_type", "string", "count", 5L, "reason", 1L, "selected_value", TestSetup.Map("string", "hello.world"), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "evaluation summary creates separate counters for different rules of same config")]
    public void EvaluationSummaryCreatesSeparateCountersForDifferentRulesOfSameConfig()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("feature-flag.integer"), "keys_without_context", TestSetup.List("feature-flag.integer")), TestSetup.Map("user", TestSetup.Map("key", "michael")));
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "feature-flag.integer", "type", "FEATURE_FLAG", "value", 5L, "value_type", "int", "count", 1L, "reason", 2L, "selected_value", TestSetup.Map("int", 5L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L)), TestSetup.Map("key", "feature-flag.integer", "type", "FEATURE_FLAG", "value", 3L, "value_type", "int", "count", 1L, "reason", 2L, "selected_value", TestSetup.Map("int", 3L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 1L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "evaluation summary groups by config key")]
    public void EvaluationSummaryGroupsByConfigKey()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("brand.new.string", "always.true")), TestSetup.Map());
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "brand.new.string", "type", "CONFIG", "value", "hello.world", "value_type", "string", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("string", "hello.world"), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L)), TestSetup.Map("key", "always.true", "type", "FEATURE_FLAG", "value", true, "value_type", "bool", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("bool", true), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "selectedValue wraps string correctly")]
    public void SelectedvalueWrapsStringCorrectly()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("brand.new.string")), TestSetup.Map());
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "brand.new.string", "type", "CONFIG", "value", "hello.world", "value_type", "string", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("string", "hello.world"), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "selectedValue wraps boolean correctly")]
    public void SelectedvalueWrapsBooleanCorrectly()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("brand.new.boolean")), TestSetup.Map());
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "brand.new.boolean", "type", "CONFIG", "value", false, "value_type", "bool", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("bool", false), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "selectedValue wraps int correctly")]
    public void SelectedvalueWrapsIntCorrectly()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("brand.new.int")), TestSetup.Map());
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "brand.new.int", "type", "CONFIG", "value", 123L, "value_type", "int", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("int", 123L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "selectedValue wraps double correctly")]
    public void SelectedvalueWrapsDoubleCorrectly()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("brand.new.double")), TestSetup.Map());
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "brand.new.double", "type", "CONFIG", "value", 123.99d, "value_type", "double", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("double", 123.99d), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "selectedValue wraps string list correctly")]
    public void SelectedvalueWrapsStringListCorrectly()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("my-string-list-key")), TestSetup.Map());
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "my-string-list-key", "type", "CONFIG", "value", TestSetup.List("a", "b", "c"), "value_type", "string_list", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("stringList", TestSetup.List("a", "b", "c")), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "context shape merges fields across multiple records")]
    public void ContextShapeMergesFieldsAcrossMultipleRecords()
    {
        object? aggregator = TestSetup.BuildAggregator("context_shape", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "context_shape", TestSetup.List(TestSetup.Map("user", TestSetup.Map("name", "alice", "age", 30L)), TestSetup.Map("user", TestSetup.Map("name", "bob", "score", 9.5d), "team", TestSetup.Map("name", "engineering"))), TestSetup.Map());
        Assert.Equal(TestSetup.List(TestSetup.Map("name", "user", "field_types", TestSetup.Map("name", 2L, "age", 1L, "score", 4L)), TestSetup.Map("name", "team", "field_types", TestSetup.Map("name", 2L))), TestSetup.AggregatorPost(aggregator, "context_shape", "/api/v1/context-shapes"));
    }

    [Fact(DisplayName = "example contexts deduplicates by key value")]
    public void ExampleContextsDeduplicatesByKeyValue()
    {
        object? aggregator = TestSetup.BuildAggregator("example_contexts", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "example_contexts", TestSetup.List(TestSetup.Map("user", TestSetup.Map("key", "user-123", "name", "alice")), TestSetup.Map("user", TestSetup.Map("key", "user-123", "name", "bob"))), TestSetup.Map());
        Assert.Equal(TestSetup.Map("user", TestSetup.Map("key", "user-123", "name", "alice")), TestSetup.AggregatorPost(aggregator, "example_contexts", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "telemetry disabled emits nothing")]
    public void TelemetryDisabledEmitsNothing()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map("collect_evaluation_summaries", false, "context_upload_mode", ":none"));
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("brand.new.string")), TestSetup.Map());
        Assert.Null(TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "shapes only mode reports shapes but not examples")]
    public void ShapesOnlyModeReportsShapesButNotExamples()
    {
        object? aggregator = TestSetup.BuildAggregator("context_shape", TestSetup.Map("context_upload_mode", ":shape_only"));
        TestSetup.FeedAggregator(aggregator, "context_shape", TestSetup.Map("user", TestSetup.Map("name", "alice", "key", "alice-123")), TestSetup.Map());
        Assert.Equal(TestSetup.List(TestSetup.Map("name", "user", "field_types", TestSetup.Map("name", 2L, "key", 2L))), TestSetup.AggregatorPost(aggregator, "context_shape", "/api/v1/context-shapes"));
    }

    [Fact(DisplayName = "log level evaluations are excluded from telemetry")]
    public void LogLevelEvaluationsAreExcludedFromTelemetry()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("log-level.prefab.criteria_evaluator")), TestSetup.Map());
        Assert.Null(TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "empty context produces no context telemetry")]
    public void EmptyContextProducesNoContextTelemetry()
    {
        object? aggregator = TestSetup.BuildAggregator("context_shape", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "context_shape", TestSetup.Map(), TestSetup.Map());
        Assert.Null(TestSetup.AggregatorPost(aggregator, "context_shape", "/api/v1/context-shapes"));
    }

    [Fact(DisplayName = "confidential plain string is redacted in selectedValue")]
    public void ConfidentialPlainStringIsRedactedInSelectedvalue()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("confidential.new.string")), TestSetup.Map());
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "confidential.new.string", "type", "CONFIG", "value", "hello.world", "value_type", "string", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("string", "*****18aa7"), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "confidential encrypted string is redacted using ciphertext hash")]
    public void ConfidentialEncryptedStringIsRedactedUsingCiphertextHash()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("a.secret.config")), TestSetup.Map());
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "a.secret.config", "type", "CONFIG", "value", "hello.world", "value_type", "string", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("string", "*****936c9"), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }
}
