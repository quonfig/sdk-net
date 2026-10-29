using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk;
using Quonfig.Sdk.Tests.Telemetry.Helpers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// qfg-6vno.12 (same class as sdk-go qfg-eefx): <c>OnConfigChange</c> must be delivered AFTER the
/// install lock is released. Before the fix the handler ran inside <c>InstallFromNetwork</c>'s
/// <c>_installLock</c>, so a handler that waited on another thread doing an install (a refresh, the
/// poller, an SSE update) deadlocked against itself, and a slow handler stalled every install path.
/// Each test makes the handler wait (bounded) on a refresh running on ANOTHER thread: under the lock
/// that refresh cannot finish; after the fix it does. Handlers stay serialized and in order: a
/// change installed while a handler runs is delivered after it returns, never concurrently.
/// </summary>
public sealed class OnConfigChangeReentrancyTests
{
    private const string SdkKey = "test-sdk-key";
    private const string ConfigsPath = "/api/v2/configs";
    private const string SsePath = "/api/v2/sse/config";
    private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(3);

    private static string EnvelopeJson(int generation) =>
        "{\"configs\":[],\"meta\":{\"version\":\"v1\",\"environment\":\"production\",\"generation\":" + generation + "}}";

    private static void ServeConfigs(WireMockServer server, int generation) =>
        server
            .Given(Request.Create().WithPath(ConfigsPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(EnvelopeJson(generation)));

    private static QuonfigOptions Options(WireMockServer server, bool sse, Action onChange) => new()
    {
        SdkKey = SdkKey,
        ApiUrls = new[] { server.Urls[0] },
        StreamUrls = sse ? new[] { server.Urls[0] } : Array.Empty<string>(),
        InitTimeout = TimeSpan.FromSeconds(10),
        CollectEvaluationSummaries = false,
        ContextUploadMode = ContextUploadMode.None,
        OnConfigChange = onChange,
    };

    /// <summary>
    /// Records every handler invocation and, on the configured call numbers, runs a RefreshAsync on
    /// another thread and waits (bounded) for it. <see cref="RefreshCompleted"/> is false if that
    /// refresh could not finish while the handler ran (i.e. the handler held the install lock).
    /// </summary>
    private sealed class RefreshingHandler
    {
        private readonly TaskCompletionSource<Quonfig> _clientSet = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<int, bool> _refreshOnCall;
        private readonly Task _beforeRefresh;
        private int _calls;
        private int _active;

        public RefreshingHandler(Func<int, bool> refreshOnCall, Task? beforeRefresh = null)
        {
            _refreshOnCall = refreshOnCall;
            _beforeRefresh = beforeRefresh ?? Task.CompletedTask;
        }

        public ConcurrentQueue<bool> RefreshCompleted { get; } = new();

        public int Calls => Volatile.Read(ref _calls);

        public bool Overlapped { get; private set; }

        public void Attach(Quonfig client)
        {
            _clientSet.TrySetResult(client);
        }

        public void Handle()
        {
            if (Interlocked.Increment(ref _active) > 1) Overlapped = true;
            try
            {
                int call = Interlocked.Increment(ref _calls);
                if (!_refreshOnCall(call)) return;
                if (!_clientSet.Task.Wait(HandlerWait)) throw new TimeoutException("client never attached");
                var client = _clientSet.Task.Result;
                if (!_beforeRefresh.Wait(HandlerWait)) throw new TimeoutException("test never opened the refresh gate");
                bool done = Task.Run(() => client.RefreshAsync()).Wait(HandlerWait);
                RefreshCompleted.Enqueue(done);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    // net48-compatible stand-in for Task.WaitAsync(timeout).
    private static async Task Within(Task task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
        {
            throw new TimeoutException("task did not complete in time");
        }
        await task;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task InitPath_HandlerCanWaitOnARefresh_AndLaterUpdatesStillInstall()
    {
        using var server = WireMockServer.Start();
        ServeConfigs(server, 42);
        var handler = new RefreshingHandler(call => call == 1);

        await using var client = new Quonfig(Options(server, sse: false, handler.Handle));
        handler.Attach(client);
        await client.InitAsync();
        await WaitUntil(() => !handler.RefreshCompleted.IsEmpty, TimeSpan.FromSeconds(10));

        handler.RefreshCompleted.Should().ContainSingle()
            .Which.Should().BeTrue("a refresh on another thread must not block on a handler fired by the init install");

        server.Reset();
        ServeConfigs(server, 43);
        await Within(client.RefreshAsync(), TimeSpan.FromSeconds(5));

        client.HeldGeneration.Should().Be(43, "later installs still go through after the handler returned");
        await WaitUntil(() => handler.Calls >= 2, TimeSpan.FromSeconds(5));
        handler.Calls.Should().Be(2);
        handler.Overlapped.Should().BeFalse("handlers are delivered serially");
    }

    [Fact]
    public async Task PollPath_HandlerCanWaitOnARefresh_AndTheChangeItInstallsIsDeliveredAfter()
    {
        using var server = WireMockServer.Start();
        ServeConfigs(server, 42);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RefreshingHandler(call => call == 2, gate.Task);

        await using var client = new Quonfig(Options(server, sse: false, handler.Handle));
        handler.Attach(client);
        await client.InitAsync();
        await WaitUntil(() => handler.Calls >= 1, TimeSpan.FromSeconds(5));

        // Refresh installs gen 43 -> handler call 2 runs a nested refresh that installs gen 44.
        server.Reset();
        ServeConfigs(server, 43);
        var refresh = client.RefreshAsync();
        await WaitUntil(() => handler.Calls >= 2, TimeSpan.FromSeconds(5));
        server.Reset();
        ServeConfigs(server, 44);
        gate.SetResult(true);
        await Within(refresh, TimeSpan.FromSeconds(10));
        await WaitUntil(() => handler.Calls >= 3, TimeSpan.FromSeconds(10));

        handler.RefreshCompleted.Should().ContainSingle()
            .Which.Should().BeTrue("a refresh on another thread must not block on a handler fired by a poll install");
        client.HeldGeneration.Should().Be(44);
        handler.Calls.Should().Be(3, "the nested install is delivered once the running handler returns");
        handler.Overlapped.Should().BeFalse("handlers are delivered serially");
    }

    [Fact]
    public async Task SsePath_HandlerCanWaitOnARefresh()
    {
        using var server = WireMockServer.Start();
        ServeConfigs(server, 42);
        server
            .Given(Request.Create().WithPath(SsePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithBody("data: " + EnvelopeJson(43) + "\n\n"));
        // Call 1 is the init fetch (gen 42); the SSE install of gen 43 is the first later call.
        var handler = new RefreshingHandler(call => call == 2);

        await using var client = new Quonfig(Options(server, sse: true, handler.Handle));
        handler.Attach(client);
        await client.InitAsync();
        await WaitUntil(() => !handler.RefreshCompleted.IsEmpty, TimeSpan.FromSeconds(10));

        handler.RefreshCompleted.Should().ContainSingle()
            .Which.Should().BeTrue("a refresh on another thread must not block on a handler fired by an SSE install");
        handler.Overlapped.Should().BeFalse("handlers are delivered serially");
    }

    /// <summary>
    /// qfg-6vno.22: an <c>OnConfigChange</c> handler fired by an SSE install runs on the SSE reader
    /// task. Before the fix, <c>CloseAsync</c> called from that handler waited (bounded, 5 s) on the
    /// very task running it, so it always returned after the full timeout. It must return promptly,
    /// the SSE loop must still end once the handler returns, and nothing may be logged at Error or
    /// surface an <see cref="ObjectDisposedException"/> while the loop unwinds.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SsePath_HandlerCanCloseTheClientWithoutWaitingOnItself(bool asyncHandler)
    {
        using var server = WireMockServer.Start();
        ServeConfigs(server, 42);
        server
            .Given(Request.Create().WithPath(SsePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithBody("data: " + EnvelopeJson(43) + "\n\n"));

        var clientSet = new TaskCompletionSource<Quonfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        async Task CloseFromHandlerAsync()
        {
            var client = await clientSet.Task.ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            await client.CloseAsync().ConfigureAwait(false);
            closed.TrySetResult(sw.ElapsedMilliseconds);
        }
        void Handle()
        {
            // Call 1 is the init fetch (gen 42); call 2 is the SSE install of gen 43.
            if (Interlocked.Increment(ref calls) != 2) return;
            // A throw here is caught and counted by the SDK; the on_config_update assertion sees it.
            if (!clientSet.Task.Wait(HandlerWait)) throw new TimeoutException("client never attached");
            if (asyncHandler)
            {
                // An async-void style handler: CloseAsync runs inline up to its first await.
                _ = CloseFromHandlerAsync();
            }
            else
            {
                CloseFromHandlerAsync().GetAwaiter().GetResult();
            }
        }

        var logger = new CaptureLogger();
        var opts = Options(server, sse: true, Handle);
        opts.Logger = logger;
        var c = new Quonfig(opts);
        await using var owned = c;
        clientSet.SetResult(c);
        await c.InitAsync();

        await Within(closed.Task, TimeSpan.FromSeconds(15));
        long elapsedMs = await closed.Task;
        elapsedMs.Should().BeLessThan(1000, "CloseAsync from an SSE-path handler must not wait on the SSE task running it");

        var sseTask = c.SseRunTask;
        sseTask.Should().NotBeNull();
        await Within(sseTask!, TimeSpan.FromSeconds(5));
        sseTask!.IsCompleted.Should().BeTrue("the SSE loop ends once the handler returns");

        c.CallbackErrorsTotal("on_config_update").Should().Be(0);
        logger.LogCount(Microsoft.Extensions.Logging.LogLevel.Error).Should().Be(0,
            string.Join("\n", logger.Lines.Select(l => l.Level + ": " + l.Message)));
        logger.Exceptions.OfType<ObjectDisposedException>().Should().BeEmpty();
        logger.LogCount(Microsoft.Extensions.Logging.LogLevel.Warning, "SSE envelope install failed|onEnvelope handler threw").Should().Be(0);
    }
}
