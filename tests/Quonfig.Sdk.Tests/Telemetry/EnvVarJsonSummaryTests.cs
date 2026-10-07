using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Telemetry;
using Xunit;

namespace Quonfig.Sdk.Tests.Telemetry;

/// <summary>
/// qfg-goi1.2.2: a json config whose value is provided by an ENV_VAR is re-parsed into a fresh
/// dictionary on every evaluation. The evaluation-summary counters must group those equal values
/// into one counter instead of growing one counter per evaluation (unbounded memory and POST size).
/// </summary>
public sealed class EnvVarJsonSummaryTests : IDisposable
{
    private const string JsonKey = "env.json";
    private readonly string _root;

    public EnvVarJsonSummaryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-envjson-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "quonfig.json"), "{\"environments\":[\"production\"]}");
        var dir = Path.Combine(_root, "configs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, JsonKey + ".json"),
            "{ \"id\": \"1\", \"key\": \"" + JsonKey + "\", \"type\": \"config\", \"valueType\": \"json\", " +
            "\"default\": { \"rules\": [ { \"criteria\": [ { \"operator\": \"ALWAYS_TRUE\" } ], \"value\": " +
            "{ \"type\": \"provided\", \"value\": { \"source\": \"ENV_VAR\", \"lookup\": \"QFG_TEST_JSON\" } } } ] } }");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class NullSender : ITelemetrySender
    {
        public Task SendAsync(IDictionary<string, object?> payload, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task RepeatedGetJson_OnEnvVarJsonConfig_RecordsOneCounter()
    {
        await using var client = new Quonfig(new QuonfigOptions
        {
            Datadir = _root,
            Environment = "production",
            EnvLookup = name => name == "QFG_TEST_JSON" ? "{\"tier\":\"gold\",\"limits\":[1,2,3]}" : null,
            TelemetrySender = new NullSender(),
            TelemetryInitialDelay = TimeSpan.FromMinutes(5),
            TelemetryFlushInterval = TimeSpan.FromMinutes(5),
        });
        await client.InitAsync();

        for (int i = 0; i < 10_000; i++) client.GetJson(JsonKey).Should().NotBeNull();

        var env = client.EvaluationSummaries!.Drain()!;
        var summaries = (List<Dictionary<string, object?>>)((Dictionary<string, object?>)env["summaries"]!)["summaries"]!;
        summaries.Should().HaveCount(1);
        var counters = (List<Dictionary<string, object?>>)summaries[0]["counters"]!;
        counters.Should().HaveCount(1, "equal JSON values from the same env var are one counter");
        counters[0]["count"].Should().Be(10_000L);
    }
}
