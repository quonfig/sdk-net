using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Quonfig.Sdk.Telemetry;
using Quonfig.Sdk.Tests.Telemetry.Helpers;
using Xunit;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// qfg-goi1.2.15 items 2 and 7. <c>GetJson</c> / <c>GetStringList</c> (and their Details) return a
/// copy of the stored value with the same runtime types, so a caller that edits the result does not
/// change the config for everyone else. A telemetry failure on the evaluation path never escapes a
/// getter (the 1.5.0 "Details never throw" contract).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA2007",
    Justification = "Test code; ConfigureAwait(false) not required.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1031",
    Justification = "The concurrency test records every exception a getter throws.")]
public sealed class ReturnedValueIsolationTests : IDisposable
{
    private const string JsonKey = "stored.json";
    private const string ListKey = "stored.list";
    private readonly string _root;

    public ReturnedValueIsolationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-isolation-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "quonfig.json"), "{\"environments\":[\"production\"]}");
        var dir = Path.Combine(_root, "configs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, JsonKey + ".json"), Config(JsonKey, "json",
            "{ \"type\": \"json\", \"value\": { \"tier\": \"gold\", \"limits\": [1, 2, 3], \"nested\": { \"on\": true } } }"));
        File.WriteAllText(Path.Combine(dir, ListKey + ".json"), Config(ListKey, "string_list",
            "{ \"type\": \"string_list\", \"value\": [\"a\", \"b\"] }"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Config(string key, string valueType, string valueJson) =>
        "{ \"id\": \"1\", \"key\": \"" + key + "\", \"type\": \"config\", \"valueType\": \"" + valueType + "\", " +
        "\"default\": { \"rules\": [ { \"criteria\": [ { \"operator\": \"ALWAYS_TRUE\" } ], \"value\": " + valueJson + " } ] } }";

    private sealed class NullSender : ITelemetrySender
    {
        public Task SendAsync(IDictionary<string, object?> payload, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private async Task<Quonfig> NewClientAsync(ILogger? logger = null)
    {
        var client = new Quonfig(new QuonfigOptions
        {
            Datadir = _root,
            Environment = "production",
            Logger = logger,
            // Telemetry on (the default), with a sender that never leaves the process.
            TelemetrySender = new NullSender(),
            TelemetryInitialDelay = TimeSpan.FromMinutes(5),
            TelemetryFlushInterval = TimeSpan.FromMinutes(5),
        });
        await client.InitAsync();
        return client;
    }

    [Fact]
    public async Task GetJson_MutatingTheResult_DoesNotChangeTheConfig()
    {
        await using var client = await NewClientAsync();

        var first = client.GetJson(JsonKey).Should().BeOfType<Dictionary<string, object?>>().Subject;
        first["limits"].Should().BeOfType<List<object?>>();
        first["nested"].Should().BeOfType<Dictionary<string, object?>>();
        first["tier"] = "MUTATED";
        ((List<object?>)first["limits"]!).Add(99L);
        ((Dictionary<string, object?>)first["nested"]!)["on"] = false;
        first["extra"] = "x";

        var second = (Dictionary<string, object?>)client.GetJson(JsonKey)!;
        second.Should().NotBeSameAs(first);
        second["tier"].Should().Be("gold");
        ((List<object?>)second["limits"]!).Should().Equal(1L, 2L, 3L);
        ((Dictionary<string, object?>)second["nested"]!)["on"].Should().Be(true);
        second.ContainsKey("extra").Should().BeFalse();

        var details = client.GetJsonDetails(JsonKey);
        ((Dictionary<string, object?>)details.Value!)["tier"].Should().Be("gold");
    }

    [Fact]
    public async Task GetJsonDetails_MutatingTheResult_DoesNotChangeTheConfig()
    {
        await using var client = await NewClientAsync();

        var first = (Dictionary<string, object?>)client.GetJsonDetails(JsonKey).Value!;
        first["tier"] = "MUTATED";

        ((Dictionary<string, object?>)client.GetJson(JsonKey)!)["tier"].Should().Be("gold");
    }

    [Fact]
    public async Task GetStringList_MutatingTheResult_DoesNotChangeTheConfig()
    {
        await using var client = await NewClientAsync();

        var first = client.GetStringList(ListKey).Should().BeOfType<List<string>>().Subject;
        first.Add("c");
        first[0] = "MUTATED";

        client.GetStringList(ListKey).Should().Equal("a", "b");
        client.GetStringListDetails(ListKey).Value.Should().BeOfType<List<string>>().Which.Should().Equal("a", "b");
    }

    [Fact]
    public async Task GetJson_WithDefault_ReturnsTheCallersOwnDefault()
    {
        await using var client = await NewClientAsync();
        var fallback = new Dictionary<string, object?> { ["d"] = 1L };
        client.GetJson("no.such.key", defaultValue: fallback).Should().BeSameAs(fallback);
    }

    // Item 7 (qfg-goi1.2.2 review): a caller mutating a GetJson result while another thread evaluates
    // the same config must not throw out of GetJsonDetails.
    [Fact]
    public async Task ConcurrentMutateAndGetJsonDetails_NeverThrows()
    {
        await using var client = await NewClientAsync();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var done = new CancellationTokenSource();

        var mutator = Task.Run(() =>
        {
            for (int i = 0; i < 20_000 && errors.IsEmpty; i++)
            {
                try
                {
                    var d = (Dictionary<string, object?>)client.GetJson(JsonKey)!;
                    d["k" + (i % 64)] = i;
                    d.Remove("k" + ((i + 32) % 64));
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            }
            done.Cancel();
        });
        var reader = Task.Run(() =>
        {
            while (!done.IsCancellationRequested && errors.IsEmpty)
            {
                try
                {
                    client.GetJsonDetails(JsonKey);
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            }
        });
        await Task.WhenAll(mutator, reader);

        errors.Should().BeEmpty("a getter must never throw because a caller mutated a returned value");
        ((Dictionary<string, object?>)client.GetJson(JsonKey)!).Keys.Should().BeEquivalentTo("tier", "limits", "nested");
    }

    // A ContextValue whose Type throws makes the context-shape telemetry fail. The getter must still
    // return the evaluated value and log the telemetry failure once.
    private sealed record ExplodingValue : ContextValue
    {
        public override string Type => throw new InvalidOperationException("boom from telemetry");

        public override object ToObject() => throw new InvalidOperationException("boom from telemetry");
    }

    // A shape-telemetry failure must not skip the example-context push for the same evaluation:
    // the two collectors are guarded separately (qfg-goi1.2.15 review).
    private sealed record ShapeOnlyExplodingValue : ContextValue
    {
        public override string Type => throw new InvalidOperationException("boom from shape telemetry");

        public override object ToObject() => "fine";
    }

    [Fact]
    public async Task ShapeTelemetryFailure_DoesNotSkipTheExampleContext()
    {
        var logger = new CaptureLogger();
        await using var client = await NewClientAsync(logger);
        var ctx = new ContextSet
        {
            ["user"] = new ContextProperties { ["key"] = "alice", ["boom"] = new ShapeOnlyExplodingValue() },
        };

        client.GetJsonDetails(JsonKey, ctx).Reason.Should().NotBe(Reason.Error);

        logger.LogCount(MelLogLevel.Warning, "telemetry").Should().Be(1);
        client.ExampleContexts!.Drain().Should().NotBeNull("the example push runs even when the shape push threw");
    }

    [Fact]
    public async Task TelemetryFailure_NeverEscapesAGetter_AndIsLoggedOnce()
    {
        var logger = new CaptureLogger();
        await using var client = await NewClientAsync(logger);
        var ctx = new ContextSet { ["user"] = new ContextProperties { ["boom"] = new ExplodingValue() } };

        for (int i = 0; i < 3; i++)
        {
            var details = client.GetJsonDetails(JsonKey, ctx);
            details.Reason.Should().NotBe(Reason.Error);
            ((Dictionary<string, object?>)details.Value!)["tier"].Should().Be("gold");
            client.GetStringList(ListKey, ctx).Should().Equal("a", "b");
        }

        logger.LogCount(MelLogLevel.Warning, "telemetry").Should().Be(1, "a telemetry failure is logged once, not per evaluation");
    }
}
