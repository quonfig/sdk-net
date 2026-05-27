using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Quonfig.Sdk.Wire;
using Xunit;

namespace Quonfig.Sdk.Tests.Wire;

/// <summary>
/// Round-trip System.Text.Json tests for <see cref="ConfigEnvelope"/> against the cross-SDK
/// corpus at <c>integration-test-data/data/integration-tests/configs/*.json</c>. Mirrors
/// the Java reference at <c>sdk-java/core/src/test/java/com/quonfig/sdk/wire/ConfigEnvelopeRoundTripTest.java</c>.
/// </summary>
public sealed class ConfigEnvelopeRoundTripTests
{
    [Fact]
    public void IntegrationCorpus_RoundTripsThroughSystemTextJson()
    {
        var corpus = LocateCorpus();
        var configs = Directory
            .EnumerateFiles(corpus, "*.json", SearchOption.AllDirectories)
            .Where(p =>
            {
                string name = Path.GetFileName(p);
                return name.Length > 0 && name[0] != '.';
            })
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => JsonDocument.Parse(File.ReadAllBytes(p)).RootElement.Clone())
            .ToList();

        configs.Should().HaveCountGreaterThan(10, "corpus should not be empty");

        var meta = new Meta("v1", "production", "ws-test");
        var envelope = new ConfigEnvelope(configs, meta);

        string json = JsonSerializer.Serialize(envelope);
        var back = JsonSerializer.Deserialize<ConfigEnvelope>(json);

        back.Should().NotBeNull();
        back!.Configs.Should().HaveCount(configs.Count);
        back.Meta.Should().NotBeNull();
        back.Meta!.Version.Should().Be("v1");
        back.Meta!.Environment.Should().Be("production");
        back.Meta!.WorkspaceId.Should().Be("ws-test");

        for (int i = 0; i < configs.Count; i++)
        {
            string originalJson = configs[i].GetRawText();
            string actualJson = back.Configs[i].GetRawText();
            // Normalize through JsonSerializer so whitespace differences don't trip the comparison.
            string norm(string s) => JsonSerializer.Serialize(JsonDocument.Parse(s).RootElement);
            norm(actualJson).Should().Be(norm(originalJson), $"envelope round-trip lost info on config[{i}]");
        }
    }

    [Fact]
    public void EmptyEnvelope_RoundTrips()
    {
        var empty = new ConfigEnvelope(null, null);
        string json = JsonSerializer.Serialize(empty);
        var back = JsonSerializer.Deserialize<ConfigEnvelope>(json);
        back.Should().NotBeNull();
        back!.Configs.Should().NotBeNull();
        back.Configs.Should().BeEmpty();
    }

    [Fact]
    public void MetaWorkspaceId_OmittedWhenNull()
    {
        var m = new Meta("v1", "staging", null);
        var env = new ConfigEnvelope(new List<JsonElement>(), m);
        string json = JsonSerializer.Serialize(env);
        using var doc = JsonDocument.Parse(json);
        var metaNode = doc.RootElement.GetProperty("meta");
        metaNode.GetProperty("version").GetString().Should().Be("v1");
        metaNode.GetProperty("environment").GetString().Should().Be("staging");
        metaNode.TryGetProperty("workspaceId", out _).Should().BeFalse("workspaceId should be omitted when null");
    }

    [Fact]
    public void SingleConfigJsonNode_UnchangedAfterRoundTrip()
    {
        string source = """
        {"id":"id-1","key":"my.key","type":"config","valueType":"string","sendToClientSdk":false,
         "default":{"rules":[{"criteria":[],"value":{"type":"string","value":"hello"}}]}}
        """;
        using var doc = JsonDocument.Parse(source);
        var element = doc.RootElement.Clone();

        var env = new ConfigEnvelope(new List<JsonElement> { element }, new Meta("v", "production", "w"));
        string json = JsonSerializer.Serialize(env);
        var back = JsonSerializer.Deserialize<ConfigEnvelope>(json);
        back.Should().NotBeNull();
        string norm(JsonElement e) => JsonSerializer.Serialize(e);
        norm(back!.Configs[0]).Should().Be(norm(element));
    }

    private static string LocateCorpus()
    {
        // Walk up looking for "integration-test-data/data/integration-tests/configs"
        // since tests can run from bin/Debug/<tfm>/.
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            string candidate = Path.GetFullPath(Path.Combine(dir, "integration-test-data", "data", "integration-tests", "configs"));
            if (Directory.Exists(candidate)) return candidate;
            string parent = Path.GetDirectoryName(dir)!;
            if (parent == dir) break;
            dir = parent;
        }
        throw new DirectoryNotFoundException("integration-test-data/data/integration-tests/configs not found by walking up from " + AppContext.BaseDirectory);
    }
}
