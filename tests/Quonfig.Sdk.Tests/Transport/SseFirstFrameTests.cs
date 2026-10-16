using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
/// qfg-goi1.10.41 (fan-out of qfg-goi1.10.9; sdk-go canary qfg-goi1.10.39): a stream connection
/// is <c>connected</c> at its FIRST VALID FRAME, not at the 200 response headers.
/// integration-test-data v2026.10.16.4 chaos/supervisor-test-contract.md, "Rule — a connection
/// is connected at its first valid frame", Test 9 (a)-(d) and the Test 2 "Reset" amendment.
/// Drives the real SSE worker against a raw 127.0.0.1 HTTP server.
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
    "Usage", "CA2213",
    Justification = "TcpListener is stopped explicitly in Dispose; it is not IDisposable on every target framework.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1835",
    Justification = "Byte-array Stream overloads keep the raw test server portable across target frameworks.")]
public sealed class SseFirstFrameTests
{
    private const string Key = "first-frame.value";

    // sdk-net's reconnect ladder: the sleep at the initial delay is the 1s initial backoff, or
    // jitter(1s) = up to 1.2s after a stream that delivered a frame (SseClient.ReconnectSleep).
    private static readonly TimeSpan MaxInitialDelaySleep = TimeSpan.FromMilliseconds(1200);

    private static string Envelope(int generation, string value) =>
        "{\"configs\":[{ \"id\": \"1\", \"key\": \"" + Key + "\", \"type\": \"config\", \"valueType\": \"string\", " +
        "\"default\": { \"rules\": [ { \"criteria\": [ { \"operator\": \"ALWAYS_TRUE\" } ], " +
        "\"value\": { \"type\": \"string\", \"value\": \"" + value + "\" } } ] } }]," +
        "\"meta\":{\"version\":\"v" + generation + "\",\"environment\":\"production\",\"generation\":" + generation + "}}";

    // ---------------------------------------------------------------- 9a

    [Fact]
    public async Task Test9a_HeadersOnly_NeverConnected_BackoffGrows_PollerDeliversV2()
    {
        // THRESHOLD 2.5s is deliberately longer than every gap the pre-fix code left between a
        // connect edge and the next one inside the wait window (1s, then 1.6-2.4s), so the old
        // code cannot engage the poller in time and the v2 assertion is not vacuous.
        using var server = new FrameServer(async (_, s, ct) =>
        {
            await FrameServer.WriteSseHeadersAsync(s, ct);
        });
        await using var rig = await ClientRig.StartAsync(server, threshold: TimeSpan.FromMilliseconds(2500));

        bool got = await WaitForAsync(
            () => rig.Client.FallbackPollerActive && rig.Client.GetString(Key) == "v2",
            TimeSpan.FromMilliseconds(4500));

        var sleeps = rig.SseSleeps.ToArray();
        var attempts = server.SseAttempts;
        rig.StopSampler();

        got.Should().BeTrue("a frameless 200 is a failed attempt: the engage timer armed at start is never cancelled, so the poller engages and delivers v2");
        rig.Client.ConnectionState.Should().Be(ConnectionState.FallingBack);
        rig.ConnectedEdges.Should().Be(0, "200 headers alone are not a frame");
        rig.SamplerSawConnected.Should().BeFalse("ConnectionState must never read connected on headers alone");
        attempts.Should().BeLessThanOrEqualTo(4, "the backoff ladder bounds the attempts");
        sleeps.Should().NotBeEmpty();
        sleeps[0].Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        for (int i = 1; i < sleeps.Length; i++)
        {
            sleeps[i].Should().BeGreaterThan(sleeps[i - 1], "a frameless attempt grows the backoff like a connect error (sleep {0})", i);
        }
    }

    // ---------------------------------------------------------------- 9b

    [Fact]
    public async Task Test9b_HeadersEarlyFrameLate_ConnectedEdgeAtTheKeepalive()
    {
        long sendAtTicks = 0;
        FrameServer? serverRef = null;
        using var server = new FrameServer(async (_, s, ct) =>
        {
            await FrameServer.WriteSseHeadersAsync(s, ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            Interlocked.Exchange(ref sendAtTicks, serverRef!.Clock.ElapsedTicks);
            await FrameServer.WriteAsync(s, ": keepalive\n\n", ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        serverRef = server;
        await using var rig = await ClientRig.StartAsync(server, threshold: TimeSpan.FromSeconds(10));

        // Wait for the keepalive to be SENT (not just for an edge: the pre-fix code raises its
        // edge on the headers, a second before the keepalive), then let the edge land.
        await WaitForAsync(() => Interlocked.Read(ref sendAtTicks) != 0 && rig.ConnectedEdges >= 1, TimeSpan.FromSeconds(4));
        await Task.Delay(200);
        Interlocked.Read(ref sendAtTicks).Should().NotBe(0, "the server must have sent the keepalive");
        rig.StopSampler();

        var sendAt = TimeSpan.FromTicks(Interlocked.Read(ref sendAtTicks) * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
        rig.ConnectedEdges.Should().Be(1);
        var edgeAt = rig.FirstConnectedEdgeAt();
        (sendAt - edgeAt).Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(50),
            "connected is reported no earlier than the first frame (edge {0}, keepalive sent {1})", edgeAt, sendAt);
        rig.SamplerFirstConnectedAt().Should().BeGreaterThanOrEqualTo(sendAt - TimeSpan.FromMilliseconds(50),
            "ConnectionState must not read connected before the keepalive");
        server.SseAttempts.Should().Be(1);
    }

    // ---------------------------------------------------------------- 9c

    [Fact]
    public async Task Test9c_KeepaliveThenClose_ConnectedPerAttempt_BackoffStaysAtInitial()
    {
        // Threshold 3s exceeds the longest initial-delay sleep (1.2s) with room for a loaded
        // runner, so the poller would only engage if the connect edge were missing. The window
        // runs past the threshold so "poller never active" is not vacuous.
        var threshold = TimeSpan.FromSeconds(3);
        using var server = new FrameServer(async (_, s, ct) =>
        {
            await FrameServer.WriteSseHeadersAsync(s, ct);
            await FrameServer.WriteAsync(s, ": keepalive\n\n", ct);
        });
        await using var rig = await ClientRig.StartAsync(server, threshold: threshold);

        var started = server.Clock.Elapsed;
        await WaitForAsync(
            () => server.SseAttempts >= 3 && server.Clock.Elapsed - started >= threshold + TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(8));
        await Task.Delay(100);
        rig.StopSampler();
        int attempts = server.SseAttempts;
        int edges = rig.ConnectedEdges;

        attempts.Should().BeGreaterThanOrEqualTo(3);
        edges.Should().BeInRange(attempts - 1, attempts, "one connected edge per attempt that delivered a keepalive");
        rig.SseSleeps.Should().OnlyContain(d => d <= MaxInitialDelaySleep, "a keepalive is a frame, so the backoff resets every attempt");
        rig.SamplerSawPollerActive.Should().BeFalse("each keepalive cancels the engage timer");
        rig.Client.GetString(Key).Should().Be("v1");
    }

    // ---------------------------------------------------------------- 9d

    [Fact]
    public async Task Test9d_EnvelopeThenHold_Control()
    {
        using var server = new FrameServer(async (_, s, ct) =>
        {
            await FrameServer.WriteSseHeadersAsync(s, ct);
            await FrameServer.WriteAsync(s, "data: " + Envelope(2, "v2") + "\n\n", ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var rig = await ClientRig.StartAsync(server, threshold: TimeSpan.FromSeconds(10));

        bool got = await WaitForAsync(() => rig.Client.GetString(Key) == "v2", TimeSpan.FromSeconds(3));
        await Task.Delay(200);
        rig.StopSampler();

        got.Should().BeTrue();
        rig.ConnectedEdges.Should().Be(1);
        rig.Client.ConnectionState.Should().Be(ConnectionState.Connected);
        rig.SamplerSawPollerActive.Should().BeFalse();
        server.ConfigHits.Should().Be(1);
    }

    // ---------------------------------------------------------------- Test 2 reset

    [Fact]
    public async Task Test2Reset_OnlyAFrameResetsTheBackoff()
    {
        // conn1 503; conn2 envelope + close (frame: reset); conn3 503 (grows from 1s);
        // conn4 200 + a keepalive line cut off by EOF (NOT a frame: no reset); conn5 hold.
        using var server = new FrameServer(async (n, s, ct) =>
        {
            switch (n)
            {
                case 1:
                case 3:
                    await FrameServer.WriteAsync(s, "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct);
                    return;
                case 2:
                    await FrameServer.WriteSseHeadersAsync(s, ct);
                    await FrameServer.WriteAsync(s, "data: " + Envelope(1, "v1") + "\n\n", ct);
                    return;
                case 4:
                    await FrameServer.WriteSseHeadersAsync(s, ct);
                    await FrameServer.WriteAsync(s, ": keepalive\n", ct);
                    return;
                default:
                    await FrameServer.WriteSseHeadersAsync(s, ct);
                    await Task.Delay(Timeout.Infinite, ct);
                    return;
            }
        });

        var sleeps = new ConcurrentQueue<TimeSpan>();
        using var sse = new SseClient(
            streamUrls: new[] { server.BaseUri },
            sdkKey: "k",
            onEnvelope: _ => { })
        {
            WaitHook = (kind, d) =>
            {
                sleeps.Enqueue(d);
                return TimeSpan.FromMilliseconds(1);
            },
        };
        using var cts = new CancellationTokenSource();
        var run = sse.RunAsync(cts.Token);

        await WaitForAsync(() => server.SseAttempts >= 5, TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));

        var s = sleeps.ToArray();
        s.Length.Should().BeGreaterThanOrEqualTo(4);
        s[1].Should().BeLessThanOrEqualTo(MaxInitialDelaySleep, "conn2 delivered a frame, so the backoff resets");
        s[3].Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1500),
            "conn4 ended before its first frame: a failed attempt grows the backoff (got {0})", s[3]);
    }

    // ---------------------------------------------------------------- parser table

    public static IEnumerable<object[]> FrameCases() => new[]
    {
        new object[] { "empty body", "", false },
        new object[] { "keepalive cut off by EOF", ": keepalive\n", false },
        new object[] { "keepalive", ": keepalive\n\n", true },
        new object[] { "bare comment", ":\n\n", true },
        new object[] { "malformed data", "data: {not json\n\n", false },
        new object[] { "valid JSON, not an envelope", "data: {\"foo\":1}\n\n", false },
        new object[] { "envelope with no terminating blank line", "data: " + Envelope(1, "v1") + "\n", false },
        new object[] { "id-only block", "id: 7\n\n", false },
        new object[] { "bare blank line", "\n", false },
        new object[] { "comment plus malformed data", ": c\ndata: nope\n\n", false },
        new object[] { "garbage block then keepalive", "data: nope\n\n: keepalive\n\n", true },
        new object[] { "envelope", "data: " + Envelope(1, "v1") + "\n\n", true },
        new object[] { "two keepalives", ": keepalive\n\n: keepalive\n\n", true },
    };

    [Theory]
    [MemberData(nameof(FrameCases))]
    public async Task ParseStream_ReturnsFrameSeen(string name, string body, bool frame)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(body));
        bool seen = await SseClient.ParseStreamAsync(ms, _ => { }, TimeSpan.Zero, CancellationToken.None);
        seen.Should().Be(frame, name);
    }

    [Theory]
    [MemberData(nameof(FrameCases))]
    public async Task ParseStream_FiresExactlyOneFirstFrameEdgeIffFrame(string name, string body, bool frame)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(body));
        int edges = 0;
        await SseClient.ParseStreamAsync(
            ms, _ => { }, TimeSpan.Zero, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            () => edges++, CancellationToken.None);
        edges.Should().Be(frame ? 1 : 0, name);
    }

    [Fact]
    public async Task ParseStream_FirstFrameEdgeFiresBeforeTheEnvelopeIsHandedOver()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("data: " + Envelope(1, "v1") + "\n\n"));
        var order = new List<string>();
        await SseClient.ParseStreamAsync(
            ms, _ => order.Add("envelope"), TimeSpan.Zero, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            () => order.Add("connected"), CancellationToken.None);
        order.Should().Equal("connected", "envelope");
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<bool> WaitForAsync(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return true;
            await Task.Delay(10);
        }
        return cond();
    }

    /// <summary>A real client wired to <see cref="FrameServer"/> plus edge/sleep/state recorders.</summary>
    private sealed class ClientRig : IAsyncDisposable
    {
        private readonly ConcurrentQueue<(ConnectionState State, long Ticks)> _edges = new();
        private readonly CancellationTokenSource _samplerCts = new();
        private Task _sampler = Task.CompletedTask;
        private long _samplerFirstConnectedTicks = -1;
        private int _samplerSawPoller;

        public Quonfig Client { get; private set; } = null!;
        public ConcurrentQueue<TimeSpan> SseSleeps { get; } = new();

        public int ConnectedEdges => _edges.Count(e => e.State == ConnectionState.Connected);
        public bool SamplerSawConnected => Interlocked.Read(ref _samplerFirstConnectedTicks) >= 0;
        public bool SamplerSawPollerActive => Volatile.Read(ref _samplerSawPoller) != 0;

        public TimeSpan FirstConnectedEdgeAt() =>
            ToSpan(_edges.First(e => e.State == ConnectionState.Connected).Ticks);

        public TimeSpan SamplerFirstConnectedAt() => ToSpan(Interlocked.Read(ref _samplerFirstConnectedTicks));

        private static TimeSpan ToSpan(long swTicks) => TimeSpan.FromTicks(swTicks * TimeSpan.TicksPerSecond / Stopwatch.Frequency);

        public static async Task<ClientRig> StartAsync(FrameServer server, TimeSpan threshold)
        {
            var rig = new ClientRig();
            var client = new Quonfig(new QuonfigOptions
            {
                SdkKey = "test-sdk-key",
                ApiUrls = new[] { server.BaseUri.ToString() },
                StreamUrls = new[] { server.BaseUri.ToString() },
                InitTimeout = TimeSpan.FromSeconds(5),
                FallbackPollThreshold = threshold,
                FallbackPollInterval = TimeSpan.FromMilliseconds(20),
                CollectEvaluationSummaries = false,
                ContextUploadMode = ContextUploadMode.None,
                TestWaitHook = (kind, d) =>
                {
                    if (kind == "sse") rig.SseSleeps.Enqueue(d);
                    return d;
                },
            });
            client.OnConnectionStateChange += st => rig._edges.Enqueue((st, server.Clock.ElapsedTicks));
            rig.Client = client;
            await client.InitAsync();
            client.GetString(Key).Should().Be("v1", "the init fetch serves v1");
            var token = rig._samplerCts.Token;
            rig._sampler = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    if (client.ConnectionState == ConnectionState.Connected)
                    {
                        Interlocked.CompareExchange(ref rig._samplerFirstConnectedTicks, server.Clock.ElapsedTicks, -1);
                    }
                    if (client.FallbackPollerActive) Volatile.Write(ref rig._samplerSawPoller, 1);
                    try { await Task.Delay(1, token); }
                    catch (OperationCanceledException) { return; }
                }
            });
            return rig;
        }

        public void StopSampler()
        {
            _samplerCts.Cancel();
            _sampler.Wait(TimeSpan.FromSeconds(2));
        }

        public async ValueTask DisposeAsync()
        {
            StopSampler();
            _samplerCts.Dispose();
            await Client.DisposeAsync();
        }
    }

    /// <summary>
    /// Raw HTTP/1.1 server on 127.0.0.1. <c>/api/v2/configs</c> serves envelope gen 1 ("v1") on
    /// the first hit and gen 2 ("v2") on every later one (the generation must advance or the
    /// reject-older guard drops it). The SSE path runs the per-test script with the 1-based
    /// attempt number; the socket closes when the script returns.
    /// </summary>
    private sealed class FrameServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<int, Stream, CancellationToken, Task> _sse;
        private readonly ConcurrentBag<TcpClient> _clients = new();
        private int _configHits;
        private int _sseAttempts;

        public FrameServer(Func<int, Stream, CancellationToken, Task> sse)
        {
            _sse = sse;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            Clock = Stopwatch.StartNew();
            _ = Task.Run(AcceptLoopAsync);
        }

        public Uri BaseUri { get; }
        public Stopwatch Clock { get; }
        public int ConfigHits => Volatile.Read(ref _configHits);
        public int SseAttempts => Volatile.Read(ref _sseAttempts);

        public static Task WriteSseHeadersAsync(Stream s, CancellationToken ct) =>
            WriteAsync(s, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n", ct);

        public static async Task WriteAsync(Stream s, string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await s.WriteAsync(bytes, 0, bytes.Length, ct);
            await s.FlushAsync(ct);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var c = await _listener.AcceptTcpClientAsync();
                    _clients.Add(c);
                    _ = Task.Run(() => HandleAsync(c));
                }
            }
            catch
            {
                // listener stopped
            }
        }

        private async Task HandleAsync(TcpClient c)
        {
            try
            {
                using (c)
                {
                    var s = c.GetStream();
                    string path = await ReadRequestPathAsync(s);
                    if (path.StartsWith("/api/v2/sse/config", StringComparison.Ordinal))
                    {
                        int n = Interlocked.Increment(ref _sseAttempts);
                        await _sse(n, s, _cts.Token);
                    }
                    else if (path.StartsWith("/api/v2/configs", StringComparison.Ordinal))
                    {
                        int n = Interlocked.Increment(ref _configHits);
                        var body = Encoding.UTF8.GetBytes(n == 1 ? Envelope(1, "v1") : Envelope(2, "v2"));
                        await WriteAsync(s, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " +
                            body.Length + "\r\nConnection: close\r\n\r\n", _cts.Token);
                        await s.WriteAsync(body, 0, body.Length, _cts.Token);
                        await s.FlushAsync(_cts.Token);
                    }
                    else
                    {
                        await WriteAsync(s, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", _cts.Token);
                    }
                }
            }
            catch
            {
                // client went away or server shutting down
            }
        }

        private static async Task<string> ReadRequestPathAsync(Stream s)
        {
            var buf = new byte[8192];
            int len = 0;
            while (len < buf.Length)
            {
                int r = await s.ReadAsync(buf, len, buf.Length - len);
                if (r == 0) break;
                len += r;
                string sofar = Encoding.ASCII.GetString(buf, 0, len);
                if (sofar.EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    var first = sofar.Substring(0, sofar.IndexOf("\r\n", StringComparison.Ordinal));
                    var parts = first.Split(' ');
                    return parts.Length > 1 ? parts[1] : string.Empty;
                }
            }
            return string.Empty;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            foreach (var c in _clients) c.Dispose();
            _cts.Dispose();
        }
    }
}
