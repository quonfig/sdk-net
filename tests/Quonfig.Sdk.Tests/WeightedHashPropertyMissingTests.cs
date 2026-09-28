using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// qfg-9dxb.8: a weighted rollout whose <c>hashByPropertyName</c> is missing from the context
/// serves bucket 0 (the first weighted variant). These tests pin what sdk-net does for each
/// edge case, pin known (key, property value) -&gt; variant results for the present-property
/// path, and cover the additive <c>hashPropertyMissing</c> metadata key and the once-per-key
/// warning.
/// </summary>
public sealed class WeightedHashPropertyMissingTests : IDisposable
{
    private const string Key = "feature-flag.weighted";
    private readonly string _root;

    public WeightedHashPropertyMissingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-weighted-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "quonfig.json"), "{\"environments\":[\"production\"]}");
        var dir = Path.Combine(_root, "feature-flags");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "weighted.json"), WeightedFlag(Key));
        File.WriteAllText(Path.Combine(dir, "weighted2.json"), WeightedFlag(Key + ".other"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Same shape as integration-test-data feature-flag.weighted: hashes on user.tracking_id,
    // weights 1 -> 1000, 3 -> 2000, 2 -> 97000. The first variant is 1.
    private static string WeightedFlag(string key) =>
        $$"""
          {
            "id": "16838163869852699",
            "key": "{{key}}",
            "type": "feature_flag",
            "valueType": "int",
            "default": {
              "rules": [
                {
                  "criteria": [ { "operator": "ALWAYS_TRUE" } ],
                  "value": {
                    "type": "weighted_values",
                    "value": {
                      "weightedValues": [
                        { "weight": 1000, "value": { "type": "int", "value": "1" } },
                        { "weight": 2000, "value": { "type": "int", "value": "3" } },
                        { "weight": 97000, "value": { "type": "int", "value": "2" } }
                      ],
                      "hashByPropertyName": "user.tracking_id"
                    }
                  }
                }
              ]
            }
          }
          """;

    private async Task<Quonfig> NewClientAsync(ILogger? logger = null)
    {
        var client = new Quonfig(new QuonfigOptions
        {
            Datadir = _root,
            Environment = "production",
            Logger = logger,
        });
        await client.InitAsync();
        return client;
    }

    private static ContextSet User(string prop, ContextValue value) =>
        new() { ["user"] = new ContextProperties { [prop] = value } };

    public static IEnumerable<object?[]> MissingCases() => new[]
    {
        new object?[] { "no context at all", null },
        new object?[] { "named context missing", new ContextSet { ["device"] = new ContextProperties { ["os"] = "ios" } } },
        new object?[] { "property missing", User("email", "a@b.c") },
    };

    // ----- 1. characterization: missing property -> bucket 0 (value 1), reason Split -----

    [Theory]
    [MemberData(nameof(MissingCases))]
    public async Task MissingHashProperty_ServesFirstVariant(string label, ContextSet? ctx)
    {
        await using var client = await NewClientAsync();
        var d = client.GetLongDetails(Key, ctx);
        d.Value.Should().Be(1L, label);
        d.Reason.Should().Be(Reason.Split, label);
        d.Variant.Should().Be("split:0", label);
        d.ErrorCode.Should().BeNull(label);
        d.Metadata.Should().ContainKey("weightedValueIndex").WhoseValue.Should().Be(0, label);
    }

    // ----- 3. hashPropertyMissing metadata: set only when the fallback fires -----

    [Theory]
    [MemberData(nameof(MissingCases))]
    public async Task MissingHashProperty_SetsHashPropertyMissingMetadata(string label, ContextSet? ctx)
    {
        await using var client = await NewClientAsync();
        var d = client.GetLongDetails(Key, ctx);
        d.Metadata.Should().ContainKey("hashPropertyMissing").WhoseValue.Should().Be(true, label);
    }

    public static IEnumerable<object?[]> PresentCases() => new[]
    {
        new object?[] { "null", User("tracking_id", null!) },
        new object?[] { "empty string", User("tracking_id", "") },
        new object?[] { "a72c15f5", User("tracking_id", "a72c15f5") },
        new object?[] { "92a202f2", User("tracking_id", "92a202f2") },
    };

    [Theory]
    [MemberData(nameof(PresentCases))]
    public async Task PresentHashProperty_OmitsHashPropertyMissingMetadata(string label, ContextSet ctx)
    {
        await using var client = await NewClientAsync();
        var d = client.GetLongDetails(Key, ctx);
        d.Metadata.Should().NotContainKey("hashPropertyMissing", label);
    }

    // ----- 4. warning: once per config key per client -----

    private static string ExpectedWarning(string key) =>
        "quonfig: weighted rollout for \"" + key + "\" hashes on \"user.tracking_id\" which is missing from context; using first variant";

    [Fact]
    public async Task MissingHashProperty_WarnsOncePerKeyPerClient()
    {
        var logger = new RecordingLogger();
        await using (var client = await NewClientAsync(logger))
        {
            client.GetLongDetails(Key);
            client.GetLong(Key, User("email", "a@b.c"));
            client.GetLongDetails(Key, new ContextSet { ["device"] = new ContextProperties { ["os"] = "ios" } });
            client.GetLongDetails(Key + ".other");
            client.GetLongDetails(Key + ".other");
            client.GetLongDetails(Key, User("tracking_id", "a72c15f5"));
        }

        var warnings = logger.Messages(MelLogLevel.Warning).Where(m => m.StartsWith("quonfig: weighted rollout", StringComparison.Ordinal)).ToList();
        warnings.Should().Equal(ExpectedWarning(Key), ExpectedWarning(Key + ".other"));

        // A second client has its own set.
        var logger2 = new RecordingLogger();
        await using (var client2 = await NewClientAsync(logger2))
        {
            client2.GetLongDetails(Key);
        }
        logger2.Messages(MelLogLevel.Warning).Where(m => m.StartsWith("quonfig: weighted rollout", StringComparison.Ordinal))
            .Should().Equal(ExpectedWarning(Key));
    }

    [Fact]
    public async Task PresentHashProperty_DoesNotWarn()
    {
        var logger = new RecordingLogger();
        await using (var client = await NewClientAsync(logger))
        {
            client.GetLongDetails(Key, User("tracking_id", "a72c15f5"));
            client.GetLongDetails(Key, User("tracking_id", ""));
            client.GetLongDetails(Key, User("tracking_id", null!));
        }
        logger.Messages(MelLogLevel.Warning).Where(m => m.StartsWith("quonfig: weighted rollout", StringComparison.Ordinal)).Should().BeEmpty();
    }

    // A property present with a null value or "" is NOT missing: it hashes configKey + "".
    [Fact]
    public async Task NullHashProperty_HashesAsEmptyString()
    {
        await using var client = await NewClientAsync();
        var d = client.GetLongDetails(Key, User("tracking_id", null!));
        d.Value.Should().Be(EmptyStringBucketValue);
        d.Reason.Should().Be(Reason.Split);
        d.Variant.Should().Be(EmptyStringBucketVariant);
    }

    [Fact]
    public async Task EmptyStringHashProperty_HashesAsEmptyString()
    {
        await using var client = await NewClientAsync();
        var d = client.GetLongDetails(Key, User("tracking_id", ""));
        d.Value.Should().Be(EmptyStringBucketValue);
        d.Reason.Should().Be(Reason.Split);
        d.Variant.Should().Be(EmptyStringBucketVariant);
    }

    private const long EmptyStringBucketValue = 2L;
    private const string EmptyStringBucketVariant = "split:2";

    // ----- 5. present property: known (value -> variant) pins, unchanged from v1.3.0 -----

    [Theory]
    [InlineData("a72c15f5", 1L, "split:0")]
    [InlineData("92a202f2", 2L, "split:2")]
    [InlineData("8f414100", 3L, "split:1")]
    public async Task PresentHashProperty_BucketsDeterministically(string trackingId, long expected, string variant)
    {
        await using var client = await NewClientAsync();
        var d = client.GetLongDetails(Key, User("tracking_id", trackingId));
        d.Value.Should().Be(expected);
        d.Reason.Should().Be(Reason.Split);
        d.Variant.Should().Be(variant);
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly object _gate = new();
        private readonly List<(MelLogLevel Level, string Message)> _entries = new();

        public List<string> Messages(MelLogLevel level)
        {
            lock (_gate) { return _entries.Where(e => e.Level == level).Select(e => e.Message).ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(MelLogLevel logLevel) => true;

        public void Log<TState>(MelLogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate) { _entries.Add((logLevel, formatter(state, exception))); }
        }
    }
}
