using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Transport;
using Xunit;

namespace Quonfig.Sdk.Tests.Transport;

/// <summary>
/// qfg-6vno.4: the internal, test-only Layer 1 counters the chaos harness reads instead of
/// deriving <c>quonfig_sse_connect_attempts_total</c> and
/// <c>quonfig_sdk_worker_restart_total{layer="1"}</c> from connection-state edges. Failed attempts
/// fire no state edge (first-frame rule), so only an SDK-side count can see them.
/// </summary>
public class SseCountersTests
{
    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("down");
    }

    /// <summary>200 + keepalive frame, then EOF: one live run per attempt.</summary>
    private sealed class KeepaliveThenCloseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ok = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(": keepalive\n\n")),
            };
            ok.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
            return Task.FromResult(ok);
        }
    }

    /// <summary>200 headers + a partial byte, then EOF before any frame: the half-open shape.</summary>
    private sealed class OkThenEofHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ok = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(":")),
            };
            ok.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
            return Task.FromResult(ok);
        }
    }

    private static async Task<SseCounters> RunUntilAsync(HttpMessageHandler handler, int sleeps)
    {
        var counters = new SseCounters();
        using var cts = new CancellationTokenSource();
        int seen = 0;
        using var sse = new SseClient(
            streamUrls: new[] { new Uri("http://127.0.0.1:1") },
            sdkKey: "k",
            onEnvelope: _ => { },
            messageHandler: handler)
        {
            Counters = counters,
            WaitHook = (_, _) =>
            {
                if (Interlocked.Increment(ref seen) >= sleeps) cts.Cancel();
                return TimeSpan.Zero;
            },
        };
        var run = sse.RunAsync(cts.Token);
        (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)))).Should().BeSameAs(run);
        return counters;
    }

    [Fact]
    public async Task RefusedAttempts_CountAsAttemptsAndRestarts()
    {
        using var handler = new AlwaysFailHandler();
        var c = await RunUntilAsync(handler, 5);
        c.ConnectAttempts.Should().Be(5);
        c.WorkerRestarts.Should().Be(5);
    }

    [Fact]
    public async Task StreamThatDeliveredAFrameAndEnded_CountsAsARestart()
    {
        using var handler = new KeepaliveThenCloseHandler();
        var c = await RunUntilAsync(handler, 3);
        c.ConnectAttempts.Should().Be(3);
        c.WorkerRestarts.Should().Be(3);
    }

    [Fact]
    public async Task OkThenEofBeforeFirstFrame_CountsAsOkWithoutFrame()
    {
        using var handler = new OkThenEofHandler();
        var c = await RunUntilAsync(handler, 4);
        c.ConnectAttempts.Should().Be(4);
        c.WorkerRestarts.Should().Be(4);
        c.OkWithoutFrame.Should().Be(4, "every attempt got a 200 and ended before its first frame");
    }

    [Fact]
    public async Task RefusedAttemptsAndLiveStreams_AreNotOkWithoutFrame()
    {
        using var refused = new AlwaysFailHandler();
        (await RunUntilAsync(refused, 3)).OkWithoutFrame.Should().Be(0, "a transport error never got a 200");
        using var live = new KeepaliveThenCloseHandler();
        (await RunUntilAsync(live, 3)).OkWithoutFrame.Should().Be(0, "these streams delivered a frame");
    }

    [Fact]
    public async Task CleanShutdown_IsNotARestart()
    {
        var counters = new SseCounters();
        using var cts = new CancellationTokenSource();
        using var handler = new HangingHandler();
        using var sse = new SseClient(
            streamUrls: new[] { new Uri("http://127.0.0.1:1") },
            sdkKey: "k",
            onEnvelope: _ => { },
            messageHandler: handler)
        {
            Counters = counters,
        };
        var run = sse.RunAsync(cts.Token);
        await handler.Entered.Task;
        cts.CancelAfter(TimeSpan.Zero);
        (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(run);
        counters.ConnectAttempts.Should().Be(1);
        counters.WorkerRestarts.Should().Be(0);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
