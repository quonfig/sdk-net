// AUTO-GENERATED from integration-test-data/tests/eval/post.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using System;
using System.Threading.Tasks;
using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public sealed class PostTests
{

    [Fact(DisplayName = "reports context shape aggregation")]
    public async Task ReportsContextShapeAggregation()
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
            telemetry.Evaluate(client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["name"] = "Michael", ["age"] = new ContextValueLong(38L), ["human"] = true }, ["role"] = new ContextProperties { ["name"] = "developer", ["admin"] = false, ["salary"] = new ContextValueDouble(15.75d), ["permissions"] = new ContextValueStringList(new[] { "read", "write" }) } }), "brand.new.string");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("name", "user", "field_types", TestSetup.Map("name", 2L, "age", 1L, "human", 5L)), TestSetup.Map("name", "role", "field_types", TestSetup.Map("name", 2L, "admin", 5L, "salary", 4L, "permissions", 10L))), telemetry.Sent("context_shape"));
    }

    [Fact(DisplayName = "reports evaluation summary")]
    public async Task ReportsEvaluationSummary()
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
            telemetry.Evaluate(scoped, "my-test-key");
            telemetry.Evaluate(scoped, "feature-flag.integer");
            telemetry.Evaluate(scoped, "my-string-list-key");
            telemetry.Evaluate(scoped, "feature-flag.integer");
            telemetry.Evaluate(scoped, "feature-flag.weighted");
        }
        Assert.Equal(TestSetup.List(TestSetup.Map("key", "my-test-key", "type", "CONFIG", "value", "my-test-value", "value_type", "string", "count", 1L, "reason", 2L, "selected_value", TestSetup.Map("string", "my-test-value"), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 1L)), TestSetup.Map("key", "my-string-list-key", "type", "CONFIG", "value", TestSetup.List("a", "b", "c"), "value_type", "string_list", "count", 1L, "reason", 1L, "selected_value", TestSetup.Map("stringList", TestSetup.List("a", "b", "c")), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L)), TestSetup.Map("key", "feature-flag.integer", "type", "FEATURE_FLAG", "value", 3L, "value_type", "int", "count", 2L, "reason", 2L, "selected_value", TestSetup.Map("int", 3L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 1L)), TestSetup.Map("key", "feature-flag.weighted", "type", "FEATURE_FLAG", "value", 2L, "value_type", "int", "count", 1L, "reason", 3L, "selected_value", TestSetup.Map("int", 2L), "summary", TestSetup.Map("config_row_index", 0L, "conditional_value_index", 0L, "weighted_value_index", 2L))), telemetry.Sent("evaluation_summary"));
    }

    [Fact(DisplayName = "reports example contexts")]
    public async Task ReportsExampleContexts()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            telemetry.Evaluate(client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["name"] = "michael", ["age"] = new ContextValueLong(38L), ["key"] = "michael:1234" }, ["device"] = new ContextProperties { ["mobile"] = false }, ["team"] = new ContextProperties { ["id"] = new ContextValueDouble(3.5d) } }), "brand.new.string");
        }
        Assert.Equal(TestSetup.Map("user", TestSetup.Map("name", "michael", "age", 38L, "key", "michael:1234"), "device", TestSetup.Map("mobile", false), "team", TestSetup.Map("id", 3.5d)), telemetry.Sent("example_contexts"));
    }

    [Fact(DisplayName = "example contexts without key are not reported")]
    public async Task ExampleContextsWithoutKeyAreNotReported()
    {
        var telemetry = new TestSetup.TelemetryCapture();
        await using (var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            TelemetrySender = telemetry.Sender,
        }))
        {
            telemetry.Evaluate(client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["name"] = "michael", ["age"] = new ContextValueLong(38L) }, ["device"] = new ContextProperties { ["mobile"] = false }, ["team"] = new ContextProperties { ["id"] = new ContextValueDouble(3.5d) } }), "brand.new.string");
        }
        Assert.Null(telemetry.Sent("example_contexts"));
    }
}
