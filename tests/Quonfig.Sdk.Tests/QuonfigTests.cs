using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Exceptions;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// Acceptance tests for the public <see cref="Quonfig"/> facade (qfg-zp7i.11). Datadir mode lets
/// us exercise the full surface — init, sync getters, OnNoDefault policy, ShouldLog hierarchy,
/// BoundQuonfig wrapper, health primitives, async dispose — without network I/O.
/// </summary>
public sealed class QuonfigTests : IDisposable
{
    private readonly string _root;

    public QuonfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-client-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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

    private static string StringConfig(string key, string value, string? id = null) =>
        $$"""
          {
            {{(id is null ? "" : $"\"id\":\"{id}\",")}}
            "key": "{{key}}",
            "type": "config",
            "valueType": "string",
            "default": {
              "rules": [
                { "criteria": [], "value": { "type": "string", "value": "{{value}}" } }
              ]
            }
          }
          """;

    private static string IntConfig(string key, long value) =>
        $$"""
          {
            "key": "{{key}}",
            "type": "config",
            "valueType": "int",
            "default": {
              "rules": [
                { "criteria": [], "value": { "type": "int", "value": {{value}} } }
              ]
            }
          }
          """;

    private static string BoolFlag(string key, bool value) =>
        $$"""
          {
            "key": "{{key}}",
            "type": "feature_flag",
            "valueType": "bool",
            "default": {
              "rules": [
                { "criteria": [], "value": { "type": "bool", "value": {{(value ? "true" : "false")}} } }
              ]
            }
          }
          """;

    private static string TargetedStringConfig(string key) =>
        $$"""
          {
            "key": "{{key}}",
            "type": "config",
            "valueType": "string",
            "default": {
              "rules": [
                {
                  "criteria": [
                    { "propertyName": "user.plan", "operator": "PROP_IS_ONE_OF",
                      "valueToMatch": { "type": "string_list", "value": ["pro"] } }
                  ],
                  "value": { "type": "string", "value": "pro-only" }
                },
                { "criteria": [], "value": { "type": "string", "value": "default-val" } }
              ]
            }
          }
          """;

    private static string LogLevelConfig(string key, string level) =>
        $$"""
          {
            "key": "{{key}}",
            "type": "config",
            "valueType": "log_level",
            "default": {
              "rules": [
                { "criteria": [], "value": { "type": "log_level", "value": "{{level}}" } }
              ]
            }
          }
          """;

    private QuonfigOptions DatadirOptions() => new()
    {
        Datadir = _root,
        Environment = "production",
    };

    [Fact]
    public async Task DatadirMode_InitAsync_PopulatesKeys()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", StringConfig("greeting", "hello", id: "id-a"));
        WriteFile("configs", "b.config.json", IntConfig("limit", 42));

        await using var client = new Quonfig(DatadirOptions());
        await client.InitAsync();

        client.Keys().Should().BeEquivalentTo(new[] { "greeting", "limit" });
        client.GetString("greeting").Should().Be("hello");
        client.GetInt("limit").Should().Be(42);
    }

    [Fact]
    public async Task DatadirMode_OnConfigChangeOption_InvokedOnLoad()
    {
        // The OnConfigChange option (qfg-3e6d.1) mirrors sdk-java's onConfigUpdate(Runnable):
        // a callback supplied at construction is subscribed to the OnConfigChange event and so
        // fires when an envelope is installed — including the synchronous initial datadir load.
        WriteManifest("production");
        WriteFile("configs", "a.config.json", StringConfig("greeting", "hello"));

        var fired = false;
        var opts = DatadirOptions();
        opts.OnConfigChange = () => fired = true;

        await using var client = new Quonfig(opts);
        await client.InitAsync();

        fired.Should().BeTrue("the OnConfigChange option must be wired to the config-change event");
    }

    [Fact]
    public async Task DatadirMode_MissingKey_OnNoDefaultThrow_Throws()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", StringConfig("greeting", "hello"));

        await using var client = new Quonfig(DatadirOptions());
        await client.InitAsync();

        var act = () => client.GetString("does.not.exist");
        act.Should().Throw<QuonfigKeyNotFoundException>().WithMessage("*does.not.exist*");
    }

    [Fact]
    public async Task DatadirMode_MissingKey_WithExplicitDefault_ReturnsDefault_EvenUnderThrowPolicy()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", StringConfig("greeting", "hello"));

        await using var client = new Quonfig(DatadirOptions());
        await client.InitAsync();

        client.GetString("does.not.exist", defaultValue: "fallback").Should().Be("fallback");
    }

    [Fact]
    public async Task DatadirMode_MissingKey_OnNoDefaultIgnore_ReturnsNull_NoThrow()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", StringConfig("greeting", "hello"));

        var opts = DatadirOptions();
        opts.OnNoDefault = OnNoDefault.Ignore;
        await using var client = new Quonfig(opts);
        await client.InitAsync();

        client.GetString("does.not.exist").Should().BeNull();
        client.GetInt("does.not.exist").Should().BeNull();
    }

    [Fact]
    public async Task IsFeatureEnabled_MissingKey_ReturnsFalse_NeverThrows()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", StringConfig("greeting", "hello"));

        // Even under Throw policy, IsFeatureEnabled bypasses OnNoDefault.
        await using var client = new Quonfig(DatadirOptions());
        await client.InitAsync();

        client.IsFeatureEnabled("does.not.exist").Should().BeFalse();
    }

    [Fact]
    public async Task IsFeatureEnabled_FlagOn_ReturnsTrue()
    {
        WriteManifest("production");
        WriteFile("feature-flags", "beta.flag.json", BoolFlag("beta", true));

        await using var client = new Quonfig(DatadirOptions());
        await client.InitAsync();

        client.IsFeatureEnabled("beta").Should().BeTrue();
    }

    [Fact]
    public async Task WithContext_AppliesBoundContext_ToGetter()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", TargetedStringConfig("plan.message"));

        await using var client = new Quonfig(DatadirOptions());
        await client.InitAsync();

        var proCtx = new ContextSet { ["user"] = new ContextProperties { ["plan"] = "pro" } };
        var bound = client.WithContext(proCtx);

        bound.GetString("plan.message").Should().Be("pro-only");
        // Without the context, the second (catchall) rule wins.
        client.GetString("plan.message").Should().Be("default-val");
    }

    [Fact]
    public async Task BoundContext_Chained_MergesWithLatestWinning()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", TargetedStringConfig("plan.message"));

        await using var client = new Quonfig(DatadirOptions());
        await client.InitAsync();

        var freeCtx = new ContextSet { ["user"] = new ContextProperties { ["plan"] = "free" } };
        var bound = client.WithContext(freeCtx);

        var proCtx = new ContextSet { ["user"] = new ContextProperties { ["plan"] = "pro" } };
        var rebound = bound.WithContext(proCtx);

        bound.GetString("plan.message").Should().Be("default-val");
        rebound.GetString("plan.message").Should().Be("pro-only");
    }

    [Fact]
    public async Task LastSuccessfulRefresh_IsSet_AfterDatadirInit()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", StringConfig("greeting", "hello"));

        var before = DateTimeOffset.UtcNow;
        await using var client = new Quonfig(DatadirOptions());
        await client.InitAsync();
        var after = DateTimeOffset.UtcNow;

        var stamp = client.LastSuccessfulRefresh;
        stamp.Should().NotBeNull();
        stamp!.Value.Should().BeOnOrAfter(before.AddMilliseconds(-100));
        stamp.Value.Should().BeOnOrBefore(after.AddMilliseconds(100));
    }

    [Fact]
    public async Task ConnectionState_IsConnected_AfterDatadirInit()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", StringConfig("greeting", "hello"));

        await using var client = new Quonfig(DatadirOptions());
        await client.InitAsync();

        client.ConnectionState.Should().Be(ConnectionState.Connected);
    }

    [Fact]
    public async Task GetStringDetails_Match_PopulatesMetadata()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", StringConfig("greeting", "hello", id: "id-greeting"));

        await using var client = new Quonfig(DatadirOptions());
        await client.InitAsync();

        var details = client.GetStringDetails("greeting");
        details.Value.Should().Be("hello");
        details.Reason.Should().Be(Reason.Static);
        details.Variant.Should().Be("static");
        details.ErrorCode.Should().BeNull();
        details.Metadata.Should().ContainKey("configKey").WhoseValue.Should().Be("greeting");
        details.Metadata.Should().ContainKey("configId").WhoseValue.Should().Be("id-greeting");
        details.Metadata.Should().ContainKey("environment").WhoseValue.Should().Be("production");
    }

    [Fact]
    public async Task GetStringDetails_MissingKey_ReturnsErrorWithFlagNotFound()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", StringConfig("greeting", "hello"));

        var opts = DatadirOptions();
        opts.OnNoDefault = OnNoDefault.Ignore;
        await using var client = new Quonfig(opts);
        await client.InitAsync();

        var details = client.GetStringDetails("missing.thing");
        details.Value.Should().BeNull();
        details.Reason.Should().Be(Reason.Error);
        details.ErrorCode.Should().Be(ErrorCode.FlagNotFound);
    }

    [Fact]
    public async Task ShouldLog_WalksDottedParents_OnMiss()
    {
        WriteManifest("production");
        // Only the parent path has a level; child should inherit.
        WriteFile("log-levels", "com.foo.json", LogLevelConfig("com.foo", "WARN"));

        await using var client = new Quonfig(DatadirOptions());
        await client.InitAsync();

        // WARN at com.foo means com.foo.Bar.Baz inherits WARN.
        client.ShouldLog("com.foo.Bar.Baz", LogLevel.Warn).Should().BeTrue();
        client.ShouldLog("com.foo.Bar.Baz", LogLevel.Info).Should().BeFalse();
        client.ShouldLog("com.foo.Bar.Baz", LogLevel.Error).Should().BeTrue();
    }

    [Fact]
    public async Task ShouldLog_NoMatchingConfig_NeverThrows_DefaultsToAllow()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", StringConfig("unrelated", "x"));

        await using var client = new Quonfig(DatadirOptions());
        await client.InitAsync();

        // No log-level config anywhere; defaults to allow.
        client.ShouldLog("some.logger", LogLevel.Info).Should().BeTrue();
    }

    [Fact]
    public async Task GetLogLevel_ReturnsConfiguredLevel()
    {
        WriteManifest("production");
        WriteFile("log-levels", "com.bar.json", LogLevelConfig("com.bar", "DEBUG"));

        await using var client = new Quonfig(DatadirOptions());
        await client.InitAsync();

        client.GetLogLevel("com.bar").Should().Be(LogLevel.Debug);
        client.GetLogLevel("nope.unknown").Should().BeNull();
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", StringConfig("greeting", "hello"));

        var client = new Quonfig(DatadirOptions());
        await client.InitAsync();

        await client.DisposeAsync();
        // Second disposal must not throw.
        await client.DisposeAsync();
        await client.CloseAsync();
    }

    [Fact]
    public void HttpMode_NoSdkKey_Throws()
    {
        var opts = new QuonfigOptions
        {
            // No datadir, no datafile, no sdkKey -> http mode validation should reject.
            ApiUrls = new[] { "https://primary.example.com" },
            StreamUrls = new[] { "https://stream.primary.example.com" },
        };

        var act = () => new Quonfig(opts);
        act.Should().Throw<ArgumentException>().WithMessage("*SdkKey*");
    }

    [Fact]
    public void DatadirMode_MissingEnvironment_Throws()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", StringConfig("greeting", "hello"));

        var opts = new QuonfigOptions { Datadir = _root /* no Environment */ };
        var act = () => new Quonfig(opts);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Environment*");
    }

    [Fact]
    public void MultipleModes_Throws()
    {
        WriteManifest("production");
        WriteFile("configs", "a.config.json", StringConfig("greeting", "hello"));

        var opts = new QuonfigOptions
        {
            Datadir = _root,
            Datafile = "/tmp/nope.json",
            Environment = "production",
        };

        var act = () => new Quonfig(opts);
        act.Should().Throw<ArgumentException>().WithMessage("*Datadir*Datafile*");
    }
}
