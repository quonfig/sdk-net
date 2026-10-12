using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Transport;
using Quonfig.Sdk.Wire;
using Xunit;

namespace Quonfig.Sdk.Tests.Transport;

/// <summary>
/// qfg-goi1.2.37 (B): the SSE connect must bound its wait for response headers with a
/// SEPARATE fixed timeout, not the read/inactivity watchdog. Port of sdk-go
/// sse_header_timeout_test.go and sdk-java SseClientHeaderTimeoutTest.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA2007",
    Justification = "Test code; ConfigureAwait(false) not required.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1031",
    Justification = "Raw socket test server; broad catch on teardown is intentional.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Usage", "CA1849",
    Justification = "Tests deliberately use synchronous CancellationTokenSource.Cancel.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA2000",
    Justification = "TcpListener is stopped explicitly; it is not IDisposable on every target framework.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1835",
    Justification = "Byte-array Stream overloads keep the raw test server portable across target frameworks.")]
public sealed class SseClientHeaderTimeoutTests
{
    private const string EnvelopeJson =
        "{\"meta\":{\"version\":\"v1\",\"environment\":\"production\"},\"configs\":[]}";

    [Fact]
    public async Task RunAsync_ReconnectsWhenPeerAcceptsButNeverSendsHeaders()
    {
        // A TCP listener that accepts and never answers. Without a header timeout the first
        // SendAsync waits forever and the client never retries.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var held = new ConcurrentBag<TcpClient>();
        int accepts = 0;
        using var serverCts = new CancellationTokenSource();
        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (!serverCts.IsCancellationRequested)
                {
                    var c = await listener.AcceptTcpClientAsync();
                    Interlocked.Increment(ref accepts);
                    held.Add(c); // keep it open, never write
                }
            }
            catch
            {
                // listener stopped
            }
        });

        using var sse = new SseClient(
            streamUrls: new[] { new Uri($"http://127.0.0.1:{port}") },
            sdkKey: "k",
            onEnvelope: _ => { },
            readTimeout: TimeSpan.FromMilliseconds(200),
            initialBackoff: TimeSpan.FromMilliseconds(10),
            maxBackoff: TimeSpan.FromMilliseconds(50))
        {
            HeaderTimeout = TimeSpan.FromMilliseconds(200),
        };
        using var cts = new CancellationTokenSource();
        var run = sse.RunAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Volatile.Read(ref accepts) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        int seen = Volatile.Read(ref accepts);

        cts.Cancel();
        await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
        serverCts.Cancel();
        listener.Stop();
        foreach (var c in held) c.Dispose();
        await Task.WhenAny(acceptLoop, Task.Delay(TimeSpan.FromSeconds(2)));

        seen.Should().BeGreaterThanOrEqualTo(2,
            "a peer that never sends headers must hit the header timeout and trigger a reconnect");
    }

    [Fact]
    public async Task RunAsync_ConnectsWhenHeadersArriveAfterReadTimeout()
    {
        // Headers arrive after 2.5x the (short) read timeout. The header wait is a separate fixed
        // timeout, so the connect must still succeed and deliver the envelope; deriving it from
        // the read timeout reconnects forever (chaos 03-latency, qfg-d1o9).
        var readTimeout = TimeSpan.FromMilliseconds(200);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var held = new ConcurrentBag<TcpClient>();
        int accepts = 0;
        using var serverCts = new CancellationTokenSource();
        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (!serverCts.IsCancellationRequested)
                {
                    var c = await listener.AcceptTcpClientAsync();
                    Interlocked.Increment(ref accepts);
                    held.Add(c);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var s = c.GetStream();
                            var buf = new byte[4096];
                            _ = await s.ReadAsync(buf, 0, buf.Length); // request
                            await Task.Delay(TimeSpan.FromMilliseconds(readTimeout.TotalMilliseconds * 2.5));
                            var head = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\n\r\n");
                            await s.WriteAsync(head, 0, head.Length);
                            var ev = Encoding.UTF8.GetBytes("data: " + EnvelopeJson + "\n\n");
                            await s.WriteAsync(ev, 0, ev.Length);
                            await s.FlushAsync();
                            // keepalives inside the read timeout
                            while (!serverCts.IsCancellationRequested)
                            {
                                await Task.Delay(50);
                                var ka = Encoding.ASCII.GetBytes(": ka\n\n");
                                await s.WriteAsync(ka, 0, ka.Length);
                            }
                        }
                        catch
                        {
                            // client went away
                        }
                    });
                }
            }
            catch
            {
                // listener stopped
            }
        });

        var received = new ConcurrentQueue<ConfigEnvelope>();
        using var sse = new SseClient(
            streamUrls: new[] { new Uri($"http://127.0.0.1:{port}") },
            sdkKey: "k",
            onEnvelope: env => received.Enqueue(env),
            readTimeout: readTimeout,
            initialBackoff: TimeSpan.FromMilliseconds(10),
            maxBackoff: TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource();
        var run = sse.RunAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (received.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        cts.Cancel();
        await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
        serverCts.Cancel();
        listener.Stop();
        foreach (var c in held) c.Dispose();
        await Task.WhenAny(acceptLoop, Task.Delay(TimeSpan.FromSeconds(2)));

        received.Should().NotBeEmpty("slow headers within the fixed header timeout must still connect");
        Volatile.Read(ref accepts).Should().Be(1, "the slow-header connect must not be torn down and retried");
    }
}
