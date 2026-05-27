using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Telemetry;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests.Telemetry;

/// <summary>
/// WireMock.Net coverage of <see cref="HttpTelemetrySender"/>: POST endpoint, auth + SDK
/// version headers, JSON body shape, and throw-on-non-2xx behavior.
/// </summary>
public sealed class HttpTelemetrySenderTests
{
    private const string SdkKey = "test-sdk-key";

    private static string ExpectedAuth()
    {
        string raw = "1:" + SdkKey;
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    private static string ExpectedVersion() => "dotnet/" + SdkInfo.Version;

    private static Dictionary<string, object?> SamplePayload() => new()
    {
        ["instanceHash"] = "abc-123",
        ["events"] = new List<object?> { new Dictionary<string, object?> { ["k"] = "v" } },
    };

    [Fact]
    public async Task send_async_posts_to_api_v1_telemetry_with_auth_and_version_headers()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v1/telemetry/").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sender = new HttpTelemetrySender(new Uri(server.Urls[0]), SdkKey);
        await sender.SendAsync(SamplePayload(), CancellationToken.None);

        var logs = server.LogEntries.ToList();
        logs.Should().HaveCount(1);
        var req = logs[0].RequestMessage;
        req.Method.Should().Be("POST");
        req.AbsolutePath.Should().Be("/api/v1/telemetry/");
        req.Headers!["Authorization"].ToString().Should().Be(ExpectedAuth());
        req.Headers["X-Quonfig-SDK-Version"].ToString().Should().Be(ExpectedVersion());
    }

    [Fact]
    public async Task send_async_writes_json_body()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v1/telemetry/").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sender = new HttpTelemetrySender(new Uri(server.Urls[0]), SdkKey);
        await sender.SendAsync(SamplePayload(), CancellationToken.None);

        var body = server.LogEntries.Single().RequestMessage.Body;
        body.Should().NotBeNull();
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("instanceHash").GetString().Should().Be("abc-123");
        doc.RootElement.GetProperty("events").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task send_async_throws_on_4xx()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v1/telemetry/").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401));

        using var sender = new HttpTelemetrySender(new Uri(server.Urls[0]), SdkKey);
        Func<Task> act = () => sender.SendAsync(SamplePayload(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task send_async_throws_on_5xx()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v1/telemetry/").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(503));

        using var sender = new HttpTelemetrySender(new Uri(server.Urls[0]), SdkKey);
        Func<Task> act = () => sender.SendAsync(SamplePayload(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task send_async_strips_trailing_slash_from_base_url()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v1/telemetry/").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        // Base URL with a trailing slash should still resolve to /api/v1/telemetry/ (not //api/...).
        using var sender = new HttpTelemetrySender(new Uri(server.Urls[0] + "/"), SdkKey);
        await sender.SendAsync(SamplePayload(), CancellationToken.None);

        server.LogEntries.Single().RequestMessage.AbsolutePath.Should().Be("/api/v1/telemetry/");
    }
}
