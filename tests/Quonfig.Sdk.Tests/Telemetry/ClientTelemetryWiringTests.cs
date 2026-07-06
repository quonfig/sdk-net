using System;
using System.Collections.Generic;
using System.Linq;
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
/// Client-level telemetry wiring (qfg-gxm6): proves the LIVE client actually constructs its
/// <see cref="TelemetryReporter"/>, feeds the collectors from real evaluation + failover call sites,
/// and posts an envelope through the sender. Before this bead the reporter was never constructed, so
/// sdk-net emitted zero runtime telemetry of any type (eval summaries, context shapes, AND the
/// qfg-41nh.18 failover counters). The test injects a capturing sender so nothing leaves the process.
/// </summary>
public sealed class ClientTelemetryWiringTests
{
    private const string SdkKey = "telemetry-wiring-key";

    // A bool flag with an ALWAYS_TRUE static rule so a GetBool resolves to a real match (which
    // records an evaluation summary). Generation 5 so the init install records a failover
    // resolvedFromPrimary counter.
    private const string EnvelopeJson =
        "{\"meta\":{\"version\":\"v1\",\"environment\":\"production\",\"generation\":5}," +
        "\"configs\":[{\"id\":\"c-tel\",\"key\":\"flag.telemetry\",\"type\":\"feature_flag\",\"valueType\":\"bool\"," +
        "\"default\":{\"rules\":[{\"criteria\":[{\"operator\":\"ALWAYS_TRUE\"}],\"value\":{\"type\":\"bool\",\"value\":true}}]}}]}";

    private static WireMockServer StartUpstream()
    {
        var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", "\"v1\"")
                .WithBody(EnvelopeJson));
        return server;
    }

    /// <summary>Records every envelope the reporter sends; never touches the network.</summary>
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

    private static IEnumerable<IDictionary<string, object?>> AllEvents(CapturingSender sender)
    {
        foreach (var env in sender.Envelopes)
        {
            if (env.TryGetValue("events", out var evObj) && evObj is IEnumerable<IDictionary<string, object?>> events)
            {
                foreach (var e in events)
                {
                    yield return e;
                }
            }
        }
    }

    [Fact]
    public async Task LiveClient_EmitsFailoverAndEvaluationSummaryTelemetry()
    {
        using var server = StartUpstream();
        var sender = new CapturingSender();

        await using var client = new Quonfig(new QuonfigOptions
        {
            SdkKey = SdkKey,
            ApiUrls = new[] { server.Urls[0] },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromSeconds(5),
            // Inject a capturing sender (no network) and push the periodic loop far out so the ONLY
            // flush is the deterministic one on CloseAsync — no timing race.
            TelemetrySender = sender,
            TelemetryInitialDelay = TimeSpan.FromMinutes(5),
            TelemetryFlushInterval = TimeSpan.FromMinutes(5),
            OnNoDefault = OnNoDefault.Ignore,
        });

        await client.InitAsync();

        // A real evaluation on the live client path → records an evaluation summary + context shape.
        client.GetBool("flag.telemetry").Should().BeTrue();

        // CloseAsync flushes the reporter synchronously, draining every collector into one envelope.
        await client.CloseAsync();

        sender.Envelopes.Should().NotBeEmpty("the live client must construct and flush a reporter");

        var events = AllEvents(sender).ToList();

        // instanceHash is present and non-empty on the envelope.
        sender.Envelopes[0].Should().ContainKey("instanceHash");
        ((string)sender.Envelopes[0]["instanceHash"]!).Should().NotBeNullOrEmpty();

        // Failover counters flow (qfg-41nh.18 emission is no longer dead): the init install recorded
        // resolvedFromPrimary.
        var failover = events.FirstOrDefault(e => e.ContainsKey("failover"));
        failover.Should().NotBeNull("the init install must have recorded a failover resolvedFrom counter");
        var fo = (IDictionary<string, object?>)failover!["failover"]!;
        ((long)fo["resolvedFromPrimary"]!).Should().BeGreaterThanOrEqualTo(1L);

        // Evaluation summaries flow: the GetBool produced a summary row.
        events.Should().Contain(e => e.ContainsKey("summaries"),
            "a resolved evaluation on the live client must produce an evaluation-summary event");
    }

    [Fact]
    public async Task TelemetryFullyDisabled_ConstructsNoReporter_SendsNothing()
    {
        using var server = StartUpstream();
        var sender = new CapturingSender();

        await using var client = new Quonfig(new QuonfigOptions
        {
            SdkKey = SdkKey,
            ApiUrls = new[] { server.Urls[0] },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromSeconds(5),
            TelemetrySender = sender,
            // Full opt-out: no eval summaries AND no context uploads → no reporter, nothing recorded.
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
            OnNoDefault = OnNoDefault.Ignore,
        });

        await client.InitAsync();
        client.GetBool("flag.telemetry").Should().BeTrue();
        await client.CloseAsync();

        sender.Envelopes.Should().BeEmpty("a full telemetry opt-out must construct no reporter and send nothing");
        client.Failover.Should().BeNull("the failover collector is gated on telemetry being enabled");
    }
}
