using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests.Transport;

/// <summary>
/// End-to-end regression for qfg-64m9: in HTTP+SSE (SDK-key) mode, api-delivery
/// scopes the payload to the SDK key's environment and emits each config row with a
/// SINGULAR <c>"environment"</c> object plus a <c>meta.environment</c> slug. The SDK
/// must (a) parse that singular block and (b) evaluate against the meta environment
/// even when the caller never set <see cref="QuonfigOptions.Environment"/> — otherwise
/// it silently serves the row's <c>default</c> value and every per-environment override
/// is wrong. This drives the real HTTP fetch path through WireMock, the exact gap the
/// first live test-net run exposed (the 421 datadir-based eval tests all use the plural
/// "environments" shape and never exercised this).
/// </summary>
public sealed class HttpDeliveryEnvironmentTests
{
    private const string SdkKey = "delivery-env-tests";

    // Delivery wire shape: meta.environment = "development", and the single config row
    // carries a SINGULAR "environment" block whose development override (false) differs
    // from the row's default (true).
    private static string DeliveryEnvelopeJson() =>
        """
        {
          "configs": [
            {
              "id": "1",
              "key": "flag.delivery",
              "type": "feature_flag",
              "valueType": "bool",
              "environment": {
                "id": "development",
                "rules": [ { "criteria": [], "value": { "type": "bool", "value": false } } ]
              },
              "default": {
                "rules": [ { "criteria": [], "value": { "type": "bool", "value": true } } ]
              }
            }
          ],
          "meta": { "version": "v1", "environment": "development", "workspaceId": "ws-test" }
        }
        """;

    [Fact]
    public async Task HttpMode_AppliesSingularEnvironmentOverride_WhenEnvironmentNotPinned()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", "\"v1\"")
                .WithBody(DeliveryEnvelopeJson()));

        // SDK-key mode, exactly like test-net: no Environment pinned, no SSE/fallback poll.
        await using var client = new Quonfig(new QuonfigOptions
        {
            SdkKey = SdkKey,
            ApiUrls = new[] { server.Urls[0] },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromSeconds(5),
        });
        await client.InitAsync();

        // default = true, development override = false. Pre-fix the parser dropped the
        // singular block AND _effectiveEnvironment stayed null, so this came back true.
        client.GetBool("flag.delivery").Should().Be(false);

        var details = client.GetBoolDetails("flag.delivery");
        details.Metadata["environment"].Should().Be("development");
    }

    [Fact]
    public async Task HttpMode_IgnoresEnvironmentPin_MetaEnvironmentIsAuthoritative()
    {
        // Cross-SDK contract (qfg-pinh): in delivery mode the server's meta.environment is
        // authoritative; an explicit Environment pin (datadir-only) must be IGNORED. The wire
        // payload's development override is false, the row default is true, and meta.environment
        // is "development". A mismatched pin ("staging") must NOT change evaluation: we expect the
        // development override (false), not the default (true), and not a staging-scoped null.
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", "\"v1\"")
                .WithBody(DeliveryEnvelopeJson()));

        await using var client = new Quonfig(new QuonfigOptions
        {
            SdkKey = SdkKey,
            ApiUrls = new[] { server.Urls[0] },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromSeconds(5),
            // Mismatched pin — must be ignored in delivery mode.
            Environment = "staging",
        });
        await client.InitAsync();

        // meta.environment ("development") wins: development override = false.
        client.GetBool("flag.delivery").Should().Be(false);

        var details = client.GetBoolDetails("flag.delivery");
        details.Metadata["environment"].Should().Be("development");
    }

    [Fact]
    public async Task HttpMode_WarnsWhenEnvironmentPinnedInDeliveryMode()
    {
        // qfg-pinh: setting a pin in delivery mode is a no-op for evaluation, so the SDK must WARN
        // once at init through its logger to make the mis-configuration visible.
        var sink = new CapturingLogger();

        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", "\"v1\"")
                .WithBody(DeliveryEnvelopeJson()));

        await using var client = new Quonfig(new QuonfigOptions
        {
            SdkKey = SdkKey,
            ApiUrls = new[] { server.Urls[0] },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromSeconds(5),
            Environment = "staging",
            Logger = sink,
        });
        await client.InitAsync();

        sink.Warnings.Should().ContainSingle(m =>
            m.Contains("environment 'staging' was set", StringComparison.Ordinal)
            && m.Contains("delivery (SDK-key) mode", StringComparison.Ordinal)
            && m.Contains("determined by the SDK key", StringComparison.Ordinal));
    }

    /// <summary>
    /// Minimal <see cref="ILogger"/> that captures Warning-level messages for assertions. Records
    /// only the formatted message text; scope/state shape is irrelevant for these tests.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public ConcurrentQueue<string> WarningQueue { get; } = new();

        public IReadOnlyList<string> Warnings => new List<string>(WarningQueue);

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                WarningQueue.Enqueue(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
