using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Tests.Telemetry.Helpers;
using Xunit;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// qfg-goi1.2.15 review: a <c>PROP_MATCHES</c> regex that times out fails closed. The client logs
/// one <c>Warning</c> per config key so a rule that silently never matches can be found. The
/// warning names the config and the property, never the context value.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA2007",
    Justification = "Test code; ConfigureAwait(false) not required.")]
public sealed class RegexTimeoutWarningTests : IDisposable
{
    private const string Key = "regex.flag";
    private readonly string _root;

    public RegexTimeoutWarningTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-regex-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "quonfig.json"), "{\"environments\":[\"production\"]}");
        var dir = Path.Combine(_root, "configs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Key + ".json"), $$"""
            {
              "id": "1", "key": "{{Key}}", "type": "config", "valueType": "string",
              "default": {
                "rules": [
                  {
                    "criteria": [
                      { "propertyName": "user.name", "operator": "PROP_MATCHES",
                        "valueToMatch": { "type": "string", "value": "^(a+)+$" } }
                    ],
                    "value": { "type": "string", "value": "matched" }
                  },
                  { "criteria": [], "value": { "type": "string", "value": "fallthrough" } }
                ]
              }
            }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task RegexTimeout_FailsClosed_AndIsLoggedOncePerKey_WithoutTheValue()
    {
        var logger = new CaptureLogger();
        await using var client = new Quonfig(new QuonfigOptions
        {
            Datadir = _root,
            Environment = "production",
            Logger = logger,
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
        });
        await client.InitAsync();
        var secretish = new string('a', 28) + "!";
        var ctx = new ContextSet { ["user"] = new ContextProperties { ["name"] = secretish } };

        client.GetString(Key, ctx).Should().Be("fallthrough");
        client.GetString(Key, ctx).Should().Be("fallthrough");

        logger.LogCount(MelLogLevel.Warning, "timed out").Should().Be(1, "a regex timeout is logged once per config key");
        var line = logger.First(MelLogLevel.Warning);
        line.Should().Contain(Key).And.Contain("user.name");
        line.Should().NotContain(secretish, "the context value may be sensitive");
    }

    [Fact]
    public async Task OrdinaryRegex_DoesNotWarn()
    {
        var logger = new CaptureLogger();
        await using var client = new Quonfig(new QuonfigOptions
        {
            Datadir = _root,
            Environment = "production",
            Logger = logger,
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
        });
        await client.InitAsync();
        var ctx = new ContextSet { ["user"] = new ContextProperties { ["name"] = "aaaa" } };

        client.GetString(Key, ctx).Should().Be("matched");
        logger.LogCount(MelLogLevel.Warning, "timed out").Should().Be(0);
    }
}
