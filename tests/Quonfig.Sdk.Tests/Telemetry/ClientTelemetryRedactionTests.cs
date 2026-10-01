using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk;
using Quonfig.Sdk.Telemetry;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests.Telemetry;

/// <summary>
/// Regression guard (qfg-2agi.16): a confidential value evaluated through the PUBLIC client must
/// reach the drained evaluation-summary payload only in its redacted "*****&lt;md5[0:5]&gt;" form.
/// The collector-level tests cannot catch the call site in <c>Quonfig.cs</c> dropping the
/// reportable marker; this one drives the real client path end to end.
/// </summary>
public sealed class ClientTelemetryRedactionTests
{
    private const string Plaintext = "hunter2-super-secret-plaintext";

    private const string EnvelopeJson =
        "{\"meta\":{\"version\":\"v1\",\"environment\":\"production\",\"generation\":5}," +
        "\"configs\":[{\"id\":\"c-secret\",\"key\":\"secret.api\",\"type\":\"config\",\"valueType\":\"string\"," +
        "\"default\":{\"rules\":[{\"criteria\":[{\"operator\":\"ALWAYS_TRUE\"}]," +
        "\"value\":{\"type\":\"string\",\"value\":\"" + Plaintext + "\",\"confidential\":true}}]}}]}";

    private sealed class CapturingSender : ITelemetrySender
    {
        private readonly object _gate = new();
        private readonly List<IDictionary<string, object?>> _envelopes = new();

        public IDictionary<string, object?>[] Envelopes
        {
            get { lock (_gate) { return _envelopes.ToArray(); } }
        }

        public Task SendAsync(IDictionary<string, object?> payload, CancellationToken cancellationToken)
        {
            lock (_gate) { _envelopes.Add(payload); }
            return Task.CompletedTask;
        }
    }

    // "*****" + first five hex chars of md5("hunter2-super-secret-plaintext"), the cross-SDK marker.
    private const string ExpectedMarker = "*****50a0e";

    [Fact]
    public async Task ConfidentialValue_ThroughPublicClient_IsRedactedInDrainedSummary()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", "\"v1\"")
                .WithBody(EnvelopeJson));

        var sender = new CapturingSender();
        await using var client = new Quonfig(new QuonfigOptions
        {
            SdkKey = "telemetry-redaction-key",
            ApiUrls = new[] { server.Urls[0] },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromSeconds(5),
            TelemetrySender = sender,
            TelemetryInitialDelay = TimeSpan.FromMinutes(5),
            TelemetryFlushInterval = TimeSpan.FromMinutes(5),
            OnNoDefault = OnNoDefault.Ignore,
        });

        await client.InitAsync();

        // The caller still gets the real value; only telemetry is redacted.
        client.GetString("secret.api").Should().Be(Plaintext);

        await client.CloseAsync();

        sender.Envelopes.Should().NotBeEmpty();
        var wire = JsonSerializer.Serialize(sender.Envelopes);

        wire.Should().NotContain(Plaintext, "a confidential value must never reach the telemetry wire in plaintext");

        var counter = sender.Envelopes
            .SelectMany(env => (IEnumerable<IDictionary<string, object?>>)env["events"]!)
            .Where(e => e.ContainsKey("summaries"))
            .Select(e => (IDictionary<string, object?>)e["summaries"]!)
            .SelectMany(s => (IEnumerable<Dictionary<string, object?>>)s["summaries"]!)
            .Where(s => (string?)s["key"] == "secret.api")
            .SelectMany(s => (IEnumerable<Dictionary<string, object?>>)s["counters"]!)
            .Should().ContainSingle().Subject;

        var selected = (IDictionary<string, object?>)counter["selectedValue"]!;
        selected.Should().ContainKey("string");
        selected["string"].Should().Be(ExpectedMarker);
    }
}
