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
/// qfg-9dxb.8 (revised 2026-09-28) and qfg-t9wo: weighted rollout contract.
/// <list type="bullet">
/// <item><description>hashByPropertyName set, value missing (no context, named context absent,
/// property absent, or null) or "": hash configKey + "" and walk the weights as normal. Missing
/// and "" land on the same variant; a weight-0 variant is never served. (v1.3.0 served the first
/// variant for the missing shapes; that is intentionally changed.)</description></item>
/// <item><description>hashByPropertyName not set: a random variant on every evaluation, by
/// weight. (v1.3.0 always served the first variant; qfg-t9wo.)</description></item>
/// <item><description><c>hashPropertyMissing</c> metadata and a once-per-key warning only when
/// the value is missing.</description></item>
/// <item><description>Present non-empty values bucket exactly as in v1.3.0.</description></item>
/// </list>
/// </summary>
public sealed class WeightedHashPropertyMissingTests : IDisposable
{
    private const string Key = "feature-flag.weighted";
    private const string NoHashAbsent = "nohash.absent";
    private const string NoHashNull = "nohash.null";
    private const string NoHashEmpty = "nohash.empty";

    // First variant (1) has weight 0; the others split 50/50.
    private static readonly string[] ZeroFirstKeys =
        { "zero-first.a", "zero-first.b", "zero-first.c", "zero-first.d", "zero-first.e" };
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
        foreach (var k in ZeroFirstKeys)
        {
            File.WriteAllText(Path.Combine(dir, k + ".json"), Flag(k, "\"user.tracking_id\"", (0, 1), (50000, 3), (50000, 2)));
        }
        File.WriteAllText(Path.Combine(dir, "nohash-absent.json"), Flag(NoHashAbsent, null, (50000, 1), (50000, 3)));
        File.WriteAllText(Path.Combine(dir, "nohash-null.json"), Flag(NoHashNull, "null", (50000, 1), (50000, 3)));
        File.WriteAllText(Path.Combine(dir, "nohash-empty.json"), Flag(NoHashEmpty, "\"\"", (50000, 1), (50000, 3)));
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
        Flag(key, "\"user.tracking_id\"", (1000, 1), (2000, 3), (97000, 2));

    // hashJson: the raw JSON for hashByPropertyName, or null to omit the field.
    private static string Flag(string key, string? hashJson, params (int Weight, int Value)[] variants)
    {
        var wv = string.Join(",", variants.Select(v =>
            "{ \"weight\": " + v.Weight + ", \"value\": { \"type\": \"int\", \"value\": \"" + v.Value + "\" } }"));
        var hash = hashJson is null ? "" : ", \"hashByPropertyName\": " + hashJson;
        return "{ \"id\": \"16838163869852699\", \"key\": \"" + key + "\", \"type\": \"feature_flag\", \"valueType\": \"int\", " +
               "\"default\": { \"rules\": [ { \"criteria\": [ { \"operator\": \"ALWAYS_TRUE\" } ], " +
               "\"value\": { \"type\": \"weighted_values\", \"value\": { \"weightedValues\": [ " + wv + " ]" + hash + " } } } ] } }";
    }

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
        new object?[] { "null value", User("tracking_id", null!) },
    };

    // What released v1.3.0 serves for a PRESENT "" tracking_id on feature-flag.weighted (computed
    // on `git archive v1.3.0`): Murmur3(configKey + "") lands in the last bucket.
    private const long EmptyStringBucketValue = 2L;
    private const string EmptyStringBucketVariant = "split:2";

    // ----- 1. missing value hashes configKey + "" (supersedes the v1.3.0 "first variant" pin) -----

    [Theory]
    [MemberData(nameof(MissingCases))]
    public async Task MissingHashProperty_HashesEmptyValue(string label, ContextSet? ctx)
    {
        await using var client = await NewClientAsync();
        var d = client.GetLongDetails(Key, ctx);
        d.Value.Should().Be(EmptyStringBucketValue, label);
        d.Reason.Should().Be(Reason.Split, label);
        d.Variant.Should().Be(EmptyStringBucketVariant, label);
        d.ErrorCode.Should().BeNull(label);
    }

    [Fact]
    public async Task EmptyStringHashProperty_HashesEmptyValue()
    {
        await using var client = await NewClientAsync();
        var d = client.GetLongDetails(Key, User("tracking_id", ""));
        d.Value.Should().Be(EmptyStringBucketValue);
        d.Reason.Should().Be(Reason.Split);
        d.Variant.Should().Be(EmptyStringBucketVariant);
    }

    // ----- 2. a weight-0 first variant is never served when the value is missing -----

    [Fact]
    public async Task MissingHashProperty_NeverServesZeroWeightFirstVariant()
    {
        await using var client = await NewClientAsync();
        foreach (var k in ZeroFirstKeys)
        {
            var empty = client.GetLongDetails(k, User("tracking_id", ""));
            empty.Value.Should().NotBe(1L, k);
            foreach (var row in MissingCases())
            {
                var d = client.GetLongDetails(k, (ContextSet?)row[1]);
                d.Value.Should().NotBe(1L, k + " / " + row[0]);
                d.Variant.Should().Be(empty.Variant, k + " / " + row[0]);
            }
        }
    }

    // ----- 3. hashPropertyMissing metadata: set only when the value is missing -----

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

    // ----- 4. warning: once per config key per client, only when the value is missing -----

    private static string ExpectedWarning(string key) =>
        "quonfig: weighted rollout for \"" + key + "\" hashes on \"user.tracking_id\" which is missing from context; hashing an empty value instead";

    private static List<string> RolloutWarnings(RecordingLogger logger) =>
        logger.Messages(MelLogLevel.Warning).Where(m => m.StartsWith("quonfig: weighted rollout", StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task MissingHashProperty_WarnsOncePerKeyPerClient()
    {
        var logger = new RecordingLogger();
        await using (var client = await NewClientAsync(logger))
        {
            client.GetLongDetails(Key);
            client.GetLong(Key, User("email", "a@b.c"));
            client.GetLongDetails(Key, new ContextSet { ["device"] = new ContextProperties { ["os"] = "ios" } });
            client.GetLongDetails(Key, User("tracking_id", null!));
            client.GetLongDetails(Key + ".other");
            client.GetLongDetails(Key + ".other");
            client.GetLongDetails(Key, User("tracking_id", "a72c15f5"));
        }

        RolloutWarnings(logger).Should().Equal(ExpectedWarning(Key), ExpectedWarning(Key + ".other"));

        // A second client has its own set.
        var logger2 = new RecordingLogger();
        await using (var client2 = await NewClientAsync(logger2))
        {
            client2.GetLongDetails(Key);
        }
        RolloutWarnings(logger2).Should().Equal(ExpectedWarning(Key));
    }

    [Fact]
    public async Task MissingHashProperty_WarnsOncePerKeyUnderConcurrency()
    {
        var logger = new RecordingLogger();
        await using (var client = await NewClientAsync(logger))
        {
            var tasks = Enumerable.Range(0, 16).Select(t => Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    client.GetLongDetails(i % 2 == 0 ? Key : Key + ".other");
                }
            })).ToArray();
            await Task.WhenAll(tasks);
        }

        RolloutWarnings(logger).Should().BeEquivalentTo(new[] { ExpectedWarning(Key), ExpectedWarning(Key + ".other") });
    }

    [Fact]
    public async Task PresentHashProperty_DoesNotWarn()
    {
        var logger = new RecordingLogger();
        await using (var client = await NewClientAsync(logger))
        {
            client.GetLongDetails(Key, User("tracking_id", "a72c15f5"));
            client.GetLongDetails(Key, User("tracking_id", ""));
        }
        RolloutWarnings(logger).Should().BeEmpty();
    }

    // ----- 6. no hashByPropertyName: random variant on every evaluation (qfg-t9wo) -----

    public static IEnumerable<object?[]> NoHashCases() => new[]
    {
        new object?[] { NoHashAbsent, false },
        new object?[] { NoHashAbsent, true },
        new object?[] { NoHashNull, false },
        new object?[] { NoHashNull, true },
        new object?[] { NoHashEmpty, false },
        new object?[] { NoHashEmpty, true },
    };

    [Theory]
    [MemberData(nameof(NoHashCases))]
    public async Task NoHashProperty_PicksRandomVariantPerEvaluation(string key, bool withContext)
    {
        var logger = new RecordingLogger();
        var counts = new Dictionary<long, int>();
        await using (var client = await NewClientAsync(logger))
        {
            var ctx = withContext ? User("tracking_id", "a72c15f5") : null;
            for (int i = 0; i < 1000; i++)
            {
                var d = client.GetLongDetails(key, ctx);
                d.Reason.Should().Be(Reason.Split);
                d.Metadata.Should().NotContainKey("hashPropertyMissing");
                long v = d.Value ?? -1L;
                counts[v] = counts.TryGetValue(v, out var c) ? c + 1 : 1;
            }
        }

        // 50/50: the chance of seeing only one variant in 1000 draws is 2^-999.
        counts.Keys.Should().BeEquivalentTo(new[] { 1L, 3L }, string.Join(",", counts.Select(kv => kv.Key + "=" + kv.Value)));
        RolloutWarnings(logger).Should().BeEmpty();
    }

    [Fact]
    public async Task NoHashProperty_RandomIsSafeUnderConcurrency()
    {
        await using var client = await NewClientAsync();

        // One evaluation first so the config row is parsed before the threads start: the
        // evaluator's first-parse cache can race on a config's very first concurrent evaluations
        // (pre-existing, unrelated to the random source this test is about).
        client.GetLongDetails(NoHashAbsent).ErrorCode.Should().BeNull();

        var seen = new System.Collections.Concurrent.ConcurrentDictionary<long, int>();
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var tasks = Enumerable.Range(0, 16).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                var d = client.GetLongDetails(NoHashAbsent, t % 2 == 0 ? null : User("tracking_id", "x" + i));
                if (d.Value is null) errors.Add(d.Reason + " " + d.ErrorCode + " " + d.ErrorMessage);
                seen.AddOrUpdate(d.Value ?? -1L, 1, (_, c) => c + 1);
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        errors.Should().BeEmpty();
        seen.Keys.Should().BeEquivalentTo(new[] { 1L, 3L });
    }

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
