using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// Cross-SDK env-var fallbacks (qfg-2qcq.1): <c>QUONFIG_BACKEND_SDK_KEY</c> seeds
/// <see cref="QuonfigOptions.SdkKey"/> and <c>QUONFIG_ENVIRONMENT</c> seeds
/// <see cref="QuonfigOptions.Environment"/> when the matching option is unset, so a service that
/// exports the canonical vars constructs with a bare <c>new QuonfigOptions()</c>. Matches sdk-go
/// (<c>applyAPIKeyEnvOverride</c> / <c>applyEnvironmentEnvOverride</c>), sdk-python
/// (<c>client.py</c>), sdk-node and sdk-java. Precedence is <b>explicit option &gt; env var</b>.
///
/// <para>The deterministic tests inject the lookup through <see cref="QuonfigOptions.EnvLookup"/>
/// (the same seam the resolver binds to <see cref="System.Environment.GetEnvironmentVariable(string)"/>
/// by default), matching the existing <c>EndpointResolverTests</c> / <c>DevContextTests</c>
/// convention and avoiding process-global env-var pollution across the parallel test run. One test
/// exercises the real process env var to prove the default binding, restoring it in a finally.</para>
/// </summary>
public sealed class EnvVarFallbackTests : IDisposable
{
    private readonly string _root;

    public EnvVarFallbackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-envfallback-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Func<string, string?> Env(params (string, string)[] pairs) =>
        name =>
        {
            foreach (var (k, v) in pairs)
            {
                if (k == name) return v;
            }
            return null;
        };

    private void WriteManifest(params string[] environments)
    {
        var envs = string.Join(",", environments.Select(e => $"\"{e}\""));
        File.WriteAllText(Path.Combine(_root, "quonfig.json"), $"{{\"environments\":[{envs}]}}");
    }

    private void WriteStringConfig(string key, string value)
    {
        var dir = Path.Combine(_root, "configs");
        Directory.CreateDirectory(dir);
        var json =
            $$"""
              {
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
        File.WriteAllText(Path.Combine(dir, key + ".config.json"), json);
    }

    // ---------------- QUONFIG_ENVIRONMENT (datadir mode) ----------------

    [Fact]
    public async Task Environment_FallsBackToEnvVar_WhenOptionAbsent_DatadirMode()
    {
        // No Environment option: datadir mode must seed it from QUONFIG_ENVIRONMENT rather than
        // throwing "Environment required".
        WriteManifest("production");
        WriteStringConfig("greeting", "hello");

        var opts = new QuonfigOptions
        {
            Datadir = _root,
            EnvLookup = Env(("QUONFIG_ENVIRONMENT", "production")),
        };

        await using var client = new Quonfig(opts);
        await client.InitAsync();

        client.GetString("greeting").Should().Be("hello");
    }

    [Fact]
    public async Task Environment_ExplicitOption_SupersedesEnvVar_DatadirMode()
    {
        // Manifest lists ONLY production. The option pins production; the env var points at an
        // environment absent from the manifest. If the env var won, DatadirLoader would throw
        // "environment ... not found". The explicit option must win, so the load succeeds.
        WriteManifest("production");
        WriteStringConfig("greeting", "hello");

        var opts = new QuonfigOptions
        {
            Datadir = _root,
            Environment = "production",
            EnvLookup = Env(("QUONFIG_ENVIRONMENT", "staging")),
        };

        await using var client = new Quonfig(opts);
        await client.InitAsync();

        client.GetString("greeting").Should().Be("hello");
    }

    [Fact]
    public async Task Environment_FallsBackToRealProcessEnvVar_WhenNoEnvLookupOverride()
    {
        // Proves the default binding (EnvLookup ?? System.Environment.GetEnvironmentVariable) reads
        // the real process env var. Restored in finally to avoid leaking into the parallel run.
        WriteManifest("production");
        WriteStringConfig("greeting", "hello");

        var prior = System.Environment.GetEnvironmentVariable("QUONFIG_ENVIRONMENT");
        try
        {
            System.Environment.SetEnvironmentVariable("QUONFIG_ENVIRONMENT", "production");

            await using var client = new Quonfig(new QuonfigOptions { Datadir = _root });
            await client.InitAsync();

            client.GetString("greeting").Should().Be("hello");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("QUONFIG_ENVIRONMENT", prior);
        }
    }

    // ---------------- QUONFIG_BACKEND_SDK_KEY (delivery / HTTP+SSE mode) ----------------

    private static WireMockServer Upstream(int generation)
    {
        var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", $"\"gen-{generation}\"")
                .WithBody(
                    $"{{\"configs\":[],\"meta\":{{\"version\":\"gen-{generation}\",\"environment\":\"production\",\"generation\":{generation}}}}}"));
        return server;
    }

    /// <summary>
    /// Decodes the HTTP Basic-auth password (the SDK key) off the first config request WireMock saw.
    /// The SDK sends <c>Authorization: Basic base64("1:" + sdkKey)</c> (username is the literal 1).
    /// </summary>
    private static string? AuthSdkKeyOf(WireMockServer server)
    {
        foreach (var entry in server.LogEntries)
        {
            var headers = entry.RequestMessage.Headers;
            if (headers is null) continue;
            var authKey = headers.Keys.FirstOrDefault(
                k => string.Equals(k, "Authorization", StringComparison.OrdinalIgnoreCase));
            if (authKey is null) continue;
            var value = headers[authKey].FirstOrDefault();
            if (value is null || !value.StartsWith("Basic ", StringComparison.Ordinal)) continue;
            var creds = Encoding.UTF8.GetString(Convert.FromBase64String(value.Substring("Basic ".Length)));
            var parts = creds.Split(new[] { ':' }, 2);
            return parts.Length == 2 ? parts[1] : parts[0];
        }
        return null;
    }

    private static QuonfigOptions DeliveryOptions(WireMockServer upstream) => new()
    {
        ApiUrls = new[] { upstream.Urls[0] },
        // No SSE, no fallback poller: the only network install is the init fetch, so the request
        // WireMock records carries the resolved SDK key deterministically.
        StreamUrls = Array.Empty<string>(),
        FallbackPollEnabled = false,
        InitTimeout = TimeSpan.FromSeconds(10),
        OnInitFailure = OnInitFailure.Throw,
        OnNoDefault = OnNoDefault.Ignore,
        // Full telemetry opt-out keeps the built-in HTTP telemetry sender off the network.
        CollectEvaluationSummaries = false,
        ContextUploadMode = ContextUploadMode.None,
    };

    [Fact]
    public async Task SdkKey_FallsBackToEnvVar_WhenOptionAbsent_DeliveryMode()
    {
        using var upstream = Upstream(generation: 7);

        var opts = DeliveryOptions(upstream);
        // SdkKey intentionally unset: HTTP+SSE mode must seed it from QUONFIG_BACKEND_SDK_KEY rather
        // than throwing "SdkKey required".
        opts.EnvLookup = Env(("QUONFIG_BACKEND_SDK_KEY", "env-backend-key"));

        await using var client = new Quonfig(opts);
        await client.InitAsync();

        client.HeldGeneration.Should().Be(7, "the client fetched and installed using the env-derived key");
        AuthSdkKeyOf(upstream).Should().Be("env-backend-key", "the env var must be the Basic-auth password");
    }

    [Fact]
    public async Task SdkKey_ExplicitOption_SupersedesEnvVar_DeliveryMode()
    {
        using var upstream = Upstream(generation: 7);

        var opts = DeliveryOptions(upstream);
        opts.SdkKey = "explicit-key";
        opts.EnvLookup = Env(("QUONFIG_BACKEND_SDK_KEY", "env-key"));

        await using var client = new Quonfig(opts);
        await client.InitAsync();

        client.HeldGeneration.Should().Be(7);
        AuthSdkKeyOf(upstream).Should().Be("explicit-key", "the explicit option must win over the env var");
    }
}
