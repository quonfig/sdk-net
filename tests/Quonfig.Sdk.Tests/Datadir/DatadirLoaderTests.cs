using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Quonfig.Sdk.Datadir;
using Quonfig.Sdk.Exceptions;
using Xunit;

namespace Quonfig.Sdk.Tests.Datadir;

public sealed class DatadirLoaderTests : IDisposable
{
    private static readonly string[] KeysA = new[] { "a" };
    private static readonly string[] KeysABCD = new[] { "a", "b", "c", "d" };

    private readonly string _root;

    public DatadirLoaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-datadir-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    private void WriteFile(string subdir, string filename, string contents)
    {
        var dir = Path.Combine(_root, subdir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, filename), contents);
    }

    private void WriteManifest(params string[] environments)
    {
        var envs = string.Join(",", environments.Select(e => $"\"{e}\""));
        File.WriteAllText(Path.Combine(_root, "quonfig.json"), $"{{\"environments\":[{envs}]}}");
    }

    private static string Config(string key) =>
        $"{{\"key\":\"{key}\",\"type\":\"config\",\"valueType\":\"string\",\"default\":{{\"value\":\"v\"}}}}";

    [Fact]
    public void Load_ReadsAllFourEnvelopeDirs()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", Config("a"));
        WriteFile("feature-flags", "b.flag.json", Config("b"));
        WriteFile("segments", "c.segment.json", Config("c"));
        WriteFile("log-levels", "d.log.json", Config("d"));

        var env = DatadirLoader.Load(_root, "production");

        var keys = env.Configs
            .Select(c => c.GetProperty("key").GetString())
            .ToList();
        keys.Should().BeEquivalentTo(KeysABCD);
        env.Meta.Should().NotBeNull();
        env.Meta!.Environment.Should().Be("production");
    }

    [Fact]
    public void Load_ExcludesSchemasDir_PerQfgB5yi()
    {
        // schemas/ holds JSON Schema documents, not config envelopes. Loading them produces empty-key
        // ghost rows. api-delivery excludes them (qfg-uzsl); sdk-java + sdk-go + sdk-node + sdk-python
        // + sdk-ruby were all fixed under qfg-b5yi. sdk-net must skip them from day one.
        WriteManifest("production");
        WriteFile("configs", "a.config.json", Config("a"));
        WriteFile("schemas", "broken.schema.json", "{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\"}");
        WriteFile("schemas", "another.schema.json", "{\"type\":\"object\",\"properties\":{}}");

        var env = DatadirLoader.Load(_root, "production");

        var keys = env.Configs
            .Select(c => c.TryGetProperty("key", out var k) ? k.GetString() : null)
            .ToList();
        keys.Should().BeEquivalentTo(KeysA);
        keys.Should().NotContain((string?)null, "schema files must be skipped, not surfaced as no-key rows");
    }

    [Fact]
    public void Load_EmptyEnvironment_Throws()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", Config("a"));

        Action act = () => DatadirLoader.Load(_root, "");
        act.Should().Throw<ArgumentException>().WithMessage("*environment*");
    }

    [Fact]
    public void Load_EnvironmentNotInManifest_Throws()
    {
        WriteManifest("production", "staging");
        WriteFile("configs", "a.config.json", Config("a"));

        Action act = () => DatadirLoader.Load(_root, "development");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*development*")
            .Which.Message.Should().Contain("production");
    }

    [Fact]
    public void Load_EmptyDatadir_ThrowsWithClearMessage()
    {
        WriteManifest("production");
        // No config files at all.
        Action act = () => DatadirLoader.Load(_root, "production");
        act.Should().Throw<QuonfigException>().WithMessage("*empty*");
    }

    [Fact]
    public void Load_SkipsDotFilesAndNonJson()
    {
        WriteManifest("production");
        WriteFile("configs", ".hidden.json", Config("hidden"));
        WriteFile("configs", "notes.txt", "text");
        WriteFile("configs", "a.config.json", Config("a"));

        var env = DatadirLoader.Load(_root, "production");
        env.Configs.Select(c => c.GetProperty("key").GetString())
            .Should().BeEquivalentTo(KeysA);
    }

    private static readonly string[] KeysGood = new[] { "good" };

    [Fact]
    public void Load_LogsAndSkipsUnparseableFiles_WhenAtLeastOneValidFileExists()
    {
        WriteManifest("production");
        WriteFile("configs", "broken.config.json", "{not json");
        WriteFile("configs", "good.config.json", Config("good"));

        var env = DatadirLoader.Load(_root, "production");
        env.Configs.Select(c => c.GetProperty("key").GetString())
            .Should().BeEquivalentTo(KeysGood);
    }

    [Fact]
    public void Load_NoManifest_SkipsEnvironmentCheck()
    {
        // Older fixtures (or hand-rolled datadirs) may lack quonfig.json. We still load.
        WriteFile("configs", "a.config.json", Config("a"));
        var env = DatadirLoader.Load(_root, "production");
        env.Configs.Should().HaveCount(1);
    }

    [Fact]
    public void Load_AgainstIntegrationFixture_LoadsRealCorpus()
    {
        string fixture = LocateFixture();
        var env = DatadirLoader.Load(fixture, "Production");

        env.Configs.Should().HaveCountGreaterThan(10, "real corpus has many configs");
        env.Meta!.Environment.Should().Be("Production");
        // Every loaded config has a non-empty key (no schema/* leakage).
        foreach (var c in env.Configs)
        {
            c.TryGetProperty("key", out var k).Should().BeTrue("every loaded entry must be an envelope row, not a schema doc");
            k.GetString().Should().NotBeNullOrEmpty();
        }
    }

    private static string LocateFixture()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            string candidate = Path.GetFullPath(Path.Combine(dir, "integration-test-data", "data", "integration-tests"));
            if (Directory.Exists(candidate)) return candidate;
            string parent = Path.GetDirectoryName(dir)!;
            if (parent == dir) break;
            dir = parent;
        }
        throw new DirectoryNotFoundException("integration-test-data/data/integration-tests not found from " + AppContext.BaseDirectory);
    }
}
