using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Quonfig.Sdk.Wire;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// Tests the token-file dev-context loader + default-on injection. Mirrors
/// sdk-node/test/dev-context.test.ts. The config home directory is driven through the
/// <c>QUONFIG_CONFIG_HOME</c> env var (honored by <c>DevContext.ResolveConfigHome</c>, mirroring
/// sdk-python) so the filesystem is isolated to a temp dir per test. The dev-override flag is an
/// in-memory <see cref="ConfigEnvelope"/> (no on-disk fixture lookup), so the
/// <c>[CallerFilePath]</c>-under-CI hazard never applies.
/// </summary>
public sealed class DevContextTests : IDisposable
{
    private const string FlagKey = "feature-flag.dev-override";

    private readonly string _tmpHome;

    public DevContextTests()
    {
        _tmpHome = Path.Combine(Path.GetTempPath(), "quonfig-dev-ctx-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_tmpHome, ".quonfig"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmpHome, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void WriteTokens(string contents) =>
        File.WriteAllText(Path.Combine(_tmpHome, ".quonfig", "tokens.json"), contents);

    /// <summary>
    /// Env lookup that points QUONFIG_CONFIG_HOME at the per-test temp dir and lets the caller
    /// override QUONFIG_DEV_CONTEXT. Everything else is absent (no inheritance from the real env).
    /// </summary>
    private Func<string, string?> EnvLookup(string? devContext) => name => name switch
    {
        "QUONFIG_CONFIG_HOME" => _tmpHome,
        "QUONFIG_DEV_CONTEXT" => devContext,
        _ => null,
    };

    /// <summary>The dev-override feature flag from integration-test-data, as an in-memory envelope.</summary>
    private static ConfigEnvelope DevOverrideEnvelope()
    {
        const string flagJson = """
        {
          "id": "17779999990001",
          "key": "feature-flag.dev-override",
          "type": "feature_flag",
          "valueType": "bool",
          "sendToClientSdk": false,
          "environments": [
            {
              "id": "Production",
              "rules": [
                {
                  "criteria": [
                    {
                      "propertyName": "quonfig-user.email",
                      "operator": "PROP_IS_ONE_OF",
                      "valueToMatch": { "type": "string_list", "value": ["bob@foo.com"] }
                    }
                  ],
                  "value": { "type": "bool", "value": true }
                },
                { "criteria": [{ "operator": "ALWAYS_TRUE" }], "value": { "type": "bool", "value": false } }
              ]
            }
          ],
          "default": {
            "rules": [
              { "criteria": [{ "operator": "ALWAYS_TRUE" }], "value": { "type": "bool", "value": false } }
            ]
          }
        }
        """;
        using var doc = JsonDocument.Parse(flagJson);
        var configs = new List<JsonElement> { doc.RootElement.Clone() };
        return new ConfigEnvelope(configs, new Meta("test", "Production", null));
    }

    private Quonfig Build(bool? enable, string? devContextEnv, ContextSet? globalContext = null) =>
        new Quonfig(new QuonfigOptions
        {
            DatafileEnvelope = DevOverrideEnvelope(),
            Environment = "Production",
            EnableQuonfigUserContext = enable,
            GlobalContext = globalContext,
            EnvLookup = EnvLookup(devContextEnv),
        });

    // 1. RED->GREEN headline: DEFAULT config (no option, no env var) + tokens.json -> flag TRUE.
    [Fact]
    public void DefaultOn_WithTokenFile_FlagEvaluatesTrue()
    {
        WriteTokens(JsonSerializer.Serialize(new { userEmail = "bob@foo.com", accessToken = "x", domain = "quonfig.com" }));

        var q = Build(enable: null, devContextEnv: null);

        Assert.Equal(true, q.GetBool(FlagKey));
    }

    // 2. EnableQuonfigUserContext=false -> no injection (flag false).
    [Fact]
    public void ExplicitFalse_DisablesInjection()
    {
        WriteTokens(JsonSerializer.Serialize(new { userEmail = "bob@foo.com" }));

        var q = Build(enable: false, devContextEnv: null);

        Assert.Equal(false, q.GetBool(FlagKey));
    }

    // 3. QUONFIG_DEV_CONTEXT=false -> no injection (flag false).
    [Fact]
    public void EnvFalse_DisablesInjection()
    {
        WriteTokens(JsonSerializer.Serialize(new { userEmail = "bob@foo.com" }));

        var q = Build(enable: null, devContextEnv: "false");

        Assert.Equal(false, q.GetBool(FlagKey));
    }

    // 4. Explicit option true overrides QUONFIG_DEV_CONTEXT=false -> injection (flag true).
    [Fact]
    public void ExplicitTrue_OverridesEnvFalse()
    {
        WriteTokens(JsonSerializer.Serialize(new { userEmail = "bob@foo.com" }));

        var q = Build(enable: true, devContextEnv: "false");

        Assert.Equal(true, q.GetBool(FlagKey));
    }

    // 5. No token file -> inert (flag false, no error).
    [Fact]
    public void NoTokenFile_IsInert()
    {
        var q = Build(enable: true, devContextEnv: null);

        Assert.Equal(false, q.GetBool(FlagKey));
    }

    // 6. Customer-supplied quonfig-user context wins on collision.
    [Fact]
    public void CustomerContext_WinsOnCollision()
    {
        WriteTokens(JsonSerializer.Serialize(new { userEmail = "bob@foo.com" }));

        var globalContext = new ContextSet
        {
            ["quonfig-user"] = new ContextProperties { ["email"] = new ContextValueString("nobody@nowhere.com") },
        };
        var q = Build(enable: true, devContextEnv: null, globalContext: globalContext);

        // Customer's email (not bob@foo.com) wins, so the override rule does NOT fire.
        Assert.Equal(false, q.GetBool(FlagKey));
    }

    // Env var QUONFIG_DEV_CONTEXT=true enables the same behavior as default-on.
    [Fact]
    public void EnvTrue_EnablesInjection()
    {
        WriteTokens(JsonSerializer.Serialize(new { userEmail = "bob@foo.com" }));

        var q = Build(enable: null, devContextEnv: "true");

        Assert.Equal(true, q.GetBool(FlagKey));
    }

    // No userEmail in the token file -> inert.
    [Fact]
    public void TokenFileWithoutUserEmail_IsInert()
    {
        WriteTokens(JsonSerializer.Serialize(new { accessToken = "x" }));

        var q = Build(enable: true, devContextEnv: null);

        Assert.Equal(false, q.GetBool(FlagKey));
    }
}
