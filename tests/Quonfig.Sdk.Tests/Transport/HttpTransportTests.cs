using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk;
using Quonfig.Sdk.Transport;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests.Transport;

/// <summary>
/// WireMock.Net coverage of <see cref="HttpTransport"/>: auth/version headers,
/// ETag round-trip, 304 handling, primary→secondary failover on transport error
/// and on 5xx, and HTTP timeout. Mirrors the contract exercised by sdk-java's
/// <c>HttpTransportTest</c> and sdk-python's <c>test_transport_lifecycle</c>.
/// </summary>
public sealed class HttpTransportTests
{
    private const string SdkKey = "test-sdk-key";

    private static string ExpectedAuth()
    {
        string raw = "1:" + SdkKey;
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    private static string ExpectedVersion() => "dotnet/" + SdkInfo.Version;

    private static string EnvelopeJson(string env = "production")
    {
        // Minimal valid ConfigEnvelope wire JSON: empty configs array + meta block.
        return $"{{\"configs\":[],\"meta\":{{\"version\":\"v1\",\"environment\":\"{env}\",\"workspaceId\":\"ws-test\"}}}}";
    }

    [Fact]
    public async Task FetchAsync_SendsBasicAuthAndSdkVersionHeaders()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", "\"abc\"")
                .WithBody(EnvelopeJson()));

        using var transport = new HttpTransport(new[] { new Uri(server.Urls[0]) }, SdkKey);

        var envelope = await transport.FetchAsync(null, CancellationToken.None);

        envelope.Should().NotBeNull();
        var logs = server.LogEntries.ToList();
        logs.Should().HaveCount(1);
        var headers = logs[0].RequestMessage.Headers!;
        headers["Authorization"].ToString().Should().Be(ExpectedAuth());
        headers["X-Quonfig-SDK-Version"].ToString().Should().Be(ExpectedVersion());
    }

    [Fact]
    public async Task FetchAsync_SendsIfNoneMatchAndReturnsNullOn304()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet()
                .WithHeader("If-None-Match", "\"prev\""))
            .RespondWith(Response.Create().WithStatusCode(304));

        using var transport = new HttpTransport(new[] { new Uri(server.Urls[0]) }, SdkKey);

        var envelope = await transport.FetchAsync("\"prev\"", CancellationToken.None);

        envelope.Should().BeNull("304 means caller's cached envelope is still current");
        server.LogEntries.Should().HaveCount(1);
    }

    [Fact]
    public async Task FetchAsync_StoresResponseETagForNextCall()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", "\"v42\"")
                .WithBody(EnvelopeJson()));

        using var transport = new HttpTransport(new[] { new Uri(server.Urls[0]) }, SdkKey);

        var envelope = await transport.FetchAsync(null, CancellationToken.None);

        envelope.Should().NotBeNull();
        transport.LastETag.Should().Be("\"v42\"");
    }

    [Fact]
    public async Task FetchAsync_FailsOverToSecondaryOn500()
    {
        using var primary = WireMockServer.Start();
        using var secondary = WireMockServer.Start();
        primary
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));
        secondary
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", "\"sec\"")
                .WithBody(EnvelopeJson()));

        using var transport = new HttpTransport(
            new[] { new Uri(primary.Urls[0]), new Uri(secondary.Urls[0]) }, SdkKey);

        var envelope = await transport.FetchAsync(null, CancellationToken.None);

        envelope.Should().NotBeNull();
        envelope!.Meta.Should().NotBeNull();
        primary.LogEntries.Should().HaveCount(1);
        secondary.LogEntries.Should().HaveCount(1);
        transport.LastETag.Should().Be("\"sec\"");
    }

    [Fact]
    public async Task FetchAsync_FailsOverOnConnectionError()
    {
        // Bind to a closed port: start a server then dispose it to get a guaranteed
        // unreachable URL. Mirrors sdk-java's reference test.
        var deadServer = WireMockServer.Start();
        var deadUrl = new Uri(deadServer.Urls[0]);
        deadServer.Stop();
        deadServer.Dispose();

        using var live = WireMockServer.Start();
        live
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(EnvelopeJson()));

        using var transport = new HttpTransport(
            new[] { deadUrl, new Uri(live.Urls[0]) }, SdkKey);

        var envelope = await transport.FetchAsync(null, CancellationToken.None);

        envelope.Should().NotBeNull();
        live.LogEntries.Should().HaveCount(1);
    }

    [Fact]
    public async Task FetchAsync_AllUrlsFail_ThrowsQuonfigException()
    {
        using var primary = WireMockServer.Start();
        using var secondary = WireMockServer.Start();
        primary
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));
        secondary
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(502));

        using var transport = new HttpTransport(
            new[] { new Uri(primary.Urls[0]), new Uri(secondary.Urls[0]) }, SdkKey);

        Func<Task> act = () => transport.FetchAsync(null, CancellationToken.None);

        await act.Should().ThrowAsync<Exceptions.QuonfigException>();
    }

    [Fact]
    public async Task FetchAsync_RespectsHttpTimeout()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(EnvelopeJson())
                .WithDelay(TimeSpan.FromMilliseconds(500)));

        using var transport = new HttpTransport(
            new[] { new Uri(server.Urls[0]) }, SdkKey,
            timeout: TimeSpan.FromMilliseconds(100));

        Func<Task> act = () => transport.FetchAsync(null, CancellationToken.None);

        await act.Should().ThrowAsync<Exceptions.QuonfigException>();
    }

    [Fact]
    public async Task FetchAsync_PerUrlTimeout_FailsOverWhenPrimaryHangs()
    {
        // f02 mechanism (qfg-7h5d.1.11): a hung primary must not consume the whole budget. The
        // primary "answers" — but only after 5s, far beyond the 300ms per-URL deadline; the
        // secondary answers instantly. With the per-URL timeout the primary leg aborts at ~300ms
        // and the fetch resolves off the secondary fast. WITHOUT it (the bug), the primary's 5s
        // response would win first (LastResolvedIndex == 0) and the fetch would block ~5s — so this
        // test asserts BOTH the resolved leg and the wall-clock bound, the two things the fix changes.
        using var primary = WireMockServer.Start();
        using var secondary = WireMockServer.Start();
        primary
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(EnvelopeJson("primary-env"))
                .WithDelay(TimeSpan.FromSeconds(5)));
        secondary
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(EnvelopeJson("secondary-env")));

        // Overall ceiling generous (10s) so the per-URL deadline — not HttpClient.Timeout — is what
        // sheds the hung primary.
        using var transport = new HttpTransport(
            new[] { new Uri(primary.Urls[0]), new Uri(secondary.Urls[0]) }, SdkKey,
            timeout: TimeSpan.FromSeconds(10),
            configFetchTimeout: TimeSpan.FromMilliseconds(300));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var envelope = await transport.FetchAsync(null, CancellationToken.None);
        sw.Stop();

        envelope.Should().NotBeNull();
        envelope!.Meta!.Environment.Should().Be("secondary-env", "the hung primary must fail over to the secondary");
        transport.LastResolvedIndex.Should().Be(1, "the secondary (index 1) served the fetch");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3),
            "failover must not wait for the primary's 5s response — the per-URL deadline sheds it at ~300ms");
    }

    [Fact]
    public async Task FetchAsync_RecordsResolvedIndexForPrimary()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(EnvelopeJson()));

        using var transport = new HttpTransport(new[] { new Uri(server.Urls[0]) }, SdkKey);

        transport.LastResolvedIndex.Should().Be(-1, "no fetch has resolved yet");
        await transport.FetchAsync(null, CancellationToken.None);
        transport.LastResolvedIndex.Should().Be(0, "the primary (index 0) served the fetch");
    }

    [Fact]
    public void Constructor_RejectsEmptyApiUrls()
    {
        Action act = () =>
        {
            using var _ = new HttpTransport(Array.Empty<Uri>(), SdkKey);
        };
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_RejectsNullSdkKey()
    {
        Action act = () =>
        {
            using var _ = new HttpTransport(new[] { new Uri("http://x/") }, null!);
        };
        act.Should().Throw<ArgumentNullException>();
    }
}
