using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Eval;
using Quonfig.Sdk.Wire;
using Xunit;

namespace Quonfig.Sdk.Tests.Eval;

public sealed class ConfigStoreTests
{
    private static readonly string[] KeysA = new[] { "a" };
    private static readonly string[] KeysB = new[] { "b" };
    private static readonly string[] KeysAB = new[] { "a", "b" };
    private static JsonElement ParseConfig(string key, string value = "v")
    {
        string json = $"{{\"key\":\"{key}\",\"type\":\"config\",\"valueType\":\"string\",\"default\":{{\"value\":\"{value}\"}}}}";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void Empty_Get_ReturnsNull()
    {
        var store = new ConfigStore();
        store.Get("missing").Should().BeNull();
        store.Keys().Should().BeEmpty();
    }

    [Fact]
    public void Update_PopulatesByKey_AndGetReturnsConfig()
    {
        var store = new ConfigStore();
        var envelope = new ConfigEnvelope(
            new List<JsonElement> { ParseConfig("a"), ParseConfig("b") },
            new Meta("v1", "production", "ws"));

        store.Update(envelope);

        store.Get("a").Should().NotBeNull();
        store.Get("a")!.Key.Should().Be("a");
        store.Get("b").Should().NotBeNull();
        store.Get("missing").Should().BeNull();
        store.Keys().Should().BeEquivalentTo(KeysAB);
    }

    [Fact]
    public void Update_AtomicReplace_DropsPreviousEntries()
    {
        var store = new ConfigStore();
        store.Update(new ConfigEnvelope(new List<JsonElement> { ParseConfig("a") }, null));
        store.Update(new ConfigEnvelope(new List<JsonElement> { ParseConfig("b") }, null));

        store.Get("a").Should().BeNull("Update is atomic replace, not merge");
        store.Get("b").Should().NotBeNull();
        store.Keys().Should().BeEquivalentTo(KeysB);
    }

    [Fact]
    public void Update_SkipsEntriesWithoutKey()
    {
        // Mirrors the empty-key rejection that api-delivery enforces (qfg-b5yi defense-in-depth).
        // A future stray non-envelope JSON file should not produce a ghost row in the store.
        var store = new ConfigStore();
        var noKey = JsonDocument.Parse("{\"type\":\"schema\"}").RootElement.Clone();
        var emptyKey = JsonDocument.Parse("{\"key\":\"\"}").RootElement.Clone();
        store.Update(new ConfigEnvelope(new List<JsonElement> { noKey, emptyKey, ParseConfig("a") }, null));

        store.Keys().Should().BeEquivalentTo(KeysA);
    }

    [Fact]
    public async Task ConcurrentReads_DoNotTear_WhileWriterUpdates()
    {
        // 50 reader threads observe Get() while a writer atomically swaps the envelope.
        // Each observed Get must either be null OR the correct ConfigResponse for that key —
        // never a partially-constructed row from a half-applied update.
        var store = new ConfigStore();
        store.Update(new ConfigEnvelope(new List<JsonElement> { ParseConfig("k", "0") }, null));

        const int readers = 50;
        const int iterations = 5_000;
        using var cts = new CancellationTokenSource();
        var readTasks = new Task[readers];
        for (int r = 0; r < readers; r++)
        {
            readTasks[r] = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var c = store.Get("k");
                    if (c is not null)
                    {
                        c.Key.Should().Be("k");
                        c.Raw.GetProperty("key").GetString().Should().Be("k");
                        c.Raw.GetProperty("default").GetProperty("value").GetString().Should().NotBeNull();
                    }
                }
            });
        }

        for (int i = 0; i < iterations; i++)
        {
            store.Update(new ConfigEnvelope(new List<JsonElement> { ParseConfig("k", i.ToString(CultureInfo.InvariantCulture)) }, null));
        }

#if NET6_0_OR_GREATER
        await cts.CancelAsync();
#else
#pragma warning disable CA1849
        cts.Cancel();
#pragma warning restore CA1849
#endif
        await Task.WhenAll(readTasks);

        store.Get("k")!.Raw.GetProperty("default").GetProperty("value").GetString()
            .Should().Be((iterations - 1).ToString(CultureInfo.InvariantCulture));
    }
}
