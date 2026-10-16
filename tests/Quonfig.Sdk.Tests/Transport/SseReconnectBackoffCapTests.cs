using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Transport;
using Xunit;

namespace Quonfig.Sdk.Tests.Transport;

/// <summary>
/// Regression test for qfg-n3nr: the SSE reconnect backoff capped at 60s with +/-20% jitter, so
/// after a long outage the next reconnect could land up to 72s after the server came back (past
/// the chaos 05/06 45s recovery windows). sdk-go (sse_client.go MaxDelay), sdk-node
/// (SSE_RECONNECT_MAX_DELAY_MS), sdk-python and supervisor contract Test 2 cap at 30s.
/// </summary>
public class SseReconnectBackoffCapTests
{
    private const int Attempts = 40;

    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("down");
    }

    private static async Task<List<TimeSpan>> RecordedBackoffsAsync()
    {
        var sleeps = new List<TimeSpan>();
        using var cts = new CancellationTokenSource();
        using var handler = new AlwaysFailHandler();
        using var sse = new SseClient(
            streamUrls: new[] { new Uri("http://127.0.0.1:1") },
            sdkKey: "k",
            onEnvelope: _ => { },
            messageHandler: handler)
        {
            WaitHook = (_, sleep) =>
            {
                lock (sleeps)
                {
                    sleeps.Add(sleep);
                    if (sleeps.Count >= Attempts) cts.Cancel();
                }
                return TimeSpan.Zero;
            },
        };
        var run = sse.RunAsync(cts.Token);
        (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)))).Should().BeSameAs(run, "the loop exits on cancel");
        return sleeps;
    }

    [Fact]
    public async Task ReconnectLadder_NeverRequestsMoreThan30s()
    {
        var sleeps = await RecordedBackoffsAsync();

        sleeps.Should().HaveCount(Attempts);
        sleeps.Max().Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(30), "the SSE reconnect cap is 30s, jitter included");
        // The ladder grows to the cap rather than stalling below it.
        sleeps.Skip(Attempts - 5).Should().OnlyContain(s => s >= TimeSpan.FromSeconds(24), "the tail sits at the 30s cap (-20% jitter floor)");
    }

    [Fact]
    public async Task ReconnectLadder_StillStartsAt1sAndGrows()
    {
        var sleeps = await RecordedBackoffsAsync();

        sleeps[0].Should().Be(TimeSpan.FromSeconds(1));
        sleeps[3].Should().BeGreaterThan(sleeps[0]);
    }

    [Fact]
    public void DefaultMaxBackoff_Is30s() =>
        SseClient.DefaultMaxBackoff.Should().Be(TimeSpan.FromSeconds(30));
}
