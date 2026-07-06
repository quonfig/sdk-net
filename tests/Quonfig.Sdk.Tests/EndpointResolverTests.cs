using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Quonfig.Sdk;
using Xunit;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// Endpoint resolution (qfg-41nh.27): the cross-SDK <c>QUONFIG_DOMAIN</c> convention plus the
/// derive-stream-from-api rule that makes an explicit <c>ApiUrls</c> override actually stream from
/// the matching host instead of the production stream cluster. Mirrors sdk-go's
/// <c>options.go</c> + <c>stream_url.go</c> behavior.
/// </summary>
public sealed class EndpointResolverTests
{
    private static Func<string, string?> Env(params (string, string)[] pairs) =>
        name =>
        {
            foreach (var (k, v) in pairs)
            {
                if (k == name) return v;
            }
            return null;
        };

    private static readonly Func<string, string?> NoEnv = _ => null;

    // ---------------- DeriveStreamUrl ----------------

    [Theory]
    [InlineData("https://primary.quonfig.com", "https://stream.primary.quonfig.com")]
    [InlineData("https://secondary.quonfig.com", "https://stream.secondary.quonfig.com")]
    [InlineData("http://localhost:8080", "http://stream.localhost:8080")]
    [InlineData("https://api-delivery.localhost", "https://stream.api-delivery.localhost")]
    [InlineData("https://primary.quonfig.com/prefix", "https://stream.primary.quonfig.com/prefix")]
    [InlineData("http://127.0.0.1:54321", "http://stream.127.0.0.1:54321")]
    public void DeriveStreamUrl_PrependsStreamToHost_PreservingSchemePortPath(string input, string expected)
    {
        EndpointResolver.DeriveStreamUrl(input).Should().Be(expected);
    }

    [Fact]
    public void DeriveStreamUrl_ReturnsInputUnchanged_WhenNotAbsoluteUrl()
    {
        EndpointResolver.DeriveStreamUrl("not a url").Should().Be("not a url");
        EndpointResolver.DeriveStreamUrl("").Should().Be("");
    }

    [Fact]
    public void DeriveStreamUrl_OfDefaultApiUrls_EqualsBuiltInDefaultStreamUrls()
    {
        // The no-explicit-StreamUrls path derives from the api URLs; that derivation must reproduce
        // the historical default stream cluster exactly (no trailing-slash drift).
        EndpointResolver.DeriveStreamUrl(QuonfigOptions.DefaultApiUrls[0])
            .Should().Be(QuonfigOptions.DefaultStreamUrls[0]);
        EndpointResolver.DeriveStreamUrl(QuonfigOptions.DefaultApiUrls[1])
            .Should().Be(QuonfigOptions.DefaultStreamUrls[1]);
    }

    // ---------------- Resolve: defaults ----------------

    [Fact]
    public void Resolve_NoDomain_NoExplicit_UsesProductionDefaults()
    {
        var (api, stream, telemetry) = EndpointResolver.Resolve(new QuonfigOptions(), NoEnv);

        api.Should().Equal("https://primary.quonfig.com", "https://secondary.quonfig.com");
        stream.Should().Equal("https://stream.primary.quonfig.com", "https://stream.secondary.quonfig.com");
        telemetry.Should().Be("https://telemetry.quonfig.com");
    }

    // ---------------- Resolve: QUONFIG_DOMAIN ----------------

    [Fact]
    public void Resolve_QuonfigDomain_DerivesAllHosts()
    {
        var env = Env(("QUONFIG_DOMAIN", "quonfig-staging.com"));

        var (api, stream, telemetry) = EndpointResolver.Resolve(new QuonfigOptions(), env);

        api.Should().Equal(
            "https://primary.quonfig-staging.com",
            "https://secondary.quonfig-staging.com");
        stream.Should().Equal(
            "https://stream.primary.quonfig-staging.com",
            "https://stream.secondary.quonfig-staging.com");
        telemetry.Should().Be("https://telemetry.quonfig-staging.com");
    }

    // ---------------- Resolve: explicit precedence ----------------

    [Fact]
    public void Resolve_ExplicitApiUrls_WinOverDomain_AndStreamFollowsThem()
    {
        // THE BUG FIX: a customer overriding only ApiUrls must stream from the matching hosts, not
        // the production (or domain) stream cluster.
        var env = Env(("QUONFIG_DOMAIN", "quonfig-staging.com"));
        var opts = new QuonfigOptions
        {
            ApiUrls = new[] { "https://primary.my-proxy.example", "https://secondary.my-proxy.example" },
        };

        var (api, stream, telemetry) = EndpointResolver.Resolve(opts, env);

        api.Should().Equal(
            "https://primary.my-proxy.example",
            "https://secondary.my-proxy.example");
        stream.Should().Equal(
            "https://stream.primary.my-proxy.example",
            "https://stream.secondary.my-proxy.example");
        // TelemetryUrl was not explicit, so the domain still applies to it.
        telemetry.Should().Be("https://telemetry.quonfig-staging.com");
    }

    [Fact]
    public void Resolve_ExplicitStreamUrls_WinOverDerivation()
    {
        var opts = new QuonfigOptions
        {
            ApiUrls = new[] { "https://primary.my-proxy.example" },
            StreamUrls = new[] { "https://custom-stream.example" },
        };

        var (_, stream, _) = EndpointResolver.Resolve(opts, NoEnv);

        stream.Should().Equal("https://custom-stream.example");
    }

    [Fact]
    public void Resolve_ExplicitTelemetryUrl_WinsOverDomain()
    {
        var env = Env(("QUONFIG_DOMAIN", "quonfig-staging.com"));
        var opts = new QuonfigOptions { TelemetryUrl = "https://telemetry.custom.example" };

        var (_, _, telemetry) = EndpointResolver.Resolve(opts, env);

        telemetry.Should().Be("https://telemetry.custom.example");
    }

    [Fact]
    public void Resolve_EmptyDomain_IsIgnored_UsesDefaults()
    {
        var env = Env(("QUONFIG_DOMAIN", ""));

        var (api, _, telemetry) = EndpointResolver.Resolve(new QuonfigOptions(), env);

        api.Should().Equal("https://primary.quonfig.com", "https://secondary.quonfig.com");
        telemetry.Should().Be("https://telemetry.quonfig.com");
    }

    // ---------------- Client-level wiring ----------------

    [Fact]
    public async Task Client_WithExplicitApiUrlsOnly_DerivesStreamUrlsFromThem()
    {
        // End-to-end: the client (not just the resolver) must adopt the derived stream URLs.
        var opts = new QuonfigOptions
        {
            SdkKey = "test-key",
            ApiUrls = new[] { "https://primary.staging.example", "https://secondary.staging.example" },
            EnvLookup = NoEnv,
            OnInitFailure = OnInitFailure.ReturnDefaults,
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
        };

        await using var client = new Quonfig(opts);

        client.ResolvedStreamUrls.Should().Equal(
            "https://stream.primary.staging.example",
            "https://stream.secondary.staging.example");
    }

    [Fact]
    public async Task Client_WithSingleExplicitApiUrl_LogsFailoverDisabledWarning()
    {
        var recorder = new WarnRecorder();
        var opts = new QuonfigOptions
        {
            SdkKey = "test-key",
            ApiUrls = new[] { "https://only.example" },
            EnvLookup = NoEnv,
            OnInitFailure = OnInitFailure.ReturnDefaults,
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
            Logger = recorder,
        };

        await using var client = new Quonfig(opts);

        recorder.Warnings.Should().Contain(
            m => m.Contains("explicit ApiUrls disables automatic failover to the secondary"));
    }

    [Fact]
    public async Task Client_WithBothApiUrls_DoesNotLogFailoverWarning()
    {
        var recorder = new WarnRecorder();
        var opts = new QuonfigOptions
        {
            SdkKey = "test-key",
            ApiUrls = new[] { "https://primary.example", "https://secondary.example" },
            EnvLookup = NoEnv,
            OnInitFailure = OnInitFailure.ReturnDefaults,
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
            Logger = recorder,
        };

        await using var client = new Quonfig(opts);

        recorder.Warnings.Should().NotContain(
            m => m.Contains("disables automatic failover"));
    }

    /// <summary>Capture-only ILogger that records formatted Warning messages.</summary>
    private sealed class WarnRecorder : ILogger
    {
        private readonly object _gate = new();
        private readonly List<string> _warnings = new();

        public IReadOnlyList<string> Warnings
        {
            get { lock (_gate) { return _warnings.ToArray(); } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(MelLogLevel logLevel) => true;

        public void Log<TState>(MelLogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != MelLogLevel.Warning) return;
            lock (_gate) { _warnings.Add(formatter(state, exception)); }
        }
    }
}
