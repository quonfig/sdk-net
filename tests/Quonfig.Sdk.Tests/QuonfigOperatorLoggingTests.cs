using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Quonfig.Sdk;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// Operator-signal logging (qfg-41nh.8, sdk-go parity). A customer who wires ANY
/// <see cref="ILogger"/> must get outage signal without code changes:
/// <list type="bullet">
///   <item><description>one startup line announcing the chosen Layer 1 / Layer 2 mode
///     (Information),</description></item>
///   <item><description>a Warning when the Layer 2 fallback poller ENGAGES (SSE down past
///     threshold — this is the outage moment), and</description></item>
///   <item><description>an Information when it DISENGAGES (SSE recovered).</description></item>
/// </list>
/// The default logger stays <c>NullLogger</c> — these lines only surface when a logger is wired.
/// </summary>
public sealed class QuonfigOperatorLoggingTests
{
    private const string SdkKey = "test-sdk-key";
    private const string ConfigsPath = "/api/v2/configs";
    private const string SsePath = "/api/v2/sse/config";

    private static string EnvelopeJson(int generation) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{{\"configs\":[],\"meta\":{{\"version\":\"v1\",\"environment\":\"production\",\"generation\":{0}}}}}",
            generation);

    private static void ServeConfigs(WireMockServer server) =>
        server
            .Given(Request.Create().WithPath(ConfigsPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(EnvelopeJson(42)));

    [Fact]
    public async Task Startup_LogsPollingConfigurationMode()
    {
        using var server = WireMockServer.Start();
        ServeConfigs(server);
        server
            .Given(Request.Create().WithPath(SsePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithBody(": heartbeat\n\n"));
        var recorder = new RecordingLogger();

        await using var client = new Quonfig(new QuonfigOptions
        {
            SdkKey = SdkKey,
            ApiUrls = new[] { server.Urls[0] },
            StreamUrls = new[] { server.Urls[0] },
            InitTimeout = TimeSpan.FromSeconds(5),
            // Operator logging, not telemetry; opt out so the now-live reporter (qfg-gxm6) does not
            // post to the default telemetry endpoint.
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
            Logger = recorder,
        });
        await client.InitAsync();

        var startupLines = recorder.Entries
            .Where(e => e.Level == MelLogLevel.Information && ContainsIgnoreCase(e.Message, "polling configuration"))
            .ToList();
        startupLines.Should().NotBeEmpty(
            "startup must announce the chosen polling mode so deployers can confirm the Layer 1/Layer 2 semantics");
        startupLines[0].Message.Should().Contain("sse-with-fallback-poll",
            "SSE + fallback poll are both enabled by default");
    }

    [Fact]
    public async Task FallbackPoller_LogsEngageWarningAndDisengageInfo()
    {
        using var server = WireMockServer.Start();
        ServeConfigs(server);
        // SSE starts DEAD (503): the poller's threshold timer arms at startup and Layer 2
        // engages once it elapses. ToggleSseServer serves a LONG-LIVED stream once healthy —
        // WireMock buffers its whole response so its streams EOF instantly, which never holds
        // the "connected" state long enough for a deterministic disengage.
        using var sseServer = new ToggleSseServer();
        var recorder = new RecordingLogger();

        await using var client = new Quonfig(new QuonfigOptions
        {
            SdkKey = SdkKey,
            ApiUrls = new[] { server.Urls[0] },
            StreamUrls = new[] { sseServer.Url },
            InitTimeout = TimeSpan.FromSeconds(5),
            FallbackPollThreshold = TimeSpan.FromMilliseconds(250),
            FallbackPollInterval = TimeSpan.FromSeconds(30),
            // Operator logging, not telemetry; opt out so the now-live reporter (qfg-gxm6) does not
            // post to the default telemetry endpoint.
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
            Logger = recorder,
        });
        await client.InitAsync();

        // 1) Engage: SSE never connects, threshold (250ms) elapses, Layer 2 engages — WARNING.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!recorder.Entries.Any(e => e.Level == MelLogLevel.Warning
                   && ContainsIgnoreCase(e.Message, "fallback poller engaged"))
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        recorder.Entries.Should().Contain(
            e => e.Level == MelLogLevel.Warning
                && ContainsIgnoreCase(e.Message, "fallback poller engaged"),
            "Layer 2 engaging IS the outage signal — it must be a Warning an operator can alert on");

        // 2) Disengage: revive the SSE endpoint; the stream reconnects (staying up this time)
        //    and Layer 2 stands down — INFORMATION.
        sseServer.Healthy = true;

        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!recorder.Entries.Any(e => e.Level == MelLogLevel.Information
                   && ContainsIgnoreCase(e.Message, "fallback poller disengaged"))
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        recorder.Entries.Should().Contain(
            e => e.Level == MelLogLevel.Information
                && ContainsIgnoreCase(e.Message, "fallback poller disengaged"),
            "recovery must be logged so the engage warning has a visible end");
    }

    /// <summary>
    /// Minimal SSE endpoint whose health is toggleable: 503 while <see cref="Healthy"/> is
    /// false, then a long-lived <c>text/event-stream</c> (comment heartbeat every 200ms, held
    /// open until dispose) once true. Needed because WireMock buffers full responses, so its
    /// "streams" EOF immediately and can't hold the SSE-connected state.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Test stub: any I/O failure just means the client went away or the test is shutting down.")]
    private sealed class ToggleSseServer : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        private readonly System.Threading.CancellationTokenSource _cts = new();
        private volatile bool _healthy;

        public string Url { get; }

        public bool Healthy
        {
            get => _healthy;
            set => _healthy = value;
        }

        public ToggleSseServer()
        {
            int port = FreePort();
            Url = FormattableString.Invariant($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            try
            {
                l.Start();
                return ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            }
            finally
            {
                l.Stop();
#if NET8_0_OR_GREATER
                // TcpListener implements IDisposable only on modern TFMs; Stop() releases the
                // socket on net48.
                l.Dispose();
#endif
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                System.Net.HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return; // listener stopped
                }
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        private async Task HandleAsync(System.Net.HttpListenerContext ctx)
        {
            try
            {
                if (!_healthy)
                {
                    ctx.Response.StatusCode = 503;
                    ctx.Response.Close();
                    return;
                }
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.SendChunked = true;
                var heartbeat = System.Text.Encoding.UTF8.GetBytes(": heartbeat\n\n");
                while (!_cts.IsCancellationRequested)
                {
#if NET8_0_OR_GREATER
                    await ctx.Response.OutputStream.WriteAsync(heartbeat.AsMemory(), _cts.Token);
#else
                    await ctx.Response.OutputStream.WriteAsync(heartbeat, 0, heartbeat.Length, _cts.Token);
#endif
                    await ctx.Response.OutputStream.FlushAsync(_cts.Token);
                    await Task.Delay(200, _cts.Token);
                }
            }
            catch (Exception)
            {
                // Client disconnected / shutdown — fine for a test stub.
            }
            finally
            {
                try { ctx.Response.Abort(); } catch (Exception) { /* best-effort */ }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch (Exception) { /* best-effort */ }
            try { _listener.Close(); } catch (Exception) { /* best-effort */ }
            _cts.Dispose();
        }
    }

    /// <summary>Case-insensitive Contains that also compiles on net48 (no Contains(string, StringComparison) pre-netcoreapp).</summary>
    private static bool ContainsIgnoreCase(string haystack, string needle) =>
#if NET8_0_OR_GREATER
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
#else
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
#endif

    /// <summary>Capture-only ILogger; records every Log call for assertions.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public sealed class Entry
        {
            public Entry(MelLogLevel level, string message, Exception? exception)
            {
                Level = level;
                Message = message;
                Exception = exception;
            }

            public MelLogLevel Level { get; }
            public string Message { get; }
            public Exception? Exception { get; }
        }

        private readonly object _gate = new();
        private readonly List<Entry> _entries = new();

        public IReadOnlyList<Entry> Entries
        {
            get { lock (_gate) { return _entries.ToArray(); } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(MelLogLevel logLevel) => true;

        public void Log<TState>(MelLogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _entries.Add(new Entry(logLevel, formatter(state, exception), exception));
            }
        }
    }
}
