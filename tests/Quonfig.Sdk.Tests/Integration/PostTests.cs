// AUTO-GENERATED from integration-test-data/tests/eval/post.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public class PostTests
{

    [Fact(DisplayName = "reports context shape aggregation")]
    public void ReportsContextShapeAggregation()
    {
        object? aggregator = TestSetup.BuildAggregator("context_shape", TestSetup.Map("context_upload_mode", ":shape_only"));
        TestSetup.FeedAggregator(aggregator, "context_shape", TestSetup.Map("user", TestSetup.Map("name", "Michael", "age", 38L, "human", true), "role", TestSetup.Map("name", "developer", "admin", false, "salary", 15.75d, "permissions", TestSetup.List("read", "write"))), TestSetup.Map());
        Assert.Equal(TestSetup.List(TestSetup.Map("name", "user", "field_types", TestSetup.Map("name", 2L, "age", 1L, "human", 5L)), TestSetup.Map("name", "role", "field_types", TestSetup.Map("name", 2L, "admin", 5L, "salary", 4L, "permissions", 10L))), TestSetup.AggregatorPost(aggregator, "context_shape", "/api/v1/context-shapes"));
    }

    [Fact(DisplayName = "reports evaluation summary")]
    public void ReportsEvaluationSummary()
    {
        object? aggregator = TestSetup.BuildAggregator("evaluation_summary", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "evaluation_summary", TestSetup.Map("keys", TestSetup.List("my-test-key", "feature-flag.integer", "my-string-list-key", "feature-flag.integer", "feature-flag.weighted")), TestSetup.Map("user", TestSetup.Map("tracking_id", "92a202f2")));
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "my-test-key", "type", "CONFIG", "value", "my-test-value", "value_type", "string", "count", 1L, "reason", 2L, "selected_value", TestSetup.Map("string", "my-test-value"), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 1L)), TestSetup.Map("key", "my-string-list-key", "type", "CONFIG", "value", TestSetup.List("a", "b", "c"), "value_type", "string_list", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("stringList", TestSetup.List("a", "b", "c")), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L)), TestSetup.Map("key", "feature-flag.integer", "type", "FEATURE_FLAG", "value", 3L, "value_type", "int", "count", 2L, "reason", 2L, "selected_value", TestSetup.Map("int", 3L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 1L)), TestSetup.Map("key", "feature-flag.weighted", "type", "FEATURE_FLAG", "value", 2L, "value_type", "int", "count", 1L, "reason", 3L, "selected_value", TestSetup.Map("int", 2L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L, "weighted_value_index", 2L))), TestSetup.AggregatorPost(aggregator, "evaluation_summary", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "reports example contexts")]
    public void ReportsExampleContexts()
    {
        object? aggregator = TestSetup.BuildAggregator("example_contexts", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "example_contexts", TestSetup.Map("user", TestSetup.Map("name", "michael", "age", 38L, "key", "michael:1234"), "device", TestSetup.Map("mobile", false), "team", TestSetup.Map("id", 3.5d)), TestSetup.Map());
        Assert.Equal(TestSetup.Map("user", TestSetup.Map("name", "michael", "age", 38L, "key", "michael:1234"), "device", TestSetup.Map("mobile", false), "team", TestSetup.Map("id", 3.5d)), TestSetup.AggregatorPost(aggregator, "example_contexts", "/api/v1/telemetry"));
    }

    [Fact(DisplayName = "example contexts without key are not reported")]
    public void ExampleContextsWithoutKeyAreNotReported()
    {
        object? aggregator = TestSetup.BuildAggregator("example_contexts", TestSetup.Map());
        TestSetup.FeedAggregator(aggregator, "example_contexts", TestSetup.Map("user", TestSetup.Map("name", "michael", "age", 38L), "device", TestSetup.Map("mobile", false), "team", TestSetup.Map("id", 3.5d)), TestSetup.Map());
        Assert.Null(TestSetup.AggregatorPost(aggregator, "example_contexts", "/api/v1/telemetry"));
    }
}
