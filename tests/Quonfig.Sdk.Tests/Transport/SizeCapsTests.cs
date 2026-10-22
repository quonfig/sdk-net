using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Exceptions;
using Quonfig.Sdk.Tests.Telemetry.Helpers;
using Quonfig.Sdk.Transport;
using Quonfig.Sdk.Wire;
using Xunit;

namespace Quonfig.Sdk.Tests.Transport;

using LogLevel = Microsoft.Extensions.Logging.LogLevel;

/// <summary>
/// qfg-6vno.20.6: the cross-SDK size caps (integration-test-data v2026.10.22.2
/// chaos/supervisor-test-contract.md, "Rule — size caps" and Test 10). One SSE event is capped
/// at 4 MiB (4,194,304 bytes) and a 200 body from <c>/api/v2/configs</c> at 16 MiB
/// (16,777,216 bytes). Over the cap is a failed attempt / failed leg: nothing installs, no ETag
/// is stored, and one WARN per oversized episode names the cap. Drives the real SSE worker and
/// HTTP transport against a raw 127.0.0.1 HTTP server.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA2007",
    Justification = "Test code; ConfigureAwait(false) not required.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1031",
    Justification = "Raw socket test server; broad catch on teardown is intentional.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA2000",
    Justification = "TcpListener is stopped explicitly; it is not IDisposable on every target framework.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Usage", "CA2213",
    Justification = "TcpListener is stopped explicitly in Dispose; it is not IDisposable on every target framework.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1835",
    Justification = "Byte-array Stream overloads keep the raw test server portable across target frameworks.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Usage", "CA1849",
    Justification = "Tests deliberately use synchronous CancellationTokenSource.Cancel.")]
public sealed class SizeCapsTests
{
    private const string Key = "size-caps.value";
    private const int EventCap = 4 * 1024 * 1024;
    private const int BodyCap = 16 * 1024 * 1024;

    private static string Envelope(int generation, string value, int padTo = 0)
    {
        string head = "{\"configs\":[{ \"id\": \"1\", \"key\": \"" + Key + "\", \"type\": \"config\", \"valueType\": \"string\", " +
            "\"default\": { \"rules\": [ { \"criteria\": [ { \"operator\": \"ALWAYS_TRUE\" } ], " +
            "\"value\": { \"type\": \"string\", \"value\": \"" + value + "\" } } ] } }]," +
            "\"meta\":{\"version\":\"v" + generation + "\",\"environment\":\"production\",\"generation\":" + generation + "}";
        if (padTo <= 0) return head + "}";
        const string padOpen = ",\"pad\":\"";
        const string padClose = "\"}";
        int pad = padTo - Encoding.UTF8.GetByteCount(head) - padOpen.Length - padClose.Length;
        return head + padOpen + new string('x', pad) + padClose;
    }

    // ---------------------------------------------------------------- Test 10 case 1

    [Fact]
    public async Task Test10_1_OversizedSseEvent_NeverConnected_BackoffGrows_PollerFetches_OneWarn()
    {
        // The SSE event is a VALID envelope (gen 3, "sse-big") padded so its data is 4 MiB + 1
        // byte. Uncapped, it would install and report connected.
        string big = "id: g2\ndata: " + Envelope(3, "sse-big", EventCap + 1) + "\n\n";
        using var server = new CapServer(
            sse: async (_, s, ct) =>
            {
                await CapServer.WriteSseHeadersAsync(s, ct);
                await CapServer.WriteAsync(s, big, ct);
                await Task.Delay(Timeout.Infinite, ct);
            },
            configs: n => CapServer.Json(n == 1 ? Envelope(1, "v1") : Envelope(2, "v2")));
        var log = new CaptureLogger();
        await using var rig = await ClientRig.StartAsync(server, log, threshold: TimeSpan.FromMilliseconds(1500));

        bool done = await WaitForAsync(
            () => server.SseAttempts >= 3 && rig.Client.GetString(Key) == "v2",
            TimeSpan.FromSeconds(10));
        rig.StopSampler();

        done.Should().BeTrue("the oversized event is a failed attempt, so SSE retries and the fallback poller delivers v2 (attempts {0}, value {1})",
            server.SseAttempts, rig.Client.GetString(Key));
        rig.Client.GetString(Key).Should().NotBe("sse-big", "an oversized event never installs");
        rig.ConnectedEdges.Should().Be(0, "an oversized event is not a frame");
        rig.SamplerSawConnected.Should().BeFalse("connectionState() never reads connected");
        server.ConfigHits.Should().BeGreaterThanOrEqualTo(2, "the fallback poller fetched /api/v2/configs");
        var sleeps = rig.SseSleeps.ToArray();
        sleeps.Length.Should().BeGreaterThanOrEqualTo(2);
        for (int i = 1; i < sleeps.Length; i++)
        {
            sleeps[i].Should().BeGreaterThan(sleeps[i - 1], "an oversized event grows the backoff (sleep {0})", i);
        }
        log.LogCount(LogLevel.Warning, "4194304").Should().Be(1, "one WARN per oversized episode; repeats are DEBUG. Lines:\n{0}",
            string.Join("\n", log.Lines.Select(l => l.Level + " " + Trim(l.Message))));
        log.LogCount(LogLevel.Debug, "4194304").Should().BeGreaterThanOrEqualTo(1, "repeats log at DEBUG");
    }

    // ---------------------------------------------------------------- Test 10 case 2

    [Fact]
    public async Task Test10_2_NoNewline8MiB_ParserStopsReadingAtTheCap()
    {
        using var body = new CountingStream(Encoding.UTF8.GetBytes("data: "), fill: (byte)'a', total: 8 * 1024 * 1024);
        int delivered = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool frame = await SseClient.ParseStreamAsync(body, _ => delivered++, TimeSpan.Zero, cts.Token);

        frame.Should().BeFalse();
        delivered.Should().Be(0);
        body.BytesRead.Should().BeLessThanOrEqualTo(EventCap + (64 * 1024),
            "the reader stops at the cap instead of buffering the whole 8 MiB line (read {0})", body.BytesRead);
    }

    [Fact]
    public async Task Test10_2_NoNewline8MiB_ConnectionEndsAndRetriesWithBackoff()
    {
        var pad = new string('a', 8 * 1024 * 1024);
        using var server = new CapServer(
            sse: async (_, s, ct) =>
            {
                await CapServer.WriteSseHeadersAsync(s, ct);
                await CapServer.WriteAsync(s, "data: " + pad, ct);
                await Task.Delay(Timeout.Infinite, ct);
            },
            configs: n => CapServer.Json(Envelope(1, "v1")));
        var sleeps = new ConcurrentQueue<TimeSpan>();
        using var sse = new SseClient(new[] { server.BaseUri }, "k", _ => { })
        {
            WaitHook = (_, d) =>
            {
                sleeps.Enqueue(d);
                return TimeSpan.FromMilliseconds(1);
            },
        };
        using var cts = new CancellationTokenSource();
        var run = sse.RunAsync(cts.Token);

        bool retried = await WaitForAsync(() => server.SseAttempts >= 3, TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));

        retried.Should().BeTrue("the oversized line ends the connection (the server holds it open), so the SSE loop retries");
        var s = sleeps.ToArray();
        s.Length.Should().BeGreaterThanOrEqualTo(2);
        s[1].Should().BeGreaterThan(s[0], "an oversized event is a failed attempt: backoff grows");
    }

    // ---------------------------------------------------------------- Test 10 case 3

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Test10_3_OversizedConfigBody_FailedLeg_NoEtag_NoIfNoneMatch(bool contentLength)
    {
        string big = Envelope(2, "http-big", BodyCap + 1);
        using var server = new CapServer(
            sse: (_, _, _) => Task.CompletedTask,
            configs: _ => CapServer.Json(big, etag: "\"big\"", contentLength: contentLength));
        using var transport = new HttpTransport(new[] { server.BaseUri }, "k", configFetchTimeout: TimeSpan.FromSeconds(20));

        // Sequential path.
        Func<Task> fetch = () => transport.FetchAsync(null, CancellationToken.None);
        (await fetch.Should().ThrowAsync<QuonfigException>()).Which.Message.Should().Contain("16777216");
        transport.LastETag.Should().BeNull("an oversized body never stores its ETag");

        // Hedged leg path (fallback poller, refresh, init).
        var legs = transport.FetchLegsAsync(new[] { 0 }, CancellationToken.None);
        var results = new List<LegResult>();
        while (await legs.WaitToReadAsync())
        {
            while (legs.TryRead(out var r)) results.Add(r);
        }
        results.Should().HaveCount(1);
        results[0].Error.Should().NotBeNull("an oversized body is a failed leg, like a 5xx");
        results[0].Envelope.Should().BeNull("nothing is installed");

        // The next leg request carries no If-None-Match from the oversized response.
        var again = transport.FetchLegsAsync(new[] { 0 }, CancellationToken.None);
        while (await again.WaitToReadAsync())
        {
            while (again.TryRead(out _)) { }
        }
        server.IfNoneMatch.Should().OnlyContain(v => v == null, "no ETag was stored from an oversized body");
    }

    [Fact]
    public async Task Test10_3_OversizedPollBody_NeverInstalls_OneWarn_RefreshDoesNotAdvance()
    {
        string big = Envelope(2, "http-big", BodyCap + 1);
        using var server = new CapServer(
            sse: async (_, s, ct) =>
            {
                // Headers only: never a frame, so the fallback poller engages.
                await CapServer.WriteSseHeadersAsync(s, ct);
                await Task.Delay(Timeout.Infinite, ct);
            },
            configs: n => n == 1 ? CapServer.Json(Envelope(1, "v1")) : CapServer.Json(big, etag: "\"big\""));
        var log = new CaptureLogger();
        await using var rig = await ClientRig.StartAsync(server, log, threshold: TimeSpan.FromMilliseconds(300));
        var refreshedAfterInit = rig.Client.LastSuccessfulRefresh;

        bool polled = await WaitForAsync(() => server.ConfigHits >= 4, TimeSpan.FromSeconds(15));
        rig.StopSampler();

        polled.Should().BeTrue("the fallback poller keeps polling (hits {0})", server.ConfigHits);
        rig.Client.GetString(Key).Should().Be("v1", "an oversized body never installs");
        rig.Client.LastSuccessfulRefresh.Should().Be(refreshedAfterInit, "an oversized body is a failed leg and does not advance LastSuccessfulRefresh");
        server.IfNoneMatch.Should().NotContain("\"big\"");
        log.LogCount(LogLevel.Warning, "16777216").Should().Be(1, "one WARN per oversized episode. Lines:\n{0}",
            string.Join("\n", log.Lines.Select(l => l.Level + " " + Trim(l.Message))));
    }

    // ---------------------------------------------------------------- Test 10 case 4

    [Fact]
    public async Task Test10_4_Non2xxBody_NeverInErrorMessage()
    {
        const string marker = "SIZE-CAPS-MARKER-7f3a";
        string body = marker + new string('z', 2 * 1024 * 1024);
        using var server = new CapServer(
            sse: (_, _, _) => Task.CompletedTask,
            configs: _ => (502, Encoding.UTF8.GetBytes(body), null, true));
        using var transport = new HttpTransport(new[] { server.BaseUri }, "k");

        Func<Task> fetch = () => transport.FetchAsync(null, CancellationToken.None);
        var ex = (await fetch.Should().ThrowAsync<QuonfigException>()).Which;
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            e.Message.Should().NotContain(marker);
        }
    }

    [Fact]
    public async Task Test10_4_Non2xxBody_NeverInAnyLogLine()
    {
        // The contract asserts no LOG LINE (and no error message) carries the marker. Drive a real
        // client: init gets v1, then every /api/v2/configs poll and every SSE attempt answers a
        // non-2xx with a ~2 MiB body that starts with the marker.
        const string marker = "SIZE-CAPS-MARKER-7f3a";
        string body = marker + new string('z', 2 * 1024 * 1024);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        using var server = new CapServer(
            sse: async (_, s, ct) =>
            {
                var head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 502 Bad Gateway\r\nContent-Type: text/plain\r\nContent-Length: " + bodyBytes.Length + "\r\nConnection: close\r\n\r\n");
                await s.WriteAsync(head, 0, head.Length, ct);
                await s.WriteAsync(bodyBytes, 0, bodyBytes.Length, ct);
            },
            configs: n => n == 1 ? CapServer.Json(Envelope(1, "v1")) : (502, bodyBytes, null, true));
        var log = new CaptureLogger();
        await using var rig = await ClientRig.StartAsync(server, log, threshold: TimeSpan.FromMilliseconds(300));

        bool exercised = await WaitForAsync(() => server.ConfigHits >= 4 && server.SseAttempts >= 2, TimeSpan.FromSeconds(15));
        rig.StopSampler();

        exercised.Should().BeTrue("both the poller and SSE hit the non-2xx path (configs {0}, sse {1})", server.ConfigHits, server.SseAttempts);
        log.Lines.Should().NotBeEmpty("the failed legs are logged, so the assertion below is not vacuous");
        log.Lines.Where(l => Regex.IsMatch(l.Message, marker)).Should().BeEmpty();
        log.Exceptions.Where(e => Regex.IsMatch(e.ToString(), marker)).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Test 10 case 5

    [Fact]
    public async Task Test10_5_Control_3MiBSseEventInstalls()
    {
        string evt = "data: " + Envelope(3, "sse-3mib", 3 * 1024 * 1024) + "\n\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(evt));
        var got = new List<ConfigEnvelope>();
        bool frame = await SseClient.ParseStreamAsync(ms, got.Add, TimeSpan.Zero, CancellationToken.None);

        frame.Should().BeTrue();
        got.Should().HaveCount(1);
        got[0].Meta!.Generation.Should().Be(3);
    }

    [Fact]
    public async Task Test10_5_Control_12MiBConfigBodyInstalls()
    {
        string body = Envelope(2, "http-12mib", 12 * 1024 * 1024);
        using var server = new CapServer(
            sse: (_, _, _) => Task.CompletedTask,
            configs: _ => CapServer.Json(body, etag: "\"twelve\""));
        using var transport = new HttpTransport(new[] { server.BaseUri }, "k", configFetchTimeout: TimeSpan.FromSeconds(20));

        var env = await transport.FetchAsync(null, CancellationToken.None);

        env.Should().NotBeNull();
        env!.Meta!.Generation.Should().Be(2);
        transport.LastETag.Should().Be("\"twelve\"");
    }

    [Fact]
    public async Task MultiLineDataSumOverTheCap_IsOversized()
    {
        // Two data lines, each under the cap, whose joined payload is over it (R1: SDKs that
        // join multi-line data SHOULD cap the sum).
        string half = new string('a', (EventCap / 2) + 16);
        string evt = "data: " + half + "\ndata: " + half + "\n\n: keepalive\n\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(evt));
        int delivered = 0;
        bool frame = await SseClient.ParseStreamAsync(ms, _ => delivered++, TimeSpan.Zero, CancellationToken.None);

        frame.Should().BeFalse("the stream ends at the oversized event, before the keepalive");
        delivered.Should().Be(0);
    }

    // ---------------------------------------------------------------- helpers

    private static string Trim(string s) => s.Length > 300 ? s.Remove(300) : s;

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

    /// <summary>A stream of <c>prefix</c> then <c>fill</c> bytes up to <c>total</c>, counting what was read.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly byte _fill;
        private readonly long _total;
        private long _pos;

        public CountingStream(byte[] prefix, byte fill, long total)
        {
            _prefix = prefix;
            _fill = fill;
            _total = total;
        }

        public long BytesRead => Interlocked.Read(ref _pos);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = 0;
            while (n < count && _pos < _total)
            {
                buffer[offset + n] = _pos < _prefix.Length ? _prefix[_pos] : _fill;
                n++;
                _pos++;
            }
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));
#if NET8_0_OR_GREATER

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var tmp = new byte[buffer.Length];
            int n = Read(tmp, 0, tmp.Length);
            tmp.AsSpan(0, n).CopyTo(buffer.Span);
            return new ValueTask<int>(n);
        }
#endif
    }

    /// <summary>A real client wired to <see cref="CapServer"/> plus edge/sleep/state recorders.</summary>
    private sealed class ClientRig : IAsyncDisposable
    {
        private readonly ConcurrentQueue<ConnectionState> _edges = new();
        private readonly CancellationTokenSource _samplerCts = new();
        private Task _sampler = Task.CompletedTask;
        private int _samplerSawConnected;

        public Quonfig Client { get; private set; } = null!;
        public ConcurrentQueue<TimeSpan> SseSleeps { get; } = new();
        public int ConnectedEdges => _edges.Count(e => e == ConnectionState.Connected);
        public bool SamplerSawConnected => Volatile.Read(ref _samplerSawConnected) != 0;

        public static async Task<ClientRig> StartAsync(CapServer server, CaptureLogger log, TimeSpan threshold)
        {
            var rig = new ClientRig();
            var client = new Quonfig(new QuonfigOptions
            {
                SdkKey = "test-sdk-key",
                ApiUrls = new[] { server.BaseUri.ToString() },
                StreamUrls = new[] { server.BaseUri.ToString() },
                InitTimeout = TimeSpan.FromSeconds(5),
                ConfigFetchHedgeAbort = TimeSpan.FromSeconds(4),
                FallbackPollThreshold = threshold,
                FallbackPollInterval = TimeSpan.FromMilliseconds(20),
                CollectEvaluationSummaries = false,
                ContextUploadMode = ContextUploadMode.None,
                Logger = log,
                TestWaitHook = (kind, d) =>
                {
                    if (kind != "sse") return d;
                    rig.SseSleeps.Enqueue(d);
                    return TimeSpan.FromTicks(d.Ticks / 10);
                },
            });
            client.OnConnectionStateChange += st => rig._edges.Enqueue(st);
            rig.Client = client;
            await client.InitAsync();
            client.GetString(Key).Should().Be("v1", "the init fetch serves v1");
            var token = rig._samplerCts.Token;
            rig._sampler = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    if (client.ConnectionState == ConnectionState.Connected) Volatile.Write(ref rig._samplerSawConnected, 1);
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
    /// Raw HTTP/1.1 server on 127.0.0.1. <c>/api/v2/configs</c> answers with the per-test
    /// <c>configs</c> function (1-based hit number); the SSE path runs the per-test script with the
    /// 1-based attempt number. Records each config request's <c>If-None-Match</c>.
    /// </summary>
    private sealed class CapServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<int, Stream, CancellationToken, Task> _sse;
        private readonly Func<int, (int Status, byte[] Body, string? ETag, bool ContentLength)> _configs;
        private readonly ConcurrentBag<TcpClient> _clients = new();
        private readonly ConcurrentQueue<string?> _ifNoneMatch = new();
        private int _configHits;
        private int _sseAttempts;

        public CapServer(
            Func<int, Stream, CancellationToken, Task> sse,
            Func<int, (int Status, byte[] Body, string? ETag, bool ContentLength)> configs)
        {
            _sse = sse;
            _configs = configs;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            _ = Task.Run(AcceptLoopAsync);
        }

        public Uri BaseUri { get; }
        public int ConfigHits => Volatile.Read(ref _configHits);
        public int SseAttempts => Volatile.Read(ref _sseAttempts);
        public IReadOnlyList<string?> IfNoneMatch => _ifNoneMatch.ToArray();

        public static (int, byte[], string?, bool) Json(string body, string? etag = null, bool contentLength = true) =>
            (200, Encoding.UTF8.GetBytes(body), etag, contentLength);

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
                    var (path, headers) = await ReadRequestAsync(s);
                    if (path.StartsWith("/api/v2/sse/config", StringComparison.Ordinal))
                    {
                        int n = Interlocked.Increment(ref _sseAttempts);
                        await _sse(n, s, _cts.Token);
                    }
                    else if (path.StartsWith("/api/v2/configs", StringComparison.Ordinal))
                    {
                        headers.TryGetValue("if-none-match", out var inm);
                        _ifNoneMatch.Enqueue(inm);
                        int n = Interlocked.Increment(ref _configHits);
                        var (status, body, etag, withLength) = _configs(n);
                        var head = new StringBuilder();
                        head.Append("HTTP/1.1 ").Append(status).Append(status == 200 ? " OK" : " Bad Gateway").Append("\r\n");
                        head.Append("Content-Type: application/json\r\n");
                        if (etag is not null) head.Append("ETag: ").Append(etag).Append("\r\n");
                        if (withLength) head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
                        head.Append("Connection: close\r\n\r\n");
                        await WriteAsync(s, head.ToString(), _cts.Token);
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

        private static async Task<(string Path, Dictionary<string, string> Headers)> ReadRequestAsync(Stream s)
        {
            var buf = new byte[16384];
            int len = 0;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (len < buf.Length)
            {
                int r = await s.ReadAsync(buf, len, buf.Length - len);
                if (r == 0) break;
                len += r;
                string sofar = Encoding.ASCII.GetString(buf, 0, len);
                int end = sofar.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0)
                {
                    var lines = sofar.Substring(0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var parts = lines[0].Split(' ');
                    for (int i = 1; i < lines.Length; i++)
                    {
                        var kv = lines[i].Split(new[] { ':' }, 2);
                        if (kv.Length == 2) headers[kv[0].Trim()] = kv[1].Trim();
                    }
                    return (parts.Length > 1 ? parts[1] : string.Empty, headers);
                }
            }
            return (string.Empty, headers);
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
