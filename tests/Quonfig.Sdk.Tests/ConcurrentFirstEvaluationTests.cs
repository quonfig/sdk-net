using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// qfg-xmuj: the evaluator caches the parsed config row per config. When several threads
/// evaluated a config for the first time at once, the cache insert threw "An item with the same
/// key has already been added" and that evaluation returned the fallback with
/// <see cref="ErrorCode.General"/>. Many fresh clients, many threads released together on the
/// first evaluation; no evaluation may fail.
/// </summary>
public sealed class ConcurrentFirstEvaluationTests : IDisposable
{
    private const string Key = "concurrent.first-eval";
    private const int Clients = 100;
    private const int Threads = 16;

    private readonly string _root;

    public ConcurrentFirstEvaluationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-concurrent-first-eval-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "quonfig.json"), "{\"environments\":[\"production\"]}");
        var dir = Path.Combine(_root, "configs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Key + ".json"),
            "{ \"id\": \"1\", \"key\": \"" + Key + "\", \"type\": \"config\", \"valueType\": \"string\", " +
            "\"default\": { \"rules\": [ { \"criteria\": [ { \"operator\": \"ALWAYS_TRUE\" } ], " +
            "\"value\": { \"type\": \"string\", \"value\": \"stored\" } } ] } }");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ConcurrentFirstEvaluations_NeverReturnErrorOrFallback()
    {
        var failures = new ConcurrentBag<string>();

        for (var c = 0; c < Clients; c++)
        {
            await using var client = new Quonfig(new QuonfigOptions
            {
                Datadir = _root,
                Environment = "production",
            });
            await client.InitAsync();

            using var barrier = new Barrier(Threads);
            var threads = Enumerable.Range(0, Threads).Select(_ => new Thread(() =>
            {
                barrier.SignalAndWait();
                var d = client.GetStringDetails(Key, defaultValue: "fallback");
                if (d.Reason == Reason.Error || d.Value != "stored")
                {
                    failures.Add($"{d.Reason} {d.ErrorCode} {d.Value} {d.ErrorMessage}");
                }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
        }

        failures.Should().BeEmpty();
    }
}
