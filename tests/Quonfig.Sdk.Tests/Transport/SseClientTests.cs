using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk;
using Quonfig.Sdk.Transport;
using Quonfig.Sdk.Wire;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests.Transport;

/// <summary>
/// Coverage of <see cref="SseClient"/>: the in-memory parser, the Layer 1 read
/// watchdog (dispose-on-stall), the backoff math, and clean cancellation. The
/// parser tests run against a <see cref="MemoryStream"/> so they exercise the
/// exact wire-format handling without dragging in an HTTP server; the watchdog
/// test uses a custom blocking <see cref="Stream"/> to prove the
/// <c>CancellationTokenSource.CancelAfter</c> wakeup unblocks an in-flight read.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA2007",
    Justification = "Test code; ConfigureAwait(false) not required.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1031",
    Justification = "Tests assert on side effects (disposal flag, elapsed time); broad catch is intentional.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Usage", "CA1849",
    Justification = "Tests deliberately use synchronous CancellationTokenSource.Cancel to match production callers.")]
public sealed class SseClientTests
{
    private const string SdkKey = "test-sdk-key";

    private static string MinimalEnvelopeJson(string version) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{{\"meta\":{{\"version\":\"{0}\",\"environment\":\"production\"}},\"configs\":[]}}",
            version);

    [Fact]
    public async Task ParseStreamAsync_ParsesMultipleEventsFromMemoryStream()
    {
        var received = new ConcurrentQueue<ConfigEnvelope>();
        var body = new StringBuilder();
        body.Append(": welcome comment\n\n");
        body.Append("id: v1\n");
        body.Append("data: ").Append(MinimalEnvelopeJson("v1")).Append("\n\n");
        body.Append(": keepalive\n\n");
        body.Append("event: update\n");
        body.Append("data: ").Append(MinimalEnvelopeJson("v2")).Append("\n\n");
        body.Append("data: {\"meta\":{\"version\":\"v3\"\n");
        body.Append("data: ,\"environment\":\"prod\"}\n");
        body.Append("data: ,\"configs\":[]}\n\n");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body.ToString()));

        var consumed = await SseClient.ParseStreamAsync(
            stream,
            env => received.Enqueue(env),
            readTimeout: TimeSpan.FromSeconds(5),
            cancellationToken: CancellationToken.None);

        consumed.Should().BeTrue("the parser saw a complete event before EOF");
        received.Count.Should().Be(3, "two single-line + one multi-line data event");
        var versions = received.Select(e => e.Meta!.Version).ToArray();
        versions.Should().Equal("v1", "v2", "v3");
    }

    [Fact]
    public async Task ParseStreamAsync_WatchdogFiresOnStallAndDisposesStream()
    {
        using var stalling = new StallingStream();

        var start = DateTime.UtcNow;
        try
        {
            await SseClient.ParseStreamAsync(
                stalling,
                _ => { },
                readTimeout: TimeSpan.FromMilliseconds(100),
                cancellationToken: CancellationToken.None);
        }
        catch (Exception)
        {
            // Watchdog disposed the stream mid-read: surface or swallow, both OK.
        }
        var elapsed = DateTime.UtcNow - start;

        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "watchdog with 100ms timeout must trip well under 2s");
        stalling.Disposed.Should().BeTrue("watchdog must call Stream.Dispose on stall");
    }

    [Fact]
    public async Task ParseStreamAsync_ReArmsWatchdogOnEachRead()
    {
        using var trickler = new TricklingStream(
            chunks: new[]
            {
                ": keepalive\n\n", ": keepalive\n\n", ": keepalive\n\n",
                ": keepalive\n\n", "data: " + MinimalEnvelopeJson("v1") + "\n\n",
            },
            interChunkDelay: TimeSpan.FromMilliseconds(50));
        ConfigEnvelope? got = null;

        var consumed = await SseClient.ParseStreamAsync(
            trickler,
            env => got = env,
            readTimeout: TimeSpan.FromMilliseconds(250),
            cancellationToken: CancellationToken.None);

        consumed.Should().BeTrue();
        got.Should().NotBeNull();
        got!.Meta!.Version.Should().Be("v1");
        trickler.Disposed.Should().BeFalse("healthy stream must not trip the watchdog");
    }

    [Fact]
    public void NextBackoff_GrowsExponentiallyAndCapsAtMax()
    {
        var rng = new Random(12345);
        var max = TimeSpan.FromSeconds(60);

        // First call to NextBackoff(1s) — formula: min(2*current, max) * jitter[0.8, 1.2)
        var d1 = SseClient.NextBackoff(TimeSpan.FromSeconds(1), max, rng);
        var d2 = SseClient.NextBackoff(TimeSpan.FromSeconds(2), max, rng);
        var d3 = SseClient.NextBackoff(TimeSpan.FromSeconds(4), max, rng);

        d1.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1600),
            "2*1s with jitter floor of 0.8");
        d1.Should().BeLessThan(TimeSpan.FromMilliseconds(2400),
            "2*1s with jitter ceiling of 1.2");
        d2.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(3200));
        d2.Should().BeLessThan(TimeSpan.FromMilliseconds(4800));
        d3.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(6400));
        d3.Should().BeLessThan(TimeSpan.FromMilliseconds(9600));

        // Cap: once we're at 60s, the next backoff is capped at 60s before jitter,
        // so the absolute upper bound is 60s * 1.2 = 72s.
        var capped = SseClient.NextBackoff(TimeSpan.FromSeconds(120), max, rng);
        capped.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(72),
            "cap is 60s + 20% upper jitter");
        capped.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(48),
            "cap is 60s - 20% lower jitter");
    }

    [Fact]
    public async Task RunAsync_CleanlyExitsWithinFiveSecondsOnCancel()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/sse/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithBody(": stalled\n\n")
                .WithDelay(TimeSpan.FromSeconds(60)));

        using var sse = new SseClient(
            streamUrls: new[] { new Uri(server.Urls[0]) },
            sdkKey: SdkKey,
            onEnvelope: _ => { },
            readTimeout: TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource();
        var runTask = sse.RunAsync(cts.Token);

        await Task.Delay(200);
        cts.Cancel();

        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(runTask, "RunAsync must unwind within 5s of cancel");
        try { await runTask; }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task RunAsync_SendsBasicAuthAndSdkVersionHeaders()
    {
        using var server = WireMockServer.Start();
        // 503 closes the connection fast so the loop bails out; the request still hits
        // the server with the headers under test. WireMock.Net buffers the whole response
        // before sending — we can't reasonably test mid-stream behavior here, that's
        // covered by the ParseStreamAsync MemoryStream tests.
        server
            .Given(Request.Create().WithPath("/api/v2/sse/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(503));

        using var sse = new SseClient(
            streamUrls: new[] { new Uri(server.Urls[0]) },
            sdkKey: SdkKey,
            onEnvelope: _ => { },
            readTimeout: TimeSpan.FromSeconds(5),
            initialBackoff: TimeSpan.FromSeconds(10));  // long backoff so we don't re-hit on cancel

        using var cts = new CancellationTokenSource();
        var runTask = sse.RunAsync(cts.Token);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!server.LogEntries.Any() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }

        server.LogEntries.ToList().Should().NotBeEmpty("SSE client must have hit the server");
        var log = server.LogEntries.First();
        string expectedAuth = "Basic " +
            Convert.ToBase64String(Encoding.UTF8.GetBytes("1:" + SdkKey));
        log.RequestMessage.Headers!["Authorization"].ToString().Should().Be(expectedAuth);
        log.RequestMessage.Headers!["X-Quonfig-SDK-Version"].ToString()
            .Should().Be("dotnet/" + SdkInfo.Version);
        log.RequestMessage.Headers!["Accept"].ToString().Should().Contain("text/event-stream");
        // Path is /api/v2/sse/config — the constant the bead pins.
        log.RequestMessage.Path.Should().Be("/api/v2/sse/config");
    }

    [Fact]
    public async Task RunAsync_FiresOnConnectAfter200AndOnDisconnectWhenStreamEnds()
    {
        using var server = WireMockServer.Start();
        // 200 OK with a tiny body that EOFs immediately — the connect edge must fire
        // after status, the disconnect edge after the parser returns.
        server
            .Given(Request.Create().WithPath("/api/v2/sse/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithBody(": welcome\n\n"));

        int connectCount = 0;
        int disconnectCount = 0;
        var order = new ConcurrentQueue<string>();

        using var sse = new SseClient(
            streamUrls: new[] { new Uri(server.Urls[0]) },
            sdkKey: SdkKey,
            onEnvelope: _ => { },
            onConnect: () => { Interlocked.Increment(ref connectCount); order.Enqueue("connect"); },
            onDisconnect: () => { Interlocked.Increment(ref disconnectCount); order.Enqueue("disconnect"); },
            readTimeout: TimeSpan.FromSeconds(5),
            initialBackoff: TimeSpan.FromSeconds(10));  // long backoff so we don't loop

        using var cts = new CancellationTokenSource();
        var runTask = sse.RunAsync(cts.Token);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref disconnectCount) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }

        connectCount.Should().BeGreaterThanOrEqualTo(1, "onConnect must fire after 200 OK");
        disconnectCount.Should().BeGreaterThanOrEqualTo(1,
            "onDisconnect must fire when the stream ends (EOF)");
        // Sequence must alternate connect → disconnect → connect → disconnect.
        var seq = order.ToArray();
        seq.Should().NotBeEmpty();
        seq[0].Should().Be("connect", "the first edge must be a connect");
        // Each connect must be paired with a disconnect by the end of the run.
        seq.Count(s => s == "connect").Should().Be(seq.Count(s => s == "disconnect"),
            "every connect must pair with a disconnect");
    }

    [Fact]
    public async Task RunAsync_DoesNotFireConnectEdgesWhenStatusIsNot200()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/sse/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(503));

        int connectCount = 0;
        int disconnectCount = 0;

        using var sse = new SseClient(
            streamUrls: new[] { new Uri(server.Urls[0]) },
            sdkKey: SdkKey,
            onEnvelope: _ => { },
            onConnect: () => Interlocked.Increment(ref connectCount),
            onDisconnect: () => Interlocked.Increment(ref disconnectCount),
            readTimeout: TimeSpan.FromSeconds(5),
            initialBackoff: TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource();
        var runTask = sse.RunAsync(cts.Token);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!server.LogEntries.Any() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        await Task.Delay(150);
        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }

        server.LogEntries.ToList().Should().NotBeEmpty("the client must have hit the server");
        connectCount.Should().Be(0,
            "onConnect must NOT fire when the server returns a non-200 status");
        disconnectCount.Should().Be(0,
            "onDisconnect must NOT fire if onConnect never fired");
    }

    [Fact]
    public void Constructor_RejectsEmptyStreamUrls()
    {
        Action act = () =>
        {
            using var _ = new SseClient(
                streamUrls: Array.Empty<Uri>(),
                sdkKey: SdkKey,
                onEnvelope: _ => { });
        };
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_RejectsNullSdkKey()
    {
        Action act = () =>
        {
            using var _ = new SseClient(
                streamUrls: new[] { new Uri("http://x/") },
                sdkKey: null!,
                onEnvelope: _ => { });
        };
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Read blocks forever until Dispose is called. Used to verify the watchdog
    /// actually unblocks an in-flight read by disposing the underlying stream.
    /// </summary>
    private sealed class StallingStream : Stream
    {
        private SemaphoreSlim? _disposed = new(0, 1);
        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            _disposed?.Wait();
            throw new ObjectDisposedException(nameof(StallingStream));
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var sem = _disposed;
            if (sem != null)
            {
                await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            throw new ObjectDisposedException(nameof(StallingStream));
        }

#if NET8_0_OR_GREATER
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return new ValueTask<int>(ReadAsync(buffer.ToArray(), 0, buffer.Length, cancellationToken));
        }
#endif

        protected override void Dispose(bool disposing)
        {
            if (disposing && !Disposed)
            {
                Disposed = true;
                var sem = _disposed;
                _disposed = null;
                if (sem != null)
                {
                    sem.Release();
                    sem.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Emits a series of byte chunks with a fixed delay between them, then EOF.
    /// Used to prove the watchdog is re-armed on every successful read.
    /// </summary>
    private sealed class TricklingStream : Stream
    {
        private readonly byte[][] _chunks;
        private readonly TimeSpan _delay;
        private int _index;
        public bool Disposed { get; private set; }

        public TricklingStream(string[] chunks, TimeSpan interChunkDelay)
        {
            _chunks = chunks.Select(c => Encoding.UTF8.GetBytes(c)).ToArray();
            _delay = interChunkDelay;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_index >= _chunks.Length) return 0;
            Thread.Sleep(_delay);
            var chunk = _chunks[_index++];
            int n = Math.Min(chunk.Length, count);
            Array.Copy(chunk, 0, buffer, offset, n);
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_index >= _chunks.Length) return 0;
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            var chunk = _chunks[_index++];
            int n = Math.Min(chunk.Length, count);
            Array.Copy(chunk, 0, buffer, offset, n);
            return n;
        }

#if NET8_0_OR_GREATER
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_index >= _chunks.Length) return 0;
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            var chunk = _chunks[_index++];
            int n = Math.Min(chunk.Length, buffer.Length);
            chunk.AsMemory(0, n).CopyTo(buffer);
            return n;
        }
#endif

        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }
}
