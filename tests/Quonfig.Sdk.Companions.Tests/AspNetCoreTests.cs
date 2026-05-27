using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quonfig.Sdk.AspNetCore;
using Xunit;

namespace Quonfig.Sdk.Companions.Tests;

/// <summary>
/// Minimal-API + WebApplicationFactory integration tests for the AspNetCore companion.
/// Stands up a real Kestrel-less test server, exercises the full DI graph, and asserts
/// both <see cref="IQuonfig"/> and <see cref="IBoundQuonfig"/> injection.
/// </summary>
public sealed class AspNetCoreTests
{
    private static TestWorkspace CreateWorkspace()
    {
        var ws = new TestWorkspace();
        ws.WriteManifest("production");
        ws.WriteBoolFlag("flag1.flag.json", "always-on", true);
        ws.WritePlanGatedFlag("plan-flag.flag.json", "beta.dashboard", onPlan: true, offPlan: false);
        return ws;
    }

    private static WebApplication BuildApp(TestWorkspace ws)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddQuonfig(opts =>
        {
            opts.Datadir = ws.Root;
            opts.Environment = "production";
        });

        var app = builder.Build();

        // Per-request context: pull plan from a request header so the test can drive both branches.
        app.UseQuonfigContext((http, ctx) =>
        {
            if (http.Request.Headers.TryGetValue("X-Plan", out var plan))
            {
                ctx["user"] = new ContextProperties { ["plan"] = plan.ToString() };
            }
        });

        app.MapGet("/iquonfig", (IQuonfig q) => q.IsFeatureEnabled("always-on") ? "yes" : "no");
        app.MapGet("/ibound", (IBoundQuonfig q) => q.IsFeatureEnabled("beta.dashboard") ? "yes" : "no");

        return app;
    }

    [Fact]
    public async Task AddQuonfig_RegistersIQuonfigAsSingleton_AndInitsViaHostedService()
    {
        using var ws = CreateWorkspace();
        var app = BuildApp(ws);
        await app.StartAsync();
        try
        {
            using var client = app.GetTestClient();
            var resp = await client.GetAsync(new System.Uri("/iquonfig", System.UriKind.Relative));
            resp.IsSuccessStatusCode.Should().BeTrue();
            (await resp.Content.ReadAsStringAsync()).Should().Be("yes");

            // Hosted service must have init'd Quonfig — the singleton's LastSuccessfulRefresh is non-null.
            var quonfig = app.Services.GetRequiredService<IQuonfig>();
            quonfig.LastSuccessfulRefresh.Should().NotBeNull();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task UseQuonfigContext_BindsPerRequestContext_AndExposesBoundQuonfig()
    {
        using var ws = CreateWorkspace();
        var app = BuildApp(ws);
        await app.StartAsync();
        try
        {
            using var client = app.GetTestClient();

            // Pro user: gated flag flips on.
            using var proReq = new HttpRequestMessage(HttpMethod.Get, "/ibound");
            proReq.Headers.Add("X-Plan", "pro");
            var proResp = await client.SendAsync(proReq);
            (await proResp.Content.ReadAsStringAsync()).Should().Be("yes");

            // Free user: gated flag stays off.
            using var freeReq = new HttpRequestMessage(HttpMethod.Get, "/ibound");
            freeReq.Headers.Add("X-Plan", "free");
            var freeResp = await client.SendAsync(freeReq);
            (await freeResp.Content.ReadAsStringAsync()).Should().Be("no");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task IBoundQuonfig_WithoutMiddleware_ResolvesEmptyContext()
    {
        using var ws = CreateWorkspace();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddQuonfig(opts =>
        {
            opts.Datadir = ws.Root;
            opts.Environment = "production";
        });

        var app = builder.Build();
        // NOTE: UseQuonfigContext intentionally NOT called — fallback empty-context path.
        app.MapGet("/ibound", (IBoundQuonfig q) =>
            q.IsFeatureEnabled("beta.dashboard") ? "yes" : "no");

        await app.StartAsync();
        try
        {
            using var client = app.GetTestClient();
            // beta.dashboard's plan-gated rule never matches without a context → falls to default-off.
            var resp = await client.GetAsync(new System.Uri("/ibound", System.UriKind.Relative));
            (await resp.Content.ReadAsStringAsync()).Should().Be("no");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
